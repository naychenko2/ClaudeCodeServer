namespace ClaudeHomeServer.Services.Mcp.Http;

/// <summary>
/// Условия, при которых продуктовый MCP-сервер едет ходу по HTTP, а не через stdio (ADR-012).
/// Решение принимается ОДИН раз на построении контекста сессии и живёт в нём — состав и
/// транспорт не смеют зависеть от свойств хода.
/// </summary>
public static class McpHttpTransport
{
    /// <summary>Конфиг-рубильник отката: выключенный возвращает все серверы на stdio.</summary>
    public const string EnabledKey = "Mcp:HttpTransport";

    /// <summary>
    /// Годится ли адрес бэкенда под http-транспорт. Fail-closed по СХЕМЕ: не http — значит
    /// https, а боевой серт выписан на внешний домен, и по локальному адресу CLI упирается
    /// в ERR_TLS_CERT_ALTNAME_INVALID, пряча инструмент от модели молча (разведка фазы 0).
    /// Такой адрес — не ошибка конфигурации, а документированное лечение HTTPS-деплоя
    /// (McpTasksApiUrl), поэтому ход просто едет прежним stdio-сервером.
    /// </summary>
    public static bool Usable(string? apiUrl, bool enabled) =>
        enabled
        && Uri.TryCreate(apiUrl, UriKind.Absolute, out var uri)
        && uri.Scheme == Uri.UriSchemeHttp;

    /// <summary>Адрес эндпоинта сервера: базовый URL владельца плюс маршрут контроллера.</summary>
    public static string EndpointFor(string apiUrl, string server) =>
        $"{apiUrl.TrimEnd('/')}/mcp/{server}";
}
