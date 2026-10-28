using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Llm;
using ClaudeHomeServer.Services.Media;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ClaudeHomeServer.Tests.Controllers;

// GET /api/local-model/hardware (вкладка «Железо», Б1): только admin (роль на действии → 403 не-админу),
// плюс серверный флаг local-model-stats (без него 404); неверное window → 400. Поле hardware в stats и
// stats/media — только у админа (у не-админа scope "self" его нет).
[Trait("Category", "Host")]
public class HardwareEndpointTests : IDisposable
{
    private sealed class FakeComfyStats : IComfyStats
    {
        public Task<ComfyMediaStatsSnapshot?> GetMediaAsync(CancellationToken ct) =>
            Task.FromResult<ComfyMediaStatsSnapshot?>(new ComfyMediaStatsSnapshot(
                Configured: true, Status: "working", DownSince: null, Version: "1.0", Gpus: [], LoadedModels: [],
                SlotsUsed: 0, SlotsLimit: 4, Running: [], Queue: [], LastHour: [], Unsupported: []));
    }

    private sealed class FakeReader : ILocalModelStatsReader
    {
        public LocalModelStatsCurrent Current { get; } = new() { Configured = true, Status = "working" };
        public double? TokensPerStep => null;
        public IReadOnlyList<LocalModelStatsPoint> GetHistory(TimeSpan window, long? sinceUnixMs = null) => [];
    }

    private readonly TestWebApplicationFactory _factory;
    private readonly HttpClient _admin;
    private readonly HttpClient _user;

    public HardwareEndpointTests()
    {
        _factory = new TestWebApplicationFactory
        {
            ExtraServices = s =>
            {
                s.AddSingleton<IComfyStats>(new FakeComfyStats());
                s.AddSingleton<ILocalModelStatsReader>(new FakeReader());
            },
        };
        var users = _factory.Services.GetRequiredService<UserStore>();
        users.SetFeatureFlag(users.FindByUsername(TestWebApplicationFactory.TestUsername)!.Id, "local-model-stats", true);
        _admin = _factory.CreateAuthenticatedClient();
        _user = _factory.CreateAuthenticatedClient(
            TestWebApplicationFactory.SecondUsername, TestWebApplicationFactory.SecondPassword);
    }

    public void Dispose() { _admin.Dispose(); _user.Dispose(); _factory.Dispose(); }

    // Мутация: убрать [Authorize(Roles="admin")] с действия — не-админ получит 200, тест краснеет.
    [Fact]
    public async Task НеАдмин_403() =>
        (await _user.GetAsync("/api/local-model/hardware")).StatusCode.Should().Be(HttpStatusCode.Forbidden);

    [Fact]
    public async Task Аноним_401() =>
        (await _factory.CreateClient().GetAsync("/api/local-model/hardware")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);

    [Theory]
    [InlineData("bad")] [InlineData("30m")] [InlineData("2h")]
    public async Task НеверноеОкно_400(string window) =>
        (await _admin.GetAsync($"/api/local-model/hardware?window={window}")).StatusCode.Should().Be(HttpStatusCode.BadRequest);

    [Theory]
    [InlineData("1m")] [InlineData("5m")] [InlineData("15m")] [InlineData("1h")]
    public async Task ВерноеОкно_200_КонтрактОтвета(string window)
    {
        var resp = await _admin.GetAsync($"/api/local-model/hardware?window={window}");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("status").GetString().Should().Be("ok", "нет датчиков (Testing) — пустые узлы, не 500");
        body.GetProperty("level").GetString().Should().Be("ok");
        body.GetProperty("nodes").GetArrayLength().Should().Be(0);
        body.GetProperty("stepMs").GetInt64().Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task БезФлага_404()
    {
        var users = _factory.Services.GetRequiredService<UserStore>();
        users.SetFeatureFlag(users.FindByUsername(TestWebApplicationFactory.TestUsername)!.Id, "local-model-stats", false);
        (await _admin.GetAsync("/api/local-model/hardware")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // hardware есть у админа в stats и stats/media...
    [Fact]
    public async Task Админ_StatsИMedia_ИмеютHardware()
    {
        var stats = await (await _admin.GetAsync("/api/local-model/stats")).Content.ReadFromJsonAsync<JsonElement>();
        stats.TryGetProperty("hardware", out var h1).Should().BeTrue();
        h1.GetProperty("level").GetString().Should().Be("ok");

        var media = await (await _admin.GetAsync("/api/local-model/stats/media")).Content.ReadFromJsonAsync<JsonElement>();
        media.GetProperty("scope").GetString().Should().Be("all");
        media.TryGetProperty("hardware", out _).Should().BeTrue();
    }

    // ...а у не-админа (scope "self") поля нет. Мутация: убрать `isAdmin ?` в контроллере — тест краснеет.
    [Fact]
    public async Task НеАдмин_Media_БезHardware()
    {
        var media = await (await _user.GetAsync("/api/local-model/stats/media")).Content.ReadFromJsonAsync<JsonElement>();
        media.GetProperty("scope").GetString().Should().Be("self");
        media.TryGetProperty("hardware", out _).Should().BeFalse("hardware отдаётся только админу");
    }
}
