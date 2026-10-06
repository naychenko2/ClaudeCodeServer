using ClaudeHomeServer.Models;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Controllers;

// Стык бэкенд↔фронт: REST создания/правки персоны обязан принимать sphereId у scope=Sphere.
// Раньше DTO его не содержали — персона создавалась сферной, но без сферы и не попадала в команду.
public class PersonaSphereIdApiTests : IDisposable
{
    private readonly TestWebApplicationFactory _factory = new();
    private readonly HttpClient _client;
    private readonly HttpClient _stranger;

    public PersonaSphereIdApiTests()
    {
        _client = _factory.CreateAuthenticatedClient();
        _stranger = _factory.CreateAuthenticatedClient(
            TestWebApplicationFactory.SecondUsername, TestWebApplicationFactory.SecondPassword);
    }

    public void Dispose() => _factory.Dispose();

    private static async Task<string> IdOf(HttpResponseMessage resp)
    {
        resp.EnsureSuccessStatusCode();
        return (await resp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
    }

    private static async Task<string> CreateSphereAsync(HttpClient c, string name) =>
        await IdOf(await c.PostAsJsonAsync("/api/project-groups", new { name, color = "#D97757" }));

    private async Task EnableFlagAsync() =>
        (await _client.PutAsJsonAsync($"/api/feature-flags/{FeatureFlagKeys.Spheres}", new { enabled = true }))
            .EnsureSuccessStatusCode();

    private async Task<JsonElement> TeamOfAsync(string sphereId)
    {
        var overview = await _client.GetFromJsonAsync<JsonElement>($"/api/spheres/{sphereId}/overview");
        return overview.GetProperty("team");
    }

    [Fact]
    public async Task Создание_СоСферой_СохраняетSphereIdИПопадаетВКоманду()
    {
        await EnableFlagAsync();
        var sphere = await CreateSphereAsync(_client, "своя");

        var resp = await _client.PostAsJsonAsync("/api/personas",
            new { name = "Сферная", scope = "sphere", sphereId = sphere });

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var persona = await resp.Content.ReadFromJsonAsync<JsonElement>();
        persona.GetProperty("sphereId").GetString().Should().Be(sphere);
        var team = await TeamOfAsync(sphere);
        team.EnumerateArray().Select(t => t.GetProperty("id").GetString())
            .Should().Contain(persona.GetProperty("id").GetString());
    }

    [Fact]
    public async Task Создание_БезSphereId_400()
    {
        await EnableFlagAsync();
        (await _client.PostAsJsonAsync("/api/personas", new { name = "Без сферы", scope = "sphere" }))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Создание_ЧужаяСфера_400()
    {
        await EnableFlagAsync();
        var foreign = await CreateSphereAsync(_stranger, "чужая");
        (await _client.PostAsJsonAsync("/api/personas", new { name = "Чужая", scope = "sphere", sphereId = foreign }))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Создание_ДругойScope_SphereIdОбнуляется()
    {
        await EnableFlagAsync();
        var sphere = await CreateSphereAsync(_client, "своя");
        var resp = await _client.PostAsJsonAsync("/api/personas",
            new { name = "Глобальная", scope = "global", sphereId = sphere });
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        (await resp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("sphereId").ValueKind
            .Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task Правка_ПереводВСферу_СохраняетSphereId_БезНеё_И_ЧужаяСфера_400()
    {
        await EnableFlagAsync();
        var sphere = await CreateSphereAsync(_client, "своя");
        var foreign = await CreateSphereAsync(_stranger, "чужая");
        var id = await IdOf(await _client.PostAsJsonAsync("/api/personas", new { name = "Глобальная" }));

        (await _client.PutAsJsonAsync($"/api/personas/{id}", new { scope = "sphere" }))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await _client.PutAsJsonAsync($"/api/personas/{id}", new { scope = "sphere", sphereId = foreign }))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var ok = await _client.PutAsJsonAsync($"/api/personas/{id}", new { scope = "sphere", sphereId = sphere });
        ok.StatusCode.Should().Be(HttpStatusCode.OK);
        (await ok.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("sphereId").GetString().Should().Be(sphere);
        (await TeamOfAsync(sphere)).GetArrayLength().Should().Be(1);

        var back = await _client.PutAsJsonAsync($"/api/personas/{id}", new { scope = "global" });
        (await back.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("sphereId").ValueKind
            .Should().Be(JsonValueKind.Null);
    }
}
