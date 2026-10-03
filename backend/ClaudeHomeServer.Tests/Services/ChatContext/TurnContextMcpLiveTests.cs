using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.ImageEditor.Threads;
using ClaudeHomeServer.Services.Llm;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace ClaudeHomeServer.Tests.Services.ChatContext;

// Сквозной прогон тулсета turn-context на собранном приложении (2б-5): настоящий McpTransportController,
// реестр тулсетов и стор контекста. Живого хода агента тут нет (нужен ключ модели) — вызываем MCP-ручку
// POST /mcp/turn-context/{sessionId} так, как её вызывает CLI, и читаем результат ручкой GET контекста,
// которую читает строка контекста человека: «подключи palette.png как образец стиля» → чип ✦.
public sealed class TurnContextMcpLiveTests : IDisposable
{
    private sealed class DepthAdapterFactory : ILlmSessionAdapterFactory
    {
        public volatile int AgentDepth;
        public ILlmSessionAdapter Create(Session session, LlmSessionContext context) => new DepthAdapter(session, this);
    }

    private sealed class DepthAdapter(Session info, DepthAdapterFactory owner) : ILlmSessionAdapter
    {
        public Session Info => info;
        public int CurrentTurnAgentDepth => owner.AgentDepth;
        public bool CurrentTurnSuppressTasksExecute => false;
        public bool HasLiveTurn => false;
        public bool HasQueuedTurn => false;
        public bool OrchestrationActive => false;
        public bool HasPendingBg => false;
        public bool HasTrackedBg => false;
        public bool HasTrackedCommandBg => false;
        public bool IsContinuationInFlight => false;
        public LlmCapabilities Capabilities => LlmCapabilitiesCatalog.Claude;

        public Task StartAsync() => Task.CompletedTask;
        public Task SendMessageAsync(string text, IReadOnlyList<string>? attachedPaths = null,
            int agentDepth = 0, bool suppressTasksExecute = false) => Task.CompletedTask;
        public Task CompactAsync() => Task.CompletedTask;
        public void RespondPermission(string requestId, string behavior) { }
        public void AnswerQuestion(string toolUseId, string updatedInputJson) { }
        public void RespondPlan(string requestId, bool approve, string? feedback) { }
        public bool TrySetPermissionModeLive(ClaudeMode mode) => false;
        public bool TrySetModelLive(string model) => false;
        public void Interrupt() { }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private readonly DepthAdapterFactory _adapters = new();
    private readonly TestWebApplicationFactory _factory;
    private readonly HttpClient _client;
    private readonly string _ownerId;
    private readonly string _projectId;
    private readonly string _projectRoot;

    public TurnContextMcpLiveTests()
    {
        _factory = new TestWebApplicationFactory
        {
            ExtraServices = services => services.AddSingleton<ILlmSessionAdapterFactory>(_adapters),
        };
        var users = _factory.Services.GetRequiredService<UserStore>();
        _ownerId = users.FindByUsername(TestWebApplicationFactory.TestUsername)!.Id;
        users.SetFeatureFlag(_ownerId, FeatureFlagKeys.ImageEditor, true);
        users.SetFeatureFlag(_ownerId, FeatureFlagKeys.ComposerContextRow, true);
        _projectRoot = Path.Combine(_factory.TempDir, "live_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(Path.Combine(_projectRoot, "art"));
        File.WriteAllBytes(Path.Combine(_projectRoot, "art", "palette.png"), [1, 2, 3]);
        _projectId = _factory.Services.GetRequiredService<ProjectManager>()
            .Create("live", _projectRoot, _ownerId, TestWebApplicationFactory.TestUsername).Id;
        _client = _factory.CreateAuthenticatedClient();
    }

    public void Dispose()
    {
        _factory.Dispose();
        GC.SuppressFinalize(this);
    }

    private Task<Session> Chat() =>
        _factory.Services.GetRequiredService<SessionManager>().CreateAsync(_projectId, ClaudeMode.AcceptEdits, name: "Чат");

    private async Task<JsonElement> RpcAsync(string sessionId, string method, object? args = null, string? tool = null)
    {
        var resp = await _client.PostAsJsonAsync($"/mcp/turn-context/{sessionId}", new
        {
            jsonrpc = "2.0", id = 1, method,
            @params = tool is null ? (object)new { } : new { name = tool, arguments = args ?? new { } },
        });
        resp.StatusCode.Should().Be(HttpStatusCode.OK, await resp.Content.ReadAsStringAsync());
        return JsonSerializer.Deserialize<JsonElement>(await resp.Content.ReadAsStringAsync());
    }

    private static string ToolText(JsonElement answer) =>
        answer.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString()!;

    private static bool IsError(JsonElement answer) => answer.GetProperty("result").TryGetProperty("isError", out var e) && e.GetBoolean();

    private async Task<JsonElement> ContextAsync(Session chat) =>
        JsonSerializer.Deserialize<JsonElement>(await _client.GetStringAsync($"/api/chats/{chat.Id}/context"));

    [Fact]
    public async Task Подключи_palette_png_как_образец_стиля_даёт_чип_агента_в_контексте()
    {
        var chat = await Chat();
        // Основной объект — картинка чата: роль style принимает только он
        _factory.Services.GetRequiredService<ImageThreadStore>().Create(_ownerId, chat.Id, null, "", true);

        var list = await RpcAsync(chat.Id, "tools/list");
        list.GetProperty("result").GetProperty("tools").EnumerateArray().Select(t => t.GetProperty("name").GetString())
            .Should().Equal("context_state", "context_attach", "context_detach");

        var attach = await RpcAsync(chat.Id, "tools/call",
            new { kind = "project-file", @ref = new { path = "art/palette.png" }, role = "style" }, "context_attach");

        IsError(attach).Should().BeFalse(ToolText(attach));
        var refs = (await ContextAsync(chat)).GetProperty("refs");
        refs.GetArrayLength().Should().Be(1);
        refs[0].GetProperty("kind").GetString().Should().Be("project-file");
        refs[0].GetProperty("role").GetString().Should().Be("style");
        refs[0].GetProperty("by").GetString().Should().Be("agent", "чип ✦ рисуется по By = Agent");
        refs[0].GetProperty("label").GetString().Should().Be("palette.png");

        var state = await RpcAsync(chat.Id, "tools/call", null, "context_state");
        ToolText(state).Should().Contain("palette.png [art/palette.png]").And.Contain("стиль (style) ✦ поставил Claude");

        // tools/list после записи тот же: состав от содержимого контекста не зависит
        (await RpcAsync(chat.Id, "tools/list")).GetProperty("result").GetProperty("tools").GetRawText()
            .Should().Be(list.GetProperty("result").GetProperty("tools").GetRawText());
    }

    [Fact]
    public async Task Мусорный_ref_отказ_и_контекст_не_тронут()
    {
        var chat = await Chat();

        var attach = await RpcAsync(chat.Id, "tools/call", new { kind = "project-file", @ref = new { path = "../secret.png" } },
            "context_attach");

        IsError(attach).Should().BeTrue();
        (await ContextAsync(chat)).GetProperty("revision").GetInt64().Should().Be(0);
    }

    [Fact]
    public async Task Делегированный_ход_не_меняет_контекст_а_читает_его()
    {
        var chat = await Chat();
        _adapters.AgentDepth = 1;

        var attach = await RpcAsync(chat.Id, "tools/call", new { kind = "project-file", @ref = new { path = "art/palette.png" } },
            "context_attach");
        var state = await RpcAsync(chat.Id, "tools/call", null, "context_state");

        IsError(attach).Should().BeTrue();
        ToolText(attach).Should().Contain("делегированном");
        IsError(state).Should().BeFalse("context_state — чтение, разрешён");
        (await ContextAsync(chat)).GetProperty("refs").GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task Без_флага_сервер_пуст()
    {
        _factory.Services.GetRequiredService<UserStore>().SetFeatureFlag(_ownerId, FeatureFlagKeys.ComposerContextRow, false);
        var chat = await Chat();

        var list = await RpcAsync(chat.Id, "tools/list");

        list.GetProperty("result").GetProperty("tools").GetArrayLength().Should().Be(0);
    }
}
