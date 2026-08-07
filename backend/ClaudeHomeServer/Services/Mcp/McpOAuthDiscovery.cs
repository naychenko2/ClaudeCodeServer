using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ClaudeHomeServer.Services.Mcp;

/// <summary>Адреса authorization server: то, ради чего затевается discovery.</summary>
public sealed record McpOAuthEndpoints(
    string AuthorizationEndpoint, string TokenEndpoint, string? RegistrationEndpoint,
    IReadOnlyList<string> ScopesSupported);

/// <summary>
/// Разбор ответов сервера при поиске authorization server — чистые функции без сети,
/// чтобы разбор чужих (и часто кривых) метаданных проверялся тестами, а не вручную.
/// Порядок по спеке MCP (2025-03-26 / 2025-06-18):
/// 401 + WWW-Authenticate → resource_metadata → protected-resource → authorization-server.
/// </summary>
public static class McpOAuthDiscovery
{
    /// <summary>
    /// Адрес метаданных ресурса из заголовка <c>WWW-Authenticate</c>
    /// (<c>Bearer realm="…", resource_metadata="https://…"</c>); null — параметра нет.
    /// </summary>
    public static string? ResourceMetadataFrom(IEnumerable<string>? wwwAuthenticate)
    {
        foreach (var header in wwwAuthenticate ?? [])
        {
            if (string.IsNullOrWhiteSpace(header)) continue;
            var index = header.IndexOf("resource_metadata", StringComparison.OrdinalIgnoreCase);
            if (index < 0) continue;
            var rest = header[(index + "resource_metadata".Length)..].TrimStart();
            if (rest.Length == 0 || rest[0] != '=') continue;
            rest = rest[1..].TrimStart();
            var value = rest.StartsWith('"')
                ? rest[1..].Split('"').FirstOrDefault() ?? ""
                : new string(rest.TakeWhile(c => c != ',' && c != ' ').ToArray());
            value = value.Trim();
            if (value.Length > 0) return value;
        }
        return null;
    }

    /// <summary>
    /// Кандидаты на метаданные защищённого ресурса, когда заголовка не было: сначала
    /// вариант с путём сервера (issuer с путём — обычное дело у мультитенантных хостов),
    /// потом корневой.
    /// </summary>
    public static IReadOnlyList<string> ProtectedResourceCandidates(Uri serverUrl) =>
        WellKnownCandidates(serverUrl, "oauth-protected-resource");

    /// <summary>Кандидаты на метаданные authorization server (+ фолбэк openid-configuration).</summary>
    public static IReadOnlyList<string> AuthorizationServerCandidates(Uri issuer) =>
    [
        .. WellKnownCandidates(issuer, "oauth-authorization-server"),
        .. WellKnownCandidates(issuer, "openid-configuration"),
    ];

    private static List<string> WellKnownCandidates(Uri url, string document)
    {
        var root = url.GetLeftPart(UriPartial.Authority);
        var path = url.AbsolutePath.TrimEnd('/');
        var result = new List<string>();
        if (path.Length > 0) result.Add($"{root}/.well-known/{document}{path}");
        result.Add($"{root}/.well-known/{document}");
        return result;
    }

    /// <summary>Адрес authorization server из метаданных ресурса; null — поля нет.</summary>
    public static string? AuthorizationServerFrom(JsonElement metadata)
    {
        if (metadata.ValueKind != JsonValueKind.Object) return null;
        if (metadata.TryGetProperty("authorization_servers", out var list)
            && list.ValueKind == JsonValueKind.Array)
            foreach (var item in list.EnumerateArray())
                if (item.ValueKind == JsonValueKind.String && item.GetString() is { Length: > 0 } value)
                    return value;
        return Str(metadata, "issuer");
    }

    /// <summary>
    /// Эндпоинты из метаданных authorization server. Отсутствующие поля добираются
    /// дефолтами спеки от issuer — часть серверов отдаёт метаданные огрызком.
    /// </summary>
    public static McpOAuthEndpoints EndpointsFrom(JsonElement metadata, Uri issuer)
    {
        var root = issuer.GetLeftPart(UriPartial.Authority) + issuer.AbsolutePath.TrimEnd('/');
        var scopes = new List<string>();
        if (metadata.ValueKind == JsonValueKind.Object
            && metadata.TryGetProperty("scopes_supported", out var list)
            && list.ValueKind == JsonValueKind.Array)
            scopes.AddRange(list.EnumerateArray()
                .Where(s => s.ValueKind == JsonValueKind.String)
                .Select(s => s.GetString()!));

        return new McpOAuthEndpoints(
            Str(metadata, "authorization_endpoint") ?? root + "/authorize",
            Str(metadata, "token_endpoint") ?? root + "/token",
            Str(metadata, "registration_endpoint") ?? root + "/register",
            scopes);
    }

    /// <summary>Эндпоинты по дефолтам спеки — метаданных нет вовсе.</summary>
    public static McpOAuthEndpoints DefaultEndpoints(Uri issuer) =>
        EndpointsFrom(default, issuer);

    private static string? Str(JsonElement node, string name) =>
        node.ValueKind == JsonValueKind.Object
        && node.TryGetProperty(name, out var v)
        && v.ValueKind == JsonValueKind.String
        && v.GetString() is { Length: > 0 } value ? value : null;
}

/// <summary>
/// PKCE (RFC 7636), метод S256 — обязательный для MCP: verifier никогда не покидает сервер,
/// в браузер уходит только challenge.
/// </summary>
public static class McpPkce
{
    /// <summary>Случайный code_verifier (43 символа base64url — 32 байта энтропии).</summary>
    public static string CreateVerifier() => Base64Url(RandomNumberGenerator.GetBytes(32));

    /// <summary>code_challenge = BASE64URL(SHA256(ASCII(verifier))).</summary>
    public static string Challenge(string verifier) =>
        Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));

    /// <summary>Непредсказуемый state — он же ключ pending-записи и защита от подмены.</summary>
    public static string CreateState() => Base64Url(RandomNumberGenerator.GetBytes(24));

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
