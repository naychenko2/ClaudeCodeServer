using System.Diagnostics.CodeAnalysis;
using ClaudeHomeServer.Services.Composition;
using ClaudeHomeServer.Services.VideoEditor.Assembly;
using ClaudeHomeServer.Services.VideoEditor.Contracts;
using ClaudeHomeServer.Services.VideoEditor.Films;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ClaudeHomeServer.Services.VideoEditor.Controllers;

// Фильмы и сохранение сцены в проект (ADR-022 §2, блок 2): проектные ручки; тело — в FilmEndpoints, общее с личными
// (PersonalFilmController). Гейт тот же, что у остальных ручек модуля: флаг, свой проект, свой чат — иначе 404.
// Локальный проект (ADR-016) отказывает до тела через ProjectCapability.
[ProjectCapability(ProjectCapabilityArea.FileBound)]
[ApiController]
[Authorize]
[Route(VideoEditorRoutes.ProjectBase)]
public class FilmController(
    VideoEditScopeGate gate,
    FilmService films,
    FilmSceneSaver saver,
    FilmAssembler assembler,
    FilmMusicComposer music,
    ChatContext.VideoContextLaunch? context = null) : FilmEndpoints(films, assembler, music, context)
{
    [HttpGet(VideoEditorRoutes.Films)]
    public IActionResult List(string projectId) =>
        Gate(projectId, out var scope, out var denied) ? ListIn(scope) : denied;

    [HttpGet(VideoEditorRoutes.FilmState)]
    public IActionResult State(string projectId, [FromQuery] string? path) =>
        Gate(projectId, out var scope, out var denied) ? StateIn(scope, path) : denied;

    [HttpPost(VideoEditorRoutes.FilmCreate)]
    public async Task<IActionResult> Create(string projectId, [FromBody] FilmCreateRequest? req, CancellationToken ct) =>
        Gate(projectId, out var scope, out var denied) ? await CreateIn(scope, req, ct) : denied;

    [HttpPatch(VideoEditorRoutes.FilmPatchRoute)]
    public async Task<IActionResult> Patch(string projectId, [FromQuery] string? path, [FromBody] FilmPatch? patch,
        CancellationToken ct, [FromQuery] string? sessionId = null) =>
        Gate(projectId, out var scope, out var denied) && gate.TryOptionalChat(UserId, projectId, sessionId, out denied)
            ? await PatchIn(scope, path, patch, sessionId, ct)
            : denied;

    [HttpPost(VideoEditorRoutes.FilmBuild)]
    public IActionResult Build(string projectId, [FromQuery] string? path, [FromQuery] string? sessionId = null,
        [FromQuery] long? contextRevision = null)
    {
        if (!Gate(projectId, out var scope, out var denied)) return denied;
        if (!gate.TryOptionalChat(UserId, projectId, sessionId, out denied)) return denied;
        return BuildIn(scope, path, sessionId, contextRevision);
    }

    [HttpGet(VideoEditorRoutes.FilmBuild)]
    public IActionResult BuildStatus(string projectId, [FromQuery] string? path) =>
        Gate(projectId, out var scope, out var denied) ? BuildStatusIn(scope, path) : denied;

    [HttpDelete(VideoEditorRoutes.FilmBuild)]
    public IActionResult CancelBuild(string projectId, [FromQuery] string? path) =>
        Gate(projectId, out var scope, out var denied) ? CancelBuildIn(scope, path) : denied;

    [HttpPost(VideoEditorRoutes.FilmMusic)]
    public async Task<IActionResult> ComposeMusic(string projectId, [FromQuery] string? path,
        [FromBody] FilmMusicRequest? req, CancellationToken ct) =>
        Gate(projectId, out var scope, out var denied) && gate.TryOptionalChat(UserId, projectId, req?.SessionId, out denied)
            ? await ComposeMusicIn(scope, path, req, ct)
            : denied;

    [HttpPost("sessions/{sessionId}/" + VideoEditorRoutes.SceneSave)]
    public async Task<IActionResult> SaveScene(string projectId, string sessionId, string sceneId,
        [FromBody] SaveSceneRequest? req, CancellationToken ct)
    {
        if (!gate.TryProjectChat(UserId, projectId, sessionId, out var scope, out var denied)) return denied;
        // Чат и сцена — из маршрута: тело не может подсунуть чужие
        var request = req is null ? null : req with { SessionId = sessionId, SceneId = sceneId };
        return FilmHttp.Map(await saver.SaveAsync(Tenant(scope), UserId, scope, request, VideoInitiators.Human, ct), saved => Ok(saved));
    }

    private bool Gate(string projectId, [NotNullWhen(true)] out VideoEditScope? scope,
        [NotNullWhen(false)] out IActionResult? denied) =>
        gate.TryProject(UserId, projectId, out scope, out denied);
}
