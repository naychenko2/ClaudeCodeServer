using System.Text.Json;

namespace ClaudeHomeServer.Services.Mcp;

/// <summary>
/// Значения секретов MCP-серверов (ключи API, Bearer-токены, OAuth-токены) —
/// data/mcp-secrets.json, per-owner. В реестре (data/mcp-servers.json) на их месте стоит
/// плейсхолдер <c>secret:{id}</c>, так что сам реестр едет в облачный архив безопасно;
/// имя ЭТОГО файла добавлено в BackupPaths.SecretFileNames — он уходит только в локальный
/// архив секретов.
/// </summary>
public class McpSecretStore
{
    // Плейсхолдер значения в записи реестра
    public const string Prefix = "secret:";

    private readonly string _filePath;
    private Dictionary<string, Dictionary<string, string>> _byOwner = new();
    private readonly object _lock = new();

    public McpSecretStore(IConfiguration config)
    {
        var dataDir = Path.GetDirectoryName(
            config["DataPath"] ?? Path.Combine(AppContext.BaseDirectory, "data", "projects.json"))!;
        _filePath = Path.Combine(dataDir, "mcp-secrets.json");
        _byOwner = JsonFileStore.Load<Dictionary<string, Dictionary<string, string>>>(_filePath, JsonOptions) ?? new();
    }

    /// <summary>Плейсхолдер для записи реестра.</summary>
    public static string Placeholder(string secretId) => Prefix + secretId;

    /// <summary>Ссылка ли это на секрет; secretId — id записи в сторе.</summary>
    public static bool TryParseRef(string? value, out string secretId)
    {
        secretId = "";
        if (value is null || !value.StartsWith(Prefix, StringComparison.Ordinal)) return false;
        secretId = value[Prefix.Length..];
        return secretId.Length > 0;
    }

    /// <summary>Кладёт значение и возвращает плейсхолдер для записи реестра.</summary>
    public string Set(string ownerId, string value)
    {
        var id = Guid.NewGuid().ToString("N");
        lock (_lock)
        {
            var bag = _byOwner.TryGetValue(ownerId, out var b) ? b : _byOwner[ownerId] = new();
            bag[id] = value;
            Save();
        }
        return Placeholder(id);
    }

    /// <summary>Значение секрета владельца; null — секрета нет (запись ссылается в пустоту).</summary>
    public string? Get(string ownerId, string secretId) =>
        _byOwner.TryGetValue(ownerId, out var bag) && bag.TryGetValue(secretId, out var value) ? value : null;

    /// <summary>Разворачивает плейсхолдер в значение; не плейсхолдер — возвращает как есть.</summary>
    public string? Resolve(string ownerId, string? value) =>
        TryParseRef(value, out var id) ? Get(ownerId, id) : value;

    /// <summary>Удаляет перечисленные секреты владельца (значения плейсхолдеров или голые id).</summary>
    public void Remove(string ownerId, IEnumerable<string> refsOrIds)
    {
        lock (_lock)
        {
            if (!_byOwner.TryGetValue(ownerId, out var bag)) return;
            var removed = false;
            foreach (var raw in refsOrIds)
            {
                if (string.IsNullOrEmpty(raw)) continue;
                var id = TryParseRef(raw, out var parsed) ? parsed : raw;
                removed |= bag.Remove(id);
            }
            if (removed) Save();
        }
    }

    private void Save() => JsonFileStore.Save(_filePath, _byOwner, JsonOptions);

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
}
