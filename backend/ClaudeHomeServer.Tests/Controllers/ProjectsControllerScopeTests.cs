using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace ClaudeHomeServer.Tests.Controllers;

// S0-4a: ProjectsController на [ProjectScope(Key = "id")] — поведение «только владелец» прежнее:
// свой проект — 200, чужой — 404 (не раскрываем существование), и чужой запрос ничего не меняет.
[Trait("Category", "Host")]
public class ProjectsControllerScopeTests(TestWebApplicationFactory factory)
    : IClassFixture<TestWebApplicationFactory>, IDisposable
{
    private readonly HttpClient _client = factory.CreateAuthenticatedClient();
    private readonly HttpClient _stranger = factory.CreateAuthenticatedClient(
        TestWebApplicationFactory.SecondUsername, TestWebApplicationFactory.SecondPassword);
    private readonly List<string> _dirs = [];

    [Fact]
    public async Task Чтение_ПроектаИПромпта_СвойЭто200_ЧужойЭто404()
    {
        var id = await CreateProjectAsync(_client, "Скоуп чтение");

        (await _client.GetAsync($"/api/projects/{id}")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await _client.GetAsync($"/api/projects/{id}/effective-prompt")).StatusCode.Should().Be(HttpStatusCode.OK);

        (await _stranger.GetAsync($"/api/projects/{id}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await _stranger.GetAsync($"/api/projects/{id}/effective-prompt")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task ПамятьКоманды_ЗаписьИЧтение_СвойЭто200_ЧужойЭто404()
    {
        var id = await CreateProjectAsync(_client, "Скоуп память");

        var add = await _client.PostAsJsonAsync($"/api/projects/{id}/team-memory", new { text = "Общий факт" });
        add.StatusCode.Should().Be(HttpStatusCode.OK);

        var list = await _client.GetAsync($"/api/projects/{id}/team-memory");
        list.StatusCode.Should().Be(HttpStatusCode.OK);
        JsonSerializer.Deserialize<JsonElement>(await list.Content.ReadAsStringAsync()).GetArrayLength().Should().Be(1);

        (await _stranger.GetAsync($"/api/projects/{id}/team-memory")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await _stranger.PostAsJsonAsync($"/api/projects/{id}/team-memory", new { text = "Чужая запись" }))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Правка_КолонокДоски_ЧужойЭто404_ИДанныеНеТронуты()
    {
        var id = await CreateProjectAsync(_client, "Скоуп доска");
        var columns = new { columns = new[] { new { id = "c1", name = "Новое", category = "todo" } } };

        (await _stranger.PutAsJsonAsync($"/api/projects/{id}/board-columns", columns))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);
        (factory.Services.GetRequiredService<ProjectManager>().GetById(id)!.BoardColumns ?? [])
            .Should().NotContain(c => c.Name == "Новое");

        (await _client.PutAsJsonAsync($"/api/projects/{id}/board-columns", columns))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        factory.Services.GetRequiredService<ProjectManager>().GetById(id)!.BoardColumns
            .Should().Contain(c => c.Name == "Новое");
    }

    [Fact]
    public async Task Удаление_ЧужойЭто404_ПроектЖив_СвойЭто204()
    {
        var id = await CreateProjectAsync(_client, "Скоуп удаление");

        (await _stranger.DeleteAsync($"/api/projects/{id}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        factory.Services.GetRequiredService<ProjectManager>().GetById(id).Should().NotBeNull();

        (await _client.DeleteAsync($"/api/projects/{id}")).StatusCode.Should().Be(HttpStatusCode.NoContent);
        factory.Services.GetRequiredService<ProjectManager>().GetById(id).Should().BeNull();
    }

    public void Dispose()
    {
        foreach (var dir in _dirs) TestFs.DeleteDirectoryResilient(dir);
    }

    private async Task<string> CreateProjectAsync(HttpClient client, string name)
    {
        var dir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(),
            "ccs_pscope_" + Guid.NewGuid().ToString("N"))).FullName;
        _dirs.Add(dir);
        var resp = await client.PostAsJsonAsync("/api/projects", new { name, rootPath = dir });
        resp.EnsureSuccessStatusCode();
        return JsonSerializer.Deserialize<JsonElement>(await resp.Content.ReadAsStringAsync())
            .GetProperty("id").GetString()!;
    }
}
