using System.Buffers;
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
// - тумблер LlmGateway:Egress:Enabled выключен — 403 с причиной, прямого выхода взамен нет;
// - одновременных туннелей не больше потолка на ход и на устройство (429), а сам туннель
//   закрывается по бездействию, потолку жизни и потолку байтов (сторож G12).
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

        var egress = options.Egress;
        using var lease = services.GetRequiredService<EgressTunnelLimiter>()
            .TryAcquire(grant.TurnId, grant.DeviceId!, egress.MaxConcurrentPerTurn, egress.MaxConcurrentPerDevice);
        if (lease is null)
        {
            log.LogWarning("Шлюз выхода: {Host}:{Port} — потолок одновременных туннелей хода или устройства", host, port);
            await RefuseAsync(http, StatusCodes.Status429TooManyRequests, "Слишком много одновременных туннелей выхода.");
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
        var reason = await PumpAsync(socket, remote, egress, http.RequestAborted);
        log.LogInformation("Шлюз выхода: {Host}:{Port} — туннель закрыт: {Reason}", host, port, reason);
    }

    private static async Task RefuseAsync(HttpContext http, int status, string reason)
    {
        http.Response.StatusCode = status;
        await http.Response.WriteAsync(reason, http.RequestAborted);
    }

    // Байты туннеля — двоичными сообщениями в обе стороны; конец любой стороны — конец туннеля.
    // Сторож закрывает туннель по бездействию и потолку жизни, копирование — по потолку байтов.
    // Закрытие потока закрывает и WebSocket — штатно, с потолком ожидания. Возвращает причину.
    private static async Task<string> PumpAsync(WebSocket socket, Stream remote, EgressGatewayOptions limits, CancellationToken ct)
    {
        await using var ws = WebSocketStream.Create(socket, WebSocketMessageType.Binary, TimeSpan.FromSeconds(2));
        using var done = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var meter = new TunnelMeter(limits.MaxBytesPerTunnel);
        var up = CopyAsync(ws, remote, meter, done.Token);
        var down = CopyAsync(remote, ws, meter, done.Token);
        var watch = WatchAsync(meter, limits, done.Token);
        try
        {
            await Task.WhenAny(up, down, watch);
        }
        finally
        {
            await done.CancelAsync();
            try { await Task.WhenAll(up, down, watch); }
            catch (Exception) { /* одна сторона закрылась — туннель окончен */ }
        }
        return meter.Reason ?? (ct.IsCancellationRequested ? "запрос прерван" : "сторона закрыла соединение");
    }

    private static async Task CopyAsync(Stream from, Stream to, TunnelMeter meter, CancellationToken ct)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(81920);
        try
        {
            int read;
            while ((read = await from.ReadAsync(buffer, ct)) > 0)
            {
                if (!meter.Count(read)) return;
                await to.WriteAsync(buffer.AsMemory(0, read), ct);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static async Task WatchAsync(TunnelMeter meter, EgressGatewayOptions limits, CancellationToken ct)
    {
        var started = Environment.TickCount64;
        var lifetime = (long)limits.MaxLifetime.TotalMilliseconds;
        var inactivity = (long)limits.InactivityTimeout.TotalMilliseconds;
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(Math.Clamp(Math.Min(lifetime, inactivity) / 4, 10, 1000)));
        while (await timer.WaitForNextTickAsync(ct))
        {
            var now = Environment.TickCount64;
            if (now - started >= lifetime) { meter.Stop("потолок жизни туннеля"); return; }
            if (now - meter.LastActivity >= inactivity) { meter.Stop("бездействие"); return; }
        }
    }

    // Байты и время последней активности туннеля — общие для обеих сторон
    private sealed class TunnelMeter(long maxBytes)
    {
        private long _bytes;
        private long _lastActivity = Environment.TickCount64;
        private string? _reason;

        public long LastActivity => Interlocked.Read(ref _lastActivity);
        public string? Reason => Volatile.Read(ref _reason);

        // false — потолок байтов превышен, порцию не пропускаем
        public bool Count(int read)
        {
            Interlocked.Exchange(ref _lastActivity, Environment.TickCount64);
            if (Interlocked.Add(ref _bytes, read) <= maxBytes) return true;
            Stop("потолок байтов");
            return false;
        }

        public void Stop(string reason) => Interlocked.CompareExchange(ref _reason, reason, null);
    }
}
