using System.Net;
using System.Net.Sockets;
using ClaudeHomeServer.Services.Reader;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Services.Reader;

// Проверяет РЕАЛЬНУЮ проводку клиента "link-reader" (ConnectCallback), не стаб — единственный
// офлайн-проверяемый кусок чек-листа ADR-005 ("ConnectCallback подключён всегда"): режим прямого
// соединения (без прокси в этом процессе) обязан резолвить сам и резать приватные адреса даже
// если запрос формально шёл на localhost-порт, поднятый самим тестом. Живой стенд с настоящим
// egress-прокси (CLAUDE_EGRESS_PROXY) недоступен из песочницы разработки — это отдельная,
// незакрытая проверка, см. отчёт по задаче.
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
}
