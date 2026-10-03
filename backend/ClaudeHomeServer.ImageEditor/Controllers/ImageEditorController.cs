using System.Diagnostics.CodeAnalysis;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Composition;
using ClaudeHomeServer.Services.ImageEditor.ChatContext;
using ClaudeHomeServer.Services.ImageEditor.Threads;
using ClaudeHomeServer.Services.ImageEditor.Versioning;
using ClaudeHomeServer.Services.Images.Editing.Raster;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ClaudeHomeServer.Services.ImageEditor.Controllers;

// Редактор картинок в проекте (ADR-017, раздел 1). Контроллер живёт в модуле редактора
// (ADR-018 §10.1), спину берёт через швы Core: владение проектом, флаг, уведомления о файлах.
// Ручки, общие с личным чатом, — в ImageEditorEndpoints; здесь их проектный гейт и то, что есть
// только у проекта: сохранение в проект и персонажи.
[ProjectCapability(ProjectCapabilityArea.FileBound)]
[ApiController]
[Authorize]
[Route("api/projects/{projectId}/image-editor")]
public class ImageEditorController(
    ImageEditScopeGate gate,
    IEnumerable<IImageEditor> editors,
    ImageEditLaunchAssembler launcher,
    IImagePlaceSettings? placeSettings = null,
    IImageEditJobs? jobs = null,
    IImageEditSaver? saver = null,
    IProjectFiles? files = null,
    ImageEditSteps? steps = null,
    IConfiguration? config = null,
    IImageRaster? raster = null,
    ImageThreadService? threads = null,
    ImageContextLaunch? context = null)
    : ImageEditorEndpoints(editors, launcher, placeSettings, jobs, steps, config, raster, context)
{
    [HttpGet("catalog")]
    public IActionResult Catalog(string projectId) =>
        Gate(projectId, out _, out var denied) ? CatalogOf() : denied;

    [HttpPost("quote")]
    public async Task<IActionResult> Quote(string projectId, [FromBody] ImageEditQuoteRequest req, CancellationToken ct) =>
        Gate(projectId, out var scope, out var denied) ? await QuoteIn(scope, req, ct) : denied;

    [HttpPost("jobs")]
    [RequestSizeLimit(MaxJobBodyBytes)]
    [RequestFormLimits(MultipartBodyLengthLimit = MaxJobBodyBytes)]
    public async Task<IActionResult> Start(string projectId, [FromForm] StartJobForm form, CancellationToken ct) =>
        Gate(projectId, out var scope, out var denied) ? await StartIn(scope, form, ct) : denied;

    // Образец с диска человека в рабочую папку модуля (ADR-023 §2.3): дальше — attachRef({kind: 'image', ref: {upload}})
    [HttpPost("uploads")]
    [RequestSizeLimit(MaxUploadBodyBytes)]
    [RequestFormLimits(MultipartBodyLengthLimit = MaxUploadBodyBytes)]
    public async Task<IActionResult> Upload(string projectId, IFormFile? file, CancellationToken ct) =>
        Gate(projectId, out _, out var denied) ? await UploadIn(file, ct) : denied;

    [HttpGet("jobs/{jobId}")]
    public IActionResult GetJob(string projectId, string jobId) =>
        Gate(projectId, out var scope, out var denied) ? JobIn(scope, jobId) : denied;

    [HttpDelete("jobs/{jobId}")]
    public async Task<IActionResult> Cancel(string projectId, string jobId, CancellationToken ct) =>
        Gate(projectId, out var scope, out var denied) ? await CancelIn(scope, jobId, ct) : denied;

    [HttpGet("jobs/{jobId}/variants/{variant:int}")]
    public IActionResult Variant(string projectId, string jobId, int variant) =>
        Gate(projectId, out var scope, out var denied) ? VariantIn(scope, jobId, variant) : denied;

    // ── Правки без ИИ и шаги истории (ADR-018 §9) ──────────────────────────────────

    [HttpPost("transform")]
    public async Task<IActionResult> Transform(string projectId, [FromBody] ImageTransformRequest req,
        [FromQuery] bool dryRun, CancellationToken ct) =>
        Gate(projectId, out var scope, out var denied) ? await TransformIn(scope, req, dryRun, ct) : denied;

    // Картинка шага истории — для холста и миниатюр; токен через ?access_token=, как у вариантов
    [HttpGet("steps/{stepId}")]
    public IActionResult Step(string projectId, string stepId) =>
        Gate(projectId, out var scope, out var denied) ? StepIn(scope, stepId) : denied;

    [HttpPost("save")]
    public async Task<IActionResult> Save(string projectId, [FromBody] ImageEditSaveRequest req)
    {
        if (!ProjectGate(projectId, out var project, out var denied)) return denied;
        if (saver is null) return JobsUnavailable();
        if (Raster is null) return RasterUnavailable();

        var mode = string.IsNullOrWhiteSpace(req.Mode) ? ImageEditSaveModes.NextVersion : req.Mode.Trim();
        if (mode is not (ImageEditSaveModes.NextVersion or ImageEditSaveModes.As))
            return Error(StatusCodes.Status400BadRequest, ImageEditErrorCodes.InvalidRequest,
                $"Неизвестный режим сохранения «{mode}»");

        foreach (var rel in new[] { req.SourcePath, req.Folder })
        {
            if (string.IsNullOrWhiteSpace(rel)) continue;
            if (!TryJoinInside(project.RootPath, rel, out _))
                return Error(StatusCodes.Status400BadRequest, ImageEditErrorCodes.InvalidRequest,
                    "Путь вне папки проекта");
        }

        // Источник — ровно одно: вариант задачи или шаг истории (ADR-018 §5). Пустой запрос —
        // ошибка запроса, а не «задача не найдена»: иначе фронт искал бы несуществующую задачу
        var hasJob = !string.IsNullOrWhiteSpace(req.JobId);
        var hasStep = !string.IsNullOrWhiteSpace(req.StepId);
        if (hasJob == hasStep)
            return Error(StatusCodes.Status400BadRequest, ImageEditErrorCodes.InvalidRequest,
                "Источник сохранения — ровно одно: вариант задачи или шаг истории");

        EditedImage? image;
        if (hasStep)
        {
            if (Steps is null) return JobsUnavailable();
            image = Steps.Open(UserId, projectId, req.StepId!.Trim())?.Image;
            if (image is null)
                return Error(StatusCodes.Status404NotFound, ImageEditErrorCodes.StepNotFound, "Шаг истории не найден");
        }
        else
        {
            if (Jobs is null) return JobsUnavailable();
            image = Jobs.OpenVariant(UserId, projectId, req.JobId!.Trim(), req.Variant);
            if (image is null) return JobNotFound();
        }

        // Перекодирование при записи (раздел 9): расширение затем ставится по новому формату
        if (req.Encode is { } encode)
        {
            var encoded = Raster.Encode(image.Bytes, encode);
            if (!encoded.Ok)
                return Error(encoded.StatusCode,
                    encoded.Error == RasterError.Busy ? ImageEditErrorCodes.TooManyJobs : ImageEditErrorCodes.InvalidRequest,
                    encoded.Message ?? "Перекодирование не выполнено");
            image = new EditedImage(encoded.Image!.Bytes, InputFitter.ContentTypeOf(encoded.Image.Format));
        }

        var saved = mode == ImageEditSaveModes.As
            ? saver.SaveAs(project.RootPath, req.Folder, req.FileName, image)
            : saver.Save(project.RootPath, req, image);
        if (saved.ErrorCode == ImageEditErrorCodes.NameTaken)
        {
            // Имя человек выбрал явно — не подменяем его номером, а подсказываем свободное
            var check = saver.Check(project.RootPath, req.Folder, req.FileName, ImageEditSaver.ExtensionOf(image.Bytes));
            return StatusCode(StatusCodes.Status409Conflict,
                new { error = saved.Error, code = saved.ErrorCode, suggestion = check.Value?.Suggestion });
        }
        // Новый файл — обычная запись в проект: синк знаний и ватчеры узнают о нём сразу
        if (saved.Value is { } result)
        {
            files?.NotifyMutated(project.RootPath, result.Path, FileMutationKind.Write);
            // Нить идёт за сохранённым файлом (ADR-019). Чужая нить или чат молча пропускаются:
            // файл уже сохранён, а ответ не должен выдавать чужое
            if (threads is not null && threads.OwnThread(UserId, project.Id, req.SessionId, req.ThreadId))
                await threads.OnSavedAsync(UserId, project.Id, req.SessionId!.Trim(), req.ThreadId!.Trim(), result.Path,
                    HttpContext?.RequestAborted ?? CancellationToken.None);
        }
        return Map(saved, Ok);
    }

    // Проверка имени «Сохранить как…» на лету: ничего не пишет, решение всё равно за CreateNew
    [HttpGet("save/check")]
    public IActionResult SaveCheck(string projectId, [FromQuery] string? folder, [FromQuery] string? name,
        [FromQuery] string? format)
    {
        if (!ProjectGate(projectId, out var project, out var denied)) return denied;
        if (saver is null) return JobsUnavailable();
        if (ExtensionFor(format) is not { } ext)
            return Error(StatusCodes.Status400BadRequest, ImageEditErrorCodes.InvalidRequest,
                "Формат — png, jpeg, webp или gif");
        return Map(saver.Check(project.RootPath, folder, name, ext), Ok);
    }

    private static string? ExtensionFor(string? format) =>
        (format ?? "").Trim().TrimStart('.').ToLowerInvariant() switch
        {
            "png" => ".png",
            "jpg" or "jpeg" => ".jpg",
            "webp" => ".webp",
            "gif" => ".gif",
            _ => null,
        };

    // ── Персонажи (ADR-017, раздел 10) ─────────────────────────────────────────────
    // Папка проекта characters/<slug>/. Чужой проект — 404 на гейте, поэтому фото
    // персонажа видит только владелец проекта. Невалидный slug неотличим от отсутствующего.

    // Потолок тела формы персонажа: 10 фото по 8 МБ плюс поля
    private const long MaxCharacterBodyBytes = 100L * 1024 * 1024;

    [HttpGet("characters")]
    public IActionResult Characters(string projectId)
    {
        if (!ProjectGate(projectId, out var project, out var denied)) return denied;
        return Ok(CharacterStore.List(project.RootPath).Select(CharacterDto.From));
    }

    [HttpGet("characters/{slug}")]
    public IActionResult Character(string projectId, string slug)
    {
        if (!ProjectGate(projectId, out var project, out var denied)) return denied;
        var manifest = CharacterStore.Get(project.RootPath, slug);
        return manifest is null ? CharacterNotFound() : Ok(CharacterDto.From(manifest));
    }

    [HttpPost("characters")]
    [RequestSizeLimit(MaxCharacterBodyBytes)]
    [RequestFormLimits(MultipartBodyLengthLimit = MaxCharacterBodyBytes)]
    public async Task<IActionResult> CreateCharacter(string projectId, [FromForm] CharacterForm form, CancellationToken ct)
    {
        if (!ProjectGate(projectId, out var project, out var denied)) return denied;
        var photos = await PhotosAsync(form.Photos, form.Angles, ct);
        var draft = new CharacterDraft(form.Name ?? "", form.Description, photos);
        var created = CharacterStore.Create(project.RootPath, draft, DateTime.UtcNow);
        if (created.Value is { } change) Notify(project.RootPath, change, FileMutationKind.Create);
        return Map(created, c => StatusCode(StatusCodes.Status201Created, CharacterDto.From(c.Manifest)));
    }

    [HttpPut("characters/{slug}")]
    [RequestSizeLimit(MaxCharacterBodyBytes)]
    [RequestFormLimits(MultipartBodyLengthLimit = MaxCharacterBodyBytes)]
    public async Task<IActionResult> UpdateCharacter(string projectId, string slug, [FromForm] CharacterForm form, CancellationToken ct)
    {
        if (!ProjectGate(projectId, out var project, out var denied)) return denied;
        var photos = await PhotosAsync(form.Photos, form.Angles, ct);
        var patch = new CharacterPatch(form.Name, form.Description, photos, form.RemovePhotos ?? [], form.PrimaryPhoto);
        var updated = CharacterStore.Update(project.RootPath, slug, patch);
        if (updated is null) return CharacterNotFound();
        if (updated.Value is { } change) Notify(project.RootPath, change, FileMutationKind.Write);
        return Map(updated, c => Ok(CharacterDto.From(c.Manifest)));
    }

    [HttpDelete("characters/{slug}")]
    public IActionResult DeleteCharacter(string projectId, string slug)
    {
        if (!ProjectGate(projectId, out var project, out var denied)) return denied;
        if (!CharacterStore.Delete(project.RootPath, slug)) return CharacterNotFound();
        files?.NotifyMutated(project.RootPath, $"{CharacterStore.Folder}/{slug}", FileMutationKind.Delete);
        return NoContent();
    }

    [HttpGet("characters/{slug}/photos/{file}")]
    public IActionResult CharacterPhoto(string projectId, string slug, string file)
    {
        if (!ProjectGate(projectId, out var project, out var denied)) return denied;
        var photo = CharacterStore.OpenPhoto(project.RootPath, slug, file);
        return photo is null ? CharacterNotFound() : File(photo.Bytes, photo.ContentType);
    }

    // Поля формы персонажа. Angles — параллельный список к Photos (front, three-quarter…).
    // В PUT пустое поле — не менять; RemovePhotos — имена файлов из манифеста.
    public sealed class CharacterForm
    {
        public string? Name { get; set; }
        public string? Description { get; set; }
        public List<IFormFile>? Photos { get; set; }
        public List<string>? Angles { get; set; }
        public List<string>? RemovePhotos { get; set; }
        public string? PrimaryPhoto { get; set; }
    }

    // Проектный гейт области: флаг и чужой проект одинаково 404 (ImageEditScopeGate)
    private bool Gate(string projectId, [NotNullWhen(true)] out ImageEditScope? scope,
        [NotNullWhen(false)] out IActionResult? denied)
    {
        scope = null;
        if (!gate.TryProject(UserId, projectId, out var project, out denied)) return false;
        scope = ImageEditScope.Of(project);
        return true;
    }

    // Ручки только проекта (сохранение, персонажи) — им нужен сам проект, а не область
    private bool ProjectGate(string projectId, [NotNullWhen(true)] out Project? project,
        [NotNullWhen(false)] out IActionResult? denied) =>
        gate.TryProject(UserId, projectId, out project, out denied);

    private IActionResult CharacterNotFound() =>
        Error(StatusCodes.Status404NotFound, ImageEditErrorCodes.CharacterNotFound, "Персонаж не найден");

    private void Notify(string root, CharacterChange change, FileMutationKind kind)
    {
        if (files is null) return;
        foreach (var rel in change.Written) files.NotifyMutated(root, rel, kind);
        foreach (var rel in change.Deleted) files.NotifyMutated(root, rel, FileMutationKind.Delete);
    }

    private static async Task<List<CharacterPhotoUpload>> PhotosAsync(
        List<IFormFile>? photos, List<string>? angles, CancellationToken ct)
    {
        var result = new List<CharacterPhotoUpload>();
        var list = photos ?? [];
        for (var i = 0; i < list.Count; i++)
            result.Add(new CharacterPhotoUpload(await ReadAsync(list[i], ct),
                angles is not null && i < angles.Count ? angles[i] : null));
        return result;
    }
}
