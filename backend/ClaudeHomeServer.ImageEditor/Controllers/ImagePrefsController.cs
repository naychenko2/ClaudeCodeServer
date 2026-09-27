using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.Composition;
using ClaudeHomeServer.Services.ImageEditor.Prefs;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ClaudeHomeServer.Services.ImageEditor.Controllers;

// Выбор человека в полосе «Картинки» проекта (без выбранной картинки): его берёт новая нить и
// запуск агентом без аргументов. Гейты как у ThreadsController: флаг и чужой проект — 404.
// Запись рассылает image_prefs_changed владельцу.
[ProjectCapability(ProjectCapabilityArea.FileBound)]
[ApiController]
[Authorize]
[Route("api/projects/{projectId}/image-editor/prefs")]
public class ImagePrefsController(
    IFeatureFlagGate flags,
    IProjectManager projects,
    ImageProjectPrefsService prefs) : ControllerBase
{
    private string UserId => User.FindFirstValue(JwtRegisteredClaimNames.Sub)!;

    [HttpGet]
    public IActionResult Get(string projectId)
    {
        if (Gate(projectId, out var project) is { } denied) return denied;
        return Ok(prefs.Get(UserId, project));
    }

    [HttpPut]
    public async Task<IActionResult> Put(string projectId, [FromBody] ImageProjectPrefs? req)
    {
        if (Gate(projectId, out var project) is { } denied) return denied;
        if (req is null || req.Count < 1 || req.Count > ImageEditCatalog.DefaultLimits.MaxCount)
            return Error($"Число вариантов — от 1 до {ImageEditCatalog.DefaultLimits.MaxCount}");
        if (req.CharacterSlug is { Length: > 0 } slug && !CharacterStore.IsValidSlug(slug.Trim()))
            return Error("Недопустимый персонаж");
        return Ok(await prefs.SetAsync(UserId, project, req));
    }

    private IActionResult? Gate(string projectId, out Project project)
    {
        project = null!;
        if (!flags.IsEnabled(UserId, FeatureFlagKeys.ImageEditor))
            return NotFound(new { error = "Редактор картинок выключен" });
        if (projects.GetById(projectId) is not { } found || found.OwnerId != UserId)
            return NotFound(new { error = "Проект не найден" });
        project = found;
        return null;
    }

    private ObjectResult Error(string error) =>
        StatusCode(StatusCodes.Status400BadRequest, new { error, code = ImageEditErrorCodes.InvalidRequest });
}
