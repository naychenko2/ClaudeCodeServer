using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.CodeGraph;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ClaudeHomeServer.Controllers;

/// <summary>
/// Read-only доступ к графу зависимостей кода проекта.
/// GET /api/projects/{projectId}/code-graph — узлы/рёбра/god-узлы + метаданные.
/// Тонкие запросы для MCP-сервера codegraph (агент): /find, /neighbors, /hubs —
/// отдают компактный срез вместо снимка целиком (~1 МБ на проект).
/// </summary>
[ApiController]
[Authorize]
[Route("api/projects/{projectId}/code-graph")]
public class CodeGraphController(
    CodeGraphService graphs,
    CodeGraphQueryService queries,
    ProjectManager projects,
    ILogger<CodeGraphController> logger) : ControllerBase
{
    // DefaultMapInboundClaims = false → sub не ремапится в NameIdentifier, читаем напрямую
    private string? UserId => User.FindFirstValue(JwtRegisteredClaimNames.Sub);

    /// <summary>
    /// Граф кода проекта (v1): 200 — граф (возможно isStale); 404 — не построен/проект не найден
    /// (при отсутствии графа запускается фоновый initial-build, ответ несёт X-CodeGraph-Building);
    /// 403 — чужой проект.
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> Get(string projectId, CancellationToken ct)
    {
        var project = projects.GetById(projectId);
        if (project is null)
        {
            // Не раскрываем существование — 404, как ProjectsController.
            return NotFound();
        }
        if (project.OwnerId != UserId)
        {
            // Чужой проект — явно 403 (контракт задачи): UI различает «нет доступа» и «графа нет».
            return Forbid();
        }

        try
        {
            var snapshot = await graphs.GetSnapshotAsync(project.RootPath, ct);
            if (snapshot is null)
            {
                // Граф ещё не построен — запускаем фоновый initial-build (не блокируя запрос):
                // HOTFIX прода — без этого граф строился только реактивно на .cs-сохранения,
                // и на свежем старте (без правок) панель «Граф» оставалась пустой.
                graphs.StartRebuildIfIdle(project.RootPath);
                Response.Headers["X-CodeGraph-Building"] = "true";
                return NotFound(new
                {
                    message = "Граф кода строится, обновите через несколько секунд",
                    building = true,
                });
            }

            // Граф есть, но несвежий (.cs новее BuiltAt) — фоновое обновление, не блокируя ответ:
            // UI показывает граф с пометкой устаревания, а следующий GET получит уже свежий снимок.
            if (snapshot.Metadata.IsStale)
                graphs.StartRebuildIfIdle(project.RootPath);

            return Ok(snapshot);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Ошибка получения графа кода для проекта {ProjectId}", projectId);
            return StatusCode(500, new { message = "Не удалось получить граф кода" });
        }
    }

    /// <summary>
    /// Явно построить граф кода (кнопка «Построить граф» в empty-state/stale).
    /// 202 — граф построен и доступен для GET; 404 — проект не найден; 403 — чужой проект.
    /// Немедленный rebuild, минуя окно дебаунса (для пустого/устаревшего графа).
    /// </summary>
    [HttpPost("build")]
    public async Task<IActionResult> Build(string projectId, CancellationToken ct)
    {
        var project = projects.GetById(projectId);
        if (project is null)
            return NotFound();
        if (project.OwnerId != UserId)
            return Forbid();

        try
        {
            await graphs.RebuildAsync(project.RootPath, ct);
            return Accepted();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return StatusCode(499, new { message = "Построение графа отменено" });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Ошибка построения графа кода для проекта {ProjectId}", projectId);
            return StatusCode(500, new { message = "Не удалось построить граф кода" });
        }
    }

    /// <summary>
    /// Поиск типов по имени или части FQN (инструмент codegraph_find).
    /// 200 — результаты (возможно пустые) + total; 404 — граф не построен/проект не найден;
    /// 403 — чужой проект.
    /// </summary>
    [HttpGet("find")]
    public Task<IActionResult> Find(string projectId, [FromQuery] string q,
        [FromQuery] int limit = CodeGraphQueryService.DefaultLimit, CancellationToken ct = default)
        => QueryAsync(projectId, "поиска по графу кода",
            async project => await queries.FindAsync(project.RootPath, q, limit, ct) is { } result
                ? Ok(result) : null,
            ct);

    /// <summary>
    /// Связи узла: входящие/исходящие с типом отношения и confidence (инструмент codegraph_neighbors).
    /// 200 — связи; 404 — граф не построен, проект не найден или узел не опознан (в теле —
    /// похожие кандидаты); 403 — чужой проект.
    /// </summary>
    [HttpGet("neighbors")]
    public Task<IActionResult> Neighbors(string projectId, [FromQuery] string node,
        [FromQuery] string? direction = null, [FromQuery] string? relation = null,
        [FromQuery] int limit = CodeGraphQueryService.DefaultLimit, CancellationToken ct = default)
        => QueryAsync(projectId, "запроса связей узла графа",
            async project =>
            {
                var outcome = await queries.NeighborsAsync(project.RootPath, node, direction, relation, limit, ct);
                if (!outcome.HasGraph) return null; // граф не построен — общий 404 + фоновая постройка
                if (outcome.Result is null)
                    return NotFound(new
                    {
                        message = $"Узел «{node}» в графе не найден — уточни имя через codegraph_find",
                        candidates = outcome.Candidates,
                    });
                return Ok(outcome.Result);
            },
            ct);

    /// <summary>
    /// Топ типов по связности (инструмент codegraph_hubs).
    /// 200 — хабы + размер графа; 404 — граф не построен/проект не найден; 403 — чужой проект.
    /// </summary>
    [HttpGet("hubs")]
    public Task<IActionResult> Hubs(string projectId,
        [FromQuery] int limit = 10, CancellationToken ct = default)
        => QueryAsync(projectId, "запроса хабов графа кода",
            async project => await queries.HubsAsync(project.RootPath, limit, ct) is { } result
                ? Ok(result) : null,
            ct);

    /// <summary>
    /// Общая обвязка тонких запросов: владение проектом (404/403), «графа нет» → 404
    /// с запуском фоновой постройки (как GET снимка), ошибки → 500 с логом.
    /// query возвращает null, когда графа для проекта ещё нет.
    /// </summary>
    private async Task<IActionResult> QueryAsync(
        string projectId, string what, Func<Models.Project, Task<IActionResult?>> query, CancellationToken ct)
    {
        var project = projects.GetById(projectId);
        if (project is null) return NotFound();
        if (project.OwnerId != UserId) return Forbid();

        try
        {
            if (await query(project) is { } result) return result;

            graphs.StartRebuildIfIdle(project.RootPath);
            Response.Headers["X-CodeGraph-Building"] = "true";
            return NotFound(new
            {
                message = "Граф кода строится, повтори запрос через несколько секунд",
                building = true,
            });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Ошибка {What} для проекта {ProjectId}", what, projectId);
            return StatusCode(500, new { message = "Не удалось выполнить запрос к графу кода" });
        }
    }
}
