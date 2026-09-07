using System.Net;
using System.Text;
using ClaudeHomeServer.Services.Reader;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClaudeHomeServer.Tests.Services.Reader;

/// <summary>
/// Порог длины текста, ниже которого SmartReader отказывается считать страницу статьёй
/// (дефект 2026-09-07: <c>CharThreshold</c> не задавался, работал дефолт библиотеки в
/// 500 символов — и целый класс живых страниц отвергался как «нечитаемые»).
/// </summary>
/// <remarks>
/// Категории <c>Dns</c> здесь НЕТ намеренно, по тем же соображениям, что и в
/// <see cref="ReaderBotShieldTests"/>: хост задан IP-литералом, который SsrfGuard
/// классифицирует без обращения к резолверу, поэтому тесты дефекта остаются в локальном
/// прогоне <c>--filter "Category!=Dns"</c> — том самом, который их и сторожит.
/// </remarks>
public class ReaderCharThresholdTests
{
    private const string PublicUrl = "http://93.184.216.34/";

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(respond(request));
    }

    private sealed class StubHttpFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private static ReaderService CreateService(string html, Dictionary<string, string?>? settings = null) =>
        new(new StubHttpFactory(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(html, Encoding.UTF8, "text/html"),
            })),
            TestConfig.Build(settings ?? []), NullLogger<ReaderService>.Instance);

    /// <summary>
    /// Разметка example.com слово в слово: одноэкранная страница, полезного текста ~170
    /// символов. Ниже дефолтных 500 по построению — правкой сети или заголовков это не лечится.
    /// </summary>
    private const string ShortPageHtml = """
        <html><head><title>Example Domain</title></head><body>
        <div>
            <h1>Example Domain</h1>
            <p>This domain is for use in illustrative examples in documents. You may use this
            domain in literature without prior coordination or asking for permission.</p>
            <p><a href="https://www.iana.org/domains/example">More information...</a></p>
        </div>
        </body></html>
        """;

    [Fact]
    public async Task КороткаяСтраница_ОколоПорога_Читается()
    {
        var outcome = await CreateService(ShortPageHtml).ReadAsync(PublicUrl, CancellationToken.None);

        outcome.Success.Should().BeTrue("страница в один экран — обычная страница, а не пустышка");
        outcome.Markdown.Should().Contain("illustrative examples");
    }

    [Fact]
    public async Task ПустоеТело_ПоПрежнемуNotReadable()
    {
        var outcome = await CreateService("<html><head><title>t</title></head><body></body></html>")
            .ReadAsync(PublicUrl, CancellationToken.None);

        outcome.Success.Should().BeFalse();
        outcome.Error.Should().Be(ReaderErrorCode.NotReadable);
    }

    [Fact]
    public async Task ТолькоНавигация_ПоПрежнемуNotReadable()
    {
        // Ради этого случая порог и не опускается к нулю: обвязка из ссылок — не контент,
        // отдать её вместо текста хуже, чем честно отказать.
        var navOnly = """
            <html><head><title>Меню</title></head><body>
            <nav>
              <ul>
                <li><a href="/">Главная</a></li>
                <li><a href="/about">О компании</a></li>
                <li><a href="/services">Услуги и цены</a></li>
                <li><a href="/portfolio">Портфолио работ</a></li>
                <li><a href="/blog">Блог и новости</a></li>
                <li><a href="/contacts">Контакты и адреса</a></li>
                <li><a href="/vacancies">Вакансии компании</a></li>
              </ul>
            </nav>
            </body></html>
            """;

        var outcome = await CreateService(navOnly).ReadAsync(PublicUrl, CancellationToken.None);

        outcome.Success.Should().BeFalse("ссылочная обвязка — не текст страницы");
        outcome.Error.Should().Be(ReaderErrorCode.NotReadable);
    }

    /// <summary>
    /// Граница порога: у <see cref="ShortPageHtml"/> извлекается ровно 175 символов текста
    /// (замер 2026-09-07), поэтому порог 175 обязан пропускать страницу, а 176 — отвергать.
    /// Обе стороны в одном тесте: если разбор фикстуры съедет, упадут оба случая сразу и
    /// причина будет видна, а не спрячется за односторонним ассертом.
    /// </summary>
    [Theory]
    [InlineData(175, true)]
    [InlineData(176, false)]
    public async Task ПорогСравниваетсяСтрого_ГраницаНаДлинеТекста(int threshold, bool expectedSuccess)
    {
        var outcome = await CreateService(ShortPageHtml,
                new Dictionary<string, string?> { ["Reader:CharThreshold"] = threshold.ToString() })
            .ReadAsync(PublicUrl, CancellationToken.None);

        outcome.Success.Should().Be(expectedSuccess);
    }

    [Fact]
    public async Task ПорогБерётсяИзКонфига_ЗавышенныйСноваОтвергаетСтраницу()
    {
        var outcome = await CreateService(ShortPageHtml,
                new Dictionary<string, string?> { ["Reader:CharThreshold"] = "5000" })
            .ReadAsync(PublicUrl, CancellationToken.None);

        outcome.Success.Should().BeFalse("значение из конфига обязано доезжать до SmartReader");
        outcome.Error.Should().Be(ReaderErrorCode.NotReadable);
    }
}
