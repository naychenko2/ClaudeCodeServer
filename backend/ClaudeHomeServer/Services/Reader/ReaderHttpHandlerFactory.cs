using System.Net;

namespace ClaudeHomeServer.Services.Reader;

/// <summary>
/// Строит <see cref="SocketsHttpHandler"/> клиента "link-reader" (ADR-005, раздел 2):
/// без кук/креденшалов/авто-редиректов/системного egress-прокси, с <c>ConnectCallback</c>,
/// который сам резолвит хост прямо перед подключением и режет приватные адреса — вторая,
/// TOCTOU-safe линия обороны поверх предварительной проверки в <see cref="ReaderService"/>.
/// Вынесено из Program.cs отдельным классом, чтобы реальную логику подключения можно было
/// проверить тестом (см. ReaderHttpHandlerFactoryTests) без поднятия всего хоста.
///
/// <c>UseProxy = false</c>, а не общий <c>WithoutEgressProxy()</c> из QuietHttpLogger.cs:
/// тот подменяет весь handler на голый <c>HttpClientHandler</c>, а этому клиенту нужен
/// именно <c>SocketsHttpHandler</c> с <c>ConnectCallback</c> ниже. Egress-прокси проверен
/// (задача SSRF-обхода, 2026-08-03): и HTTP forward, и HTTPS CONNECT релеят на приватные
/// и loopback-адреса без всякой фильтрации (см. ADR-005, раздел 2) — соединение через
/// прокси означало бы, что реальный IP цели проверять некому. Поэтому единственный
/// поддерживаемый режим — прямое соединение, и <c>ConnectCallback</c> резолвит и
/// фильтрует его без оглядки на прокси-переменные окружения.
/// </summary>
public static class ReaderHttpHandlerFactory
{
    public static SocketsHttpHandler Create() => new()
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        PreAuthenticate = false,
        UseProxy = false,
        AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
        ConnectTimeout = TimeSpan.FromSeconds(5),
        ConnectCallback = ConnectAsync,
    };

    private static async ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext context, CancellationToken ct)
    {
        // Прокси отключён (UseProxy = false выше), поэтому соединение всегда прямое:
        // резолвим сами прямо перед connect и фильтруем по SsrfGuard. Закрывает TOCTOU-окно
        // между предварительной проверкой в ReaderService и реальным подключением (DNS rebinding).
        IPAddress[] addresses;
        try
        {
            addresses = IPAddress.TryParse(context.DnsEndPoint.Host, out var literal)
                ? [literal]
                : await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, ct);
        }
        catch
        {
            throw new ReaderConnectBlockedException(dnsFailed: true);
        }

        var publicAddress = addresses.FirstOrDefault(SsrfGuard.IsPublic);
        if (publicAddress is null)
            throw new ReaderConnectBlockedException(dnsFailed: addresses.Length == 0);

        return await ReaderConnect.RawAsync(new IPEndPoint(publicAddress, context.DnsEndPoint.Port), ct);
    }
}
