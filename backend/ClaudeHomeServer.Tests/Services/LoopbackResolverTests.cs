using System.Net;
using System.Net.Sockets;
using ClaudeHomeServer.Services.ProjectServices;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Services;

/// <summary>
/// Резолв семьи loopback-адресов. Смысл сервиса — увидеть dev-сервер, слушающий ТОЛЬКО
/// ::1 (так по умолчанию делает Node 17+): прежняя проба ходила на 127.0.0.1 и живой
/// порт считала мёртвым.
///
/// IPv6 на раннере CI может быть недоступен, поэтому такие тесты честно скипаются, а не
/// валятся: бинд оборачиваем и при отказе объявляем пропуск.
///
/// Кэш резолвера статичен и ключован по НОМЕРУ порта (TTL 10 с), а порты здесь эфемерные:
/// тот же номер ОС отдаёт следующему тесту, а свежая запись — своя или чужого класса того
/// же testhost'а — отдалась бы чужой семьёй без пробы (LoopbackResolver.cs:64). Поэтому
/// каждый тест снимает чужую запись на входе, сразу после захвата порта, и свою — в finally.
/// </summary>
public class LoopbackResolverTests
{
    /// <summary>Слушающий сокет на свободном порту заданной семьи; null — семья недоступна.</summary>
    private static TcpListener? TryListen(IPAddress address, out int port)
    {
        port = 0;
        try
        {
            var listener = new TcpListener(address, 0);
            listener.Start();
            port = ((IPEndPoint)listener.LocalEndpoint).Port;
            return listener;
        }
        catch (SocketException) { return null; }
    }

    [Fact]
    public async Task Резолвер_находит_порт_по_IPv4()
    {
        var listener = TryListen(IPAddress.Loopback, out var port);
        listener.Should().NotBeNull("IPv4 loopback есть на любой машине");
        LoopbackResolver.Invalidate(port);
        try
        {
            (await LoopbackResolver.ResolveBaseAsync(port)).Should().Be($"http://127.0.0.1:{port}");
            (await LoopbackResolver.IsListeningAsync(port)).Should().BeTrue();
        }
        finally { listener!.Stop(); LoopbackResolver.Invalidate(port); }
    }

    /// <summary>
    /// Главный сценарий, ради которого сервис и заведён: сервис слушает только ::1.
    /// До правки такой порт не находился вовсе.
    /// </summary>
    [SkippableFact]
    public async Task Резолвер_находит_порт_по_IPv6_когда_IPv4_молчит()
    {
        Skip.IfNot(Socket.OSSupportsIPv6, "IPv6 недоступен на этой машине");
        var listener = TryListen(IPAddress.IPv6Loopback, out var port);
        Skip.If(listener is null, "не удалось занять IPv6-порт");
        LoopbackResolver.Invalidate(port);
        try
        {
            // DualMode по умолчанию выключен, значит 127.0.0.1 этот порт не отдаёт —
            // ровно как у Node-сервера на «localhost»
            (await LoopbackResolver.ResolveBaseAsync(port)).Should().Be($"http://[::1]:{port}");
        }
        finally { listener!.Stop(); LoopbackResolver.Invalidate(port); }
    }

    [Fact]
    public async Task Мёртвый_порт_не_резолвится()
    {
        // Свободный порт: заняли и сразу отпустили
        var probe = TryListen(IPAddress.Loopback, out var port);
        probe!.Stop();
        LoopbackResolver.Invalidate(port);

        (await LoopbackResolver.ResolveBaseAsync(port)).Should().BeNull();
        (await LoopbackResolver.IsListeningAsync(port)).Should().BeFalse();
    }

    /// <summary>
    /// Инвариант кэша: он помнит ВЫБОР СЕМЬИ, а не живость порта. Иначе панель «Сервисы»
    /// десять секунд показывала бы «external» у сервиса, умершего секунду назад.
    /// </summary>
    [Fact]
    public async Task Кэш_семьи_не_подменяет_пробу_живости()
    {
        var listener = TryListen(IPAddress.Loopback, out var port);
        listener.Should().NotBeNull();
        LoopbackResolver.Invalidate(port);
        try
        {
            // Заполняем кэш живым портом
            (await LoopbackResolver.ResolveBaseAsync(port)).Should().NotBeNull();

            listener!.Stop();

            // TTL кэша ещё не истёк, но проба обязана сходить на порт и увидеть, что он мёртв
            (await LoopbackResolver.IsListeningAsync(port)).Should().BeFalse(
                "IsListeningAsync проверяет соединением, а не кэшем");
        }
        // finally обязателен на провале: если IsListeningAsync вернул true, значит порт
        // держит чужой процесс и проба записала свою семью (LoopbackResolver.cs:41-44) —
        // без снятия она перешла бы следующему тесту с тем же номером порта.
        finally { LoopbackResolver.Invalidate(port); }
    }

    /// <summary>
    /// Порядок семей: при живом IPv4 берётся он, даже если ::1 тоже слушает. Порты
    /// песочницы docker публикуются именно на 127.0.0.1, и уход на ::1 увёл бы форвард
    /// на посторонний процесс с тем же номером порта.
    /// </summary>
    [SkippableFact]
    public async Task При_двух_живых_семьях_выбирается_IPv4()
    {
        Skip.IfNot(Socket.OSSupportsIPv6, "IPv6 недоступен на этой машине");

        var v6 = TryListen(IPAddress.IPv6Loopback, out var port);
        Skip.If(v6 is null, "не удалось занять IPv6-порт");
        LoopbackResolver.Invalidate(port);

        TcpListener? v4 = null;
        try
        {
            try
            {
                v4 = new TcpListener(IPAddress.Loopback, port);
                v4.Start();
            }
            catch (SocketException)
            {
                Skip.If(true, "ОС не даёт занять один порт в обеих семьях независимо");
            }

            (await LoopbackResolver.ResolveBaseAsync(port)).Should().Be($"http://127.0.0.1:{port}");
        }
        finally { v4?.Stop(); v6!.Stop(); LoopbackResolver.Invalidate(port); }
    }
}
