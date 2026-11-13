using System.Security.Claims;
using ClaudeHomeServer.Services.VideoEditor.Assembly;
using ClaudeHomeServer.Services.VideoEditor.Contracts;
using ClaudeHomeServer.Services.VideoEditor.Films;
using Microsoft.AspNetCore.Mvc;

namespace ClaudeHomeServer.Services.VideoEditor.Controllers;

// Общее тело ручек фильмов (ADR-022 §2, ADR-040 Ф2-6): одна реализация на проектные и личные ручки, как
// VideoEditorEndpoints. Наследник проходит свой гейт и передаёт область; чат, если он нужен, — тоже его.
// Личная область без папки сюда не доходит: наследник отвечает до тела (GET — «пусто», остальное — «нет такого фильма»).
public abstract class FilmEndpoints(
    FilmService films,
    FilmAssembler assembler,
    FilmMusicComposer music,
    ChatContext.VideoContextLaunch? context = null) : ControllerBase
{
    private const string SubClaim = "sub";

    protected string UserId => User.FindFirstValue(SubClaim)!;

    // Ключ хранилищ фильмов — арендатор области, UserId остаётся только в гейте (право доступа)
    protected static string Tenant(VideoEditScope scope) =>
        scope.TenantOwnerId ?? throw new InvalidOperationException("Область без арендатора: её выдаёт только VideoEditScopeGate");

    protected IActionResult ListIn(VideoEditScope scope) =>
        FilmHttp.Map(films.List(scope), list => Ok(list));

    protected IActionResult StateIn(VideoEditScope scope, string? path) =>
        FilmHttp.Map(films.State(Tenant(scope), UserId, scope, path), state => Ok(state));

    protected async Task<IActionResult> CreateIn(VideoEditScope scope, FilmCreateRequest? req, CancellationToken ct) =>
        FilmHttp.Map(await films.CreateAsync(Tenant(scope), UserId, scope, req, ct),
            state => StatusCode(StatusCodes.Status201Created, state));

    protected async Task<IActionResult> PatchIn(VideoEditScope scope, string? path, FilmPatch? patch, string? sessionId,
        CancellationToken ct) =>
        FilmHttp.Map(await films.PatchAsync(Tenant(scope), UserId, scope, path, patch, VideoInitiators.Human, ct, sessionId),
            state => Ok(state));

    protected IActionResult BuildIn(VideoEditScope scope, string? path, string? sessionId, long? contextRevision)
    {
        // Ревизия контекста (ADR-023 §Д2.1): основной объект обязан быть этим фильмом, иначе 409/400 до сборки
        if (contextRevision is { } revision && ContextRefusal(scope, path, sessionId, revision) is { } refusal)
            return refusal;
        return FilmHttp.Map(assembler.Start(Tenant(scope), UserId, scope, path, VideoInitiators.Human, sessionId),
            status => StatusCode(StatusCodes.Status202Accepted, status));
    }

    private IActionResult? ContextRefusal(VideoEditScope scope, string? path, string? sessionId, long revision)
    {
        if (context is null)
            return StatusCode(StatusCodes.Status503ServiceUnavailable,
                new { error = ChatContext.VideoContextLaunch.UnavailableText, code = VideoEditorErrors.ProviderUnavailable });
        var read = context.Read(Tenant(scope), scope, sessionId, revision, UserId);
        if (!read.Ok)
            return read.Fresh is { } fresh
                ? StatusCode(StatusCodes.Status409Conflict, new Protocol.ChatContextConflictDto(read.ErrorCode!, fresh))
                : FilmHttp.Map(FilmCallResult<object>.Fail(read.ErrorCode!, read.Error ?? "Запрос не выполнен"), o => Ok(o));
        var inputs = ChatContext.VideoContextLaunch.Extract(read.State!);
        return inputs.FilmPath is { } film && FilmPaths.Normalize(film) == FilmPaths.Normalize(path)
            ? null
            : FilmHttp.Map(FilmCallResult<object>.Fail(VideoEditorErrors.InvalidRequest, ChatContext.VideoContextLaunch.NotFilmText), o => Ok(o));
    }

    protected IActionResult BuildStatusIn(VideoEditScope scope, string? path) =>
        assembler.Status(Tenant(scope), UserId, scope, path) is { } status
            ? Ok(status)
            : BuildNotFound("Сборки ещё не было");

    protected IActionResult CancelBuildIn(VideoEditScope scope, string? path) =>
        assembler.Cancel(Tenant(scope), UserId, scope, path) && assembler.Status(Tenant(scope), UserId, scope, path) is { } status
            ? Ok(status)
            : BuildNotFound("Сборка не идёт");

    protected static IActionResult BuildNotFound(string text) =>
        FilmHttp.Map(FilmCallResult<FilmBuildStatusDto>.Fail(VideoEditorErrors.JobNotFound, text), s => new OkObjectResult(s));

    protected async Task<IActionResult> ComposeMusicIn(VideoEditScope scope, string? path, FilmMusicRequest? req, CancellationToken ct) =>
        FilmHttp.Map(await music.CreateDraftAsync(Tenant(scope), UserId, scope, path, req, ct), draft => Ok(draft));

    // «Фильма нет»: личная область без папки «Личного» — файла фильма быть не может, папку не создаём
    protected static IActionResult NoSuchFilm() =>
        FilmHttp.Map(FilmCallResult<object>.Fail(VideoEditorErrors.FileNotFound, "Фильм не найден"), o => new OkObjectResult(o));
}
