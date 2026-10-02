using System.Diagnostics.CodeAnalysis;
using ClaudeHomeServer.Services.Composition;
using ClaudeHomeServer.Services.VideoEditor.Contracts;
using ClaudeHomeServer.Services.VideoEditor.Jobs;
using ClaudeHomeServer.Services.VideoEditor.Prefs;
using ClaudeHomeServer.Services.VideoEditor.Scenes;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ClaudeHomeServer.Services.VideoEditor.Controllers;

// Модуль «Видео» в личном чате вне проекта. Тело ручек — общее с проектом (VideoEditorEndpoints), здесь
// только личный гейт. Фильмов и сохранения в проект здесь нет намеренно: записи в проект у личного чата
// нет по построению, такие пути — 404 маршрутизации, а не 500 (в проектах они отвечают
// personal_scope_no_films только у агента). Local закрыт в v1: отказ области — local_unavailable_personal.
// Префы — одни на владельца, как и сама личная область.
[ProjectCapability(ProjectCapabilityArea.Platform)]
[ApiController]
[Authorize]
[Route(VideoEditorRoutes.PersonalBase)]
public class PersonalVideoEditorController(
    VideoEditScopeGate gate,
    IEnumerable<IVideoEngine> engines,
    VideoEditJobService jobs,
    VideoJobThreads threads,
    VideoPrefsService prefs,
    VideoEditWorkspace workspace,
    VideoSceneService scenes)
    : VideoEditorEndpoints(engines, jobs, threads, prefs, workspace, scenes)
{
    [HttpGet(VideoEditorRoutes.State)]
    public IActionResult State(string sessionId) =>
        Gate(sessionId, out var scope, out var denied) ? StateIn(scope, sessionId) : denied;

    [HttpGet(VideoEditorRoutes.Catalog)]
    public IActionResult Catalog(string sessionId) =>
        Gate(sessionId, out var scope, out var denied) ? CatalogIn(scope) : denied;

    [HttpGet(VideoEditorRoutes.Prefs)]
    public IActionResult GetPrefs(string sessionId) =>
        Gate(sessionId, out var scope, out var denied) ? PrefsIn(scope) : denied;

    [HttpPut(VideoEditorRoutes.Prefs)]
    public IActionResult PutPrefs(string sessionId, [FromBody] VideoPrefsDto? req) =>
        Gate(sessionId, out var scope, out var denied) ? PutPrefsIn(scope, req) : denied;

    [HttpPost(VideoEditorRoutes.Quote)]
    public async Task<IActionResult> Quote(string sessionId, [FromBody] VideoQuoteRequest? req, CancellationToken ct) =>
        Gate(sessionId, out var scope, out var denied) ? await QuoteIn(scope, req, ct) : denied;

    [HttpPost(VideoEditorRoutes.Jobs)]
    public async Task<IActionResult> Start(string sessionId, [FromBody] VideoLaunchRequest? req, CancellationToken ct) =>
        Gate(sessionId, out var scope, out var denied) ? await StartIn(scope, req, ct) : denied;

    [HttpGet(VideoEditorRoutes.Job)]
    public IActionResult GetJob(string sessionId, string jobId) =>
        Gate(sessionId, out var scope, out var denied) ? JobIn(scope, jobId) : denied;

    [HttpDelete(VideoEditorRoutes.Job)]
    public async Task<IActionResult> Cancel(string sessionId, string jobId, CancellationToken ct) =>
        Gate(sessionId, out var scope, out var denied) ? await CancelIn(scope, jobId, ct) : denied;

    [HttpGet(VideoEditorRoutes.Scenes)]
    public IActionResult Scenes(string sessionId) =>
        Gate(sessionId, out _, out var denied) ? ScenesIn(sessionId) : denied;

    // Только сцена без папки: папок проекта у личного чата нет
    [HttpPost(VideoEditorRoutes.Scenes)]
    public async Task<IActionResult> AddScene(string sessionId, [FromBody] VideoSceneCreateRequest? req, CancellationToken ct) =>
        Gate(sessionId, out var scope, out var denied) ? await AddSceneIn(scope, sessionId, req, ct) : denied;

    [HttpPut(VideoEditorRoutes.ScenesFocus)]
    public async Task<IActionResult> Focus(string sessionId, [FromBody] VideoSceneFocusRequest? req) =>
        Gate(sessionId, out var scope, out var denied) ? await FocusIn(scope, sessionId, req) : denied;

    [HttpDelete(VideoEditorRoutes.Scene)]
    public async Task<IActionResult> Remove(string sessionId, string sceneId, [FromQuery] long revision) =>
        Gate(sessionId, out var scope, out var denied) ? await RemoveIn(scope, sessionId, sceneId, revision) : denied;

    [HttpPut(VideoEditorRoutes.SceneSettings)]
    public async Task<IActionResult> Settings(string sessionId, string sceneId, [FromBody] VideoSceneSettingsRequest? req) =>
        Gate(sessionId, out var scope, out var denied) ? await SettingsIn(scope, sessionId, sceneId, req) : denied;

    [HttpPut(VideoEditorRoutes.SceneCurrent)]
    public async Task<IActionResult> Current(string sessionId, string sceneId, [FromBody] VideoSceneCurrentRequest? req) =>
        Gate(sessionId, out var scope, out var denied) ? await CurrentIn(scope, sessionId, sceneId, req) : denied;

    [HttpGet(VideoEditorRoutes.SceneVersionFile)]
    public IActionResult VersionFile(string sessionId, string sceneId, string versionId, [FromQuery] bool download) =>
        Gate(sessionId, out _, out var denied) ? VersionFileIn(sessionId, sceneId, versionId, download) : denied;

    private bool Gate(string sessionId, [NotNullWhen(true)] out VideoEditScope? scope,
        [NotNullWhen(false)] out IActionResult? denied) =>
        gate.TryPersonalChat(UserId, sessionId, out scope, out denied);
}
