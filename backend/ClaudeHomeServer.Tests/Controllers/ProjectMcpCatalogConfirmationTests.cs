using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace ClaudeHomeServer.Tests.Controllers;

// Второе подтверждение (план «Каталог MCP-серверов», шаг 6, решение владельца
// от 28.08.2026): включение каталожного stdio-сервера в проект — единственный
// момент, когда чужой код впервые едет в ходы. Гейт на сервере, не в диалоге.
public class ProjectMcpCatalogConfirmationTests(TestWebApplicationFactory factory)
    : IClassFixture<TestWebApplicationFactory>
{
    private readonly HttpClient _client = factory.CreateAuthenticatedClient();

    private async Task<string> CreateProjectAsync()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ccs_tests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var resp = await _client.PostAsJsonAsync("/api/projects", new { name = "Каталог-тест", rootPath = dir });
        resp.EnsureSuccessStatusCode();
        return JsonSerializer.Deserialize<JsonElement>(
            await resp.Content.ReadAsStringAsync()).GetProperty("id").GetString()!;
    }

    private async Task<string> CreateServerAsync(object? catalogRef)
    {
        var users = factory.Services.GetRequiredService<UserStore>();
        users.SetFeatureFlag(users.GetFirst()!.Id, FeatureFlagKeys.McpCatalog, true).Should().BeTrue();
        var body = new Dictionary<string, object?>
        {
            ["key"] = "cat-" + Guid.NewGuid().ToString("N")[..6],
            ["transport"] = "stdio",
            ["command"] = "npx",
            ["args"] = new[] { "-y", "some-mcp@1.0.0" },
        };
        if (catalogRef is not null) body["catalogRef"] = catalogRef;
        var resp = await _client.PostAsJsonAsync("/api/mcp/servers", body);
        resp.EnsureSuccessStatusCode();
        return JsonSerializer.Deserialize<JsonElement>(
            await resp.Content.ReadAsStringAsync()).GetProperty("key").GetString()!;
    }

    [Fact]
    public async Task Включение_каталожной_stdio_без_подтверждения_400_с_ключами()
    {
        var projectId = await CreateProjectAsync();
        var key = await CreateServerAsync(new { name = "io.github.o/some", version = "1.0.0" });

        var resp = await _client.PutAsJsonAsync($"/api/projects/{projectId}",
            new { mcpServersOn = new[] { key } });
        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = JsonSerializer.Deserialize<JsonElement>(await resp.Content.ReadAsStringAsync());
        body.GetProperty("requiresConfirmation").GetBoolean().Should().BeTrue();
        body.GetProperty("servers")[0].GetProperty("key").GetString().Should().Be(key);
        body.GetProperty("servers")[0].GetProperty("command").GetString()
            .Should().Be("npx -y some-mcp@1.0.0");
    }

    [Fact]
    public async Task Включение_с_подтверждением_проходит()
    {
        var projectId = await CreateProjectAsync();
        var key = await CreateServerAsync(new { name = "io.github.o/ok", version = "1.0.0" });

        var resp = await _client.PutAsJsonAsync($"/api/projects/{projectId}",
            new { mcpServersOn = new[] { key }, mcpCatalogConfirmed = true });
        resp.EnsureSuccessStatusCode();
        var body = JsonSerializer.Deserialize<JsonElement>(await resp.Content.ReadAsStringAsync());
        body.GetProperty("mcpServersOn")[0].GetString().Should().Be(key);
    }

    [Fact]
    public async Task Ручная_запись_включается_без_подтверждения()
    {
        var projectId = await CreateProjectAsync();
        var key = await CreateServerAsync(catalogRef: null);

        var resp = await _client.PutAsJsonAsync($"/api/projects/{projectId}",
            new { mcpServersOn = new[] { key } });
        resp.EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task Каталожная_http_запись_включается_без_подтверждения()
    {
        // Подтверждение — про запуск на компьютере; удалённый сервер на нём ничего не запускает
        var users = factory.Services.GetRequiredService<UserStore>();
        users.SetFeatureFlag(users.GetFirst()!.Id, FeatureFlagKeys.McpCatalog, true).Should().BeTrue();
        var projectId = await CreateProjectAsync();

        var resp = await _client.PostAsJsonAsync("/api/mcp/servers", new Dictionary<string, object?>
        {
            ["key"] = "cat-http-" + Guid.NewGuid().ToString("N")[..6],
            ["transport"] = "http",
            ["url"] = "https://93.184.216.34/mcp", // публичный IP-литерал: без DNS
            ["catalogRef"] = new { name = "com.example/http", version = "1.0.0" },
        });
        resp.EnsureSuccessStatusCode();
        var key = JsonSerializer.Deserialize<JsonElement>(
            await resp.Content.ReadAsStringAsync()).GetProperty("key").GetString()!;

        var put = await _client.PutAsJsonAsync($"/api/projects/{projectId}",
            new { mcpServersOn = new[] { key } });
        put.EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task Повторное_сохранение_уже_включённого_не_спрашивает()
    {
        var projectId = await CreateProjectAsync();
        var key = await CreateServerAsync(new { name = "io.github.o/again", version = "1.0.0" });

        var first = await _client.PutAsJsonAsync($"/api/projects/{projectId}",
            new { mcpServersOn = new[] { key }, mcpCatalogConfirmed = true });
        first.EnsureSuccessStatusCode();

        // Уже включён: переименование проекта или правка других полей не требует подтверждения
        var second = await _client.PutAsJsonAsync($"/api/projects/{projectId}",
            new { name = "Переименован", mcpServersOn = new[] { key } });
        second.EnsureSuccessStatusCode();
    }
}
