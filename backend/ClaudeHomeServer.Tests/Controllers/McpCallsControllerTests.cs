using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ClaudeHomeServer.Filters;
using ClaudeHomeServer.Services.Mcp;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Controllers;

/// <summary>
/// Наблюдаемость MCP: вызовы продуктовых серверов к бэкенду обязаны попадать в статистику.
/// До этого следа на бэкенде не было вовсе — разбор жалобы «инструменты отваливаются» шёл
/// вручную по data/sessions/*/history.json, и такой разбор пришлось бы повторять каждый раз.
/// </summary>
public class McpCallsControllerTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly TestWebApplicationFactory _factory;

    public McpCallsControllerTests(TestWebApplicationFactory factory) => _factory = factory;

    private HttpRequestMessage McpRequest(string path, string tool, string sessionId = "sess-1")
    {
        var req = new HttpRequestMessage(HttpMethod.Get, path);
        req.Headers.Add(DenyOnDelegatedTurnAttribute.CallerHeader, sessionId);
        req.Headers.Add(McpCallLogMiddleware.ToolHeader, tool);
        return req;
    }

    [Fact]
    public async Task ВызовMcp_ПопадаетВСтатистику_СОтказами()
    {
        var client = _factory.CreateAuthenticatedClient();

        // Несуществующий проект → 404 от контроллера: для нас это отказ инструмента
        var failing = McpRequest("/api/projects/нет-такого/tasks", "tasks_list");
        var failResponse = await client.SendAsync(failing);
        failResponse.StatusCode.Should().Be(HttpStatusCode.NotFound);

        var stats = await client.GetFromJsonAsync<JsonElement>("/api/mcp/calls");

        var tools = stats.GetProperty("tools").EnumerateArray()
            .Select(t => t.GetProperty("tool").GetString()).ToList();
        tools.Should().Contain("tasks_list", "вызов инструмента обязан попасть в счётчики");

        var entry = stats.GetProperty("tools").EnumerateArray()
            .First(t => t.GetProperty("tool").GetString() == "tasks_list");
        entry.GetProperty("calls").GetInt64().Should().BeGreaterThan(0);
        entry.GetProperty("failures").GetInt64().Should().BeGreaterThan(0, "404 — отказ");

        var failures = stats.GetProperty("recentFailures").EnumerateArray().ToList();
        failures.Should().NotBeEmpty();
        failures.Should().Contain(f => f.GetProperty("tool").GetString() == "tasks_list"
            && f.GetProperty("statusCode").GetInt32() == 404
            && f.GetProperty("sessionId").GetString() == "sess-1",
            "в сбое видно и инструмент, и код, и чат — по ним ищут причину");
    }

    [Fact]
    public async Task ОбычныйЗапросФронта_ВСтатистикуНеПопадает()
    {
        var client = _factory.CreateAuthenticatedClient();

        // Тот же путь, но без заголовков MCP — это фронт, его учитывать незачем
        await client.GetAsync("/api/projects/нет-такого-2/tasks");

        var stats = await client.GetFromJsonAsync<JsonElement>("/api/mcp/calls");

        stats.GetProperty("recentFailures").EnumerateArray()
            .Should().NotContain(f => f.GetProperty("path").GetString()!.Contains("нет-такого-2"));
    }

    [Fact]
    public async Task Статистика_ТолькоАдмину()
    {
        var user = _factory.CreateAuthenticatedClient(
            TestWebApplicationFactory.SecondUsername, TestWebApplicationFactory.SecondPassword);

        var response = await user.GetAsync("/api/mcp/calls");

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden,
            "в выдаче — инструменты и чаты всех владельцев");
    }
}
