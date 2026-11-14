using System.Diagnostics.CodeAnalysis;
using ClaudeHomeServer.Services.VideoEditor.Assembly;
using ClaudeHomeServer.Services.VideoEditor.Contracts;
using ClaudeHomeServer.Services.VideoEditor.Films;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ClaudeHomeServer.Services.VideoEditor.Controllers;

// Фильмы в личном чате вне проекта (ADR-040, Ф2-6): те же маршруты и то же тело (FilmEndpoints), что у проектных;
// фильмы лежат в Personal/video/. Гейт — флаг и свой чат (иначе 404). Чат берётся из маршрута, поэтому sessionId из
// query/тела не читается: правка и сборка идут от этого чата. Папку «Личного» создаёт только создание фильма (WithFolderAsync);
// остальные ручки её не создают: без папки список пуст, а фильма, сборки и правки нет (404).
[ApiController]
[Authorize]
[Route(VideoEditorRoutes.PersonalBase)]
public class PersonalFilmController(
    VideoEditScopeGate gate,
    FilmService films,
    FilmAssembler assembler,
    FilmMusicComposer music,
    ChatContext.VideoContextLaunch? context = null) : FilmEndpoints(films, assembler, music, context)
{
    [HttpGet(VideoEditorRoutes.Films)]
    public IActionResult List(string sessionId) =>
        Gate(sessionId, out var scope, out var denied)
            ? scope.HasFolder ? ListIn(scope) : Ok(Array.Empty<FilmSummaryDto>())
            : denied;

    [HttpGet(VideoEditorRoutes.FilmState)]
    public IActionResult State(string sessionId, [FromQuery] string? path) =>
        Gate(sessionId, out var scope, out var denied) ? scope.HasFolder ? StateIn(scope, path) : NoSuchFilm() : denied;

    [HttpPost(VideoEditorRoutes.FilmCreate)]
    public async Task<IActionResult> Create(string sessionId, [FromBody] FilmCreateRequest? req, CancellationToken ct)
    {
        if (!Gate(sessionId, out var scope, out var denied)) return denied;
        // Пишущий путь: «Личное» создаётся здесь; не вышло — CreateAsync отвечает personal_scope_no_films («папка недоступна»)
        return await CreateIn(await gate.WithFolderAsync(scope, ct), req, ct);
    }

    [HttpPatch(VideoEditorRoutes.FilmPatchRoute)]
    public async Task<IActionResult> Patch(string sessionId, [FromQuery] string? path, [FromBody] FilmPatch? patch, CancellationToken ct) =>
        Gate(sessionId, out var scope, out var denied) ? scope.HasFolder ? await PatchIn(scope, path, patch, sessionId, ct) : NoSuchFilm() : denied;

    [HttpPost(VideoEditorRoutes.FilmBuild)]
    public IActionResult Build(string sessionId, [FromQuery] string? path, [FromQuery] long? contextRevision = null) =>
        Gate(sessionId, out var scope, out var denied) ? scope.HasFolder ? BuildIn(scope, path, sessionId, contextRevision) : NoSuchFilm() : denied;

    [HttpGet(VideoEditorRoutes.FilmBuild)]
    public IActionResult BuildStatus(string sessionId, [FromQuery] string? path) =>
        Gate(sessionId, out var scope, out var denied)
            ? scope.HasFolder ? BuildStatusIn(scope, path) : BuildNotFound("Сборки ещё не было")
            : denied;

    [HttpDelete(VideoEditorRoutes.FilmBuild)]
    public IActionResult CancelBuild(string sessionId, [FromQuery] string? path) =>
        Gate(sessionId, out var scope, out var denied)
            ? scope.HasFolder ? CancelBuildIn(scope, path) : BuildNotFound("Сборка не идёт")
            : denied;

    [HttpPost(VideoEditorRoutes.FilmMusic)]
    public async Task<IActionResult> ComposeMusic(string sessionId, [FromQuery] string? path, CancellationToken ct) =>
        Gate(sessionId, out var scope, out var denied)
            ? scope.HasFolder ? await ComposeMusicIn(scope, path, new FilmMusicRequest(sessionId), ct) : NoSuchFilm()
            : denied;

    private bool Gate(string sessionId, [NotNullWhen(true)] out VideoEditScope? scope,
        [NotNullWhen(false)] out IActionResult? denied) =>
        gate.TryPersonalChat(UserId, sessionId, out scope, out denied);
}
