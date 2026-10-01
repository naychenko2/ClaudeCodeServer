using System.Text.Json;
using System.Text.Json.Nodes;
using ClaudeHomeServer.Services.AudioEditor.Threads;

namespace ClaudeHomeServer.Services.AudioEditor.Prefs;

// Выбор человека для режима (AudioModes.*) в области: операция, поставщик, модель, число вариантов
// и поля по умолчанию. Запись на режим отдельная: выбор в «Музыке» не трогает «Голос».
// null у поля — режим его не задаёт, берётся умолчание каталога. Добавлять поля только аддитивно:
// файл живёт в бэкапе
public sealed record AudioModePrefs(string? Operation, string? Provider, string? Model, int? Count, JsonObject? Fields)
{
    public const int MaxCount = 4;

    public static AudioModePrefs Empty { get; } = new(null, null, null, null, null);

    // Настройки новой нити режима — копия префов: поля нити правятся отдельно от префов
    public AudioThreadSettings ToThreadSettings(string mode) =>
        new(mode, Operation, Provider, Model, Fields?.DeepClone().AsObject(), Count);
}

// Итог цепочки для запуска: всё разрешено, Count в пределах 1..MaxCount
public sealed record AudioLaunchSettings(string Mode, string? Operation, string? Provider, string? Model, int Count, JsonObject Fields);

// Цепочка разрешения при запуске (решение 2026-10-01): настройки нити → префы режима → умолчание
// каталога. Скалярное поле берётся из первого источника, где оно задано; Fields сливаются по ключам
// в том же порядке старшинства. Настройки нити другого режима не в счёт: они про другую операцию
public static class AudioPrefsResolver
{
    public static AudioLaunchSettings Resolve(string mode, AudioThreadSettings? thread, AudioModePrefs? prefs,
        AudioModePrefs catalogDefault)
    {
        if (!AudioModes.IsValid(mode)) throw new ArgumentException($"Неизвестный режим: {mode}", nameof(mode));
        var own = thread?.Mode == mode ? thread : null;
        var count = own?.Count ?? prefs?.Count ?? catalogDefault.Count ?? 1;
        return new AudioLaunchSettings(
            mode,
            own?.Operation ?? prefs?.Operation ?? catalogDefault.Operation,
            own?.Provider ?? prefs?.Provider ?? catalogDefault.Provider,
            own?.Model ?? prefs?.Model ?? catalogDefault.Model,
            Math.Clamp(count, 1, AudioModePrefs.MaxCount),
            Merge(catalogDefault.Fields, prefs?.Fields, own?.Fields));
    }

    // Младший источник первым, старший перезаписывает ключи; узлы копируются — у JsonNode один родитель
    private static JsonObject Merge(params JsonObject?[] layers)
    {
        var merged = new JsonObject();
        foreach (var layer in layers)
            if (layer is not null)
                foreach (var (key, value) in layer)
                    merged[key] = value?.DeepClone();
        return merged;
    }
}

// data/audio-editor-prefs/{ownerId}/{scopeKey}.json: { "voice": {…}, "music": {…}, "process": {…} }.
// Нет файла, записи режима или файл битый — null (умолчание каталога)
public sealed class AudioPrefsStore(string root)
{
    public const string DirName = "audio-editor-prefs";

    private readonly Lock _gate = new();

    public string Root { get; } = root;

    public static AudioPrefsStore FromConfig(IConfiguration config)
    {
        var dataPath = config["DataPath"] ?? Path.Combine(AppContext.BaseDirectory, "data", "projects.json");
        return new AudioPrefsStore(Path.Combine(Path.GetDirectoryName(dataPath)!, DirName));
    }

    public AudioModePrefs? Get(string ownerId, string scopeKey, string mode)
    {
        Require(mode);
        lock (_gate) return Read(PathOf(ownerId, scopeKey)).GetValueOrDefault(mode);
    }

    // Пишется только запись режима, остальные режимы файла остаются как были. Через временный
    // файл: оборванная запись не оставит битый JSON
    public void Save(string ownerId, string scopeKey, string mode, AudioModePrefs prefs)
    {
        Require(mode);
        if (prefs.Count is { } count && (count < 1 || count > AudioModePrefs.MaxCount))
            throw new ArgumentException($"Число вариантов — от 1 до {AudioModePrefs.MaxCount}", nameof(prefs));
        var path = PathOf(ownerId, scopeKey);
        lock (_gate)
        {
            var all = Read(path);
            all[mode] = prefs;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(all, AudioThreadStore.Json));
            File.Move(tmp, path, overwrite: true);
        }
    }

    public string PathOf(string ownerId, string scopeKey) =>
        Path.Combine(Root, Safe(ownerId), Safe(scopeKey) + ".json");

    private static Dictionary<string, AudioModePrefs> Read(string path)
    {
        try
        {
            if (File.Exists(path)
                && JsonSerializer.Deserialize<Dictionary<string, AudioModePrefs>>(File.ReadAllText(path), AudioThreadStore.Json) is { } all)
                return new Dictionary<string, AudioModePrefs>(all.Where(p => AudioModes.IsValid(p.Key) && p.Value is not null));
        }
        catch (Exception ex) when (ex is IOException or JsonException) { }
        return [];
    }

    private static void Require(string mode)
    {
        if (!AudioModes.IsValid(mode)) throw new ArgumentException($"Неизвестный режим: {mode}", nameof(mode));
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

// Префы области и цепочка запуска поверх хранилища. Область — уже своя: владение проверяет
// вызывающий (ручка, тулсет)
public sealed class AudioPrefsService(AudioPrefsStore store)
{
    public AudioModePrefs? Get(string ownerId, AudioEditScope scope, string mode) => store.Get(ownerId, scope.Key, mode);

    public void Save(string ownerId, AudioEditScope scope, string mode, AudioModePrefs prefs) =>
        store.Save(ownerId, scope.Key, mode, prefs);

    // Настройки новой нити режима — последние префы этого режима; префов нет — null, нить
    // тогда при запуске целиком идёт на умолчание каталога
    public AudioThreadSettings? ForNewThread(string ownerId, AudioEditScope scope, string mode) =>
        store.Get(ownerId, scope.Key, mode)?.ToThreadSettings(mode);

    // catalogDefault — умолчание каталога операции; каталога пока нет, его передаёт вызывающий
    public AudioLaunchSettings Resolve(string ownerId, AudioEditScope scope, string mode, AudioThreadSettings? thread,
        AudioModePrefs catalogDefault) =>
        AudioPrefsResolver.Resolve(mode, thread, store.Get(ownerId, scope.Key, mode), catalogDefault);
}
