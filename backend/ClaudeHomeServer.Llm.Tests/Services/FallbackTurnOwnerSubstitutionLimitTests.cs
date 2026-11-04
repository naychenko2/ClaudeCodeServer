using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Llm;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace ClaudeHomeServer.Tests.Services;

// Потолок подмен резолвится по владельцу ХОДА, а не по Info.OwnerId: у проектной сессии
// Info.OwnerId пуст по контракту, и по нему читался бы global-слой, а не личный слот арендатора.
public class FallbackTurnOwnerSubstitutionLimitTests : IDisposable
{
    private const string Tenant = "tenant-1";
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ccs_fbl_" + Guid.NewGuid().ToString("N"));

    public FallbackTurnOwnerSubstitutionLimitTests()
    {
        Directory.CreateDirectory(_dir);
        // global = 2, личный слот арендатора = 5
        File.WriteAllText(Path.Combine(_dir, "model-fallback.json"),
            """{"version":1,"global":{"maxSubstitutions":2},"owners":{"tenant-1":{"maxSubstitutions":5}}}""");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private sealed class StubInner(Session info) : ILlmSessionAdapter
    {
        public Session Info { get; } = info;
        public LlmCapabilities Capabilities => LlmCapabilitiesCatalog.Claude;
        public int CurrentTurnAgentDepth => 0;
        public bool CurrentTurnSuppressTasksExecute => false;
        public bool HasLiveTurn => false;
        public bool HasQueuedTurn => false;
        public bool OrchestrationActive => false;
        public bool HasPendingBg => false;
        public bool HasTrackedBg => false;
        public bool HasTrackedCommandBg => false;
        public bool IsContinuationInFlight => false;
        public long SubmittedTurnSeq => 0;
        public Task SendMessageAsync(string text, IReadOnlyList<string>? attachedPaths = null,
            int agentDepth = 0, bool suppressTasksExecute = false, string? turnModelOverride = null) => Task.CompletedTask;
        public Task StartAsync() => Task.CompletedTask;
        public Task CompactAsync() => Task.CompletedTask;
        public void RespondPermission(string requestId, string behavior) { }
        public void AnswerQuestion(string toolUseId, string updatedInputJson) { }
        public void RespondPlan(string requestId, bool approve, string? feedback) { }
        public bool TrySetPermissionModeLive(ClaudeMode mode) => false;
        public bool TrySetModelLive(string model) => false;
        public void Interrupt() { }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private FallbackLlmSessionAdapter Build(string? sessionOwnerId, string? turnOwnerId)
    {
        var cfg = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["DataPath"] = Path.Combine(_dir, "projects.json"),
        }).Build();
        var session = new Session { Model = "sonnet", OwnerId = sessionOwnerId };
        var pool = new ClaudeSubscriptionPool(new ConfigurationBuilder().Build());
        return new FallbackLlmSessionAdapter(new StubInner(session), () => session.Model,
            _ => Task.CompletedTask, pool, null, Path.GetTempPath(), launcher: null,
            initialProfileRoot: null, fallbackSettings: new FallbackSettingsStore(cfg),
            turnOwnerId: turnOwnerId);
    }

    [Fact]
    public void ПроектнаяСессия_ПустойInfoOwner_ПотолокИзЛичногоСлотаАрендатора()
    {
        var sut = Build(sessionOwnerId: null, turnOwnerId: Tenant);

        Assert.Equal(5, sut.EffectiveMaxSubstitutions());
    }

    [Fact]
    public void ЧатВнеПроекта_ВладелецХодаРавенInfoOwner_ПотолокПрежний()
    {
        var sut = Build(sessionOwnerId: Tenant, turnOwnerId: Tenant);

        Assert.Equal(5, sut.EffectiveMaxSubstitutions());
    }

    // Боевой кейс: владелец есть, личного слота нет → global. Фабрика всегда передаёт непустой
    // TurnOwnerId, поэтому «ни владельца сессии, ни владельца хода» в бою недостижимо.
    [Fact]
    public void ВладелецЕсть_ЛичногоСлотаНет_ГлобальныйПотолок()
    {
        var sut = Build(sessionOwnerId: null, turnOwnerId: "tenant-without-slot");

        Assert.Equal(2, sut.EffectiveMaxSubstitutions());
    }
}
