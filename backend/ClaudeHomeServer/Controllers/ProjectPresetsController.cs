using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Telemetry;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ClaudeHomeServer.Controllers;

/// <summary>
/// Применение пресета каркаса к проекту (знакомство v2, п.3 плана). Единственная дверь:
/// тело строго { presetKey }, никаких путей и имён папок снаружи — состав лежит в
/// PresetCatalog и пишется через ProjectPresetService.
/// </summary>
[ApiController]
[Authorize]
[Route("api/projects/{id}/preset")]
public class ProjectPresetsController(
    ProjectManager projects, ProjectPresetService presets, FeatureFlagService flags)
    : ControllerBase
{
    private string UserId => User.FindFirstValue(JwtRegisteredClaimNames.Sub)!;

    // Чужой проект — 404, а не 403: подтверждать его существование незачем
    private Project? Owned(string id)
    {
        var project = projects.GetById(id);
        return project is null || project.OwnerId != UserId ? null : project;
    }

    // Применить пресет ({ presetKey: "<ключ каталога>" }) или зафиксировать отказ
    // ({ presetKey: "none" }). Идемпотентность: PresetKey != "pending" → 409 —
    // действует и на применение, и на отказ, и на проекты, созданные до фичи (null).
    [HttpPost]
    public ActionResult Apply(string id, [FromBody] ApplyPresetRequest req)
    {
        if (Owned(id) is not { } project) return NotFound();
        // Флаг гейтит эндпоинт на бэке обязательно: OnboardingController его не проверяет
        // (там гейтит фронт), а этот POST пишет в репозиторий — конвенция фона проекта
        if (!flags.IsEnabled(UserId, FeatureFlagKeys.DefaultPersonasOnboarding)) return NotFound();

        var key = (req.PresetKey ?? "").Trim();
        if (key.Length == 0)
            return BadRequest(new { error = "Пустой presetKey" });

        if (project.PresetKey != ProjectPreset.Pending)
            return Conflict(new
            {
                error = "Каркас для этого проекта уже применён, отклонён или проект создан до фичи",
            });

        // Отказ — не пресет: зарезервированное значение, валидацию каталога не проходит
        if (key == ProjectPreset.None)
        {
            projects.SetPresetKey(id, ProjectPreset.None);
            ServerMetrics.RecordPresetApplied(ProjectPreset.None);
            return Ok(new PresetApplyResult([], []));
        }

        var preset = PresetCatalog.Find(key);
        if (preset is null)
            return BadRequest(new { error = $"Неизвестный пресет: {key}" });

        var report = presets.Apply(project, preset);
        ServerMetrics.RecordPresetApplied(preset.Key);
        return Ok(report);
    }
}

public record ApplyPresetRequest(string? PresetKey);
