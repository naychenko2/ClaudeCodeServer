using System.Net.Http.Json;
using System.Text.Json;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace ClaudeHomeServer.Tests.Controllers;

// MCP personas_create/personas_update не расширяют зону персоны: неизвестный scope — отказ
// (раньше молча становился Global), персона сферы не создаётся и не меняет зону через MCP.
public class PersonasToolsetZoneTests : IDisposable
{
    private readonly TestWebApplicationFactory _factory = new();
    private readonly HttpClient _client;

    public PersonasToolsetZoneTests() => _client = _factory.CreateAuthenticatedClient();

    public void Dispose() => _factory.Dispose();

    private string OwnerId => _factory.Services.GetRequiredService<UserStore>()
        .FindByUsername(TestWebApplicationFactory.TestUsername)!.Id;

    private PersonaManager Personas => _factory.Services.GetRequiredService<PersonaManager>();

    private async Task<string> SessionAsync()
    {
        var project = await _client.PostAsJsonAsync("/api/projects", new { name = $"pz-{Guid.NewGuid():N}" });
        project.EnsureSuccessStatusCode();
        var projectId = (await project.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
        var session = await _client.PostAsJsonAsync($"/api/projects/{projectId}/sessions", new { mode = "acceptEdits" });
        session.EnsureSuccessStatusCode();
        return (await session.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
    }

    private async Task<JsonElement> CallAsync(string sessionId, string tool, object args)
    {
        var resp = await _client.PostAsJsonAsync($"/mcp/personas/{sessionId}", new
        {
            jsonrpc = "2.0", id = 1, method = "tools/call", @params = new { name = tool, arguments = args },
        });
        resp.EnsureSuccessStatusCode();
        return (await resp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("result");
    }

    private static bool IsError(JsonElement r) => r.TryGetProperty("isError", out var e) && e.GetBoolean();
    private static string TextOf(JsonElement r) => r.GetProperty("content")[0].GetProperty("text").GetString()!;

    private Persona NewPersona(PersonaScope scope, string? sphereId = null)
    {
        var p = Personas.Create(OwnerId, "П" + Guid.NewGuid().ToString("N")[..5], null, null, null, null, null,
            PersonaScope.Global, null, null, null, false);
        p.Scope = scope;
        p.SphereId = sphereId;
        return p;
    }

    [Theory]
    [InlineData("sphere", "Зону персоны сферы меняет только человек")]
    [InlineData("galaxy", "Неизвестный scope")]
    public async Task Create_СферныйИНеизвестныйScope_Отказ_ПерсонаНеСоздана(string scope, string expected)
    {
        var sessionId = await SessionAsync();
        var before = Personas.GetByOwner(OwnerId).Count;

        var result = await CallAsync(sessionId, "personas_create",
            new { name = "Чужак" + Guid.NewGuid().ToString("N")[..4], scope });

        IsError(result).Should().BeTrue();
        TextOf(result).Should().Contain(expected);
        Personas.GetByOwner(OwnerId).Count.Should().Be(before, "отказ не должен создавать глобальную персону");
    }

    [Fact]
    public async Task Create_ОбычныйGlobal_КакРаньше()
    {
        var sessionId = await SessionAsync();

        var result = await CallAsync(sessionId, "personas_create",
            new { name = "Глобальная" + Guid.NewGuid().ToString("N")[..4], scope = "global" });

        IsError(result).Should().BeFalse(TextOf(result));
    }

    [Theory]
    [InlineData("global")]
    [InlineData("project")]
    [InlineData("sphere")]
    [InlineData("")]
    public async Task Update_ПерсонаСферы_ЛюбаяСменаЗоны_Отказ_ЗонаНеМеняется(string scope)
    {
        var sessionId = await SessionAsync();
        var target = NewPersona(PersonaScope.Sphere, "S1");

        var result = await CallAsync(sessionId, "personas_update", new { id = target.Id, scope });

        IsError(result).Should().BeTrue();
        TextOf(result).Should().Contain("Зону персоны сферы меняет только человек");
        Personas.Get(target.Id, OwnerId)!.Scope.Should().Be(PersonaScope.Sphere);
        Personas.Get(target.Id, OwnerId)!.SphereId.Should().Be("S1");
    }

    [Theory]
    [InlineData("sphere", "Зону персоны сферы меняет только человек")]
    [InlineData("galaxy", "Неизвестный scope")]
    [InlineData("", "Неизвестный scope")]
    public async Task Update_ГлобальнаяПерсона_СферныйИНеизвестныйScope_Отказ(string scope, string expected)
    {
        var sessionId = await SessionAsync();
        var target = NewPersona(PersonaScope.Global);

        var result = await CallAsync(sessionId, "personas_update", new { id = target.Id, scope });

        IsError(result).Should().BeTrue();
        TextOf(result).Should().Contain(expected);
        Personas.Get(target.Id, OwnerId)!.Scope.Should().Be(PersonaScope.Global);
    }

    [Fact]
    public async Task Update_ОбычнаяПерсонаМеняетЗонуNaGlobal_КакРаньше()
    {
        var sessionId = await SessionAsync();
        var target = NewPersona(PersonaScope.Global);

        var result = await CallAsync(sessionId, "personas_update", new { id = target.Id, scope = "global", role = "Роль" });

        IsError(result).Should().BeFalse(TextOf(result));
    }

    [Fact]
    public async Task Update_ПерсонаСферыБезScope_ПравитсяКакОбычно()
    {
        var sessionId = await SessionAsync();
        var target = NewPersona(PersonaScope.Sphere, "S1");

        var result = await CallAsync(sessionId, "personas_update", new { id = target.Id, role = "Новая роль" });

        IsError(result).Should().BeFalse(TextOf(result));
        Personas.Get(target.Id, OwnerId)!.Scope.Should().Be(PersonaScope.Sphere);
    }
}
