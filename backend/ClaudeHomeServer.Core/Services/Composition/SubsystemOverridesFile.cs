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
//
// Битый файл не роняет старт процесса: как обычный источник конфигурации он был бы
// прочитан на старте (JsonConfigurationProvider.Load бросает на невалидном JSON, и инстанс
// не поднимался бы вовсе), а ручка на живом процессе на том же файле отвечает 503 с
// причиной. Старт дороже: Program.cs спрашивает здесь DescribeConfigurationLoadError и
// пропускает такой источник (состав подсистем — по умолчанию).
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

    // Можно ли подключать файл как источник конфигурации: null — можно, иначе — причина отказа.
    // Спрашиваем ТЕМ ЖЕ путём, что им подключается (AddJsonFile + Build): правила парсера —
    // корень не объект, дублирующийся ключ — своей копией не повторяются, а «здесь» и «там»
    // обязаны отвечать одинаково, иначе пропуск источника был бы ложным. Построить
    // JsonConfigurationProvider напрямую — мимо EnsureDefaults: он враньёт «файл не найден»
    // на существующем файле (проверено пробой).
    // Ловим широко: старт процесса не должен зависеть от состояния файла — любая причина
    // здесь заканчивается одинаково (источник не подключён, состав по умолчанию, человек
    // видит причину в stderr). Нет файла — отказа нет: источник не нужен (optional).
    public static string? DescribeConfigurationLoadError(string path)
    {
        if (!File.Exists(path)) return null;
        try
        {
            new ConfigurationBuilder().AddJsonFile(path, optional: false).Build();
            return null;
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

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
            SweepStaleTemps(path); // хвосты прошлых падений процесса — до своей записи
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
            // видит полфайла, а неудачная запись оставляет оригинал как был. Падение процесса
            // между WriteAllText и File.Move оставляет temp в каталоге — его снимает
            // следующая запись (SweepStaleTemps выше).
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

    // Хвосты прошлых падений: temp с префиксом нашего файла остаётся в каталоге, если
    // процесс умер между WriteAllText и File.Move. Снимаем их под тем же замком пути и до
    // своего temp: живой записи по этому пути сейчас нет (замок), а в бою файл пишет только
    // этот процесс (InstanceLock не даёт второму инстансу тот же каталог) — значит любой
    // такой temp здесь мусор прошлого запуска. В бэкап он не попадает (`.tmp` отсекается),
    // но в data/ лежал бы вечно.
    private static void SweepStaleTemps(string path)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (string.IsNullOrEmpty(dir)) return;
        try
        {
            foreach (var stale in Directory.EnumerateFiles(dir, Path.GetFileName(path) + ".*.tmp"))
            {
                try { File.Delete(stale); } catch { /* уборка мусора — не предмет проверки */ }
            }
        }
        catch (Exception)
        {
            // Каталог недоступен: причину назовёт сама запись ниже (тот же путь, тот же
            // замок) — вторая копия той же причины в ответе панели не нужна.
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
        var section = FindProperty(root, "Subsystems", "Корень документа", path);
        if (section is null)
            return values; // секции нет — ничего не переопределено
        if (section.Value.ValueKind != JsonValueKind.Object)
            throw new SubsystemOverridesFileException($"Секция \"Subsystems\" в {path} — не объект.");

        foreach (var entry in section.Value.EnumerateObject())
        {
            var enabled = FindProperty(entry.Value, "Enabled", $"Запись \"{entry.Name}\"", path);
            if (enabled is null || enabled.Value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                throw new SubsystemOverridesFileException(
                    $"Запись \"{entry.Name}\" в {path} без поля Enabled (true|false).");
            values[entry.Name] = enabled.Value.GetBoolean();
        }

        return values;
    }

    // Поиск свойства без учёта регистра: провайдер конфигурации регистронезависим, и чтение
    // обязано видеть то же, что видит гейт.
    //
    // Проверка ValueKind обязательна: EnumerateObject() на не-объекте (корень «[]» или «5»,
    // значение записи «{"Subsystems":{"tts":5}}») бросает InvalidOperationException, который не
    // ловит ни catch в Parse (там JsonException/IOException), ни catch в контроллере — это был
    // бы 500 без причины. Инвариант ручки: на содержимое файла ответ всегда 503 с code.
    private static JsonElement? FindProperty(JsonElement element, string name, string subject, string path)
    {
        if (element.ValueKind != JsonValueKind.Object)
            throw new SubsystemOverridesFileException($"{subject} в {path} — не объект.");

        foreach (var property in element.EnumerateObject())
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
