using System.Globalization;
using System.Net.WebSockets;
using ClaudeHomeServer.Protocol;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ClaudeHomeServer.Services.Llm.Gateway;

// Туннель выхода (ADR-016 §2, задача 2.9): CONNECT, который CLI на устройстве шлёт в сайдкар
// по HTTPS_PROXY, приезжает сюда WebSocket'ом /gw/t/{turnId}/egress?host=…&port=…, и наружу
// его выпускает сервер. Это не открытый прокси:
// - вход — только токен хода вместе с учёткой его устройства (та же проверка, что у LLM/MCP);
// - порт — из LlmGateway:Egress:AllowedPorts (по умолчанию только 443);
// - адрес проверяется ПОСЛЕ разрешения имени (EgressConnector/EgressAddressPolicy), соединение
//   идёт на проверенный адрес;
// - тумблер LlmGateway:Egress:Enabled выключен — 403 с причиной, прямого выхода взамен нет.
// В лог — только хост:порт и исход: содержимое туннеля (TLS насквозь) шлюз не видит и не пишет.
// Отказ отвечается кодом ДО апгрейда: сайдкар по нему отвечает CLI на CONNECT.
public static class EgressGatewayEndpoints
{
    public const string RoutePattern = "/gw/t/{" + TurnTokenEndpointFilter.RouteKey + "}/" + DeviceEgressRoutes.Segment;

    // Как у серверных входов, которые проксирует YARP: WebSocket-мидлвара — на ветке эндпоинта
    public static IEndpointConventionBuilder MapEgressGateway(this IEndpointRouteBuilder endpoints)
    {
        var branch = endpoints.CreateApplicationBuilder();
        branch.UseWebSockets();
        branch.Run(HandleAsync);
        // Авторизация — токен хода (AuthorizeAsync ниже), а не JWT пользователя
        return endpoints.Map(RoutePattern, branch.Build()).AllowAnonymous().WithName("EgressGateway");
    }

    internal static async Task HandleAsync(HttpContext http)
    {
        var services = http.RequestServices;
        var options = services.GetRequiredService<IOptionsMonitor<LlmGatewayOptions>>().CurrentValue;
        if (!options.Enabled)
        {
            http.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }
        if (!options.Egress.Enabled)
        {
            await RefuseAsync(http, StatusCodes.Status403Forbidden, "Выход наружу через сервер выключен (LlmGateway:Egress:Enabled).");
            return;
        }

        var grant = await TurnTokenEndpointFilter.AuthorizeAsync(http, services.GetRequiredService<TurnTokenService>());
        if (grant is null)
        {
            http.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }

        var host = http.Request.Query[DeviceEgressRoutes.HostQuery].ToString();
        if (host.Length == 0 || Uri.CheckHostName(host) == UriHostNameType.Unknown
            || !int.TryParse(http.Request.Query[DeviceEgressRoutes.PortQuery], NumberStyles.None, CultureInfo.InvariantCulture, out var port)
            || port is < 1 or > 65535)
        {
            http.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        var log = services.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(EgressGatewayEndpoints).FullName!);
        if (!options.Egress.EffectivePorts.Contains(port))
        {
            log.LogWarning("Шлюз выхода: {Host}:{Port} — порт не разрешён", host, port);
            await RefuseAsync(http, StatusCodes.Status403Forbidden, "Порт назначения не разрешён.");
            return;
        }

        var connector = services.GetRequiredService<EgressConnector>();
        var resolved = await connector.ResolveAsync(host, http.RequestAborted);
        if (resolved.Addresses is null)
        {
            log.LogWarning("Шлюз выхода: {Host}:{Port} — {Outcome}", host, port, resolved.Outcome);
            await RefuseAsync(http, resolved.Status, resolved.Outcome);
            return;
        }

        if (!http.WebSockets.IsWebSocketRequest)
        {
            http.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        var opened = await connector.ConnectAsync(resolved.Addresses, port, http.RequestAborted);
        if (opened.Stream is null)
        {
            log.LogInformation("Шлюз выхода: {Host}:{Port} — {Outcome}", host, port, opened.Outcome);
            await RefuseAsync(http, opened.Status, opened.Outcome);
            return;
        }

        await using var remote = opened.Stream;
        using var socket = await http.WebSockets.AcceptWebSocketAsync();
        log.LogInformation("Шлюз выхода: {Host}:{Port} — туннель открыт", host, port);
        await PumpAsync(socket, remote, http.RequestAborted);
        log.LogDebug("Шлюз выхода: {Host}:{Port} — туннель закрыт", host, port);
    }

    private static async Task RefuseAsync(HttpContext http, int status, string reason)
    {
        http.Response.StatusCode = status;
        await http.Response.WriteAsync(reason, http.RequestAborted);
    }

    // Байты туннеля — двоичными сообщениями в обе стороны; конец любой стороны — конец туннеля.
    // Закрытие потока закрывает и WebSocket — штатно, с потолком ожидания.
    private static async Task PumpAsync(WebSocket socket, Stream remote, CancellationToken ct)
    {
        await using var ws = WebSocketStream.Create(socket, WebSocketMessageType.Binary, TimeSpan.FromSeconds(2));
        using var done = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var up = ws.CopyToAsync(remote, done.Token);
        var down = remote.CopyToAsync(ws, done.Token);
        try
        {
            await Task.WhenAny(up, down);
        }
        finally
        {
            await done.CancelAsync();
            try { await Task.WhenAll(up, down); }
            catch (Exception) { /* одна сторона закрылась — туннель окончен */ }
        }
    }
}
