using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Controllers;

// Права и гейт флага у эндпоинтов фона проекта (ADR-008 §7): чужой проект — 404,
// выключенный флаг — 404 на обоих POST.
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

    [Fact]
    public async Task При_выключенном_флаге_генерация_и_сброс_недоступны_владельцу()
    {
        var owner = factory.CreateAuthenticatedClient();
        var projectId = await CreateProjectAsync(owner);

        (await owner.PostAsync($"/api/projects/{projectId}/background/generate", null))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await owner.PostAsync($"/api/projects/{projectId}/background/reset", null))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);
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
