using System.Net;
using System.Net.Sockets;
using ClaudeHomeServer.Services.Reader;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Services.Reader;

// Проверяет РЕАЛЬНУЮ проводку клиента "link-reader" (ConnectCallback), не стаб — единственный
// офлайн-проверяемый кусок чек-листа ADR-005 ("ConnectCallback подключён всегда"): режим прямого
// соединения (без прокси в этом процессе) обязан резолвить сам и резать приватные адреса даже
// если запрос формально шёл на localhost-порт, поднятый самим тестом. Живой стенд с настоящим
// egress-прокси (CLAUDE_EGRESS_PROXY) проверен вручную из контейнера claude-server (задача
// SSRF-обхода, 2026-08-03): и HTTP forward, и HTTPS CONNECT релеят на приватные/loopback-адреса
// без фильтрации — см. ADR-005, раздел 2. Отсюда регрессионный тест ниже: клиент обязан игнорировать
// системный прокси из окружения, а не полагаться на его периметр.
public class ReaderHttpHandlerFactoryTests
{
    [Fact]
    public async Task ПрямоеСоединение_НаЛокальныйПорт_РежетсяКакПриватныйАдрес()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var acceptTask = listener.AcceptTcpClientAsync();

        using var handler = ReaderHttpHandlerFactory.Create();
        using var client = new HttpClient(handler);

        var act = async () => await client.GetAsync($"http://127.0.0.1:{port}/", CancellationToken.None);

        await act.Should().ThrowAsync<HttpRequestException>();
        acceptTask.IsCompleted.Should().BeFalse("ConnectCallback обязан остановить подключение до сокета");
    }

    [Fact]
    public void Handler_НеИспользуетСистемныйПрокси()
    {
        // Egress-прокси релеит запросы во внутреннюю сеть без фильтрации (см. ADR-005) —
        // если бы клиент шёл через него, ConnectCallback проверял бы адрес прокси, а не цели,
        // и весь SSRF-периметр держался бы на прокси, а не на нас.
        using var handler = ReaderHttpHandlerFactory.Create();

        handler.UseProxy.Should().BeFalse();
    }

    [Fact]
    public async Task ПрямоеСоединение_ИгнорируетПеременныеОкруженияПрокси()
    {
        // Регрессия на SSRF-обход: HTTP_PROXY/HTTPS_PROXY заданы (как в docker-compose.claude.yml
        // при включённом CLAUDE_EGRESS_PROXY), но клиент "link-reader" всё равно обязан соединяться
        // напрямую с целью и резать приватный адрес сам — а не отдавать проверку прокси, который,
        // как проверено вручную, приватные адреса не фильтрует.
        var previousHttpProxy = Environment.GetEnvironmentVariable("HTTP_PROXY");
        var previousHttpsProxy = Environment.GetEnvironmentVariable("HTTPS_PROXY");
        Environment.SetEnvironmentVariable("HTTP_PROXY", "http://198.51.100.1:2080");
        Environment.SetEnvironmentVariable("HTTPS_PROXY", "http://198.51.100.1:2080");
        try
        {
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var acceptTask = listener.AcceptTcpClientAsync();

            using var handler = ReaderHttpHandlerFactory.Create();
            using var client = new HttpClient(handler);

            var act = async () => await client.GetAsync($"http://127.0.0.1:{port}/", CancellationToken.None);

            // Если бы прокси из окружения подхватывался, соединение уходило бы на недостижимый
            // 198.51.100.1 и падало бы таймаутом/сетевой ошибкой прокси, а не мгновенным SSRF-отказом.
            await act.Should().ThrowAsync<HttpRequestException>();
            acceptTask.IsCompleted.Should().BeFalse("ConnectCallback обязан остановить подключение до сокета");
        }
        finally
        {
            Environment.SetEnvironmentVariable("HTTP_PROXY", previousHttpProxy);
            Environment.SetEnvironmentVariable("HTTPS_PROXY", previousHttpsProxy);
        }
    }
}
