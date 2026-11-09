using System.Net;
using System.Net.Sockets;
using ClaudeHomeServer.Services.Llm;
using FluentAssertions;
using Microsoft.Extensions.Configuration;

namespace ClaudeHomeServer.Tests.Services;

// Проба выхода в сеть: разбор адреса прокси и поведение «прокси не задан».
// Живой TCP-коннект здесь НЕ проверяется — это среда, а не логика (и CI не должен
// зависеть от занятых портов); поведение под лежащим каналом покрыто на уровне
// адаптера через подставную пробу.
public class EgressProbeTests
{
    [Theory]
    [InlineData("http://192.168.7.208:2080", "192.168.7.208", 2080)]
    [InlineData("https://proxy.local:8443", "proxy.local", 8443)]
    // Без схемы — законное значение переменной окружения, достраиваем сами
    [InlineData("192.168.7.208:2080", "192.168.7.208", 2080)]
    // Порт не указан — берём по схеме
    [InlineData("http://proxy.local", "proxy.local", 80)]
    [InlineData("https://proxy.local", "proxy.local", 443)]
    // socks5 Uri не знает — порт по умолчанию подставляем сами
    [InlineData("socks5://127.0.0.1", "127.0.0.1", 1080)]
    [InlineData("  http://proxy.local:3128  ", "proxy.local", 3128)]
    public void РазборАдресаПрокси(string raw, string host, int port)
    {
        EgressProbe.TryParseProxy(raw, out var parsed).Should().BeTrue();
        parsed.Host.Should().Be(host);
        parsed.Port.Should().Be(port);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ПустоеЗначение_ЭтоНеПрокси(string? raw) =>
        EgressProbe.TryParseProxy(raw, out _).Should().BeFalse();

    [Fact]
    public async Task ПроксиНеЗадан_КаналСчитаетсяЖивым()
    {
        // Инвариант fail-open: у инстанса без прокси проверять нечего, и фолбэк обязан
        // вести себя ровно как раньше — иначе правка «сломала бы» дефолтную установку
        var probe = new EgressProbe(proxy: null);

        (await probe.IsDownAsync()).Should().BeFalse();
        probe.ProxyAddress.Should().BeNull();
    }

    [Fact]
    public void АдресБерётсяИзКонфигаПесочницы_КогдаВОкруженииПроксиНет()
    {
        // У container-владельцев прокси задаётся Sandbox:Proxy и раздаётся контейнеру, а в env
        // бэкенда на хосте его нет вовсе — без второго источника ветка отказа канала была бы
        // мертва ровно в той топологии, где она нужнее всего.
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Sandbox:Proxy"] = "http://192.168.7.208:2080",
            }).Build();

        EgressProbe.ReadProxyFromConfig(config).Should().Be(("192.168.7.208", 2080));
        EgressProbe.ReadProxyFromConfig(new ConfigurationBuilder().Build()).Should().BeNull();
    }

    [Fact]
    public async Task ПортНеСлушает_КаналЛежит()
    {
        // Порт 1 на loopback не слушает никто ни на Windows, ни на Linux-раннере CI:
        // соединение отбивается мгновенно, таймаут не задействуется
        var probe = new EgressProbe(("127.0.0.1", 1), timeout: TimeSpan.FromMilliseconds(400));

        (await probe.IsDownAsync()).Should().BeTrue();
        probe.ProxyAddress.Should().Be("127.0.0.1:1");
    }

    [Fact]
    public async Task РезультатКешируется_ПачкаШаговЦепочкиНеДолбитКанал()
    {
        // Кеш нужен, чтобы шаги цепочки, идущие секунда в секунду, не превращались в очередь
        // TCP-коннектов. Доказательство — смена состояния канала МЕЖДУ вызовами: пока кеш жив,
        // вердикт не меняется, а проба без кеша тот же канал видит уже мёртвым.
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        try
        {
            var cached = new EgressProbe(("127.0.0.1", port), cacheFor: TimeSpan.FromMinutes(5));
            (await cached.IsDownAsync()).Should().BeFalse("порт слушает — канал жив");

            listener.Stop();

            (await cached.IsDownAsync()).Should().BeFalse("кеш держит прежний вердикт");
            var fresh = new EgressProbe(("127.0.0.1", port), cacheFor: TimeSpan.Zero);
            (await fresh.IsDownAsync()).Should().BeTrue("без кеша виден реальный отказ");
        }
        finally
        {
            try { listener.Stop(); } catch (SocketException) { /* уже остановлен */ }
        }
    }
}
