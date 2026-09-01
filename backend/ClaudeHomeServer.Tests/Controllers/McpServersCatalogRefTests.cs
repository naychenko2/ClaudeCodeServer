using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Mcp;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace ClaudeHomeServer.Tests.Controllers;

// Волна 1 «Каталога MCP-серверов», шаг 4: CatalogRef в запросе создания и DTO,
// каталожная запись заводится выключенной, PUT её не выключает и не теряет,
// AllowOutsideProjects начинает переноситься, стор переживает неизвестные поля.
public class McpServersCatalogRefTests(TestWebApplicationFactory factory)
    : IClassFixture<TestWebApplicationFactory>
{
    private readonly HttpClient _client = factory.CreateAuthenticatedClient();

    private static string UserIdOf(TestWebApplicationFactory factory) =>
        factory.Services.GetRequiredService<UserStore>().GetFirst()!.Id;

    // Тесты класса делят один UserStore: включённый другим тестом флаг не должен
    // протекать в проверки выключенного состояния
    private static void SetFlag(TestWebApplicationFactory factory, bool enabled) =>
        factory.Services.GetRequiredService<UserStore>()
            .SetFeatureFlag(UserIdOf(factory), FeatureFlagKeys.McpCatalog, enabled).Should().BeTrue();

    private static async Task<JsonElement> CreateServerAsync(HttpClient client, object? catalogRef = null)
    {
        var body = new Dictionary<string, object?>
        {
            ["key"] = "catalog-" + Guid.NewGuid().ToString("N")[..8],
            ["transport"] = "stdio",
            ["command"] = "node",
            ["args"] = new[] { "server.js" },
        };
        if (catalogRef is not null) body["catalogRef"] = catalogRef;
        var resp = await client.PostAsJsonAsync("/api/mcp/servers", body);
        resp.EnsureSuccessStatusCode();
        return JsonSerializer.Deserialize<JsonElement>(await resp.Content.ReadAsStringAsync());
    }

    // --- Создание: CatalogRef и выключенность ---

    [Fact]
    public async Task Create_сCatalogRef_выключенИПомечен()
    {
        SetFlag(factory, enabled: true);

        var created = await CreateServerAsync(_client, new
        {
            name = "io.github.owner/filesystem",
            version = "1.2.0",
            publishedAt = DateTime.Parse("2026-01-02T03:04:05Z"),
        });
        created.GetProperty("enabled").GetBoolean().Should().BeFalse();
        var catalogRef = created.GetProperty("catalogRef");
        catalogRef.GetProperty("name").GetString().Should().Be("io.github.owner/filesystem");
        catalogRef.GetProperty("version").GetString().Should().Be("1.2.0");
        // stdio-запись: импортированного адреса нет (поле сериализуется как null)
        catalogRef.GetProperty("url").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task Create_сCatalogRef_безФлага_отказ()
    {
        // Флаг по умолчанию выключен (dark launch); соседние тесты его включают — гасим явно
        SetFlag(factory, enabled: false);
        var resp = await _client.PostAsJsonAsync("/api/mcp/servers", new Dictionary<string, object?>
        {
            ["key"] = "flagged",
            ["transport"] = "stdio",
            ["command"] = "node",
            ["catalogRef"] = new { name = "io.github.owner/filesystem", version = "1.0.0" },
        });
        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // Каталожный http-URL проходит настоящий SSRF-резолв (SsrfGuard.CheckAsync в Create):
    // на машине с Proxifier резолв внешнего имени может прийти loopback-адресом (127.x.x.x),
    // и гейт честно режет его как приватный — среда, не регрессия. На CI (ubuntu) это NXDOMAIN → DnsFailed.
    [Fact]
    [Trait("Category", "Dns")]  // нужен настоящий DNS — см. remarks ReaderServiceTests
    public async Task Create_http_сCatalogRef_импортированныйUrlСовпадаетСЗаписью()
    {
        SetFlag(factory, enabled: true);

        var resp = await _client.PostAsJsonAsync("/api/mcp/servers", new Dictionary<string, object?>
        {
            ["key"] = "remote-cat",
            ["transport"] = "http",
            ["url"] = "https://api.example.com/mcp",
            ["catalogRef"] = new { name = "com.example/remote", version = "2.0.0" },
        });
        resp.EnsureSuccessStatusCode();
        var created = JsonSerializer.Deserialize<JsonElement>(await resp.Content.ReadAsStringAsync());
        created.GetProperty("enabled").GetBoolean().Should().BeFalse();
        created.GetProperty("catalogRef").GetProperty("url").GetString()
            .Should().Be("https://api.example.com/mcp");
    }

    // --- PUT: не выключает, не теряет указатель, переносит оси доступа ---

    [Fact]
    public async Task Put_безEnabled_не_выключает_каталожную_запись()
    {
        SetFlag(factory, enabled: true);
        var created = await CreateServerAsync(_client, new { name = "io.github.owner/one", version = "1.0.0" });
        var id = created.GetProperty("id").GetString()!;

        // Человек включил запись (вручную) — правка ради заголовка выключать её не должна
        var enabled = await _client.PostAsJsonAsync($"/api/mcp/servers/{id}/enable", new { enabled = true });
        enabled.EnsureSuccessStatusCode();

        var put = await _client.PutAsJsonAsync($"/api/mcp/servers/{id}", new Dictionary<string, object?>
        {
            ["label"] = "Переименованный",
        });
        put.EnsureSuccessStatusCode();
        var updated = JsonSerializer.Deserialize<JsonElement>(await put.Content.ReadAsStringAsync());
        updated.GetProperty("enabled").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task Put_сохраняетCatalogRefИПереноситAllowOutsideProjects()
    {
        SetFlag(factory, enabled: true);
        var created = await CreateServerAsync(_client, new { name = "io.github.owner/two", version = "1.0.0" });
        var id = created.GetProperty("id").GetString()!;

        var put = await _client.PutAsJsonAsync($"/api/mcp/servers/{id}", new Dictionary<string, object?>
        {
            ["label"] = "Правка",
            ["allowOutsideProjects"] = true,
            // CatalogRef в PUT не отдаётся — указатель живёт в записи
        });
        put.EnsureSuccessStatusCode();
        var updated = JsonSerializer.Deserialize<JsonElement>(await put.Content.ReadAsStringAsync());
        updated.GetProperty("allowOutsideProjects").GetBoolean().Should().BeTrue();
        updated.GetProperty("catalogRef").GetProperty("name").GetString()
            .Should().Be("io.github.owner/two");
    }

    [Fact]
    public async Task Put_ручнойЗаписи_переноситAllowOutsideProjects()
    {
        var created = await CreateServerAsync(_client);
        var id = created.GetProperty("id").GetString()!;

        var put = await _client.PutAsJsonAsync($"/api/mcp/servers/{id}", new Dictionary<string, object?>
        {
            ["allowOutsideProjects"] = true,
        });
        put.EnsureSuccessStatusCode();
        var updated = JsonSerializer.Deserialize<JsonElement>(await put.Content.ReadAsStringAsync());
        updated.GetProperty("allowOutsideProjects").GetBoolean().Should().BeTrue();
    }

    // --- Совместимость стора ---

    [Fact]
    public void Стор_неизвестноеСвойство_не_роняет_файл()
    {
        // Откат/старые клиенты кладут в mcp-servers.json поля, которых новый код не знает
        // (и наоборот) — Load обязан их пропускать, а не уносить файл в .corrupt-*.bak
        var dir = Path.Combine(Path.GetTempPath(), "ccs_tests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var path = Path.Combine(dir, "mcp-servers.json");
            File.WriteAllText(path, """
                {"u1":[{"id":"s1","OwnerId":"u1","Key":"old","futureField":123,
                "CatalogRef":{"Name":"io.github.owner/x","Version":"1.0.0","Url":null}}]}
                """);
            var config = new ConfigurationBuilder().AddInMemoryCollection(
                new Dictionary<string, string?> { ["DataPath"] = Path.Combine(dir, "projects.json") }).Build();
            var registry = new McpRegistry(config, new McpSecretStore(config));
            var list = registry.GetByOwner("u1");
            list.Should().HaveCount(1);
            list[0].CatalogRef.Should().NotBeNull();
            list[0].CatalogRef!.Name.Should().Be("io.github.owner/x");
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}
