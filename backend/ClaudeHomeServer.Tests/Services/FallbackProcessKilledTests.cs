using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Llm;
using ClaudeHomeServer.Services.Turn;
using FluentAssertions;
using Microsoft.Extensions.Configuration;

namespace ClaudeHomeServer.Tests.Services;

/// <summary>
/// Фолбэк хода не подменяет пару, когда процесс CLI убит сигналом ОС (oom-kill, exit 137), и
/// пропускает ненастроенных провайдеров при обходе цепочки. Инцидент 2026-10-10: три oom-kill
/// подряд увели чат MiniMax → opus → deepseek, а на deepseek без ключа ход упал исключением.
/// </summary>
public class FallbackProcessKilledTests
{
    private readonly List<ServerMessage> _downstream = [];

    private sealed class FakeInnerAdapter(Session info) : ILlmSessionAdapter
    {
        public Session Info { get; } = info;
        public Func<ServerMessage, Task>? Sink;
        public Queue<Action> Scripts { get; } = new();
        public List<(string Provider, string? Model)> Attempts { get; } = [];

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
            int agentDepth = 0, bool suppressTasksExecute = false, string? turnModelOverride = null)
        {
            SubmittedTurnSeq++;
            Attempts.Add((Info.Provider ?? "", Info.Model));
            if (Scripts.Count > 0) Scripts.Dequeue()();
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
        public int Interrupts;
        // Как в проде: Interrupt убивает процесс, и тот досылает свой терминал
        public Action? OnInterrupt = null;
        public void Interrupt() { Interrupts++; OnInterrupt?.Invoke(); }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static ClaudeSubscriptionPool BuildPool(params string[] keys)
    {
        var dict = new Dictionary<string, string?>();
        foreach (var key in keys) dict[$"ClaudeSubscriptions:{key}:OAuthToken"] = $"token-{key}";
        return new ClaudeSubscriptionPool(new ConfigurationBuilder().AddInMemoryCollection(dict).Build());
    }

    // p1 настроен, p2 — без ApiKey (Enabled=false): ровно DeepSeek без ключа из инцидента
    private static LlmProviderRegistry BuildProviders()
    {
        var dict = new Dictionary<string, string?>
        {
            ["LlmProviders:p1:ApiKey"] = "sk-1",
            ["LlmProviders:p1:AnthropicBaseUrl"] = "https://p1.example.com",
            ["LlmProviders:p1:Models:0:Id"] = "m1",
            ["LlmProviders:p2:AnthropicBaseUrl"] = "https://p2.example.com",
            ["LlmProviders:p2:Models:0:Id"] = "m2",
        };
        return new LlmProviderRegistry(new ConfigurationBuilder().AddInMemoryCollection(dict).Build());
    }

    private (FallbackLlmSessionAdapter Sut, FakeInnerAdapter Inner) BuildSut(
        ClaudeSubscriptionPool pool, string[]? chain = null)
    {
        var session = new Session { Model = "sonnet", Provider = "acc-a" };
        var inner = new FakeInnerAdapter(session);
        var sut = new FallbackLlmSessionAdapter(inner,
            () => session.Model,
            msg => { lock (_downstream) _downstream.Add(msg); return Task.CompletedTask; },
            pool, BuildProviders(), Path.GetTempPath(), launcher: null, initialProfileRoot: null,
            effectiveChain: chain is null ? null : () => chain,
            events: new TurnEventBus());
        inner.Sink = sut.HandleMessageAsync;
        return (sut, inner);
    }

    private List<ServerMessage> Downstream()
    {
        lock (_downstream) return [.. _downstream];
    }

    private async Task WaitForAsync(Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return;
            await Task.Delay(10);
        }
        throw new TimeoutException($"не дождались: {what}");
    }

    private static string KilledDetails =>
        $"{TurnErrorClassifier.ProcessKilledMarker} процесс CLI убит сигналом (exit=137)";

    [Fact]
    public void Классификатор_МаркерГибелиПроцесса_ProcessKilled_АНеUnreachable()
    {
        TurnErrorClassifier.Classify(new TurnAttemptOutcome { HasResult = false, ErrorText = KilledDetails })
            .Should().Be(FallbackErrorClass.ProcessKilled);
        TurnErrorClassifier.WireName(FallbackErrorClass.ProcessKilled).Should().Be("process_killed");
    }

    [Fact]
    public void Классификатор_ОбычныйОбрывБезСигнала_ОстаётсяUnreachable()
    {
        TurnErrorClassifier.Classify(new TurnAttemptOutcome
            { HasResult = false, ErrorText = "Процесс модели завершился во время хода — ответ не был получен" })
            .Should().Be(FallbackErrorClass.Unreachable);
        // Маркер, процитированный не первым токеном, гибелью не считается
        TurnErrorClassifier.Classify(new TurnAttemptOutcome
            { HasResult = false, ErrorText = "см. " + KilledDetails })
            .Should().Be(FallbackErrorClass.Unreachable);
    }

    [Fact]
    public async Task ПроцессУбитСигналом_ПараНеМеняется_ЧестнаяОшибкаБезПодмены()
    {
        var pool = BuildPool("acc-a", "acc-b");
        var (sut, inner) = BuildSut(pool, chain: ["sonnet", "m1"]);
        inner.Scripts.Enqueue(() =>
        {
            inner.Emit(new ErrorMessage(TurnFailureText.ProcessKilled, Details: KilledDetails));
            inner.Emit(new ExitedMessage());
        });
        inner.Scripts.Enqueue(() => inner.Emit(new ResultMessage("success", 10, 1, null, null)));

        await sut.SendMessageAsync("сделай что-нибудь");
        await WaitForAsync(() => Downstream().OfType<ResultMessage>().Any(), "финальный result");

        inner.Attempts.Should().HaveCount(1, "oom-kill на нашей машине смена пары не лечит");
        Downstream().OfType<ProviderSwitchedMessage>().Should().BeEmpty();
        pool.IsExhausted("acc-a").Should().BeFalse();
        var error = Downstream().OfType<ErrorMessage>().Should().ContainSingle().Subject;
        error.Text.Should().Be(TurnFailureText.ProcessKilled);
        error.Details.Should().NotContain(TurnErrorClassifier.ProcessKilledMarker, "служебный маркер человеку не показываем");
        Downstream().OfType<ResultMessage>().Should().ContainSingle()
            .Which.Subtype.Should().Be("error", "ход не состоялся — статус обязан это показать");
    }

    [Fact]
    public async Task НенастроенныйПровайдер_ПропускаетсяВЦепочке_ХодИдётНаСледующийШаг()
    {
        var pool = BuildPool("acc-a");
        // m2 принадлежит p2 без ключа — между исходной парой и рабочим m1
        var (sut, inner) = BuildSut(pool, chain: ["sonnet", "m2", "m1"]);
        inner.Scripts.Enqueue(() => inner.Emit(new ExitedMessage()));
        inner.Scripts.Enqueue(() => inner.Emit(new ResultMessage("success", 10, 1, null, null)));

        await sut.SendMessageAsync("сделай что-нибудь");
        await WaitForAsync(() => Downstream().OfType<ResultMessage>().Any(), "финальный result");

        inner.Attempts.Should().HaveCount(2);
        inner.Attempts[1].Provider.Should().Be("p1", "p2 без ApiKey пропущен, а не опробован");
        inner.Attempts.Should().NotContain(a => a.Provider == "p2");
    }

    [Fact]
    public async Task ЦепочкаИсчерпана_БезНастроенныхШагов_ТекстНеЛжётПроНеотвечающийСервис()
    {
        var pool = BuildPool("acc-a");
        var (sut, inner) = BuildSut(pool, chain: ["sonnet", "m2"]);
        inner.Scripts.Enqueue(() => inner.Emit(new ExitedMessage()));

        await sut.SendMessageAsync("сделай что-нибудь");
        await WaitForAsync(() => Downstream().OfType<ResultMessage>().Any(), "финальный result");

        inner.Attempts.Should().HaveCount(1, "единственный шаг дальше не настроен — попытки на нём нет");
        Downstream().OfType<ResultMessage>().Should().ContainSingle().Which.Subtype.Should().Be("error");
    }
}
