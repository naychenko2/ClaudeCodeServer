using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ClaudeHomeServer.Services.Llm;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ClaudeHomeServer.Tests.Controllers;

// GET /api/sessions/{id}/local-model (фича local-model-stats, шаг 19): итог последнего
// хода на локальной модели для владельца чата. Чужая/неизвестная сессия — 404 (существование
// не раскрываем), аноним — 401.
public class SessionLocalModelControllerTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly TestWebApplicationFactory _factory;
    private readonly HttpClient _client;
    private readonly HttpClient _second;

    public SessionLocalModelControllerTests(TestWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateAuthenticatedClient();
        _second = factory.CreateAuthenticatedClient(
            TestWebApplicationFactory.SecondUsername, TestWebApplicationFactory.SecondPassword);
    }

    private async Task<string> CreateSessionAsync(HttpClient client)
    {
        var dir = Path.Combine(_factory.TempDir, "lm_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        var proj = await client.PostAsJsonAsync("/api/projects", new { name = "LM", rootPath = dir });
        proj.EnsureSuccessStatusCode();
        var projectId = JsonSerializer.Deserialize<JsonElement>(await proj.Content.ReadAsStringAsync())
            .GetProperty("id").GetString()!;
        var resp = await client.PostAsJsonAsync($"/api/projects/{projectId}/sessions", new { mode = "auto" });
        resp.EnsureSuccessStatusCode();
        return JsonSerializer.Deserialize<JsonElement>(await resp.Content.ReadAsStringAsync())
            .GetProperty("id").GetString()!;
    }

    // Владелец, ходов на локальной модели не было → 200, lastTurn: null.
    [Fact]
    public async Task Владелец_БезХодов_200LastTurnNull()
    {
        var sessionId = await CreateSessionAsync(_client);

        var resp = await _client.GetAsync($"/api/sessions/{sessionId}/local-model");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("lastTurn").ValueKind.Should().Be(JsonValueKind.Null);
    }

    // Чужая сессия → 404 (существование не раскрываем). Мутация: вернуть 200 — тест краснеет.
    [Fact]
    public async Task ЧужаяСессия_404()
    {
        var sessionId = await CreateSessionAsync(_second);

        var resp = await _client.GetAsync($"/api/sessions/{sessionId}/local-model");
        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // Аноним → 401.
    [Fact]
    public async Task Аноним_401()
    {
        var sessionId = await CreateSessionAsync(_client);
        var anon = _factory.CreateClient();

        var resp = await anon.GetAsync($"/api/sessions/{sessionId}/local-model");
        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // Итог хода из счётчика (ILocalModelTurnMeter) передаётся в ответ без искажений;
    // prefillTps — null (движок пока не отчитывается). Мутация: перепутать ttftMs/queueMs —
    // тест краснеет.
    [Fact]
    public async Task Владелец_ИтогХода_Передан()
    {
        var summary = new LocalModelTurnSummary
        {
            OutTokens = 128,
            GenTps = 42.5,
            TtftMs = 300,
            QueueMsApprox = 1500,
            CacheHitPerc = 0.8,
        };
        var factory = new TestWebApplicationFactory();
        factory.ExtraServices = services =>
            services.AddSingleton<ILocalModelTurnMeter>(new FakeMeter(summary));
        using (factory)
        using (var client = factory.CreateAuthenticatedClient())
        {
            var sessionId = await CreateSessionAsync(client);
            var resp = await client.GetAsync($"/api/sessions/{sessionId}/local-model");
            resp.StatusCode.Should().Be(HttpStatusCode.OK);
            var last = await resp.Content.ReadFromJsonAsync<JsonElement>();
            var turn = last.GetProperty("lastTurn");
            turn.GetProperty("genTps").GetDouble().Should().Be(42.5);
            turn.GetProperty("prefillTps").ValueKind.Should().Be(JsonValueKind.Null);
            turn.GetProperty("ttftMs").GetInt32().Should().Be(300);
            turn.GetProperty("queueMsApprox").GetInt32().Should().Be(1500);
            turn.GetProperty("cacheHitPerc").GetDouble().Should().Be(0.8);
            turn.GetProperty("outTokens").GetInt32().Should().Be(128);
        }
    }

    // Фейковый счётчик хода: один и тот же итог на любой сессии.
    private sealed class FakeMeter(LocalModelTurnSummary? summary) : ILocalModelTurnMeter
    {
        public IReadOnlyList<TurnChatLive> ActiveChats() => [];
        public LocalModelTurnSummary? GetLastTurn(string sessionId) => summary;
    }
}
