using System.Text.Json.Serialization;

namespace ClaudeHomeServer.Models;

/// <summary>Транспорт MCP-сервера: локальный процесс или удалённый HTTP/SSE-эндпоинт.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum McpTransport { Stdio, Http, Sse }

/// <summary>Способ авторизации у внешнего сервера. OAuth2 — волна 7, каркас заложен сразу.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum McpAuthKind { None, ApiKey, Bearer, OAuth2 }

/// <summary>Откуда взялась запись: заведена руками или импортирована из наследства.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum McpServerSource { Manual, LegacyMcpConfig, LegacyUserScope }

/// <summary>
/// Настройки OAuth2-клиента сервера (волна 7). В реестре живут только несекретные поля —
/// сами токены и client_secret лежат в McpSecretStore по ссылкам *SecretRef.
/// </summary>
public class McpOAuthConfig
{
    public string? AuthorizationServer { get; set; }
    /// <summary>
    /// Адрес обмена кода и рефреша, найденный при discovery. Хранится, чтобы обновление
    /// токена перед ходом не ходило заново по всей цепочке well-known.
    /// </summary>
    public string? TokenEndpoint { get; set; }
    public string? ClientId { get; set; }
    /// <summary>Ссылка на секрет с client_secret (id записи в McpSecretStore).</summary>
    public string? ClientSecretRef { get; set; }
    public List<string>? Scopes { get; set; }
    /// <summary>
    /// Ссылка на запись токенов в McpSecretStore: access, refresh и срок лежат ОДНОЙ
    /// записью (<see cref="Services.Mcp.McpSecretEntry"/>) — рефреш переписывает их вместе.
    /// </summary>
    public string? AccessTokenRef { get; set; }
    /// <summary>Наследство каркаса волны 1: refresh-токен живёт в записи AccessTokenRef.</summary>
    public string? RefreshTokenRef { get; set; }
    /// <summary>Срок жизни access-токена (UTC); null — неизвестен.</summary>
    public DateTime? ExpiresAt { get; set; }
    /// <summary>
    /// redirect_uri, зарегистрированный в DCR и использованный при выдаче кода. Тот же
    /// адрес обязан уйти в запрос обмена — сервер сверяет точное совпадение.
    /// </summary>
    public string? RedirectUri { get; set; }
}

/// <summary>
/// Авторизация внешнего сервера. Значение ключа/токена в записи не хранится никогда —
/// только SecretRef на запись в data/mcp-secrets.json.
/// </summary>
public class McpAuthConfig
{
    public McpAuthKind Kind { get; set; } = McpAuthKind.None;
    /// <summary>Имя заголовка для ApiKey (напр. X-Api-Key). Bearer всегда шлёт Authorization.</summary>
    public string? HeaderName { get; set; }
    public string? SecretRef { get; set; }
    public McpOAuthConfig? OAuth { get; set; }
}

/// <summary>
/// Запись MCP-сервера в личном реестре владельца (data/mcp-servers.json).
/// Секретных ЗНАЧЕНИЙ здесь нет: в Env/Headers на их месте стоит плейсхолдер
/// <c>secret:{id}</c> (см. <see cref="Services.Mcp.McpSecretStore"/>), у Auth — SecretRef.
/// </summary>
public class McpServerRecord
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string OwnerId { get; set; } = "";
    /// <summary>Slug, уникальный у владельца. В каталоге инструментов живёт как <c>mcp:{Key}</c>.</summary>
    public string Key { get; set; } = "";
    public string Label { get; set; } = "";
    public string? Description { get; set; }
    public McpTransport Transport { get; set; } = McpTransport.Stdio;

    // stdio
    public string? Command { get; set; }
    public List<string>? Args { get; set; }
    public Dictionary<string, string>? Env { get; set; }

    // http / sse
    public string? Url { get; set; }
    public Dictionary<string, string>? Headers { get; set; }

    public McpAuthConfig Auth { get; set; } = new();

    public bool Enabled { get; set; } = true;
    /// <summary>
    /// Поднимать сервер до старта хода. Дефолт false: чужой сервер — это лишний процесс и
    /// секунды старта на каждый ход, платить за них имеет смысл только по явному решению.
    /// </summary>
    public bool AlwaysLoad { get; set; }
    /// <summary>
    /// Отдавать ли сервер персонам с профилем доступа ReadOnly (волна 3): имён чужих
    /// инструментов мы не знаем, поэтому по умолчанию такие персоны его не получают.
    /// </summary>
    public bool AllowReadOnlyPersonas { get; set; }

    public McpServerSource Source { get; set; } = McpServerSource.Manual;

    /// <summary>
    /// Версия авторизации: растёт при правке записи, ре-авторизации и рефреше токена.
    /// Входит в сигнатуру запуска CLI — заголовки запекаются в конфиг на старте процесса,
    /// и обновлённый токен живому процессу иначе не доедет. Сам секрет в сигнатуру не попадает.
    /// </summary>
    public int AuthVersion { get; set; } = 1;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
