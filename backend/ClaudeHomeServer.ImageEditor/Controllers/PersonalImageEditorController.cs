using System.Diagnostics.CodeAnalysis;
using ClaudeHomeServer.Services.Composition;
using ClaudeHomeServer.Services.ImageEditor.ChatContext;
using ClaudeHomeServer.Services.ImageEditor.Prefs;
using ClaudeHomeServer.Services.ImageEditor.Threads;
using ClaudeHomeServer.Services.Images.Editing.Raster;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ClaudeHomeServer.Services.ImageEditor.Controllers;

// Редактор картинок в личном чате вне проекта (разрез
// docs/research/image-editor-personal-chats-cut-2026-09.md, «Бэкенд» п.3). Тело ручек — общее с
// проектом (ImageEditorEndpoints, ImageThreadEndpoints), здесь только личный гейт и выбор человека.
// save, save/check и characters* здесь нет намеренно: записи в проект и персонажей у личного чата
// нет по построению, эти пути — 404 маршрутизации, а не 500.
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
    ImageContextLaunch? context = null)
    : ImageEditorEndpoints(editors, launcher, placeSettings, jobs, steps, config, raster, context)
{
    [HttpGet("catalog")]
    public IActionResult Catalog(string sessionId) =>
        Gate(sessionId, out _, out var denied) ? CatalogOf() : denied;

    [HttpPost("quote")]
    public async Task<IActionResult> Quote(string sessionId, [FromBody] ImageEditQuoteRequest req, CancellationToken ct) =>
        Gate(sessionId, out var scope, out var denied) ? await QuoteIn(scope, req, ct) : denied;

    [HttpPost("jobs")]
    [RequestSizeLimit(MaxJobBodyBytes)]
    [RequestFormLimits(MultipartBodyLengthLimit = MaxJobBodyBytes)]
    public async Task<IActionResult> Start(string sessionId, [FromForm] StartJobForm form, CancellationToken ct) =>
        Gate(sessionId, out var scope, out var denied) ? await StartIn(scope, form, ct) : denied;

    [HttpPost("uploads")]
    [RequestSizeLimit(MaxUploadBodyBytes)]
    [RequestFormLimits(MultipartBodyLengthLimit = MaxUploadBodyBytes)]
    public async Task<IActionResult> Upload(string sessionId, IFormFile? file, CancellationToken ct) =>
        Gate(sessionId, out _, out var denied) ? await UploadIn(file, ct) : denied;

    [HttpGet("jobs/{jobId}")]
    public IActionResult GetJob(string sessionId, string jobId) =>
        Gate(sessionId, out var scope, out var denied) ? JobIn(scope, jobId) : denied;

    [HttpDelete("jobs/{jobId}")]
    public async Task<IActionResult> Cancel(string sessionId, string jobId, CancellationToken ct) =>
        Gate(sessionId, out var scope, out var denied) ? await CancelIn(scope, jobId, ct) : denied;

    [HttpGet("jobs/{jobId}/variants/{variant:int}")]
    public IActionResult Variant(string sessionId, string jobId, int variant) =>
        Gate(sessionId, out var scope, out var denied) ? VariantIn(scope, jobId, variant) : denied;

    // Только от шага (Base.StepId): Base.Path — 400, файлов проекта у личного чата нет
    [HttpPost("transform")]
    public async Task<IActionResult> Transform(string sessionId, [FromBody] ImageTransformRequest req,
        [FromQuery] bool dryRun, CancellationToken ct) =>
        Gate(sessionId, out var scope, out var denied) ? await TransformIn(scope, req, dryRun, ct) : denied;

    [HttpGet("steps/{stepId}")]
    public IActionResult Step(string sessionId, string stepId) =>
        Gate(sessionId, out var scope, out var denied) ? StepIn(scope, stepId) : denied;

    // Выбор человека личной области — один на владельца, как и сама область
    [HttpGet("prefs")]
    public IActionResult GetPrefs(string sessionId) =>
        Gate(sessionId, out var scope, out var denied) ? Ok(prefs.Get(UserId, scope)) : denied;

    // characterSlug принудительно null: персонажей у личной области нет
    [HttpPut("prefs")]
    public async Task<IActionResult> PutPrefs(string sessionId, [FromBody] ImageProjectPrefs? req)
    {
        if (!Gate(sessionId, out var scope, out var denied)) return denied;
        if (ImageProjectPrefsService.Validate(req is null ? null : req with { CharacterSlug = null }) is { } error)
            return Error(StatusCodes.Status400BadRequest, ImageEditErrorCodes.InvalidRequest, error);
        return Ok(await prefs.SetAsync(UserId, scope, req!));
    }

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
        Gate(sessionId, out _, out var denied) ? GetIn(sessionId) : denied;

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
