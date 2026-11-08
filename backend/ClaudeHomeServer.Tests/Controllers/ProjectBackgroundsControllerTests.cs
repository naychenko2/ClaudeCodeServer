using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ClaudeHomeServer.Tests.Helpers;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Access;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace ClaudeHomeServer.Tests.Controllers;

// Права у эндпоинтов фона проекта (ADR-008 §7): чужой проект — 404 на всех трёх,
// владельцу они доступны.
[Trait("Category", "Host")]
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

/// <summary>
/// Участник-редактор общего проекта видит проект (Read), но Write ему не выдан. В отличие от
/// соседей на [ProjectScope] (403 «Нет права на запись») фон отвечает 404: решение зафиксировано
/// (см. комментарий у Scoped в контроллере) — атрибут здесь нельзя, а различать 403/404
/// ради единообразия не стали, существование проекта участнику и так известно, выигрыша нет.
/// </summary>
[Trait("Category", "Host")]
public class ProjectBackgroundsEditorTests(ProjectBackgroundsEditorTests.Hosts hosts)
    : IClassFixture<ProjectBackgroundsEditorTests.Hosts>
{
    private TestWebApplicationFactory _factory => hosts;

    /// <summary>Хост на КЛАСС, а не на тест: общие проекты включаются конфигом при создании.</summary>
    public sealed class Hosts : TestWebApplicationFactory
    {
        public Hosts() => ExtraConfig[SharedProjectsGate.SwitchKey] = "true";
    }

    [Fact]
    public async Task Редактор_общего_проекта_видит_но_не_пишет_получает_404_на_запись()
    {
        var owner = _factory.CreateAuthenticatedClient();
        var editor = _factory.CreateAuthenticatedClient(
            TestWebApplicationFactory.SecondUsername, TestWebApplicationFactory.SecondPassword);
        foreach (var c in new[] { owner, editor })
            (await c.PutAsJsonAsync("/api/feature-flags/shared-projects", new { enabled = true })).EnsureSuccessStatusCode();

        var dir = Path.Combine(_factory.TempDir, "bg_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        var created = await owner.PostAsJsonAsync("/api/projects", new { name = "Общий", rootPath = dir });
        created.EnsureSuccessStatusCode();
        var projectId = JsonSerializer.Deserialize<JsonElement>(await created.Content.ReadAsStringAsync())
            .GetProperty("id").GetString()!;

        var users = _factory.Services.GetRequiredService<UserStore>();
        _factory.Services.GetRequiredService<ProjectManager>().SetMembers(projectId,
        [
            new ProjectMember
            {
                UserId = users.FindByUsername(TestWebApplicationFactory.SecondUsername)!.Id,
                Role = ProjectRole.Editor,
                AddedBy = users.FindByUsername(TestWebApplicationFactory.TestUsername)!.Id,
            },
        ]);

        // Read у редактора есть: тайл не сгенерирован, поэтому 404 «файла нет», а не отказ по праву.
        // Различить причину по статусу нельзя — на записи отказ тоже 404 (решение, не упущение).
        (await editor.PostAsync($"/api/projects/{projectId}/background/generate", null))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await editor.PostAsync($"/api/projects/{projectId}/background/reset", null))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);
        // Владелец при этом пишет: 404 редактору — именно отказ по праву, а не «проекта нет».
        (await owner.PostAsync($"/api/projects/{projectId}/background/reset", null))
            .StatusCode.Should().Be(HttpStatusCode.OK);
    }
}
