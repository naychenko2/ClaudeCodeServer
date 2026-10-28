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
    };

    private sealed record LogLine(
        DateTimeOffset Ts, string Op, string Section, IReadOnlyList<string> Pages, string? Source, string Actor);

    /// <summary>Безопасная форма ключа раздела для имени файла манифеста: «/» в ключе —
    /// разделитель ключа, не папка: манифесты всех разделов лежат в одной директории.
    /// «/» → «__»: форма обратима («__» → «/», одиночный «_» в id сохраняется) и не
    /// сталкивается с ключом, где «_» есть в id. «projects/{id}» → «projects__{id}»;
    /// «common» и «personal» — без изменений.</summary>
    public static string SafeFileName(string sectionKey) => sectionKey.Replace("/", "__");

    /// <summary>Манифест раздела: страница → sha256 (hex в нижнем регистре).
    /// Манифеста нет — пустая карта: хранилище до первого применения. Невалидный
    /// файл — исключение: сверка с испорченным манифестом не должна проходить молча.</summary>
    public static Dictionary<string, string> Read(string root, string sectionKey)
    {
        var text = WikiFileWriter.Read(root, ManifestRelPath(sectionKey));
        if (string.IsNullOrWhiteSpace(text))
            return new Dictionary<string, string>();
        return JsonSerializer.Deserialize<Dictionary<string, string>>(text) ?? new Dictionary<string, string>();
    }

    /// <summary>Записать хэши применённых страниц в манифест раздела: чтение → правка
    /// карты → атомарная запись. Ключи существующих страниц перезаписываются новыми
    /// хэшами, чужие записи манифеста не трогаются; другие разделы — другие файлы.</summary>
    public static void RecordApplied(string root, string sectionKey, IReadOnlyDictionary<string, string> pageHashes)
    {
        var manifest = Read(root, sectionKey);
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
    /// файла. ts — время сервера (ISO-8601, UTC).</summary>
    public static void AppendLog(
        string root, string op, string section, IReadOnlyList<string> pages, string? source, string actor)
    {
        var full = SafePath.Join(root, LogRelPath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        var line = JsonSerializer.Serialize(
            new LogLine(DateTimeOffset.UtcNow, op, section, pages, source, actor), JsonOpts);
        File.AppendAllText(full, line + "\n");
    }

    /// <summary>sha256 содержимого — hex в нижнем регистре. Одна форма хэша на всю вики:
    /// манифест, baseHashes пакета и сверка диска должны сравнивать одно и то же.</summary>
    public static string Sha256Hex(string content) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content))).ToLowerInvariant();

    private static string ManifestRelPath(string sectionKey) =>
        $"{ManifestDir}/{SafeFileName(sectionKey)}.json";
}
