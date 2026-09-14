using System.Net;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace ClaudeHomeServer.Tests.Controllers;

// /api/proxy: белый список доменов и WARN при отказе. Сторож синхронности списков с
// фронтендом живёт в ProxyAllowedHostsSyncTests, конкретный отказ разрешённого/чужого
// хоста — здесь.
public class ProxyControllerTests : IClassFixture<TestWebApplicationFactory>
{
    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(respond(request));
    }

    // Хелпер для теста: создаём ОТДЕЛЬНЫЙ TestWebApplicationFactory с подменой
    // HttpClient "media-proxy" на стаб. Без WithWebHostBuilder — иначе теряем
    // TestWebApplicationFactory-методы вроде CreateAuthenticatedClient.
    private static TestWebApplicationFactory WithMediaProxy(Func<HttpRequestMessage, HttpResponseMessage> respond)
    {
        var f = new TestWebApplicationFactory();
        var original = f.ExtraServices;
        f.ExtraServices = services =>
        {
            original?.Invoke(services);
            services.AddHttpClient("media-proxy")
                .ConfigurePrimaryHttpMessageHandler(() => new StubHandler(respond));
        };
        return f;
    }

    [Fact]
    public async Task Proxy_РазрешённыйХост_Проксирует()
    {
        // Точный distribution Higgsfield — главная цель правки; смотрим, что он
        // действительно проходит и тело/тип проброшены клиенту.
        using var f = WithMediaProxy(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent([0x89, 0x50, 0x4E, 0x47])
            {
                Headers = { ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/png") }
            }
        });
        using var client = f.CreateAuthenticatedClient();

        var resp = await client.GetAsync(
            "/api/proxy?url=" + Uri.EscapeDataString("https://d8j0ntlcm91z4.cloudfront.net/test.png"));

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        resp.Content.Headers.ContentType!.MediaType.Should().Be("image/png");
    }

    [Fact]
    public async Task Proxy_ЧужойПоддоменCloudfront_400()
    {
        // Общий суффикс cloudfront.net НЕ открыт: distribution там заводит кто угодно, а
        // /api/proxy ходит с сервера и отдаёт ответ клиенту — открывать его = стать
        // прокси на чужой контент. Соседний distribution должен отлетать 400-кой.
        using var f = WithMediaProxy(_ => throw new InvalidOperationException("не должно дойти до upstream"));
        using var client = f.CreateAuthenticatedClient();

        var resp = await client.GetAsync(
            "/api/proxy?url=" + Uri.EscapeDataString("https://evil.cloudfront.net/secret.png"));

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var text = await resp.Content.ReadAsStringAsync();
        text.Should().Contain("Домен не разрешён");
    }

    [Fact]
    public async Task Proxy_НеHttps_400()
    {
        using var f = WithMediaProxy(_ => throw new InvalidOperationException("не должно дойти до upstream"));
        using var client = f.CreateAuthenticatedClient();

        var resp = await client.GetAsync(
            "/api/proxy?url=" + Uri.EscapeDataString("http://fal.media/test.png"));

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var text = await resp.Content.ReadAsStringAsync();
        text.Should().Contain("Только HTTPS");
    }
}
