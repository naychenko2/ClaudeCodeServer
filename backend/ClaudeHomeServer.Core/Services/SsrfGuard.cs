using System.Net;
using System.Net.Sockets;

namespace ClaudeHomeServer.Services;

/// <summary>
/// SSRF-защита для загрузки произвольных пользовательских URL (save-from-url):
/// резолвит хост и запрещает приватные/loopback/link-local/CGNAT-диапазоны,
/// чтобы сервер не мог быть использован как прокси к внутренней сети или
/// облачным metadata-эндпоинтам (169.254.169.254 и т.п.).
/// </summary>
public static class SsrfGuard
{
    /// <summary>Итог проверки адреса — различает приватный адрес и неудачный резолв (нужно ридеру для разных кодов ошибки).</summary>
    public enum AddressCheck { Public, Private, DnsFailed }

    /// <summary>
    /// true, если все адреса, в которые резолвится хост URI, публично маршрутизируемы.
    /// Пустой резолв или любой приватный адрес → false (fail-closed).
    /// </summary>
    public static async Task<bool> IsPubliclyRoutableAsync(Uri uri, CancellationToken ct) =>
        await CheckAsync(uri, ct) == AddressCheck.Public;

    /// <summary>
    /// Резолвит хост и классифицирует результат: публичный / приватный / DNS не ответил.
    /// Ридеру нужны разные коды ошибки (<c>local-address</c> vs <c>dns-failed</c>) — там,
    /// где <see cref="IsPubliclyRoutableAsync"/> схлопывает оба случая в false.
    /// </summary>
    public static async Task<AddressCheck> CheckAsync(Uri uri, CancellationToken ct)
    {
        IPAddress[] addresses;
        if (IPAddress.TryParse(uri.Host, out var literal))
        {
            addresses = [literal];
        }
        else
        {
            try { addresses = await Dns.GetHostAddressesAsync(uri.Host, ct); }
            catch { return AddressCheck.DnsFailed; }
        }
        if (addresses.Length == 0) return AddressCheck.DnsFailed;
        return addresses.All(IsPublic) ? AddressCheck.Public : AddressCheck.Private;
    }

    /// <summary>Публично ли маршрутизируем адрес (не приватный/loopback/link-local и т.п.).</summary>
    public static bool IsPublic(IPAddress ip)
    {
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        if (IPAddress.IsLoopback(ip)) return false;

        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = ip.GetAddressBytes();
            // 0.0.0.0/8, 10/8, 127/8
            if (b[0] is 0 or 10 or 127) return false;
            // 169.254/16 link-local (в т.ч. cloud metadata 169.254.169.254)
            if (b[0] == 169 && b[1] == 254) return false;
            // 172.16/12
            if (b[0] == 172 && b[1] is >= 16 and <= 31) return false;
            // 192.168/16
            if (b[0] == 192 && b[1] == 168) return false;
            // 100.64/10 CGNAT
            if (b[0] == 100 && b[1] is >= 64 and <= 127) return false;
            // 224.0.0.0/4 multicast
            if (b[0] is >= 224 and <= 239) return false;
            // 240.0.0.0/4 reserved (включает 255.255.255.255 broadcast)
            if (b[0] >= 240) return false;
            // 198.18.0.0/15 benchmark
            if (b[0] == 198 && b[1] is 18 or 19) return false;
            return true;
        }

        // IPv6
        if (ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || ip.IsIPv6Multicast) return false;
        if (ip.Equals(IPAddress.IPv6Any) || ip.Equals(IPAddress.IPv6Loopback)) return false;
        // Unique local fc00::/7
        var v6 = ip.GetAddressBytes();
        if ((v6[0] & 0xFE) == 0xFC) return false;
        // NAT64 64:ff9b::/96 — встраивает IPv4 в последние 32 бита (64:ff9b::7f00:1 = 127.0.0.1)
        if (v6[0] == 0x00 && v6[1] == 0x64 && v6[2] == 0xFF && v6[3] == 0x9B &&
            v6[4] == 0 && v6[5] == 0 && v6[6] == 0 && v6[7] == 0 &&
            v6[8] == 0 && v6[9] == 0 && v6[10] == 0 && v6[11] == 0)
        {
            var embedded = new IPAddress([v6[12], v6[13], v6[14], v6[15]]);
            if (!IsPublic(embedded)) return false;
        }
        return true;
    }
}
