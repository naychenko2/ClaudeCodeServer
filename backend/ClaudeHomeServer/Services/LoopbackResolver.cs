using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;

namespace ClaudeHomeServer.Services;

/// <summary>
/// Определяет, по какой семье loopback-адресов отвечает порт, и собирает базовый URL
/// для форварда на него.
///
/// Зачем: Node 17+ резолвит «localhost» в ::1 и слушает ТОЛЬКО его, поэтому проба и
/// прокси, прибитые к 127.0.0.1, такой dev-сервер не видят вовсе — соединение
/// отвергается, и в панели «Сервисы» живой сервис остаётся «idle».
///
/// Порядок семей ЖЁСТКИЙ: сначала IPv4, ::1 — только когда IPv4 отказал. Порты
/// песочницы docker публикуются именно на 127.0.0.1 (см. DockerProcessRunner), и
/// предпочтение ::1 могло бы увести форвард на посторонний процесс, занявший тот же
/// номер порта в другой семье.
/// </summary>
public static class LoopbackResolver
{
    /// <summary>
    /// Кэш хранит ВЫБОР СЕМЬИ адресов для порта, а НЕ факт живости порта: живость
    /// проверяется соединением каждый раз заново. Иначе панель показывала бы «external»
    /// у сервиса, умершего секунду назад, — а статус там и означает «порт отвечает».
    /// </summary>
    private static readonly ConcurrentDictionary<int, (DateTime At, AddressFamily Family)> Cache = new();

    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(10);

    // Бюджет прежней пробы в DevServerService: соединение идёт на loopback, отказ там
    // приходит мгновенно, ждать дольше нечего — таймаут нужен лишь от зависшего стека.
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromMilliseconds(400);

    /// <summary>
    /// Реально соединяется с портом и возвращает семью, которая ответила (null — не
    /// ответила ни одна). Кэш ОБНОВЛЯЕТ, но не читает: это проба живости.
    /// </summary>
    public static async Task<AddressFamily?> ProbeAsync(int port)
    {
        if (await TryConnectAsync(IPAddress.Loopback, port))
            return Remember(port, AddressFamily.InterNetwork);
        if (Socket.OSSupportsIPv6 && await TryConnectAsync(IPAddress.IPv6Loopback, port))
            return Remember(port, AddressFamily.InterNetworkV6);

        // Порт не отвечает — прежний выбор семьи протух вместе с процессом
        Cache.TryRemove(port, out _);
        return null;
    }

    /// <summary>Принимает ли кто-то соединения на loopback-порту (любой семьи).</summary>
    public static async Task<bool> IsListeningAsync(int port) => await ProbeAsync(port) is not null;

    /// <summary>
    /// Базовый URL для форварда на loopback-порт: «http://127.0.0.1:{порт}» либо
    /// «http://[::1]:{порт}». null — порт не отвечает ни по одной семье.
    ///
    /// Горячий путь прокси: при свежем кэше семья берётся из него без пробы — соединение
    /// форварда всё равно либо установится, либо нет, и лишний коннект на каждый
    /// запрос ничего не проверяет, зато удваивает их число.
    /// </summary>
    public static async Task<string?> ResolveBaseAsync(int port)
    {
        if (Cache.TryGetValue(port, out var c) && DateTime.UtcNow - c.At < CacheTtl)
            return BaseFor(c.Family, port);
        return await ProbeAsync(port) is { } family ? BaseFor(family, port) : null;
    }

    /// <summary>
    /// Забыть выбранную для порта семью. Зовётся, когда форвард не удался: процесс мог
    /// смениться на другой, слушающий по другой семье.
    /// </summary>
    public static void Invalidate(int port) => Cache.TryRemove(port, out _);

    private static AddressFamily Remember(int port, AddressFamily family)
    {
        Cache[port] = (DateTime.UtcNow, family);
        return family;
    }

    private static string BaseFor(AddressFamily family, int port) =>
        family == AddressFamily.InterNetworkV6 ? $"http://[::1]:{port}" : $"http://127.0.0.1:{port}";

    private static async Task<bool> TryConnectAsync(IPAddress address, int port)
    {
        try
        {
            using var client = new TcpClient(address.AddressFamily);
            var connect = client.ConnectAsync(address, port);
            var ok = await Task.WhenAny(connect, Task.Delay(ConnectTimeout)) == connect;
            if (connect.IsFaulted) _ = connect.Exception; // погасить unobserved
            return ok && connect.IsCompletedSuccessfully && client.Connected;
        }
        catch { return false; }
    }
}
