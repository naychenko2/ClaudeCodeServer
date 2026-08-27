using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Controllers;

// Дефолт-персоны: назначение make-default (валидация зон,
// ручной путь профиль НЕ меняет) и удаление дефолтной только с преемником по той же зоне.
public class PersonasControllerDefaultTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly HttpClient _client;
    private readonly string _tempDir;

    public PersonasControllerDefaultTests(TestWebApplicationFactory factory)
    {
        _client = factory.CreateAuthenticatedClient();
        _tempDir = Path.Combine(factory.TempDir, "persona_default_tests");
        Directory.CreateDirectory(_tempDir);
    }

    private async Task<JsonElement> PostJsonAsync(string url, object body)
    {
        var response = await _client.PostAsJsonAsync(url, body);
        response.EnsureSuccessStatusCode();
        return JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync());
    }

    private async Task<string> CreateGlobalPersonaAsync(string name)
    {
        var persona = await PostJsonAsync("/api/personas", new { name });
        return persona.GetProperty("id").GetString()!;
    }

    private async Task<string> CreateProjectAsync()
    {
        var dir = Path.Combine(_tempDir, "proj_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        var project = await PostJsonAsync("/api/projects", new { name = "DefaultsProject", rootPath = dir });
        return project.GetProperty("id").GetString()!;
    }

    private async Task<string> CreateProjectPersonaAsync(string projectId, string name)
    {
        var persona = await PostJsonAsync("/api/personas", new { name, scope = "project", projectId });
        return persona.GetProperty("id").GetString()!;
    }

    private async Task<JsonElement> MeAsync()
    {
        var response = await _client.GetAsync("/api/auth/me");
        response.EnsureSuccessStatusCode();
        return JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task MakeDefault_Глобальная_СтановитсяЛичнымДефолтом()
    {
        var personaId = await CreateGlobalPersonaAsync("Личный дефолт");

        var response = await _client.PostAsync($"/api/personas/{personaId}/make-default", null);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var me = await MeAsync();
        me.GetProperty("defaultPersonaId").GetString().Should().Be(personaId);
    }

    [Fact]
    public async Task MakeDefault_Проектная_СтановитсяДефолтомПроекта()
    {
        var projectId = await CreateProjectAsync();
        var personaId = await CreateProjectPersonaAsync(projectId, "Руководитель");

        var response = await _client.PostAsync($"/api/personas/{personaId}/make-default", null);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var project = JsonSerializer.Deserialize<JsonElement>(
            await (await _client.GetAsync($"/api/projects/{projectId}")).Content.ReadAsStringAsync());
        project.GetProperty("defaultPersonaId").GetString().Should().Be(personaId);
    }

    [Fact]
    public async Task MakeDefault_НесуществующаяПерсона_404()
    {
        var response = await _client.PostAsync("/api/personas/nonexistent/make-default", null);
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task РучнойMakeDefault_НеМеняетПрофильПерсоны()
    {
        // Ручная смена дефолта (REST без X-Caller-Session-Id) НЕ досевает профиль:
        // молчаливая дозапись Access=Full + personas-manage была бы тихой эскалацией прав
        var personaId = await CreateGlobalPersonaAsync("Без досева");
        (await _client.PostAsync($"/api/personas/{personaId}/make-default", null))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        var persona = JsonSerializer.Deserialize<JsonElement>(
            await (await _client.GetAsync($"/api/personas/{personaId}")).Content.ReadAsStringAsync());
        persona.GetProperty("specialty").GetString().Should().Be("none",
            "ручной make-default не должен назначать Coordinator");
        var hasManageBinding = persona.TryGetProperty("bindings", out var bindings)
            && bindings.ValueKind == JsonValueKind.Array
            && bindings.EnumerateArray().Any(b =>
                b.GetProperty("target").GetString() == "personas-manage");
        hasManageBinding.Should().BeFalse("ручной make-default не должен досевать привязку personas-manage");
    }

    [Fact]
    public async Task Delete_Дефолтной_БезПреемника_400()
    {
        var personaId = await CreateGlobalPersonaAsync("Незаменимая");
        (await _client.PostAsync($"/api/personas/{personaId}/make-default", null)).EnsureSuccessStatusCode();

        var response = await _client.DeleteAsync($"/api/personas/{personaId}");
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).Should().Contain("преемника");

        // Персона на месте — удаление не состоялось
        (await _client.GetAsync($"/api/personas/{personaId}")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Delete_Дефолтной_ПреемникНеТойЗоны_400()
    {
        var personaId = await CreateGlobalPersonaAsync("Дефолт с зоной");
        (await _client.PostAsync($"/api/personas/{personaId}/make-default", null)).EnsureSuccessStatusCode();
        var projectId = await CreateProjectAsync();
        var projectPersonaId = await CreateProjectPersonaAsync(projectId, "Проектный кандидат");

        // Преемник личного дефолта обязан быть глобальной персоной
        var response = await _client.DeleteAsync($"/api/personas/{personaId}?successorId={projectPersonaId}");
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Delete_Дефолтной_СПреемником_ПереназначаетИУдаляет()
    {
        var oldId = await CreateGlobalPersonaAsync("Уходящая");
        var successorId = await CreateGlobalPersonaAsync("Преемница");
        (await _client.PostAsync($"/api/personas/{oldId}/make-default", null)).EnsureSuccessStatusCode();

        var response = await _client.DeleteAsync($"/api/personas/{oldId}?successorId={successorId}");
        response.StatusCode.Should().Be(HttpStatusCode.NoContent);

        (await MeAsync()).GetProperty("defaultPersonaId").GetString().Should().Be(successorId);
        (await _client.GetAsync($"/api/personas/{oldId}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Delete_ДефолтнойПроекта_СПреемникомЭтогоПроекта_Переназначает()
    {
        var projectId = await CreateProjectAsync();
        var oldId = await CreateProjectPersonaAsync(projectId, "Старый руководитель");
        var successorId = await CreateProjectPersonaAsync(projectId, "Новый руководитель");
        (await _client.PostAsync($"/api/personas/{oldId}/make-default", null)).EnsureSuccessStatusCode();

        var response = await _client.DeleteAsync($"/api/personas/{oldId}?successorId={successorId}");
        response.StatusCode.Should().Be(HttpStatusCode.NoContent);

        var project = JsonSerializer.Deserialize<JsonElement>(
            await (await _client.GetAsync($"/api/projects/{projectId}")).Content.ReadAsStringAsync());
        project.GetProperty("defaultPersonaId").GetString().Should().Be(successorId);
    }
}
