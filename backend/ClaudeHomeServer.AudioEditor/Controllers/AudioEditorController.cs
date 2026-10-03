using System.Diagnostics.CodeAnalysis;
using ClaudeHomeServer.Services.AudioEditor.Jobs;
using ClaudeHomeServer.Services.AudioEditor.Prefs;
using ClaudeHomeServer.Services.AudioEditor.Threads;
using ClaudeHomeServer.Services.Composition;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ClaudeHomeServer.Services.AudioEditor.Controllers;

// Модуль «Звук» в проекте (ADR-021 §2). Тело ручек, общее с личным чатом, — в AudioEditorEndpoints;
// здесь проектный гейт (флаг и чужой проект — 404, чат другого проекта — 404 chat_not_found) и то,
// что есть только у проекта: сохранение версии в проект.
[ProjectCapability(ProjectCapabilityArea.FileBound)]
[ApiController]
[Authorize]
[Route("api/projects/{projectId}/audio-editor")]
public class AudioEditorController(
    AudioEditScopeGate gate,
    IEnumerable<IAudioEngine> engines,
    AudioEditJobService jobs,
    AudioJobThreads threads,
    AudioPrefsService prefs,
    AudioEditWorkspace workspace,
    Engines.DspAudioEngine dsp,
    AudioConcatService concat,
    IProjectFiles? files = null,
    ChatContext.AudioContextLaunch? context = null)
    : AudioEditorEndpoints(engines, jobs, threads, prefs, workspace, dsp, concat, context)
{
    private readonly AudioJobThreads _threads = threads;
    private readonly AudioEditWorkspace _workspace = workspace;

    [HttpGet("catalog")]
    public IActionResult Catalog(string projectId) =>
        Gate(projectId, out var scope, out var denied) ? CatalogIn(scope) : denied;

    [HttpGet("prefs")]
    public IActionResult GetPrefs(string projectId) =>
        Gate(projectId, out var scope, out var denied) ? PrefsIn(scope) : denied;

    [HttpPut("prefs/{mode}")]
    public async Task<IActionResult> PutPrefs(string projectId, string mode, [FromBody] AudioModePrefs? req) =>
        Gate(projectId, out var scope, out var denied) ? await PutPrefsIn(scope, mode, req) : denied;

    [HttpPost("quote")]
    public async Task<IActionResult> Quote(string projectId, [FromBody] AudioQuoteRequest? req, CancellationToken ct) =>
        Gate(projectId, out var scope, out var denied) ? await QuoteIn(scope, req, ct) : denied;

    [HttpPost("jobs")]
    [RequestSizeLimit(MaxJobBodyBytes)]
    [RequestFormLimits(MultipartBodyLengthLimit = MaxJobBodyBytes)]
    public async Task<IActionResult> Start(string projectId, [FromForm] AudioStartJobForm form, CancellationToken ct) =>
        Gate(projectId, out var scope, out var denied) ? await StartIn(scope, form, ct) : denied;

    [HttpGet("jobs/{jobId}")]
    public IActionResult GetJob(string projectId, string jobId) =>
        Gate(projectId, out var scope, out var denied) ? JobIn(scope, jobId) : denied;

    [HttpDelete("jobs/{jobId}")]
    public async Task<IActionResult> Cancel(string projectId, string jobId, CancellationToken ct) =>
        Gate(projectId, out var scope, out var denied) ? await CancelIn(scope, jobId, ct) : denied;

    // ── Чат проекта: состояние, нити, файлы версий, сохранение ─────────────────────

    [HttpGet("sessions/{sessionId}/state")]
    public IActionResult State(string projectId, string sessionId) =>
        ChatGate(projectId, sessionId, out var scope, out var denied) ? StateIn(scope, sessionId) : denied;

    [HttpGet("sessions/{sessionId}/threads")]
    public IActionResult Threads(string projectId, string sessionId) =>
        ChatGate(projectId, sessionId, out _, out var denied) ? ThreadsIn(sessionId) : denied;

    [HttpPost("sessions/{sessionId}/threads")]
    public async Task<IActionResult> Open(string projectId, string sessionId, [FromBody] AudioThreadOpenRequest req,
        CancellationToken ct) =>
        ChatGate(projectId, sessionId, out var scope, out var denied) ? await OpenIn(scope, sessionId, req, ct) : denied;

    [HttpPut("sessions/{sessionId}/threads/focus")]
    public async Task<IActionResult> Focus(string projectId, string sessionId, [FromBody] AudioThreadFocusRequest req) =>
        ChatGate(projectId, sessionId, out var scope, out var denied) ? await FocusIn(scope, sessionId, req) : denied;

    [HttpDelete("sessions/{sessionId}/threads/{threadId}")]
    public async Task<IActionResult> Remove(string projectId, string sessionId, string threadId, [FromQuery] long revision) =>
        ChatGate(projectId, sessionId, out var scope, out var denied) ? await RemoveIn(scope, sessionId, threadId, revision) : denied;

    [HttpPut("sessions/{sessionId}/threads/{threadId}/settings")]
    public async Task<IActionResult> Settings(string projectId, string sessionId, string threadId,
        [FromBody] AudioThreadSettingsRequest req) =>
        ChatGate(projectId, sessionId, out var scope, out var denied) ? await SettingsIn(scope, sessionId, threadId, req) : denied;

    [HttpPut("sessions/{sessionId}/threads/{threadId}/current")]
    public async Task<IActionResult> Current(string projectId, string sessionId, string threadId,
        [FromBody] AudioThreadCurrentRequest req) =>
        ChatGate(projectId, sessionId, out var scope, out var denied) ? await CurrentIn(scope, sessionId, threadId, req) : denied;

    [HttpGet("sessions/{sessionId}/threads/{threadId}/versions/{versionId}/files/{role}")]
    public IActionResult VersionFile(string projectId, string sessionId, string threadId, string versionId, string role,
        [FromQuery] bool download) =>
        ChatGate(projectId, sessionId, out var scope, out var denied)
            ? VersionFileIn(scope, sessionId, threadId, versionId, role, download)
            : denied;

    [HttpPost("sessions/{sessionId}/threads/{threadId}/edit")]
    public async Task<IActionResult> Edit(string projectId, string sessionId, string threadId,
        [FromBody] AudioDspEditRequest? req, CancellationToken ct) =>
        ChatGate(projectId, sessionId, out var scope, out var denied) ? await EditIn(scope, sessionId, threadId, req, ct) : denied;

    [HttpPost("sessions/{sessionId}/threads/{threadId}/mix")]
    public async Task<IActionResult> Mix(string projectId, string sessionId, string threadId,
        [FromBody] AudioMixRequest? req, CancellationToken ct) =>
        ChatGate(projectId, sessionId, out var scope, out var denied) ? await MixIn(scope, sessionId, threadId, req, ct) : denied;

    [HttpGet("sessions/{sessionId}/threads/{threadId}/versions/{versionId}/peaks")]
    public async Task<IActionResult> Peaks(string projectId, string sessionId, string threadId, string versionId,
        [FromQuery] int points = 800, [FromQuery] string? role = null, CancellationToken ct = default) =>
        ChatGate(projectId, sessionId, out var scope, out var denied)
            ? await PeaksIn(scope, sessionId, threadId, versionId, role, points, ct)
            : denied;

    [HttpPost("sessions/{sessionId}/concat")]
    public async Task<IActionResult> Concat(string projectId, string sessionId, [FromBody] AudioConcatRequest? req,
        CancellationToken ct) =>
        ChatGate(projectId, sessionId, out var scope, out var denied) ? await ConcatIn(scope, sessionId, req, ct) : denied;

    // Сохранить версию в проект: следующая версия рядом с исходником или «Сохранить как». Только
    // человек и только новыми файлами; занятое имя «Сохранить как» — 409 name_taken с подсказкой
    [HttpPost("sessions/{sessionId}/threads/{threadId}/save")]
    public async Task<IActionResult> Save(string projectId, string sessionId, string threadId, [FromBody] AudioSaveRequest req)
    {
        if (!ChatGate(projectId, sessionId, out var scope, out var denied)) return denied;
        var project = scope.Project!;
        var mode = string.IsNullOrWhiteSpace(req.Mode) ? AudioProjectSaver.NextVersion : req.Mode.Trim();
        if (mode is not (AudioProjectSaver.NextVersion or AudioProjectSaver.As))
            return Error(StatusCodes.Status400BadRequest, AudioEditErrorCodes.InvalidRequest, $"Неизвестный режим сохранения «{mode}»");

        var thread = _threads.Store.Get(UserId, sessionId).Threads.FirstOrDefault(t => t.Id == threadId);
        if (thread is null) return ThreadNotFound();
        var version = string.IsNullOrWhiteSpace(req.VersionId) ? thread.CurrentVersion : thread.Version(req.VersionId.Trim());
        if (version is null) return VersionNotFound();
        if (version.IsOrigin)
            return Error(StatusCodes.Status400BadRequest, AudioEditErrorCodes.InvalidRequest, "Исходник уже лежит в проекте");

        var items = new List<AudioProjectSaver.Item>();
        foreach (var file in version.Files)
        {
            if (AudioVersionFiles.Resolve(_workspace, UserId, scope, version, file) is not { } path || !System.IO.File.Exists(path))
                return Error(StatusCodes.Status404NotFound, AudioEditErrorCodes.FileNotFound,
                    "Файлы версии больше не найдены — рабочая папка очищена");
            items.Add(new AudioProjectSaver.Item(file.Role, path));
        }

        var saved = AudioProjectSaver.Save(project.RootPath, thread, items, mode, req.Folder, req.FileName);
        if (saved.ErrorCode == AudioEditErrorCodes.NameTaken)
            return StatusCode(StatusCodes.Status409Conflict, new { error = saved.Error, code = saved.ErrorCode, suggestion = saved.Suggestion });
        if (saved.Value is not { } result)
            return Error(saved.ErrorCode == AudioEditErrorCodes.FileNotFound ? StatusCodes.Status404NotFound : StatusCodes.Status400BadRequest,
                saved.ErrorCode ?? AudioEditErrorCodes.InvalidRequest, saved.Error ?? "Не сохранено");
        // Новые файлы — обычная запись в проект: синк знаний и ватчеры узнают о них сразу
        foreach (var rel in result.Files) files?.NotifyMutated(project.RootPath, rel, FileMutationKind.Create);
        // Нить помнит, что звук уже в проекте; версия из одних стемов звука не даёт — нить остаётся при своём
        await _threads.OnSavedAsync(UserId, scope.Key, sessionId, threadId, result.Path,
            result.HasAudio ? null : result.Name);
        return Ok(result);
    }

    private bool Gate(string projectId, [NotNullWhen(true)] out AudioEditScope? scope,
        [NotNullWhen(false)] out IActionResult? denied) =>
        gate.TryProject(UserId, projectId, out scope, out denied);

    private bool ChatGate(string projectId, string sessionId, [NotNullWhen(true)] out AudioEditScope? scope,
        [NotNullWhen(false)] out IActionResult? denied) =>
        gate.TryProjectChat(UserId, projectId, sessionId, out scope, out denied);
}
