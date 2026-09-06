using System.Diagnostics;
using System.Net;
using System.Text;
using ClaudeHomeServer.Services.Reader;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClaudeHomeServer.Tests.Services.Reader;

// Проба встраиваемости (docs/adr/ADR-006-reader-embed-check.md): тесты по чек-листу
// «Что проверить при реализации». Сеть — через StubHandler (без реальных сокетов),
// SSRF-рубежи реальны: 127.0.0.1 и *.invalid детерминированно приватный/нерезолвящийся
// адрес на любой машине и в CI (та же схема, что в ReaderServiceTests).
/// <remarks>
/// Категория <c>Dns</c>: тестам нужен НАСТОЯЩИЙ резолв внешних имён (example.com и т.п.).
/// На машине с системным прокси (Proxifier) DNS отдаёт вместо них локальный адрес, SsrfGuard
/// честно рубит его как loopback, и тесты валятся пачкой — это среда, а не регрессия; в CI они
/// зелёные. Локально исключаются фильтром: dotnet test --filter "Category!=Dns".
/// </remarks>
[Trait("Category", "Dns")]
public class ReaderEmbedCheckTests
{
    private const string PublicHost = "example.com";
    private const string PublicUrl = "http://example.com/article";

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<Uri> RequestedUris { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            RequestedUris.Add(request.RequestUri!);
            return Task.FromResult(respond(request));
        }
    }

    private sealed class HangingHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            await Task.Delay(Timeout.Infinite, ct);
            throw new InvalidOperationException("хоп обязан оборваться таймаутом раньше");
        }
    }

    private sealed class StubHttpFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class RecordingHttpFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public List<string> RequestedClients { get; } = [];
        public HttpClient CreateClient(string name)
        {
            RequestedClients.Add(name);
            return new HttpClient(handler, disposeHandler: false);
        }
    }

    private sealed class CollectingLogger : ILogger<ReaderService>
    {
        public List<string> Entries { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter) => Entries.Add(formatter(state, exception));
    }

    private static ReaderService CreateService(
        HttpMessageHandler handler,
        Dictionary<string, string?>? settings = null,
        ILogger<ReaderService>? logger = null,
        IHttpClientFactory? factory = null)
    {
        var config = TestConfig.Build(settings ?? []);
        return new ReaderService(factory ?? new StubHttpFactory(handler), config, logger ?? NullLogger<ReaderService>.Instance);
    }

    private static HttpResponseMessage Html(string body = "<html><body>страница</body></html>")
        => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "text/html") };

    private static HttpResponseMessage WithHeaders(HttpResponseMessage resp, params (string Name, string Value)[] headers)
    {
        foreach (var (name, value) in headers)
            resp.Headers.TryAddWithoutValidation(name, value);
        return resp;
    }

    private static HttpResponseMessage Redirect(string location, HttpStatusCode code = HttpStatusCode.Found)
    {
        var resp = new HttpResponseMessage(code);
        resp.Headers.Location = new Uri(location, UriKind.RelativeOrAbsolute);
        return resp;
    }

    private static HttpResponseMessage Content(string mediaType) => new(HttpStatusCode.OK)
    {
        Content = new ByteArrayContent([1, 2, 3]) { Headers = { ContentType = new(mediaType) } },
    };

    // ---------- Общий клиент и конвейер с /read ----------

    [Fact]
    public async Task ПробаИЧтение_ХодятОднимИменованнымКлиентом()
    {
        // Чек-лист ADR-006: «хендлер общий с /read, а не второй экземпляр с дефолтами» —
        // оба метода берут клиент с одним и тем же именем link-reader, а значит один и тот же
        // хендлер (без кук, без прокси, ConnectCallback с SSRF-фильтром) из Program.cs.
        var factory = new RecordingHttpFactory(new StubHandler(_ => Html()));
        var service = CreateService(new StubHandler(_ => Html()), factory: factory);

        await service.CheckEmbedAsync(PublicUrl, CancellationToken.None);
        await service.ReadAsync(PublicUrl, CancellationToken.None);

        factory.RequestedClients.Should().HaveCount(2);
        factory.RequestedClients.Should().OnlyContain(name => name == ReaderService.HttpClientName);
    }

    [Fact]
    public async Task Redirect_ВердиктБерётсяИзФинальногоОтветаЦепочки()
    {
        var handler = new StubHandler(req =>
            req.RequestUri!.AbsolutePath == "/article"
                ? Redirect("/final")
                : WithHeaders(Html(), ("X-Frame-Options", "SAMEORIGIN")));

        var result = await CreateService(handler).CheckEmbedAsync(PublicUrl, CancellationToken.None);

        result.Embeddable.Should().BeFalse();
        result.Reason.Should().Be("frame-denied");
        handler.RequestedUris.Should().HaveCount(2);
    }

    [Fact]
    public async Task Redirect_ОтносительныйLocation_РазрешаетсяОтТекущегоХопа()
    {
        var handler = new StubHandler(req =>
            req.RequestUri!.AbsolutePath == "/article" ? Redirect("/moved") : Html());

        var result = await CreateService(handler).CheckEmbedAsync(PublicUrl, CancellationToken.None);

        result.Embeddable.Should().BeTrue();
        handler.RequestedUris[1].AbsoluteUri.Should().Be("http://example.com/moved");
    }

    // ---------- Перепроверка рубежей на каждом хопе редиректа ----------

    [Fact]
    public async Task Redirect_НаПриватныйАдрес_LocalAddress()
    {
        var handler = new StubHandler(req =>
            req.RequestUri!.Host == PublicHost
                ? Redirect("http://127.0.0.1/internal")
                : throw new InvalidOperationException("не должно дойти"));

        var result = await CreateService(handler).CheckEmbedAsync(PublicUrl, CancellationToken.None);

        result.Embeddable.Should().BeFalse();
        result.Reason.Should().Be("local-address");
    }

    [Fact]
    public async Task Redirect_НаНестандартныйПорт_InvalidUrl()
    {
        var handler = new StubHandler(req =>
            req.RequestUri!.Host == PublicHost
                ? Redirect("http://example.com:6379/")
                : throw new InvalidOperationException("не должно дойти"));

        var result = await CreateService(handler).CheckEmbedAsync(PublicUrl, CancellationToken.None);

        result.Reason.Should().Be("invalid-url");
    }

    [Fact]
    public async Task Redirect_СменаСхемыНаFile_InvalidUrl()
    {
        var handler = new StubHandler(_ => Redirect("file:///C:/secrets.txt"));
        var result = await CreateService(handler).CheckEmbedAsync(PublicUrl, CancellationToken.None);
        result.Reason.Should().Be("invalid-url");
    }

    [Fact]
    public async Task Redirect_БольшеПяти_TooManyRedirects()
    {
        var hop = 0;
        var handler = new StubHandler(_ => Redirect($"http://example.com/hop{hop++}"));
        var result = await CreateService(handler).CheckEmbedAsync(PublicUrl, CancellationToken.None);
        result.Reason.Should().Be("too-many-redirects");
        handler.RequestedUris.Should().HaveCount(6, "исходный запрос + пять разрешённых хопов");
    }

    // ---------- Таблица вердиктов: X-Frame-Options ----------

    [Theory]
    [InlineData("DENY")]
    [InlineData("deny")]
    [InlineData("SAMEORIGIN")]
    [InlineData("ALLOW-FROM=https://example.com")]
    [InlineData("ALLOW-FROM https://example.com")]
    public async Task Xfo_ЛюбоеВалидноеЗначение_FrameDenied(string xfo)
    {
        var handler = new StubHandler(_ => WithHeaders(Html(), ("X-Frame-Options", xfo)));
        var result = await CreateService(handler).CheckEmbedAsync(PublicUrl, CancellationToken.None);

        result.Embeddable.Should().BeFalse();
        result.Reason.Should().Be("frame-denied");
    }

    [Theory]
    [InlineData("ALLOWALL")]
    [InlineData("banana")]
    [InlineData("")]
    public async Task Xfo_НевалидноеИлиМусорноеЗначение_Игнорируется(string xfo)
    {
        var handler = new StubHandler(_ => WithHeaders(Html(), ("X-Frame-Options", xfo)));
        var result = await CreateService(handler).CheckEmbedAsync(PublicUrl, CancellationToken.None);
        result.Embeddable.Should().BeTrue("невалидный заголовок игнорируется — как делают браузеры");
    }

    // ---------- Таблица вердиктов: CSP frame-ancestors ----------

    [Theory]
    [InlineData("frame-ancestors 'none'")]
    [InlineData("frame-ancestors 'self'")]
    [InlineData("frame-ancestors https://trusted.example")]
    [InlineData("frame-ancestors 'self' https://trusted.example")]
    [InlineData("default-src 'self'; frame-ancestors 'none'")]
    public async Task Csp_FrameAncestorsNoneИлиЯвныйСписок_FrameDenied(string csp)
    {
        var handler = new StubHandler(_ => WithHeaders(Html(), ("Content-Security-Policy", csp)));
        var result = await CreateService(handler).CheckEmbedAsync(PublicUrl, CancellationToken.None);

        result.Embeddable.Should().BeFalse();
        result.Reason.Should().Be("frame-denied");
    }

    [Theory]
    [InlineData("frame-ancestors *")]
    [InlineData("frame-ancestors http:")]
    [InlineData("frame-ancestors https:")]
    [InlineData("frame-ancestors * https://foo.example")]
    [InlineData("default-src 'self'")]
    public async Task Csp_ВайлдкардыИлиОтсутствиеFrameAncestors_НеЗапрет(string csp)
    {
        var handler = new StubHandler(_ => WithHeaders(Html(), ("Content-Security-Policy", csp)));
        var result = await CreateService(handler).CheckEmbedAsync(PublicUrl, CancellationToken.None);
        result.Embeddable.Should().BeTrue();
    }

    [Fact]
    public async Task Csp_НесколькоПолитикЧерезЗапятую_ЗапретВЛюбойИзНих()
    {
        var handler = new StubHandler(_ => WithHeaders(Html(),
            ("Content-Security-Policy", "default-src 'self', frame-ancestors 'none'")));
        var result = await CreateService(handler).CheckEmbedAsync(PublicUrl, CancellationToken.None);
        result.Reason.Should().Be("frame-denied");
    }

    [Fact]
    public async Task CspReportOnly_Игнорируется_ОнНеБлокирует()
    {
        var handler = new StubHandler(_ => WithHeaders(Html(),
            ("Content-Security-Policy-Report-Only", "frame-ancestors 'none'")));
        var result = await CreateService(handler).CheckEmbedAsync(PublicUrl, CancellationToken.None);
        result.Embeddable.Should().BeTrue();
    }

    // ---------- Таблица вердиктов: Content-Type ----------

    [Theory]
    [InlineData("application/pdf")]
    [InlineData("text/plain")]
    [InlineData("application/json")]
    [InlineData("application/zip")]
    public async Task ContentType_НеHtml_NotHtml(string mediaType)
    {
        var handler = new StubHandler(_ => Content(mediaType));
        var result = await CreateService(handler).CheckEmbedAsync(PublicUrl, CancellationToken.None);

        result.Embeddable.Should().BeFalse();
        result.Reason.Should().Be("not-html");
    }

    [Fact]
    public async Task ContentType_Xhtml_Встраиваем()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new StringContent("<html/>", Encoding.UTF8, "application/xhtml+xml") });
        var result = await CreateService(handler).CheckEmbedAsync(PublicUrl, CancellationToken.None);
        result.Embeddable.Should().BeTrue();
    }

    [Fact]
    public async Task ContentType_ПроверяетсяРаньшеЗапретаВстраивания()
    {
        // Порядок таблицы ADR-006 §1: не-HTML отдаёт свой reason, даже если заголовки запрещают.
        var handler = new StubHandler(_ => WithHeaders(Content("application/pdf"), ("X-Frame-Options", "DENY")));
        var result = await CreateService(handler).CheckEmbedAsync(PublicUrl, CancellationToken.None);
        result.Reason.Should().Be("not-html");
    }

    // ---------- Таблица вердиктов: статус финального ответа ----------

    [Theory]
    [InlineData(HttpStatusCode.NotFound, "not-found")]
    [InlineData(HttpStatusCode.Gone, "not-found")]
    [InlineData(HttpStatusCode.Unauthorized, "auth-required")]
    [InlineData(HttpStatusCode.Forbidden, "auth-required")]
    [InlineData(HttpStatusCode.TooManyRequests, "blocked-by-site")]
    [InlineData(HttpStatusCode.InternalServerError, "server-error")]
    [InlineData(HttpStatusCode.ServiceUnavailable, "server-error")]
    [InlineData(HttpStatusCode.BadRequest, "server-error")]
    public async Task СтатусНе2xx_ReasonПоТаблицеКодовAdr005(HttpStatusCode status, string reason)
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(status));
        var result = await CreateService(handler).CheckEmbedAsync(PublicUrl, CancellationToken.None);

        result.Embeddable.Should().BeFalse();
        result.Reason.Should().Be(reason);
    }

    [Fact]
    public async Task Статус403_СЗаголовкомCfRay_BlockedBySite()
    {
        // Тело проба не читает — маркеры щита определяются только по заголовкам.
        var handler = new StubHandler(_ => WithHeaders(new HttpResponseMessage(HttpStatusCode.Forbidden), ("cf-ray", "abc123")));
        var result = await CreateService(handler).CheckEmbedAsync(PublicUrl, CancellationToken.None);
        result.Reason.Should().Be("blocked-by-site");
    }

    // ---------- Сбои самой пробы ----------

    [Theory]
    [InlineData("ftp://example.com/")]
    [InlineData("http://example.com:6379/")]
    [InlineData("http://user:pass@example.com/")]
    [InlineData("not a url")]
    public async Task Проба_НевалидныйUrl_InvalidUrlБезСети(string url)
    {
        var handler = new StubHandler(_ => throw new InvalidOperationException("сеть не должна дёргаться"));
        var result = await CreateService(handler).CheckEmbedAsync(url, CancellationToken.None);

        result.Embeddable.Should().BeFalse();
        result.Reason.Should().Be("invalid-url");
        handler.RequestedUris.Should().BeEmpty();
    }

    [Fact]
    public async Task Проба_ПрямойЛокальныйАдрес_LocalAddress()
    {
        var handler = new StubHandler(_ => throw new InvalidOperationException("сеть не должна дёргаться"));
        var result = await CreateService(handler).CheckEmbedAsync("http://127.0.0.1/", CancellationToken.None);
        result.Reason.Should().Be("local-address");
        handler.RequestedUris.Should().BeEmpty();
    }

    [Fact]
    public async Task Проба_НерезолвящийсяХост_DnsFailed()
    {
        var handler = new StubHandler(_ => throw new InvalidOperationException("сеть не должна дёргаться"));
        var result = await CreateService(handler).CheckEmbedAsync("http://nonexistent.invalid/", CancellationToken.None);
        result.Reason.Should().Be("dns-failed");
    }

    [Fact]
    public async Task Проба_СерверНеОтдаётЗаголовкиЗаТаймаут_Timeout()
    {
        // Таймаут пробы — 5 с на заголовки (ADR-006 §1); в тесте сжимаем до 1 с конфигом.
        var sw = Stopwatch.StartNew();
        var result = await CreateService(
            new HangingHandler(),
            new Dictionary<string, string?> { ["Reader:HeaderTimeoutSeconds"] = "1" })
            .CheckEmbedAsync(PublicUrl, CancellationToken.None);

        result.Embeddable.Should().BeFalse();
        result.Reason.Should().Be("timeout");
        sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(4));
    }

    // ---------- Тело ответа не читается ----------

    private sealed class ExplodingStream : Stream
    {
        private static InvalidOperationException Explode() =>
            new("проба встраиваемости не должна читать тело ответа");

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => 0; set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw Explode();
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) => throw Explode();
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) => throw Explode();
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    [Fact]
    public async Task Проба_ТелоНеЧитается_ВердиктТолькоПоЗаголовкам()
    {
        // 2-гигабайтная страница не влияет на пробу: чтение тела бросило бы исключение,
        // а вердикт обязан сложиться из одних заголовков (ResponseHeadersRead + закрытие после).
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(new ExplodingStream())
            {
                Headers = { ContentType = new("text/html") },
            },
        };
        var handler = new StubHandler(_ => response);

        var result = await CreateService(handler).CheckEmbedAsync(PublicUrl, CancellationToken.None);

        result.Embeddable.Should().BeTrue();
    }

    // ---------- Логи ----------

    [Fact]
    public async Task Лог_СодержитТолькоДоменИсходИДлительность_БезПолногоUrl()
    {
        var logger = new CollectingLogger();
        var service = CreateService(new StubHandler(_ => Html()), logger: logger);

        await service.CheckEmbedAsync("http://example.com/secret/path?token=abc123", CancellationToken.None);

        logger.Entries.Should().NotBeEmpty();
        logger.Entries.Should().Contain(e => e.Contains("example.com"));
        logger.Entries.Should().NotContain(e =>
            e.Contains("/secret") || e.Contains("token=abc123") || e.Contains("example.com/"));
    }
}
