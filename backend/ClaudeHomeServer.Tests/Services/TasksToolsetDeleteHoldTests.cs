using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Tasks;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace ClaudeHomeServer.Tests.Services;

// W4-HOLD-N (хвост R-N п.3, критерий c3): дверь MCP tasks_delete обходила страховку
// возврата замороженных сроков — удаляя эпик на паузе, она оставляла чаты его шагов
// «вечными». Здесь — связка на уровне тулсета: tasks_delete зовёт
// RestoreFrozenChatsBeforeDelete до tasks.Delete. Мутация: убрать вызов в case
// "tasks_delete" — последний абзац краснеет (чат остаётся без срока).
[Trait("Category", "Integration")]
public class TasksToolsetDeleteHoldTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly TestWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public TasksToolsetDeleteHoldTests(TestWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateAuthenticatedClient();
    }

    private TaskManager Tasks => _factory.Services.GetRequiredService<TaskManager>();
    private SessionManager Sessions => _factory.Services.GetRequiredService<SessionManager>();
    private string OwnerId => _factory.Services.GetRequiredService<UserStore>()
        .FindByUsername(TestWebApplicationFactory.TestUsername)!.Id;

    [Fact]
    public async Task TasksDelete_Mcp_EpicOnPause_ReturnsFrozenExpiryToItsStep()
    {
        var dir = Path.Combine(_factory.TempDir, "mcp_del_hold_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        var project = await _client.PostAsJsonAsync("/api/projects",
            new { name = "McpDelHold", rootPath = dir, createDirectory = true });
        project.EnsureSuccessStatusCode();
        var projectId = JsonSerializer.Deserialize<JsonElement>(await project.Content.ReadAsStringAsync())
            .GetProperty("id").GetString()!;
        var session = await _client.PostAsJsonAsync($"/api/projects/{projectId}/sessions",
            new { mode = "acceptEdits" });
        session.EnsureSuccessStatusCode();
        var sessionId = JsonSerializer.Deserialize<JsonElement>(await session.Content.ReadAsStringAsync())
            .GetProperty("id").GetString()!;

        var epic = Tasks.Create(projectId, OwnerId, new CreateTaskRequest("Фича на паузе", Role: "feature"));
        var step = Tasks.Create(projectId, OwnerId, new CreateTaskRequest("Шаг на паузе", Role: "step",
            FeatureId: epic.Id, Status: TaskItemStatus.InProgress));
        var chat = await Sessions.CreateChatAsync(OwnerId, ClaudeMode.AcceptEdits, name: "exec",
            taskId: step.Id);
        Tasks.MarkClaudeStarted(step.Id, chat.Id, DateTime.UtcNow);
        Sessions.SetExpiry(chat.Id, 10);

        var set = await _client.PostAsJsonAsync($"/api/tasks/features/{epic.Id}/hold", new { reason = "manual" });
        set.StatusCode.Should().Be(HttpStatusCode.OK);
        Sessions.GetById(chat.Id)!.ExpiresAfterMinutes.Should().BeNull("пауза заморозила срок чата");

        var resp = await _client.PostAsync($"/mcp/tasks/{sessionId}",
            new StringContent(JsonSerializer.Serialize(new
            {
                jsonrpc = "2.0", id = 1, method = "tools/call",
                @params = new { name = "tasks_delete", arguments = new { id = epic.Id } },
            }), System.Text.Encoding.UTF8, "application/json"));
        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        Sessions.GetById(chat.Id)!.ExpiresAfterMinutes.Should().Be(10,
            "MCP-удаление эпика на паузе возвращает срок его шагу — чат не остаётся вечным");
    }
}
