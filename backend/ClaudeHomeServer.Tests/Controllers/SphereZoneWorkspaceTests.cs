using System.Net.Http.Json;
using System.Text.Json;
using ClaudeHomeServer.Controllers;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace ClaudeHomeServer.Tests.Controllers;

// B3 «Сфер»: персона сферы видит задачи и файлы ВСЕХ проектов сферы и ни одного вне её;
// состав зоны считается на каждый вызов — выход проекта из сферы снимает доступ сразу.
public class SphereZoneWorkspaceTests : IDisposable
{
    private readonly TestWebApplicationFactory _factory = new();
    private readonly HttpClient _client;

    public SphereZoneWorkspaceTests() => _client = _factory.CreateAuthenticatedClient();

    public void Dispose() => _factory.Dispose();

    private T Svc<T>() where T : notnull => _factory.Services.GetRequiredService<T>();

    private string OwnerId => Svc<UserStore>().FindByUsername(TestWebApplicationFactory.TestUsername)!.Id;

    private static async Task<string> IdOf(HttpResponseMessage resp)
    {
        resp.EnsureSuccessStatusCode();
        return (await resp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
    }

    private async Task<string> CreateProjectAsync(string? groupId = null) =>
        await IdOf(await _client.PostAsJsonAsync("/api/projects", new { name = $"zone-{Guid.NewGuid():N}", groupId }));

    private async Task<(string Sphere, string In1, string In2, string Outside, Persona Persona)> ArrangeAsync()
    {
        (await _client.PutAsJsonAsync($"/api/feature-flags/{FeatureFlagKeys.Spheres}", new { enabled = true }))
            .EnsureSuccessStatusCode();
        var sphere = await IdOf(await _client.PostAsJsonAsync("/api/project-groups", new { name = "зона", color = "#D97757" }));
        var in1 = await CreateProjectAsync(sphere);
        var in2 = await CreateProjectAsync(sphere);
        var outside = await CreateProjectAsync();
        var persona = Svc<PersonaManager>().Create(
            OwnerId, "Сферная", null, null, null, null, null, PersonaScope.Sphere, null, null, null, false);
        persona.SphereId = sphere;
        return (sphere, in1, in2, outside, persona);
    }

    [Fact]
    public async Task Задачи_СкоупыПоПроектамСферы_ПолныеИБезВнеШнихПроектов()
    {
        var (_, in1, in2, outside, persona) = await ArrangeAsync();
        var bindings = Svc<PersonaBindingsService>();

        var scopes = bindings.BuildExternalTaskScopes(OwnerId, persona);

        scopes.Select(s => s.ProjectId).Should().BeEquivalentTo([in1, in2]);
        scopes.Should().OnlyContain(s => !s.ReadOnly);
        scopes.Select(s => s.ProjectId).Should().NotContain(outside);
        persona.Bindings.Should().BeNullOrEmpty("синтетические скоупы в стор привязок не пишутся");
    }

    [Fact]
    public async Task Задачи_ИсполнительПерсонаСферы_ТолькоВПроектахСферы()
    {
        var (_, in1, _, outside, persona) = await ArrangeAsync();
        var bindings = Svc<PersonaBindingsService>();
        var personas = Svc<PersonaManager>();
        var scopes = bindings.BuildExternalTaskScopes(OwnerId, persona);

        TaskPersonaValidator.Error(personas, OwnerId, persona.Id, in1, scopes).Should().BeNull();
        TaskPersonaValidator.Error(personas, OwnerId, persona.Id, outside, scopes).Should().NotBeNull();
        TaskPersonaValidator.Error(personas, OwnerId, persona.Id, null).Should().NotBeNull();
    }

    [Fact]
    public async Task Задачи_ПерсонаСферы_НеИсполнительЛичнойЗадачи()
    {
        var (_, _, _, _, persona) = await ArrangeAsync();
        var scopes = Svc<PersonaBindingsService>().BuildExternalTaskScopes(OwnerId, persona);

        TaskPersonaValidator.Error(Svc<PersonaManager>(), OwnerId, persona.Id, null, scopes)
            .Should().Be("Персона сферы может выполнять только задачи проектов своей сферы");
    }

    [Fact]
    public async Task Файлы_ЗонаРабочегоПространства_ПроектыСферы_ИНеNull()
    {
        var (_, in1, in2, outside, persona) = await ArrangeAsync();

        var plan = Svc<SessionManager>().BuildWorkspacePlan(OwnerId, in1, persona);

        plan.Should().NotBeNull();
        plan!.AllowedProjectIds.Should().NotBeNull("null означал бы все проекты владельца");
        plan.AllowedProjectIds.Should().BeEquivalentTo([in1, in2]);
        plan.AllowedProjectIds.Should().NotContain(outside);
    }

    [Fact]
    public async Task ПроектВышелИзСферы_СледующийВызовУжеБезНего()
    {
        var (_, in1, in2, _, persona) = await ArrangeAsync();
        var sessions = Svc<SessionManager>();
        var bindings = Svc<PersonaBindingsService>();
        sessions.BuildWorkspacePlan(OwnerId, in1, persona)!.AllowedProjectIds.Should().Contain(in2);

        (await _client.PutAsJsonAsync($"/api/projects/{in2}", new { groupId = "" })).EnsureSuccessStatusCode();

        sessions.BuildWorkspacePlan(OwnerId, in1, persona)!.AllowedProjectIds.Should().BeEquivalentTo([in1]);
        bindings.BuildExternalTaskScopes(OwnerId, persona).Select(s => s.ProjectId).Should().BeEquivalentTo([in1]);
    }

    [Fact]
    public async Task ПустаяЗона_ЗонаРабочегоПространстваНеСнимается()
    {
        var (_, in1, in2, _, persona) = await ArrangeAsync();
        foreach (var id in new[] { in1, in2 })
            (await _client.PutAsJsonAsync($"/api/projects/{id}", new { groupId = "" })).EnsureSuccessStatusCode();

        var plan = Svc<SessionManager>().BuildWorkspacePlan(OwnerId, null, persona);

        // Пустой список потребители читают как «без сужения» — нужна заглушка
        plan!.AllowedProjectIds.Should().Equal(SessionManager.NoProjectsZone);
    }
}
