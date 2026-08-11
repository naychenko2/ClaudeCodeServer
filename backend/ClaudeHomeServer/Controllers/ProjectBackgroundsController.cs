using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Backgrounds;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ClaudeHomeServer.Controllers;

/// <summary>
/// Фон рабочего пространства проекта (ADR-008 §7): генерация, возврат к стандартному
/// и отдача собранного сервером тайла. Генерация и сброс — только владельцу проекта.
/// </summary>
[ApiController]
[Authorize]
[Route("api/projects/{id}/background")]
public class ProjectBackgroundsController(
    ProjectManager projects, ProjectBackgroundService backgrounds, FeatureFlagService flags)
    : ControllerBase
{
    private string UserId => User.FindFirstValue(JwtRegisteredClaimNames.Sub)!;

    // Чужой проект — 404, а не 403: подтверждать его существование незачем
    private Project? Owned(string id)
    {
        var project = projects.GetById(id);
        return project is null || project.OwnerId != UserId ? null : project;
    }

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
        if (Owned(id) is not { } project) return NotFound();
        if (!flags.IsEnabled(UserId, FeatureFlagKeys.ProjectBackgrounds)) return NotFound();

        return Ok(Payload(await backgrounds.GenerateAsync(project, ct)));
    }

    [HttpPost("reset")]
    public ActionResult Reset(string id)
    {
        if (Owned(id) is null) return NotFound();
        if (!flags.IsEnabled(UserId, FeatureFlagKeys.ProjectBackgrounds)) return NotFound();

        return Ok(Payload(backgrounds.Reset(id)));
    }

    // Тайл маски. access_token в query — запрос идёт из CSS (mask-image), заголовок туда
    // браузер не поставит (приём GET {id}/icon). Параметр v в запросе — только cache-buster:
    // имя файла берётся из стора, из запроса путь не строится никогда.
    [HttpGet("tile.svg")]
    public IActionResult Tile(string id)
    {
        if (Owned(id) is not { Background: { Kind: ProjectBackgroundKind.Generated, TileFile: { } file } })
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
