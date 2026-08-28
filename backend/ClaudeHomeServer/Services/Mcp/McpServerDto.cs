using ClaudeHomeServer.Models;

namespace ClaudeHomeServer.Services.Mcp;

/// <summary>Пара «имя-значение» env/headers. Секрет наружу не выходит: Value = null, HasValue = true.</summary>
public sealed record McpValueDto(string Name, string? Value, bool HasValue, bool Secret);

/// <summary>Авторизация без единого секретного значения: только вид, имя заголовка и признаки.</summary>
public sealed record McpAuthDto(string Kind, string? HeaderName, bool HasSecret,
    string? AuthorizationServer, string? ClientId, DateTime? ExpiresAt, bool HasTokens);

/// <summary>
/// Последнее наблюдение состояния сервера. Источник и время — не украшение: «работал в ходе
/// час назад» и «проверен только что» человек читает по-разному.
/// </summary>
public sealed record McpServerStatusDto(string Status, DateTime ObservedAt, string Source,
    string? SessionId, string? Error);

/// <summary>Указатель на запись реестра-каталога, из которой сервер заведён; null — вручную.</summary>
public sealed record McpCatalogRefDto(string Name, string? Version, DateTime? PublishedAt, string? Url);

/// <summary>Запись реестра для выдачи наружу — маскированная (см. McpServerMapper).</summary>
public sealed record McpServerDto(
    string Id, string Key, string ToolKey, string Label, string? Description,
    string Transport, string? Command, IReadOnlyList<string> Args, IReadOnlyList<McpValueDto> Env,
    string? Url, IReadOnlyList<McpValueDto> Headers, McpAuthDto Auth,
    bool Enabled, bool AlwaysLoad, bool AllowReadOnlyPersonas, bool AllowOutsideProjects,
    string Source, int AuthVersion, DateTime CreatedAt, DateTime UpdatedAt,
    McpCatalogRefDto? CatalogRef = null, McpServerStatusDto? Status = null);

/// <summary>
/// ЕДИНСТВЕННАЯ точка выхода записей реестра наружу. Всегда маскирует: значение, лежащее
/// в McpSecretStore, отдаётся как <c>null + hasValue: true</c>, токены OAuth — только
/// статусом и сроком. Прямая сериализация McpServerRecord в ответ API запрещена: в Env
/// голыми лежат несекретные значения, но перепутать их с секретными легко, а цена ошибки —
/// утёкший в браузер токен.
/// </summary>
public static class McpServerMapper
{
    public static McpServerDto ToDto(McpServerRecord r, McpServerStatusEntry? status = null) => new(
        Id: r.Id,
        Key: r.Key,
        ToolKey: McpRegistry.ToolKeyPrefix + r.Key,
        Label: r.Label,
        Description: r.Description,
        Transport: r.Transport.ToString().ToLowerInvariant(),
        Command: r.Command,
        Args: r.Args ?? [],
        Env: MapValues(r.Env),
        Url: r.Url,
        Headers: MapValues(r.Headers),
        Auth: MapAuth(r.Auth),
        Enabled: r.Enabled,
        AlwaysLoad: r.AlwaysLoad,
        AllowReadOnlyPersonas: r.AllowReadOnlyPersonas,
        AllowOutsideProjects: r.AllowOutsideProjects,
        Source: r.Source.ToString().ToLowerInvariant(),
        AuthVersion: r.AuthVersion,
        CreatedAt: r.CreatedAt,
        UpdatedAt: r.UpdatedAt,
        CatalogRef: r.CatalogRef is null ? null
            : new McpCatalogRefDto(r.CatalogRef.Name, r.CatalogRef.Version,
                r.CatalogRef.PublishedAt, r.CatalogRef.Url),
        Status: MapStatus(status));

    private static McpServerStatusDto? MapStatus(McpServerStatusEntry? status) =>
        status is null ? null : new McpServerStatusDto(
            status.Status, status.ObservedAt,
            status.Source.ToString().ToLowerInvariant(), status.SessionId, status.Error);

    private static IReadOnlyList<McpValueDto> MapValues(Dictionary<string, string>? map)
    {
        if (map is null || map.Count == 0) return [];
        return map.Select(kv => McpSecretStore.TryParseRef(kv.Value, out _)
                ? new McpValueDto(kv.Key, null, HasValue: true, Secret: true)
                : new McpValueDto(kv.Key, kv.Value, HasValue: kv.Value.Length > 0, Secret: false))
            .OrderBy(v => v.Name, StringComparer.Ordinal)
            .ToList();
    }

    private static McpAuthDto MapAuth(McpAuthConfig auth) => new(
        Kind: auth.Kind.ToString().ToLowerInvariant(),
        HeaderName: auth.HeaderName,
        HasSecret: !string.IsNullOrEmpty(auth.SecretRef),
        AuthorizationServer: auth.OAuth?.AuthorizationServer,
        ClientId: auth.OAuth?.ClientId,
        ExpiresAt: auth.OAuth?.ExpiresAt,
        HasTokens: !string.IsNullOrEmpty(auth.OAuth?.AccessTokenRef));
}
