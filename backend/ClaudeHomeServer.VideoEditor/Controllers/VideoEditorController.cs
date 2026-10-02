using System.Diagnostics.CodeAnalysis;
using ClaudeHomeServer.Services.Composition;
using ClaudeHomeServer.Services.VideoEditor.Contracts;
using ClaudeHomeServer.Services.VideoEditor.Jobs;
using ClaudeHomeServer.Services.VideoEditor.Prefs;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ClaudeHomeServer.Services.VideoEditor.Controllers;

// Модуль «Видео» в проекте (ADR-022 §2). Тело ручек, общее с личным чатом, — в VideoEditorEndpoints; здесь
// проектный гейт (флаг и чужой проект — 404, чат другого проекта — 404 chat_not_found). Локальный проект
// (ADR-016) отказывает до тела ручки через ProjectCapability: файлы на устройстве, сервер их не трогает.
// Ручки фильмов и сохранения сцены в проект добавляет блок 2.
[ProjectCapability(ProjectCapabilityArea.FileBound)]
[ApiController]
[Authorize]
[Route(VideoEditorRoutes.ProjectBase)]
public class VideoEditorController(
    VideoEditScopeGate gate,
    IEnumerable<IVideoEngine> engines,
    VideoEditJobService jobs,
    VideoJobThreads threads,
    VideoPrefsService prefs,
    VideoEditWorkspace workspace)
    : VideoEditorEndpoints(engines, jobs, threads, prefs, workspace)
{
    [HttpGet(VideoEditorRoutes.Catalog)]
    public IActionResult Catalog(string projectId) =>
        Gate(projectId, out var scope, out var denied) ? CatalogIn(scope) : denied;

    [HttpGet(VideoEditorRoutes.Prefs)]
    public IActionResult GetPrefs(string projectId) =>
        Gate(projectId, out var scope, out var denied) ? PrefsIn(scope) : denied;

    [HttpPut(VideoEditorRoutes.Prefs)]
    public IActionResult PutPrefs(string projectId, [FromBody] VideoPrefsDto? req) =>
        Gate(projectId, out var scope, out var denied) ? PutPrefsIn(scope, req) : denied;

    [HttpPost(VideoEditorRoutes.Quote)]
    public async Task<IActionResult> Quote(string projectId, [FromBody] VideoQuoteRequest? req, CancellationToken ct) =>
        Gate(projectId, out var scope, out var denied) ? await QuoteIn(scope, req, ct) : denied;

    [HttpPost(VideoEditorRoutes.Jobs)]
    public async Task<IActionResult> Start(string projectId, [FromBody] VideoLaunchRequest? req, CancellationToken ct) =>
        Gate(projectId, out var scope, out var denied) ? await StartIn(scope, req, ct) : denied;

    [HttpGet(VideoEditorRoutes.Job)]
    public IActionResult GetJob(string projectId, string jobId) =>
        Gate(projectId, out var scope, out var denied) ? JobIn(scope, jobId) : denied;

    [HttpDelete(VideoEditorRoutes.Job)]
    public async Task<IActionResult> Cancel(string projectId, string jobId, CancellationToken ct) =>
        Gate(projectId, out var scope, out var denied) ? await CancelIn(scope, jobId, ct) : denied;

    // ── Чат проекта: состояние, сцены, файлы версий ───────────────────────────────

    [HttpGet("sessions/{sessionId}/" + VideoEditorRoutes.State)]
    public IActionResult State(string projectId, string sessionId) =>
        ChatGate(projectId, sessionId, out var scope, out var denied) ? StateIn(scope, sessionId) : denied;

    [HttpGet("sessions/{sessionId}/" + VideoEditorRoutes.Scenes)]
    public IActionResult Scenes(string projectId, string sessionId) =>
        ChatGate(projectId, sessionId, out _, out var denied) ? ScenesIn(sessionId) : denied;

    [HttpPost("sessions/{sessionId}/" + VideoEditorRoutes.Scenes)]
    public async Task<IActionResult> AddScene(string projectId, string sessionId, [FromBody] VideoSceneCreateRequest? req,
        CancellationToken ct) =>
        ChatGate(projectId, sessionId, out var scope, out var denied) ? await AddSceneIn(scope, sessionId, req, ct) : denied;

    [HttpPut("sessions/{sessionId}/" + VideoEditorRoutes.ScenesFocus)]
    public async Task<IActionResult> Focus(string projectId, string sessionId, [FromBody] VideoSceneFocusRequest? req) =>
        ChatGate(projectId, sessionId, out var scope, out var denied) ? await FocusIn(scope, sessionId, req) : denied;

    [HttpDelete("sessions/{sessionId}/" + VideoEditorRoutes.Scene)]
    public async Task<IActionResult> Remove(string projectId, string sessionId, string sceneId, [FromQuery] long revision) =>
        ChatGate(projectId, sessionId, out var scope, out var denied) ? await RemoveIn(scope, sessionId, sceneId, revision) : denied;

    [HttpPut("sessions/{sessionId}/" + VideoEditorRoutes.SceneSettings)]
    public async Task<IActionResult> Settings(string projectId, string sessionId, string sceneId,
        [FromBody] VideoSceneSettingsRequest? req) =>
        ChatGate(projectId, sessionId, out var scope, out var denied) ? await SettingsIn(scope, sessionId, sceneId, req) : denied;

    [HttpPut("sessions/{sessionId}/" + VideoEditorRoutes.SceneCurrent)]
    public async Task<IActionResult> Current(string projectId, string sessionId, string sceneId,
        [FromBody] VideoSceneCurrentRequest? req) =>
        ChatGate(projectId, sessionId, out var scope, out var denied) ? await CurrentIn(scope, sessionId, sceneId, req) : denied;

    [HttpGet("sessions/{sessionId}/" + VideoEditorRoutes.SceneVersionFile)]
    public IActionResult VersionFile(string projectId, string sessionId, string sceneId, string versionId,
        [FromQuery] bool download) =>
        ChatGate(projectId, sessionId, out _, out var denied) ? VersionFileIn(sessionId, sceneId, versionId, download) : denied;

    private bool Gate(string projectId, [NotNullWhen(true)] out VideoEditScope? scope,
        [NotNullWhen(false)] out IActionResult? denied) =>
        gate.TryProject(UserId, projectId, out scope, out denied);

    private bool ChatGate(string projectId, string sessionId, [NotNullWhen(true)] out VideoEditScope? scope,
        [NotNullWhen(false)] out IActionResult? denied) =>
        gate.TryProjectChat(UserId, projectId, sessionId, out scope, out denied);
}
