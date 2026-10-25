using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ClaudeHomeServer.Services.Tasks;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace ClaudeHomeServer.Tests.Services;

// Запись предгейта в карточке задачи (MCP): ревьюер смотрит tasks_get и должен увидеть
// вердикт и коммит, к которому он привязан. Без этого предгейт для ревьюера — пустое место.
[Trait("Category", "Integration")]
public class TasksToolsetPregateTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly TestWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public TasksToolsetPregateTests(TestWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateAuthenticatedClient();
    }

    private async Task<(string ProjectId, string SessionId)> CreateProjectWithSessionAsync(string name)
    {
        var project = await _client.PostAsJsonAsync("/api/projects",
            new { name, rootPath = Path.Combine(_factory.TempDir, name + "_" + Guid.NewGuid().ToString("N")[..8]), createDirectory = true });
        project.EnsureSuccessStatusCode();
        var projectId = JsonSerializer.Deserialize<JsonElement>(await project.Content.ReadAsStringAsync())
            .GetProperty("id").GetString()!;
        var session = await _client.PostAsJsonAsync($"/api/projects/{projectId}/sessions",
            new { mode = "acceptEdits" });
        session.EnsureSuccessStatusCode();
        var sessionId = JsonSerializer.Deserialize<JsonElement>(await session.Content.ReadAsStringAsync())
            .GetProperty("id").GetString()!;
        return (projectId, sessionId);
    }

    private async Task<string> CreateProjectTaskAsync(string projectId, string id, string title)
    {
        var resp = await _client.PostAsJsonAsync($"/api/projects/{projectId}/tasks", new { id, title });
        resp.EnsureSuccessStatusCode();
        return JsonSerializer.Deserialize<JsonElement>(await resp.Content.ReadAsStringAsync())
            .GetProperty("id").GetString()!;
    }

    // Вызов MCP-инструмента через /mcp/tasks/{sessionId}: {content[0].text, isError}
    private async Task<(string Text, bool IsError)> CallToolAsync(string sessionId, string tool, object args)
    {
        var resp = await _client.PostAsync($"/mcp/tasks/{sessionId}",
            new StringContent(JsonSerializer.Serialize(new
            {
                jsonrpc = "2.0", id = 1, method = "tools/call",
                @params = new { name = tool, arguments = args },
            }), System.Text.Encoding.UTF8, "application/json"));
        resp.StatusCode.Should().Be(HttpStatusCode.OK,
            "MCP отвечает 200 даже на отказ — isError=true в теле, а не HTTP-код");
        var body = JsonSerializer.Deserialize<JsonElement>(await resp.Content.ReadAsStringAsync());
        var result = body.GetProperty("result");
        var isError = result.TryGetProperty("isError", out var e) && e.GetBoolean();
        var text = result.GetProperty("content").EnumerateArray()
            .First().GetProperty("text").GetString()!;
        return (text, isError);
    }

    [Fact]
    public async Task TasksGet_УЗадачиСПредгейтом_ОтдаётВердиктИSha()
    {
        var (projectId, sessionId) = await CreateProjectWithSessionAsync("pregate-get");
        var taskId = await CreateProjectTaskAsync(projectId,
            "aaaaaaa1-bbbb-cccc-dddd-eeeeeeeeeeee", "Шаг с предгейтом");
        var sha = "1234567890abcdef1234567890abcdef12345678";
        _factory.Services.GetRequiredService<TaskManager>()
            .RecordPregate(taskId, sha, green: true, summary: "", redKey: null);

        var (text, isError) = await CallToolAsync(sessionId, "tasks_get", new { id = taskId });

        isError.Should().BeFalse();
        var card = JsonSerializer.Deserialize<JsonElement>(text);
        var pregate = card.GetProperty("pregate");
        pregate.GetProperty("green").GetBoolean().Should().BeTrue();
        pregate.GetProperty("sha").GetString().Should().Be(sha);
    }
}
