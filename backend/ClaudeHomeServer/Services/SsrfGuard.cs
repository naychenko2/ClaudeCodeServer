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
    /// <summary>
    /// true, если все адреса, в которые резолвится хост URI, публично маршрутизируемы.
    /// Пустой резолв или любой приватный адрес → false (fail-closed).
    /// </summary>
    public static async Task<bool> IsPubliclyRoutableAsync(Uri uri, CancellationToken ct)
    {
        IPAddress[] addresses;
        if (IPAddress.TryParse(uri.Host, out var literal))
        {
            addresses = [literal];
        }
        else
        {
            try { addresses = await Dns.GetHostAddressesAsync(uri.Host, ct); }
            catch { return false; }
        }
        return addresses.Length > 0 && addresses.All(IsPublic);
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
            return true;
        }

        // IPv6
        if (ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || ip.IsIPv6Multicast) return false;
        if (ip.Equals(IPAddress.IPv6Any) || ip.Equals(IPAddress.IPv6Loopback)) return false;
        // Unique local fc00::/7
        var v6 = ip.GetAddressBytes();
        if ((v6[0] & 0xFE) == 0xFC) return false;
        return true;
    }
}
