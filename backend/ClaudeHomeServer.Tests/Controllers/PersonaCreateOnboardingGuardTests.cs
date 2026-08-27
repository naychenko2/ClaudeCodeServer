using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace ClaudeHomeServer.Tests.Controllers;

// Серверный предохранитель personas_create (план 2.9, PersonasController.Create): в личном
// знакомстве (сессия с OnboardingKind == "user") создание новой персоны запрещено, ПОКА
// заготовка-ассистент резолвится в живую персону — интервью обязано дорабатывать её через
// personas_update, а не плодить дубликат. Резолв, а не проверка на непустоту: мёртвый
// AssistantPersonaId разрешает создание (деградация мастера). Проектное знакомство создаёт
// руководителя штатно — предохранитель его не трогает.
public class PersonaCreateOnboardingGuardTests : IDisposable
{
    private readonly TestWebApplicationFactory _factory = new();
    private readonly HttpClient _client;
    private readonly string _tempDir;

    public PersonaCreateOnboardingGuardTests()
    {
        _client = _factory.CreateAuthenticatedClient();
        _tempDir = Path.Combine(_factory.TempDir, "persona_create_guard_tests");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose() => _factory.Dispose();

    private async Task<JsonElement> MeAsync() => JsonSerializer.Deserialize<JsonElement>(
        await (await _client.GetAsync("/api/auth/me")).Content.ReadAsStringAsync());

    private async Task EnsureHomeConfiguredAsync()
    {
        var homes = Path.Combine(_factory.TempDir, "guard_homes");
        Directory.CreateDirectory(homes);
        (await _client.PutAsJsonAsync("/api/settings", new { defaultProjectsPath = homes }))
            .EnsureSuccessStatusCode();
    }

    private async Task<string> StartUserOnboardingAsync()
    {
        var response = await _client.PostAsync("/api/onboarding/user/start", null);
        response.EnsureSuccessStatusCode();
        return JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync())
            .GetProperty("id").GetString()!;
    }

    private async Task<HttpResponseMessage> CreatePersonaFromSessionAsync(string sessionId, object body)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/personas");
        request.Headers.Add("X-Caller-Session-Id", sessionId);
        request.Content = JsonContent.Create(body);
        return await _client.SendAsync(request);
    }

    [Fact]
    public async Task UserОнбординг_ЖиваяЗаготовка_Create_400()
    {
        // Заготовку завёл стартовый проход провижна: AssistantPersonaId резолвится в живую
        var sessionId = await StartUserOnboardingAsync();

        var response = await CreatePersonaFromSessionAsync(sessionId, new { name = "Дубликат" });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).Should().Contain("personas_update");
    }

    [Fact]
    public async Task UserОнбординг_МёртваяЗаготовка_Create_200()
    {
        var sessionId = await StartUserOnboardingAsync();
        var userId = (await MeAsync()).GetProperty("userId").GetString()!;

        // Заготовка «умерла»: AssistantPersonaId висит на несуществующей персоне — резолв
        // в PersonasController.Create даёт null, предохранитель не срабатывает (деградация).
        var users = _factory.Services.GetRequiredService<UserStore>();
        users.SetAssistantPersona(userId, "dead-assistant-id-does-not-exist");

        var response = await CreatePersonaFromSessionAsync(sessionId, new { name = "Новый ассистент" });

        response.StatusCode.Should().Be(HttpStatusCode.OK,
            "мёртвый id заготовки не должен запирать знакомство навсегда");
    }

    [Fact]
    public async Task ProjectОнбординг_ЖиваяЗаготовка_Create_200()
    {
        // Личный дефолт (нужен для старта проектного знакомства) есть от стартового прохода
        await EnsureHomeConfiguredAsync();

        var dir = Path.Combine(_tempDir, "proj_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        var projectResponse = await _client.PostAsJsonAsync("/api/projects", new { name = "GuardProject", rootPath = dir });
        projectResponse.EnsureSuccessStatusCode();
        var projectId = JsonSerializer.Deserialize<JsonElement>(await projectResponse.Content.ReadAsStringAsync())
            .GetProperty("id").GetString()!;

        var startResponse = await _client.PostAsync($"/api/onboarding/project/{projectId}/start", null);
        startResponse.EnsureSuccessStatusCode();
        var sessionId = JsonSerializer.Deserialize<JsonElement>(await startResponse.Content.ReadAsStringAsync())
            .GetProperty("id").GetString()!;

        var response = await CreatePersonaFromSessionAsync(sessionId,
            new { name = "Руководитель", scope = "project", projectId });

        response.StatusCode.Should().Be(HttpStatusCode.OK,
            "предохранитель ограничивает только личное знакомство (OnboardingKind == user)");
    }
}
