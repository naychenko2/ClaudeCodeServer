using System.Diagnostics.CodeAnalysis;
using ClaudeHomeServer.Services.Composition;
using ClaudeHomeServer.Services.ImageEditor.ChatContext;
using ClaudeHomeServer.Services.ImageEditor.Prefs;
using ClaudeHomeServer.Services.ImageEditor.Threads;
using ClaudeHomeServer.Services.Images.Editing.Raster;
using ClaudeHomeServer.Services.Media;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ClaudeHomeServer.Services.ImageEditor.Controllers;

// Редактор картинок в личном чате вне проекта (разрез
// docs/research/image-editor-personal-chats-cut-2026-09.md, «Бэкенд» п.3). Тело ручек — общее с
// проектом (ImageEditorEndpoints, ImageThreadEndpoints), здесь только личный гейт и выбор человека.
// save и save/check пишут в «Личное» (Personal/images/, ADR-040 §5); characters* здесь нет — Ф2-4.
[ProjectCapability(ProjectCapabilityArea.Platform)]
[ApiController]
[Authorize]
[Route("api/image-editor/chats/{sessionId}")]
public class PersonalImageEditorController(
    ImageEditScopeGate gate,
    IEnumerable<IImageEditor> editors,
    ImageEditLaunchAssembler launcher,
    ImageProjectPrefsService prefs,
    IImagePlaceSettings? placeSettings = null,
    IImageEditJobs? jobs = null,
    ImageEditSteps? steps = null,
    IConfiguration? config = null,
    IImageRaster? raster = null,
    ImageContextLaunch? context = null,
    IImageEditSaver? saver = null)
    : ImageEditorEndpoints(editors, launcher, placeSettings, jobs, steps, config, raster, context)
{
    [HttpGet("catalog")]
    public IActionResult Catalog(string sessionId) =>
        Gate(sessionId, out _, out var denied) ? CatalogOf() : denied;

    [HttpGet("format")]
    public async Task<IActionResult> Format(string sessionId, [FromQuery] string? provider, [FromQuery] string? model,
        [FromQuery] string? op, [FromQuery] string? prompt, CancellationToken ct) =>
        Gate(sessionId, out var scope, out var denied) ? await FormatIn(scope, provider, model, op, prompt, ct) : denied;

    [HttpPost("quote")]
    public async Task<IActionResult> Quote(string sessionId, [FromBody] ImageEditQuoteRequest req, CancellationToken ct) =>
        Gate(sessionId, out var scope, out var denied) ? await QuoteIn(scope, req, ct) : denied;

    [HttpPost("jobs")]
    [RequestSizeLimit(MaxJobBodyBytes)]
    [RequestFormLimits(MultipartBodyLengthLimit = MaxJobBodyBytes)]
    public async Task<IActionResult> Start(string sessionId, [FromForm] StartJobForm form, CancellationToken ct)
    {
        if (!Gate(sessionId, out var scope, out var denied)) return denied;
        // Образцы и исходник из файлов — вход, которому нужен диск: «Личное» создаём здесь, а не на GET
        if (form.ReferencePaths is { Count: > 0 } || !string.IsNullOrWhiteSpace(form.SourcePath))
            scope = await gate.WithFolderAsync(scope, ct);
        return await StartIn(scope, form, ct);
    }

    [HttpPost("uploads")]
    [RequestSizeLimit(MaxUploadBodyBytes)]
    [RequestFormLimits(MultipartBodyLengthLimit = MaxUploadBodyBytes)]
    public async Task<IActionResult> Upload(string sessionId, IFormFile? file, CancellationToken ct) =>
        Gate(sessionId, out var scope, out var denied) ? await UploadIn(scope, file, ct) : denied;

    [HttpGet("jobs/{jobId}")]
    public IActionResult GetJob(string sessionId, string jobId) =>
        Gate(sessionId, out var scope, out var denied) ? JobIn(scope, jobId) : denied;

    [HttpDelete("jobs/{jobId}")]
    public async Task<IActionResult> Cancel(string sessionId, string jobId, CancellationToken ct) =>
        Gate(sessionId, out var scope, out var denied) ? await CancelIn(scope, jobId, ct) : denied;

    [HttpGet("jobs/{jobId}/variants/{variant:int}")]
    public IActionResult Variant(string sessionId, string jobId, int variant) =>
        Gate(sessionId, out var scope, out var denied) ? VariantIn(scope, jobId, variant) : denied;

    // От шага (Base.StepId) или от файла в «Личном» (Base.Path)
    [HttpPost("transform")]
    public async Task<IActionResult> Transform(string sessionId, [FromBody] ImageTransformRequest req,
        [FromQuery] bool dryRun, CancellationToken ct)
    {
        if (!Gate(sessionId, out var scope, out var denied)) return denied;
        if (req.Base?.Path is { Length: > 0 }) scope = await gate.WithFolderAsync(scope, ct);
        return await TransformIn(scope, req, dryRun, ct);
    }

    [HttpGet("steps/{stepId}")]
    public IActionResult Step(string sessionId, string stepId) =>
        Gate(sessionId, out var scope, out var denied) ? StepIn(scope, stepId) : denied;

    // Выбор человека личной области — один на владельца, как и сама область
    [HttpGet("prefs")]
    public IActionResult GetPrefs(string sessionId) =>
        Gate(sessionId, out var scope, out var denied) ? Ok(prefs.Get(scope.Tenant, scope)) : denied;

    // characterSlug принудительно null: персонажей у личной области нет
    [HttpPut("prefs")]
    public async Task<IActionResult> PutPrefs(string sessionId, [FromBody] ImageProjectPrefs? req)
    {
        if (!Gate(sessionId, out var scope, out var denied)) return denied;
        if (ImageProjectPrefsService.Validate(req is null ? null : req with { CharacterSlug = null }) is { } error)
            return Error(StatusCodes.Status400BadRequest, ImageEditErrorCodes.InvalidRequest, error);
        return Ok(await prefs.SetAsync(scope.Tenant, scope, req!));
    }

    // Сохранить в «Личное»: Personal/images/ (ADR-040 §5). Источник — вариант задачи или шаг истории, в том
    // числе старой личной нити: байты копируются в папку новым файлом (CreateNew), перезаписи нет.
    // Нить за сохранённым файлом не идёт: нити по файлу в личной области — отдельный путь (ThreadsController).
    [HttpPost("save")]
    public async Task<IActionResult> Save(string sessionId, [FromBody] ImageEditSaveRequest req, CancellationToken ct)
    {
        if (!Gate(sessionId, out var scope, out var denied)) return denied;
        if (saver is null) return JobsUnavailable();
        if (Raster is null) return RasterUnavailable();

        var mode = string.IsNullOrWhiteSpace(req.Mode) ? ImageEditSaveModes.NextVersion : req.Mode.Trim();
        if (mode is not (ImageEditSaveModes.NextVersion or ImageEditSaveModes.As))
            return Error(StatusCodes.Status400BadRequest, ImageEditErrorCodes.InvalidRequest,
                $"Неизвестный режим сохранения «{mode}»");
        if (PersonalMediaFolders.Resolve(PersonalMediaFolders.Images, req.Folder, out var folder) is { } folderError)
            return Error(StatusCodes.Status400BadRequest, ImageEditErrorCodes.InvalidRequest, folderError);
        if (!string.IsNullOrWhiteSpace(req.SourcePath) && !PersonalMediaFolders.Allows(PersonalMediaFolders.Images, req.SourcePath))
            return Error(StatusCodes.Status400BadRequest, ImageEditErrorCodes.InvalidRequest, "Путь вне папки проекта");

        var hasJob = !string.IsNullOrWhiteSpace(req.JobId);
        var hasStep = !string.IsNullOrWhiteSpace(req.StepId);
        if (hasJob == hasStep)
            return Error(StatusCodes.Status400BadRequest, ImageEditErrorCodes.InvalidRequest,
                "Источник сохранения — ровно одно: вариант задачи или шаг истории");

        EditedImage? image;
        if (hasStep)
        {
            if (Steps is null) return JobsUnavailable();
            image = Steps.Open(scope.Tenant, scope.Key, req.StepId!.Trim())?.Image;
            if (image is null)
                return Error(StatusCodes.Status404NotFound, ImageEditErrorCodes.StepNotFound, "Шаг истории не найден");
        }
        else
        {
            if (Jobs is null) return JobsUnavailable();
            image = Jobs.OpenVariant(scope.Tenant, scope.Key, req.JobId!.Trim(), req.Variant);
            if (image is null) return JobNotFound();
        }

        if (req.Encode is { } encode)
        {
            var encoded = Raster.Encode(image.Bytes, encode);
            if (!encoded.Ok)
                return Error(encoded.StatusCode,
                    encoded.Error == RasterError.Busy ? ImageEditErrorCodes.TooManyJobs : ImageEditErrorCodes.InvalidRequest,
                    encoded.Message ?? "Перекодирование не выполнено");
            image = new EditedImage(encoded.Image!.Bytes, InputFitter.ContentTypeOf(encoded.Image.Format));
        }

        // Папку «Личного» создаём только здесь, на пишущем пути
        scope = await gate.WithFolderAsync(scope, ct);
        if (scope.Project is not { } project)
            return Error(StatusCodes.Status503ServiceUnavailable, PersonalFolderResolver.UnavailableCode,
                PersonalFolderResolver.UnavailableText);
        if (PersonalMediaFolders.EnsureDirectory(project.RootPath, folder) is null)
            return Error(StatusCodes.Status400BadRequest, ImageEditErrorCodes.InvalidRequest, "Путь вне папки проекта");

        var saved = mode == ImageEditSaveModes.As
            ? saver.SaveAs(project.RootPath, folder, req.FileName, image)
            : saver.Save(project.RootPath, req with { Folder = folder }, image);
        if (saved.ErrorCode == ImageEditErrorCodes.NameTaken)
        {
            var check = saver.Check(project.RootPath, folder, req.FileName, ImageEditSaver.ExtensionOf(image.Bytes));
            return StatusCode(StatusCodes.Status409Conflict,
                new { error = saved.Error, code = saved.ErrorCode, suggestion = check.Value?.Suggestion });
        }
        return Map(saved, Ok);
    }

    // Проверка имени «Сохранить как…» на лету: ничего не пишет и папку «Личного» не создаёт (GET)
    [HttpGet("save/check")]
    public IActionResult SaveCheck(string sessionId, [FromQuery] string? folder, [FromQuery] string? name,
        [FromQuery] string? format)
    {
        if (!Gate(sessionId, out var scope, out var denied)) return denied;
        if (saver is null) return JobsUnavailable();
        if (ExtensionFor(format) is not { } ext)
            return Error(StatusCodes.Status400BadRequest, ImageEditErrorCodes.InvalidRequest,
                "Формат — png, jpeg, webp или gif");
        if (PersonalMediaFolders.Resolve(PersonalMediaFolders.Images, folder, out var target) is { } folderError)
            return Error(StatusCodes.Status400BadRequest, ImageEditErrorCodes.InvalidRequest, folderError);
        // Нет папки (ещё не сохраняли): имя свободно, настоящая проверка — при сохранении
        if (scope.Project is not { } project || ProjectLinkGuard.ResolveInside(project.RootPath, target) is not { } full
            || !Directory.Exists(full))
            return Ok(new SaveCheckResponse($"{target}/{(string.IsNullOrWhiteSpace(name) ? "image" : name.Trim())}{ext}", false, null));
        return Map(saver.Check(project.RootPath, target, name, ext), Ok);
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

    private bool Gate(string sessionId, [NotNullWhen(true)] out ImageEditScope? scope,
        [NotNullWhen(false)] out IActionResult? denied) =>
        gate.TryPersonalChat(UserId, sessionId, out scope, out denied);
}

// Нити картинок личного чата вне проекта: тело — ImageThreadEndpoints, взять в работу можно только
// черновик в корне (draftFolder: ""), нить по файлу и черновик в папке — 400.
[ProjectCapability(ProjectCapabilityArea.Platform)]
[ApiController]
[Authorize]
[Route("api/image-editor/chats/{sessionId}/threads")]
public class PersonalThreadsController(
    ImageEditScopeGate gate,
    ImageThreadService threads,
    IImageEditJobs? jobs = null) : ImageThreadEndpoints(threads, jobs)
{
    [HttpGet]
    public IActionResult Get(string sessionId) =>
        Gate(sessionId, out var scope, out var denied) ? GetIn(scope, sessionId) : denied;

    [HttpPost]
    public async Task<IActionResult> Open(string sessionId, [FromBody] ImageThreadOpenRequest req, CancellationToken ct) =>
        Gate(sessionId, out var scope, out var denied) ? await OpenIn(scope, sessionId, req, ct) : denied;

    [HttpPut("focus")]
    public async Task<IActionResult> PutFocus(string sessionId, [FromBody] ImageThreadFocusRequest req) =>
        Gate(sessionId, out var scope, out var denied) ? await FocusIn(scope, sessionId, req) : denied;

    [HttpDelete("{threadId}")]
    public async Task<IActionResult> Remove(string sessionId, string threadId, [FromQuery] long revision) =>
        Gate(sessionId, out var scope, out var denied) ? await RemoveIn(scope, sessionId, threadId, revision) : denied;

    [HttpPut("{threadId}/current")]
    public async Task<IActionResult> Continue(string sessionId, string threadId, [FromBody] ImageThreadContinueRequest req) =>
        Gate(sessionId, out var scope, out var denied) ? await ContinueIn(scope, sessionId, threadId, req) : denied;

    [HttpPost("{threadId}/steps")]
    public async Task<IActionResult> AddStep(string sessionId, string threadId, [FromBody] ImageThreadStepRequest req) =>
        Gate(sessionId, out var scope, out var denied) ? await AddStepIn(scope, sessionId, threadId, req) : denied;

    [HttpPost("{threadId}/crop")]
    public async Task<IActionResult> Crop(string sessionId, string threadId, [FromBody] ImageThreadCropRequest req,
        CancellationToken ct) =>
        Gate(sessionId, out var scope, out var denied) ? await CropIn(scope, sessionId, threadId, req, ct) : denied;

    [HttpPost("{threadId}/take")]
    public async Task<IActionResult> Take(string sessionId, string threadId, [FromBody] ImageThreadTakeRequest req,
        CancellationToken ct) =>
        Gate(sessionId, out var scope, out var denied) ? await TakeIn(scope, sessionId, threadId, req, ct) : denied;

    [HttpPost("{threadId}/dismiss")]
    public async Task<IActionResult> Dismiss(string sessionId, string threadId, [FromBody] ImageThreadDismissRequest req) =>
        Gate(sessionId, out var scope, out var denied) ? await DismissIn(scope, sessionId, threadId, req) : denied;

    [HttpPost("{threadId}/rollback")]
    public async Task<IActionResult> Rollback(string sessionId, string threadId, [FromBody] ImageThreadRollbackRequest req) =>
        Gate(sessionId, out var scope, out var denied) ? await RollbackIn(scope, sessionId, threadId, req) : denied;

    [HttpPut("{threadId}/settings")]
    public async Task<IActionResult> Settings(string sessionId, string threadId, [FromBody] ImageThreadSettingsRequest req) =>
        Gate(sessionId, out var scope, out var denied) ? await SettingsIn(scope, sessionId, threadId, req) : denied;

    private bool Gate(string sessionId, [NotNullWhen(true)] out ImageEditScope? scope,
        [NotNullWhen(false)] out IActionResult? denied) =>
        gate.TryPersonalChat(UserId, sessionId, out scope, out denied);
}
