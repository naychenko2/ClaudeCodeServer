using System.Collections.Concurrent;
using System.Text.Json;

namespace ClaudeHomeServer.Services.Composition;

// Файл тумблеров панели «Компоненты»: data/subsystems.json — писаемая часть конфигурации
// подсистем (S2c-1). Program.cs подключает его источником конфигурации ПОСЛЕ
// appsettings.Local.json (значение, записанное панелью, перекрывает ручную настройку в
// Local.json — обратного нет: этот файл переписывает сервер), с reloadOnChange — чтобы
// GET /api/admin/subsystems показывал свежий Enabled без рестарта процесса. Сам гейт
// применения остаётся одноразовым (ADR-014): тумблер действует после перезапуска.
//
// Почему отдельный файл, а не appsettings.Local.json: Local.json правится руками и может
// содержать комментарии — переписывание его JSON-парсером их теряет; этот файл пишет
// только сервер, поэтому потеря нечего. data/ переживает выкатку и едет в бэкап
// (BackupCore архивирует каталог data/ целиком, отдельного списка файлов нет).
//
// Формат: { "Subsystems": { "<key>": { "Enabled": false } } } — та же форма, которую читает
// SubsystemGate (`Subsystems:{Key}:Enabled`), поэтому файл сам по себе валидная конфигурация.
public static class SubsystemOverridesFile
{
    public const string FileName = "subsystems.json";

    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

    // Замок на путь файла, а не на экземпляр контроллера: два PUT к разным ключам приходят
    // в один файл, и без замка чтение второго начинается до записи первого — одно из
    // значений теряется. Словарь статический: в продовом процессе путь один; в тестах
    // несколько хостов на разных TempDir — у каждого свой замок, и это правильно
    // (общего файла между ними нет).
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> _locks =
        new(StringComparer.OrdinalIgnoreCase);

    // Путь файла: тот же каталог данных, что у projects.json (ResolveDataDir — общая форма
    // от DataPath), отличается только имя файла.
    public static string PathFor(IConfiguration config) =>
        Path.Combine(SubsystemHostingExtensions.ResolveDataDir(config), FileName);

    // Текущее содержимое: ключ → Enabled. Ключи без учёта регистра — как их видит провайдер
    // конфигурации и SubsystemStateStore: «TTS» в файле и «tts» из панели — один ключ.
    // Нет файла = пустой документ (дефолт — всё включено). Битый файл или неверная форма —
    // SubsystemOverridesFileException: вызывающий отвечает 503, а не 500, и файл не тронут
    // (чтение происходит до записи).
    public static Dictionary<string, bool> ReadEnabled(string path)
    {
        if (!File.Exists(path))
            return new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        using var doc = Parse(path);
        return Extract(doc.RootElement, path);
    }

    // Чтение-правка-запись под замком пути: меняет/добавляет значение ключа, остальные
    // строки сохраняет. Ключ из запроса с другим регистром, чем в файле, обновляет
    // существующую запись, не плодит вторую.
    public static void WriteEnabled(string path, string key, bool enabled)
    {
        if (string.IsNullOrWhiteSpace(key))
            throw new ArgumentException("Ключ подсистемы пуст — писать нечего.", nameof(key));

        var lockObj = _locks.GetOrAdd(Path.GetFullPath(path), _ => new SemaphoreSlim(1, 1));
        lockObj.Wait();
        try
        {
            var values = ReadEnabled(path); // битый файл бросает здесь — оригинал не тронут
            values[key] = enabled;

            var payload = new
            {
                // Порядок ключей в файле — по алфавиту: файл смотрит человек, diff должен
                // быть читаемым.
                Subsystems = values
                    .OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(kv => kv.Key, kv => new { Enabled = kv.Value }),
            };
            var json = JsonSerializer.Serialize(payload, WriteOptions);

            // Запись: temp с уникальным именем + File.Move(overwrite) — читающий никогда не
            // видит полфайла, а неудачная запись оставляет оригинал как был.
            var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(temp, json);
                File.Move(temp, path, overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                try { File.Delete(temp); } catch { /* temp — не предмет проверки */ }
                throw new SubsystemOverridesFileException($"Не удалось записать {path}: {ex.Message}", ex);
            }
        }
        finally
        {
            lockObj.Release();
        }
    }

    private static JsonDocument Parse(string path)
    {
        try
        {
            return JsonDocument.Parse(File.ReadAllText(path));
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            throw new SubsystemOverridesFileException($"Не удалось прочитать {path}: {ex.Message}", ex);
        }
    }

    private static Dictionary<string, bool> Extract(JsonElement root, string path)
    {
        var values = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        var section = FindProperty(root, "Subsystems");
        if (section is null)
            return values; // секции нет — ничего не переопределено
        if (section.Value.ValueKind != JsonValueKind.Object)
            throw new SubsystemOverridesFileException($"Секция \"Subsystems\" в {path} — не объект.");

        foreach (var entry in section.Value.EnumerateObject())
        {
            var enabled = FindProperty(entry.Value, "Enabled");
            if (enabled is null || enabled.Value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                throw new SubsystemOverridesFileException(
                    $"Запись \"{entry.Name}\" в {path} без поля Enabled (true|false).");
            values[entry.Name] = enabled.Value.GetBoolean();
        }

        return values;
    }

    // Поиск свойства без учёта регистра: провайдер конфигурации регистронезависим, и чтение
    // обязано видеть то же, что видит гейт.
    private static JsonElement? FindProperty(JsonElement obj, string name)
    {
        foreach (var property in obj.EnumerateObject())
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                return property.Value;
        return null;
    }
}

// Сбой чтения или записи файла тумблеров. Причина уходит в 503 (code=write_failed) —
// её показывает строка панели: человек должен отличить «файл битый» от «сервер упал».
public sealed class SubsystemOverridesFileException : Exception
{
    public SubsystemOverridesFileException(string message) : base(message) { }
    public SubsystemOverridesFileException(string message, Exception cause) : base(message, cause) { }
}
