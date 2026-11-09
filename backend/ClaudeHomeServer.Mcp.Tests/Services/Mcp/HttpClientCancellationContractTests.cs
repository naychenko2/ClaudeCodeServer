using System.Net;

namespace ClaudeHomeServer.Tests.Services.Mcp;

/// <summary>
/// Контракт отмены транспорта net10.0, на который опираются guard'ы «отмена хоста /
/// сервер не ответил» в HiggsfieldToolset, McpOAuthService, McpCatalogClient и узкие
/// catch'i в движках медиа. Замерено этой пробой, а не пересказом документации:
/// — превышение MaxResponseContentBufferSize даёт HttpRequestException (не InvalidOperationException)
///   и касается только буферующего пути клиента (GetAsync/SendAsync с полным чтением);
/// — чтение тела с уже отменённым токеном даёт TaskCanceledException (тот же тип, что таймаут);
/// — client.Timeout при ResponseHeadersRead тело НЕ покрывает: стоп-тело переживает любой
///   потолок клиента и обрезается только токеном.
/// </summary>
public class HttpClientCancellationContractTests
{
    // Тело 128 КБ при буфере 64 КБ — ровно конфигурация тестовых фабриок каталога
    private sealed class BigBodyHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(new string('a', 128 * 1024)),
            });
    }

    // Тело без конца: по байту каждые 10 мс, никогда не заканчивается.
    private sealed class EndlessStreamHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(new EndlessStream()),
            });
    }

    private sealed class EndlessStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => 0; set { } }

        public override int Read(byte[] buffer, int offset, int count)
        {
            Thread.Sleep(10);
            buffer[offset] = (byte)'x';
            return 1;
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private static HttpClient Client(HttpMessageHandler handler) =>
        new(handler) { Timeout = TimeSpan.FromSeconds(10), MaxResponseContentBufferSize = 64 * 1024 };

    [Fact]
    public async Task Превышение_MaxResponseContentBufferSize_даёт_HttpRequestException()
    {
        using var http = Client(new BigBodyHandler());

        // Путь каталога: GetAsync (полное буферирование) — исключение на месте GetAsync.
        var actGet = new Func<Task>(async () =>
        {
            using var r = await http.GetAsync("https://registry.example/v0.1/servers");
            _ = await r.Content.ReadAsStringAsync();
        });
        var thrownGet = await actGet.Should().ThrowAsync<HttpRequestException>();
        thrownGet.Which.Message.Should().Contain("buffer",
            "предел буфера назван в тексте — не «таймаут» и не «обрыв связи»");

        // Предел касается только буферирования внутри GetAsync/SendAsync: при
        // ResponseHeadersRead чтение контента идёт своим ходом, и предел там не участвует.
        // (Ровно поэтому в каталоге «слишком большое тело» падает на GetAsync, а не на
        // ReadAsStringAsync.)
        using var resp = await http.SendAsync(new HttpRequestMessage(HttpMethod.Get,
            "https://registry.example/v0.1/servers"), HttpCompletionOption.ResponseHeadersRead);
        var text = await resp.Content.ReadAsStringAsync();
        text.Length.Should().Be(128 * 1024,
            "чтение вне буферующего пути клиента предела не знает — это отдельный факт, "
            + "на который опирается комментарий у ReadAsStringAsync в McpCatalogClient");
    }

    [Fact]
    public async Task Чтение_тела_с_уже_отменённым_токеном_даёт_TaskCanceledException()
    {
        using var http = Client(new EndlessStreamHandler());
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        using var r = await http.SendAsync(
            new HttpRequestMessage(HttpMethod.Get, "https://example/v0.1/servers"),
            HttpCompletionOption.ResponseHeadersRead);

        // Тот же тип, что таймаут транспорта: различить их может только признак
        // «хост отменил» (ct.IsCancellationRequested), а не тип исключения.
        var act = async () => await r.Content.ReadAsStringAsync(cts.Token);
        await act.Should().ThrowAsync<TaskCanceledException>();
    }

    [Fact]
    public async Task Стоп_тело_переживает_таймаут_клиента_и_обрезается_токеном()
    {
        // 1) client.Timeout при ResponseHeadersRead тело не покрывает: 300 мс против
        // тела без конца — за 1,5 с чтение не оборвалось бы ни чем.
        using (var http = new HttpClient(new EndlessStreamHandler())
        {
            Timeout = TimeSpan.FromMilliseconds(300),
        })
        {
            using var r = await http.SendAsync(
                new HttpRequestMessage(HttpMethod.Get, "https://example/mcp"),
                HttpCompletionOption.ResponseHeadersRead);
            using var stream = await r.Content.ReadAsStreamAsync();
            var readTask = new System.IO.StreamReader(stream).ReadToEndAsync(CancellationToken.None);
            var finished = await Task.WhenAny(readTask, Task.Delay(1500));
            (finished != readTask).Should().BeTrue(
                "тело при ResponseHeadersRead живёт вне client.Timeout — нужен отдельный потолок-токен");
        }

        // 2) Тот же стоп-ответ, но чтение под токеном с потолком: оборывается токеном.
        using (var http = new HttpClient(new EndlessStreamHandler())
        {
            Timeout = TimeSpan.FromSeconds(10),
        })
        {
            using var r = await http.SendAsync(
                new HttpRequestMessage(HttpMethod.Get, "https://example/mcp"),
                HttpCompletionOption.ResponseHeadersRead);
            using var stream = await r.Content.ReadAsStreamAsync();
            using var deadline = new CancellationTokenSource(200);
            var act = async () => await new System.IO.StreamReader(stream).ReadToEndAsync(deadline.Token);
            await act.Should().ThrowAsync<TaskCanceledException>();
        }
    }
}
