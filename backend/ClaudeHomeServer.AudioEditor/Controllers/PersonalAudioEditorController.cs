using ClaudeHomeServer.Services.Media;
using System.Diagnostics.CodeAnalysis;
using ClaudeHomeServer.Services.AudioEditor.Jobs;
using ClaudeHomeServer.Services.AudioEditor.Prefs;
using ClaudeHomeServer.Services.Composition;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ClaudeHomeServer.Services.AudioEditor.Controllers;

// Модуль «Звук» в личном чате вне проекта. Тело ручек — общее с проектом (AudioEditorEndpoints), здесь
// только личный гейт. save пишет в «Личное» (Personal/audio/, ADR-040 §5); версию по-прежнему можно скачать
// (files/{role}?download=true). Префы — одни на владельца, как и сама личная область.
[ProjectCapability(ProjectCapabilityArea.Platform)]
[ApiController]
[Authorize]
[Route("api/audio-editor/chats/{sessionId}")]
public class PersonalAudioEditorController(
    AudioEditScopeGate gate,
    IEnumerable<IAudioEngine> engines,
    AudioEditJobService jobs,
    AudioJobThreads threads,
    AudioPrefsService prefs,
    AudioEditWorkspace workspace,
    Engines.DspAudioEngine dsp,
    AudioConcatService concat,
    ChatContext.AudioContextLaunch? context = null,
    IVideoSceneSound? videoScenes = null)
    : AudioEditorEndpoints(engines, jobs, threads, prefs, workspace, dsp, concat, context, videoScenes)
{
    [HttpGet("state")]
    public IActionResult State(string sessionId) =>
        Gate(sessionId, out var scope, out var denied) ? StateIn(scope, sessionId) : denied;

    [HttpGet("catalog")]
    public IActionResult Catalog(string sessionId) =>
        Gate(sessionId, out var scope, out var denied) ? CatalogIn(scope) : denied;

    // Библиотека «Голоса» — PersonalAudioVoicesController (voices/ в папке «Личного»)

    [HttpGet("prefs")]
    public IActionResult GetPrefs(string sessionId) =>
        Gate(sessionId, out var scope, out var denied) ? PrefsIn(scope) : denied;

    [HttpPut("prefs/{mode}")]
    public async Task<IActionResult> PutPrefs(string sessionId, string mode, [FromBody] AudioModePrefs? req) =>
        Gate(sessionId, out var scope, out var denied) ? await PutPrefsIn(scope, mode, req) : denied;

    [HttpPost("quote")]
    public async Task<IActionResult> Quote(string sessionId, [FromBody] AudioQuoteRequest? req, CancellationToken ct) =>
        Gate(sessionId, out var scope, out var denied) ? await QuoteIn(scope, req, ct) : denied;

    [HttpPost("jobs")]
    [RequestSizeLimit(MaxJobBodyBytes)]
    [RequestFormLimits(MultipartBodyLengthLimit = MaxJobBodyBytes)]
    public async Task<IActionResult> Start(string sessionId, [FromForm] AudioStartJobForm form, CancellationToken ct)
    {
        if (!Gate(sessionId, out var scope, out var denied)) return denied;
        // Входы из файлов «Личного» — вход, которому нужен диск: папку создаём здесь, а не на GET
        if (!string.IsNullOrWhiteSpace(form.ReferencePath) || form.ClipPaths is { Count: > 0 }
            || !string.IsNullOrWhiteSpace(form.VoiceModelPath) || !string.IsNullOrWhiteSpace(form.VoiceIndexPath)
            || !string.IsNullOrWhiteSpace(form.VideoPath))
            scope = await gate.WithFolderAsync(scope, ct);
        return await StartIn(scope, form, ct);
    }

    [HttpGet("jobs/{jobId}")]
    public IActionResult GetJob(string sessionId, string jobId) =>
        Gate(sessionId, out var scope, out var denied) ? JobIn(scope, jobId) : denied;

    [HttpDelete("jobs/{jobId}")]
    public async Task<IActionResult> Cancel(string sessionId, string jobId, CancellationToken ct) =>
        Gate(sessionId, out var scope, out var denied) ? await CancelIn(scope, jobId, ct) : denied;

    [HttpGet("threads")]
    public IActionResult Threads(string sessionId) =>
        Gate(sessionId, out var scope, out var denied) ? ThreadsIn(scope, sessionId) : denied;

    // Только черновик в корне: файл и папка черновика — 400, файлов проекта у личного чата нет
    [HttpPost("threads")]
    public async Task<IActionResult> Open(string sessionId, [FromBody] AudioThreadOpenRequest req, CancellationToken ct) =>
        Gate(sessionId, out var scope, out var denied) ? await OpenIn(scope, sessionId, req, ct) : denied;

    [HttpPut("threads/focus")]
    public async Task<IActionResult> Focus(string sessionId, [FromBody] AudioThreadFocusRequest req) =>
        Gate(sessionId, out var scope, out var denied) ? await FocusIn(scope, sessionId, req) : denied;

    [HttpDelete("threads/{threadId}")]
    public async Task<IActionResult> Remove(string sessionId, string threadId, [FromQuery] long revision) =>
        Gate(sessionId, out var scope, out var denied) ? await RemoveIn(scope, sessionId, threadId, revision) : denied;

    [HttpPut("threads/{threadId}/settings")]
    public async Task<IActionResult> Settings(string sessionId, string threadId, [FromBody] AudioThreadSettingsRequest req) =>
        Gate(sessionId, out var scope, out var denied) ? await SettingsIn(scope, sessionId, threadId, req) : denied;

    [HttpPut("threads/{threadId}/current")]
    public async Task<IActionResult> Current(string sessionId, string threadId, [FromBody] AudioThreadCurrentRequest req) =>
        Gate(sessionId, out var scope, out var denied) ? await CurrentIn(scope, sessionId, threadId, req) : denied;

    [HttpGet("threads/{threadId}/versions/{versionId}/files/{role}")]
    public IActionResult VersionFile(string sessionId, string threadId, string versionId, string role,
        [FromQuery] bool download) =>
        Gate(sessionId, out var scope, out var denied) ? VersionFileIn(scope, sessionId, threadId, versionId, role, download) : denied;

    [HttpPost("threads/{threadId}/edit")]
    public async Task<IActionResult> Edit(string sessionId, string threadId, [FromBody] AudioDspEditRequest? req,
        CancellationToken ct) =>
        Gate(sessionId, out var scope, out var denied) ? await EditIn(scope, sessionId, threadId, req, ct) : denied;

    [HttpPost("threads/{threadId}/mix")]
    public async Task<IActionResult> Mix(string sessionId, string threadId, [FromBody] AudioMixRequest? req,
        CancellationToken ct) =>
        Gate(sessionId, out var scope, out var denied) ? await MixIn(scope, sessionId, threadId, req, ct) : denied;

    [HttpGet("threads/{threadId}/versions/{versionId}/peaks")]
    public async Task<IActionResult> Peaks(string sessionId, string threadId, string versionId,
        [FromQuery] int points = 800, [FromQuery] string? role = null, CancellationToken ct = default) =>
        Gate(sessionId, out var scope, out var denied) ? await PeaksIn(scope, sessionId, threadId, versionId, role, points, ct) : denied;

    // Куски — версии нитей этого чата и файлы «Личного»
    [HttpPost("concat")]
    public async Task<IActionResult> Concat(string sessionId, [FromBody] AudioConcatRequest? req, CancellationToken ct)
    {
        if (!Gate(sessionId, out var scope, out var denied)) return denied;
        if (req?.Pieces?.Any(p => !string.IsNullOrWhiteSpace(p.ProjectFile)) == true)
            scope = await gate.WithFolderAsync(scope, ct);
        return await ConcatIn(scope, sessionId, req, ct);
    }

    // Сохранить версию в «Личное»: Personal/audio/, только новыми файлами. Работает и для старых личных версий:
    // файлы версии копируются из рабочей папки
    [HttpPost("threads/{threadId}/save")]
    public async Task<IActionResult> Save(string sessionId, string threadId, [FromBody] AudioSaveRequest req, CancellationToken ct)
    {
        if (!Gate(sessionId, out var scope, out var denied)) return denied;
        scope = await gate.WithFolderAsync(scope, ct);
        return await SaveIn(scope, sessionId, threadId, req);
    }

    private bool Gate(string sessionId, [NotNullWhen(true)] out AudioEditScope? scope,
        [NotNullWhen(false)] out IActionResult? denied) =>
        gate.TryPersonalChat(UserId, sessionId, out scope, out denied);
}
