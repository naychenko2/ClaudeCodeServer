using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Controllers;

// S0-4b: остальные контроллеры Main на [ProjectScope] / явном IProjectAccess.Resolve.
// Поведение «только владелец» прежнее: чужой получает тот же отказ, что и до перевода
// (404; у CodeGraph и «Сервисов проекта» — прежний 403), свой проект отвечает как раньше.
public class ProjectScopeRestControllersTests(TestWebApplicationFactory factory)
    : IClassFixture<TestWebApplicationFactory>, IDisposable
{
    private readonly HttpClient _client = factory.CreateAuthenticatedClient();
    private readonly HttpClient _stranger = factory.CreateAuthenticatedClient(
        TestWebApplicationFactory.SecondUsername, TestWebApplicationFactory.SecondPassword);
    private readonly List<string> _dirs = [];

    [Theory]
    [InlineData("docs")]
    [InlineData("git/status")]
    [InlineData("knowledge")]
    [InlineData("sessions")]
    [InlineData("subjects")]
    [InlineData("subjects/run")]
    [InlineData("map-hygiene/scan")]
    [InlineData("dossiers")]
    [InlineData("skills")]
    [InlineData("files")]
    [InlineData("files/tree")]
    [InlineData("tasks")]
    public async Task Чтение_ЧужойЭто404_СвойНеОтказ(string path)
    {
        var id = await CreateProjectAsync(_client);

        (await _stranger.GetAsync($"/api/projects/{id}/{path}")).StatusCode.Should().Be(HttpStatusCode.NotFound);

        var own = await _client.GetAsync($"/api/projects/{id}/{path}");
        own.StatusCode.Should().NotBe(HttpStatusCode.NotFound).And.NotBe(HttpStatusCode.Forbidden);
    }

    [Theory]
    [InlineData("code-graph")]
    [InlineData("services")]
    public async Task Чтение_ЧужойПроект_ПрежнийОтказ403_СвойНеОтказ(string path)
    {
        var id = await CreateProjectAsync(_client);

        (await _stranger.GetAsync($"/api/projects/{id}/{path}")).StatusCode.Should().Be(HttpStatusCode.Forbidden);

        // 404 у своего проекта — штатное «графа ещё нет», поэтому проверяем только, что не 403
        var own = await _client.GetAsync($"/api/projects/{id}/{path}");
        own.StatusCode.Should().NotBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Запись_ЧужойЭто404_ПроектНеТронут()
    {
        var id = await CreateProjectAsync(_client);

        (await _stranger.PostAsJsonAsync($"/api/projects/{id}/files/create", new { path = "x.txt", content = "чужое" }))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);
        File.Exists(Path.Combine(_dirs[^1], "x.txt")).Should().BeFalse();

        (await _stranger.PostAsync($"/api/projects/{id}/background/reset", null))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await _stranger.PostAsJsonAsync($"/api/projects/{id}/tasks", new { title = "Чужая задача" }))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);

        (await _client.PostAsJsonAsync($"/api/projects/{id}/files/create", new { path = "x.txt", content = "своё" }))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        File.Exists(Path.Combine(_dirs[^1], "x.txt")).Should().BeTrue();
    }

    public void Dispose()
    {
        foreach (var dir in _dirs) TestFs.DeleteDirectoryResilient(dir);
    }

    private async Task<string> CreateProjectAsync(HttpClient client)
    {
        var dir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(),
            "ccs_pscope_rest_" + Guid.NewGuid().ToString("N"))).FullName;
        _dirs.Add(dir);
        var resp = await client.PostAsJsonAsync("/api/projects", new { name = "Скоуп REST " + _dirs.Count, rootPath = dir });
        resp.EnsureSuccessStatusCode();
        return JsonSerializer.Deserialize<JsonElement>(await resp.Content.ReadAsStringAsync())
            .GetProperty("id").GetString()!;
    }
}
