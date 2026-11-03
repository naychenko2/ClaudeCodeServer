using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Access;
using ClaudeHomeServer.Services.Tasks;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace ClaudeHomeServer.Tests.Controllers;

// S2d-7b-2: приватный чат не привязывается к фиче. Семь дверей — четыре REST (POST проектных
// задач, POST личных, PUT, execute) и три MCP (tasks_create, tasks_update, tasks_run_executor):
// приватный чат-вызыватель → 409 private_chat_mode (REST) / isError с тем же текстом (MCP);
// общий чат — как раньше. Обратное направление: PUT visibility(private) на чате, уже входящем
// в фичу, → 409 private_chat_mode. Мутация: снять DenyPrivateForHeader в REST Update и
// DenyPrivate в case "tasks_create" — краснеют RestUpdate_* и McpCreate_*.
[Trait("Category", "Host")]
public class TasksPrivateChatDoorTests : IClassFixture<TestWebApplicationFactory>
{
    private const string Code = "private_chat_mode";
    private readonly TestWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public TasksPrivateChatDoorTests(TestWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateAuthenticatedClient();
    }

    private TaskManager Tasks => _factory.Services.GetRequiredService<TaskManager>();
    private SessionManager Sessions => _factory.Services.GetRequiredService<SessionManager>();
    private string OwnerId => _factory.Services.GetRequiredService<UserStore>()
        .FindByUsername(TestWebApplicationFactory.TestUsername)!.Id;

    private async Task<Session> NewChat(bool isPrivate)
    {
        var chat = await Sessions.CreateChatAsync(OwnerId, ClaudeMode.AcceptEdits, name: "chat-" + Guid.NewGuid().ToString("N")[..6]);
        if (isPrivate) chat.PrivateToUserId = OwnerId;
        return chat;
    }

    // Чат общего проекта: у такого чата рамка хода MCP узкая (только задачи проекта чата),
    // поэтому MCP-двери приватного чата достижимы только с ProjectId.
    private async Task<Session> ProjectChat(string pid, bool isPrivate)
    {
        var chat = await NewChat(isPrivate);
        chat.ProjectId = pid;
        chat.CreatedByUserId = OwnerId;
        return chat;
    }

    private string SharedProjectId()
    {
        var pid = NewProjectId();
        _factory.Services.GetRequiredService<ProjectManager>().GetById(pid)!.Members =
            [new ProjectMember { UserId = Guid.NewGuid().ToString(), Role = ProjectRole.Editor, AddedBy = OwnerId }];
        return pid;
    }

    private TaskItem Epic(string title, string? sourceChat = null) =>
        Tasks.Create(null, OwnerId, new CreateTaskRequest(title, Role: "feature", SourceSessionId: sourceChat));

    private int TaskCount => Tasks.GetByOwner(OwnerId).Count;

    private Task<HttpResponseMessage> Rest(HttpMethod method, string url, object body, string callerChat)
    {
        var req = new HttpRequestMessage(method, url) { Content = JsonContent.Create(body) };
        req.Headers.Add("X-Caller-Session-Id", callerChat);
        return _client.SendAsync(req);
    }

    private async Task<(bool IsError, string Text)> Mcp(string chatId, string tool, object args)
    {
        var resp = await _client.PostAsync($"/mcp/tasks/{chatId}",
            new StringContent(JsonSerializer.Serialize(new
            {
                jsonrpc = "2.0", id = 1, method = "tools/call",
                @params = new { name = tool, arguments = args },
            }), Encoding.UTF8, "application/json"));
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = JsonSerializer.Deserialize<JsonElement>(await resp.Content.ReadAsStringAsync()).GetProperty("result");
        var isError = result.TryGetProperty("isError", out var e) && e.GetBoolean();
        return (isError, result.GetProperty("content")[0].GetProperty("text").GetString()!);
    }

    private static async Task AssertPrivateDenied(HttpResponseMessage resp)
    {
        resp.StatusCode.Should().Be(HttpStatusCode.Conflict);
        JsonSerializer.Deserialize<JsonElement>(await resp.Content.ReadAsStringAsync())
            .GetProperty("code").GetString().Should().Be(Code);
    }

    private string NewProjectId()
    {
        var root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "pcd_" + Guid.NewGuid().ToString("N"))).FullName;
        var users = _factory.Services.GetRequiredService<UserStore>();
        return _factory.Services.GetRequiredService<ProjectManager>()
            .Create("Проект pcd", root, OwnerId, users.GetById(OwnerId)!.Username).Id;
    }

    [Fact]
    public async Task RestProjectCreate_PrivateChat_Conflict_SharedChat_Ok()
    {
        var feature = Epic("Фича P1");
        var pid = NewProjectId();
        var before = TaskCount;

        var denied = await Rest(HttpMethod.Post, $"/api/projects/{pid}/tasks",
            new { title = "шаг", featureId = feature.Id, role = "step" }, (await NewChat(true)).Id);
        await AssertPrivateDenied(denied);
        TaskCount.Should().Be(before);

        var ok = await Rest(HttpMethod.Post, $"/api/projects/{pid}/tasks",
            new { title = "шаг", featureId = feature.Id, role = "step" }, (await NewChat(false)).Id);
        ok.StatusCode.Should().Be(HttpStatusCode.OK, "общий чат без привязок — как раньше");
    }

    [Fact]
    public async Task RestPersonalCreate_PrivateChat_Conflict_SharedChat_Ok()
    {
        var feature = Epic("Фича P2");
        var before = TaskCount;

        await AssertPrivateDenied(await Rest(HttpMethod.Post, "/api/tasks",
            new { title = "шаг", featureId = feature.Id, role = "step" }, (await NewChat(true)).Id));
        TaskCount.Should().Be(before);

        (await Rest(HttpMethod.Post, "/api/tasks",
            new { title = "шаг", featureId = feature.Id, role = "step" }, (await NewChat(false)).Id))
            .StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task RestPersonalCreate_PrivateChat_TaskOutsideFeature_Ok()
    {
        var resp = await Rest(HttpMethod.Post, "/api/tasks", new { title = "без фичи" }, (await NewChat(true)).Id);
        resp.StatusCode.Should().Be(HttpStatusCode.OK, "задача вне фичи чат к фиче не привязывает");
    }

    [Fact]
    public async Task RestUpdate_PrivateChat_Conflict_FeatureUnchanged_SharedChat_Ok()
    {
        var feature = Epic("Фича P3");
        var free = Tasks.Create(null, OwnerId, new CreateTaskRequest("свободная"));

        await AssertPrivateDenied(await Rest(HttpMethod.Put, $"/api/tasks/{free.Id}",
            new { featureId = feature.Id }, (await NewChat(true)).Id));
        Tasks.GetById(free.Id)!.FeatureId.Should().BeNull();

        (await Rest(HttpMethod.Put, $"/api/tasks/{free.Id}", new { featureId = feature.Id }, (await NewChat(false)).Id))
            .StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task RestExecute_PrivateChat_Conflict_LauncherNotRecorded()
    {
        var feature = Epic("Фича P4");
        var step = Tasks.Create(null, OwnerId, new CreateTaskRequest("шаг", FeatureId: feature.Id, Role: "step"));

        await AssertPrivateDenied(await Rest(HttpMethod.Post, $"/api/tasks/{step.Id}/execute", new { }, (await NewChat(true)).Id));
        Tasks.GetById(step.Id)!.ExecutorLaunchedBySessionId.Should().BeNull();
    }

    [Fact]
    public async Task McpCreate_PrivateChat_Deny_SharedChat_Ok()
    {
        var feature = Epic("Фича P5");
        var pid = SharedProjectId();
        var before = TaskCount;

        var (isError, text) = await Mcp((await ProjectChat(pid, true)).Id, "tasks_create",
            new { title = "шаг", featureId = feature.Id, role = "step" });
        isError.Should().BeTrue();
        text.Should().Be(FeatureChatDoorReason);
        TaskCount.Should().Be(before);

        var shared = await Mcp((await ProjectChat(pid, false)).Id, "tasks_create",
            new { title = "шаг", featureId = feature.Id, role = "step" });
        shared.IsError.Should().BeFalse("общий чат — как раньше");
    }

    [Fact]
    public async Task McpUpdate_PrivateChat_Deny_FeatureUnchanged_SharedChat_Ok()
    {
        var feature = Epic("Фича P6");
        var pid = SharedProjectId();
        var free = Tasks.Create(pid, OwnerId, new CreateTaskRequest("свободная"));

        var (isError, text) = await Mcp((await ProjectChat(pid, true)).Id, "tasks_update", new { id = free.Id, featureId = feature.Id });
        isError.Should().BeTrue();
        text.Should().Be(FeatureChatDoorReason);
        Tasks.GetById(free.Id)!.FeatureId.Should().BeNull();

        (await Mcp((await ProjectChat(pid, false)).Id, "tasks_update", new { id = free.Id, featureId = feature.Id }))
            .IsError.Should().BeFalse();
    }

    [Fact]
    public async Task McpRunExecutor_PrivateChat_Deny_LauncherNotRecorded()
    {
        var feature = Epic("Фича P7");
        var pid = SharedProjectId();
        var step = Tasks.Create(pid, OwnerId, new CreateTaskRequest("шаг", FeatureId: feature.Id, Role: "step"));

        var (isError, text) = await Mcp((await ProjectChat(pid, true)).Id, "tasks_run_executor", new { taskId = step.Id });
        isError.Should().BeTrue();
        text.Should().Be(FeatureChatDoorReason);
        Tasks.GetById(step.Id)!.ExecutorLaunchedBySessionId.Should().BeNull();
    }

    private const string FeatureChatDoorReason =
        "Приватный чат нельзя привязать к фиче: сначала сделайте чат общим либо работайте с фичей из общего чата.";

    // Обратное направление: чат, уже входящий в фичу (здесь — куратор эпика), приватным не делается.
    private async Task<(Session Chat, string ProjectId)> SharedProjectChat()
    {
        var pid = SharedProjectId();
        return (await ProjectChat(pid, false), pid);
    }

    [Fact]
    public async Task SetVisibility_ChatBoundToFeature_Conflict_StaysShared()
    {
        var (chat, _) = await SharedProjectChat();
        Epic("Фича P8", chat.Id);

        var result = Sessions.SetChatVisibility(chat.Id, OwnerId, makePrivate: true);

        result.Outcome.Should().Be(ChatVisibilityOutcome.BoundToFeature);
        chat.PrivateToUserId.Should().BeNull();
    }

    [Fact]
    public async Task SetVisibility_PutEndpoint_ChatBoundToFeature_409_PrivateChatMode()
    {
        var (chat, _) = await SharedProjectChat();
        Epic("Фича P9", chat.Id);

        var resp = await _client.PutAsJsonAsync($"/api/chats/{chat.Id}/visibility", new { @private = true });

        await AssertPrivateDenied(resp);
    }

    [Fact]
    public async Task SetVisibility_FreeChat_BecomesPrivate()
    {
        var (chat, _) = await SharedProjectChat();

        Sessions.SetChatVisibility(chat.Id, OwnerId, makePrivate: true).Outcome.Should().Be(ChatVisibilityOutcome.Ok);
        chat.PrivateToUserId.Should().Be(OwnerId);
    }

    // M1: человеческий REST не принимает id чата из тела — ни ось «источник», ни «чат-исполнитель».
    [Fact]
    public async Task RestUpdate_ExecutorLauncherFromBody_Ignored()
    {
        var privateChat = await NewChat(true);
        var task = Tasks.Create(null, OwnerId, new CreateTaskRequest("свободная"));

        var resp = await _client.PutAsJsonAsync($"/api/tasks/{task.Id}",
            new { executorLaunchedBySessionId = privateChat.Id });

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        Tasks.GetById(task.Id)!.ExecutorLaunchedBySessionId.Should().BeNull();
    }

    [Fact]
    public async Task RestCreate_SourceSessionFromBody_Ignored()
    {
        var privateChat = await NewChat(true);
        var feature = Epic("Фича M1");

        var resp = await _client.PostAsJsonAsync("/api/tasks",
            new { title = "шаг", featureId = feature.Id, role = "step", sourceSessionId = privateChat.Id });
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var id = JsonSerializer.Deserialize<JsonElement>(await resp.Content.ReadAsStringAsync()).GetProperty("id").GetString()!;
        Tasks.GetById(id)!.SourceSessionId.Should().BeNull();

        var pid = NewProjectId();
        var resp2 = await _client.PostAsJsonAsync($"/api/projects/{pid}/tasks",
            new { title = "шаг2", featureId = feature.Id, role = "step", sourceSessionId = privateChat.Id });
        resp2.StatusCode.Should().Be(HttpStatusCode.OK);
        var id2 = JsonSerializer.Deserialize<JsonElement>(await resp2.Content.ReadAsStringAsync()).GetProperty("id").GetString()!;
        Tasks.GetById(id2)!.SourceSessionId.Should().BeNull();
    }

    // M2: легаси-ось (чат создал шаг фичи через SourceSessionId; кураторство эпика — другого чата)
    // — тоже привязка, как её считает передняя дверь.
    [Fact]
    public async Task SetVisibility_ChatBoundByLegacyAxis_Conflict()
    {
        var (chat, _) = await SharedProjectChat();
        var feature = Epic("Фича M2", (await NewChat(false)).Id);
        Tasks.Create(null, OwnerId, new CreateTaskRequest("шаг", FeatureId: feature.Id, Role: "step", SourceSessionId: chat.Id));

        var resp = await _client.PutAsJsonAsync($"/api/chats/{chat.Id}/visibility", new { @private = true });

        await AssertPrivateDenied(resp);
        chat.PrivateToUserId.Should().BeNull();
    }

    // M3: при выключенной подсистеме tasks шов — «привязок нет». Хост с Subsystems:tasks:Enabled=false
    // не поднимается вовсе (TeamWaveService и другие требуют TaskManager), поэтому проверяем
    // реализацию «привязок нет» и то, что включённая подсистема даёт настоящую реализацию.
    [Fact]
    public void FeatureChatLinks_TasksDisabledSeam_NoLinks_EnabledSeam_Real()
    {
        new NoFeatureChatLinks().IsBoundToFeature("any", "chat").Should().BeFalse();
        _factory.Services.GetRequiredService<IFeatureChatLinks>().Should().BeOfType<FeatureChatLinks>();
    }
}
