using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.Tasks;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace ClaudeHomeServer.Tests.Controllers;

// B4c: подбор маршрута при агентском создании эпика (MCP tasks_create, role=feature) — зеркало
// Create_Эпик_ПодборФоном из TaskWorkflowControllerTests: та же фоновая точка EpicWorkflowRecommend.
[Trait("Category", "Host")]
public class TaskWorkflowToolsetTests : IClassFixture<TaskWorkflowControllerTests.Host>
{
    private const string FastTrack = "builtin-fast-track";
    private readonly TaskWorkflowControllerTests.Host _host;
    private readonly HttpClient _owner;

    public TaskWorkflowToolsetTests(TaskWorkflowControllerTests.Host host)
    {
        _host = host;
        _owner = host.CreateAuthenticatedClient();
    }

    private TaskManager Tasks => _host.Services.GetRequiredService<TaskManager>();

    private async Task<(JsonElement Answer, string? EpicId)> CreateEpicViaToolAsync()
    {
        var project = await _owner.PostAsJsonAsync("/api/projects", new { name = $"b4c-{Guid.NewGuid():N}" });
        project.EnsureSuccessStatusCode();
        var projectId = JsonSerializer.Deserialize<JsonElement>(await project.Content.ReadAsStringAsync())
            .GetProperty("id").GetString()!;
        var session = await _owner.PostAsJsonAsync($"/api/projects/{projectId}/sessions", new { mode = "acceptEdits" });
        session.EnsureSuccessStatusCode();
        var sessionId = JsonSerializer.Deserialize<JsonElement>(await session.Content.ReadAsStringAsync())
            .GetProperty("id").GetString()!;

        var resp = await _owner.AsCaller(sessionId).PostAsJsonAsync($"/mcp/tasks/{sessionId}", new
        {
            jsonrpc = "2.0", id = 1, method = "tools/call",
            @params = new { name = "tasks_create", arguments = new { title = "фича агента", role = TaskRoles.Feature } },
        });
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var answer = JsonSerializer.Deserialize<JsonElement>(await resp.Content.ReadAsStringAsync());
        var result = answer.GetProperty("result");
        result.TryGetProperty("isError", out _).Should().BeFalse("создание не роняется подбором");
        var text = result.GetProperty("content")[0].GetProperty("text").GetString()!;
        var epicId = JsonSerializer.Deserialize<JsonElement>(text).GetProperty("id").GetString();
        return (answer, epicId);
    }

    [Fact]
    public async Task ToolCreate_Эпик_ПодборФоном_ПишетПредложение()
    {
        _host.Runner.Reset();
        _host.Runner.Text = """
            {"templateId":"builtin-fast-track","skipped":["plan"],"rationale":"малая правка"}
            """;

        var (_, epicId) = await CreateEpicViaToolAsync();

        TaskItem? epic = null;
        for (var i = 0; i < 40 && epic?.WorkflowProposal is null; i++)
        {
            epic = Tasks.GetById(epicId!);
            if (epic?.WorkflowProposal is null) await Task.Delay(50);
        }
        epic?.WorkflowProposal.Should().NotBeNull("агентское создание эпика запускает подбор фоном");
        epic!.WorkflowProposal!.TemplateId.Should().Be(FastTrack);
        epic.Workflow.Should().BeNull();
    }

    [Fact]
    public async Task ToolCreate_Эпик_СбойПодбора_ОтветЖивойМаршрутНеТронут()
    {
        _host.Runner.Reset();
        _host.Runner.Failure = new InvalidOperationException("model down");

        var (_, epicId) = await CreateEpicViaToolAsync();

        (await Task.WhenAny(_host.Runner.Entered.Task, Task.Delay(5000))).Should().Be(_host.Runner.Entered.Task,
            "подбор при агентском создании эпика не запущен");
        Tasks.GetById(epicId!).Should().NotBeNull();
        Tasks.GetById(epicId!)!.WorkflowProposal.Should().BeNull();
        Tasks.GetById(epicId!)!.Workflow.Should().BeNull();
    }
}
