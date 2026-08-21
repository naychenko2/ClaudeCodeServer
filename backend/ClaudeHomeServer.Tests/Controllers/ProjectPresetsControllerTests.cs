using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Controllers;

// Права у эндпоинта пресета каркаса (знакомство v2, п.3): чужой проект — 404,
// неизвестный ключ — 400, повторное применение/отказ — 409.
public class ProjectPresetsControllerTests(TestWebApplicationFactory factory)
    : IClassFixture<TestWebApplicationFactory>
{
    private async Task<string> CreateProjectAsync(HttpClient client)
    {
        var dir = Path.Combine(factory.TempDir, "preset_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        var response = await client.PostAsJsonAsync("/api/projects", new { name = "Каркасный", rootPath = dir });
        response.EnsureSuccessStatusCode();
        return JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync())
            .GetProperty("id").GetString()!;
    }

    private HttpClient Owner() => factory.CreateAuthenticatedClient();

    private static async Task<string> PresetKeyOfAsync(HttpClient client, string projectId)
    {
        var project = JsonSerializer.Deserialize<JsonElement>(
            await (await client.GetAsync($"/api/projects/{projectId}")).Content.ReadAsStringAsync());
        return project.GetProperty("presetKey").GetString()!;
    }

    [Fact]
    public async Task ЧужойПроект_404()
    {
        var owner = Owner();
        var stranger = factory.CreateAuthenticatedClient(
            TestWebApplicationFactory.SecondUsername, TestWebApplicationFactory.SecondPassword);
        var projectId = await CreateProjectAsync(owner);

        (await stranger.PostAsJsonAsync($"/api/projects/{projectId}/preset", new { presetKey = "docs" }))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Theory]
    [InlineData("nope")]          // неизвестный ключ
    [InlineData("pending")]       // зарезервированное значение — не пресет и не отказ
    [InlineData("Исходники")]     // имена папок снаружи не принимаются
    public async Task НеверныйКлюч_400(string key)
    {
        var owner = Owner();
        var projectId = await CreateProjectAsync(owner);

        (await owner.PostAsJsonAsync($"/api/projects/{projectId}/preset", new { presetKey = key }))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task ПустойКлюч_400()
    {
        var owner = Owner();
        var projectId = await CreateProjectAsync(owner);

        (await owner.PostAsJsonAsync($"/api/projects/{projectId}/preset", new { presetKey = "  " }))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Применение_ОтчётКаркасНаДискеИПовтор_409()
    {
        var owner = Owner();
        var projectId = await CreateProjectAsync(owner);
        var project = JsonSerializer.Deserialize<JsonElement>(
            await (await owner.GetAsync($"/api/projects/{projectId}")).Content.ReadAsStringAsync());
        var root = project.GetProperty("rootPath").GetString()!;

        var response = await owner.PostAsJsonAsync($"/api/projects/{projectId}/preset", new { presetKey = "docs" });
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var report = JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync());
        report.GetProperty("created").EnumerateArray().Select(e => e.GetString())
            .Should().Contain(["Исходники", "CLAUDE.md", "Статус.md", ".docs", "Доска задач"]);
        report.GetProperty("skipped").GetArrayLength().Should().Be(0);

        // Каркас реально на диске (кириллица, путь из стора — как на Linux-CI)
        Directory.Exists(Path.Combine(root, "Исходники")).Should().BeTrue();
        File.Exists(Path.Combine(root, "Статус.md")).Should().BeTrue();
        File.Exists(Path.Combine(root, ".docs")).Should().BeTrue();
        (await PresetKeyOfAsync(owner, projectId)).Should().Be("docs");

        // Повтор — и применением, и отказом: PresetKey != pending
        (await owner.PostAsJsonAsync($"/api/projects/{projectId}/preset", new { presetKey = "docs" }))
            .StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await owner.PostAsJsonAsync($"/api/projects/{projectId}/preset", new { presetKey = "none" }))
            .StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Отказ_None_ПустойОтчётИПовтор_409()
    {
        var owner = Owner();
        var projectId = await CreateProjectAsync(owner);

        var response = await owner.PostAsJsonAsync($"/api/projects/{projectId}/preset", new { presetKey = "none" });
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var report = JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync());
        report.GetProperty("created").GetArrayLength().Should().Be(0);
        report.GetProperty("skipped").GetArrayLength().Should().Be(0);
        (await PresetKeyOfAsync(owner, projectId)).Should().Be("none");

        (await owner.PostAsJsonAsync($"/api/projects/{projectId}/preset", new { presetKey = "none" }))
            .StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await owner.PostAsJsonAsync($"/api/projects/{projectId}/preset", new { presetKey = "docs" }))
            .StatusCode.Should().Be(HttpStatusCode.Conflict);
    }
}
