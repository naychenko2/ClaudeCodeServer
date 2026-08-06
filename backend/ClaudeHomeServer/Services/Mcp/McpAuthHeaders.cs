using ClaudeHomeServer.Models;

namespace ClaudeHomeServer.Services.Mcp;

/// <summary>
/// Заголовки авторизации http/sse-записи реестра. Единая точка для обоих потребителей —
/// сборки конфига хода (SessionManager) и разовой пробы (<see cref="McpProbeService"/>):
/// иначе проба говорила бы «работает» про сервер, которому ход отдаёт другие заголовки.
/// </summary>
public static class McpAuthHeaders
{
    /// <summary>
    /// Дописывает заголовок авторизации. <paramref name="resolve"/> разворачивает ссылку на
    /// секрет в значение. false — секрет потерян или конфиг неполон: отдавать серверу заведомо
    /// анонимный запрос нельзя, инструменты молча отвечали бы 401.
    /// </summary>
    public static bool TryApply(McpServerRecord record, Dictionary<string, string> headers,
        Func<string?, string?> resolve)
    {
        var auth = record.Auth;
        if (auth.Kind == McpAuthKind.None) return true;
        var secretRef = auth.Kind == McpAuthKind.OAuth2 ? auth.OAuth?.AccessTokenRef : auth.SecretRef;
        var value = resolve(secretRef);
        if (string.IsNullOrEmpty(value)) return false;
        if (auth.Kind == McpAuthKind.ApiKey)
        {
            if (string.IsNullOrWhiteSpace(auth.HeaderName)) return false;
            headers[auth.HeaderName] = value;
        }
        else headers["Authorization"] = "Bearer " + value;
        return true;
    }
}
