using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Access;
using ClaudeHomeServer.Core.Telemetry;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ClaudeHomeServer.Services.Composition;

namespace ClaudeHomeServer.Controllers;

/// <summary>
/// Применение пресета каркаса к проекту (знакомство v2, п.3 плана). Единственная дверь:
/// тело строго { presetKey }, никаких путей и имён папок снаружи — состав лежит в
/// PresetCatalog и пишется через ProjectPresetService.
/// </summary>
[ProjectCapability(ProjectCapabilityArea.Platform, ProjectKey = "id")]
[ProjectScope(Key = "id", Right = ProjectRight.Write)]
[ApiController]
[Authorize]
[Route("api/projects/{id}/preset")]
public class ProjectPresetsController(ProjectManager projects, ProjectPresetService presets)
    : ControllerBase
{
    // Доступ к проекту проверил [ProjectScope] (чужой — 404, а не 403: существование не подтверждаем)
    private Project? Scoped(string id) => projects.GetById(id);

    // Применить пресет ({ presetKey: "<ключ каталога>" }) или зафиксировать отказ
    // ({ presetKey: "none" }). Идемпотентность: PresetKey != "pending" → 409 —
    // действует и на применение, и на отказ, и на проекты, созданные до фичи (null).
    [HttpPost]
    // Раскладывает папки и файлы пресета в папку проекта
    [ProjectCapability(ProjectCapabilityArea.FileBound, ProjectKey = "id")]
    public ActionResult Apply(string id, [FromBody] ApplyPresetRequest req)
    {
        if (Scoped(id) is not { } project) return NotFound();

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
