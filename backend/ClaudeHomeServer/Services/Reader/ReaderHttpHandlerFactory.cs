using System.Net;

namespace ClaudeHomeServer.Services.Reader;

/// <summary>
/// Строит <see cref="SocketsHttpHandler"/> клиента "link-reader" (ADR-005, раздел 2):
/// без кук/креденшалов/авто-редиректов, с <c>ConnectCallback</c>, который сам резолвит хост
/// прямо перед подключением и режет приватные адреса — вторая, TOCTOU-safe линия обороны
/// поверх предварительной проверки в <see cref="ReaderService"/>. Вынесено из Program.cs
/// отдельным классом, чтобы реальную логику подключения можно было проверить тестом
/// (см. ReaderHttpHandlerFactoryTests) без поднятия всего хоста.
/// </summary>
public static class ReaderHttpHandlerFactory
{
    public static SocketsHttpHandler Create() => new()
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        PreAuthenticate = false,
        AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
        ConnectTimeout = TimeSpan.FromSeconds(5),
        ConnectCallback = ConnectAsync,
    };

    private static async ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext context, CancellationToken ct)
    {
        var requestHost = context.InitialRequestMessage.RequestUri?.Host;
        var isDirect = string.Equals(context.DnsEndPoint.Host, requestHost, StringComparison.OrdinalIgnoreCase);

        if (!isDirect)
        {
            // Прокси задан и цель вне NO_PROXY — соединение реально идёт к прокси, не к цели.
            // Проверять его адрес бессмысленно (ADR: «периметр там держится на прокси»).
            return await ReaderConnect.RawAsync(context.DnsEndPoint, ct);
        }

        // Прямое соединение (прокси не задан ИЛИ цель в NO_PROXY) — резолвим сами прямо
        // перед connect и фильтруем по SsrfGuard. Закрывает TOCTOU-окно между предварительной
        // проверкой в ReaderService и реальным подключением (DNS rebinding).
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
