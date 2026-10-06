using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace ClaudeHomeServer.Tests.Controllers;

// B4 «Сфер»: персона сферы не ведёт чат и не становится собеседником в проекте вне своей сферы — 400.
public class SphereChatZoneTests : IDisposable
{
    private readonly TestWebApplicationFactory _factory = new();
    private readonly HttpClient _client;

    public SphereChatZoneTests() => _client = _factory.CreateAuthenticatedClient();

    public void Dispose() => _factory.Dispose();

    private T Svc<T>() where T : notnull => _factory.Services.GetRequiredService<T>();

    private string OwnerId => Svc<UserStore>().FindByUsername(TestWebApplicationFactory.TestUsername)!.Id;

    private static async Task<string> IdOf(HttpResponseMessage resp)
    {
        resp.EnsureSuccessStatusCode();
        return (await resp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
    }

    private async Task<string> CreateProjectAsync(string? groupId = null) =>
        await IdOf(await _client.PostAsJsonAsync("/api/projects", new { name = $"chat-{Guid.NewGuid():N}", groupId }));

    private async Task<(string In, string Outside, Persona Persona)> ArrangeAsync()
    {
        (await _client.PutAsJsonAsync($"/api/feature-flags/{FeatureFlagKeys.Spheres}", new { enabled = true }))
            .EnsureSuccessStatusCode();
        var sphere = await IdOf(await _client.PostAsJsonAsync("/api/project-groups", new { name = "чаты", color = "#D97757" }));
        var inside = await CreateProjectAsync(sphere);
        var outside = await CreateProjectAsync();
        var persona = Svc<PersonaManager>().Create(
            OwnerId, "Сферная", null, null, null, null, null, PersonaScope.Sphere, null, null, null, false);
        persona.SphereId = sphere;
        return (inside, outside, persona);
    }

    [Fact]
    public async Task ЧатСПерсонойСферы_ПроектВнеСферы_400()
    {
        var (inside, outside, persona) = await ArrangeAsync();

        var bad = await _client.PostAsJsonAsync($"/api/personas/{persona.Id}/chats", new { projectId = outside });
        var none = await _client.PostAsJsonAsync($"/api/personas/{persona.Id}/chats", new { });
        var ok = await _client.PostAsJsonAsync($"/api/personas/{persona.Id}/chats", new { projectId = inside });

        bad.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        none.StatusCode.Should().Be(HttpStatusCode.BadRequest, "у чата вне проекта зоны сферы нет");
        ok.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task СменаСобеседника_ПерсонаСферыВПроектеВнеСферы_400()
    {
        var (inside, outside, persona) = await ArrangeAsync();
        var plain = Svc<PersonaManager>().Create(
            OwnerId, "Обычная", null, null, null, null, null, PersonaScope.Global, null, null, null, false);
        var chatOutside = await IdOf(await _client.PostAsJsonAsync($"/api/personas/{plain.Id}/chats", new { projectId = outside }));
        var chatInside = await IdOf(await _client.PostAsJsonAsync($"/api/personas/{plain.Id}/chats", new { projectId = inside }));

        var bad = await _client.PostAsJsonAsync($"/api/projects/{outside}/sessions/{chatOutside}/persona", new { personaId = persona.Id });
        var ok = await _client.PostAsJsonAsync($"/api/projects/{inside}/sessions/{chatInside}/persona", new { personaId = persona.Id });

        bad.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        ok.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task ГрупповойЧат_ПерсонаСферыВнеПроекта_400()
    {
        var (_, _, persona) = await ArrangeAsync();
        var plain = Svc<PersonaManager>().Create(
            OwnerId, "Обычная", null, null, null, null, null, PersonaScope.Global, null, null, null, false);

        var resp = await _client.PostAsJsonAsync("/api/chats/group", new { personaIds = new[] { plain.Id, persona.Id } });

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task ВидимостьПерсоныСферы_ТолькоВПроектахСферы()
    {
        var (inside, outside, persona) = await ArrangeAsync();
        var sessions = Svc<SessionManager>();

        sessions.PersonaVisibleIn(persona, inside).Should().BeTrue();
        sessions.PersonaVisibleIn(persona, outside).Should().BeFalse();
    }
}
