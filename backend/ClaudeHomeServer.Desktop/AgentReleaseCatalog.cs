using System.Text.Json;
using System.Text.RegularExpressions;
using ClaudeHomeServer.Protocol;

namespace ClaudeHomeServer.Services.Desktop;

/// <summary>
/// Файловая система каталога релизов. Шов нужен тестам раздачи: они доказывают, что строки
/// запроса не доходят до диска, — по списку путей, которых касался каталог.
/// </summary>
public interface IAgentReleaseFileSystem
{
    /// <summary>Время последней записи файла; null — файла нет.</summary>
    DateTime? LastWriteTimeUtc(string path);

    bool DirectoryExists(string path);

    /// <summary>Имена (не пути) подкаталогов; каталога нет — пусто.</summary>
    IEnumerable<string> DirectoryNames(string path);

    /// <summary>Текст файла; null — файла нет.</summary>
    string? ReadAllText(string path);

    Stream OpenRead(string path);
}

public sealed class DiskAgentReleaseFileSystem : IAgentReleaseFileSystem
{
    public DateTime? LastWriteTimeUtc(string path) => File.Exists(path) ? File.GetLastWriteTimeUtc(path) : null;

    public bool DirectoryExists(string path) => Directory.Exists(path);

    public IEnumerable<string> DirectoryNames(string path) =>
        Directory.Exists(path) ? Directory.EnumerateDirectories(path).Select(Path.GetFileName).OfType<string>() : [];

    public string? ReadAllText(string path) => File.Exists(path) ? File.ReadAllText(path) : null;

    public Stream OpenRead(string path) =>
        new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous);
}

/// <summary>Архив агента под один RID одной версии — ровно как его описал манифест.</summary>
public sealed record AgentReleaseArchive(string Version, string Rid, string File, long Size, string Sha256)
{
    /// <summary>Путь под <c>/agent/</c>: <c>{version}/{rid}/{file}</c>.</summary>
    public string RelativePath => $"{Version}/{Rid}/{File}";
}

/// <summary>Версия агента и её архивы по RID.</summary>
public sealed record AgentRelease(string Version, IReadOnlyDictionary<string, AgentReleaseArchive> Archives);

/// <summary>
/// Снимок каталога. <see cref="Latest"/> — версия, на которую указывает указатель текущей
/// выкатки; null — агент не раздаётся, причина — в <see cref="Problem"/> (текст для человека,
/// без путей сервера: его видит анонимный клиент).
/// </summary>
public sealed record AgentReleaseSnapshot(
    AgentRelease? Latest,
    IReadOnlyDictionary<string, AgentRelease> Versions,
    string? Problem,
    string? Root)
{
    public bool Served => Latest is not null;

    public static AgentReleaseSnapshot NotServed(string problem) =>
        new(null, new Dictionary<string, AgentRelease>(StringComparer.Ordinal), problem, null);
}

/// <summary>
/// Каталог релизов агента устройства (agent-distribution Р3, Р11). Архивы лежат вне
/// каталога приложения — в <see cref="ReleasesPathKey"/>, раскладка
/// <c>{version}/manifest.json</c> и <c>{version}/{archive}</c>; в каталоге приложения — только
/// указатель текущей выкатки <see cref="PointerFileName"/>. Формат указателя и манифеста один:
/// <code>
/// { "version": "1.123.0",
///   "archives": { "win-x64": { "file": "ai-home-agent-1.123.0-win-x64.zip", "size": 1, "sha256": "…64 hex…" } } }
/// </code>
///
/// Всё, что пришло с диска, проверяется: RID — из белого списка <see cref="DeviceAgentRids"/>
/// (незнакомые пропускаются), SHA-256 — 64 hex, имя архива — без разделителей пути, версия —
/// разбираемая <see cref="DeviceAgentVersion"/> и совпадает с именем каталога. Битый манифест
/// выпадает из каталога целиком. Указатель обязан совпасть с манифестом своей версии.
///
/// Снимок кэшируется до смены mtime указателя: выкатка пишет указатель последним. Строки
/// запроса скачивания в пути к диску не участвуют никогда — они только сравниваются с данными
/// манифеста (<see cref="FindArchive"/>), путь строит <see cref="OpenArchive"/> из самого манифеста.
/// </summary>
public sealed partial class AgentReleaseCatalog
{
    public const string ReleasesPathKey = "DeviceAgent:ReleasesPath";

    /// <summary>Путь указателя; не задан — <see cref="PointerFileName"/> рядом со сборкой сервера.</summary>
    public const string PointerPathKey = "DeviceAgent:ReleasePointerPath";

    public const string PointerFileName = "agent-release.json";
    public const string ManifestFileName = "manifest.json";

    public const string NotServedPrefix = "Сервер не раздаёт агента";

    private readonly IConfiguration _config;
    private readonly ILogger<AgentReleaseCatalog>? _log;
    private readonly IAgentReleaseFileSystem _fs;
    private readonly Lock _lock = new();
    private (string? Root, string Pointer, bool RootExists, DateTime? Stamp)? _cacheKey;
    private AgentReleaseSnapshot? _cached;

    public AgentReleaseCatalog(
        IConfiguration config, ILogger<AgentReleaseCatalog>? log = null, IAgentReleaseFileSystem? fileSystem = null)
    {
        _config = config;
        _log = log;
        _fs = fileSystem ?? new DiskAgentReleaseFileSystem();
    }

    /// <summary>Текущий снимок; перечитывается, только если сменился указатель или настройка.</summary>
    public AgentReleaseSnapshot Current()
    {
        var configured = _config[ReleasesPathKey];
        var root = string.IsNullOrWhiteSpace(configured) ? null : Path.GetFullPath(configured.Trim());
        var pointer = _config[PointerPathKey] is { Length: > 0 } p
            ? Path.GetFullPath(p)
            : Path.Combine(AppContext.BaseDirectory, PointerFileName);
        var rootExists = root is not null && _fs.DirectoryExists(root);
        var stamp = _fs.LastWriteTimeUtc(pointer);
        var key = (root, pointer, rootExists, stamp);

        lock (_lock)
        {
            if (_cached is not null && _cacheKey == key) return _cached;
            _cached = Load(root, pointer, rootExists, stamp);
            _cacheKey = key;
            return _cached;
        }
    }

    /// <summary>
    /// Архив по координатам из URL. Каждая строка запроса только СРАВНИВАЕТСЯ с данными
    /// каталога: RID — с белым списком, версия — с версиями каталога, имя — с именем архива в
    /// манифесте этой версии. null — любое несовпадение.
    /// </summary>
    public AgentReleaseArchive? FindArchive(string? version, string? rid, string? file)
    {
        if (!DeviceAgentRids.IsSupported(rid) || version is null || file is null) return null;
        var snapshot = Current();
        if (!snapshot.Versions.TryGetValue(version, out var release)) return null;
        if (!release.Archives.TryGetValue(rid!, out var archive)) return null;
        return string.Equals(archive.File, file, StringComparison.Ordinal) ? archive : null;
    }

    /// <summary>Открыть архив: путь собирается только из корня каталога и данных манифеста.</summary>
    public Stream OpenArchive(AgentReleaseArchive archive)
    {
        var root = Current().Root ?? throw new InvalidOperationException($"{NotServedPrefix}: каталог релизов не настроен");
        return _fs.OpenRead(Path.Combine(root, archive.Version, archive.File));
    }

    private AgentReleaseSnapshot Load(string? root, string pointerPath, bool rootExists, DateTime? stamp)
    {
        if (root is null)
            return AgentReleaseSnapshot.NotServed($"{NotServedPrefix}: каталог релизов не настроен");
        if (!rootExists)
        {
            _log?.LogWarning("Каталог релизов агента {Root} не найден — агент не раздаётся", root);
            return AgentReleaseSnapshot.NotServed($"{NotServedPrefix}: каталог релизов не найден");
        }
        if (stamp is null)
        {
            _log?.LogInformation("Указатель релиза агента {Pointer} не найден — агент не раздаётся", pointerPath);
            return AgentReleaseSnapshot.NotServed($"{NotServedPrefix}: релиз агента не опубликован");
        }

        var versions = new Dictionary<string, AgentRelease>(StringComparer.Ordinal);
        foreach (var dir in _fs.DirectoryNames(root))
        {
            // Имя каталога — ровно каноническая версия: иначе версия из URL и путь на диске
            // могли бы разъехаться
            if (!DeviceAgentVersion.TryParse(dir, out var v) || v.ToString() != dir) continue;
            var release = Parse(_fs.ReadAllText(Path.Combine(root, dir, ManifestFileName)), dir, $"манифест {dir}");
            if (release is not null) versions[dir] = release;
        }

        var pointer = Parse(_fs.ReadAllText(pointerPath), expectedVersion: null, "указатель");
        if (pointer is null)
            return new(null, versions, $"{NotServedPrefix}: указатель релиза повреждён", root);

        if (!versions.TryGetValue(pointer.Version, out var latest) || !SameArchives(pointer, latest))
        {
            _log?.LogWarning("Указатель релиза агента называет {Version}, но в каталоге такого манифеста нет или он другой",
                pointer.Version);
            return new(null, versions, $"{NotServedPrefix}: релиз {pointer.Version} не найден в каталоге", root);
        }

        _log?.LogInformation("Раздача агента: текущая версия {Version}, RID {Rids}, версий в каталоге {Count}",
            latest.Version, string.Join(", ", latest.Archives.Keys), versions.Count);
        return new(latest, versions, null, root);
    }

    private static bool SameArchives(AgentRelease a, AgentRelease b) =>
        a.Archives.Count == b.Archives.Count
        && a.Archives.All(kv => b.Archives.TryGetValue(kv.Key, out var other) && other == kv.Value);

    /// <summary>Манифест или указатель; null — повреждён (причина — в логе).</summary>
    private AgentRelease? Parse(string? json, string? expectedVersion, string what)
    {
        if (json is null)
        {
            if (expectedVersion is not null) _log?.LogWarning("Релиз агента: нет файла {What}", what);
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var versionText = root.GetProperty("version").GetString();
            if (!DeviceAgentVersion.TryParse(versionText, out var version) || version.ToString() != versionText)
                return Reject(what, $"версия «{versionText}» не каноническая");
            if (expectedVersion is not null && versionText != expectedVersion)
                return Reject(what, $"версия {versionText} не совпадает с каталогом {expectedVersion}");

            var archives = new Dictionary<string, AgentReleaseArchive>(StringComparer.Ordinal);
            var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in root.GetProperty("archives").EnumerateObject())
            {
                // Незнакомый RID — не ошибка: манифест будущей выкатки может знать больше платформ
                if (!DeviceAgentRids.IsSupported(entry.Name)) continue;

                var file = entry.Value.GetProperty("file").GetString();
                var size = entry.Value.GetProperty("size").GetInt64();
                var sha = entry.Value.GetProperty("sha256").GetString();
                if (file is null || !ArchiveName().IsMatch(file)) return Reject(what, $"имя архива «{file}» недопустимо");
                if (sha is null || !Sha256Hex().IsMatch(sha)) return Reject(what, $"SHA-256 архива {entry.Name} не 64 hex");
                if (size <= 0) return Reject(what, $"размер архива {entry.Name} не положителен");
                // Раскладка на диске — {version}/{archive}: два RID с одним именем делили бы файл
                if (!files.Add(file)) return Reject(what, $"имя архива «{file}» повторяется");

                archives[entry.Name] = new AgentReleaseArchive(versionText, entry.Name, file, size, sha.ToLowerInvariant());
            }

            if (archives.Count == 0) return Reject(what, "нет ни одного архива известной платформы");
            return new AgentRelease(versionText, archives);
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            return Reject(what, ex.Message);
        }
    }

    private AgentRelease? Reject(string what, string reason)
    {
        _log?.LogWarning("Релиз агента: {What} отвергнут — {Reason}", what, reason);
        return null;
    }

    // Только буквы, цифры, точка, дефис, подчёркивание: ни разделителей пути, ни пробелов,
    // первый символ — не точка
    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._-]{0,127}\z")]
    private static partial Regex ArchiveName();

    [GeneratedRegex(@"^[0-9A-Fa-f]{64}\z")]
    private static partial Regex Sha256Hex();
}
