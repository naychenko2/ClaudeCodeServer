using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Controllers;

// HTTP-слой гигиены карты проекта: авторизация, чужой проект, форма ответа.
// Сам разбор покрыт юнит-тестами ProjectMapScannerTests — здесь только контракт эндпоинта.
public class ProjectMapControllerTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly TestWebApplicationFactory _factory;
    private readonly HttpClient _client;
    private readonly string _tempDir;

    public ProjectMapControllerTests(TestWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateAuthenticatedClient();
        _tempDir = Path.Combine(factory.TempDir, "map_hygiene_tests");
        Directory.CreateDirectory(_tempDir);
    }

    private async Task<string> SetupProjectAsync()
    {
        var dir = Path.Combine(_tempDir, "proj_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "CLAUDE.md"),
            "# Карта\n\n## Раздел\n\n[нет такого](backend/Models/FeatureFlag.cs)\n");

        var response = await _client.PostAsJsonAsync("/api/projects", new { name = "MapProject", rootPath = dir });
        response.EnsureSuccessStatusCode();
        var json = JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync());
        return json.GetProperty("id").GetString()!;
    }

    [Fact]
    public async Task Скан_ОтдаётФактыКарты()
    {
        var id = await SetupProjectAsync();

        var response = await _client.GetAsync($"/api/projects/{id}/map-hygiene/scan");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync());
        body.GetProperty("exists").GetBoolean().Should().BeTrue();
        body.GetProperty("path").GetString().Should().Be("CLAUDE.md");
        body.GetProperty("deadLinks").EnumerateArray().Single()
            .GetProperty("target").GetString().Should().Be("backend/Models/FeatureFlag.cs");
    }

    // Кейс 9 плана: несуществующий проект — 404
    [Fact]
    public async Task Скан_НесуществующийПроект_Возвращает404()
    {
        var response = await _client.GetAsync($"/api/projects/{Guid.NewGuid()}/map-hygiene/scan");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // Главный инвариант эндпоинта — изоляция по владельцу, и проверять его надо на РЕАЛЬНО
    // существующем чужом проекте: по выдуманному id срабатывает ветка «проекта нет»,
    // а проверка владельца при этом может отсутствовать вовсе. Чужой владелец иначе получил
    // бы карту чужого репозитория — имена секций, пути файлов, вложенные карты
    [Fact]
    public async Task Скан_ПроектЧужогоВладельца_Возвращает404()
    {
        var id = await SetupProjectAsync();
        using var stranger = _factory.CreateAuthenticatedClient(
            TestWebApplicationFactory.SecondUsername, TestWebApplicationFactory.SecondPassword);

        var response = await stranger.GetAsync($"/api/projects/{id}/map-hygiene/scan");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Скан_БезАвторизации_Возвращает401()
    {
        var id = await SetupProjectAsync();
        using var anonymous = _factory.CreateClient();

        var response = await anonymous.GetAsync($"/api/projects/{id}/map-hygiene/scan");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }
}
