using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Tasks;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace ClaudeHomeServer.Tests.Controllers;

// S2 (фича chat-feature-focus): гард привязки чата к фиче на дверях REST и MCP. Правило —
// FeatureChatBinding (S1); здесь проверяется, что каждая дверь его зовёт и отказывает одним
// текстом. Мутация: убрать вызов FeatureChatDoor.DenyReason в case "tasks_create" тулсета —
// краснеют MCP-тесты создания и сравнение текстов.
[Trait("Category", "Integration")]
public class TasksFeatureChatBindingDoorTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly TestWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public TasksFeatureChatBindingDoorTests(TestWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateAuthenticatedClient();
    }

    private TaskManager Tasks => _factory.Services.GetRequiredService<TaskManager>();
    private SessionManager Sessions => _factory.Services.GetRequiredService<SessionManager>();
    private string OwnerId => _factory.Services.GetRequiredService<UserStore>()
        .FindByUsername(TestWebApplicationFactory.TestUsername)!.Id;

    private async Task<Session> NewChat() =>
        await Sessions.CreateChatAsync(OwnerId, ClaudeMode.AcceptEdits, name: "chat-" + Guid.NewGuid().ToString("N")[..6]);

    private TaskItem Epic(string title, string? sourceChat = null) =>
        Tasks.Create(null, OwnerId, new CreateTaskRequest(title, Role: "feature", SourceSessionId: sourceChat));

    private int TaskCount => Tasks.GetByOwner(OwnerId).Count;

    // REST: заголовок вызывающего чата необязателен — без него это кнопка в UI.
    private Task<HttpResponseMessage> Rest(HttpMethod method, string url, object body, string? callerChat)
    {
        var req = new HttpRequestMessage(method, url) { Content = JsonContent.Create(body) };
        if (callerChat is not null) req.Headers.Add("X-Caller-Session-Id", callerChat);
        return _client.SendAsync(req);
    }

    private static async Task<(bool IsError, string Text)> ParseMcp(HttpResponseMessage resp)
    {
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = JsonSerializer.Deserialize<JsonElement>(await resp.Content.ReadAsStringAsync()).GetProperty("result");
        var isError = result.TryGetProperty("isError", out var e) && e.GetBoolean();
        return (isError, result.GetProperty("content")[0].GetProperty("text").GetString()!);
    }

    private async Task<(bool IsError, string Text)> Mcp(string chatId, string tool, object args) =>
        await ParseMcp(await _client.PostAsync($"/mcp/tasks/{chatId}",
            new StringContent(JsonSerializer.Serialize(new
            {
                jsonrpc = "2.0", id = 1, method = "tools/call",
                @params = new { name = tool, arguments = args },
            }), Encoding.UTF8, "application/json")));

    [Fact]
    public async Task RestCreate_ChatOfFeatureA_CreatesTaskOfB_Conflict_CountUnchanged()
    {
        var chat = await NewChat();
        Epic("Фича A", chat.Id);
        var b = Epic("Фича B");
        var before = TaskCount;

        var resp = await Rest(HttpMethod.Post, "/api/tasks", new { title = "шаг B", featureId = b.Id, role = "step" }, chat.Id);

        resp.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var body = JsonSerializer.Deserialize<JsonElement>(await resp.Content.ReadAsStringAsync());
        body.GetProperty("code").GetString().Should().Be("chat_feature_bound");
        body.GetProperty("error").GetString().Should().Contain("Фича A");
        TaskCount.Should().Be(before);
    }

    [Fact]
    public async Task RestCreate_NewChatCreatesA_ThenB_SecondDenied()
    {
        var chat = await NewChat();
        var a = Epic("Фича A2");
        var b = Epic("Фича B2");

        var first = await Rest(HttpMethod.Post, "/api/tasks",
            new { title = "шаг A", featureId = a.Id, role = "step", sourceSessionId = chat.Id }, chat.Id);
        first.StatusCode.Should().Be(HttpStatusCode.OK, "свободный чат привязывается созданием первой задачи");
        // привязка чата к A — по задаче с SourceSessionId; REST её берёт из тела, фиксируем явно
        Tasks.Create(null, OwnerId, new CreateTaskRequest("шаг A (из чата)", FeatureId: a.Id, Role: "step", SourceSessionId: chat.Id));

        var second = await Rest(HttpMethod.Post, "/api/tasks", new { title = "шаг B", featureId = b.Id, role = "step" }, chat.Id);
        second.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task RestCreate_LegacyChatWithAAndB_CreatesA_Ok()
    {
        var chat = await NewChat();
        var a = Epic("Фича A3", chat.Id);
        Epic("Фича B3", chat.Id);

        var resp = await Rest(HttpMethod.Post, "/api/tasks", new { title = "шаг A", featureId = a.Id, role = "step" }, chat.Id);

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task RestCreate_WithoutCallerHeader_Ok()
    {
        var chat = await NewChat();
        Epic("Фича A4", chat.Id);
        var b = Epic("Фича B4");

        var resp = await Rest(HttpMethod.Post, "/api/tasks", new { title = "шаг B", featureId = b.Id, role = "step" }, callerChat: null);

        resp.StatusCode.Should().Be(HttpStatusCode.OK, "кнопка в UI — вызывающего чата нет, гард пропускает");
    }

    [Fact]
    public async Task RestUpdate_ChangeFeatureId_FromBoundChat_Conflict_FeatureUnchanged()
    {
        var chat = await NewChat();
        Epic("Фича A5", chat.Id);
        var b = Epic("Фича B5");
        var free = Tasks.Create(null, OwnerId, new CreateTaskRequest("свободная"));

        var resp = await Rest(HttpMethod.Put, $"/api/tasks/{free.Id}", new { featureId = b.Id }, chat.Id);

        resp.StatusCode.Should().Be(HttpStatusCode.Conflict);
        Tasks.GetById(free.Id)!.FeatureId.Should().BeNull();
    }

    [Fact]
    public async Task RestExecute_TaskOfB_FromChatA_Conflict_LauncherNotRecorded()
    {
        var chat = await NewChat();
        Epic("Фича A6", chat.Id);
        var b = Epic("Фича B6");
        var step = Tasks.Create(null, OwnerId, new CreateTaskRequest("шаг B", FeatureId: b.Id, Role: "step"));

        var resp = await Rest(HttpMethod.Post, $"/api/tasks/{step.Id}/execute", new { }, chat.Id);

        resp.StatusCode.Should().Be(HttpStatusCode.Conflict);
        JsonSerializer.Deserialize<JsonElement>(await resp.Content.ReadAsStringAsync())
            .GetProperty("code").GetString().Should().Be("chat_feature_bound");
        Tasks.GetById(step.Id)!.ExecutorLaunchedBySessionId.Should().BeNull();
    }

    [Fact]
    public async Task McpCreate_ChatOfFeatureA_CreatesTaskOfB_Deny_CountUnchanged_TextEqualsRest()
    {
        var chat = await NewChat();
        Epic("Фича A7", chat.Id);
        var b = Epic("Фича B7");
        var before = TaskCount;

        var (isError, text) = await Mcp(chat.Id, "tasks_create", new { title = "шаг B", featureId = b.Id, role = "step" });

        isError.Should().BeTrue();
        TaskCount.Should().Be(before);
        var rest = await Rest(HttpMethod.Post, "/api/tasks", new { title = "шаг B", featureId = b.Id, role = "step" }, chat.Id);
        var restText = JsonSerializer.Deserialize<JsonElement>(await rest.Content.ReadAsStringAsync())
            .GetProperty("error").GetString();
        text.Should().Be(restText, "текст отказа REST и MCP побайтно один");
    }

    [Fact]
    public async Task RestCreate_ChatOfFeatureA_CreatesEpic_Conflict_CountUnchanged()
    {
        var chat = await NewChat();
        Epic("Фича A9", chat.Id);
        var before = TaskCount;

        var resp = await Rest(HttpMethod.Post, "/api/tasks", new { title = "Фича B9", role = "feature" }, chat.Id);

        resp.StatusCode.Should().Be(HttpStatusCode.Conflict);
        JsonSerializer.Deserialize<JsonElement>(await resp.Content.ReadAsStringAsync())
            .GetProperty("code").GetString().Should().Be("chat_feature_bound");
        TaskCount.Should().Be(before);
    }

    [Fact]
    public async Task RestCreate_NewChat_CreatesEpic_Ok()
    {
        var chat = await NewChat();

        var resp = await Rest(HttpMethod.Post, "/api/tasks", new { title = "Фича C9", role = "feature" }, chat.Id);

        resp.StatusCode.Should().Be(HttpStatusCode.OK, "свободный чат вправе завести эпик");
    }

    [Fact]
    public async Task RestCreate_LegacyChatWithAAndB_CreatesEpic_Conflict()
    {
        var chat = await NewChat();
        Epic("Фича A10", chat.Id);
        Epic("Фича B10", chat.Id);
        var before = TaskCount;

        var resp = await Rest(HttpMethod.Post, "/api/tasks", new { title = "Фича C10", role = "feature" }, chat.Id);

        resp.StatusCode.Should().Be(HttpStatusCode.Conflict, "легаси-чат привязан к A и B, третья фича — чужая");
        TaskCount.Should().Be(before);
    }

    [Fact]
    public async Task McpCreate_ChatOfFeatureA_CreatesEpic_Deny_CountUnchanged_TextEqualsRest()
    {
        var chat = await NewChat();
        Epic("Фича A11", chat.Id);
        var before = TaskCount;

        var (isError, text) = await Mcp(chat.Id, "tasks_create", new { title = "Фича B11", role = "feature" });

        isError.Should().BeTrue();
        TaskCount.Should().Be(before);
        var rest = await Rest(HttpMethod.Post, "/api/tasks", new { title = "Фича B11", role = "feature" }, chat.Id);
        var restText = JsonSerializer.Deserialize<JsonElement>(await rest.Content.ReadAsStringAsync())
            .GetProperty("error").GetString();
        text.Should().Be(restText, "текст отказа REST и MCP побайтно один");
    }

    [Fact]
    public async Task McpCreate_NewChat_CreatesEpic_Ok()
    {
        var chat = await NewChat();

        var (isError, _) = await Mcp(chat.Id, "tasks_create", new { title = "Фича C11", role = "feature" });

        isError.Should().BeFalse();
    }

    [Fact]
    public async Task McpCreate_LegacyChatWithAAndB_CreatesEpic_Deny()
    {
        var chat = await NewChat();
        Epic("Фича A12", chat.Id);
        Epic("Фича B12", chat.Id);
        var before = TaskCount;

        var (isError, _) = await Mcp(chat.Id, "tasks_create", new { title = "Фича C12", role = "feature" });

        isError.Should().BeTrue();
        TaskCount.Should().Be(before);
    }

    [Fact]
    public async Task McpCreate_LegacyChatWithAAndB_CreatesA_Ok()
    {
        var chat = await NewChat();
        var a = Epic("Фича A8", chat.Id);
        Epic("Фича B8", chat.Id);

        var (isError, _) = await Mcp(chat.Id, "tasks_create", new { title = "шаг A", featureId = a.Id, role = "step" });

        isError.Should().BeFalse();
    }

    [Fact]
    public async Task McpUpdate_ChangeFeatureId_FromBoundChat_Deny_FeatureUnchanged()
    {
        var chat = await NewChat();
        Epic("Фича A9", chat.Id);
        var b = Epic("Фича B9");
        var free = Tasks.Create(null, OwnerId, new CreateTaskRequest("свободная"));

        var (isError, _) = await Mcp(chat.Id, "tasks_update", new { id = free.Id, featureId = b.Id });

        isError.Should().BeTrue();
        Tasks.GetById(free.Id)!.FeatureId.Should().BeNull();
    }

    [Fact]
    public async Task McpRunExecutor_TaskOfB_FromChatA_Deny_LauncherNotRecorded()
    {
        var chat = await NewChat();
        Epic("Фича A10", chat.Id);
        var b = Epic("Фича B10");
        var step = Tasks.Create(null, OwnerId, new CreateTaskRequest("шаг B", FeatureId: b.Id, Role: "step"));

        var (isError, text) = await Mcp(chat.Id, "tasks_run_executor", new { taskId = step.Id });

        isError.Should().BeTrue();
        text.Should().Contain("Фича A10");
        Tasks.GetById(step.Id)!.ExecutorLaunchedBySessionId.Should().BeNull();
    }
}
