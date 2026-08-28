using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace ClaudeHomeServer.Tests.Controllers;

// Гейты каталожных записей (план «Каталог MCP-серверов», волна 1, шаги 5–6):
// SSRF-фильтр пробы (приватный адрес отказ, расхождение с импортированным снимает,
// шаблонный url держит), ранний отказ при создании и подтверждение пробы с полной
// строкой запуска у stdio-записи local-владельца. Адреса в тестах — IP-литералы:
// гейт и создание не должны ходить в DNS.
public class McpServersCatalogProbeTests(TestWebApplicationFactory factory)
    : IClassFixture<TestWebApplicationFactory>
{
    private readonly HttpClient _client = factory.CreateAuthenticatedClient();

    private void EnableFlag()
    {
        var users = factory.Services.GetRequiredService<UserStore>();
        users.SetFeatureFlag(users.GetFirst()!.Id, FeatureFlagKeys.McpCatalog, true).Should().BeTrue();
    }

    private async Task<(string Id, JsonElement Body)> CreateAsync(object body)
    {
        var resp = await _client.PostAsJsonAsync("/api/mcp/servers", body);
        resp.EnsureSuccessStatusCode();
        return (JsonSerializer.Deserialize<JsonElement>(
            await resp.Content.ReadAsStringAsync()).GetProperty("id").GetString()!, default);
    }

    private static async Task<JsonElement> ProbeAsync(HttpClient client, string id, object? body = null)
    {
        var resp = body is null
            ? await client.PostAsync($"/api/mcp/servers/{id}/probe", null)
            : await client.PostAsJsonAsync($"/api/mcp/servers/{id}/probe", body);
        resp.StatusCode.Should().Be(HttpStatusCode.OK, $"поба не должна давать не-200: {await resp.Content.ReadAsStringAsync()}");
        return JsonSerializer.Deserialize<JsonElement>(await resp.Content.ReadAsStringAsync());
    }

    // --- ранний отказ при создании: приватный адрес из каталога ---

    [Fact]
    public async Task Create_каталожный_приватный_адрес_400()
    {
        EnableFlag();
        var resp = await _client.PostAsJsonAsync("/api/mcp/servers", new Dictionary<string, object?>
        {
            ["key"] = "priv-" + Guid.NewGuid().ToString("N")[..6],
            ["transport"] = "http",
            ["url"] = "https://10.0.0.5/mcp",
            ["catalogRef"] = new { name = "com.example/priv", version = "1.0.0" },
        });
        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await resp.Content.ReadAsStringAsync()).Should().Contain("частную сеть");
    }

    // --- SSRF-гейт пробы ---

    [Fact]
    public async Task Проба_каталожный_шаблонный_url_гейт_держит()
    {
        EnableFlag();
        // Публичный IP-литерал с незаполненной переменной: создание проходит (адрес публичный),
        // проба обязана отказать до любого похода в сеть
        var (id, _) = await CreateAsync(new Dictionary<string, object?>
        {
            ["key"] = "tpl-" + Guid.NewGuid().ToString("N")[..6],
            ["transport"] = "http",
            ["url"] = "https://93.184.216.34/{COMPANY}/mcp",
            ["catalogRef"] = new { name = "com.example/tpl", version = "1.0.0" },
        });
        var result = await ProbeAsync(_client, id);
        result.GetProperty("ok").GetBoolean().Should().BeFalse();
        result.GetProperty("error").GetString().Should().Contain("переменные");
    }

    [Fact]
    public async Task Проба_изменённый_владельцем_адрес_гейт_снят()
    {
        EnableFlag();
        var (id, _) = await CreateAsync(new Dictionary<string, object?>
        {
            ["key"] = "chg-" + Guid.NewGuid().ToString("N")[..6],
            ["transport"] = "http",
            ["url"] = "https://93.184.216.34/mcp",
            ["catalogRef"] = new { name = "com.example/chg", version = "1.0.0" },
        });
        // Владелец увёл адрес от импортированного (в т.ч. на http) — гейт больше не про каталог
        var put = await _client.PutAsJsonAsync($"/api/mcp/servers/{id}", new Dictionary<string, object?>
        {
            ["url"] = "http://127.0.0.1:9/mcp",
        });
        put.EnsureSuccessStatusCode();
        var result = await ProbeAsync(_client, id);
        result.GetProperty("ok").GetBoolean().Should().BeFalse();
        // Ручной адрес (loopback) — гейта нет: причина любая, кроме «из каталога…»
        result.GetProperty("error").GetString().Should().NotContain("из каталога");
    }

    // --- подтверждение пробы stdio-записи local-владельца ---

    [Fact]
    public async Task Проба_каталожная_stdio_без_подтверждения_400_со_строкой_запуска()
    {
        EnableFlag();
        var (id, _) = await CreateAsync(new Dictionary<string, object?>
        {
            ["key"] = "stdio-" + Guid.NewGuid().ToString("N")[..6],
            ["transport"] = "stdio",
            ["command"] = "npx",
            ["args"] = new[] { "-y", "some-mcp@1.0.0" },
            ["catalogRef"] = new { name = "io.github.o/some", version = "1.0.0" },
        });
        var resp = await _client.PostAsync($"/api/mcp/servers/{id}/probe", null);
        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = JsonSerializer.Deserialize<JsonElement>(await resp.Content.ReadAsStringAsync());
        body.GetProperty("requiresConfirmation").GetBoolean().Should().BeTrue();
        body.GetProperty("command").GetString().Should().Be("npx -y some-mcp@1.0.0");
    }

    [Fact]
    public async Task Проба_каталожная_stdio_с_подтверждением_идёт()
    {
        EnableFlag();
        var (id, _) = await CreateAsync(new Dictionary<string, object?>
        {
            ["key"] = "conf-" + Guid.NewGuid().ToString("N")[..6],
            ["transport"] = "stdio",
            ["command"] = "definitely-missing-command-xyz",
            ["args"] = new[] { "-y" },
            ["catalogRef"] = new { name = "io.github.o/conf", version = "1.0.0" },
        });
        var result = await ProbeAsync(_client, id, new { confirmed = true });
        // Подтверждение принято: отказа-гейта нет, проба честно пыталась и не нашла команду
        result.GetProperty("ok").GetBoolean().Should().BeFalse();
        result.GetProperty("error").GetString().Should().Contain("запустить");
    }

    [Fact]
    public async Task Проба_ручной_записи_без_тела_200_без_гейта()
    {
        // Фронт и сегодня шлёт пробу без тела: контракт обязан остаться прежним
        var (id, _) = await CreateAsync(new Dictionary<string, object?>
        {
            ["key"] = "manual-" + Guid.NewGuid().ToString("N")[..6],
            ["transport"] = "stdio",
            ["command"] = "definitely-missing-command-xyz",
        });
        var result = await ProbeAsync(_client, id);
        result.GetProperty("ok").GetBoolean().Should().BeFalse();
        result.GetProperty("error").GetString().Should().NotContain("подтвердите");
    }
}
