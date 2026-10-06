using System.Net.Http.Json;
using System.Text.Json;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Memory;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace ClaudeHomeServer.Tests.Controllers;

// C4 «Сфер»: инструменты sphere_memory_* сервера памяти — состав по флагу владельца, сфера из проекта
// чата, запись только персонами ЭТОЙ сферы, adopt = перемещение с полки проекта на полку сферы.
public class SphereMemoryToolsetTests : IDisposable
{
    private readonly TestWebApplicationFactory _factory = new();
    private readonly HttpClient _client;

    public SphereMemoryToolsetTests() => _client = _factory.CreateAuthenticatedClient();

    public void Dispose() => _factory.Dispose();

    private string OwnerId => _factory.Services.GetRequiredService<UserStore>()
        .FindByUsername(TestWebApplicationFactory.TestUsername)!.Id;

    private static async Task<string> IdOf(HttpResponseMessage resp)
    {
        resp.EnsureSuccessStatusCode();
        return (await resp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
    }

    private async Task EnableFlagAsync() =>
        (await _client.PutAsJsonAsync($"/api/feature-flags/{FeatureFlagKeys.Spheres}", new { enabled = true }))
            .EnsureSuccessStatusCode();

    private async Task<string> SphereAsync(string name) =>
        await IdOf(await _client.PostAsJsonAsync("/api/project-groups", new { name, color = "#D97757" }));

    private async Task<string> ProjectAsync(string? sphereId) =>
        await IdOf(await _client.PostAsJsonAsync("/api/projects",
            new { name = $"sm-{Guid.NewGuid():N}", groupId = sphereId }));

    private Persona NewPersona(PersonaScope scope, string? projectId = null, string? sphereId = null)
    {
        var p = _factory.Services.GetRequiredService<PersonaManager>().Create(
            OwnerId, "П" + Guid.NewGuid().ToString("N")[..5], null, null, null, null, null,
            PersonaScope.Global, null, null, null, false);
        p.Scope = scope;
        p.ProjectId = projectId;
        p.SphereId = sphereId;
        return p;
    }

    private async Task<JsonElement> CallAsync(string? personaId, string projectId, string tool, object args)
    {
        var resp = await _client.PostAsJsonAsync($"/mcp/memory/{personaId ?? "-"}/{projectId}", new
        {
            jsonrpc = "2.0", id = 1, method = "tools/call", @params = new { name = tool, arguments = args },
        });
        resp.EnsureSuccessStatusCode();
        return (await resp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("result");
    }

    private static bool IsError(JsonElement result) =>
        result.TryGetProperty("isError", out var e) && e.GetBoolean();

    private static string TextOf(JsonElement result) => result.GetProperty("content")[0].GetProperty("text").GetString()!;

    [Fact]
    public async Task Состав_ПоФлагуВладельца_НеПоЧленствуПроекта()
    {
        var projectId = await ProjectAsync(null);
        var url = $"/mcp/memory/-/{projectId}";
        async Task<List<string>> NamesAsync()
        {
            var resp = await _client.PostAsJsonAsync(url, new { jsonrpc = "2.0", id = 1, method = "tools/list" });
            return (await resp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("result").GetProperty("tools")
                .EnumerateArray().Select(t => t.GetProperty("name").GetString()!).ToList();
        }

        (await NamesAsync()).Should().NotContain(n => n.StartsWith("sphere_memory_"));

        await EnableFlagAsync();
        // Проект не в сфере, но флаг включён — инструменты есть, отказ выдаст сам вызов
        (await NamesAsync()).Where(n => n.StartsWith("sphere_memory_")).Should().HaveCount(5);
        var denied = await CallAsync(null, projectId, "sphere_memory_list", new { });
        IsError(denied).Should().BeTrue();
        TextOf(denied).Should().Contain("Проект не входит в сферу");
    }

    [Fact]
    public async Task Запись_ЧеловекИПерсонаСферы_Пишут_ПроектнаяИГлобальная_Отказ()
    {
        await EnableFlagAsync();
        var sphere = await SphereAsync("запись");
        var projectId = await ProjectAsync(sphere);
        var mem = _factory.Services.GetRequiredService<SphereMemoryService>();

        IsError(await CallAsync(null, projectId, "sphere_memory_remember", new { text = "от человека" })).Should().BeFalse();
        var spherePersona = NewPersona(PersonaScope.Sphere, sphereId: sphere);
        IsError(await CallAsync(spherePersona.Id, projectId, "sphere_memory_remember", new { text = "от персоны сферы" }))
            .Should().BeFalse();
        mem.List(OwnerId, sphere).Select(e => e.Text).Should().BeEquivalentTo("от человека", "от персоны сферы");

        var projectPersona = NewPersona(PersonaScope.Project, projectId: projectId);
        var globalPersona = NewPersona(PersonaScope.Global);
        foreach (var who in new[] { projectPersona, globalPersona })
        {
            var denied = await CallAsync(who.Id, projectId, "sphere_memory_remember", new { text = "чужое" });
            IsError(denied).Should().BeTrue();
            TextOf(denied).Should().Contain("только персонам ЭТОЙ сферы");
        }
        // Чтение им доступно
        IsError(await CallAsync(globalPersona.Id, projectId, "sphere_memory_list", new { })).Should().BeFalse();
        mem.List(OwnerId, sphere).Should().HaveCount(2);
    }

    [Fact]
    public async Task ЧужойSphereIdВАргументах_Игнорируется_ЗаписьУходитВСферуПроектаСессии()
    {
        await EnableFlagAsync();
        var s1 = await SphereAsync("первая");
        var s2 = await SphereAsync("вторая");
        var projectInS2 = await ProjectAsync(s2);
        var personaS2 = NewPersona(PersonaScope.Sphere, sphereId: s2);
        var mem = _factory.Services.GetRequiredService<SphereMemoryService>();

        // Персона сферы s2 и человек пытаются адресовать записью чужую сферу s1 через аргумент
        IsError(await CallAsync(personaS2.Id, projectInS2, "sphere_memory_remember",
            new { text = "от персоны", sphereId = s1 })).Should().BeFalse();
        IsError(await CallAsync(null, projectInS2, "sphere_memory_remember",
            new { text = "от человека", sphereId = s1 })).Should().BeFalse();

        mem.Count(OwnerId, s1).Should().Be(0, "sphereId из аргументов игнорируется");
        mem.List(OwnerId, s2).Select(e => e.Text).Should().BeEquivalentTo("от персоны", "от человека");
    }

    [Fact]
    public async Task ПерсонаДругойСферы_ПисатьВСферуПроектаНеМожет_Отказ()
    {
        await EnableFlagAsync();
        var s1 = await SphereAsync("первая");
        var s2 = await SphereAsync("вторая");
        var projectInS2 = await ProjectAsync(s2);
        var personaS1 = NewPersona(PersonaScope.Sphere, sphereId: s1);
        var mem = _factory.Services.GetRequiredService<SphereMemoryService>();

        var denied = await CallAsync(personaS1.Id, projectInS2, "sphere_memory_remember",
            new { text = "в чужую сферу", sphereId = s1 });

        IsError(denied).Should().BeTrue();
        mem.Count(OwnerId, s1).Should().Be(0);
        mem.Count(OwnerId, s2).Should().Be(0);
    }

    [Fact]
    public async Task Adopt_ПроектнойПерсоне_Отказ_Сферной_ПеремещаетЗапись()
    {
        await EnableFlagAsync();
        var sphere = await SphereAsync("подъём");
        var projectId = await ProjectAsync(sphere);
        var team = _factory.Services.GetRequiredService<TeamMemoryService>();
        var mem = _factory.Services.GetRequiredService<SphereMemoryService>();
        var entry = team.Add(OwnerId, projectId, "общий для сферы стек — .NET", TeamMemoryType.Convention);

        var projectPersona = NewPersona(PersonaScope.Project, projectId: projectId);
        var denied = await CallAsync(projectPersona.Id, projectId, "sphere_memory_adopt", new { entryId = entry.Id });
        IsError(denied).Should().BeTrue();
        team.List(OwnerId, projectId).Should().ContainSingle("отказ ничего не трогает");
        mem.Count(OwnerId, sphere).Should().Be(0);

        var spherePersona = NewPersona(PersonaScope.Sphere, sphereId: sphere);
        IsError(await CallAsync(spherePersona.Id, projectId, "sphere_memory_adopt", new { entryId = entry.Id }))
            .Should().BeFalse();

        team.List(OwnerId, projectId).Should().BeEmpty("adopt — перемещение: с полки проекта запись ушла");
        var adopted = mem.List(OwnerId, sphere).Should().ContainSingle().Subject;
        adopted.Text.Should().Be("общий для сферы стек — .NET");
        adopted.Type.Should().Be(TeamMemoryType.Convention);
        adopted.PromotedFrom.Should().NotBeNull();
        adopted.PromotedFrom!.ProjectId.Should().Be(projectId);
        adopted.PromotedFrom.EntryId.Should().Be(entry.Id);

        IsError(await CallAsync(spherePersona.Id, projectId, "sphere_memory_adopt", new { entryId = "нет" }))
            .Should().BeTrue();
    }
}
