using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Access;
using ClaudeHomeServer.Services.Backgrounds;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ClaudeHomeServer.Services.Composition;

namespace ClaudeHomeServer.Controllers;

/// <summary>
/// Фон рабочего пространства проекта (ADR-008 §7): генерация, возврат к стандартному
/// и отдача собранного сервером тайла. Генерация и сброс — только владельцу проекта.
/// </summary>
[ProjectCapability(ProjectCapabilityArea.Platform, ProjectKey = "id")]
[ProjectScope(Key = "id", Right = ProjectRight.Write)]
[ApiController]
[Authorize]
[Route("api/projects/{id}/background")]
public class ProjectBackgroundsController(
    ProjectManager projects, ProjectBackgroundService backgrounds)
    : ControllerBase
{
    // Доступ к проекту проверил [ProjectScope] (чужой — 404, а не 403: существование не подтверждаем)
    private Project? Scoped(string id) => projects.GetById(id);

    private static object Payload(BackgroundResult r) => new
    {
        kind = ProjectBackgroundView.Name(r.Kind),
        tileVersion = r.TileVersion,
        suggestedColorKey = r.SuggestedColorKey,
        colorApplied = r.ColorApplied,
        failReason = r.FailReason,
    };

    [HttpPost("generate")]
    public async Task<ActionResult> Generate(string id, CancellationToken ct)
    {
        if (Scoped(id) is not { } project) return NotFound();

        return Ok(Payload(await backgrounds.GenerateAsync(project, ct)));
    }

    [HttpPost("reset")]
    public ActionResult Reset(string id)
    {
        if (Scoped(id) is null) return NotFound();

        return Ok(Payload(backgrounds.Reset(id)));
    }

    // Тайл маски. access_token в query — запрос идёт из CSS (mask-image), заголовок туда
    // браузер не поставит (приём GET {id}/icon). Параметр v в запросе — только cache-buster:
    // имя файла берётся из стора, из запроса путь не строится никогда.
    [HttpGet("tile.svg")]
    [ProjectScope(Key = "id")]
    public IActionResult Tile(string id)
    {
        if (Scoped(id) is not { Background: { Kind: ProjectBackgroundKind.Generated, TileFile: { } file } })
            return NotFound();

        var full = Path.Combine(projects.BackgroundsDir, id, file);
        if (!System.IO.File.Exists(full)) return NotFound();

        Response.Headers["X-Content-Type-Options"] = "nosniff";
        Response.Headers.ContentSecurityPolicy = "default-src 'none'";
        // Имя файла меняется при каждой генерации, поэтому кеш безопасно долгий
        Response.Headers.CacheControl = "private, max-age=604800, immutable";
        return PhysicalFile(full, "image/svg+xml");
    }
}
