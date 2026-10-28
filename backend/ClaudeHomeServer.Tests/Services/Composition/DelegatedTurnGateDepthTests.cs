using System.Net.Http.Json;
using System.Text.Json;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Composition;
using ClaudeHomeServer.Services.Llm;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace ClaudeHomeServer.Tests.Services.Composition;

/// <summary>
/// Матрица гейта анти-рекурсии делегирования (DelegatedTurnGate.Decide) по двум осям:
/// фактическая глубина хода (agentDepth) и допустимая глубина действия (allowedDepth).
///
/// Решение владельца 17.10: запуск исполнителя задачи разрешён на глубине 1 (чат позван
/// из другого чата), на глубине 2+ анти-рекурсия держит запрет. Все ОСТАЛЬНЫЕ потребители
/// гейта (chats_send, удаления, persona_ask, редакторы) держат allowedDepth = 0 — для них
/// любой делегированный ход (depth >= 1) по-прежнему запрещён. Тест держит обе ветки:
/// allowedDepth = 1 (запуск) и allowedDepth = 0 (остальные), на глубинах 0/1/2.
///
/// Гейт вызывается напрямую (internal, InternalsVisibleTo), минуя HTTP-обвязку: это чистая
/// проверка логики вердикта, а не транспорта. Интеграционный путь (http-тулсет и REST-фильтр
/// реально передают allowedDepth) покрыт отдельно — TasksHttpDelegationGateTests и
/// SessionManagerTests (ExecuteFilter).
/// </summary>
[Trait("Category", "Host")]
public class DelegatedTurnGateDepthTests : IDisposable
{
    // Глубину хода меняем на лету: GetActiveTurnDelegation читает живой адаптер
    private sealed class DepthAdapterFactory : ILlmSessionAdapterFactory
    {
        public volatile int AgentDepth;

        public ILlmSessionAdapter Create(Session session, LlmSessionContext context) =>
            new DepthAdapter(session, this);
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

    private const string Action = "Запуск задачи на исполнение";

    private readonly DepthAdapterFactory _adapters = new();
    private readonly TestWebApplicationFactory _factory;

    public DelegatedTurnGateDepthTests()
    {
        _factory = new TestWebApplicationFactory
        {
            ExtraServices = services => services.AddSingleton<ILlmSessionAdapterFactory>(_adapters),
        };
    }

    public void Dispose() => _factory.Dispose();

    private async Task<(string SessionId, string OwnerId)> CreateSessionAsync()
    {
        var client = _factory.CreateAuthenticatedClient();
        var project = await client.PostAsJsonAsync("/api/projects", new { name = $"depth-{Guid.NewGuid():N}" });
        project.EnsureSuccessStatusCode();
        var projectId = JsonSerializer.Deserialize<JsonElement>(
            await project.Content.ReadAsStringAsync()).GetProperty("id").GetString()!;

        var session = await client.PostAsJsonAsync($"/api/projects/{projectId}/sessions",
            new { mode = "acceptEdits" });
        session.EnsureSuccessStatusCode();
        var sessionId = JsonSerializer.Deserialize<JsonElement>(
            await session.Content.ReadAsStringAsync()).GetProperty("id").GetString()!;

        var ownerId = _factory.Services.GetRequiredService<UserStore>()
            .FindByUsername(TestWebApplicationFactory.TestUsername)!.Id;
        return (sessionId, ownerId);
    }

    // Гейт напрямую: allowedDepth — допустимая глубина действия, agentDepth — фактическая глубина хода
    private ClaudeHomeServer.Filters.DelegatedTurnGateDecision Decide(string ownerId, string sessionId, int allowedDepth)
    {
        var sessions = _factory.Services.GetRequiredService<SessionManager>();
        return ClaudeHomeServer.Filters.DelegatedTurnGate.Decide(
            sessions, ownerId, sessionId, Action,
            alsoWhenExecutorSuppressed: false, allowInTeamImplement: false,
            failOpenWhenUnknown: false, allowedDepth: allowedDepth);
    }

    // ── Запуск исполнителя (allowedDepth = 1) ─────────────────────────────

    [Fact]
    public async Task Запуск_Глубина0_Разрешено()
    {
        var (sessionId, ownerId) = await CreateSessionAsync();
        _adapters.AgentDepth = 0;

        Decide(ownerId, sessionId, allowedDepth: McpEndpoints.RunExecutorAllowedDepth).Allowed.Should().BeTrue();
    }

    [Fact]
    public async Task Запуск_Глубина1_Разрешено()
    {
        var (sessionId, ownerId) = await CreateSessionAsync();
        _adapters.AgentDepth = 1;

        Decide(ownerId, sessionId, allowedDepth: McpEndpoints.RunExecutorAllowedDepth).Allowed.Should().BeTrue(
            "решение владельца 17.10: на глубине 1 запуск разрешён — анти-рекурсия стала потолком");
    }

    [Fact]
    public async Task Запуск_Глубина2_Запрет()
    {
        var (sessionId, ownerId) = await CreateSessionAsync();
        _adapters.AgentDepth = 2;

        Decide(ownerId, sessionId, allowedDepth: McpEndpoints.RunExecutorAllowedDepth).Allowed.Should().BeFalse(
            "на глубине 2+ цепочка делегирования дальше не идёт — запрет держится");
    }

    // ── Остальные потребители (allowedDepth = 0): chats_send, удаления, persona_ask ──

    [Fact]
    public async Task ДругойПотребитель_Глубина0_Разрешено()
    {
        var (sessionId, ownerId) = await CreateSessionAsync();
        _adapters.AgentDepth = 0;

        Decide(ownerId, sessionId, allowedDepth: 0).Allowed.Should().BeTrue();
    }

    [Fact]
    public async Task ДругойПотребитель_Глубина1_Запрет()
    {
        var (sessionId, ownerId) = await CreateSessionAsync();
        _adapters.AgentDepth = 1;

        Decide(ownerId, sessionId, allowedDepth: 0).Allowed.Should().BeFalse(
            "chats_send/удаления держат allowedDepth = 0: любой делегированный ход (depth >= 1) по-прежнему запрещён");
    }

    [Fact]
    public async Task ДругойПотребитель_Глубина2_Запрет()
    {
        var (sessionId, ownerId) = await CreateSessionAsync();
        _adapters.AgentDepth = 2;

        Decide(ownerId, sessionId, allowedDepth: 0).Allowed.Should().BeFalse();
    }
}
