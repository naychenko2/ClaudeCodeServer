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
/// </summary>
[ApiController]
[Authorize]
[Route("api/projects/{projectId}/code-graph")]
public class CodeGraphController(
    CodeGraphService graphs,
    ProjectManager projects,
    ILogger<CodeGraphController> logger) : ControllerBase
{
    // DefaultMapInboundClaims = false → sub не ремапится в NameIdentifier, читаем напрямую
    private string? UserId => User.FindFirstValue(JwtRegisteredClaimNames.Sub);

    /// <summary>
    /// Граф кода проекта (v1): 200 — граф; 404 — не построен/проект не найден; 403 — чужой проект.
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
                return NotFound(new { message = "Граф кода ещё не построен для этого проекта" });

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
}
