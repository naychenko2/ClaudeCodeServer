using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Services;

// Критерии приёмки: один гейт в TaskManager, одинаковый отказ через MCP (Deny) и REST (400).
[Trait("Category", "Integration")]
public class TasksAcceptanceGateTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly HttpClient _client;
    private readonly string _projectDir;

    public TasksAcceptanceGateTests(TestWebApplicationFactory factory)
    {
        _client = factory.CreateAuthenticatedClient();
        _projectDir = Path.Combine(factory.TempDir, "tasks_acceptance_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_projectDir);
    }

    private async Task<string> CreateSessionAsync()
    {
        var project = await _client.PostAsJsonAsync("/api/projects",
            new { name = "TasksAcceptance", rootPath = _projectDir, createDirectory = true });
        project.EnsureSuccessStatusCode();
        var projectId = JsonSerializer.Deserialize<JsonElement>(await project.Content.ReadAsStringAsync())
            .GetProperty("id").GetString()!;
        var session = await _client.PostAsJsonAsync($"/api/projects/{projectId}/sessions", new { mode = "acceptEdits" });
        session.EnsureSuccessStatusCode();
        return JsonSerializer.Deserialize<JsonElement>(await session.Content.ReadAsStringAsync())
            .GetProperty("id").GetString()!;
    }

    private async Task<(string Text, bool IsError)> CallToolAsync(string sessionId, string tool, object args)
    {
        var resp = await _client.PostAsync($"/mcp/tasks/{sessionId}",
            new StringContent(JsonSerializer.Serialize(new
            {
                jsonrpc = "2.0", id = 1, method = "tools/call",
                @params = new { name = tool, arguments = args },
            }), System.Text.Encoding.UTF8, "application/json"));
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = JsonSerializer.Deserialize<JsonElement>(await resp.Content.ReadAsStringAsync()).GetProperty("result");
        var isError = result.TryGetProperty("isError", out var e) && e.GetBoolean();
        return (result.GetProperty("content").EnumerateArray().First().GetProperty("text").GetString()!, isError);
    }

    private async Task<string> CreateTaskWithThreeCriteriaAsync(string sessionId)
    {
        var (text, isError) = await CallToolAsync(sessionId, "tasks_create", new
        {
            title = "С критериями",
            acceptance = new[] { new { text = "первый" }, new { text = "второй" }, new { text = "третий" } },
        });
        isError.Should().BeFalse(text);
        return JsonSerializer.Deserialize<JsonElement>(text).GetProperty("id").GetString()!;
    }

    [Fact]
    public async Task Complete_БезСвидетельств_DenyСПеречнем_ИЗаТемЗакрываетсяСТремя()
    {
        var sessionId = await CreateSessionAsync();
        var id = await CreateTaskWithThreeCriteriaAsync(sessionId);

        var (denyText, isError) = await CallToolAsync(sessionId, "tasks_complete", new { id });
        isError.Should().BeTrue();
        denyText.Should().Contain("c1").And.Contain("c2").And.Contain("c3");

        var (okText, okErr) = await CallToolAsync(sessionId, "tasks_complete", new
        {
            id,
            evidence = new[]
            {
                new { criterionId = "c1", how = "тест", result = "зелёный" },
                new { criterionId = "c2", how = "тест", result = "зелёный" },
                new { criterionId = "c3", how = "тест", result = "зелёный" },
            },
        });
        okErr.Should().BeFalse(okText);
        JsonSerializer.Deserialize<JsonElement>(okText).GetProperty("evidence").GetArrayLength().Should().Be(3);
    }

    [Fact]
    public async Task Rest_ЗакрытиеБезСвидетельств_400СТемЖеПеречнем()
    {
        var sessionId = await CreateSessionAsync();
        var id = await CreateTaskWithThreeCriteriaAsync(sessionId);

        var resp = await _client.PutAsJsonAsync($"/api/tasks/{id}", new { status = "done" });

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await resp.Content.ReadAsStringAsync();
        body.Should().Contain("c1").And.Contain("c2").And.Contain("c3");

        var ok = await _client.PutAsJsonAsync($"/api/tasks/{id}", new
        {
            status = "done",
            evidence = new[]
            {
                new { criterionId = "c1", how = "h", result = "r" },
                new { criterionId = "c2", how = "h", result = "r" },
                new { criterionId = "c3", how = "h", result = "r" },
            },
        });
        ok.StatusCode.Should().Be(HttpStatusCode.OK, await ok.Content.ReadAsStringAsync());
    }
}
