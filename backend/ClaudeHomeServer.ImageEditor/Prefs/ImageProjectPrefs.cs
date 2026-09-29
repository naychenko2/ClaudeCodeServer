using System.Text.Json;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services.Composition;
using ClaudeHomeServer.Services.ImageEditor.Threads;

namespace ClaudeHomeServer.Services.ImageEditor.Prefs;

// Выбор человека в полосе «Картинки» без выбранной картинки: поставщик, модель, число вариантов,
// «размер оригинала» и подключённый персонаж. На владельца и проект; новая нить получает из них
// свои Settings, а запуск агентом без аргументов — поставщика, модель и персонажа.
// Добавлять поля можно только аддитивно: файл живёт в бэкапе.
public sealed record ImageProjectPrefs(string? Provider, string? Model, int Count, bool MatchSourceSize, string? CharacterSlug)
{
    public static ImageProjectPrefs Default { get; } = new(null, null, 2, true, null);

    public ImageThreadSettings ToThreadSettings() => new(Provider, Model, Count, MatchSourceSize);
}

// data/image-editor-prefs/{ownerId}/{projectId}.json. Нет файла или он битый — умолчания
public sealed class ImageProjectPrefsStore(string root)
{
    public const string DirName = "image-editor-prefs";

    private readonly Lock _gate = new();

    public string Root { get; } = root;

    public static ImageProjectPrefsStore FromConfig(IConfiguration config)
    {
        var dataPath = config["DataPath"] ?? Path.Combine(AppContext.BaseDirectory, "data", "projects.json");
        return new ImageProjectPrefsStore(Path.Combine(Path.GetDirectoryName(dataPath)!, DirName));
    }

    public ImageProjectPrefs Get(string ownerId, string projectId)
    {
        var path = PathOf(ownerId, projectId);
        lock (_gate)
        {
            try
            {
                if (File.Exists(path)
                    && JsonSerializer.Deserialize<ImageProjectPrefs>(File.ReadAllText(path), ImageThreadStore.Json) is { } prefs)
                    return prefs;
            }
            catch (Exception ex) when (ex is IOException or JsonException) { }
            return ImageProjectPrefs.Default;
        }
    }

    // Через временный файл: оборванная запись не оставит битый JSON
    public void Save(string ownerId, string projectId, ImageProjectPrefs prefs)
    {
        var path = PathOf(ownerId, projectId);
        lock (_gate)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(prefs, ImageThreadStore.Json));
            File.Move(tmp, path, overwrite: true);
        }
    }

    public string PathOf(string ownerId, string projectId) =>
        Path.Combine(Root, Safe(ownerId), Safe(projectId) + ".json");

    // id владельца — из claim, проекта — из маршрута; маршрут приходит снаружи, отсюда белый список
    private static string Safe(string segment)
    {
        if (string.IsNullOrWhiteSpace(segment) || segment.Contains("..")
            || segment.IndexOfAny(['/', '\\', ':']) >= 0 || segment.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new ArgumentException("Недопустимый идентификатор", nameof(segment));
        return segment;
    }
}

// Чтение с проверкой персонажа и запись с событием image_prefs_changed владельцу. Проект —
// уже свой: владение проверяет вызывающий (ручка, тулсет, блок хода)
public sealed class ImageProjectPrefsService(
    ImageProjectPrefsStore store,
    ILogger<ImageProjectPrefsService> log,
    IProjectManager? projects = null,
    ISessionBroadcaster? broadcaster = null)
{
    // Настройки для новой нити: персонаж в них не входит, проект открывать незачем
    public ImageThreadSettings SettingsFor(string ownerId, string projectId) =>
        store.Get(ownerId, projectId).ToThreadSettings();

    // Персонаж, которого уже нет в проекте (удалили), читается как null; у личной области
    // персонажей нет вовсе — всегда null
    public ImageProjectPrefs Get(string ownerId, ImageEditScope scope)
    {
        var prefs = store.Get(ownerId, scope.Key);
        return prefs.CharacterSlug is { } slug
               && (scope.Project is not { } project || CharacterStore.Get(project.RootPath, slug) is null)
            ? prefs with { CharacterSlug = null }
            : prefs;
    }

    public ImageProjectPrefs Get(string ownerId, Project project) => Get(ownerId, ImageEditScope.Of(project));

    // Для блока хода, где на руках только id проекта: чужой или пропавший проект — умолчания
    public ImageProjectPrefs Get(string ownerId, string projectId) =>
        projects?.GetById(projectId) is { } project && project.OwnerId == ownerId
            ? Get(ownerId, project)
            : ImageProjectPrefs.Default;

    // Проверка тела PUT …/prefs — общая для ручек проекта и личного чата; null — годится
    public static string? Validate(ImageProjectPrefs? req)
    {
        if (req is null || req.Count < 1 || req.Count > ImageEditCatalog.DefaultLimits.MaxCount)
            return $"Число вариантов — от 1 до {ImageEditCatalog.DefaultLimits.MaxCount}";
        if (req.CharacterSlug is { Length: > 0 } slug && !CharacterStore.IsValidSlug(slug.Trim()))
            return "Недопустимый персонаж";
        return null;
    }

    public Task<ImageProjectPrefs> SetAsync(string ownerId, Project project, ImageProjectPrefs prefs) =>
        SetAsync(ownerId, ImageEditScope.Of(project), prefs);

    // У личной области персонаж принудительно null: папки characters/ у неё нет
    public async Task<ImageProjectPrefs> SetAsync(string ownerId, ImageEditScope scope, ImageProjectPrefs prefs)
    {
        store.Save(ownerId, scope.Key, prefs with
        {
            Provider = Blank(prefs.Provider),
            Model = Blank(prefs.Model),
            CharacterSlug = scope.Project is null ? null : Blank(prefs.CharacterSlug),
        });
        var saved = Get(ownerId, scope);
        if (broadcaster is not null)
        {
            try
            {
                await broadcaster.ToOwner(ownerId, new ImageProjectPrefsChangedMessage(scope.Key, saved));
            }
            catch (Exception ex)
            {
                // Потерянное событие фронт догоняет GET …/prefs
                log.LogDebug(ex, "Редактор картинок: настройки области {ScopeKey} не разосланы", scope.Key);
            }
        }
        return saved;
    }

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
