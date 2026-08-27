namespace ClaudeHomeServer.Services.Mcp.Http;

/// <summary>
/// Условия, при которых продуктовый MCP-сервер едет ходу по HTTP, а не через stdio (ADR-012).
/// Состав и транспорт не смеют зависеть от свойств хода. Решение о транспорте состоит из
/// двух слагаемых: СХЕМА адреса (стабильна — свойство ApiUrl контекста) и рубильник
/// Mcp:HttpTransport (живой: LlmSessionContext.HttpMcpEnabledProvider спрашивается на
/// каждый ход, чтобы откат доезжал и до уже поднятых чатов — техдолг ADR-012 §1).
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
    /// Схема адреса не меняется в жизни адаптера, так что этот предел решения вычисляется
    /// один раз на построении контекста; рубильник домножается живьём на каждый ход.
    ///
    /// Строгость к ФОРМЕ сырой строки: <c>Uri.TryCreate</c> молча прощает ведущие пробелы и
    /// разбирает query, а адрес эндпоинта строится из той же строки — расхождение «гейт
    /// пропустил, конфиг сломался» снова даёт молчаливую пропажу инструмента. Пробелы по
    /// краям и ?/# в базовом адресе — тоже fail-closed на stdio.
    /// </summary>
    public static bool Usable(string? apiUrl, bool enabled) =>
        enabled
        && apiUrl is { } url
        && url == url.Trim()
        && !url.Contains('?') && !url.Contains('#')
        && Uri.TryCreate(url, UriKind.Absolute, out var uri)
        && uri.Scheme == Uri.UriSchemeHttp;

    /// <summary>
    /// Адрес эндпоинта сервера: базовый URL владельца плюс маршрут контроллера. Строится из
    /// РАЗОБРАННОГО адреса, а не конкатенацией сырой строки — гейт судит по нормализованному
    /// Uri, и адрес в конфиге хода обязан с ним соглашаться.
    /// </summary>
    public static string EndpointFor(string apiUrl, string server) =>
        Uri.TryCreate(apiUrl, UriKind.Absolute, out var uri)
            ? uri.GetLeftPart(UriPartial.Authority) + uri.AbsolutePath.TrimEnd('/') + "/mcp/" + server
            : apiUrl.TrimEnd('/') + "/mcp/" + server;
}
