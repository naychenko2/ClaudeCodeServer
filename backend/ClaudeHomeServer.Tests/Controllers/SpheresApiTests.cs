using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Spheres;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace ClaudeHomeServer.Tests.Controllers;

// A5 «Сфер»: членство проекта как выдача прав (400 на чужую/несуществующую сферу, MCP не
// переносит проект, событие), удаление сферы с командой (409) и сводка сферы.
public class SpheresApiTests : IDisposable
{
    private readonly TestWebApplicationFactory _factory = new();
    private readonly HttpClient _client;
    private readonly HttpClient _stranger;

    public SpheresApiTests()
    {
        _client = _factory.CreateAuthenticatedClient();
        _stranger = _factory.CreateAuthenticatedClient(
            TestWebApplicationFactory.SecondUsername, TestWebApplicationFactory.SecondPassword);
    }

    public void Dispose() => _factory.Dispose();

    private string OwnerId => _factory.Services.GetRequiredService<UserStore>()
        .FindByUsername(TestWebApplicationFactory.TestUsername)!.Id;

    private static async Task<string> IdOf(HttpResponseMessage resp)
    {
        resp.EnsureSuccessStatusCode();
        return (await resp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
    }

    private static async Task<string> CreateSphereAsync(HttpClient c, string name) =>
        await IdOf(await c.PostAsJsonAsync("/api/project-groups", new { name, color = "#D97757" }));

    private async Task<string> CreateProjectAsync(HttpClient c, string? groupId = null) =>
        await IdOf(await c.PostAsJsonAsync("/api/projects", new { name = $"sp-{Guid.NewGuid():N}", groupId }));

    private async Task EnableFlagAsync() =>
        (await _client.PutAsJsonAsync($"/api/feature-flags/{FeatureFlagKeys.Spheres}", new { enabled = true }))
            .EnsureSuccessStatusCode();

    [Fact]
    public async Task REST_ЧужаяИНесуществующаяСфера_400()
    {
        var foreign = await CreateSphereAsync(_stranger, "чужая");
        var projectId = await CreateProjectAsync(_client);

        var upd = await _client.PutAsJsonAsync($"/api/projects/{projectId}", new { groupId = foreign });
        upd.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await _client.PutAsJsonAsync($"/api/projects/{projectId}", new { groupId = "нет-такой" }))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await _client.PostAsJsonAsync("/api/projects", new { name = "x-" + Guid.NewGuid().ToString("N"), groupId = foreign }))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var own = await CreateSphereAsync(_client, "своя");
        (await _client.PutAsJsonAsync($"/api/projects/{projectId}", new { groupId = own }))
            .StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Событие_СменаСферыПоднимаетSphereMembershipChanged()
    {
        var sphere = await CreateSphereAsync(_client, "для события");
        var projectId = await CreateProjectAsync(_client);
        var events = new List<SphereMembershipChanged>();
        _factory.Services.GetRequiredService<ProjectManager>().OnSphereMembershipChanged += events.Add;

        await _client.PutAsJsonAsync($"/api/projects/{projectId}", new { groupId = sphere });
        await _client.PutAsJsonAsync($"/api/projects/{projectId}", new { name = "без смены" });

        events.Should().ContainSingle().Which.Should().Be(
            new SphereMembershipChanged(OwnerId, projectId, null, sphere));
    }

    [Fact]
    public async Task Удаление_СПерсонойСферы_409_БезНеё_204()
    {
        var sphere = await CreateSphereAsync(_client, "с командой");
        var persona = _factory.Services.GetRequiredService<PersonaManager>().Create(
            OwnerId, "Сферная", null, null, null, null, null, PersonaScope.Sphere, null, null, null, false);
        persona.SphereId = sphere;

        var refused = await _client.DeleteAsync($"/api/project-groups/{sphere}");
        refused.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await refused.Content.ReadAsStringAsync()).Should().Contain("персон сферы: 1");

        persona.SphereId = null;
        (await _client.DeleteAsync($"/api/project-groups/{sphere}")).StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task Overview_ПроектыКомандаЗадачи_ЧужаяСфера404()
    {
        await EnableFlagAsync();
        var sphere = await CreateSphereAsync(_client, "обзор");
        var projectId = await CreateProjectAsync(_client, sphere);
        var persona = _factory.Services.GetRequiredService<PersonaManager>().Create(
            OwnerId, "Сферная", null, null, null, null, null, PersonaScope.Sphere, null, null, null, false);
        persona.SphereId = sphere;
        await IdOf(await _client.PostAsJsonAsync($"/api/projects/{projectId}/tasks", new { title = "открытая" }));

        var body = await _client.GetFromJsonAsync<JsonElement>($"/api/spheres/{sphere}/overview");
        body.GetProperty("sphere").GetProperty("id").GetString().Should().Be(sphere);
        var project = body.GetProperty("projects").EnumerateArray().Should().ContainSingle().Subject;
        project.GetProperty("id").GetString().Should().Be(projectId);
        project.GetProperty("capabilities").GetProperty("host").GetString().Should().Be("server");
        body.GetProperty("team").EnumerateArray().Select(t => t.GetProperty("id").GetString())
            .Should().ContainSingle().Which.Should().Be(persona.Id);
        body.GetProperty("memory").GetProperty("count").GetInt32().Should().Be(0);
        body.GetProperty("openTasks").EnumerateArray().Select(t => t.GetProperty("title").GetString())
            .Should().Contain("открытая");

        (await _stranger.GetAsync($"/api/spheres/{sphere}/overview")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await _client.GetAsync("/api/spheres/нет/overview")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    private async Task<JsonElement> CallToolAsync(string sessionId, string tool, object args)
    {
        var resp = await _client.PostAsJsonAsync($"/mcp/wsp/{sessionId}", new
        {
            jsonrpc = "2.0", id = 1, method = "tools/call",
            @params = new { name = tool, arguments = args },
        });
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        return await resp.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static string TextOf(JsonElement rpc) =>
        rpc.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString()!;

    private static bool IsError(JsonElement rpc) =>
        rpc.GetProperty("result").TryGetProperty("isError", out var e) && e.GetBoolean();

    [Fact]
    public async Task MCP_projects_update_groupId_ФлагВключён_Отказ_ВыключенЧужая400Аналог()
    {
        var sphere = await CreateSphereAsync(_client, "для mcp");
        var foreign = await CreateSphereAsync(_stranger, "чужая mcp");
        var projectId = await CreateProjectAsync(_client);
        var session = await IdOf(await _client.PostAsJsonAsync($"/api/projects/{projectId}/sessions", new { mode = "acceptEdits" }));

        // флаг выключен: чужая и несуществующая — отказ, своя — проходит
        IsError(await CallToolAsync(session, "projects_update", new { projectId, groupId = foreign })).Should().BeTrue();
        IsError(await CallToolAsync(session, "projects_update", new { projectId, groupId = "нет-такой" })).Should().BeTrue();
        IsError(await CallToolAsync(session, "projects_update", new { projectId, groupId = sphere })).Should().BeFalse();

        // флаг включён: ключ groupId — отказ даже со своей сферой и пустой строкой, остальные поля работают
        await EnableFlagAsync();
        var denied = await CallToolAsync(session, "projects_update", new { projectId, groupId = sphere });
        IsError(denied).Should().BeTrue();
        TextOf(denied).Should().Contain("Переносить проект в сферу может только человек");
        IsError(await CallToolAsync(session, "projects_update", new { projectId, groupId = "" })).Should().BeTrue();
        IsError(await CallToolAsync(session, "projects_update", new { projectId, name = "переименован-" + Guid.NewGuid().ToString("N") }))
            .Should().BeFalse();
    }
}
