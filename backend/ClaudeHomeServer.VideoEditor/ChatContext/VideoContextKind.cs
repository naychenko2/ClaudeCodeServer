using System.Text.Json.Nodes;
using ClaudeHomeServer.Services.ChatContext;
using ClaudeHomeServer.Services.Composition;
using ClaudeHomeServer.Services.Media;
using ClaudeHomeServer.Services.VideoEditor.Catalog;
using ClaudeHomeServer.Services.VideoEditor.Films;
using ClaudeHomeServer.Services.VideoEditor.Prefs;
using ClaudeHomeServer.Services.VideoEditor.Scenes;

namespace ClaudeHomeServer.Services.VideoEditor.ChatContext;

// Роли референсов сцены (ADR-023 §1, Дополнение 3): принимает основной объект «video-scene»; роль
// принадлежит съёмке. Картинка-версия или файл проекта входят в сцену кадром A или кадром B
public static class VideoContextRoles
{
    public const string FrameA = "frame-a";
    public const string FrameB = "frame-b";
}

// Имена операций видео в таблице ролей (usedBy серого чипа) и в каталоге действий
public static class VideoContextOps
{
    public const string Shoot = "shoot";
    public const string Build = "build";
}

// Виды «video-scene» и «video-film» контекста чата (ADR-023, фаза 3).
// video-scene: Ref = {sceneId} — сцена нитей этого чата; основной объект, принимает кадры (AcceptedRefs).
// video-film: Ref = {filmPath} — файл video/<фильм>/*.film проекта (правило — FilmPaths.IsFilmPath); основной объект, референсов не принимает.
// Основной один: составной фокус «Видео» {sceneId, filmPath} распадается, основным становится выбранное
// последним. Сцена показывает фильм, к которому относится («сцена 3 · утро-в-горах»), но отдельным полем
// стора он не хранится. Засевает контекст чата без файла из фокуса «Видео» — после картинки и звука.
public sealed class VideoContextKind(
    VideoThreadStore store,
    IEnumerable<IVideoEngine>? engines = null,
    VideoPrefsService? prefs = null) : IContextKindProvider, IChatContextSeedSource
{
    public const string SceneKind = "video-scene";
    public const string FilmKind = "video-film";

    public const string SceneKey = "sceneId";
    public const string FilmKey = "filmPath";

    public IReadOnlyList<string> Kinds { get; } = [SceneKind, FilmKind];

    public int SeedPriority => 2;

    public bool CanBePrimary(string kind) => kind is SceneKind or FilmKind;

    // Сцена обязана быть в нитях этого владельца и этого чата; фильм — только в чате проекта, внутри video/**,
    // файлом .film, найденным через ProjectLinkGuard (символическая ссылка наружу — отказ)
    public string? Validate(ContextScope scope, string kind, JsonObject reference)
    {
        if (kind == SceneKind)
        {
            if (Text(reference, SceneKey) is not { } sceneId) return "Не указана сцена";
            return Find(scope, sceneId) is null ? "Сцена не найдена в этом чате" : null;
        }
        if (kind != FilmKind) return $"Вид «{kind}» не принадлежит редактору видео";
        if (scope.Project is not { } project) return "В личном чате нет фильмов";
        if (ProjectCapabilityGuard.Refusal(project, ProjectCapabilityArea.FileBound) is { } refusal) return refusal;
        if (Text(reference, FilmKey) is not { } path) return "Не указан фильм";
        if (!FilmPaths.IsFilmPath(FilmPaths.Normalize(path) ?? ""))
            return "Фильм должен лежать в папке video/<имя фильма>/ и называться *.film";
        return ProjectLinkGuard.ResolveInside(project.RootPath, path) is { } full && File.Exists(full)
            ? null
            : "Фильм не найден в проекте";
    }

    public ContextItemSummary Describe(ContextScope scope, ContextItem item)
    {
        if (item.Kind == FilmKind)
        {
            var path = Text(item.Ref, FilmKey);
            if (path is null) return new ContextItemSummary("фильм недоступен", null, null, true);
            var exists = scope.Project is { } project
                && FilmPaths.IsFilmPath(FilmPaths.Normalize(path) ?? "")
                && ProjectCapabilityGuard.Allows(project, ProjectCapabilityArea.FileBound)
                && ProjectLinkGuard.ResolveInside(project.RootPath, path) is { } full && File.Exists(full);
            return new ContextItemSummary(FilmName(path), null, null, !exists);
        }
        if (Text(item.Ref, SceneKey) is not { } sceneId || Find(scope, sceneId) is not { } scene)
            return new ContextItemSummary("сцена недоступна", null, null, true);
        var current = scene.Versions.FirstOrDefault(v => v.VersionId == scene.CurrentVersionId);
        // «сцена 3 · утро-в-горах»: номер строки в фильме и имя фильма; вне фильма — имя сцены, у неснятой — «Сцена 1 · черновик»
        var label = scene.FilmRef is { } film ? $"сцена {film.Position + 1} · {FilmName(film.Path)}"
            : scene.Versions.Count == 0 ? $"{scene.Name} · черновик" : scene.Name;
        return new ContextItemSummary(label, current is null ? null : $"v{current.Number}", null, false);
    }

    // Кадры принимает основная сцена; op == null — таблица по всем операциям (из неё считается usedBy)
    public IReadOnlyList<ContextRoleSpec> AcceptedRefs(ContextScope scope, ContextItem primary, string? op) =>
        primary.Kind != SceneKind || op is not null && op != VideoContextOps.Shoot ? [] : RolesOf();

    internal static IReadOnlyList<ContextRoleSpec> RolesOf() =>
    [
        new(VideoContextRoles.FrameA, "Кадр A", ["image", ProjectFileContextKind.Kind], [VideoContextOps.Shoot]),
        new(VideoContextRoles.FrameB, "Кадр B", ["image", ProjectFileContextKind.Kind], [VideoContextOps.Shoot]),
    ];

    // «Чем»: сцена — поставщик и модель из настроек сцены, затем префов области; без выбора — «Авто».
    // Фильм собирается ffmpeg без ИИ
    public string? DescribeExecutor(ContextScope scope, ContextItem primary)
    {
        if (primary.Kind == FilmKind) return "сборка ffmpeg · без ИИ";
        if (primary.Kind != SceneKind || Text(primary.Ref, SceneKey) is not { } sceneId) return null;
        var settings = Find(scope, sceneId)?.Settings;
        var chosen = prefs?.Get(scope.OwnerId, VideoEditScope.Of(scope.Session));
        var providerKey = settings?.Provider ?? chosen?.Provider;
        var modelId = settings?.Model ?? chosen?.Model;
        var provider = VideoCatalog.IsAuto(providerKey) ? null : providerKey!.Trim();
        var engine = provider is null ? null
            : engines?.FirstOrDefault(e => string.Equals(e.Key, provider, StringComparison.OrdinalIgnoreCase));
        var model = VideoCatalog.IsAuto(modelId) ? null : engine?.Models.FirstOrDefault(m => m.Id == modelId!.Trim());
        return string.Join(" · ",
            provider is null ? VideoCatalog.AutoModelLabel : engine?.Label ?? provider,
            VideoCatalog.IsAuto(modelId) ? VideoCatalog.AutoModelLabel : model?.Label ?? modelId!);
    }

    // «Формат» (ADR-025): желание сцены под модель её выбора — то, что уйдёт в съёмку; у сцены фильма — замок
    // пропорций. Строка и в режиме «Чат», с правилом «не передавай aspect/quality без просьбы человека»
    public string? DescribeFormat(ContextScope scope, ContextItem primary)
    {
        if (primary.Kind != SceneKind || Text(primary.Ref, SceneKey) is not { } sceneId || Find(scope, sceneId) is not { } scene)
            return null;
        var chosen = prefs?.Get(scope.OwnerId, VideoEditScope.Of(scope.Session));
        var wish = scene.Settings.Format;
        if (wish is null && (scene.Settings.Aspect ?? chosen?.Aspect) is { } legacyAspect)
            wish = new MediaFormat(FormatModes.Aspect, legacyAspect, null, null, null, ContextActor.Human, DateTime.UtcNow);
        wish ??= chosen?.Format;
        var providerKey = scene.Settings.Provider ?? chosen?.Provider;
        var modelId = scene.Settings.Model ?? chosen?.Model;
        var engine = VideoCatalog.IsAuto(providerKey) ? null
            : engines?.FirstOrDefault(e => string.Equals(e.Key, providerKey!.Trim(), StringComparison.OrdinalIgnoreCase));
        var model = VideoCatalog.IsAuto(modelId) ? null : engine?.Models.FirstOrDefault(m => m.Id == modelId!.Trim());
        var caps = model?.Caps.Format;
        if (wish is { Mode: FormatModes.Aspect, Tier: null } && engine is not null && model is not null)
            wish = wish with { Tier = VideoFormatCatalog.DefaultTier(engine.Key, model.Id) };
        var line = "формат сцены — " + MediaFormatText.Line(wish, MediaFormatResolver.Resolve(caps, wish), model is not null);
        if (scene.LockedBy is { } locked)
            line += $"; сцена фильма «{FilmName(locked.FilmPath)}» — пропорции фильма {locked.Aspect}, смена без force даёт film_format_locked";
        if (scene.Settings.FrameFit?.Anchor is { } anchor)
            line += $"; кадры A и B подрезаются под формат, рамка: {anchor}";
        return line;
    }

    // Из фокуса «Видео»: сцена в работе, а без неё открытый фильм
    public ContextItem? SeedPrimary(ContextScope scope)
    {
        var focus = store.Get(scope.OwnerId, scope.Session.Id).Focus;
        if (focus.SceneId is { } sceneId && Find(scope, sceneId) is not null)
            return new ContextItem("seed_video_scene", SceneKind, new JsonObject { [SceneKey] = sceneId }, null,
                ContextActor.Human, scope.Session.CreatedAt);
        if (focus.FilmPath is { } path && scope.Project is not null && FilmPaths.IsFilmPath(FilmPaths.Normalize(path) ?? ""))
            return new ContextItem("seed_video_film", FilmKind, new JsonObject { [FilmKey] = path }, null,
                ContextActor.Human, scope.Session.CreatedAt);
        return null;
    }

    private Contracts.VideoSceneDto? Find(ContextScope scope, string sceneId) =>
        store.Get(scope.OwnerId, scope.Session.Id).Scenes.FirstOrDefault(s => s.SceneId == sceneId);

    private static string FilmName(string path) => Path.GetFileNameWithoutExtension(path.Replace('\\', '/'));

    private static string? Text(JsonObject reference, string name) =>
        reference[name] is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s) ? s : null;
}
