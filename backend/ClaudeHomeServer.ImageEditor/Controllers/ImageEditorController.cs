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
// Ручки, общие с личным чатом, — в ImageEditorEndpoints (персонажи — в ImageEditorCharacterEndpoints);
// здесь их проектный гейт и то, что есть только у проекта: сохранение в проект.
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
    ImageContextLaunch? context = null,
    ILocalImageMedia? localMedia = null,
    CharacterTrainingPhotos? trainingPhotos = null,
    CharacterPhotoLabeling? labeling = null,
    CharacterLoraRuns? loraRuns = null)
    : ImageEditorCharacterEndpoints(editors, launcher, placeSettings, jobs, steps, config, raster, context,
        files, localMedia, trainingPhotos, labeling, loraRuns)
{
    [HttpGet("catalog")]
    public IActionResult Catalog(string projectId) =>
        Gate(projectId, out _, out var denied) ? CatalogOf() : denied;

    [HttpGet("format")]
    public async Task<IActionResult> Format(string projectId, [FromQuery] string? provider, [FromQuery] string? model,
        [FromQuery] string? op, [FromQuery] string? prompt, CancellationToken ct) =>
        Gate(projectId, out var scope, out var denied) ? await FormatIn(scope, provider, model, op, prompt, ct) : denied;

    [HttpPost("quote")]
    public async Task<IActionResult> Quote(string projectId, [FromBody] ImageEditQuoteRequest req, CancellationToken ct) =>
        Gate(projectId, out var scope, out var denied) && gate.TryOptionalChat(UserId, projectId, req.SessionId, out denied)
            ? await QuoteIn(scope, req, ct) : denied;

    [HttpPost("jobs")]
    [RequestSizeLimit(MaxJobBodyBytes)]
    [RequestFormLimits(MultipartBodyLengthLimit = MaxJobBodyBytes)]
    public async Task<IActionResult> Start(string projectId, [FromForm] StartJobForm form, CancellationToken ct) =>
        Gate(projectId, out var scope, out var denied) && gate.TryOptionalChat(UserId, projectId, form.SessionId, out denied)
            ? await StartIn(scope, form, ct) : denied;

    // Образец с диска человека в рабочую папку модуля (ADR-023 §2.3): дальше — attachRef({kind: 'image', ref: {upload}})
    [HttpPost("uploads")]
    [RequestSizeLimit(MaxUploadBodyBytes)]
    [RequestFormLimits(MultipartBodyLengthLimit = MaxUploadBodyBytes)]
    public async Task<IActionResult> Upload(string projectId, IFormFile? file, CancellationToken ct) =>
        Gate(projectId, out var scope, out var denied) ? await UploadIn(scope, file, ct) : denied;

    [HttpGet("jobs/{jobId}")]
    public IActionResult GetJob(string projectId, string jobId) =>
        Gate(projectId, out var scope, out var denied) ? (JobVisible(scope, jobId) ? JobIn(scope, jobId) : JobNotFound()) : denied;

    [HttpDelete("jobs/{jobId}")]
    public async Task<IActionResult> Cancel(string projectId, string jobId, CancellationToken ct) =>
        Gate(projectId, out var scope, out var denied)
            ? (JobVisible(scope, jobId) ? await CancelIn(scope, jobId, ct) : JobNotFound()) : denied;

    [HttpGet("jobs/{jobId}/variants/{variant:int}")]
    public IActionResult Variant(string projectId, string jobId, int variant) =>
        Gate(projectId, out var scope, out var denied)
            ? (JobVisible(scope, jobId) ? VariantIn(scope, jobId, variant) : JobNotFound()) : denied;

    // ── Правки без ИИ и шаги истории (ADR-018 §9) ──────────────────────────────────

    [HttpPost("transform")]
    public async Task<IActionResult> Transform(string projectId, [FromBody] ImageTransformRequest req,
        [FromQuery] bool dryRun, CancellationToken ct) =>
        Gate(projectId, out var scope, out var denied)
            ? (BaseVisible(scope, req.Base) ? await TransformIn(scope, req, dryRun, ct)
                : Error(StatusCodes.Status404NotFound, ImageEditErrorCodes.JobNotFound, "Задача не найдена"))
            : denied;

    // Картинка шага истории — для холста и миниатюр; токен через ?access_token=, как у вариантов
    [HttpGet("steps/{stepId}")]
    public IActionResult Step(string projectId, string stepId) =>
        Gate(projectId, out var scope, out var denied)
            ? (StepVisible(scope, stepId) ? StepIn(scope, stepId)
                : Error(StatusCodes.Status404NotFound, ImageEditErrorCodes.StepNotFound, "Шаг истории не найден"))
            : denied;

    [HttpPost("save")]
    public async Task<IActionResult> Save(string projectId, [FromBody] ImageEditSaveRequest req)
    {
        if (!ProjectGate(projectId, out var project, out var denied)) return denied;
        if (!gate.TryOptionalChat(UserId, projectId, req.SessionId, out denied)) return denied;
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
            if (!StepVisible(ImageEditScope.Of(project), req.StepId!.Trim()))
                return Error(StatusCodes.Status404NotFound, ImageEditErrorCodes.StepNotFound, "Шаг истории не найден");
            image = Steps.Open(ImageEditScope.TenantOf(project), projectId, req.StepId!.Trim())?.Image;
            if (image is null)
                return Error(StatusCodes.Status404NotFound, ImageEditErrorCodes.StepNotFound, "Шаг истории не найден");
        }
        else
        {
            if (Jobs is null) return JobsUnavailable();
            if (!JobVisible(ImageEditScope.Of(project), req.JobId!.Trim())) return JobNotFound();
            image = Jobs.OpenVariant(ImageEditScope.TenantOf(project), projectId, req.JobId!.Trim(), req.Variant);
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
            Files?.NotifyMutated(project.RootPath, result.Path, FileMutationKind.Write);
            // Нить идёт за сохранённым файлом (ADR-019). Чужая нить или чат молча пропускаются:
            // файл уже сохранён, а ответ не должен выдавать чужое
            if (threads is not null && threads.OwnThread(ImageEditScope.TenantOf(project), project.Id, req.SessionId, req.ThreadId))
                await threads.OnSavedAsync(ImageEditScope.TenantOf(project), project.Id, req.SessionId!.Trim(), req.ThreadId!.Trim(), result.Path,
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
    // Чужой проект — 404 на гейте, поэтому фото персонажа видит только владелец проекта. Тело — в
    // ImageEditorCharacterEndpoints, общее с личным чатом.

    [HttpGet("characters")]
    public IActionResult Characters(string projectId) =>
        ProjectGate(projectId, out var project, out var denied) ? CharactersIn(project) : denied;

    [HttpGet("loras")]
    public async Task<IActionResult> Loras(string projectId, CancellationToken ct) =>
        ProjectGate(projectId, out var project, out var denied)
            ? await LorasIn(ImageEditScope.TenantOf(project), project, ct) : denied;

    [HttpGet("characters/{slug}")]
    public IActionResult Character(string projectId, string slug) =>
        ProjectGate(projectId, out var project, out var denied) ? CharacterIn(project, slug) : denied;

    [HttpPost("characters")]
    [RequestSizeLimit(MaxCharacterBodyBytes)]
    [RequestFormLimits(MultipartBodyLengthLimit = MaxCharacterBodyBytes)]
    public async Task<IActionResult> CreateCharacter(string projectId, [FromForm] CharacterForm form, CancellationToken ct) =>
        ProjectGate(projectId, out var project, out var denied) ? await CreateCharacterIn(project, form, ct) : denied;

    [HttpPut("characters/{slug}")]
    [RequestSizeLimit(MaxCharacterBodyBytes)]
    [RequestFormLimits(MultipartBodyLengthLimit = MaxCharacterBodyBytes)]
    public async Task<IActionResult> UpdateCharacter(string projectId, string slug, [FromForm] CharacterForm form, CancellationToken ct) =>
        ProjectGate(projectId, out var project, out var denied) ? await UpdateCharacterIn(project, slug, form, ct) : denied;

    [HttpDelete("characters/{slug}")]
    public IActionResult DeleteCharacter(string projectId, string slug) =>
        ProjectGate(projectId, out var project, out var denied) ? DeleteCharacterIn(project, slug) : denied;

    [HttpGet("characters/{slug}/photos/{file}")]
    public IActionResult CharacterPhoto(string projectId, string slug, string file) =>
        ProjectGate(projectId, out var project, out var denied) ? CharacterPhotoIn(project, slug, file) : denied;

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

    // Артефакты по id (находка 4.3): привязка к чату проекта — зритель обязан его видеть. Задачи нет —
    // true: «не найдена» ответит сама ручка
    private bool JobVisible(ImageEditScope scope, string jobId)
    {
        var chat = Jobs?.Get(scope.Tenant, scope.Key, jobId)?.ChatSessionId;
        return gate.TryArtifactChat(UserId, scope.Key, chat);
    }

    private bool StepVisible(ImageEditScope scope, string stepId) =>
        Steps?.Open(scope.Tenant, scope.Key, stepId) is not { Step.JobId: { } jobId } || JobVisible(scope, jobId);

    private bool BaseVisible(ImageEditScope scope, ImageTransformBase? b) =>
        b is null
        || (string.IsNullOrWhiteSpace(b.JobId) || JobVisible(scope, b.JobId.Trim()))
        && (string.IsNullOrWhiteSpace(b.StepId) || StepVisible(scope, b.StepId.Trim()));

}
