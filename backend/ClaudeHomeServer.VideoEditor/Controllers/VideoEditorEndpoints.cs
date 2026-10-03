using System.Security.Claims;
using ClaudeHomeServer.Services.Media;
using ClaudeHomeServer.Services.VideoEditor.Catalog;
using ClaudeHomeServer.Services.VideoEditor.Contracts;
using ClaudeHomeServer.Services.VideoEditor.Jobs;
using ClaudeHomeServer.Services.VideoEditor.Prefs;
using ClaudeHomeServer.Services.VideoEditor.Scenes;
using Microsoft.AspNetCore.Mvc;

namespace ClaudeHomeServer.Services.VideoEditor.Controllers;

// Общее тело ручек модуля «Видео» (ADR-022 §2, как AudioEditorEndpoints): одна реализация проверок на
// проектные и личные ручки. Наследник проходит свой гейт (флаг, проект, чат — 404) и передаёт область;
// всё, что читает диск проекта, берёт scope.Project и у личной области отказывает до обращения к RootPath.
//
// Сцены: чужая сцена в своём чате — 404 scene_not_found; каждая мутация несёт revision, от которой
// считал фронт, устарела — 409 с актуальным состоянием; ответ мутации — полное состояние нитей.
public abstract class VideoEditorEndpoints(
    IEnumerable<IVideoEngine> engines,
    VideoEditJobService jobs,
    VideoJobThreads threads,
    VideoPrefsService prefs,
    VideoEditWorkspace workspace,
    VideoSceneService scenes) : ControllerBase
{
    // Владелец — claim sub сервисного JWT. Константой, а не JwtRegisteredClaimNames: своих пакетов у
    // модуля нет (DynamicModulePackagesGuardTests)
    private const string SubClaim = "sub";

    // Модуль пишет в проект только в video/** и music/**; сцены живут в video/**
    public const string ScenesRoot = VideoSceneService.ScenesRoot;

    protected string UserId => User.FindFirstValue(SubClaim)!;

    // ── Состояние, каталог, префы ────────────────────────────────────────────────

    protected IActionResult StateIn(VideoEditScope scope, string sessionId) =>
        Ok(new VideoStateDto(threads.Store.Get(UserId, sessionId).ToDto(),
            VideoCatalogView.Build(engines, scope, jobs.PrefersLocal(UserId)), prefs.Get(UserId, scope)));

    protected IActionResult CatalogIn(VideoEditScope scope) =>
        Ok(VideoCatalogView.Build(engines, scope, jobs.PrefersLocal(UserId)));

    protected IActionResult PrefsIn(VideoEditScope scope) => Ok(prefs.Get(UserId, scope));

    protected IActionResult PutPrefsIn(VideoEditScope scope, VideoPrefsDto? req)
    {
        if (req is null)
            return Error(StatusCodes.Status400BadRequest, VideoEditorErrors.InvalidRequest, "Пустые настройки");
        if (req.Count is { } count && (count < 1 || count > VideoThreadStore.MaxCount))
            return Error(StatusCodes.Status400BadRequest, VideoEditorErrors.InvalidRequest,
                $"Вариантов — от 1 до {VideoThreadStore.MaxCount}");
        if (req.DurationSec is <= 0)
            return Error(StatusCodes.Status400BadRequest, VideoEditorErrors.InvalidRequest, "Длительность должна быть положительной");
        prefs.Save(UserId, scope, req);
        return Ok(prefs.Get(UserId, scope));
    }

    // ── Котировка, запуск, отмена ────────────────────────────────────────────────

    protected async Task<IActionResult> QuoteIn(VideoEditScope scope, VideoQuoteRequest? req, CancellationToken ct)
    {
        if (req is null || string.IsNullOrWhiteSpace(req.SessionId) || string.IsNullOrWhiteSpace(req.SceneId))
            return Error(StatusCodes.Status400BadRequest, VideoEditorErrors.InvalidRequest, "Не указана сцена");
        return Map(await jobs.QuoteAsync(UserId, scope, req with { SessionId = req.SessionId.Trim(), SceneId = req.SceneId.Trim() }, ct), Ok);
    }

    // Запуск человека: Initiator из тела игнорируется — агент запускает через тулсет, а не ручкой
    protected async Task<IActionResult> StartIn(VideoEditScope scope, VideoLaunchRequest? req, CancellationToken ct)
    {
        if (req is null || string.IsNullOrWhiteSpace(req.QuoteId) || string.IsNullOrWhiteSpace(req.SessionId)
            || string.IsNullOrWhiteSpace(req.SceneId))
            return Error(StatusCodes.Status400BadRequest, VideoEditorErrors.InvalidRequest, "Не указаны котировка и сцена");
        if (jobs.FindQuote(UserId, scope.Key, req.QuoteId.Trim()) is null)
            return Error(StatusCodes.Status404NotFound, VideoEditorErrors.QuoteNotFound, VideoEditJobService.QuoteExpiredText);

        var input = req with
        {
            QuoteId = req.QuoteId.Trim(), SessionId = req.SessionId.Trim(), SceneId = req.SceneId.Trim(),
            Initiator = VideoInitiators.Human,
        };
        return Map(await jobs.StartAsync(UserId, scope, input, ct), created => StatusCode(StatusCodes.Status202Accepted, created));
    }

    protected IActionResult JobIn(VideoEditScope scope, string jobId) =>
        jobs.Get(UserId, scope.Key, jobId) is { } job ? Ok(job) : JobNotFound();

    protected async Task<IActionResult> CancelIn(VideoEditScope scope, string jobId, CancellationToken ct) =>
        await jobs.CancelAsync(UserId, scope.Key, jobId, ct) is { } job ? Ok(job) : JobNotFound();

    // ── Сцены ────────────────────────────────────────────────────────────────────

    protected IActionResult ScenesIn(string sessionId) => Ok(threads.Store.Get(UserId, sessionId).ToDto());

    // Новая сцена: папка — внутри video/** проекта (у личного чата — только пустая), кадры в настройках
    // проверяются так же, как при правке настроек. Тело — VideoSceneService, общий с тулсетом агента
    protected async Task<IActionResult> AddSceneIn(VideoEditScope scope, string sessionId, VideoSceneCreateRequest? req,
        CancellationToken ct)
    {
        if (req is null)
            return Error(StatusCodes.Status400BadRequest, VideoEditorErrors.InvalidRequest, "Пустой запрос");
        return Reply(await scenes.AddAsync(UserId, scope, sessionId, req.Folder, req.Settings, req.Name, req.Revision, ct));
    }

    protected async Task<IActionResult> FocusIn(VideoEditScope scope, string sessionId, VideoSceneFocusRequest? req)
    {
        if (req?.Focus is not { } focus)
            return Error(StatusCodes.Status400BadRequest, VideoEditorErrors.InvalidRequest, "Не указан фокус");
        return Reply(await scenes.FocusAsync(UserId, scope, sessionId, focus, req.Revision));
    }

    // Убрать сцену, где нечего терять; у сцены с версиями или идущим запуском — 400
    protected async Task<IActionResult> RemoveIn(VideoEditScope scope, string sessionId, string sceneId, long revision) =>
        await ResultAsync(scope, sessionId, threads.Store.Remove(UserId, sessionId, sceneId, revision));

    protected async Task<IActionResult> SettingsIn(VideoEditScope scope, string sessionId, string sceneId,
        VideoSceneSettingsRequest? req)
    {
        if (req?.Settings is not { } settings)
            return Error(StatusCodes.Status400BadRequest, VideoEditorErrors.InvalidRequest, "Пустые настройки");
        return Reply(await scenes.SettingsAsync(UserId, scope, sessionId, sceneId, settings, req.Revision));
    }

    // «Продолжить от версии»: версия становится текущей, сцена — в работе. Ничего не удаляет
    protected async Task<IActionResult> CurrentIn(VideoEditScope scope, string sessionId, string sceneId,
        VideoSceneCurrentRequest? req)
    {
        if (req is null || string.IsNullOrWhiteSpace(req.VersionId))
            return Error(StatusCodes.Status400BadRequest, VideoEditorErrors.InvalidRequest, "Не указана версия");
        return Reply(await scenes.CurrentAsync(UserId, scope, sessionId, sceneId, req.VersionId, req.Revision));
    }

    // Клип версии для плеера: Range — перемотка без скачивания целиком. Только файлы своих сцен; токен для
    // <video src> — через ?access_token=, как у картинок и звука
    protected IActionResult VersionFileIn(string sessionId, string sceneId, string versionId, bool download)
    {
        var scene = threads.Store.Get(UserId, sessionId).Scenes.FirstOrDefault(s => s.SceneId == sceneId);
        if (scene is null) return SceneNotFound();
        var version = scene.Versions.FirstOrDefault(v => v.VersionId == versionId);
        if (version is null) return VersionNotFound();
        string? path;
        try { path = workspace.FindClip(UserId, version.JobId, version.Variant); }
        catch (ArgumentException) { path = null; }
        if (path is null || !System.IO.File.Exists(path))
            return Error(StatusCodes.Status404NotFound, VideoEditorErrors.FileNotFound,
                "Файла клипа больше нет — рабочая папка очищена");
        var result = PhysicalFile(path, ContentTypeOf(path), enableRangeProcessing: true);
        if (download) result.FileDownloadName = $"{SafeName(scene.Name)}.v{version.Number}{Path.GetExtension(path)}";
        return result;
    }

    private async Task<IActionResult> ResultAsync(VideoEditScope scope, string sessionId, VideoThreadWrite written) =>
        Reply(await scenes.PublishedAsync(UserId, scope, sessionId, written));

    // Отказ проверки входа — 400 с кодом; запись хранилища — по статусу
    private IActionResult Reply(VideoSceneService.Call call)
    {
        if (call.Written is not { } written)
            return Error(StatusCodes.Status400BadRequest, call.ErrorCode ?? VideoEditorErrors.InvalidRequest, call.Error ?? "Запрос не выполнен");
        return written.Status switch
        {
            VideoThreadWriteStatus.Ok => Ok(written.State.ToDto()),
            VideoThreadWriteStatus.Conflict => StatusCode(StatusCodes.Status409Conflict, new
            {
                error = "Сцены чата уже поменялись — перечитайте их",
                code = VideoEditorErrors.RevisionConflict,
                state = written.State.ToDto(),
            }),
            VideoThreadWriteStatus.VersionNotFound => VersionNotFound(),
            VideoThreadWriteStatus.Invalid => Error(StatusCodes.Status400BadRequest, VideoEditorErrors.InvalidRequest,
                "Действие не подходит этой сцене: у неё уже есть версии, идёт съёмка или настройки неверны"),
            _ => SceneNotFound(),
        };
    }

    internal static bool InsideAllowed(string relative) => VideoSceneService.InsideAllowed(relative);

    // ── Помощники ────────────────────────────────────────────────────────────────

    private static string ContentTypeOf(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".mp4" => "video/mp4",
        ".webm" => "video/webm",
        ".mov" => "video/quicktime",
        _ => "application/octet-stream",
    };

    private static string SafeName(string name) =>
        string.Concat(name.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '-')).Trim('-');

    protected ObjectResult Error(int status, string code, string error) => StatusCode(status, new { error, code });

    protected IActionResult SceneNotFound() =>
        Error(StatusCodes.Status404NotFound, VideoEditorErrors.SceneNotFound, "Сцена не найдена в этом чате");

    protected IActionResult VersionNotFound() =>
        Error(StatusCodes.Status404NotFound, VideoEditorErrors.VersionNotFound, "Версии нет в этой сцене");

    private IActionResult JobNotFound() =>
        Error(StatusCodes.Status404NotFound, VideoEditorErrors.JobNotFound, "Задача не найдена");

    protected IActionResult Map<T>(VideoEditCallResult<T> result, Func<T, IActionResult> ok)
    {
        if (result.ErrorCode is null && result.Value is not null) return ok(result.Value);
        var code = result.ErrorCode ?? VideoEditorErrors.InvalidRequest;
        var status = code switch
        {
            VideoEditorErrors.ProviderUnavailable or VideoEditorErrors.NameTaken
                or VideoEditorErrors.RevisionConflict => StatusCodes.Status409Conflict,
            VideoEditorErrors.QuoteNotFound or VideoEditorErrors.JobNotFound or VideoEditorErrors.SceneNotFound
                or VideoEditorErrors.VersionNotFound or VideoEditorErrors.FileNotFound => StatusCodes.Status404NotFound,
            VideoEditorErrors.TooManyJobs or VideoEditorErrors.HeavyBusy => StatusCodes.Status429TooManyRequests,
            VideoEditorErrors.DspUnavailable => StatusCodes.Status503ServiceUnavailable,
            _ => StatusCodes.Status400BadRequest,
        };
        // Сосед при отказе поставщика: кнопка «Повторить через …» показывает его цену
        if (result.Retry is { } retry)
            return StatusCode(status, new { error = result.Error ?? "Запрос не выполнен", code, retry });
        return Error(status, code, result.Error ?? "Запрос не выполнен");
    }
}
