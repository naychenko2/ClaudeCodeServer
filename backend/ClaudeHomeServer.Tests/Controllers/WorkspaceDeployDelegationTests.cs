using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Llm;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace ClaudeHomeServer.Tests.Controllers;

/// <summary>
/// Выкатка из чата идёт http-тулсетом workspace мимо DeployController, поэтому гейт
/// делегированного хода (ADR-025, F1-5) стоит и там: ход процесса или chats_send
/// инструменты deploy_* не получает, обычный ход админа — получает.
/// </summary>
[Trait("Category", "Host")]
public class WorkspaceDeployDelegationTests : IDisposable
{
    private sealed class DepthAdapterFactory : ILlmSessionAdapterFactory
    {
        // Признаки ИДУЩЕГО хода, как у ClaudeSession: между ходами (после result) — 0/false
        public volatile int AgentDepth;
        public volatile bool Suppressed;
        // Живые фоновые агенты прогона — держат делегированность прогона после result
        public volatile bool BgAlive;

        public ILlmSessionAdapter Create(Session session, LlmSessionContext context) => new DepthAdapter(session, this);
    }

    private sealed class DepthAdapter(Session info, DepthAdapterFactory owner) : ILlmSessionAdapter
    {
        public Session Info => info;
        public int CurrentTurnAgentDepth => owner.AgentDepth;
        public bool CurrentTurnSuppressTasksExecute => owner.Suppressed;
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
            int agentDepth = 0, bool suppressTasksExecute = false, string? turnModelOverride = null) => Task.CompletedTask;
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

    public WorkspaceDeployDelegationTests()
    {
        _factory = new TestWebApplicationFactory
        {
            ExtraServices = services => services.AddSingleton<ILlmSessionAdapterFactory>(_adapters),
        };
        // Секция deploy монтируется только там, где контур выкатки настроен
        _factory.ExtraConfig["Deploy:Enabled"] = "true";
    }

    public void Dispose() => _factory.Dispose();

    private async Task<string> DeployStatusTextAsync(HttpClient client, string sessionId)
    {
        var resp = await client.PostAsJsonAsync($"/mcp/wsp/{sessionId}", new
        {
            jsonrpc = "2.0", id = 1, method = "tools/call",
            @params = new { name = "deploy_status", arguments = new { } },
        });
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = JsonSerializer.Deserialize<JsonElement>(await resp.Content.ReadAsStringAsync());
        return string.Join('\n', body.GetProperty("result").GetProperty("content").EnumerateArray()
            .Select(c => c.GetProperty("text").GetString()));
    }

    private static async Task<string> NewSessionAsync(HttpClient client, string projectId)
    {
        var session = await client.PostAsJsonAsync($"/api/projects/{projectId}/sessions", new { mode = "default" });
        session.EnsureSuccessStatusCode();
        return JsonSerializer.Deserialize<JsonElement>(
            await session.Content.ReadAsStringAsync()).GetProperty("id").GetString()!;
    }

    private static async Task<string> NewProjectAsync(HttpClient client)
    {
        var project = await client.PostAsJsonAsync("/api/projects", new { name = $"wsp-deploy-{Guid.NewGuid():N}" });
        project.EnsureSuccessStatusCode();
        return JsonSerializer.Deserialize<JsonElement>(
            await project.Content.ReadAsStringAsync()).GetProperty("id").GetString()!;
    }

    private static async Task<string> ChatsSendTextAsync(HttpClient client, string sessionId, string targetId)
    {
        var resp = await client.PostAsJsonAsync($"/mcp/wsp/{sessionId}", new
        {
            jsonrpc = "2.0", id = 1, method = "tools/call",
            @params = new { name = "chats_send", arguments = new { sessionId = targetId, text = "привет", wait = "none" } },
        });
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = JsonSerializer.Deserialize<JsonElement>(await resp.Content.ReadAsStringAsync());
        return string.Join('\n', body.GetProperty("result").GetProperty("content").EnumerateArray()
            .Select(c => c.GetProperty("text").GetString()));
    }

    // Вердикт гейта удаления публичной базы Dify — тем же методом, что зовёт delete_dataset
    private bool DifyAdminDenied(string sessionId)
    {
        var dify = _factory.Services.GetServices<ClaudeHomeServer.Services.Mcp.Http.IMcpToolset>()
            .OfType<ClaudeHomeServer.Services.Mcp.Http.DifyToolset>().Single();
        var method = typeof(ClaudeHomeServer.Services.Mcp.Http.DifyToolset).GetMethod("DelegatedTurn",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        var owner = _factory.Services.GetRequiredService<SessionManager>().GetById(sessionId)!;
        var ownerId = _factory.Services.GetRequiredService<ClaudeHomeServer.Services.ProjectManager>()
            .GetById(owner.ProjectId!)!.OwnerId!;
        return (bool)method.Invoke(dify, [new ClaudeHomeServer.Services.Mcp.Http.McpToolCallContext(ownerId, sessionId)])!;
    }

    // Ход «закончился» в ядре: статус свободен, ожидатель хода снят — как после result
    private void EndTurn(string sessionId)
    {
        var sessions = _factory.Services.GetRequiredService<SessionManager>();
        sessions.GetById(sessionId)!.Status = SessionStatus.Active;
        var map = (System.Collections.IDictionary)typeof(SessionManager).GetField("_sessions",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(sessions)!;
        var entry = map[sessionId]!;
        entry.GetType().GetField("TurnWaiter")!.SetValue(entry, null);
    }

    // B1 ревью F1-5 (с уточнением N1): признак хода у адаптера гаснет с его концом, а фоновые
    // агенты делегированного хода живут дальше и ходят в MCP с заголовком этого чата — они
    // остаются делегированными, пока живы, или до подачи человека
    [Fact]
    public async Task ДелегированныйПрогон_ПослеResult_ВыкаткаChatsSendDifyЗакрыты_ХодЧеловекаОткрывает()
    {
        var client = _factory.CreateAuthenticatedClient();
        var projectId = await NewProjectAsync(client);
        var sessionId = await NewSessionAsync(client, projectId);
        var otherId = await NewSessionAsync(client, projectId);
        var sessions = _factory.Services.GetRequiredService<SessionManager>();

        // Делегированный ход (chats_send из другого чата / ход процесса) подан и закончился:
        // адаптер говорит «между ходами» (глубина 0), прогон с фоновыми агентами жив
        await sessions.SendMessageAndWaitAsync(sessionId, "сделай из другого чата", TimeSpan.Zero, agentDepth: 1);
        _adapters.BgAlive = true;
        EndTurn(sessionId);
        _adapters.AgentDepth = 0;

        (await DeployStatusTextAsync(client, sessionId)).Should().Contain("недоступно на делегированном ходу",
            "фоновый агент делегированного хода выкатку не получает");
        (await ChatsSendTextAsync(client, sessionId, otherId)).Should().Contain("недоступно на делегированном ходу");
        DifyAdminDenied(sessionId).Should().BeTrue("и удаление публичной базы Dify — тоже");

        // Ход человека снимает делегированность прогона
        await sessions.SendMessageAsync(sessionId, "теперь я сам", []);
        EndTurn(sessionId);

        (await DeployStatusTextAsync(client, sessionId)).Should().NotContain("делегированном ходу");
        DifyAdminDenied(sessionId).Should().BeFalse();
    }

    // N1: фоновые агенты делегированного хода завершились — признак снят без хода человека
    [Fact]
    public async Task ДелегированныйПрогон_ФоновыеЗавершились_ВыкаткаОткрыта()
    {
        var client = _factory.CreateAuthenticatedClient();
        var sessionId = await NewSessionAsync(client, await NewProjectAsync(client));
        var sessions = _factory.Services.GetRequiredService<SessionManager>();

        await sessions.SendMessageAndWaitAsync(sessionId, "сделай из другого чата", TimeSpan.Zero, agentDepth: 1);
        _adapters.BgAlive = true;
        EndTurn(sessionId);
        (await DeployStatusTextAsync(client, sessionId)).Should().Contain("недоступно на делегированном ходу");

        _adapters.BgAlive = false;
        (await DeployStatusTextAsync(client, sessionId)).Should().NotContain("делегированном ходу");
        DifyAdminDenied(sessionId).Should().BeFalse();
    }

    // Замечание 4 ревью F1-5: выкатка и удаление публичной базы — admin-действия, они закрыты и
    // на ходе-реакции на доклад исполнителя (как у AdminByStoreHandler)
    [Fact]
    public async Task ХодРеакцияНаДоклад_ВыкаткаИDifyЗакрыты()
    {
        var client = _factory.CreateAuthenticatedClient();
        var sessionId = await NewSessionAsync(client, await NewProjectAsync(client));

        _adapters.Suppressed = true;
        (await DeployStatusTextAsync(client, sessionId)).Should().Contain("отвечаешь на доклад исполнителя");
        DifyAdminDenied(sessionId).Should().BeTrue();

        _adapters.Suppressed = false;
        (await DeployStatusTextAsync(client, sessionId)).Should().NotContain("доклад исполнителя");
    }

    [Fact]
    public async Task ДелегированныйХод_ВыкаткаЗакрыта_ОбычныйХод_Открыта()
    {
        var client = _factory.CreateAuthenticatedClient();
        var sessionId = await NewSessionAsync(client, await NewProjectAsync(client));

        _adapters.AgentDepth = 1;
        var delegated = await DeployStatusTextAsync(client, sessionId);
        delegated.Should().Contain("недоступно на делегированном ходу");

        _adapters.AgentDepth = 0;
        var normal = await DeployStatusTextAsync(client, sessionId);
        normal.Should().NotContain("делегированном ходу");
        normal.Should().NotContain("секция deploy выключена", "контроль обязан дойти до самой выкатки");
    }
}
