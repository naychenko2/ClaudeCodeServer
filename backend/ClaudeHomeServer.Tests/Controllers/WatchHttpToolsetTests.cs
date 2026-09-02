using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Mcp.Http;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace ClaudeHomeServer.Tests.Controllers;

/// <summary>
/// Интеграционный путь http-тулсета сторожей чатов: полный цикл watch_start → watch_list →
/// watch_cancel, изоляция чужого токена и видимость тулсета в реестре/журнале вызовов.
/// Форма теста — по образцу DifyHttpOwnerIsolationTests (ADR-012, волна 2).
/// </summary>
public class WatchHttpToolsetTests : IDisposable
{
    private readonly TestWebApplicationFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    private HttpClient Client => _factory.CreateAuthenticatedClient();

    private async Task<(string ProjectId, string SessionId, string OwnerId)> CreateProjectWithSessionAsync(
        HttpClient client)
    {
        var project = await client.PostAsJsonAsync("/api/projects", new { name = $"watch-{Guid.NewGuid():N}" });
        project.EnsureSuccessStatusCode();
        var projectId = JsonSerializer.Deserialize<JsonElement>(
            await project.Content.ReadAsStringAsync()).GetProperty("id").GetString()!;
        var session = await client.PostAsJsonAsync($"/api/projects/{projectId}/sessions",
            new { mode = "acceptEdits" });
        session.EnsureSuccessStatusCode();
        var sessionId = JsonSerializer.Deserialize<JsonElement>(
            await session.Content.ReadAsStringAsync()).GetProperty("id").GetString()!;
        var ownerId = _factory.Services.GetRequiredService<ProjectManager>()
            .GetById(projectId)!.OwnerId!;
        return (projectId, sessionId, ownerId);
    }

    private async Task<JsonElement> CallToolAsync(HttpClient client, string sessionId, string tool, object args)
    {
        var resp = await client.PostAsJsonAsync($"/mcp/watch/{sessionId}", new
        {
            jsonrpc = "2.0", id = 1, method = "tools/call",
            @params = new { name = tool, arguments = args },
        });
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        return JsonSerializer.Deserialize<JsonElement>(await resp.Content.ReadAsStringAsync());
    }

    private async Task<IReadOnlyList<string>> ListToolsAsync(HttpClient client, string sessionId)
    {
        var resp = await client.PostAsJsonAsync($"/mcp/watch/{sessionId}",
            new { jsonrpc = "2.0", id = 1, method = "tools/list" });
        resp.EnsureSuccessStatusCode();
        return JsonSerializer.Deserialize<JsonElement>(await resp.Content.ReadAsStringAsync())
            .GetProperty("result").GetProperty("tools").EnumerateArray()
            .Select(t => t.GetProperty("name").GetString()!).ToList();
    }

    private static string ResultText(JsonElement call) =>
        call.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString()!;

    /// <summary>Живой цикл: start → list → cancel → cancelled в list.</summary>
    [Fact]
    public async Task СтартСписокСнятие()
    {
        var (_, sessionId, _) = await CreateProjectWithSessionAsync(Client);

        var tools = await ListToolsAsync(Client, sessionId);
        tools.Should().BeEquivalentTo(["watch_start", "watch_list", "watch_cancel"]);

        var start = await CallToolAsync(Client, sessionId, "watch_start",
            new { name = "Релиндекс", poll_command = "py check.py", interval_seconds = 45, timeout_minutes = 30 });
        start.GetProperty("result").TryGetProperty("isError", out _).Should().BeFalse(ResultText(start));
        var created = JsonSerializer.Deserialize<JsonElement>(ResultText(start));
        var id = created.GetProperty("id").GetString()!;
        created.GetProperty("intervalSeconds").GetInt32().Should().Be(45);
        // Таймаут запуска сервер считает сам: min(60, интервал)
        created.GetProperty("timeoutMinutes").GetInt32().Should().Be(30);

        var list = await CallToolAsync(Client, sessionId, "watch_list", new { });
        var entries = JsonSerializer.Deserialize<JsonElement>(ResultText(list));
        entries.GetArrayLength().Should().Be(1);
        entries[0].GetProperty("status").GetString().Should().Be("active");

        var cancel = await CallToolAsync(Client, sessionId, "watch_cancel", new { watch_id = id });
        ResultText(cancel).Should().Contain("снят");

        var afterList = await CallToolAsync(Client, sessionId, "watch_list", new { });
        var after = JsonSerializer.Deserialize<JsonElement>(ResultText(afterList));
        after[0].GetProperty("status").GetString().Should().Be("cancelled");

        // Чужой id — честный 404-текст
        var foreign = await CallToolAsync(Client, sessionId, "watch_cancel", new { watch_id = "no-such" });
        foreign.GetProperty("result").GetProperty("isError").GetBoolean().Should().BeTrue();
        ResultText(foreign).Should().Contain("не найден");

        // Снятие уже снятого — отказ «уже завершён», статус не меняется и не перезаписывается
        var again = await CallToolAsync(Client, sessionId, "watch_cancel", new { watch_id = id });
        again.GetProperty("result").GetProperty("isError").GetBoolean().Should().BeTrue();
        ResultText(again).Should().Contain("уже завершён").And.Contain("cancelled");
        var finalList = await CallToolAsync(Client, sessionId, "watch_list", new { });
        JsonSerializer.Deserialize<JsonElement>(ResultText(finalList))[0]
            .GetProperty("status").GetString().Should().Be("cancelled");
    }

    /// <summary>Чужой токен с хвостом сессии владельца: ни состава, ни вызова — fail-closed.</summary>
    [Fact]
    public async Task ЧужойТокен_НиСоставаНиВызова()
    {
        var (_, sessionIdA, _) = await CreateProjectWithSessionAsync(Client);
        using var clientB = _factory.CreateAuthenticatedClient(
            TestWebApplicationFactory.SecondUsername, TestWebApplicationFactory.SecondPassword);

        (await ListToolsAsync(clientB, sessionIdA)).Should().BeEmpty(
            "чужая сессия — пустой состав (fail-closed)");

        var call = await CallToolAsync(clientB, sessionIdA, "watch_start",
            new { name = "Билд", poll_command = "true" });
        call.GetProperty("result").GetProperty("isError").GetBoolean().Should().BeTrue();
        ResultText(call).Should().Contain("другому владельцу");
    }

    /// <summary>Тулсет в реестре и виден в журнале вызовов (GET /api/mcp/calls).</summary>
    [Fact]
    public async Task ТулсетВРеестреИЖурналеВызовов()
    {
        var registry = _factory.Services.GetRequiredService<McpToolsetRegistry>();
        registry.Find(WatchToolset.ServerName).Should().BeOfType<WatchToolset>();

        var (_, sessionId, _) = await CreateProjectWithSessionAsync(Client);
        // Журнал пишет только запросы хода: X-Caller-Session-Id лежит в конфиге хода
        using var caller = _factory.CreateAuthenticatedClient();
        caller.DefaultRequestHeaders.Add("X-Caller-Session-Id", sessionId);
        await CallToolAsync(caller, sessionId, "watch_start",
            new { name = "Журнал", poll_command = "true", interval_seconds = 60 });

        var calls = await Client.GetFromJsonAsync<JsonElement>("/api/mcp/calls");
        var loggedTools = calls.GetProperty("tools").EnumerateArray()
            .Select(t => t.GetProperty("tool").GetString()).ToList();
        loggedTools.Should().Contain("watch_start",
            $"журнал вызовов видит тулсет; фактический состав: {string.Join(", ", loggedTools)}");
    }
}
