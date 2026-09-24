using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Configuration;

namespace ClaudeHomeServer.Services.Llm.Gateway;

// Серверная сторона туннеля выхода (задача 2.9): разрешает имя, отсекает запрещённые адреса
// (EgressAddressPolicy) и открывает TCP до ПРОВЕРЕННОГО адреса — через тот же исходящий
// прокси, что у серверного CLI (HTTPS_PROXY… окружения сервера, иначе Sandbox:Proxy; порядок
// как у EgressProbe). Прокси получает CONNECT на IP, а не на имя: иначе он разрешил бы имя
// заново, и проверка адреса ничего бы не стоила.
public class EgressConnector
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(15);
    private const int MaxProxyHeadBytes = 8 * 1024;

    private readonly Uri? _proxy;
    private readonly EgressAddressPolicy _policy;

    public EgressConnector(IConfiguration config)
        : this(ReadProxy(config), new EgressAddressPolicy()) { }

    internal EgressConnector(Uri? proxy, EgressAddressPolicy policy)
    {
        _proxy = proxy;
        _policy = policy;
    }

    public sealed record Resolved(IPAddress[]? Addresses, int Status, string Outcome);

    public sealed record Opened(Stream? Stream, int Status, string Outcome);

    // Разрешение имени и SSRF-проверка. Хоть один запрещённый адрес — отказ целиком: у
    // публичного имени внутренних адресов не бывает, а смешанный ответ — признак подмены.
    public virtual async Task<Resolved> ResolveAsync(string host, CancellationToken ct)
    {
        IPAddress[] addresses;
        if (IPAddress.TryParse(host, out var literal)) addresses = [literal];
        else
        {
            try { addresses = await Dns.GetHostAddressesAsync(host, ct); }
            catch (SocketException) { return new(null, StatusCodes.Status502BadGateway, "имя не разрешилось"); }
        }

        if (addresses.Length == 0) return new(null, StatusCodes.Status502BadGateway, "имя не разрешилось");
        if (addresses.Any(_policy.IsForbidden)) return new(null, StatusCodes.Status403Forbidden, "адрес назначения запрещён");
        return new(addresses, 0, "");
    }

    public virtual async Task<Opened> ConnectAsync(IPAddress[] addresses, int port, CancellationToken ct)
    {
        if (_proxy is not null && _proxy.Scheme != Uri.UriSchemeHttp)
            return new(null, StatusCodes.Status502BadGateway, $"схема прокси {_proxy.Scheme} не поддерживается");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(ConnectTimeout);
        var outcome = "соединение не установлено";
        foreach (var address in addresses)
        {
            var client = _proxy is null ? new TcpClient(address.AddressFamily) : new TcpClient();
            try
            {
                if (_proxy is null)
                {
                    await client.ConnectAsync(address, port, timeout.Token);
                    return new(client.GetStream(), 0, "");
                }

                await client.ConnectAsync(_proxy.Host, _proxy.Port, timeout.Token);
                var stream = client.GetStream();
                var status = await ProxyConnectAsync(stream, address, port, timeout.Token);
                if (status == 200) return new(stream, 0, "");
                outcome = $"прокси отказал ({status})";
                client.Dispose();
            }
            catch (Exception e) when (e is SocketException or IOException or OperationCanceledException)
            {
                outcome = e is OperationCanceledException ? "таймаут соединения" : "соединение не установлено";
                client.Dispose();
                if (ct.IsCancellationRequested) break;
            }
        }
        return new(null, StatusCodes.Status502BadGateway, outcome);
    }

    // CONNECT к прокси на проверенный IP; возвращает код ответа прокси (0 — ответ не разобран)
    private async Task<int> ProxyConnectAsync(NetworkStream stream, IPAddress address, int port, CancellationToken ct)
    {
        var authority = address.AddressFamily == AddressFamily.InterNetworkV6 ? $"[{address}]:{port}" : $"{address}:{port}";
        var head = new StringBuilder($"CONNECT {authority} HTTP/1.1\r\nHost: {authority}\r\n");
        if (!string.IsNullOrEmpty(_proxy!.UserInfo))
            head.Append("Proxy-Authorization: Basic ")
                .Append(Convert.ToBase64String(Encoding.UTF8.GetBytes(Uri.UnescapeDataString(_proxy.UserInfo))))
                .Append("\r\n");
        head.Append("\r\n");
        await stream.WriteAsync(Encoding.ASCII.GetBytes(head.ToString()), ct);

        // Читаем ответ побайтно до пустой строки: всё, что дальше, — уже байты туннеля
        var response = new List<byte>(256);
        var one = new byte[1];
        while (response.Count < MaxProxyHeadBytes)
        {
            if (await stream.ReadAsync(one, ct) == 0) return 0;
            response.Add(one[0]);
            if (response.Count >= 4 && response[^4] == '\r' && response[^3] == '\n' && response[^2] == '\r' && response[^1] == '\n')
                break;
        }
        var statusLine = Encoding.ASCII.GetString(response.ToArray()).Split("\r\n", 2)[0].Split(' ');
        return statusLine.Length >= 2 && statusLine[0].StartsWith("HTTP/1.", StringComparison.Ordinal)
            && int.TryParse(statusLine[1], out var code) ? code : 0;
    }

    internal static Uri? ReadProxy(IConfiguration config)
    {
        foreach (var raw in new[] { "HTTPS_PROXY", "https_proxy", "HTTP_PROXY", "http_proxy", "ALL_PROXY", "all_proxy" }
                     .Select(Environment.GetEnvironmentVariable).Append(config["Sandbox:Proxy"]))
            if (TryParseProxy(raw) is { } proxy) return proxy;
        return null;
    }

    internal static Uri? TryParseProxy(string? raw)
    {
        if (!EgressProbe.TryParseProxy(raw, out _)) return null;
        var text = raw!.Trim();
        if (!text.Contains("://", StringComparison.Ordinal)) text = "http://" + text;
        return new Uri(text);
    }
}
