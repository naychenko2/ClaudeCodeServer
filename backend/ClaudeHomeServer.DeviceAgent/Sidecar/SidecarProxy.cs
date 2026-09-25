using System.Text.RegularExpressions;
using ClaudeHomeServer.Protocol;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace ClaudeHomeServer.DeviceAgent.Sidecar;

/// <summary>Кто звонит шлюзу: адрес сервера и учётка устройства. Токен — только в памяти.</summary>
internal interface IDeviceIdentity
{
    Uri ServerUri { get; }
    string DeviceToken { get; }
    string Fingerprint { get; }
}

/// <summary>
/// Сайдкар на 127.0.0.1 (ADR-016 §2): CLI ходит сюда за LLM (<c>ANTHROPIC_BASE_URL</c>) и
/// MCP (<c>http</c>-серверы конфига). Маршрут <c>/t/{ключ}/{llm|mcp}/…</c> уходит на
/// <c>{сервер}/gw/t/{ход}/{llm|mcp}/…</c>: авторизация CLI (заглушка) выбрасывается,
/// ставятся токен устройства с отпечатком (кто звонит) и токен хода (какой ход).
/// Пропускается любой метод — MCP streamable HTTP шлёт GET и DELETE. Ответ отдаётся
/// потоком, без буферизации: SSE-дельты доходят до CLI по мере прихода.
/// </summary>
internal sealed partial class SidecarProxy(TurnGrants grants, IDeviceIdentity device, HttpClient http, ILogger<SidecarProxy> log)
{
    public const string HttpClientName = "sidecar-gateway";

    public const string DeviceAuthPrefix = "Device ";
    public const string FingerprintHeader = "X-Device-Fingerprint";
    public const string TurnTokenHeader = "X-Turn-Token";

    // Что клиент не вправе передать дальше: его авторизация, наши заголовки (их ставим мы),
    // контекст MCP (его ставит шлюз по привязке токена) и hop-by-hop
    private static readonly HashSet<string> DroppedRequestHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Authorization", "X-Api-Key", "Proxy-Authorization", "Proxy-Connection", "Cookie",
        TurnTokenHeader, FingerprintHeader, "X-Caller-Session-Id",
        "Host", "Connection", "Keep-Alive", "Transfer-Encoding", "TE", "Trailer", "Upgrade", "Expect",
        "Content-Length", "Content-Type", "Content-Encoding", "Content-Language", "Content-Location",
        "Content-MD5", "Content-Range", "Content-Disposition",
    };

    private static readonly HashSet<string> DroppedResponseHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Connection", "Keep-Alive", "Transfer-Encoding", "TE", "Trailer", "Upgrade", "Proxy-Authenticate",
    };

    [GeneratedRegex("^/" + DeviceSidecarRoutes.TurnSegment + "/(?<key>[0-9a-f]{32})/(?<kind>"
        + DeviceSidecarRoutes.Llm + "|" + DeviceSidecarRoutes.Mcp + ")(?<rest>/.*)?$")]
    private static partial Regex RouteRegex();

    public async Task HandleAsync(HttpContext context)
    {
        var match = RouteRegex().Match(context.Request.Path.Value ?? "");
        if (!match.Success || !grants.TryGet(match.Groups["key"].Value, out var grant))
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }
        if (grant is null)
        {
            // Сервер не выдал ходу шлюз — идти некуда, и это видно CLI, а не тишиной
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            await context.Response.WriteAsync("шлюз для этого хода не выдан сервером");
            return;
        }

        var kind = match.Groups["kind"].Value;
        var rest = match.Groups["rest"].Success ? match.Groups["rest"].Value : "/";
        var target = new Uri(device.ServerUri,
            $"gw/t/{Uri.EscapeDataString(grant.TurnId)}/{kind}{rest}{context.Request.QueryString}");

        using var request = new HttpRequestMessage(new HttpMethod(context.Request.Method), target);
        if (HasBody(context.Request))
        {
            request.Content = new StreamContent(context.Request.Body);
            foreach (var header in context.Request.Headers)
                if (header.Key.StartsWith("Content-", StringComparison.OrdinalIgnoreCase)
                    && !header.Key.Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
                    request.Content.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray());
            if (context.Request.ContentLength is { } length) request.Content.Headers.ContentLength = length;
        }

        foreach (var header in context.Request.Headers)
            if (!DroppedRequestHeaders.Contains(header.Key))
                request.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray());

        request.Headers.TryAddWithoutValidation("Authorization", DeviceAuthPrefix + device.DeviceToken);
        request.Headers.TryAddWithoutValidation(FingerprintHeader, device.Fingerprint);
        request.Headers.TryAddWithoutValidation(TurnTokenHeader, grant.Token);

        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, context.RequestAborted);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            if (context.RequestAborted.IsCancellationRequested) return;
            // Сообщение исключения несёт только адрес, заголовков запроса в нём нет
            log.LogWarning("Сайдкар: шлюз недоступен ({Kind} {Method}): {Error}", kind, context.Request.Method, e.Message);
            context.Response.StatusCode = StatusCodes.Status502BadGateway;
            return;
        }

        using (response)
        {
            context.Response.StatusCode = (int)response.StatusCode;
            foreach (var header in response.Headers.Concat(response.Content.Headers))
                if (!DroppedResponseHeaders.Contains(header.Key))
                    context.Response.Headers[header.Key] = header.Value.ToArray();

            await context.Response.StartAsync(context.RequestAborted);
            await using var body = await response.Content.ReadAsStreamAsync(context.RequestAborted);
            var buffer = new byte[16 * 1024];
            try
            {
                int n;
                while ((n = await body.ReadAsync(buffer, context.RequestAborted)) > 0)
                {
                    await context.Response.Body.WriteAsync(buffer.AsMemory(0, n), context.RequestAborted);
                    await context.Response.Body.FlushAsync(context.RequestAborted);
                }
            }
            catch (Exception e) when (e is IOException or OperationCanceledException or HttpRequestException)
            {
                // CLI или шлюз оборвали поток — дописывать некуда
            }
        }
    }

    private static bool HasBody(HttpRequest request) =>
        request.ContentLength > 0
        || request.Headers.TransferEncoding.Count > 0
        || (request.ContentLength is null && request.Method is "POST" or "PUT" or "PATCH");
}
