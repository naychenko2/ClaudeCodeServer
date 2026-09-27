using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace ClaudeHomeServer.Services.Llm.Gateway;

// SSRF-граница туннеля выхода (задача 2.9): куда сервер НЕ пускает трафик CLI устройства.
// Проверяется уже разрешённый адрес, а не имя: имя, которое ведёт во внутреннюю сеть (или
// начнёт вести после проверки — DNS-rebinding), отсекается здесь, а соединение потом идёт
// ровно на проверенный адрес.
public class EgressAddressPolicy
{
    // Закрытые диапазоны IPv4: «этот» хост, RFC1918, CGNAT, loopback, link-local (там же
    // метаданные облака 169.254.169.254), служебные IETF, бенчмарки, мультикаст, резерв и broadcast
    private static readonly (uint Network, int Prefix)[] ForbiddenV4 =
    [
        (0x00000000, 8), (0x0A000000, 8), (0x64400000, 10), (0x7F000000, 8), (0xA9FE0000, 16),
        (0xAC100000, 12), (0xC0000000, 24), (0xC0A80000, 16), (0xC6120000, 15), (0xE0000000, 4),
        (0xF0000000, 4),
    ];

    public virtual bool IsForbidden(IPAddress address) => IsForbiddenRange(address) || IsOwnAddress(address);

    public static bool IsForbiddenRange(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (address.AddressFamily == AddressFamily.InterNetwork) return IsForbiddenV4(address);
        if (address.AddressFamily != AddressFamily.InterNetworkV6) return true;

        if (address.Equals(IPAddress.IPv6Any) || address.Equals(IPAddress.IPv6Loopback)
            || address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || address.IsIPv6Multicast)
            return true;
        var bytes = address.GetAddressBytes();
        // ULA fc00::/7 (там же метаданные AWS fd00:ec2::254)
        if ((bytes[0] & 0xFE) == 0xFC) return true;
        // Встроенный IPv4 не должен уводить за границу: NAT64 64:ff9b::/96 и 6to4 2002::/16
        if (bytes[0] == 0x00 && bytes[1] == 0x64 && bytes[2] == 0xFF && bytes[3] == 0x9B && bytes[4..12].All(b => b == 0))
            return IsForbiddenV4(new IPAddress(bytes[12..16]));
        if (bytes[0] == 0x20 && bytes[1] == 0x02)
            return IsForbiddenV4(new IPAddress(bytes[2..6]));
        // Teredo 2001::/32 закрыт целиком: сервер Teredo и клиентский IPv4 в адресе ведут в
        // туннель. Сравниваются ровно 32 бита — по 16 отрезали бы публичный 2001:4860:: и др.
        if (bytes[0] == 0x20 && bytes[1] == 0x01 && bytes[2] == 0x00 && bytes[3] == 0x00)
            return true;
        return false;
    }

    private static bool IsForbiddenV4(IPAddress address)
    {
        var b = address.GetAddressBytes();
        var value = (uint)(b[0] << 24 | b[1] << 16 | b[2] << 8 | b[3]);
        foreach (var (network, prefix) in ForbiddenV4)
        {
            var mask = uint.MaxValue << (32 - prefix);
            if ((value & mask) == network) return true;
        }
        return false;
    }

    // Адреса самого сервера: публичный адрес машины за границей диапазонов выше не отсекается
    private static bool IsOwnAddress(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .SelectMany(i => i.GetIPProperties().UnicastAddresses)
                .Any(u => u.Address.Equals(address));
        }
        catch (NetworkInformationException)
        {
            // Перечень интерфейсов не получен — пускать наугад нельзя
            return true;
        }
    }
}
