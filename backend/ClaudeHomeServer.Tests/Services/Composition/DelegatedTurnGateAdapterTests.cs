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
/// Шов <see cref="DelegatedTurnGateAdapter"/> на ЖИВОМ SessionManager: тулсет редактора
/// картинок тратит деньги, а в его собственных тестах гейт замокан — здесь проверяется, что
/// адаптер зовёт DelegatedTurnGate с нужными флагами (ADR-018 §2): отказ на делегированном
/// ходу и на ходу доклада исполнителя, отказ без вызывателя (fail-closed), пропуск обычного хода.
/// </summary>
public class DelegatedTurnGateAdapterTests : IDisposable
{
    // Глубину и флаг «ход доклада» меняем на лету: GetActiveTurnDelegation читает живой адаптер
    private sealed class TurnAdapterFactory : ILlmSessionAdapterFactory
    {
        public volatile int AgentDepth;
        public volatile bool SuppressTasksExecute;

        public ILlmSessionAdapter Create(Session session, LlmSessionContext context) =>
            new TurnAdapter(session, this);
    }

    private sealed class TurnAdapter(Session info, TurnAdapterFactory owner) : ILlmSessionAdapter
    {
        public Session Info => info;
        public int CurrentTurnAgentDepth => owner.AgentDepth;
        public bool CurrentTurnSuppressTasksExecute => owner.SuppressTasksExecute;
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

    private const string Action = "Генерация картинки";

    private readonly TurnAdapterFactory _adapters = new();
    private readonly TestWebApplicationFactory _factory;

    public DelegatedTurnGateAdapterTests()
    {
        _factory = new TestWebApplicationFactory
        {
            ExtraServices = services => services.AddSingleton<ILlmSessionAdapterFactory>(_adapters),
        };
    }

    public void Dispose() => _factory.Dispose();

    private DelegatedTurnGateAdapter Gate =>
        new(_factory.Services.GetRequiredService<SessionManager>());

    private async Task<(string SessionId, string OwnerId)> CreateSessionAsync()
    {
        var client = _factory.CreateAuthenticatedClient();
        var project = await client.PostAsJsonAsync("/api/projects", new { name = $"gate-{Guid.NewGuid():N}" });
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

    [Fact]
    public async Task ДелегированныйХод_Отказ()
    {
        var (sessionId, ownerId) = await CreateSessionAsync();
        _adapters.AgentDepth = 1;

        Gate.Deny(ownerId, sessionId, Action).Should().Contain("недоступно на делегированном ходу");
    }

    [Fact]
    public async Task ХодДокладаИсполнителя_Отказ()
    {
        var (sessionId, ownerId) = await CreateSessionAsync();
        _adapters.AgentDepth = 0;
        _adapters.SuppressTasksExecute = true;

        Gate.Deny(ownerId, sessionId, Action).Should().Contain("ты отвечаешь на доклад исполнителя",
            "тулсет тратит деньги — реакционный ход доклада тоже под запретом");
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public async Task ХодБезВызывателя_ОтказПоПостроению(string? callerSessionId)
    {
        var (_, ownerId) = await CreateSessionAsync();

        Gate.Deny(ownerId, callerSessionId!, Action).Should().Contain("без сессии-вызывателя",
            "http-вызов без вызывателя — аномалия, пропуск стоил бы денег (fail-closed)");
    }

    [Fact]
    public async Task ХодБезВладельца_ОтказПоПостроению()
    {
        var (sessionId, _) = await CreateSessionAsync();

        Gate.Deny("", sessionId, Action).Should().Contain("без сессии-вызывателя");
    }

    [Fact]
    public async Task ОбычныйХод_Пропуск()
    {
        var (sessionId, ownerId) = await CreateSessionAsync();
        _adapters.AgentDepth = 0;
        _adapters.SuppressTasksExecute = false;

        Gate.Deny(ownerId, sessionId, Action).Should().BeNull();
    }
}
