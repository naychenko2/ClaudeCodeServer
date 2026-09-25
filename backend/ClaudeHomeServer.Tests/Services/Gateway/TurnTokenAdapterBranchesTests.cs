using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services.Llm;
using ClaudeHomeServer.Services.Llm.Gateway;
using ClaudeHomeServer.Services.Turn;
using FluentAssertions;
using Microsoft.Extensions.Configuration;

namespace ClaudeHomeServer.Tests.Services.Gateway;

/// <summary>
/// Токен хода отзывается на КАЖДОЙ ветке конца хода, которую проходит настоящий
/// FallbackLlmSessionAdapter: успех, «Стоп», убийство адаптера, сбой запуска процесса и
/// сбой сборки паспорта (раньше он глотал и саму публикацию turn/completed).
/// </summary>
public class TurnTokenAdapterBranchesTests
{
    private sealed class FakeInner(Session info) : ILlmSessionAdapter
    {
        public Session Info { get; } = info;
        public Func<ServerMessage, Task>? Sink;
        public Action? OnSend;
        public Action? OnInterrupt;

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
        public long SubmittedTurnSeq { get; private set; }

        public Task SendMessageAsync(string text, IReadOnlyList<string>? attachedPaths = null,
            int agentDepth = 0, bool suppressTasksExecute = false)
        {
            SubmittedTurnSeq++;
            OnSend?.Invoke();
            return Task.CompletedTask;
        }

        public void Emit(ServerMessage msg) => Sink?.Invoke(msg).GetAwaiter().GetResult();

        public Task StartAsync() => Task.CompletedTask;
        public Task CompactAsync() => Task.CompletedTask;
        public void RespondPermission(string requestId, string behavior) { }
        public void AnswerQuestion(string toolUseId, string updatedInputJson) { }
        public void RespondPlan(string requestId, bool approve, string? feedback) { }
        public bool TrySetPermissionModeLive(ClaudeMode mode) => false;
        public bool TrySetModelLive(string model) => false;
        public void Interrupt() => OnInterrupt?.Invoke();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class LiveEgress : IEgressProbe
    {
        public Task<bool> IsDownAsync(CancellationToken ct = default) => Task.FromResult(false);
    }

    private readonly TurnEventBus _bus = new();
    private readonly List<TurnCompleted> _completed = [];

    private (FallbackLlmSessionAdapter Sut, FakeInner Inner, TurnTokenService Tokens) BuildSut(
        Func<int>? contextEstimate = null)
    {
        var tokens = new TurnTokenService(_bus);
        _bus.OnNotification<TurnCompleted>(e => { lock (_completed) _completed.Add(e); return Task.CompletedTask; });
        var session = new Session { Model = "sonnet", Provider = "acc-a", OwnerId = "owner-1" };
        var inner = new FakeInner(session);
        var pool = new ClaudeSubscriptionPool(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["ClaudeSubscriptions:acc-a:OAuthToken"] = "t" })
            .Build());
        var sut = new FallbackLlmSessionAdapter(inner,
            () => session.Model,
            _ => Task.CompletedTask,
            pool, providers: null, Path.GetTempPath(), launcher: null, initialProfileRoot: null,
            lastContextTokens: contextEstimate,
            egress: new LiveEgress(),
            events: _bus,
            egressRetryDelay: TimeSpan.FromMilliseconds(10));
        inner.Sink = sut.HandleMessageAsync;
        return (sut, inner, tokens);
    }

    private static async Task WaitForAsync(Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return;
            await Task.Delay(10);
        }
        throw new TimeoutException($"не дождались: {what}");
    }

    private async Task AssertRevokedByTurnCompletedAsync(TurnTokenService tokens, IssuedTurnToken t, string outcome)
    {
        await WaitForAsync(() => { lock (_completed) return _completed.Count > 0; }, "turn/completed");
        lock (_completed) _completed.Should().ContainSingle().Which.Outcome.Should().Be(outcome);
        tokens.Validate(t.Grant.TurnId, t.Token).Should().BeNull($"ход закончился ({outcome}) — токен обязан умереть");
    }

    [Fact]
    public async Task Успех()
    {
        var (sut, inner, tokens) = BuildSut();
        var t = tokens.Issue("owner-1", inner.Info.Id);
        inner.OnSend = () => inner.Emit(new ResultMessage("success", 10, 1, null, null));

        await sut.SendMessageAsync("ход");

        await AssertRevokedByTurnCompletedAsync(tokens, t, "success");
    }

    [Fact]
    public async Task Стоп()
    {
        var (sut, inner, tokens) = BuildSut();
        var t = tokens.Issue("owner-1", inner.Info.Id);
        var sent = new TaskCompletionSource();
        inner.OnSend = () => sent.TrySetResult();
        inner.OnInterrupt = () => inner.Emit(new ExitedMessage());

        await sut.SendMessageAsync("ход");
        await sent.Task.WaitAsync(TimeSpan.FromSeconds(5));
        tokens.Validate(t.Grant.TurnId, t.Token).Should().NotBeNull("ход ещё идёт");
        sut.Interrupt();

        await AssertRevokedByTurnCompletedAsync(tokens, t, "interrupted");
    }

    [Fact]
    public async Task УбийствоАдаптера()
    {
        var (sut, inner, tokens) = BuildSut();
        var t = tokens.Issue("owner-1", inner.Info.Id);
        var sent = new TaskCompletionSource();
        inner.OnSend = () => sent.TrySetResult();

        await sut.SendMessageAsync("ход");
        await sent.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await sut.DisposeAsync();

        await AssertRevokedByTurnCompletedAsync(tokens, t, "cancelled");
    }

    [Fact]
    public async Task СбойЗапускаПроцесса()
    {
        var (sut, inner, tokens) = BuildSut();
        var t = tokens.Issue("owner-1", inner.Info.Id);
        // Так ClaudeSession сообщает о провале Process.Start
        inner.OnSend = () => inner.Emit(new ErrorMessage("Не удалось запустить claude",
            Details: "[Win32:2]No such file or directory"));

        await sut.SendMessageAsync("ход");

        await AssertRevokedByTurnCompletedAsync(tokens, t, "failed");
    }

    [Fact]
    public async Task СбойСборкиПаспорта_СобытиеВсёРавноУходит()
    {
        var (sut, inner, tokens) = BuildSut(contextEstimate: () => throw new InvalidOperationException("оценка сломалась"));
        var t = tokens.Issue("owner-1", inner.Info.Id);
        inner.OnSend = () => inner.Emit(new ResultMessage("success", 10, 1, null, null));

        await sut.SendMessageAsync("ход");

        await AssertRevokedByTurnCompletedAsync(tokens, t, "success");
        lock (_completed) _completed[0].Passport.Should().BeNull("паспорт не собрался, но конец хода опубликован");
    }
}
