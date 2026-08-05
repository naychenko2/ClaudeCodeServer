using System.Text.Json;
using System.Text.RegularExpressions;
using ClaudeHomeServer.Models;

namespace ClaudeHomeServer.Services.Mcp;

/// <summary>
/// Личный реестр MCP-серверов владельца — data/mcp-servers.json. Секретных значений
/// в файле нет: вместо них плейсхолдеры <c>secret:{id}</c> (см. <see cref="McpSecretStore"/>).
/// Реестр только хранит и валидирует; маскировка для выдачи наружу — McpServerMapper,
/// каскад доступности (проект/персона) — резолвер доставки в SessionManager.
/// </summary>
public class McpRegistry
{
    // Ключ сервера в каталоге инструментов персоны — с префиксом, чтобы имя чужого сервера
    // («git», «tasks») не столкнулось с ключами статического ToolCatalog
    public const string ToolKeyPrefix = "mcp:";

    /// <summary>
    /// Ключи встроенных серверов продукта: одноимённая запись реестра всё равно проиграет
    /// им при сборке конфига хода (встроенные ставятся позже), то есть молча не работала бы.
    /// </summary>
    public static readonly string[] ReservedKeys =
    [
        "tasks", "notes", "memory", "personas", "wsp", "notifications",
        "widgets", "codegraph", "dify", "fal-ai", "glif",
    ];

    // Префикс серверов памяти персон-консультантов (pmem_<handle>)
    public const string ConsultantMemoryPrefix = "pmem_";

    private static readonly Regex KeyPattern = new("^[a-z0-9][a-z0-9_-]{0,39}$", RegexOptions.Compiled);

    private readonly string _filePath;
    private readonly McpSecretStore _secrets;
    private readonly Modules.ModuleRegistry? _modules;
    private Dictionary<string, List<McpServerRecord>> _byOwner;
    private readonly object _lock = new();

    public McpRegistry(IConfiguration config, McpSecretStore secrets,
        // Опционально (в тестах не передаётся): ключи MCP-серверов внешних модулей
        // тоже зарезервированы — модуль ставится в конфиг хода аддитивно и запись
        // реестра с тем же ключом просто пропала бы
        Modules.ModuleRegistry? modules = null)
    {
        _secrets = secrets;
        _modules = modules;
        var dataDir = Path.GetDirectoryName(
            config["DataPath"] ?? Path.Combine(AppContext.BaseDirectory, "data", "projects.json"))!;
        _filePath = Path.Combine(dataDir, "mcp-servers.json");
        _byOwner = JsonFileStore.Load<Dictionary<string, List<McpServerRecord>>>(_filePath, JsonOptions) ?? new();
    }

    /// <summary>Записи владельца (снимок, итерируется вне лока).</summary>
    public IReadOnlyList<McpServerRecord> GetByOwner(string ownerId)
    {
        lock (_lock)
            return _byOwner.TryGetValue(ownerId, out var list) ? list.ToList() : [];
    }

    public McpServerRecord? Get(string ownerId, string id)
    {
        lock (_lock)
            return _byOwner.TryGetValue(ownerId, out var list)
                ? list.FirstOrDefault(r => r.Id == id)
                : null;
    }

    /// <summary>
    /// Заводит запись. Ключ нормализуется и проверяется (slug, резерв, уникальность
    /// у владельца); нарушение — InvalidOperationException с текстом для 400.
    /// </summary>
    public McpServerRecord Create(string ownerId, McpServerRecord draft)
    {
        draft.OwnerId = ownerId;
        draft.Key = (draft.Key ?? "").Trim().ToLowerInvariant();
        draft.Id = string.IsNullOrWhiteSpace(draft.Id) ? Guid.NewGuid().ToString() : draft.Id;
        draft.CreatedAt = draft.UpdatedAt = DateTime.UtcNow;
        if (draft.AuthVersion < 1) draft.AuthVersion = 1;
        if (string.IsNullOrWhiteSpace(draft.Label)) draft.Label = draft.Key;

        lock (_lock)
        {
            var list = _byOwner.TryGetValue(ownerId, out var l) ? l : _byOwner[ownerId] = [];
            var error = ValidateKey(draft.Key, list, excludeId: null);
            if (error is not null) throw new InvalidOperationException(error);
            list.Add(draft);
            Save();
        }
        return draft;
    }

    /// <summary>
    /// Заменяет запись целиком (кроме Id/OwnerId/CreatedAt) и поднимает AuthVersion:
    /// заголовки запекаются в конфиг на старте процесса, поэтому правка обязана менять
    /// сигнатуру запуска — иначе живой процесс доживания останется со старым секретом.
    /// null — записи нет у владельца.
    /// </summary>
    public McpServerRecord? Update(string ownerId, string id, McpServerRecord draft)
    {
        lock (_lock)
        {
            if (!_byOwner.TryGetValue(ownerId, out var list)) return null;
            var existing = list.FirstOrDefault(r => r.Id == id);
            if (existing is null) return null;

            var key = (draft.Key ?? "").Trim().ToLowerInvariant();
            var error = ValidateKey(key, list, excludeId: id);
            if (error is not null) throw new InvalidOperationException(error);

            existing.Key = key;
            existing.Label = string.IsNullOrWhiteSpace(draft.Label) ? key : draft.Label;
            existing.Description = draft.Description;
            existing.Transport = draft.Transport;
            existing.Command = draft.Command;
            existing.Args = draft.Args;
            existing.Env = draft.Env;
            existing.Url = draft.Url;
            existing.Headers = draft.Headers;
            existing.Auth = draft.Auth;
            existing.Enabled = draft.Enabled;
            existing.AlwaysLoad = draft.AlwaysLoad;
            existing.AllowReadOnlyPersonas = draft.AllowReadOnlyPersonas;
            existing.AuthVersion++;
            existing.UpdatedAt = DateTime.UtcNow;
            Save();
            return existing;
        }
    }

    /// <summary>Рубильник записи. null — записи нет у владельца.</summary>
    public McpServerRecord? SetEnabled(string ownerId, string id, bool enabled)
    {
        lock (_lock)
        {
            var existing = _byOwner.TryGetValue(ownerId, out var list)
                ? list.FirstOrDefault(r => r.Id == id) : null;
            if (existing is null) return null;
            if (existing.Enabled == enabled) return existing;
            existing.Enabled = enabled;
            existing.UpdatedAt = DateTime.UtcNow;
            Save();
            return existing;
        }
    }

    /// <summary>Удаляет запись и возвращает её (для уборки секретов); null — не найдена.</summary>
    public McpServerRecord? Delete(string ownerId, string id)
    {
        lock (_lock)
        {
            if (!_byOwner.TryGetValue(ownerId, out var list)) return null;
            var existing = list.FirstOrDefault(r => r.Id == id);
            if (existing is null) return null;
            list.Remove(existing);
            Save();
            return existing;
        }
    }

    /// <summary>Все ссылки на секреты записи — что удалить из McpSecretStore вместе с ней.</summary>
    public static IEnumerable<string> SecretRefsOf(McpServerRecord record)
    {
        var values = (record.Env?.Values ?? Enumerable.Empty<string>())
            .Concat(record.Headers?.Values ?? Enumerable.Empty<string>());
        foreach (var value in values)
            if (McpSecretStore.TryParseRef(value, out _)) yield return value;
        if (record.Auth.SecretRef is { Length: > 0 } authRef) yield return authRef;
        if (record.Auth.OAuth is { } oauth)
        {
            if (oauth.ClientSecretRef is { Length: > 0 } cs) yield return cs;
            if (oauth.AccessTokenRef is { Length: > 0 } at) yield return at;
            if (oauth.RefreshTokenRef is { Length: > 0 } rt) yield return rt;
        }
    }

    /// <summary>Текст ошибки для 400 или null, если ключ годен.</summary>
    public string? ValidateKey(string key, IReadOnlyList<McpServerRecord> ownerServers, string? excludeId)
    {
        if (string.IsNullOrWhiteSpace(key))
            return "Не задан ключ сервера";
        if (!KeyPattern.IsMatch(key))
            return "Ключ: латиница в нижнем регистре, цифры, дефис и подчёркивание, до 40 символов";
        if (ReservedKeys.Contains(key, StringComparer.OrdinalIgnoreCase))
            return $"Ключ «{key}» занят встроенным сервером продукта";
        if (key.StartsWith(ConsultantMemoryPrefix, StringComparison.OrdinalIgnoreCase))
            return $"Ключи с префиксом «{ConsultantMemoryPrefix}» заняты памятью персон-консультантов";
        if (ModuleKeys().Contains(key, StringComparer.OrdinalIgnoreCase))
            return $"Ключ «{key}» занят MCP-сервером внешнего модуля";
        if (ownerServers.Any(r => r.Id != excludeId && string.Equals(r.Key, key, StringComparison.OrdinalIgnoreCase)))
            return $"Сервер с ключом «{key}» уже есть";
        return null;
    }

    private IEnumerable<string> ModuleKeys() =>
        _modules is null ? []
            : _modules.All.SelectMany(m => m.Manifest.Mcp ?? []).Select(m => m.Key);

    private void Save() => JsonFileStore.Save(_filePath, _byOwner, JsonOptions);

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    /// <summary>
    /// Разбор вставленного фрагмента <c>{"mcpServers": {...}}</c> (формат самого CLI):
    /// возвращает черновики записей без секретов — значения env/headers приходят голыми,
    /// пометить их секретами и увезти в стор обязан вызывающий. Мусорные узлы пропускаются.
    /// </summary>
    public static List<McpServerRecord> ParseImport(JsonElement root)
    {
        var result = new List<McpServerRecord>();
        var servers = root.ValueKind == JsonValueKind.Object
                      && root.TryGetProperty("mcpServers", out var wrapped)
            ? wrapped : root;
        if (servers.ValueKind != JsonValueKind.Object) return result;

        foreach (var prop in servers.EnumerateObject())
        {
            if (prop.Value.ValueKind != JsonValueKind.Object) continue;
            var node = prop.Value;
            var record = new McpServerRecord
            {
                Key = prop.Name.Trim().ToLowerInvariant(),
                Label = prop.Name,
                Source = McpServerSource.LegacyMcpConfig,
                Enabled = false, // импортированное включает человек, посмотрев на содержимое
            };
            var url = Str(node, "url");
            var type = Str(node, "type");
            if (url is not null)
            {
                record.Transport = string.Equals(type, "sse", StringComparison.OrdinalIgnoreCase)
                    ? McpTransport.Sse : McpTransport.Http;
                record.Url = url;
                record.Headers = Map(node, "headers");
            }
            else
            {
                record.Transport = McpTransport.Stdio;
                record.Command = Str(node, "command");
                if (node.TryGetProperty("args", out var args) && args.ValueKind == JsonValueKind.Array)
                    record.Args = args.EnumerateArray()
                        .Where(a => a.ValueKind == JsonValueKind.String)
                        .Select(a => a.GetString()!).ToList();
                record.Env = Map(node, "env");
                if (record.Command is null) continue; // без команды запись бессмысленна
            }
            result.Add(record);
        }
        return result;

        static string? Str(JsonElement node, string name) =>
            node.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

        static Dictionary<string, string>? Map(JsonElement node, string name)
        {
            if (!node.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.Object) return null;
            var map = new Dictionary<string, string>();
            foreach (var p in v.EnumerateObject())
                if (p.Value.ValueKind == JsonValueKind.String) map[p.Name] = p.Value.GetString()!;
            return map.Count > 0 ? map : null;
        }
    }
}
