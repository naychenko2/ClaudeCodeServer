using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.Composition;
using ClaudeHomeServer.Services.ImageEditor.Prefs;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ClaudeHomeServer.Services.ImageEditor.Controllers;

// Выбор человека в полосе «Картинки» проекта (без выбранной картинки): его берёт новая нить и
// запуск агентом без аргументов. Гейт — ImageEditScopeGate: флаг и чужой проект — 404; проверка
// тела общая с личной ручкой (PersonalImageEditorController). Запись рассылает image_prefs_changed.
[ProjectCapability(ProjectCapabilityArea.FileBound)]
[ApiController]
[Authorize]
[Route("api/projects/{projectId}/image-editor/prefs")]
public class ImagePrefsController(
    ImageEditScopeGate gate,
    ImageProjectPrefsService prefs) : ControllerBase
{
    private string UserId => User.FindFirstValue(JwtRegisteredClaimNames.Sub)!;

    [HttpGet]
    public IActionResult Get(string projectId)
    {
        if (!gate.TryProject(UserId, projectId, out var project, out var denied)) return denied;
        return Ok(prefs.Get(UserId, ImageEditScope.Of(project)));
    }

    [HttpPut]
    public async Task<IActionResult> Put(string projectId, [FromBody] ImageProjectPrefs? req)
    {
        if (!gate.TryProject(UserId, projectId, out var project, out var denied)) return denied;
        if (ImageProjectPrefsService.Validate(req) is { } error)
            return StatusCode(StatusCodes.Status400BadRequest, new { error, code = ImageEditErrorCodes.InvalidRequest });
        return Ok(await prefs.SetAsync(UserId, ImageEditScope.Of(project), req!));
    }
}
