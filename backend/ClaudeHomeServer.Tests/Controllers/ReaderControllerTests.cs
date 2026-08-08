using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.Reader;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace ClaudeHomeServer.Tests.Controllers;

// Эндпоинты POST /api/reader/read и POST /api/reader/embed-check (ADR-006 §1): авторизация
// и счастливый путь. Сетевую часть подменяем StubHandler на именованном клиенте "link-reader" —
// редиректы, SSRF-рубежи и вердикты встраиваемости уже покрыты ReaderServiceTests/ReaderEmbedCheckTests
// без реального HTTP.
public class ReaderControllerTests(TestWebApplicationFactory factory) : IClassFixture<TestWebApplicationFactory>
{
    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(respond(request));
    }

    private const string ReadableArticleHtml = """
        <html><head><title>t</title></head><body><article>
        <h1>Заголовок статьи</h1>
        <p>Первый абзац с достаточным количеством текста для читаемости — чем длиннее связный текст, тем увереннее алгоритм сочтёт документ настоящей статьёй, а не пустой оболочкой.</p>
        <p>Второй абзац тоже длинный, добавляет содержательности и веса кандидату при оценке текстовой плотности блока — чем больше связного текста, тем лучше для итоговой оценки читаемости.</p>
        <p>Третий абзац для верности, чтобы суммарная длина текста точно перевалила порог читаемости алгоритма — порог по умолчанию довольно высокий, поэтому текста должно быть действительно много.</p>
        </article></body></html>
        """;

    private WebApplicationFactory<Program> WithStubReader(Func<HttpRequestMessage, HttpResponseMessage> respond) =>
        factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.AddHttpClient(ReaderService.HttpClientName)
                .ConfigurePrimaryHttpMessageHandler(() => new StubHandler(respond));
        }));

    private static async Task<HttpClient> AuthenticatedClientAsync(WebApplicationFactory<Program> f)
    {
        var client = f.CreateClient();
        var login = await client.PostAsJsonAsync("/api/auth/login", new
        {
            username = TestWebApplicationFactory.TestUsername,
            password = TestWebApplicationFactory.TestPassword,
        });
        login.EnsureSuccessStatusCode();
        var body = await login.Content.ReadFromJsonAsync<JsonElement>();
        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", body.GetProperty("token").GetString());
        return client;
    }

    [Fact]
    public async Task Read_БезАвторизации_401()
    {
        using var f = WithStubReader(_ => throw new InvalidOperationException("не должно дойти"));
        using var client = f.CreateClient();

        var resp = await client.PostAsJsonAsync("/api/reader/read", new { url = "http://example.com/" });

        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Read_ВозвращаетMarkdown()
    {
        using var f = WithStubReader(_ => new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new StringContent(ReadableArticleHtml, Encoding.UTF8, "text/html") });
        using var client = await AuthenticatedClientAsync(f);

        var resp = await client.PostAsJsonAsync("/api/reader/read", new { url = "http://example.com/article" });

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        body.TryGetProperty("error", out _).Should().BeFalse();
        body.GetProperty("markdown").GetString().Should().Contain("Заголовок статьи");
    }

    [Fact]
    public async Task Image_ВозвращаетБайтыКартинки()
    {
        using var f = WithStubReader(_ => new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new ByteArrayContent([1, 2, 3]) { Headers = { ContentType = new("image/png") } } });
        using var client = await AuthenticatedClientAsync(f);

        var resp = await client.GetAsync("/api/reader/image?url=" + Uri.EscapeDataString("http://example.com/pic.png"));

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        resp.Content.Headers.ContentType!.MediaType.Should().Be("image/png");
        (await resp.Content.ReadAsByteArrayAsync()).Should().Equal([1, 2, 3]);
    }

    [Fact]
    public async Task Image_ЛокальныйАдрес_502БезИсключения()
    {
        using var f = WithStubReader(_ => throw new InvalidOperationException("не должно дойти"));
        using var client = await AuthenticatedClientAsync(f);

        var resp = await client.GetAsync("/api/reader/image?url=" + Uri.EscapeDataString("http://127.0.0.1/pic.png"));

        resp.StatusCode.Should().Be(HttpStatusCode.BadGateway);
    }

    [Fact]
    public async Task Read_ЛокальныйАдрес_ОтдаётКодОшибкиБез500()
    {
        using var f = WithStubReader(_ => throw new InvalidOperationException("SSRF-рубеж должен остановить раньше сети"));
        using var client = await AuthenticatedClientAsync(f);

        var resp = await client.PostAsJsonAsync("/api/reader/read", new { url = "http://127.0.0.1/" });

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("error").GetProperty("code").GetString().Should().Be("local-address");
    }

    // ---------- embed-check (ADR-006 §1) ----------

    [Fact]
    public async Task EmbedCheck_БезАвторизации_401()
    {
        using var f = WithStubReader(_ => throw new InvalidOperationException("не должно дойти"));
        using var client = f.CreateClient();

        var resp = await client.PostAsJsonAsync("/api/reader/embed-check", new { url = "http://example.com/" });

        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task EmbedCheck_ВстраиваемБезReason()
    {
        using var f = WithStubReader(_ => new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new StringContent(ReadableArticleHtml, Encoding.UTF8, "text/html") });
        using var client = await AuthenticatedClientAsync(f);

        var resp = await client.PostAsJsonAsync("/api/reader/embed-check", new { url = "http://example.com/article" });

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("embeddable").GetBoolean().Should().BeTrue();
        body.TryGetProperty("reason", out _).Should().BeFalse("у embeddable: true причины нет");
    }

    [Fact]
    public async Task EmbedCheck_XfoDeny_ИдётОбщимХендлеромСRead()
    {
        // Вердикт приходит из стаба, подменяющего primary-handler именно именованного клиента
        // "link-reader": второй клиент с дефолтным хендлером до стаба бы не дошёл.
        using var f = WithStubReader(_ =>
        {
            var resp = new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent(ReadableArticleHtml, Encoding.UTF8, "text/html") };
            resp.Headers.Add("X-Frame-Options", "DENY");
            return resp;
        });
        using var client = await AuthenticatedClientAsync(f);

        var resp = await client.PostAsJsonAsync("/api/reader/embed-check", new { url = "http://example.com/article" });

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("embeddable").GetBoolean().Should().BeFalse();
        body.GetProperty("reason").GetString().Should().Be("frame-denied");
    }

    [Fact]
    public async Task EmbedCheck_ПустойUrl_InvalidUrl()
    {
        using var f = WithStubReader(_ => throw new InvalidOperationException("не должно дойти"));
        using var client = await AuthenticatedClientAsync(f);

        var resp = await client.PostAsJsonAsync("/api/reader/embed-check", new { url = "" });

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("embeddable").GetBoolean().Should().BeFalse();
        body.GetProperty("reason").GetString().Should().Be("invalid-url");
    }

    [Fact]
    public async Task EmbedCheck_RateКвотаОбщаяСRead()
    {
        // Квота существует ради IP-репутации сервера, и embed-check — исходящий запрос:
        // счётчик один на /read и /embed-check (ADR-006 §1).
        using var f = WithStubReader(_ => new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new StringContent(ReadableArticleHtml, Encoding.UTF8, "text/html") });
        using var client = await AuthenticatedClientAsync(f);

        for (var i = 0; i < ReaderQuotaService.MaxPerMinutePerOwner; i++)
        {
            var ok = await client.PostAsJsonAsync("/api/reader/embed-check", new { url = "http://example.com/article" });
            ok.StatusCode.Should().Be(HttpStatusCode.OK);
        }

        var overLimit = await client.PostAsJsonAsync("/api/reader/read", new { url = "http://example.com/article" });
        overLimit.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
    }
}
