using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ClaudeHomeServer.Services.Notes.Wiki;

// Манифест раздела и журнал изменений Хранителя (ADR-035, решение 4):
// keeper/manifest/{section}.json — страница → sha256 содержимого после применения;
// keeper/log.jsonl — журнал: строки дописываются, не переписываются (между бэкапами
// откат держат прообразы и журнал, решение 13).
// Манифест читается и пишется через WikiFileWriter: наполовину записанный манифест дал бы
// ложный drift при сверке диска перед применением (пункт 2 транзакции). Журнал — append:
// его перезапись потеряла бы историю.
// Ключи записей манифеста — путь страницы относительно pages/ (тот же контракт, что у
// WikiSections.Parse). root — корень вики владельца (WikiPaths.Root); пути — только через
// SafePath.Join внутри WikiFileWriter.
public static class WikiManifest
{
    private const string ManifestDir = "keeper/manifest";
    private const string LogRelPath = "keeper/log.jsonl";

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        // Форма строк журнала — как в ADR: {ts, op, section, pages, source, actor}.
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        // Имена страниц и карточки источников — по-русски: «О…» в файле, который человек
        // читает при откате (решение 13), нечитаем. Тот же приём, что в WikiPackageStore
        // (класс правки «origin × op»).
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    // Policy — политика применения пакета: «auto» | «inbox» (решение 12г, мера 6: журнал
    // показывает «само» или «Входящие»). null для системных операций (adopt): у них нет
    // политики, есть только автор. Пустым полем не заменяется: «не указано» и «auto» — разные
    // утверждения.
    private sealed record LogLine(
        DateTimeOffset Ts, string Op, string Section, IReadOnlyList<string> Pages, string? Source, string Actor, string? Policy);

    /// <summary>Безопасная форма ключа раздела для имени файла манифеста: «/» в ключе —
    /// разделитель ключа, не папка: манифесты всех разделов лежат в одной директории.
    /// «/» → «__»: форма обратима («__» → «/», одиночный «_» в id сохраняется) и не
    /// сталкивается с ключом, где «_» есть в id. «projects/{id}» → «projects__{id}»;
    /// «common» и «personal» — без изменений.</summary>
    public static string SafeFileName(string sectionKey) => sectionKey.Replace("/", "__");

    /// <summary>Прочитать манифест раздела: страница → sha256 (hex в нижнем регистре).
    /// Различает три состояния файла на диске (см. WikiManifestState): файла нет —
    /// норма до первого применения; валиден — карта; пустой или битый — сигнал
    /// «Invalid», не исключение: фоновый читатель не должен падать, а испорченный
    /// манифест не должен притворяться «не применено ничего» (ревью S2b-1, M1;
    /// ADR-035:325). Вызывающий на Invalid: ничего не применяет, строка в _health.md.</summary>
    public static WikiManifestReadResult TryRead(string root, string sectionKey)
    {
        var text = WikiFileWriter.Read(root, ManifestRelPath(sectionKey));
        if (text is null)
            return new WikiManifestReadResult(WikiManifestState.Missing, new Dictionary<string, string>());
        if (string.IsNullOrWhiteSpace(text))
            return new WikiManifestReadResult(WikiManifestState.Invalid, new Dictionary<string, string>());
        try
        {
            var map = JsonSerializer.Deserialize<Dictionary<string, string>>(text);
            if (map is not null)
                return new WikiManifestReadResult(WikiManifestState.Valid, map);
        }
        catch (JsonException)
        {
            // Битый JSON — сигнал «не применять», а не исключение наружу: необработанное
            // исключение фонового читателя останавливает хост (ADR-035:325).
        }
        return new WikiManifestReadResult(WikiManifestState.Invalid, new Dictionary<string, string>());
    }

    /// <summary>Записать хэши применённых страниц в манифест раздела: чтение → правка
    /// карты → атомарная запись. Ключи существующих страниц перезаписываются новыми
    /// хэшами, чужие записи манифеста не трогаются; другие разделы — другие файлы.
    /// На невалидном манифесте — отказ: слить новую карту с нечитаемой и перезаписать
    /// битый файл означало бы молча стереть его записи (тот же fail-closed).</summary>
    public static void RecordApplied(string root, string sectionKey, IReadOnlyDictionary<string, string> pageHashes)
    {
        var existing = TryRead(root, sectionKey);
        if (existing.State == WikiManifestState.Invalid)
            throw new InvalidOperationException($"Манифест раздела «{sectionKey}» невалиден — запись в него невозможна");
        var manifest = new Dictionary<string, string>(existing.Manifest);
        foreach (var (page, hash) in pageHashes)
            manifest[page] = hash;
        // Порядок ключей фиксированный: манифест — данные на диске и в бэкапе, его diff
        // между применениями должен быть читаемым, а не зависеть от обхода словаря.
        var sorted = new Dictionary<string, string>();
        foreach (var kv in manifest.OrderBy(kv => kv.Key, StringComparer.Ordinal))
            sorted[kv.Key] = kv.Value;
        WikiFileWriter.Write(root, ManifestRelPath(sectionKey), JsonSerializer.Serialize(sorted, JsonOpts));
    }

    /// <summary>Дописать строку журнала. Строки не переписываются: append, не перезапись
    /// файла. ts — время сервера (ISO-8601, UTC). policy — «auto» | «inbox» для строки
    /// применения (мера 6); для системных операций — null.</summary>
    public static void AppendLog(
        string root, string op, string section, IReadOnlyList<string> pages, string? source, string actor,
        string? policy = null)
    {
        var full = SafePath.Join(root, LogRelPath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        var line = JsonSerializer.Serialize(
            new LogLine(DateTimeOffset.UtcNow, op, section, pages, source, actor, policy), JsonOpts);
        File.AppendAllText(full, line + "\n");
    }

    /// <summary>sha256 содержимого — hex в нижнем регистре. Одна форма хэша на всю вики:
    /// манифест, baseHashes пакета и сверка диска должны сравнивать одно и то же.</summary>
    public static string Sha256Hex(string content) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content))).ToLowerInvariant();

    private static string ManifestRelPath(string sectionKey) =>
        $"{ManifestDir}/{SafeFileName(sectionKey)}.json";
}

/// <summary>Состояние файла манифеста на диске (ревью S2b-1, M1): три состояния
/// нельзя схлопнуть в два — пустой файл, притворившийся «не применено ничего»,
/// возвращал бы удалённые вручную страницы; битый бросал бы исключение в фоновую службу.</summary>
public enum WikiManifestState
{
    /// <summary>Манифеста нет: хранилище до первого применения — норма.</summary>
    Missing,
    /// <summary>Манифест валиден: карта страница → хэш читаема.</summary>
    Valid,
    /// <summary>Манифест невалиден: пустой (0 байт, одни пробелы) или битый JSON.
    /// Ничего не применяется; вызывающий пишет строку в _health.md (ADR-035:325).</summary>
    Invalid,
}

/// <summary>Результат чтения манифеста: состояние + карта. Карта значима только при Valid;
/// при Missing/Invalid она пуста — вызывающий решает по State, а не по содержимому карты.</summary>
public readonly record struct WikiManifestReadResult(
    WikiManifestState State,
    IReadOnlyDictionary<string, string> Manifest);
