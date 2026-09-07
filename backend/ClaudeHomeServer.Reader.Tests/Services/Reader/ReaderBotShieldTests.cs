using System.Net;
using System.Text;
using ClaudeHomeServer.Services.Reader;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClaudeHomeServer.Tests.Services.Reader;

/// <summary>
/// Распознавание бот-щита и разведение классов отказа (дефект 2026-09-07: ридер считал
/// щитом ЛЮБОЙ сайт за Cloudflare, потому что маркером служил заголовок <c>cf-ray</c> —
/// а он есть в каждом ответе Cloudflare, включая успешный 200).
/// </summary>
/// <remarks>
/// Категории <c>Dns</c> здесь НЕТ намеренно, в отличие от <see cref="ReaderServiceTests"/>:
/// хост задан IP-литералом, который SsrfGuard классифицирует без обращения к резолверу.
/// Иначе главный тест дефекта («200 с CF-RAY читается») выпадал бы из локального прогона
/// <c>--filter "Category!=Dns"</c> — то есть из того самого прогона, который его сторожит.
/// 93.184.216.34 — публичный адрес, в сеть тест не ходит: HTTP подменён StubHandler.
/// </remarks>
public class ReaderBotShieldTests
{
    private const string PublicUrl = "http://93.184.216.34/article";

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(respond(request));
    }

    private sealed class StubHttpFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private static ReaderService CreateService(Func<HttpRequestMessage, HttpResponseMessage> respond) =>
        new(new StubHttpFactory(new StubHandler(respond)), TestConfig.Build([]),
            NullLogger<ReaderService>.Instance);

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

    private static HttpResponseMessage Html(string body, HttpStatusCode code, params (string Name, string Value)[] headers)
    {
        var resp = new HttpResponseMessage(code) { Content = new StringContent(body, Encoding.UTF8, "text/html") };
        foreach (var (name, value) in headers) resp.Headers.Add(name, value);
        return resp;
    }

    // ---------- Сам дефект ----------

    [Fact]
    public async Task Успех200_СЗаголовкомCfRay_СтраницаЧитается()
    {
        // CF-RAY — идентификатор запроса Cloudflare, он есть в КАЖДОМ её ответе. Отказ по нему
        // означал отказ почти всему вебу.
        var outcome = await CreateService(_ => Html(ReadableArticleHtml, HttpStatusCode.OK, ("CF-RAY", "8f2a1b3c4d5e-DME")))
            .ReadAsync(PublicUrl, CancellationToken.None);

        outcome.Success.Should().BeTrue("cf-ray без cf-mitigated — не блокировка, а обычный ответ Cloudflare");
        outcome.Markdown.Should().Contain("Заголовок статьи");
    }

    [Fact]
    public async Task Успех200_СЗаголовкомCfMitigated_БотЩит()
    {
        // cf-mitigated ставится, только когда Cloudflare сам погасил запрос
        var outcome = await CreateService(_ => Html(ReadableArticleHtml, HttpStatusCode.OK, ("cf-mitigated", "challenge")))
            .ReadAsync(PublicUrl, CancellationToken.None);

        outcome.Success.Should().BeFalse();
        outcome.Error.Should().Be(ReaderErrorCode.BlockedBySite);
    }

    [Fact]
    public async Task Успех200_СЗаглушкойJustAMomentВТеле_БотЩит()
    {
        // Классическая заглушка Cloudflare приходит С КОДОМ 200 и без cf-mitigated (его ставит
        // не всякая конфигурация) — после сужения заголовочного признака тело осталось
        // ЕДИНСТВЕННЫМ признаком щита на этом пути. Снятие HasBotShieldBody из ParseArticle
        // обязано ронять этот тест.
        var outcome = await CreateService(_ => Html(
                "<html><head><title>Just a moment...</title></head><body>"
                + "<p>Checking your browser before accessing the site.</p></body></html>",
                HttpStatusCode.OK))
            .ReadAsync(PublicUrl, CancellationToken.None);

        outcome.Success.Should().BeFalse();
        outcome.Error.Should().Be(ReaderErrorCode.BlockedBySite, "заглушка щита — не «нечитаемая страница»");
        outcome.HttpStatus.Should().Be(200);
    }

    [Theory]
    [InlineData("Attention Required! | Cloudflare")]
    [InlineData("Access denied")]
    [InlineData("You have been blocked")]
    public async Task Статус403_СТитуломБлокСтраницы_БотЩитАНеТребуетсяВход(string title)
    {
        // Так падают Akamai, DataDome, Imperva, PerimeterX и правило Block самого Cloudflare.
        // Диагноз «страница требует входа» тут уверенно неверный.
        var outcome = await CreateService(_ => Html(
                $"<html><head><title>{title}</title></head><body>blocked</body></html>",
                HttpStatusCode.Forbidden))
            .ReadAsync(PublicUrl, CancellationToken.None);

        outcome.Error.Should().Be(ReaderErrorCode.BlockedBySite);
        outcome.HttpStatus.Should().Be(403);
    }

    [Fact]
    public async Task Редирект302БезLocation_СайтОтветилОшибкой_АНеСетевойСбой()
    {
        // Сайт ОТВЕТИЛ — значит канал жив; «не удалось соединиться» с приписанным HTTP-кодом
        // противоречило бы само себе
        var outcome = await CreateService(_ => new HttpResponseMessage(HttpStatusCode.Found))
            .ReadAsync(PublicUrl, CancellationToken.None);

        outcome.Error.Should().Be(ReaderErrorCode.ServerError);
        outcome.HttpStatus.Should().Be(302);
    }

    // ---------- Классы отказа разведены ----------

    [Fact]
    public async Task Статус403_СCfRayНоБезCfMitigated_НеБотЩит()
    {
        var outcome = await CreateService(_ => Html("forbidden", HttpStatusCode.Forbidden, ("cf-ray", "abc123")))
            .ReadAsync(PublicUrl, CancellationToken.None);

        outcome.Error.Should().Be(ReaderErrorCode.AuthRequired);
        outcome.HttpStatus.Should().Be(403);
    }

    [Fact]
    public async Task Статус403_СCfMitigated_БотЩитСКодом()
    {
        var outcome = await CreateService(_ => Html("<html><body>blocked</body></html>",
                HttpStatusCode.Forbidden, ("cf-mitigated", "challenge")))
            .ReadAsync(PublicUrl, CancellationToken.None);

        outcome.Error.Should().Be(ReaderErrorCode.BlockedBySite);
        outcome.HttpStatus.Should().Be(403);
    }

    [Fact]
    public async Task Статус429_БотЩитСКодом()
    {
        var outcome = await CreateService(_ => new HttpResponseMessage(HttpStatusCode.TooManyRequests))
            .ReadAsync(PublicUrl, CancellationToken.None);

        outcome.Error.Should().Be(ReaderErrorCode.BlockedBySite);
        outcome.HttpStatus.Should().Be(429);
    }

    [Fact]
    public async Task ОбрывСоединения_Unreachable_БезКодаИБезЩита()
    {
        var outcome = await CreateService(_ => throw new HttpRequestException("соединение разорвано"))
            .ReadAsync(PublicUrl, CancellationToken.None);

        outcome.Error.Should().Be(ReaderErrorCode.Unreachable, "сетевой сбой — не блокировка сайтом");
        outcome.HttpStatus.Should().BeNull("ответа не было, кода взяться неоткуда");
    }

    // ---------- Проба встраиваемости (тот же маркер) ----------

    [Fact]
    public async Task Проба_Статус403_СCfRay_НеБотЩит()
    {
        var result = await CreateService(_ => Html("forbidden", HttpStatusCode.Forbidden, ("cf-ray", "abc123")))
            .CheckEmbedAsync(PublicUrl, CancellationToken.None);

        result.Reason.Should().Be("auth-required");
    }

    [Fact]
    public async Task Проба_Статус403_СCfMitigated_БотЩит()
    {
        var result = await CreateService(_ => Html("blocked", HttpStatusCode.Forbidden, ("cf-mitigated", "challenge")))
            .CheckEmbedAsync(PublicUrl, CancellationToken.None);

        result.Reason.Should().Be("blocked-by-site");
    }
}
