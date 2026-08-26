using ClaudeHomeServer.Services;
using FluentAssertions;
using Microsoft.AspNetCore.Http;

namespace ClaudeHomeServer.Tests.Services;

/// <summary>
/// Трансформер форварда на дев-сервер поддомена.
///
/// Главный сторож здесь — заголовок Host. База YARP копирует его из исходного запроса, и
/// дев-сервер видел публичное имя поддомена: Vite с webpack-dev-server такой Host не пускают
/// («Blocked request. This host is not allowed»), а выглядит это как поломка прокси.
/// </summary>
public class ExternalPreviewTransformerTests
{
    private const int Port = 5173;
    private static readonly HostString Public = new("svc.example.me", 8080);

    private static async Task<HttpRequestMessage> TransformAsync()
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Host = Public;
        ctx.Request.Method = "GET";
        ctx.Request.Path = "/assets/app.js";

        var request = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{Port}/assets/app.js");
        // Ровно то, что делает база: тянет Host исходного запроса за собой
        request.Headers.TryAddWithoutValidation("Host", Public.Value);

        var sut = new ExternalPreviewTransformer(Port, Public, https: true);
        await sut.TransformRequestAsync(ctx, request, $"http://127.0.0.1:{Port}", CancellationToken.None);
        return request;
    }

    [Fact]
    public async Task Host_не_уезжает_на_дев_сервер()
    {
        var request = await TransformAsync();

        request.Headers.Host.Should().BeNull(
            "null означает «подставь хост назначения» — а публичное имя дев-сервер отбил бы");
    }

    /// <summary>
    /// Публичное имя не выбрасывается, а переезжает в X-Forwarded-*: оттуда его берут
    /// фреймворки, которым надо построить внешний адрес.
    /// </summary>
    [Fact]
    public async Task Публичное_имя_уходит_в_forwarded_заголовки()
    {
        var request = await TransformAsync();

        request.Headers.GetValues("X-Forwarded-Host").Should().ContainSingle()
            .Which.Should().Be(Public.Value);
        request.Headers.GetValues("X-Forwarded-Proto").Should().ContainSingle()
            .Which.Should().Be("https");
        request.Headers.GetValues("X-Forwarded-Port").Should().ContainSingle()
            .Which.Should().Be("8080");
    }

    /// <summary>
    /// Редирект дев-сервера на самого себя клиенту бесполезен — на его машине этого порта
    /// нет. Делаем адрес относительным, чтобы браузер остался на поддомене.
    /// </summary>
    [Theory]
    [InlineData("http://127.0.0.1:5173/next?a=1", "/next?a=1")]
    [InlineData("http://localhost:5173/next", "/next")]
    [InlineData("http://[::1]:5173/next", "/next")]
    public async Task Свой_редирект_становится_относительным(string location, string expected)
    {
        var ctx = new DefaultHttpContext();
        ctx.Response.Headers.Location = location;
        var response = new HttpResponseMessage(System.Net.HttpStatusCode.Redirect);

        var sut = new ExternalPreviewTransformer(Port, Public, https: true);
        await sut.TransformResponseAsync(ctx, response, CancellationToken.None);

        ctx.Response.Headers.Location.ToString().Should().Be(expected);
    }

    /// <summary>Чужой адрес не трогаем: уход на внешний сайт может быть осмысленным.</summary>
    [Fact]
    public async Task Чужой_редирект_остаётся_как_есть()
    {
        var ctx = new DefaultHttpContext();
        ctx.Response.Headers.Location = "https://example.com/auth";
        var response = new HttpResponseMessage(System.Net.HttpStatusCode.Redirect);

        var sut = new ExternalPreviewTransformer(Port, Public, https: true);
        await sut.TransformResponseAsync(ctx, response, CancellationToken.None);

        ctx.Response.Headers.Location.ToString().Should().Be("https://example.com/auth");
    }
}
