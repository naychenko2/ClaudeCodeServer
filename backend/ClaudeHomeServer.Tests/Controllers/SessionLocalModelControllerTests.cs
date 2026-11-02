using System.Collections.Concurrent;
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
[Trait("Category", "Host")]
public class SessionLocalModelControllerTests : IClassFixture<SessionLocalModelControllerTests.Hosts>
{
    private static readonly LocalModelTurnSummary MeterSummary = new()
    {
        OutTokens = 128,
        GenTps = 42.5,
        TtftMs = 300,
        QueueMsApprox = 1500,
        CacheHitPerc = 0.8,
    };

    // Хост на КОНФИГУРАЦИЮ, а не на тест: обычный и с подменённым счётчиком хода. Проект и чат
    // каждый тест заводит сам в своём каталоге
    public sealed class Hosts : IDisposable
    {
        public TestWebApplicationFactory Plain { get; } = new();
        public MeterHost Metered { get; } = new();

        public void Dispose()
        {
            Plain.Dispose();
            Metered.Dispose();
        }
    }

    public sealed class MeterHost : TestWebApplicationFactory
    {
        public FakeMeter Meter { get; } = new();

        public MeterHost()
        {
            ExtraServices = services => services.AddSingleton<ILocalModelTurnMeter>(Meter);
        }
    }

    private readonly TestWebApplicationFactory _factory;
    private readonly HttpClient _client;
    private readonly HttpClient _second;
    private readonly Hosts _hosts;

    public SessionLocalModelControllerTests(Hosts hosts)
    {
        _hosts = hosts;
        _factory = hosts.Plain;
        _client = _factory.CreateAuthenticatedClient();
        _second = _factory.CreateAuthenticatedClient(
            TestWebApplicationFactory.SecondUsername, TestWebApplicationFactory.SecondPassword);
    }

    private async Task<string> CreateSessionAsync(HttpClient client, TestWebApplicationFactory? host = null)
    {
        var dir = Path.Combine((host ?? _factory).TempDir, "lm_" + Guid.NewGuid().ToString("N")[..8]);
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
        using var client = _hosts.Metered.CreateAuthenticatedClient();
        var sessionId = await CreateSessionAsync(client, _hosts.Metered);
        _hosts.Metered.Meter.Set(sessionId, MeterSummary);
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

    // Итог привязан к своей сессии: у соседней сессии того же владельца на том же хосте итога нет.
    // Мутация: отдавать итог любой сессии (GetLastTurn игнорирует sessionId) — тест краснеет.
    [Fact]
    public async Task ИтогХода_ПривязанКСвоейСессии()
    {
        using var client = _hosts.Metered.CreateAuthenticatedClient();
        var withTurn = await CreateSessionAsync(client, _hosts.Metered);
        var without = await CreateSessionAsync(client, _hosts.Metered);
        _hosts.Metered.Meter.Set(withTurn, MeterSummary);

        var resp = await client.GetAsync($"/api/sessions/{without}/local-model");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("lastTurn").ValueKind.Should().Be(JsonValueKind.Null);
    }

    // Фейковый счётчик хода: итог выдаётся только той сессии, для которой его задал тест.
    public sealed class FakeMeter : ILocalModelTurnMeter
    {
        private readonly ConcurrentDictionary<string, LocalModelTurnSummary> _bySession = new();

        public void Set(string sessionId, LocalModelTurnSummary summary) => _bySession[sessionId] = summary;
        public IReadOnlyList<TurnChatLive> ActiveChats() => [];
        public LocalModelTurnSummary? GetLastTurn(string sessionId) =>
            _bySession.TryGetValue(sessionId, out var summary) ? summary : null;
    }
}
