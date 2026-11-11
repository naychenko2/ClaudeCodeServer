using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Mcp.Http;
using ClaudeHomeServer.Services.Tasks;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace ClaudeHomeServer.Tests.Services;

// FS-E1: «ждёт человека» ставит только MCP tasks_update (awaitingHuman: {ask}), null снимает;
// Since штампует сервер; ask 1–120 символов. REST-тела POST/PUT поле игнорируют ([JsonIgnore]).
// Мутации:
// • убрать вызов SetAwaitingHuman в case "tasks_update" — тест «Ask_СтавитОжидание» краснеет;
// • убрать проверку AwaitingAskError — тест «Ask121Символ» краснеет;
// • убрать CanWriteVoiceHere для эпика — тест «Эпик_СессияИсполнителя» краснеет;
// • снять [property: JsonIgnore] с запросов — REST-тесты ниже краснеют.
[Trait("Category", "Integration")]
[Trait("Category", "Host")]
public class TasksToolsetAwaitingHumanTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly TestWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public TasksToolsetAwaitingHumanTests(TestWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateAuthenticatedClient();
    }

    private TaskManager Tasks => _factory.Services.GetRequiredService<TaskManager>();
    private SessionManager Sessions => _factory.Services.GetRequiredService<SessionManager>();
    private string OwnerId => _factory.Services.GetRequiredService<UserStore>()
        .FindByUsername(TestWebApplicationFactory.TestUsername)!.Id;

    private async Task<string> CreateProjectAsync(string tag)
    {
        var dir = Path.Combine(_factory.TempDir, "mcp_await_" + tag + "_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        var project = await _client.PostAsJsonAsync("/api/projects",
            new { name = "McpAwait" + tag, rootPath = dir, createDirectory = true });
        project.EnsureSuccessStatusCode();
        return JsonSerializer.Deserialize<JsonElement>(await project.Content.ReadAsStringAsync())
            .GetProperty("id").GetString()!;
    }

    private async Task<string> CreateSessionAsync(string projectId)
    {
        var session = await _client.PostAsJsonAsync($"/api/projects/{projectId}/sessions", new { mode = "acceptEdits" });
        session.EnsureSuccessStatusCode();
        return JsonSerializer.Deserialize<JsonElement>(await session.Content.ReadAsStringAsync())
            .GetProperty("id").GetString()!;
    }

    private async Task<(string Text, bool IsError)> CallToolAsync(string sessionId, string tool, object args)
    {
        var resp = await _client.AsCaller(sessionId).PostAsync($"/mcp/tasks/{sessionId}",
            new StringContent(JsonSerializer.Serialize(new
            {
                jsonrpc = "2.0", id = 1, method = "tools/call",
                @params = new { name = tool, arguments = args },
            }), System.Text.Encoding.UTF8, "application/json"));
        resp.StatusCode.Should().Be(HttpStatusCode.OK, "MCP отвечает 200 даже на отказ — isError в теле");
        var body = JsonSerializer.Deserialize<JsonElement>(await resp.Content.ReadAsStringAsync());
        var result = body.GetProperty("result");
        var isError = result.TryGetProperty("isError", out var e) && e.GetBoolean();
        var text = result.GetProperty("content").EnumerateArray().First().GetProperty("text").GetString()!;
        return (text, isError);
    }

    private async Task<string> CreateTaskAsync(string sessionId, object args)
    {
        var (text, err) = await CallToolAsync(sessionId, "tasks_create", args);
        err.Should().BeFalse(text);
        return JsonSerializer.Deserialize<JsonElement>(text).GetProperty("id").GetString()!;
    }

    [Fact]
    public void ToolsList_TasksUpdate_СодержитAwaitingHuman()
    {
        foreach (var (label, tools) in new[] { ("project", TasksToolset.ProjectChatTools), ("personal", TasksToolset.PersonalTools) })
        {
            var props = (JsonObject)tools.Single(t => t.Name == "tasks_update").InputSchema["properties"]!;
            props.ContainsKey("awaitingHuman").Should().BeTrue($"{label}: awaitingHuman — параметр tasks_update");
        }
    }

    [Fact]
    public async Task Ask_СтавитОжидание_SinceСерверный()
    {
        var projectId = await CreateProjectAsync("set");
        var sessionId = await CreateSessionAsync(projectId);
        var taskId = await CreateTaskAsync(sessionId, new { title = "Обычная задача" });
        var before = DateTime.UtcNow;

        var (text, isError) = await CallToolAsync(sessionId, "tasks_update",
            new { id = taskId, awaitingHuman = new { ask = "  выбрать вариант  " } });
        isError.Should().BeFalse(text);

        var stored = Tasks.GetById(taskId)!;
        stored.AwaitingHumanReason.Should().Be("выбрать вариант");
        stored.AwaitingHumanSince.Should().NotBeNull();
        stored.AwaitingHumanSince!.Value.Should().BeOnOrAfter(before, "Since штампует сервер");
    }

    [Fact]
    public async Task Null_СнимаетОжидание()
    {
        var projectId = await CreateProjectAsync("clear");
        var sessionId = await CreateSessionAsync(projectId);
        var taskId = await CreateTaskAsync(sessionId, new { title = "Задача на снятие" });
        (await CallToolAsync(sessionId, "tasks_update", new { id = taskId, awaitingHuman = new { ask = "ждём" } }))
            .IsError.Should().BeFalse();

        var (text, isError) = await CallToolAsync(sessionId, "tasks_update", new { id = taskId, awaitingHuman = (object?)null });
        isError.Should().BeFalse(text);

        var stored = Tasks.GetById(taskId)!;
        stored.AwaitingHumanSince.Should().BeNull();
        stored.AwaitingHumanReason.Should().BeNull();
    }

    [Fact]
    public async Task Ask121Символ_Отказ_НичегоНеЗаписано()
    {
        var projectId = await CreateProjectAsync("long");
        var sessionId = await CreateSessionAsync(projectId);
        var taskId = await CreateTaskAsync(sessionId, new { title = "Задача лимит" });

        var (text, isError) = await CallToolAsync(sessionId, "tasks_update",
            new { id = taskId, awaitingHuman = new { ask = new string('ы', 121) } });
        isError.Should().BeTrue();
        text.Should().Contain("120");
        Tasks.GetById(taskId)!.AwaitingHumanSince.Should().BeNull();

        var (_, ok120) = await CallToolAsync(sessionId, "tasks_update",
            new { id = taskId, awaitingHuman = new { ask = new string('ы', 120) } });
        ok120.Should().BeFalse("граница 120 включительно");
    }

    [Fact]
    public async Task ПустойAsk_Отказ()
    {
        var projectId = await CreateProjectAsync("empty");
        var sessionId = await CreateSessionAsync(projectId);
        var taskId = await CreateTaskAsync(sessionId, new { title = "Задача пустой ask" });

        var (_, isError) = await CallToolAsync(sessionId, "tasks_update",
            new { id = taskId, awaitingHuman = new { ask = "   " } });
        isError.Should().BeTrue();
        Tasks.GetById(taskId)!.AwaitingHumanSince.Should().BeNull();
    }

    // Эпик: ожидание ставит только тот, кто может писать голос фичи (curator/orchestrator);
    // чат-исполнитель шага — нет. Мутация: убрать CanWriteVoiceHere для эпика — краснеет.
    [Fact]
    public async Task Эпик_СессияИсполнителя_Отказ_Curator_Можно()
    {
        var projectId = await CreateProjectAsync("epic");
        var curatorId = await CreateSessionAsync(projectId);
        var epicId = await CreateTaskAsync(curatorId, new { title = "Фича await", role = "feature" });
        var stepId = await CreateTaskAsync(curatorId, new { title = "Шаг await", role = "step", featureId = epicId });
        var execChat = await Sessions.CreateChatAsync(OwnerId, ClaudeMode.AcceptEdits, name: "exec", taskId: stepId);

        var (text, isError) = await CallToolAsync(execChat.Id, "tasks_update",
            new { id = epicId, awaitingHuman = new { ask = "ждём решения" } });
        isError.Should().BeTrue("исполнитель шага не пишет голос фичи");
        Tasks.GetById(epicId)!.AwaitingHumanSince.Should().BeNull("отказ ничего не записал");

        var (okText, okErr) = await CallToolAsync(curatorId, "tasks_update",
            new { id = epicId, awaitingHuman = new { ask = "ждём решения" } });
        okErr.Should().BeFalse(okText);
        Tasks.GetById(epicId)!.AwaitingHumanReason.Should().Be("ждём решения");
    }

    // REST-лазейка закрыта: awaitingHumanSince/Reason в теле POST и PUT в задачу не попадают.
    [Fact]
    public async Task Rest_Post_AwaitingHumanВТеле_Игнорируется()
    {
        var resp = await _client.PostAsJsonAsync("/api/tasks", new
        {
            title = "REST создание",
            awaitingHumanSince = "2026-11-10T10:00:00Z",
            awaitingHumanReason = "подмена",
        });
        resp.EnsureSuccessStatusCode();
        var id = JsonSerializer.Deserialize<JsonElement>(await resp.Content.ReadAsStringAsync())
            .GetProperty("id").GetString()!;
        var stored = Tasks.GetById(id)!;
        stored.AwaitingHumanSince.Should().BeNull();
        stored.AwaitingHumanReason.Should().BeNull();
    }

    [Fact]
    public async Task Rest_Put_AwaitingHumanВТеле_Игнорируется()
    {
        var created = await _client.PostAsJsonAsync("/api/tasks", new { title = "REST правка" });
        created.EnsureSuccessStatusCode();
        var id = JsonSerializer.Deserialize<JsonElement>(await created.Content.ReadAsStringAsync())
            .GetProperty("id").GetString()!;

        var resp = await _client.PutAsJsonAsync($"/api/tasks/{id}", new
        {
            title = "REST правка 2",
            awaitingHumanSince = "2026-11-10T10:00:00Z",
            awaitingHumanReason = "подмена",
        });
        resp.EnsureSuccessStatusCode();
        var stored = Tasks.GetById(id)!;
        stored.Title.Should().Be("REST правка 2", "остальные поля тела применились");
        stored.AwaitingHumanSince.Should().BeNull();
        stored.AwaitingHumanReason.Should().BeNull();
    }
}
