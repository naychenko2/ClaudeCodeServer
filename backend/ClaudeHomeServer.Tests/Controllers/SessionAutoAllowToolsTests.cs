using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace ClaudeHomeServer.Tests.Controllers;

// Снятие инструмента с «Разрешать всегда»: DELETE /api/sessions/{id}/auto-allow?tool=Bash.
// Единый маршрут по id сессии — покрывает и проектные сессии, и чаты вне проекта
// (как /pending и снимки промпта в этом же контроллере).
public class SessionAutoAllowToolsTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly TestWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public SessionAutoAllowToolsTests(TestWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateAuthenticatedClient();
    }

    private async Task<string> CreateProjectSessionAsync()
    {
        var dir = Path.Combine(_factory.TempDir, "autoallow_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        var projectResp = await _client.PostAsJsonAsync("/api/projects",
            new { name = "AutoAllow", rootPath = dir });
        projectResp.EnsureSuccessStatusCode();
        var project = JsonSerializer.Deserialize<JsonElement>(await projectResp.Content.ReadAsStringAsync());
        var projectId = project.GetProperty("id").GetString()!;

        var sessionResp = await _client.PostAsJsonAsync($"/api/projects/{projectId}/sessions", new { mode = "auto" });
        sessionResp.EnsureSuccessStatusCode();
        var session = JsonSerializer.Deserialize<JsonElement>(await sessionResp.Content.ReadAsStringAsync());
        return session.GetProperty("id").GetString()!;
    }

    // Список пополняет ответ «Разрешать всегда» на карточке (SignalR RespondPermission);
    // в тесте сеем его напрямую через SessionManager — интерес здесь к снятию и контракту.
    private void SeedAutoAllow(string sessionId, params string[] tools)
    {
        var sessions = _factory.Services.GetRequiredService<SessionManager>();
        sessions.GetById(sessionId)!.AutoAllowTools.AddRange(tools);
    }

    private async Task<JsonElement> GetSessionAsync(string sessionId)
    {
        var resp = await _client.GetAsync($"/api/chats/{sessionId}");
        resp.EnsureSuccessStatusCode();
        return JsonSerializer.Deserialize<JsonElement>(await resp.Content.ReadAsStringAsync());
    }

    // Контракт для фронта: поле есть у любой сессии, тип — массив строк (пустой по умолчанию)
    [Fact]
    public async Task СессияОтдаётПолеAutoAllowTools()
    {
        var id = await CreateProjectSessionAsync();

        var session = await GetSessionAsync(id);

        session.GetProperty("autoAllowTools").ValueKind.Should().Be(JsonValueKind.Array);
        session.GetProperty("autoAllowTools").GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task Delete_УбираетИнструментИОтдаётОбновлённуюСессию()
    {
        var id = await CreateProjectSessionAsync();
        SeedAutoAllow(id, "Bash", "Write");

        var resp = await _client.DeleteAsync($"/api/sessions/{id}/auto-allow?tool=Bash");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = JsonSerializer.Deserialize<JsonElement>(await resp.Content.ReadAsStringAsync());
        body.GetProperty("autoAllowTools").EnumerateArray().Select(e => e.GetString())
            .Should().BeEquivalentTo(["Write"]);
        (await GetSessionAsync(id)).GetProperty("autoAllowTools").EnumerateArray()
            .Select(e => e.GetString()).Should().BeEquivalentTo(["Write"], "снятие должно пережить перезапрос");
    }

    [Fact]
    public async Task Delete_БезИнструмента_400()
    {
        var id = await CreateProjectSessionAsync();

        var resp = await _client.DeleteAsync($"/api/sessions/{id}/auto-allow");

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Delete_ЧужаяСессия_404()
    {
        var id = await CreateProjectSessionAsync();
        SeedAutoAllow(id, "Bash");
        using var stranger = _factory.CreateAuthenticatedClient(
            TestWebApplicationFactory.SecondUsername, TestWebApplicationFactory.SecondPassword);

        var resp = await stranger.DeleteAsync($"/api/sessions/{id}/auto-allow?tool=Bash");

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await GetSessionAsync(id)).GetProperty("autoAllowTools").EnumerateArray()
            .Select(e => e.GetString()).Should().BeEquivalentTo(["Bash"], "чужое снятие не должно применяться");
    }

    [Fact]
    public async Task Delete_НесуществующаяСессия_404()
    {
        var resp = await _client.DeleteAsync("/api/sessions/no-such-session/auto-allow?tool=Bash");
        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}
