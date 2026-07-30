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
/// Опциональный ?rootPath= выбирает дерево: корень проекта (по умолчанию) либо отдельное
/// worktree чата — у него свой граф (ADR-003).
/// </summary>
[ApiController]
[Authorize]
[Route("api/projects/{projectId}/code-graph")]
public class CodeGraphController(
    CodeGraphService graphs,
    CodeGraphQueryService queries,
    ProjectManager projects,
    SessionManager sessions,
    FileWatcherService watchers,
    ILogger<CodeGraphController> logger) : ControllerBase
{
    // DefaultMapInboundClaims = false → sub не ремапится в NameIdentifier, читаем напрямую
    private string? UserId => User.FindFirstValue(JwtRegisteredClaimNames.Sub);

    /// <summary>
    /// Дерево, к графу которого идёт запрос. Без ?rootPath= — корень проекта (прежнее поведение).
    /// С параметром принимаются ТОЛЬКО свои деревья: корень проекта либо worktree одной из его
    /// сессий — белым списком, а не проверкой существования пути, иначе через параметр читались
    /// бы чужие деревья с диска. Не подошедший путь → null (контроллер отвечает 400: владельца
    /// проверили выше, это спор о параметре, а не о доступе).
    /// Для worktree заодно лениво поднимаем watcher его файлов: контроллер — единственная дверь
    /// к графу отдельного дерева (MCP-инструменты и панель ходят сюда), поэтому «первое обращение
    /// к графу» видно именно здесь.
    /// </summary>
    private string? ResolveRoot(Models.Project project, string? rootPath)
    {
        if (string.IsNullOrWhiteSpace(rootPath)) return project.RootPath;

        var wanted = WorkspaceKnowledgeStore.NormalizePath(rootPath);
        if (wanted == WorkspaceKnowledgeStore.NormalizePath(project.RootPath)) return project.RootPath;

        var session = sessions.GetByProject(project.Id).FirstOrDefault(s =>
            s.WorktreePath is { } wt && WorkspaceKnowledgeStore.NormalizePath(wt) == wanted);
        if (session?.WorktreePath is not { } worktree) return null;

        watchers.WatchPath("worktree:" + session.Id, worktree);
        return worktree;
    }

    private IActionResult BadRoot() =>
        BadRequest(new { message = "Неизвестное рабочее дерево: rootPath не совпадает ни с папкой проекта, ни с отдельным деревом его чатов" });

    /// <summary>
    /// Граф кода проекта (v1): 200 — граф (возможно isStale); 404 — не построен/проект не найден
    /// (при отсутствии графа запускается фоновый initial-build, ответ несёт X-CodeGraph-Building);
    /// 403 — чужой проект.
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> Get(string projectId, [FromQuery] string? rootPath,
        CancellationToken ct)
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
        if (ResolveRoot(project, rootPath) is not { } root) return BadRoot();

        try
        {
            var snapshot = await graphs.GetSnapshotAsync(root, ct);
            if (snapshot is null)
            {
                // Граф ещё не построен — запускаем фоновый initial-build (не блокируя запрос):
                // HOTFIX прода — без этого граф строился только реактивно на .cs-сохранения,
                // и на свежем старте (без правок) панель «Граф» оставалась пустой.
                graphs.StartRebuildIfIdle(root);
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
                graphs.StartRebuildIfIdle(root);

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
    public async Task<IActionResult> Build(string projectId, [FromQuery] string? rootPath,
        CancellationToken ct)
    {
        var project = projects.GetById(projectId);
        if (project is null)
            return NotFound();
        if (project.OwnerId != UserId)
            return Forbid();
        if (ResolveRoot(project, rootPath) is not { } root) return BadRoot();

        try
        {
            await graphs.RebuildAsync(root, ct);
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
        [FromQuery] int limit = CodeGraphQueryService.DefaultLimit,
        [FromQuery] string? rootPath = null, CancellationToken ct = default)
        => QueryAsync(projectId, rootPath, "поиска по графу кода",
            async root => await queries.FindAsync(root, q, limit, ct) is { } result
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
        [FromQuery] int limit = CodeGraphQueryService.DefaultLimit,
        [FromQuery] string? rootPath = null, CancellationToken ct = default)
        => QueryAsync(projectId, rootPath, "запроса связей узла графа",
            async root =>
            {
                var outcome = await queries.NeighborsAsync(root, node, direction, relation, limit, ct);
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
        [FromQuery] int limit = 10, [FromQuery] string? rootPath = null, CancellationToken ct = default)
        => QueryAsync(projectId, rootPath, "запроса хабов графа кода",
            async root => await queries.HubsAsync(root, limit, ct) is { } result
                ? Ok(result) : null,
            ct);

    /// <summary>
    /// Общая обвязка тонких запросов: владение проектом (404/403), выбор дерева (400 на чужом
    /// rootPath), «графа нет» → 404 с запуском фоновой постройки (как GET снимка), ошибки → 500
    /// с логом. query получает путь дерева и возвращает null, когда графа для него ещё нет.
    /// </summary>
    private async Task<IActionResult> QueryAsync(
        string projectId, string? rootPath, string what,
        Func<string, Task<IActionResult?>> query, CancellationToken ct)
    {
        var project = projects.GetById(projectId);
        if (project is null) return NotFound();
        if (project.OwnerId != UserId) return Forbid();
        if (ResolveRoot(project, rootPath) is not { } root) return BadRoot();

        try
        {
            if (await query(root) is { } result) return result;

            graphs.StartRebuildIfIdle(root);
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
