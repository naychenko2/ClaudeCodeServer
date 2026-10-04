using ClaudeHomeServer.Services.ChatContext;
using ClaudeHomeServer.Services.VideoEditor.Films;
using ClaudeHomeServer.Services.VideoEditor.Contracts;
using ClaudeHomeServer.Services.VideoEditor.Jobs;
using ClaudeHomeServer.Services.VideoEditor.Prefs;

namespace ClaudeHomeServer.Services.VideoEditor.Scenes;

// Операции над нитями сцен, общие для ручек человека и тулсета агента (ADR-022 §5, требование Андрея 2026-10-02:
// у тулсета нет своего пути исполнения). Здесь — проверки входов (папка, кадры, пределы), запись в хранилище,
// якорь сцены в ленте (video_scene) и рассылка нитей владельцу. Ревизия необязательна: ручка человека передаёт
// ту, от которой считал фронт, агент — null (без сверки).
public sealed class VideoSceneService(VideoJobThreads threads, VideoPrefsService prefs)
{
    // Модуль пишет в проект только в video/** и music/**; сцены живут в video/**
    public const string ScenesRoot = "video";

    // Результат операции: либо отказ проверки входа (ErrorCode + Error), либо запись хранилища
    public sealed record Call(VideoThreadWrite? Written, string? ErrorCode, string? Error)
    {
        public static Call Refuse(string code, string error) => new(null, code, error);
        public static Call Of(VideoThreadWrite written) => new(written, null, null);
    }

    public VideoThreadStore Store => threads.Store;

    public async Task<Call> AddAsync(string ownerId, VideoEditScope scope, string sessionId, string? rawFolder,
        VideoSceneSettingsDto? given, string? name, long? revision, CancellationToken ct,
        ContextActor by = ContextActor.Human)
    {
        if (FolderProblem(scope, rawFolder, out var folder) is { } badFolder) return badFolder;
        var settings = given ?? prefs.ForNewScene(ownerId, scope);
        if (SettingsProblem(scope, settings) is { } badSettings) return badSettings;

        var written = threads.Tracked(ownerId, sessionId, () => Store.AddScene(ownerId, sessionId, folder, settings, revision,
            string.IsNullOrWhiteSpace(name) ? null : name.Trim()), by);
        if (written is { Status: VideoThreadWriteStatus.Ok, Scene: { } scene })
            await threads.AnchorAsync(sessionId, scene, ct);
        return await PublishedAsync(ownerId, scope, sessionId, written);
    }

    // Фокус: сцена в работе и открытый фильм. Фильм — только у проекта и только внутри video/**: путь хранится
    // как есть, диска не касаемся
    public async Task<Call> FocusAsync(string ownerId, VideoEditScope scope, string sessionId, VideoFocusDto focus,
        long? revision, ContextActor by = ContextActor.Human)
    {
        if (focus.FilmPath is not null && (scope.IsPersonal || !InsideAllowed(focus.FilmPath)))
            return Call.Refuse(scope.IsPersonal ? VideoEditorErrors.PersonalScopeNoFilms : VideoEditorErrors.OutsideAllowedFolders,
                scope.IsPersonal ? "Фильмы — только в чате проекта" : "Фильм должен лежать в video/");
        // Фильм — только по правилу пути video/<имя фильма>/*.film (то же, что у Validate вида): иначе он встал бы основным мимо него
        if (!string.IsNullOrWhiteSpace(focus.FilmPath) && !FilmPaths.IsFilmPath(FilmPaths.Normalize(focus.FilmPath) ?? ""))
            return Call.Refuse(VideoEditorErrors.OutsideAllowedFolders, "Фильм должен лежать в папке video/<имя фильма>/");
        var clean = new VideoFocusDto(string.IsNullOrWhiteSpace(focus.SceneId) ? null : focus.SceneId.Trim(),
            string.IsNullOrWhiteSpace(focus.FilmPath) ? null : focus.FilmPath.Trim());
        return await PublishedAsync(ownerId, scope, sessionId,
            threads.Tracked(ownerId, sessionId, () => Store.SetFocus(ownerId, sessionId, clean, revision), by));
    }

    public async Task<Call> SettingsAsync(string ownerId, VideoEditScope scope, string sessionId, string sceneId,
        VideoSceneSettingsDto settings, long? revision)
    {
        if (SettingsProblem(scope, settings) is { } bad) return bad;
        return await PublishedAsync(ownerId, scope, sessionId, Store.SetSettings(ownerId, sessionId, sceneId, settings, revision));
    }

    // «Продолжить от версии»: версия становится текущей, сцена — в работе. Ничего не удаляет
    public async Task<Call> CurrentAsync(string ownerId, VideoEditScope scope, string sessionId, string sceneId,
        string versionId, long? revision, ContextActor by = ContextActor.Human) =>
        await PublishedAsync(ownerId, scope, sessionId,
            threads.Tracked(ownerId, sessionId,
                () => Store.SetCurrentVersion(ownerId, sessionId, sceneId, versionId.Trim(), revision, focus: true), by));

    // Запись прошла — свежие нити уходят владельцу
    public async Task<Call> PublishedAsync(string ownerId, VideoEditScope scope, string sessionId, VideoThreadWrite written)
    {
        if (written.Status == VideoThreadWriteStatus.Ok)
            await threads.BroadcastAsync(ownerId, scope.Key, sessionId, written.State);
        return Call.Of(written);
    }

    // ── Проверки входов ──────────────────────────────────────────────────────────

    // Папка сцены: у личной области только пустая (диска проекта нет — отказ до RootPath); у проекта — внутри
    // video/** без «..» и без символических ссылок по существующим сегментам
    public static Call? FolderProblem(VideoEditScope scope, string? raw, out string folder)
    {
        folder = "";
        var value = (raw ?? "").Trim().Replace('\\', '/').Trim('/');
        if (value.Length == 0) return null;
        if (scope.Project is not { } project)
            return Call.Refuse(VideoEditorErrors.InvalidRequest,
                "У чата вне проекта нет папок проекта: сцену можно завести только без папки");
        if (!InsideAllowed(value))
            return Call.Refuse(VideoEditorErrors.OutsideAllowedFolders,
                $"Сцены лежат только в video/ проекта, а получено «{value}»: укажи полный путь от корня проекта, например video/утро");
        if (ClaudeHomeServer.Services.Media.ProjectLinkGuard.ResolveInside(project.RootPath, value) is null)
            return Call.Refuse(VideoEditorErrors.OutsideAllowedFolders, "Путь идёт через символическую ссылку или вне проекта");
        folder = value;
        return null;
    }

    // Только video/** и music/**: сегменты без «..», корень — video или music
    public static bool InsideAllowed(string relative)
    {
        var value = relative.Trim().Replace('\\', '/').Trim('/');
        if (value.Length == 0 || value.Split('/').Any(s => s is ".." or "." or "")) return false;
        var root = value.Split('/')[0];
        return root is ScenesRoot or "music";
    }

    // Кадры в настройках: image — с threadId и versionId, file — путь существующего файла проекта (у личной
    // области файлов проекта нет); число вариантов и длительность в пределах
    public static Call? SettingsProblem(VideoEditScope scope, VideoSceneSettingsDto s)
    {
        if (s.Count is { } count && (count < 1 || count > VideoThreadStore.MaxCount))
            return Call.Refuse(VideoEditorErrors.InvalidRequest, $"Вариантов — от 1 до {VideoThreadStore.MaxCount}");
        if (s.DurationSec is <= 0)
            return Call.Refuse(VideoEditorErrors.InvalidRequest, "Длительность должна быть положительной");
        foreach (var frame in new[] { s.FrameA, s.FrameB })
        {
            if (frame is null) continue;
            switch (frame.Kind)
            {
                case FrameRef.KindImage when string.IsNullOrWhiteSpace(frame.ThreadId) || string.IsNullOrWhiteSpace(frame.VersionId):
                    return Call.Refuse(VideoEditorErrors.InvalidRequest, "У кадра из «Картинок» нужны нить и версия");
                case FrameRef.KindFile:
                    if (scope.Project is not { } project)
                    {
                        // Личный чат: только загруженный «С компьютера» кадр рабочей папки; наличие файла проверит съёмка
                        if (!VideoEditWorkspace.IsFrameRef(frame.Path))
                            return Call.Refuse(VideoEditorErrors.InvalidRequest,
                                "У чата вне проекта нет файлов проекта: загрузите кадр «С компьютера»");
                        break;
                    }
                    if (string.IsNullOrWhiteSpace(frame.Path)
                        || ClaudeHomeServer.Services.Media.ProjectLinkGuard.ResolveInside(project.RootPath, frame.Path) is not { } full
                        || !File.Exists(full))
                        return Call.Refuse(VideoEditorErrors.InvalidRequest, "Кадр не найден в проекте");
                    break;
                case FrameRef.KindImage:
                    break;
                default:
                    return Call.Refuse(VideoEditorErrors.InvalidRequest, "Неизвестный вид кадра");
            }
        }
        return null;
    }
}
