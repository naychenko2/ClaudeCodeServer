using System.Diagnostics.CodeAnalysis;
using System.Security.Claims;
using ClaudeHomeServer.Services.Composition;
using ClaudeHomeServer.Services.VideoEditor.Assembly;
using ClaudeHomeServer.Services.VideoEditor.Contracts;
using ClaudeHomeServer.Services.VideoEditor.Films;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ClaudeHomeServer.Services.VideoEditor.Controllers;

// Фильмы и сохранение сцены в проект (ADR-022 §2, блок 2): только проектные ручки — у личного чата диска проекта
// нет (PersonalFilmController отвечает personal_scope_no_films). Гейт тот же, что у остальных ручек модуля: флаг,
// свой проект, свой чат — иначе 404. Локальный проект (ADR-016) отказывает до тела через ProjectCapability.
[ProjectCapability(ProjectCapabilityArea.FileBound)]
[ApiController]
[Authorize]
[Route(VideoEditorRoutes.ProjectBase)]
public class FilmController(
    VideoEditScopeGate gate,
    FilmService films,
    FilmSceneSaver saver,
    FilmAssembler assembler,
    FilmMusicComposer music) : ControllerBase
{
    private const string SubClaim = "sub";

    private string UserId => User.FindFirstValue(SubClaim)!;

    [HttpGet(VideoEditorRoutes.Films)]
    public IActionResult List(string projectId) =>
        Gate(projectId, out var scope, out var denied)
            ? FilmHttp.Map(films.List(scope), list => Ok(list))
            : denied;

    [HttpGet(VideoEditorRoutes.FilmState)]
    public IActionResult State(string projectId, [FromQuery] string? path) =>
        Gate(projectId, out var scope, out var denied)
            ? FilmHttp.Map(films.State(UserId, scope, path), state => Ok(state))
            : denied;

    [HttpPatch(VideoEditorRoutes.FilmPatchRoute)]
    public async Task<IActionResult> Patch(string projectId, [FromQuery] string? path, [FromBody] FilmPatch? patch,
        CancellationToken ct, [FromQuery] string? sessionId = null) =>
        Gate(projectId, out var scope, out var denied)
            ? FilmHttp.Map(await films.PatchAsync(UserId, scope, path, patch, VideoInitiators.Human, ct, sessionId), state => Ok(state))
            : denied;

    [HttpPost(VideoEditorRoutes.FilmBuild)]
    public IActionResult Build(string projectId, [FromQuery] string? path, [FromQuery] string? sessionId = null) =>
        Gate(projectId, out var scope, out var denied)
            ? FilmHttp.Map(assembler.Start(UserId, scope, path, VideoInitiators.Human, sessionId),
                status => StatusCode(StatusCodes.Status202Accepted, status))
            : denied;

    [HttpGet(VideoEditorRoutes.FilmBuild)]
    public IActionResult BuildStatus(string projectId, [FromQuery] string? path) =>
        Gate(projectId, out var scope, out var denied)
            ? assembler.Status(UserId, scope, path) is { } status
                ? Ok(status)
                : FilmHttp.Map(FilmCallResult<FilmBuildStatusDto>.Fail(VideoEditorErrors.JobNotFound, "Сборки ещё не было"), s => Ok(s))
            : denied;

    [HttpDelete(VideoEditorRoutes.FilmBuild)]
    public IActionResult CancelBuild(string projectId, [FromQuery] string? path) =>
        Gate(projectId, out var scope, out var denied)
            ? assembler.Cancel(UserId, scope, path) && assembler.Status(UserId, scope, path) is { } status
                ? Ok(status)
                : FilmHttp.Map(FilmCallResult<FilmBuildStatusDto>.Fail(VideoEditorErrors.JobNotFound, "Сборка не идёт"), s => Ok(s))
            : denied;

    [HttpPost(VideoEditorRoutes.FilmMusic)]
    public async Task<IActionResult> ComposeMusic(string projectId, [FromQuery] string? path,
        [FromBody] FilmMusicRequest? req, CancellationToken ct) =>
        Gate(projectId, out var scope, out var denied)
            ? FilmHttp.Map(await music.CreateDraftAsync(UserId, scope, path, req, ct), draft => Ok(draft))
            : denied;

    [HttpPost("sessions/{sessionId}/" + VideoEditorRoutes.SceneSave)]
    public async Task<IActionResult> SaveScene(string projectId, string sessionId, string sceneId,
        [FromBody] SaveSceneRequest? req, CancellationToken ct)
    {
        if (!gate.TryProjectChat(UserId, projectId, sessionId, out var scope, out var denied)) return denied;
        // Чат и сцена — из маршрута: тело не может подсунуть чужие
        var request = req is null ? null : req with { SessionId = sessionId, SceneId = sceneId };
        return FilmHttp.Map(await saver.SaveAsync(UserId, scope, request, VideoInitiators.Human, ct), saved => Ok(saved));
    }

    private bool Gate(string projectId, [NotNullWhen(true)] out VideoEditScope? scope,
        [NotNullWhen(false)] out IActionResult? denied) =>
        gate.TryProject(UserId, projectId, out scope, out denied);
}
