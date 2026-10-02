using System.Net;
using ClaudeHomeServer.Services.Architecture;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;

namespace ClaudeHomeServer.Tests.Services;

// Раздача сборки Viaduct: честный «не установлен», подхват сборки без рестарта,
// заголовки под iframe-песочницу и отсутствие провала в SPA-фолбэк CCS.
public sealed class ViaductStaticHostingTests : IAsyncLifetime
{
    private readonly string _dist = Path.Combine(Path.GetTempPath(), "ccs-viaduct-" + Guid.NewGuid().ToString("N"), "dist");
    private WebApplication _app = null!;
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        _app = builder.Build();
        _app.UseViaductStatic(_dist);
        // Имитация SPA-фолбэка CCS — именно endpoint, как MapFallbackToFile в Program.cs:
        // роутинг выбирает его ДО ветки, и StaticFiles при выбранном endpoint молчат
        _app.MapFallback(ctx => ctx.Response.WriteAsync("ccs-spa-fallback"));
        await _app.StartAsync();
        _client = _app.GetTestClient();
    }

    public async Task DisposeAsync()
    {
        await _app.DisposeAsync();
        var root = Path.GetDirectoryName(_dist)!;
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }

    private void Install()
    {
        Directory.CreateDirectory(Path.Combine(_dist, "assets"));
        File.WriteAllText(Path.Combine(_dist, "index.html"), "<!doctype html><title>viaduct</title>");
        File.WriteAllText(Path.Combine(_dist, "assets", "index-abc.js"), "export {}");
        File.WriteAllText(Path.Combine(_dist, ".viaduct-build.json"), "{}");
    }

    [Fact]
    public async Task НеУстановлен_404СКодом()
    {
        var resp = await _client.GetAsync("/modules/viaduct/");

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await resp.Content.ReadAsStringAsync()).Should().Contain(ViaductStaticHosting.NotInstalledCode);
    }

    [Fact]
    public async Task УстановленПриЖивомСервере_ОтдаётсяБезРестарта()
    {
        (await _client.GetAsync("/modules/viaduct/")).StatusCode.Should().Be(HttpStatusCode.NotFound);

        Install();
        var resp = await _client.GetAsync("/modules/viaduct/");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        (await resp.Content.ReadAsStringAsync()).Should().Contain("<title>viaduct</title>");
        resp.Headers.GetValues("Content-Security-Policy").Single()
            .Should().Be(ViaductStaticHosting.ContentSecurityPolicy);
        resp.Headers.GetValues("Access-Control-Allow-Origin").Single().Should().Be("*");
    }

    [Fact]
    public async Task Ассет_ВечныйКэшИCors()
    {
        Install();
        var resp = await _client.GetAsync("/modules/viaduct/assets/index-abc.js");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        resp.Headers.CacheControl!.ToString().Should().Contain("immutable");
        resp.Headers.GetValues("Access-Control-Allow-Origin").Single().Should().Be("*");
    }

    [Theory]
    [InlineData("/modules/viaduct/editor")]
    [InlineData("/modules/viaduct/.viaduct-build.json")]
    public async Task Промах_404БезSpaФолбэка(string path)
    {
        Install();
        var resp = await _client.GetAsync(path);

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await resp.Content.ReadAsStringAsync()).Should().NotContain("ccs-spa-fallback");
    }

    [Fact]
    public async Task Запись_405()
    {
        Install();
        var resp = await _client.PostAsync("/modules/viaduct/index.html", new StringContent(""));

        resp.StatusCode.Should().Be(HttpStatusCode.MethodNotAllowed);
    }
}
