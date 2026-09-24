using System.Net;
using System.Net.WebSockets;
using ClaudeHomeServer.Protocol;

namespace ClaudeHomeServer.DeviceAgent.Sidecar;

/// <summary>Туннель CONNECT, открытый сервером: поток — при успехе, иначе код отказа сервера.</summary>
internal sealed record EgressTunnel(Stream? Stream, int Status);

/// <summary>Кто открывает туннель выхода наружу для CONNECT CLI (задача 2.9).</summary>
internal interface IEgressTunnelOpener
{
    Task<EgressTunnel> OpenAsync(DeviceExecGateway grant, string host, int port, CancellationToken ct);
}

/// <summary>
/// Туннель через сервер: WebSocket на <c>/gw/t/{ход}/egress?host=…&amp;port=…</c> с токеном
/// устройства, отпечатком и токеном хода — те же заголовки, что у LLM/MCP сайдкара. Отказ
/// сервер отдаёт кодом до апгрейда. Системный прокси машины не используется: адрес сервера
/// задан явно, а прокси увидел бы токены в заголовках.
/// </summary>
internal sealed class ServerEgressTunnelOpener(IDeviceIdentity device) : IEgressTunnelOpener
{
    // Сколько закрытие туннеля ждёт штатного закрытия WebSocket, прежде чем оборвать его
    private static readonly TimeSpan CloseTimeout = TimeSpan.FromSeconds(2);

    public async Task<EgressTunnel> OpenAsync(DeviceExecGateway grant, string host, int port, CancellationToken ct)
    {
        var socket = new ClientWebSocket();
        socket.Options.CollectHttpResponseDetails = true;
        socket.Options.Proxy = null;
        socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
        socket.Options.SetRequestHeader("Authorization", SidecarProxy.DeviceAuthPrefix + device.DeviceToken);
        socket.Options.SetRequestHeader(SidecarProxy.FingerprintHeader, device.Fingerprint);
        socket.Options.SetRequestHeader(SidecarProxy.TurnTokenHeader, grant.Token);

        var builder = new UriBuilder(new Uri(device.ServerUri, DeviceEgressRoutes.GatewayPath(grant.TurnId)))
        {
            Query = $"{DeviceEgressRoutes.HostQuery}={Uri.EscapeDataString(host)}&{DeviceEgressRoutes.PortQuery}={port}",
        };
        builder.Scheme = builder.Scheme == Uri.UriSchemeHttps ? "wss" : "ws";

        try
        {
            await socket.ConnectAsync(builder.Uri, ct);
            return new EgressTunnel(WebSocketStream.Create(socket, WebSocketMessageType.Binary, CloseTimeout), 0);
        }
        catch (Exception e) when (e is WebSocketException or HttpRequestException or OperationCanceledException)
        {
            var status = socket.HttpStatusCode;
            socket.Dispose();
            return new EgressTunnel(null, status == 0 ? (int)HttpStatusCode.BadGateway : (int)status);
        }
    }
}
