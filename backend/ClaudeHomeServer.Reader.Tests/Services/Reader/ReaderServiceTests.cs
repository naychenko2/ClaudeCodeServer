using System.Net;
using System.Text;
using ClaudeHomeServer.Services.Reader;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClaudeHomeServer.Tests.Services.Reader;

// ReaderService — оркестрация чтения (ADR-005): цепочка редиректов с перепроверкой на каждом
// хопе, маршрутизация по Content-Type, коды ошибок. Сеть — через StubHandler (без реальных
// сокетов), приватность/DNS-рубежи (SsrfGuard) реальны и детерминированы: 127.0.0.1 и
// *.invalid — гарантированно приватный/нерезолвящийся адрес на любой машине и в CI.
/// <remarks>
/// Категория <c>Dns</c>: тестам нужен НАСТОЯЩИЙ резолв внешних имён (example.com и т.п.).
/// На машине с системным прокси (Proxifier) DNS отдаёт вместо них локальный адрес, SsrfGuard
/// честно рубит его как loopback, и тесты валятся пачкой — это среда, а не регрессия; в CI они
/// зелёные. Локально исключаются фильтром: dotnet test --filter "Category!=Dns".
/// </remarks>
[Trait("Category", "Dns")]
public class ReaderServiceTests
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

    private sealed class StubHttpFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private static ReaderService CreateService(StubHandler handler, Dictionary<string, string?>? settings = null)
    {
        var config = TestConfig.Build(settings ?? []);
        return new ReaderService(new StubHttpFactory(handler), config, NullLogger<ReaderService>.Instance);
    }

    private static HttpResponseMessage Html(string body, HttpStatusCode code = HttpStatusCode.OK)
        => new(code) { Content = new StringContent(body, Encoding.UTF8, "text/html") };

    private static HttpResponseMessage Redirect(string location, HttpStatusCode code = HttpStatusCode.Found)
    {
        var resp = new HttpResponseMessage(code);
        resp.Headers.Location = new Uri(location, UriKind.RelativeOrAbsolute);
        return resp;
    }

    private const string ReadableArticleHtml = """
        <html><head><title>t</title></head><body>
        <article>
        <h1>Заголовок статьи</h1>
        <p>Первый абзац с достаточным количеством текста, чтобы Readability сочла его настоящей статьёй, а не пустой оболочкой — здесь нужно набрать побольше слов для веса кандидата.</p>
        <p>Второй абзац тоже длинный, добавляет содержательности и веса кандидату при оценке текстовой плотности блока — чем больше связного текста, тем увереннее алгоритм сочтёт документ читаемым.</p>
        <p>Третий абзац для верности, чтобы суммарная длина текста точно перевалила порог читаемости алгоритма — порог по умолчанию довольно высокий, поэтому текста должно быть действительно много.</p>
        <p>Четвёртый абзац на всякий случай — запас по длине не помешает, а короткие тестовые фикстуры как раз то, что чаще всего проваливает эвристику Readability по умолчанию.</p>
        </article>
        </body></html>
        """;

    // ---------- Валидация URL ----------

    [Theory]
    [InlineData("ftp://example.com/")]
    [InlineData("gopher://example.com/")]
    [InlineData("not a url")]
    public async Task InvalidUrl_НедопустимаяСхема(string url)
    {
        var handler = new StubHandler(_ => throw new InvalidOperationException("сеть не должна дёргаться"));
        var outcome = await CreateService(handler).ReadAsync(url, CancellationToken.None);
        outcome.Success.Should().BeFalse();
        outcome.Error.Should().Be(ReaderErrorCode.InvalidUrl);
        handler.RequestedUris.Should().BeEmpty();
    }

    [Fact]
    public async Task InvalidUrl_НестандартныйПорт()
    {
        var handler = new StubHandler(_ => throw new InvalidOperationException("сеть не должна дёргаться"));
        var outcome = await CreateService(handler).ReadAsync("http://example.com:6379/", CancellationToken.None);
        outcome.Error.Should().Be(ReaderErrorCode.InvalidUrl);
    }

    [Fact]
    public async Task InvalidUrl_UserInfoВСтроке()
    {
        var handler = new StubHandler(_ => throw new InvalidOperationException("сеть не должна дёргаться"));
        var outcome = await CreateService(handler).ReadAsync("http://user:pass@example.com/", CancellationToken.None);
        outcome.Error.Should().Be(ReaderErrorCode.InvalidUrl);
    }

    // ---------- Рубежи адресов ----------

    [Fact]
    public async Task LocalAddress_ПрямойЛитерал_БезСетевогоВызова()
    {
        var handler = new StubHandler(_ => throw new InvalidOperationException("сеть не должна дёргаться"));
        var outcome = await CreateService(handler).ReadAsync("http://127.0.0.1/", CancellationToken.None);
        outcome.Error.Should().Be(ReaderErrorCode.LocalAddress);
        handler.RequestedUris.Should().BeEmpty();
    }

    [Fact]
    public async Task DnsFailed_НерезолвящийсяХост()
    {
        var handler = new StubHandler(_ => throw new InvalidOperationException("сеть не должна дёргаться"));
        var outcome = await CreateService(handler).ReadAsync("http://nonexistent.invalid/", CancellationToken.None);
        outcome.Error.Should().Be(ReaderErrorCode.DnsFailed);
    }

    // ---------- Редиректы ----------

    [Fact]
    public async Task Redirect_ОтносительныйLocation_РазрешаетсяОтТекущегоХопа()
    {
        var handler = new StubHandler(req =>
            req.RequestUri!.AbsolutePath == "/article"
                ? Redirect("/moved")
                : Html(ReadableArticleHtml));

        var outcome = await CreateService(handler).ReadAsync(PublicUrl, CancellationToken.None);

        outcome.Success.Should().BeTrue();
        handler.RequestedUris.Should().HaveCount(2);
        handler.RequestedUris[1].AbsoluteUri.Should().Be("http://example.com/moved");
    }

    [Fact]
    public async Task Redirect_НаПриватныйАдрес_LocalAddress()
    {
        var handler = new StubHandler(req =>
            req.RequestUri!.Host == PublicHost
                ? Redirect("http://127.0.0.1/internal")
                : throw new InvalidOperationException("не должно дойти"));

        var outcome = await CreateService(handler).ReadAsync(PublicUrl, CancellationToken.None);
        outcome.Error.Should().Be(ReaderErrorCode.LocalAddress);
    }

    [Fact]
    public async Task Redirect_СменаСхемыНаFile_InvalidUrl()
    {
        var handler = new StubHandler(_ => Redirect("file:///C:/secrets.txt"));
        var outcome = await CreateService(handler).ReadAsync(PublicUrl, CancellationToken.None);
        outcome.Error.Should().Be(ReaderErrorCode.InvalidUrl);
    }

    [Fact]
    public async Task Redirect_БольшеПяти_TooManyRedirects()
    {
        var hop = 0;
        var handler = new StubHandler(_ => Redirect($"http://example.com/hop{hop++}"));
        var outcome = await CreateService(handler).ReadAsync(PublicUrl, CancellationToken.None);
        outcome.Error.Should().Be(ReaderErrorCode.TooManyRedirects);
    }

    // ---------- Content-Type ----------

    [Fact]
    public async Task ContentType_Pdf()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new ByteArrayContent([1, 2, 3]) { Headers = { ContentType = new("application/pdf") } } });
        var outcome = await CreateService(handler).ReadAsync(PublicUrl, CancellationToken.None);
        outcome.Error.Should().Be(ReaderErrorCode.Pdf);
    }

    [Fact]
    public async Task ContentType_Архив_NotAPage()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new ByteArrayContent([1, 2, 3]) { Headers = { ContentType = new("application/zip") } } });
        var outcome = await CreateService(handler).ReadAsync(PublicUrl, CancellationToken.None);
        outcome.Error.Should().Be(ReaderErrorCode.NotAPage);
    }

    [Fact]
    public async Task ContentType_Markdown_ОтдаётсяКакЕсть()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new StringContent("# Заголовок\n\nтекст", Encoding.UTF8, "text/markdown") });
        var outcome = await CreateService(handler).ReadAsync(PublicUrl, CancellationToken.None);
        outcome.Success.Should().BeTrue();
        outcome.Markdown.Should().Contain("# Заголовок");
    }

    [Fact]
    public async Task ContentType_PlainТекстСТройнымиБэктиками_ЭкранируетсяФенсомДлиннее()
    {
        var body = "some text\n```\nnot a real fence\n```\nmore";
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new StringContent(body, Encoding.UTF8, "text/plain") });
        var outcome = await CreateService(handler).ReadAsync(PublicUrl, CancellationToken.None);
        outcome.Success.Should().BeTrue();
        outcome.Markdown.Should().StartWith("````");
        outcome.Markdown.Should().Contain(body);
    }

    // ---------- Статусы ----------

    [Theory]
    [InlineData(HttpStatusCode.NotFound, ReaderErrorCode.NotFound)]
    [InlineData(HttpStatusCode.Gone, ReaderErrorCode.NotFound)]
    [InlineData(HttpStatusCode.TooManyRequests, ReaderErrorCode.BlockedBySite)]
    [InlineData(HttpStatusCode.InternalServerError, ReaderErrorCode.ServerError)]
    public async Task Статус_МапитсяНаКод(HttpStatusCode status, ReaderErrorCode expected)
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(status));
        var outcome = await CreateService(handler).ReadAsync(PublicUrl, CancellationToken.None);
        outcome.Error.Should().Be(expected);
        outcome.HttpStatus.Should().Be((int)status);
    }

    [Fact]
    public async Task Статус401_БезМаркеровЩита_AuthRequired()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized)
        { Content = new StringContent("no access") });
        var outcome = await CreateService(handler).ReadAsync(PublicUrl, CancellationToken.None);
        outcome.Error.Should().Be(ReaderErrorCode.AuthRequired);
    }

    [Fact]
    public async Task Статус403_СЗаголовкомCfRay_BlockedBySite()
    {
        var handler = new StubHandler(_ =>
        {
            var resp = new HttpResponseMessage(HttpStatusCode.Forbidden) { Content = new StringContent("blocked") };
            resp.Headers.Add("cf-ray", "abc123");
            return resp;
        });
        var outcome = await CreateService(handler).ReadAsync(PublicUrl, CancellationToken.None);
        outcome.Error.Should().Be(ReaderErrorCode.BlockedBySite);
    }

    [Fact]
    public async Task Статус503_СТитуломJustAMoment_BlockedBySite()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
        { Content = new StringContent("<html><head><title>Just a moment...</title></head></html>") });
        var outcome = await CreateService(handler).ReadAsync(PublicUrl, CancellationToken.None);
        outcome.Error.Should().Be(ReaderErrorCode.BlockedBySite);
    }

    [Fact]
    public async Task Статус503_БезМаркеров_ServerError()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
        { Content = new StringContent("maintenance") });
        var outcome = await CreateService(handler).ReadAsync(PublicUrl, CancellationToken.None);
        outcome.Error.Should().Be(ReaderErrorCode.ServerError);
    }

    // ---------- Потолок тела ----------

    [Fact]
    public async Task TooLarge_ТелоБольшеПотолка()
    {
        var handler = new StubHandler(_ => Html(new string('x', 200)));
        var outcome = await CreateService(handler, new Dictionary<string, string?> { ["Reader:MaxBodyBytes"] = "100" })
            .ReadAsync(PublicUrl, CancellationToken.None);
        outcome.Error.Should().Be(ReaderErrorCode.TooLarge);
    }

    // ---------- HTML / SmartReader ----------

    [Fact]
    public async Task Html_НечитаемаяСтраница_БезМаркеровВхода_NotReadable()
    {
        var handler = new StubHandler(_ => Html("<html><body><p>short</p></body></html>"));
        var outcome = await CreateService(handler).ReadAsync(PublicUrl, CancellationToken.None);
        outcome.Error.Should().Be(ReaderErrorCode.NotReadable);
    }

    [Fact]
    public async Task Html_НечитаемаяСтраница_СПолемПароля_AuthRequired()
    {
        var handler = new StubHandler(_ => Html(
            "<html><body><form action=\"/login\"><input type=\"password\" name=\"p\"></form></body></html>"));
        var outcome = await CreateService(handler).ReadAsync(PublicUrl, CancellationToken.None);
        outcome.Error.Should().Be(ReaderErrorCode.AuthRequired);
    }

    [Fact]
    public async Task Html_ЧитаемаяСтатья_ВозвращаетMarkdown()
    {
        var handler = new StubHandler(_ => Html(ReadableArticleHtml));
        var outcome = await CreateService(handler).ReadAsync(PublicUrl, CancellationToken.None);
        outcome.Success.Should().BeTrue();
        outcome.Markdown.Should().Contain("Заголовок статьи");
    }

    // ---------- Прокси картинок (/api/reader/image) ----------

    [Fact]
    public async Task Image_Успех_ВозвращаетБайтыИContentType()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new ByteArrayContent([1, 2, 3]) { Headers = { ContentType = new("image/png") } } });

        var result = await CreateService(handler).ReadImageAsync("http://example.com/pic.png", CancellationToken.None);

        result.Should().NotBeNull();
        result!.Value.ContentType.Should().Be("image/png");
        result.Value.Bytes.Should().Equal([1, 2, 3]);
    }

    [Fact]
    public async Task Image_ЛокальныйАдрес_Null()
    {
        var handler = new StubHandler(_ => throw new InvalidOperationException("не должно дойти"));
        var result = await CreateService(handler).ReadImageAsync("http://127.0.0.1/pic.png", CancellationToken.None);
        result.Should().BeNull();
    }

    [Fact]
    public async Task Image_НеImageContentType_Null()
    {
        var handler = new StubHandler(_ => Html("<html></html>"));
        var result = await CreateService(handler).ReadImageAsync("http://example.com/pic.png", CancellationToken.None);
        result.Should().BeNull();
    }

    [Fact]
    public async Task Image_РедиректНаПриватныйАдрес_Null()
    {
        var handler = new StubHandler(req =>
            req.RequestUri!.Host == PublicHost ? Redirect("http://169.254.169.254/pic.png") : throw new InvalidOperationException());
        var result = await CreateService(handler).ReadImageAsync("http://example.com/pic.png", CancellationToken.None);
        result.Should().BeNull();
    }

    [Fact]
    public async Task Image_БольшеПотолка_Null()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new ByteArrayContent(new byte[200]) { Headers = { ContentType = new("image/png") } } });
        var result = await CreateService(handler, new Dictionary<string, string?> { ["Reader:MaxImageBytes"] = "100" })
            .ReadImageAsync("http://example.com/pic.png", CancellationToken.None);
        result.Should().BeNull();
    }

    [Fact]
    public async Task Html_ОтносительныеСсылки_СтановятсяАбсолютными()
    {
        var articleWithRelativeLink = """
            <html><head><title>t</title></head><body><article>
            <h1>Заголовок статьи</h1>
            <p>Первый абзац с достаточным количеством текста для читаемости, добавляем слов побольше сюда — чем длиннее связный текст, тем увереннее алгоритм сочтёт документ настоящей статьёй.</p>
            <p>Смотрите также <a href="/other-page">другую страницу</a> по теме — вот ещё немного текста для длины, и снова длинное предложение ради суммарного объёма содержимого блока статьи.</p>
            <p>Третий абзац для верности, чтобы суммарная длина текста точно перевалила порог читаемости алгоритма — порог по умолчанию довольно высокий, поэтому текста должно быть действительно много.</p>
            </article></body></html>
            """;
        var handler = new StubHandler(_ => Html(articleWithRelativeLink));
        var outcome = await CreateService(handler).ReadAsync(PublicUrl, CancellationToken.None);
        outcome.Success.Should().BeTrue();
        outcome.Markdown.Should().Contain("(http://example.com/other-page)");
    }
}
