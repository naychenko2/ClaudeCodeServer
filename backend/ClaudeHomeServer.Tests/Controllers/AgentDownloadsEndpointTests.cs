using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using ClaudeHomeServer.Services.Desktop;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace ClaudeHomeServer.Tests.Controllers;

/// <summary>
/// Раздача агента и самоотзыв устройства сквозь настоящий хост (agent-distribution AD-3):
/// маршруты, анонимность, лимит по IP, гейт Desktop и схема авторизации устройства.
/// Фронт в хосте не раздаётся (<c>UseFrontend = false</c>): иначе SPA-фолбэк отвечал бы
/// 200 на всё, что мимо маршрутов, и 404 здесь ничего бы не доказывал.
/// </summary>
public class AgentDownloadsEndpointTests : IDisposable
{
    private readonly AgentReleaseFixture _rel = new();
    private readonly TestWebApplicationFactory _factory;

    public AgentDownloadsEndpointTests()
    {
        _factory = new TestWebApplicationFactory { UseFrontend = false };
        foreach (var (k, v) in _rel.Settings()) _factory.ExtraConfig[k] = v;
    }

    public void Dispose()
    {
        _factory.Dispose();
        _rel.Dispose();
    }

    private static string ArchiveUrl(string file = AgentReleaseFixture.WinFile, string rid = "win-x64") =>
        $"/agent/{AgentReleaseFixture.Version}/{rid}/{file}";

    [Theory]
    [InlineData("/agent/install.ps1")]
    [InlineData("/agent/install.sh")]
    [InlineData("/agent/manifest.json")]
    public async Task Анонимно_СкриптыИУказатель_200(string url)
    {
        using var client = _factory.CreateClient();

        var response = await client.GetAsync(url);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync()).Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task Анонимно_Архив_200БайтВБайт_ИHead()
    {
        using var client = _factory.CreateClient();

        var get = await client.GetAsync(ArchiveUrl());
        get.StatusCode.Should().Be(HttpStatusCode.OK);
        (await get.Content.ReadAsByteArrayAsync()).Should().Equal(_rel.WinBytes);

        var head = await client.SendAsync(new HttpRequestMessage(HttpMethod.Head, ArchiveUrl()));
        head.StatusCode.Should().Be(HttpStatusCode.OK);
        head.Content.Headers.ContentLength.Should().Be(_rel.WinBytes.Length);
    }

    [Theory]
    [InlineData("/agent/1.200.0/win-x64/..%2F..%2Fapp%2Fagent-release.json")]
    [InlineData("/agent/1.200.0/win-x64/%2e%2e%2f%2e%2e%2fapp%2fagent-release.json")]
    [InlineData("/agent/1.200.0/win-x64/..%5C..%5Capp%5Cagent-release.json")]
    [InlineData("/agent/%2e%2e/win-x64/ai-home-agent-1.200.0-win-x64.zip")]
    [InlineData("/agent/1.200.0/win-x64/manifest.json")]
    [InlineData("/agent/1.200.0/osx-arm64/ai-home-agent-1.200.0-win-x64.zip")]
    [InlineData("/agent/1.9.0/win-x64/ai-home-agent-1.200.0-win-x64.zip")]
    public async Task ВраждебныйПуть_404(string url)
    {
        using var client = _factory.CreateClient();

        var response = await client.GetAsync(url);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound, url);
    }

    [Fact]
    public async Task ЛимитПоIp_СверхЛимита429()
    {
        _factory.ExtraConfig["DeviceAgent:DownloadRateLimit"] = "2";
        using var client = _factory.CreateClient();

        (await client.GetAsync("/agent/manifest.json")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.GetAsync("/agent/install.sh")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.GetAsync(ArchiveUrl())).StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
    }

    [Fact]
    public async Task КаталогаНет_503СПричиной()
    {
        Directory.Delete(_rel.Root, recursive: true);
        using var client = _factory.CreateClient();

        var response = await client.GetAsync("/agent/manifest.json");

        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("error").GetString().Should().StartWith(AgentReleaseCatalog.NotServedPrefix);
    }

    [Fact]
    public async Task ДесктопВыключен_503АНе500()
    {
        using var disabled = new DesktopDisabledFactory();
        foreach (var (k, v) in _rel.Settings()) disabled.ExtraConfig[k] = v;
        using var client = disabled.CreateClient();

        foreach (var url in new[] { "/agent/install.sh", "/agent/manifest.json", ArchiveUrl() })
        {
            var response = await client.GetAsync(url);
            response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable, url);
            (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString()
                .Should().StartWith(AgentReleaseCatalog.NotServedPrefix);
        }

        disabled.Services.GetService<AgentReleaseCatalog>().Should().BeNull("гейт стоит на регистрации");
    }

    private sealed class DesktopDisabledFactory : TestWebApplicationFactory
    {
        public DesktopDisabledFactory() => UseFrontend = false;

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            // Гейт читается при регистрации, раньше колбэков ConfigureAppConfiguration: доезжает
            // только хост-настройкой (разбор — в шапке NotesDisabledTests)
            builder.UseSetting("Subsystems:desktop:Enabled", "false");
        }
    }

    // ---------- DELETE /api/devices/self (agent-distribution Р12) ----------

    private (string DeviceId, string Token, string Fingerprint) Pair(string name, string machine)
    {
        var registry = _factory.Services.GetRequiredService<DeviceRegistry>();
        var fingerprint = MachineFingerprint.Of(machine);
        var (device, token) = registry.Register("owner-self-revoke", name, fingerprint);
        return (device.Id, token, fingerprint);
    }

    private static HttpRequestMessage SelfRevoke(string? token, string? fingerprint)
    {
        var request = new HttpRequestMessage(HttpMethod.Delete, "/api/devices/self");
        if (token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Device", token);
        if (fingerprint is not null) request.Headers.Add(DesktopDeviceAuthHandler.FingerprintHeader, fingerprint);
        return request;
    }

    [Fact]
    public async Task Самоотзыв_СвоимТокеном_НадгробиеТолькоСвоё()
    {
        var mine = Pair("home", "machine-a");
        var other = Pair("work", "machine-b");
        using var client = _factory.CreateClient();

        var response = await client.SendAsync(SelfRevoke(mine.Token, mine.Fingerprint));

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
        var registry = _factory.Services.GetRequiredService<DeviceRegistry>();
        registry.Get("owner-self-revoke", mine.DeviceId)!.Revoked.Should().BeTrue();
        registry.Get("owner-self-revoke", other.DeviceId)!.Revoked.Should().BeFalse("чужое устройство не трогается");
        registry.Authenticate(mine.Token, mine.Fingerprint).Should().BeNull("токен умер вместе с устройством");

        (await client.SendAsync(SelfRevoke(mine.Token, mine.Fingerprint))).StatusCode
            .Should().Be(HttpStatusCode.Unauthorized, "отозванный токен больше ничего не открывает");
    }

    [Fact]
    public async Task Самоотзыв_БезСвоегоТокена_401()
    {
        var mine = Pair("home", "machine-a");
        var other = Pair("work", "machine-b");
        using var anonymous = _factory.CreateClient();
        using var web = _factory.CreateAuthenticatedClient();

        (await anonymous.SendAsync(SelfRevoke(null, null))).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await anonymous.SendAsync(SelfRevoke(mine.Token, other.Fingerprint))).StatusCode
            .Should().Be(HttpStatusCode.Unauthorized, "токен с чужой машины не проходит сверку отпечатка");
        (await anonymous.SendAsync(SelfRevoke(mine.Token[..^2] + "xx", mine.Fingerprint))).StatusCode
            .Should().Be(HttpStatusCode.Unauthorized);
        (await web.DeleteAsync("/api/devices/self")).StatusCode
            .Should().Be(HttpStatusCode.Unauthorized, "веб-JWT самоотзыв не открывает");

        var registry = _factory.Services.GetRequiredService<DeviceRegistry>();
        registry.Get("owner-self-revoke", mine.DeviceId)!.Revoked.Should().BeFalse();
        registry.Get("owner-self-revoke", other.DeviceId)!.Revoked.Should().BeFalse();
    }
}
