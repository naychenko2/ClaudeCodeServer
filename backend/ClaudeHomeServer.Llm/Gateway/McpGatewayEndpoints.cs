using System.Collections.Frozen;
using System.Net.Http.Headers;
using ClaudeHomeServer.Core.Telemetry;
using ClaudeHomeServer.Services.Composition;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Logging;

namespace ClaudeHomeServer.Services.Llm.Gateway;

// Шлюз MCP (ADR-016, план §3 задача 1.3): /gw/t/{turnId}/mcp/{name}[/{хвост}] → MCP-over-HTTP
// бэкенда. Устройство не знает ни JWT, ни адреса бэкенда: вход — по токену хода
// (TurnTokenEndpointFilter), а сервисный JWT владельца и X-Caller-Session-Id подставляет шлюз
// из привязки токена. Всё, что клиент прислал в Authorization и X-Caller-Session-Id, выбрасывается.
//
// Хвост маршрута сверяется с чатом токена: у серверов, чей хвост — id чата, путь до бэкенда
// строится из привязки, а не из клиентского хвоста, чужой чат — 403. Незнакомое имя сервера
// — 404 без похода в бэкенд (fail-closed: новый сервер с иной формой хвоста сюда вписывается явно).
//
// Любой HTTP-метод проходит насквозь (streamable HTTP шлёт GET), ответ — потоком, без буфера.
// Состав tools/list шлюз не трогает: тело ответа бэкенда едет как есть (сторож G9).
public static class McpGatewayEndpoints
{
    public const string HttpClientName = "mcp-gateway";
    public const string RoutePattern = "/gw/t/{" + TurnTokenEndpointFilter.RouteKey + "}/mcp/{name}/{**tail}";

    // Серверы, у которых хвост маршрута — id чата-вызывателя (EndpointFor(…, Info.Id) в ClaudeSession)
    private static readonly FrozenSet<string> SessionTailed = new[]
    {
        McpEndpoints.TasksName, McpEndpoints.NotesName, McpEndpoints.PersonasName,
        McpEndpoints.NotificationsName, McpEndpoints.WatchName, McpEndpoints.WebSearchName,
        McpEndpoints.CodeGraphName, McpEndpoints.DifyName, McpEndpoints.HiggsfieldName,
        McpEndpoints.WorkspaceName,
    }.ToFrozenSet(StringComparer.Ordinal);

    // Заголовки, которые к бэкенду не едут: учётные данные клиента, контекст, который ставит
    // шлюз, и hop-by-hop. Host выставит HttpClient по адресу бэкенда.
    private static readonly FrozenSet<string> DroppedRequestHeaders = new[]
    {
        "Authorization", "Cookie", "Host", TurnTokenEndpointFilter.HeaderName, McpEndpoints.CallerSessionHeader,
        TurnTokenEndpointFilter.DeviceFingerprintHeader,
        "Connection", "Keep-Alive", "Proxy-Authorization", "Proxy-Connection", "TE", "Trailer",
        "Transfer-Encoding", "Upgrade",
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    // Текст ошибки HttpClient несёт адрес и ответ бэкенда — в лог идёт с потолком
    private const int MaxLoggedErrorLength = 500;

    public static IEndpointConventionBuilder MapMcpGateway(this IEndpointRouteBuilder app) =>
        app.Map(RoutePattern, HandleAsync)
            .AddEndpointFilter<TurnTokenEndpointFilter>()
            .WithName("McpGateway");

    // Путь на бэкенде для имени и хвоста по привязке токена: Status 0 — идём по Path,
    // иначе отвечаем этим кодом, не звоня в бэкенд.
    internal static (int Status, string? Path) ResolveUpstreamPath(string name, string? tail, string sessionId)
    {
        tail = string.IsNullOrEmpty(tail) ? null : tail;
        if (SessionTailed.Contains(name))
            return string.Equals(tail, sessionId, StringComparison.Ordinal)
                ? (0, $"/mcp/{name}/{Uri.EscapeDataString(sessionId)}")
                : (StatusCodes.Status403Forbidden, null);
        if (name == McpEndpoints.WidgetsName)
            return tail is null ? (0, "/mcp/" + name) : (StatusCodes.Status403Forbidden, null);
        if (name == McpEndpoints.MemoryName)
        {
            // Хвост памяти — {персона}/{проект}, чата в нём нет: чужую персону или проект
            // отсекает тулсет по владельцу из JWT. Здесь — только форма, чтобы хвост не
            // увёл запрос с маршрута памяти.
            var parts = tail?.Split('/');
            if (parts is not { Length: 2 } || parts.Any(p => p.Length == 0 || p is "." or ".."))
                return (StatusCodes.Status403Forbidden, null);
            return (0, $"/mcp/{name}/{Uri.EscapeDataString(parts[0])}/{Uri.EscapeDataString(parts[1])}");
        }
        return (StatusCodes.Status404NotFound, null);
    }

    private static async Task HandleAsync(HttpContext http, string name, string? tail)
    {
        // Фильтр токена уже прошёл — без привязки сюда не попасть
        var grant = (TurnTokenGrant)http.Items[typeof(TurnTokenGrant)]!;
        var (status, path) = ResolveUpstreamPath(name, tail, grant.SessionId);
        if (path is null)
        {
            if (status == StatusCodes.Status403Forbidden)
                Logger(http).LogWarning("Шлюз MCP: ход {TurnId} чата {SessionId} постучался в «{Server}» с чужим хвостом",
                    grant.TurnId, grant.SessionId, name);
            http.Response.StatusCode = status;
            return;
        }

        var backend = http.RequestServices.GetRequiredService<IMcpBackendAccess>();
        var client = http.RequestServices.GetRequiredService<IHttpClientFactory>().CreateClient(HttpClientName);

        using var request = new HttpRequestMessage(new HttpMethod(http.Request.Method), backend.ApiUrl.TrimEnd('/') + path);
        if (http.Features.Get<IHttpRequestBodyDetectionFeature>()?.CanHaveBody == true)
            request.Content = new StreamContent(http.Request.Body);
        foreach (var (key, values) in http.Request.Headers)
        {
            if (DroppedRequestHeaders.Contains(key)) continue;
            if (!request.Headers.TryAddWithoutValidation(key, (IEnumerable<string?>)values))
                request.Content?.Headers.TryAddWithoutValidation(key, (IEnumerable<string?>)values);
        }
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", backend.ServiceTokenFor(grant.OwnerId));
        request.Headers.TryAddWithoutValidation(McpEndpoints.CallerSessionHeader, grant.SessionId);

        HttpResponseMessage response;
        try
        {
            response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, http.RequestAborted);
        }
        catch (HttpRequestException ex)
        {
            Logger(http).LogWarning("Шлюз MCP: бэкенд не ответил на «{Server}»: {Error}", name, LogText.OneLine(ex.Message, MaxLoggedErrorLength));
            http.Response.StatusCode = StatusCodes.Status502BadGateway;
            return;
        }
        catch (OperationCanceledException) when (http.RequestAborted.IsCancellationRequested)
        {
            return;
        }

        using (response)
        {
            http.Response.StatusCode = (int)response.StatusCode;
            foreach (var (key, values) in response.Headers.Concat(response.Content.Headers))
                if (!LlmGatewayEndpoints.DroppedResponseHeaders.Contains(key))
                    http.Response.Headers[key] = values.ToArray();

            // Стрим без буфера: событие SSE уходит клиенту, как только пришло от бэкенда
            http.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();
            await using var body = await response.Content.ReadAsStreamAsync(http.RequestAborted);
            var buffer = new byte[16 * 1024];
            try
            {
                int read;
                while ((read = await body.ReadAsync(buffer, http.RequestAborted)) > 0)
                {
                    await http.Response.Body.WriteAsync(buffer.AsMemory(0, read), http.RequestAborted);
                    await http.Response.Body.FlushAsync(http.RequestAborted);
                }
            }
            catch (OperationCanceledException) when (http.RequestAborted.IsCancellationRequested)
            {
                // Клиент ушёл посреди стрима — доливать некому
            }
        }
    }

    private static ILogger Logger(HttpContext http) =>
        http.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(McpGatewayEndpoints).FullName!);
}
