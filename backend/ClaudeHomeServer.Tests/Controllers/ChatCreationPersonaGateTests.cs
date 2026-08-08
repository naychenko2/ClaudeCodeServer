using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Controllers;

// Бэк-страховка инварианта «новый чат человека — только с персоной» (флаг
// default-personas-onboarding): под флагом создание без personaId/resumeSessionId — 400,
// без флага — прежнее поведение байт-в-байт. Флаг выставляется явно В КАЖДОМ тесте
// (состояние per-user живёт в общей фабрике класса, порядок тестов не гарантирован).
public class ChatCreationPersonaGateTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly TestWebApplicationFactory _factory;
    private readonly HttpClient _client;
    private readonly string _tempDir;

    public ChatCreationPersonaGateTests(TestWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateAuthenticatedClient();
        _tempDir = Path.Combine(factory.TempDir, "persona_gate_tests");
        Directory.CreateDirectory(_tempDir);
    }

    private async Task SetFlagAsync(bool enabled)
    {
        var response = await _client.PutAsJsonAsync(
            $"/api/feature-flags/{FeatureFlagKeys.DefaultPersonasOnboarding}", new { enabled });
        response.EnsureSuccessStatusCode();
    }

    private async Task EnsureHomeConfiguredAsync()
    {
        var homes = Path.Combine(_factory.TempDir, "homes");
        Directory.CreateDirectory(homes);
        (await _client.PutAsJsonAsync("/api/settings", new { defaultProjectsPath = homes }))
            .EnsureSuccessStatusCode();
    }

    private async Task<string> CreateProjectAsync()
    {
        var dir = Path.Combine(_tempDir, "proj_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        var response = await _client.PostAsJsonAsync("/api/projects", new { name = "GateProject", rootPath = dir });
        response.EnsureSuccessStatusCode();
        return JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync())
            .GetProperty("id").GetString()!;
    }

    private async Task<string> CreatePersonaAsync()
    {
        var response = await _client.PostAsJsonAsync("/api/personas", new { name = "Собеседница" });
        response.EnsureSuccessStatusCode();
        return JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync())
            .GetProperty("id").GetString()!;
    }

    [Fact]
    public async Task ПодФлагом_СессияБезПерсоны_ПровижнитИСоздаёт()
    {
        await SetFlagAsync(true);
        var projectId = await CreateProjectAsync();

        // План 2.4: под флагом без personaId сервер НЕ отвечает 400, а провижнит ассистента
        // (рубеж 2.4 в паре с фронт-правкой 4.3) и создаёт чат с ним. personaId сессии
        // совпадает с дефолтом владельца — ассистентом, созданным включением флага (2.2).
        var response = await _client.PostAsJsonAsync($"/api/projects/{projectId}/sessions", new { mode = "auto" });
        response.StatusCode.Should().Be(HttpStatusCode.Created);

        var me = JsonSerializer.Deserialize<JsonElement>(
            await (await _client.GetAsync("/api/auth/me")).Content.ReadAsStringAsync());
        var defaultId = me.GetProperty("defaultPersonaId").GetString();
        JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync())
            .GetProperty("personaId").GetString().Should().Be(defaultId);
    }

    [Fact]
    public async Task ПодФлагом_СессияСПерсоной_Создаётся()
    {
        await SetFlagAsync(true);
        var projectId = await CreateProjectAsync();
        var personaId = await CreatePersonaAsync();

        var response = await _client.PostAsJsonAsync($"/api/projects/{projectId}/sessions",
            new { mode = "auto", personaId });
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync())
            .GetProperty("personaId").GetString().Should().Be(personaId);
    }

    [Fact]
    public async Task ПодФлагом_СессияСResume_Проходит()
    {
        await SetFlagAsync(true);
        var projectId = await CreateProjectAsync();

        // resumeSessionId — продолжение существующего разговора, персона не требуется
        var response = await _client.PostAsJsonAsync($"/api/projects/{projectId}/sessions",
            new { mode = "auto", resumeSessionId = "resume-abc-123" });
        response.StatusCode.Should().Be(HttpStatusCode.Created);
    }

    [Fact]
    public async Task БезФлага_СессияБезПерсоны_КакРаньше()
    {
        await SetFlagAsync(false);
        var projectId = await CreateProjectAsync();

        var response = await _client.PostAsJsonAsync($"/api/projects/{projectId}/sessions", new { mode = "auto" });
        response.StatusCode.Should().Be(HttpStatusCode.Created);
    }

    [Fact]
    public async Task ПодФлагом_ЧатБезПерсоны_ПровижнитИСоздаёт()
    {
        await SetFlagAsync(true);
        await EnsureHomeConfiguredAsync();

        // План 2.4: под флагом без personaId сервер провижнит ассистента и создаёт чат с ним.
        var response = await _client.PostAsJsonAsync("/api/chats", new { mode = "auto" });
        response.StatusCode.Should().Be(HttpStatusCode.Created);

        var me = JsonSerializer.Deserialize<JsonElement>(
            await (await _client.GetAsync("/api/auth/me")).Content.ReadAsStringAsync());
        var defaultId = me.GetProperty("defaultPersonaId").GetString();
        JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync())
            .GetProperty("personaId").GetString().Should().Be(defaultId);
    }

    [Fact]
    public async Task ПодФлагом_ЧатСПерсоной_Создаётся()
    {
        await SetFlagAsync(true);
        await EnsureHomeConfiguredAsync();
        var personaId = await CreatePersonaAsync();

        var response = await _client.PostAsJsonAsync("/api/chats", new { mode = "auto", personaId });
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync())
            .GetProperty("personaId").GetString().Should().Be(personaId);
    }

    [Fact]
    public async Task БезФлага_ЧатБезПерсоны_КакРаньше()
    {
        await SetFlagAsync(false);
        await EnsureHomeConfiguredAsync();

        var response = await _client.PostAsJsonAsync("/api/chats", new { mode = "auto" });
        response.StatusCode.Should().Be(HttpStatusCode.Created);
    }
}
