using System.Text.Json;
using ClaudeHomeServer.Services.VideoEditor.Contracts;
using ClaudeHomeServer.Services.VideoEditor.Scenes;

namespace ClaudeHomeServer.Services.VideoEditor.Prefs;

// Выбор человека в области: data/video-editor-prefs/{ownerId}/{scopeKey}.json — одна запись VideoPrefsDto
// (у видео нет режимов, как у звука). null у поля — человек его не настраивал, берётся умолчание каталога.
// Добавлять поля только аддитивно: файл живёт в бэкапе. Нет файла или он битый — пустые префы
public sealed class VideoPrefsStore(string root)
{
    public const string DirName = "video-editor-prefs";

    private readonly Lock _gate = new();

    public string Root { get; } = root;

    public static VideoPrefsStore FromConfig(IConfiguration config)
    {
        var dataPath = config["DataPath"] ?? Path.Combine(AppContext.BaseDirectory, "data", "projects.json");
        return new VideoPrefsStore(Path.Combine(Path.GetDirectoryName(dataPath)!, DirName));
    }

    public static VideoPrefsDto Empty { get; } = new(null, null, null, null, null, null);

    public VideoPrefsDto Get(string ownerId, string scopeKey)
    {
        lock (_gate) return Read(PathOf(ownerId, scopeKey));
    }

    // Через временный файл: оборванная запись не оставит битый JSON
    public void Save(string ownerId, string scopeKey, VideoPrefsDto prefs)
    {
        if (prefs.Count is { } count && (count < 1 || count > VideoThreadStore.MaxCount))
            throw new ArgumentException($"Число вариантов — от 1 до {VideoThreadStore.MaxCount}", nameof(prefs));
        if (prefs.DurationSec is <= 0)
            throw new ArgumentException("Длительность должна быть положительной", nameof(prefs));
        var path = PathOf(ownerId, scopeKey);
        lock (_gate)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(prefs, VideoThreadStore.Json));
            File.Move(tmp, path, overwrite: true);
        }
    }

    public string PathOf(string ownerId, string scopeKey) =>
        Path.Combine(Root, Safe(ownerId), Safe(scopeKey) + ".json");

    private static VideoPrefsDto Read(string path)
    {
        try
        {
            if (File.Exists(path) && JsonSerializer.Deserialize<VideoPrefsDto>(File.ReadAllText(path), VideoThreadStore.Json) is { } prefs)
                return prefs;
        }
        catch (Exception ex) when (ex is IOException or JsonException) { }
        return Empty;
    }

    // id владельца — из claim, области — из маршрута; маршрут приходит снаружи, отсюда белый список
    private static string Safe(string segment)
    {
        if (string.IsNullOrWhiteSpace(segment) || segment.Contains("..")
            || segment.IndexOfAny(['/', '\\', ':']) >= 0 || segment.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new ArgumentException("Недопустимый идентификатор", nameof(segment));
        return segment;
    }
}

// Префы области поверх хранилища. Область — уже своя: владение проверяет вызывающий (ручка, тулсет).
// Настройки новой сцены наследуют префы: выбор поставщика, модели, длительности, пропорций, звука и числа
public sealed class VideoPrefsService(VideoPrefsStore store)
{
    public VideoPrefsDto Get(string ownerId, VideoEditScope scope) => store.Get(ownerId, scope.Key);

    public void Save(string ownerId, VideoEditScope scope, VideoPrefsDto prefs) => store.Save(ownerId, scope.Key, prefs);

    public VideoSceneSettingsDto ForNewScene(string ownerId, VideoEditScope scope)
    {
        var p = store.Get(ownerId, scope.Key);
        return new VideoSceneSettingsDto(null, null, "", p.Provider, p.Model, p.DurationSec, p.Aspect, p.Sound, p.Count);
    }
}
