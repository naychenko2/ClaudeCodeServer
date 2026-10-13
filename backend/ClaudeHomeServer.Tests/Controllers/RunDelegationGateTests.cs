using System.Collections;
using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Llm;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace ClaudeHomeServer.Tests.Controllers;

/// <summary>
/// Делегированность ПРОГОНА (B1 ревью F1-5). Признак хода у адаптера (ClaudeSession) гаснет в
/// finally хода вместе с _turnLock, а прогон с фоновыми агентами (Agent run_in_background,
/// Workflow) живёт после result и ходит в MCP с заголовком своего чата. По одному признаку хода
/// такой агент выглядел человеком и получал chats_send и запуск исполнителей. Теперь
/// SessionManager помнит делегированность прогона, пока живы его фоновые агенты
/// (ILlmSessionAdapter.HasLiveBgAgents), или до подачи человека.
///
/// Адаптер здесь ведёт себя как ClaudeSession между ходами: глубина 0 — ход отпущен, живость
/// фоновых агентов прогона задаёт тест.
/// Тест держится только ядра сессий и гейтов: вертикаль «Процессов» ему не нужна.
/// </summary>
public class RunDelegationGateTests : IDisposable
{
    private sealed class DepthAdapterFactory : ILlmSessionAdapterFactory
    {
        // Признак ИДУЩЕГО хода: между ходами — 0, как у ClaudeSession после finally
        public volatile int AgentDepth;
        // Живые фоновые агенты прогона — держат делегированность прогона после result
        public volatile bool BgAlive;

        public ILlmSessionAdapter Create(Session session, LlmSessionContext context) => new DepthAdapter(session, this);
    }

    private sealed class DepthAdapter(Session info, DepthAdapterFactory owner) : ILlmSessionAdapter
    {
        public Session Info => info;
        public int CurrentTurnAgentDepth => owner.AgentDepth;
        // Признак хода доклада между ходами погашен: держит его только память прогона в SessionManager
        public bool CurrentTurnSuppressTasksExecute => false;
        public bool HasLiveTurn => false;
        public bool HasQueuedTurn => false;
        public bool OrchestrationActive => false;
        public bool HasPendingBg => false;
        public bool HasTrackedBg => false;
        public bool HasTrackedCommandBg => false;
        public bool HasLiveBgAgents => owner.BgAlive;
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

    public RunDelegationGateTests()
    {
        _factory = new TestWebApplicationFactory
        {
            ExtraServices = services => services.AddSingleton<ILlmSessionAdapterFactory>(_adapters),
        };
    }

    public void Dispose() => _factory.Dispose();

    private SessionManager Sessions => _factory.Services.GetRequiredService<SessionManager>();

    private static async Task<string> NewSessionAsync(HttpClient client, string projectId)
    {
        var session = await client.PostAsJsonAsync($"/api/projects/{projectId}/sessions", new { mode = "default" });
        session.EnsureSuccessStatusCode();
        return JsonSerializer.Deserialize<JsonElement>(
            await session.Content.ReadAsStringAsync()).GetProperty("id").GetString()!;
    }

    private static async Task<string> NewProjectAsync(HttpClient client)
    {
        var project = await client.PostAsJsonAsync("/api/projects", new { name = $"run-deleg-{Guid.NewGuid():N}" });
        project.EnsureSuccessStatusCode();
        return JsonSerializer.Deserialize<JsonElement>(
            await project.Content.ReadAsStringAsync()).GetProperty("id").GetString()!;
    }

    private static async Task<string> ToolTextAsync(HttpClient client, string server, string sessionId,
        string tool, object args)
    {
        var resp = await client.PostAsJsonAsync($"/mcp/{server}/{sessionId}", new
        {
            jsonrpc = "2.0", id = 1, method = "tools/call",
            @params = new { name = tool, arguments = args },
        });
        resp.StatusCode.Should().Be(HttpStatusCode.OK, "отказ гейта — content-ошибка, не протокольная");
        var body = JsonSerializer.Deserialize<JsonElement>(await resp.Content.ReadAsStringAsync());
        return string.Join('\n', body.GetProperty("result").GetProperty("content").EnumerateArray()
            .Select(c => c.GetProperty("text").GetString()));
    }

    private Task<string> ChatsSendAsync(HttpClient client, string sessionId, string targetId) =>
        ToolTextAsync(client, "wsp", sessionId, "chats_send", new { sessionId = targetId, text = "привет", wait = "none" });

    private Task<string> RunExecutorAsync(HttpClient client, string sessionId) =>
        ToolTextAsync(client, "tasks", sessionId, "tasks_run_executor", new { taskId = "нет-такой" });

    // Ход «закончился» в ядре: статус свободен, ожидатель хода снят — как после result
    private void EndTurn(string sessionId)
    {
        Sessions.GetById(sessionId)!.Status = SessionStatus.Active;
        var map = (IDictionary)typeof(SessionManager).GetField("_sessions",
            BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(Sessions)!;
        var entry = map[sessionId]!;
        entry.GetType().GetField("TurnWaiter")!.SetValue(entry, null);
    }

    private string OwnerOf(string sessionId) =>
        _factory.Services.GetRequiredService<ProjectManager>()
            .GetById(Sessions.GetById(sessionId)!.ProjectId!)!.OwnerId!;

    [Fact]
    public async Task ДелегированныйПрогон_ФоновыйАгентПослеResult_ГейтыЗакрыты_ХодЧеловекаСнимает()
    {
        var client = _factory.CreateAuthenticatedClient();
        var projectId = await NewProjectAsync(client);
        var sessionId = await NewSessionAsync(client, projectId);
        var otherId = await NewSessionAsync(client, projectId);

        // Ход пришёл из другого чата (chats_send, глубина 1) и закончился: адаптер говорит
        // «между ходами», а фоновые агенты этого хода живы и зовут MCP с заголовком чата
        await Sessions.SendMessageAndWaitAsync(sessionId, "сделай из другого чата", TimeSpan.Zero, agentDepth: 1);
        _adapters.BgAlive = true;
        EndTurn(sessionId);
        _adapters.AgentDepth = 0;

        Sessions.GetActiveTurnDelegation(sessionId, OwnerOf(sessionId)).AgentDepth.Should().Be(1,
            "прогон делегированного хода делегирован, пока живы его фоновые агенты");
        (await ChatsSendAsync(client, sessionId, otherId)).Should().Contain("недоступно на делегированном ходу",
            "фоновый агент не пишет в третьи чаты");
        (await RunExecutorAsync(client, sessionId)).Should().Contain("недоступно на делегированном ходу",
            "и не запускает исполнителей");

        // Авто-ход глубины 0 (автоматика, не человек) делегированность прогона не снимает
        await Sessions.SendOrEnqueueAsync(sessionId, "автоматика");
        EndTurn(sessionId);
        (await ChatsSendAsync(client, sessionId, otherId)).Should().Contain("недоступно на делегированном ходу");

        // Ход человека — снова человек
        await Sessions.SendMessageAsync(sessionId, "дальше я сам", []);
        EndTurn(sessionId);
        Sessions.GetActiveTurnDelegation(sessionId, OwnerOf(sessionId)).AgentDepth.Should().Be(0);
        (await ChatsSendAsync(client, sessionId, otherId)).Should().NotContain("делегированном ходу");
        (await RunExecutorAsync(client, sessionId)).Should().Contain("не найдена")
            .And.NotContain("делегированном", "гейт ход человека пропускает, отказ — только по задаче");
    }

    // Ход-реакция на доклад исполнителя тоже помечает прогон: его фоновые агенты после result
    // остаются «ходом доклада», пока живы или пока не напишет человек
    [Fact]
    public async Task ХодДоклада_ПризнакДоживаетДоХодаЧеловека()
    {
        var client = _factory.CreateAuthenticatedClient();
        var sessionId = await NewSessionAsync(client, await NewProjectAsync(client));
        var owner = OwnerOf(sessionId);

        await Sessions.SendOrEnqueueAsync(sessionId, "доклад исполнителя", silent: true, suppressTasksExecute: true);
        _adapters.BgAlive = true;
        EndTurn(sessionId);

        Sessions.GetActiveTurnDelegation(sessionId, owner).ExecutorSuppressed.Should().BeTrue();

        await Sessions.SendMessageAsync(sessionId, "принял", []);
        Sessions.GetActiveTurnDelegation(sessionId, owner).ExecutorSuppressed.Should().BeFalse();
    }

    // Контроль: чат, в который делегированных ходов не было, — обычный (регрессии нет)
    [Fact]
    public async Task БезДелегированныхПодач_ГейтПропускает()
    {
        var client = _factory.CreateAuthenticatedClient();
        var projectId = await NewProjectAsync(client);
        var sessionId = await NewSessionAsync(client, projectId);
        var otherId = await NewSessionAsync(client, projectId);

        await Sessions.SendMessageAsync(sessionId, "привет", []);
        EndTurn(sessionId);

        (await ChatsSendAsync(client, sessionId, otherId)).Should().NotContain("делегированном ходу");
    }
}
