using System.Buffers;
using System.IO.Pipelines;
using System.Text;
using ClaudeHomeServer.Protocol;
using Microsoft.AspNetCore.Connections;
using Microsoft.Extensions.Logging;

namespace ClaudeHomeServer.DeviceAgent.Sidecar;

/// <summary>
/// <c>HTTPS_PROXY</c> CLI смотрит в сайдкар (ADR-016 §2): прочий HTTPS-трафик CLI (WebFetch,
/// телеметрия) приходит сюда запросом <c>CONNECT host:port</c>. Kestrel такой запрос
/// принимает, но сырого потока не отдаёт, поэтому туннель живёт на уровне соединения:
/// первые байты подсматриваются, <c>CONNECT</c> обслуживается здесь, всё прочее уходит в
/// обычный HTTP-конвейер сайдкара нетронутым.
///
/// Наружу с машины сайдкар сам не ходит (задача 2.9): туннель едет на сервер
/// (<see cref="IEgressTunnelOpener"/>), и уже сервер решает, куда его пустить. Ход CONNECT
/// опознаётся по <c>Proxy-Authorization</c> — CLI берёт её из учётки в адресе прокси
/// (<see cref="DeviceEgressRoutes.ProxyUrl"/>). TLS идёт насквозь, содержимого сайдкар не видит.
/// Трафик Bash харнеса мимо прокси по-прежнему возможен: это маршрутизация, а не граница.
/// </summary>
internal static class ConnectTunnel
{
    private const int MaxHeadBytes = 8 * 1024;
    private static readonly byte[] ConnectPrefix = "CONNECT "u8.ToArray();
    private static readonly byte[] HeadEnd = "\r\n\r\n"u8.ToArray();

    public static Func<ConnectionDelegate, ConnectionDelegate> Middleware(ILogger log, TurnGrants grants, IEgressTunnelOpener egress) =>
        next => async context =>
        {
            var input = context.Transport.Input;
            while (true)
            {
                var result = await input.ReadAsync(context.ConnectionClosed);
                var buffer = result.Buffer;
                var decided = Decide(buffer, out var isConnect, out var headLength);

                if (!decided && !result.IsCompleted && buffer.Length < MaxHeadBytes)
                {
                    // Мало байт для решения — ничего не потребляем, ждём ещё
                    input.AdvanceTo(buffer.Start, buffer.End);
                    continue;
                }

                if (!decided || !isConnect)
                {
                    input.AdvanceTo(buffer.Start);
                    await next(context);
                    return;
                }

                var head = Encoding.ASCII.GetString(buffer.Slice(0, headLength).ToArray());
                input.AdvanceTo(buffer.GetPosition(headLength));
                await ServeAsync(context, head, log, grants, egress);
                return;
            }
        };

    // Решено ли по уже пришедшим байтам: не CONNECT — сразу по префиксу; CONNECT — когда
    // пришёл весь заголовок запроса
    internal static bool Decide(ReadOnlySequence<byte> buffer, out bool isConnect, out int headLength)
    {
        isConnect = false;
        headLength = 0;
        var probeLength = (int)Math.Min(buffer.Length, ConnectPrefix.Length);
        var probe = buffer.Slice(0, probeLength).ToArray();
        if (!ConnectPrefix.AsSpan(0, probeLength).SequenceEqual(probe)) return true;
        if (probeLength < ConnectPrefix.Length) return false;

        var reader = new SequenceReader<byte>(buffer);
        if (!reader.TryReadTo(out ReadOnlySequence<byte> _, HeadEnd, advancePastDelimiter: true)) return false;
        isConnect = true;
        headLength = (int)reader.Consumed;
        return true;
    }

    internal static bool TryParseTarget(string head, out string host, out int port)
    {
        host = "";
        port = 0;
        var firstLine = head.Split("\r\n", 2)[0];
        var parts = firstLine.Split(' ');
        if (parts.Length != 3 || parts[0] != "CONNECT" || !parts[2].StartsWith("HTTP/1.", StringComparison.Ordinal))
            return false;

        var authority = parts[1];
        var colon = authority.LastIndexOf(':');
        if (colon <= 0 || !int.TryParse(authority[(colon + 1)..], out port) || port is < 1 or > 65535) return false;
        host = authority[..colon].Trim('[', ']');
        return host.Length > 0 && Uri.CheckHostName(host) != UriHostNameType.Unknown;
    }

    // Ключ хода сайдкара из Proxy-Authorization: Basic base64("turn:{ключ}")
    internal static string? ProxyKey(string head)
    {
        foreach (var line in head.Split("\r\n").Skip(1))
        {
            var colon = line.IndexOf(':');
            if (colon <= 0 || !line[..colon].Trim().Equals("Proxy-Authorization", StringComparison.OrdinalIgnoreCase)) continue;
            var value = line[(colon + 1)..].Trim();
            if (!value.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase)) return null;
            try
            {
                var pair = Encoding.UTF8.GetString(Convert.FromBase64String(value[6..].Trim()));
                var sep = pair.IndexOf(':');
                return sep > 0 && pair[..sep] == DeviceEgressRoutes.ProxyUser ? pair[(sep + 1)..] : null;
            }
            catch (FormatException)
            {
                return null;
            }
        }
        return null;
    }

    private static async Task ServeAsync(ConnectionContext context, string head, ILogger log, TurnGrants grants, IEgressTunnelOpener egress)
    {
        var output = context.Transport.Output;
        if (!TryParseTarget(head, out var host, out var port))
        {
            await WriteStatusAsync(output, "400 Bad Request");
            return;
        }

        if (ProxyKey(head) is not { } key || !grants.TryGet(key, out var grant))
        {
            await WriteStatusAsync(output, "407 Proxy Authentication Required", "Proxy-Authenticate: Basic realm=\"ai-home\"\r\n");
            return;
        }
        if (grant is null)
        {
            // Сервер не выдал ходу шлюз — выпускать наружу некому, а прямого выхода нет
            await WriteStatusAsync(output, "503 Service Unavailable");
            return;
        }

        EgressTunnel tunnel;
        using (var openTimeout = CancellationTokenSource.CreateLinkedTokenSource(context.ConnectionClosed))
        {
            openTimeout.CancelAfter(TimeSpan.FromSeconds(30));
            tunnel = await egress.OpenAsync(grant, host, port, openTimeout.Token);
        }
        if (tunnel.Stream is null)
        {
            log.LogInformation("Сайдкар: CONNECT {Host}:{Port} — сервер отказал ({Status})", host, port, tunnel.Status);
            await WriteStatusAsync(output, tunnel.Status switch
            {
                403 => "403 Forbidden",
                504 => "504 Gateway Timeout",
                _ => "502 Bad Gateway",
            });
            return;
        }

        log.LogDebug("Сайдкар: туннель CONNECT {Host}:{Port} через сервер", host, port);
        await WriteStatusAsync(output, "200 Connection Established");

        await using var remote = tunnel.Stream;
        using var done = CancellationTokenSource.CreateLinkedTokenSource(context.ConnectionClosed);
        var up = context.Transport.Input.CopyToAsync(remote, done.Token);
        var down = remote.CopyToAsync(context.Transport.Output, done.Token);
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

    private static async Task WriteStatusAsync(PipeWriter output, string status, string headers = "")
    {
        await output.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 {status}\r\n{headers}\r\n"));
        await output.FlushAsync();
    }
}
