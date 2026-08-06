using System.Text.Json;
using System.Text.Json.Serialization;

namespace ClaudeHomeServer.Services.Mcp;

/// <summary>
/// Значение секрета: либо плоская строка (ключ API, Bearer-токен), либо структурированная
/// запись OAuth. Токены OAuth живут ОДНОЙ записью — рефреш переписывает access, refresh
/// и срок вместе, а три отдельные ссылки разъезжались бы при сбое посередине.
/// </summary>
[JsonConverter(typeof(McpSecretEntryConverter))]
public sealed class McpSecretEntry
{
    /// <summary>Само значение: ключ API, Bearer-токен или access-токен OAuth.</summary>
    public string? Value { get; set; }
    public string? RefreshToken { get; set; }
    /// <summary>Срок жизни access-токена (UTC); null — бессрочный или неизвестен.</summary>
    public DateTime? ExpiresAt { get; set; }
    public string? Scope { get; set; }
    public string? TokenType { get; set; }

    /// <summary>Запись без OAuth-полей — на диск уходит голой строкой (формат до волны 7).</summary>
    [JsonIgnore]
    public bool IsPlain =>
        RefreshToken is null && ExpiresAt is null && Scope is null && TokenType is null;

    public static McpSecretEntry Plain(string value) => new() { Value = value };
}

/// <summary>
/// Читает обе формы значения — строку (уже лежащие ключи API и Bearer) и объект (токены
/// OAuth), пишет минимальную: запись без OAuth-полей остаётся строкой. Обратная
/// совместимость нужна в ОБЕ стороны — тот же файл читают старые архивы бэкапа.
/// </summary>
public sealed class McpSecretEntryConverter : JsonConverter<McpSecretEntry>
{
    public override McpSecretEntry? Read(ref Utf8JsonReader reader, Type typeToConvert,
        JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.Null: return null;
            case JsonTokenType.String: return McpSecretEntry.Plain(reader.GetString() ?? "");
            case JsonTokenType.StartObject: break;
            default: throw new JsonException("Значение секрета MCP: ожидалась строка или объект");
        }

        var entry = new McpSecretEntry();
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject) return entry;
            if (reader.TokenType != JsonTokenType.PropertyName) continue;
            var name = reader.GetString() ?? "";
            reader.Read();
            switch (name.ToLowerInvariant())
            {
                case "value": entry.Value = Text(ref reader); break;
                case "refreshtoken": entry.RefreshToken = Text(ref reader); break;
                case "scope": entry.Scope = Text(ref reader); break;
                case "tokentype": entry.TokenType = Text(ref reader); break;
                case "expiresat":
                    entry.ExpiresAt = reader.TokenType == JsonTokenType.Null
                        ? null : reader.GetDateTime().ToUniversalTime();
                    break;
                default: reader.Skip(); break;
            }
        }
        throw new JsonException("Незакрытый объект значения секрета MCP");

        static string? Text(ref Utf8JsonReader reader) =>
            reader.TokenType == JsonTokenType.Null ? null : reader.GetString();
    }

    public override void Write(Utf8JsonWriter writer, McpSecretEntry value, JsonSerializerOptions options)
    {
        if (value.IsPlain)
        {
            writer.WriteStringValue(value.Value ?? "");
            return;
        }
        writer.WriteStartObject();
        if (value.Value is not null) writer.WriteString("Value", value.Value);
        if (value.RefreshToken is not null) writer.WriteString("RefreshToken", value.RefreshToken);
        if (value.ExpiresAt is { } expires) writer.WriteString("ExpiresAt", expires.ToUniversalTime());
        if (value.Scope is not null) writer.WriteString("Scope", value.Scope);
        if (value.TokenType is not null) writer.WriteString("TokenType", value.TokenType);
        writer.WriteEndObject();
    }
}

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
    private Dictionary<string, Dictionary<string, McpSecretEntry>> _byOwner = new();
    private readonly object _lock = new();

    public McpSecretStore(IConfiguration config)
    {
        var dataDir = Path.GetDirectoryName(
            config["DataPath"] ?? Path.Combine(AppContext.BaseDirectory, "data", "projects.json"))!;
        _filePath = Path.Combine(dataDir, "mcp-secrets.json");
        _byOwner = JsonFileStore.Load<Dictionary<string, Dictionary<string, McpSecretEntry>>>(
            _filePath, JsonOptions) ?? new();
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
    public string Set(string ownerId, string value) => SetEntry(ownerId, McpSecretEntry.Plain(value));

    /// <summary>
    /// Кладёт структурированную запись и возвращает плейсхолдер. <paramref name="refOrId"/>
    /// задан — перезаписывает существующую запись (рефреш токена обязан сохранить ссылку:
    /// иначе пришлось бы править реестр на каждое обновление).
    /// </summary>
    public string SetEntry(string ownerId, McpSecretEntry entry, string? refOrId = null)
    {
        var id = string.IsNullOrEmpty(refOrId)
            ? Guid.NewGuid().ToString("N")
            : TryParseRef(refOrId, out var parsed) ? parsed : refOrId;
        lock (_lock)
        {
            var bag = _byOwner.TryGetValue(ownerId, out var b) ? b : _byOwner[ownerId] = new();
            bag[id] = entry;
            Save();
        }
        return Placeholder(id);
    }

    /// <summary>Значение секрета владельца; null — секрета нет (запись ссылается в пустоту).</summary>
    public string? Get(string ownerId, string secretId) => GetEntry(ownerId, secretId)?.Value;

    /// <summary>Запись целиком (с полями OAuth); null — секрета нет.</summary>
    public McpSecretEntry? GetEntry(string ownerId, string secretId)
    {
        lock (_lock)
            return _byOwner.TryGetValue(ownerId, out var bag) && bag.TryGetValue(secretId, out var entry)
                ? entry : null;
    }

    /// <summary>Разворачивает плейсхолдер в значение; не плейсхолдер — возвращает как есть.</summary>
    public string? Resolve(string ownerId, string? value) =>
        TryParseRef(value, out var id) ? Get(ownerId, id) : value;

    /// <summary>Разворачивает плейсхолдер в запись; не плейсхолдер — плоская запись из значения.</summary>
    public McpSecretEntry? ResolveEntry(string ownerId, string? value) =>
        TryParseRef(value, out var id) ? GetEntry(ownerId, id)
            : value is null ? null : McpSecretEntry.Plain(value);

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
