using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Controllers;

// Права у эндпоинтов фона проекта (ADR-008 §7): чужой проект — 404 на всех трёх,
// владельцу они доступны.
public class ProjectBackgroundsControllerTests(TestWebApplicationFactory factory)
    : IClassFixture<TestWebApplicationFactory>
{
    private async Task<string> CreateProjectAsync(HttpClient client)
    {
        var dir = Path.Combine(factory.TempDir, "bg_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        var response = await client.PostAsJsonAsync("/api/projects", new { name = "Фоновый", rootPath = dir });
        response.EnsureSuccessStatusCode();
        return JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync())
            .GetProperty("id").GetString()!;
    }

    [Fact]
    public async Task Не_владелец_получает_404_на_всех_трёх_эндпоинтах()
    {
        var owner = factory.CreateAuthenticatedClient();
        var stranger = factory.CreateAuthenticatedClient(
            TestWebApplicationFactory.SecondUsername, TestWebApplicationFactory.SecondPassword);
        var projectId = await CreateProjectAsync(owner);

        (await stranger.PostAsync($"/api/projects/{projectId}/background/generate", null))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await stranger.PostAsync($"/api/projects/{projectId}/background/reset", null))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await stranger.GetAsync($"/api/projects/{projectId}/background/tile.svg"))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // Сброс владельцу доступен без всяких условий (модель для него не нужна — в отличие
    // от generate, который в тестовом хосте пошёл бы к живому CheapTextRunner)
    [Fact]
    public async Task Сброс_к_стандартному_фону_доступен_владельцу()
    {
        var owner = factory.CreateAuthenticatedClient();
        var projectId = await CreateProjectAsync(owner);

        var response = await owner.PostAsync($"/api/projects/{projectId}/background/reset", null);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync())
            .GetProperty("kind").GetString().Should().Be("standard");
    }

    [Fact]
    public async Task Тайл_несгенерированного_фона_отдаёт_404()
    {
        var owner = factory.CreateAuthenticatedClient();
        var projectId = await CreateProjectAsync(owner);

        (await owner.GetAsync($"/api/projects/{projectId}/background/tile.svg"))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}
