using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text.Json.Serialization;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.Composition;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ClaudeHomeServer.Services.ImageEditor.Controllers;

// Версии LoRA персонажа: список, подключение, переименование, удаление (ADR-026-contracts §7.3).
// Запись в манифест персонажа — только через CharacterStore; файл на GPU стирает доводчик по статусу deleting.
[ProjectCapability(ProjectCapabilityArea.FileBound)]
[ApiController]
[Authorize]
[Route("api/projects/{projectId}/image-editor/characters/{slug}/lora-versions")]
public class CharacterLoraVersionsController(
    ImageEditScopeGate gate,
    CharacterLoraRuns runs,
    IProjectFiles? files = null,
    ILocalImageMedia? localMedia = null,
    IFeatureFlagGate? flags = null) : ControllerBase
{
    private const int MaxTitle = 60;

    private string UserId => User.FindFirstValue(JwtRegisteredClaimNames.Sub)!;

    [HttpGet]
    public IActionResult List(string projectId, string slug)
    {
        if (!Character(projectId, slug, out var project, out var manifest, out var denied)) return denied;
        var all = runs.List(project.RootPath, slug);
        var active = manifest.ReadLora()?.Name;
        // Своя LoRA, обученная у другого персонажа проекта, несёт дату обучения (аддитивно: поле trainedAt внутри own)
        DateTimeOffset? ownTrained = null;
        if (active is not null && runs.TrainedDates(project.RootPath).TryGetValue(active, out var at)) ownTrained = at;
        var own = active is not null && !all.Any(r => CharacterLoraRuns.IsConnected(r, active))
            ? new { lora = active, trainedAt = ownTrained } : null;
        return Ok(new
        {
            items = all.Select(r => Dto(r, active)),
            own,
            limit = CharacterLoraRuns.MaxVersions,
            used = runs.CountTowardsLimit(project.RootPath, slug),
        });
    }

    [HttpPost("{runId}/connect")]
    public async Task<IActionResult> Connect(string projectId, string slug, string runId, CancellationToken ct)
    {
        if (!Character(projectId, slug, out var project, out var manifest, out var denied)) return denied;
        if (Run(project, slug, runId) is not { } run) return VersionNotFound();
        if (run.Status != CharacterLoraRuns.Status.Ready || run.LoraFile is not { Length: > 0 } loraFile)
            return Error(StatusCodes.Status409Conflict, "version_not_ready", "Версия ещё не обучена");
        if (!LoraNamespace.VisibleTo(UserId, loraFile))
            return Error(StatusCodes.Status403Forbidden, ImageEditErrorCodes.LoraNotVisible, "Эта LoRA вам недоступна.");
        if (localMedia is null) return NoSeam();
        var known = await localMedia.ListLorasAsync(ct);
        if (known is null)
            return Error(StatusCodes.Status503ServiceUnavailable, "unavailable", "ComfyUI не отвечает, подключить LoRA не получилось");
        if (!known.Contains(loraFile, StringComparer.Ordinal))
            return Error(StatusCodes.Status409Conflict, "lora_missing", "Файла этой LoRA нет в ComfyUI");

        using var _ = await runs.LockCharacterAsync(project.RootPath, slug, ct);
        manifest = CharacterStore.Get(project.RootPath, slug) ?? manifest;
        var strength = manifest.ReadLora()?.Strength;
        var patch = new CharacterPatch(null, null, [], [], null, CharacterLoraPatch.Set(loraFile, run.Trigger, strength, known));
        var updated = CharacterStore.Update(project.RootPath, UserId, slug, patch);
        if (updated?.Value is not { } change)
            return Error(StatusCodes.Status409Conflict, updated?.ErrorCode ?? ImageEditErrorCodes.InvalidRequest,
                updated?.Error ?? "Не удалось подключить LoRA");
        Notify(project.RootPath, change);
        return Ok(CharacterDto.From(change.Manifest, flags?.IsEnabled(UserId, FeatureFlagKeys.CharacterSmartPhotos) == true));
    }

    [HttpPatch("{runId}")]
    public IActionResult Rename(string projectId, string slug, string runId, [FromBody] RenameRequest? req)
    {
        if (!Character(projectId, slug, out var project, out var manifest, out var denied)) return denied;
        if (Run(project, slug, runId) is not { } run) return VersionNotFound();
        if (run.Status == CharacterLoraRuns.Status.Training) return VersionTraining();
        var title = req?.Title?.Trim();
        if (title is { Length: > MaxTitle })
            return Error(StatusCodes.Status400BadRequest, "bad_title", $"Название — не длиннее {MaxTitle} символов");
        if (string.IsNullOrEmpty(title)) title = null;
        var updated = runs.Update(project.RootPath, slug, runId, r => r with { Title = title });
        return updated is null ? VersionNotFound() : Ok(Dto(updated, manifest.ReadLora()?.Name));
    }

    [HttpDelete("{runId}")]
    public async Task<IActionResult> Delete(string projectId, string slug, string runId, CancellationToken ct)
    {
        if (!Character(projectId, slug, out var project, out var manifest, out var denied)) return denied;
        if (Run(project, slug, runId) is not { } run) return VersionNotFound();
        if (run.Status == CharacterLoraRuns.Status.Training) return VersionTraining();

        using var _ = await runs.LockCharacterAsync(project.RootPath, slug, ct);
        manifest = CharacterStore.Get(project.RootPath, slug) ?? manifest;
        CharacterDto? character = null;
        var disconnected = CharacterLoraRuns.IsConnected(run, manifest.ReadLora()?.Name);
        if (disconnected)
        {
            var updated = CharacterStore.Update(project.RootPath, UserId, slug,
                new CharacterPatch(null, null, [], [], null, CharacterLoraPatch.Removal()));
            if (updated?.Value is not { } change)
                return Error(StatusCodes.Status409Conflict, updated?.ErrorCode ?? ImageEditErrorCodes.InvalidRequest,
                    updated?.Error ?? "Не удалось снять LoRA с персонажа");
            Notify(project.RootPath, change);
            character = CharacterDto.From(change.Manifest, flags?.IsEnabled(UserId, FeatureFlagKeys.CharacterSmartPhotos) == true);
        }
        if (runs.Update(project.RootPath, slug, runId, r => r with { Status = CharacterLoraRuns.Status.Deleting }) is null)
            return VersionNotFound();
        return Accepted(new { status = "deleting", disconnected, character });
    }

    // Deleting-запись для человека уже не существует: список её не показывает
    private CharacterLoraRun? Run(Project project, string slug, string runId) =>
        runs.Get(project.RootPath, slug, runId) is { Status: not CharacterLoraRuns.Status.Deleting } run ? run : null;

    private static VersionDto Dto(CharacterLoraRun r, string? active) => new(
        r.RunId, r.Status, r.Title, r.CreatedAt, r.TrainedAt, r.PhotosCount, r.PhotosKept, r.Trigger,
        CharacterLoraRuns.IsConnected(r, active), r.Rejected, r.Error,
        r.Status == CharacterLoraRuns.Status.Ready ? r.LoraFile : null);

    private void Notify(string root, CharacterChange change)
    {
        if (files is null) return;
        foreach (var rel in change.Written) files.NotifyMutated(root, rel, FileMutationKind.Write);
        foreach (var rel in change.Deleted) files.NotifyMutated(root, rel, FileMutationKind.Delete);
    }

    private bool Character(string projectId, string slug, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out Project? project,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out CharacterManifest? manifest,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(false)] out IActionResult? denied)
    {
        manifest = null;
        if (!gate.TryProject(UserId, projectId, out project, out denied)) return false;
        manifest = CharacterStore.IsValidSlug(slug) ? CharacterStore.Get(project.RootPath, slug) : null;
        if (manifest is null)
        {
            denied = Error(StatusCodes.Status404NotFound, ImageEditErrorCodes.CharacterNotFound, "Персонаж не найден");
            return false;
        }
        return true;
    }

    private ObjectResult Error(int status, string code, string error) => StatusCode(status, new { error, code });

    private IActionResult NoSeam() =>
        Error(StatusCodes.Status503ServiceUnavailable, "no_seam", "Подключение LoRA недоступно: подсистема картинок выключена");

    private IActionResult VersionNotFound() =>
        Error(StatusCodes.Status404NotFound, "version_not_found", "Версия LoRA не найдена");

    private IActionResult VersionTraining() =>
        Error(StatusCodes.Status409Conflict, "version_training", "Версия ещё обучается — сначала отмените обучение");

    public sealed record RenameRequest(string? Title);

    private sealed record VersionDto(
        string RunId, string Status, string? Title, DateTimeOffset CreatedAt,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] DateTimeOffset? TrainedAt,
        int PhotosCount,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? PhotosKept,
        string Trigger, bool Connected,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<LoraRunRejection>? Rejected,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] LoraRunError? Error,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? LoraFile);
}
