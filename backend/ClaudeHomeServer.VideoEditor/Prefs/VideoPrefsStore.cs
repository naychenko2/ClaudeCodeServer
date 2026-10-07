using System.Text.Json;
using ClaudeHomeServer.Services.ChatContext;
using ClaudeHomeServer.Services.Composition;
using ClaudeHomeServer.Services.Media;
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
        if (VideoSceneFormat.Validate(prefs.Format) is { } badFormat)
            throw new ArgumentException(badFormat, nameof(prefs));
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
public sealed class VideoPrefsService(
    VideoPrefsStore store,
    ISessionBroadcaster? broadcaster = null,
    ILogger<VideoPrefsService>? log = null)
{
    public VideoPrefsDto Get(string ownerId, VideoEditScope scope) => store.Get(ownerId, scope.Key);

    // Запись человека (ручка PUT prefs) и агента (by). Format в теле: нет — остаётся сохранённым (старый фронт пишет только
    // Aspect, тогда оно становится желанием), есть — заменяется; автора и время ставит сервер. Aspect — зеркало Format.Aspect
    public VideoPrefsDto Save(string ownerId, VideoEditScope scope, VideoPrefsDto prefs, ContextActor by = ContextActor.Human)
    {
        var saved = store.Get(ownerId, scope.Key);
        var format = prefs.Format;
        if (format is null && saved.Format is { } kept)
            format = prefs.Aspect is { } aspect && aspect != kept.Aspect ? kept with { Mode = FormatModes.Aspect, Aspect = aspect } : kept;
        if (format is not null && format != saved.Format) format = VideoSceneFormat.Stamp(format, by, DateTime.UtcNow);
        var result = prefs with
        {
            Format = format,
            Aspect = format is { Mode: FormatModes.Aspect } ? format.Aspect : prefs.Aspect,
        };
        store.Save(ownerId, scope.Key, result);
        Broadcast(ownerId, scope.Key, result);
        return result;
    }

    // Единая точка рассылки: любая запись префов (человек, агент) уходит во все вкладки владельца событием
    // video_prefs_changed. Сбой рассылки запись не откатывает
    private void Broadcast(string ownerId, string scopeKey, VideoPrefsDto prefs)
    {
        if (broadcaster is null) return;
        try
        {
            _ = broadcaster.ToOwner(ownerId, new VideoPrefsChangedMessage(scopeKey, prefs))
                .ContinueWith(t => log?.LogDebug(t.Exception, "Видео: префы области {ScopeKey} не разосланы", scopeKey),
                    TaskContinuationOptions.OnlyOnFaulted);
        }
        catch (Exception ex)
        {
            log?.LogDebug(ex, "Видео: префы области {ScopeKey} не разосланы", scopeKey);
        }
    }

    // Новая сцена засевает своё желание из формата проекта; Aspect — его зеркало
    public VideoSceneSettingsDto ForNewScene(string ownerId, VideoEditScope scope)
    {
        var p = store.Get(ownerId, scope.Key);
        return new VideoSceneSettingsDto(null, null, "", p.Provider, p.Model, p.DurationSec, p.Aspect, p.Sound, p.Count, p.Format);
    }
}
