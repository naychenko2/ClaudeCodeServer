using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Controllers;

// GET /api/personas/binding-targets?type=tool[&personaId=...]: каталог Tool-привязок
// с дефолтным состоянием для конкретной персоны.
public class PersonasControllerBindingTargetsTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly TestWebApplicationFactory _factory;
    private readonly HttpClient _client;
    private readonly HttpClient _stranger;

    public PersonasControllerBindingTargetsTests(TestWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateAuthenticatedClient();
        _stranger = factory.CreateAuthenticatedClient(
            TestWebApplicationFactory.SecondUsername, TestWebApplicationFactory.SecondPassword);
    }

    private async Task<string> CreatePersonaAsync(object body)
    {
        var response = await _client.PostAsJsonAsync("/api/personas", body);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
    }

    private async Task<JsonElement> GetToolTargetsAsync(string? personaId = null)
    {
        var url = "/api/personas/binding-targets?type=tool";
        if (!string.IsNullOrWhiteSpace(personaId))
            url += $"&personaId={Uri.EscapeDataString(personaId)}";
        var response = await _client.GetAsync(url);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    [Fact]
    public async Task ToolTargets_БезPersonaId_НетDefaultПолей()
    {
        var doc = await GetToolTargetsAsync();
        var items = doc.EnumerateArray().ToList();
        items.Should().NotBeEmpty();
        foreach (var item in items)
        {
            item.TryGetProperty("defaultEnabled", out _).Should().BeFalse();
            item.TryGetProperty("defaultOrigin", out _).Should().BeFalse();
        }
    }

    [Fact]
    public async Task ToolTargets_ЧужаяПерсона_404()
    {
        var id = await CreatePersonaAsync(new { name = "Моя" });
        var response = await _stranger.GetAsync($"/api/personas/binding-targets?type=tool&personaId={id}");
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task ToolTargets_НесуществующаяПерсона_404()
    {
        var response = await _client.GetAsync("/api/personas/binding-targets?type=tool&personaId=no-such-id");
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task ToolTargets_PersonaСTools_TasksOnNotesOff()
    {
        var id = await CreatePersonaAsync(new { name = "С задачами", tools = new[] { "tasks" } });
        var doc = await GetToolTargetsAsync(id);
        var items = doc.EnumerateArray().ToDictionary(i => i.GetProperty("id").GetString()!);

        items["tasks"].GetProperty("defaultEnabled").GetBoolean().Should().BeTrue();
        items["tasks"].GetProperty("defaultOrigin").GetString().Should().Be("settings");

        items["notes"].GetProperty("defaultEnabled").GetBoolean().Should().BeFalse();
        items["notes"].GetProperty("defaultOrigin").GetString().Should().Be("settings");
    }

    [Fact]
    public async Task ToolTargets_ПерсонаИсполнитель_GitOnBrowserOff()
    {
        var id = await CreatePersonaAsync(new { name = "Исполнитель", specialty = "executor" });
        var doc = await GetToolTargetsAsync(id);
        var items = doc.EnumerateArray().ToDictionary(i => i.GetProperty("id").GetString()!);

        items["git"].GetProperty("defaultEnabled").GetBoolean().Should().BeTrue();
        items["git"].GetProperty("defaultOrigin").GetString().Should().Be("role");

        items["browser"].GetProperty("defaultEnabled").GetBoolean().Should().BeFalse();
        items["browser"].GetProperty("defaultOrigin").GetString().Should().BeNull();
    }

    [Fact]
    public async Task ToolTargets_ServerКлюч_ВсегдаВключенOriginNull()
    {
        var id = await CreatePersonaAsync(new { name = "С узким Tools", tools = new[] { "tasks" } });
        var doc = await GetToolTargetsAsync(id);
        var items = doc.EnumerateArray().ToDictionary(i => i.GetProperty("id").GetString()!);

        items["personas"].GetProperty("defaultEnabled").GetBoolean().Should().BeTrue();
        items["personas"].GetProperty("defaultOrigin").GetString().Should().BeNull();
    }
}
