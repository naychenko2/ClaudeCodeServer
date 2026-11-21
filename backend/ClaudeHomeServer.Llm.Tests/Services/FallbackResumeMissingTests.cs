using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Llm;
using ClaudeHomeServer.Services.Llm.Claude;
using ClaudeHomeServer.Services.Turn;
using FluentAssertions;
using Microsoft.Extensions.Configuration;

namespace ClaudeHomeServer.Tests.Services;

/// <summary>
/// «No conversation found with session ID» в stderr (exit=1 до первого события) — транскрипт
/// resume не найден. Инцидент 21.11: у чатов отключённой подписки пула CLI стартовал в ~/.claude,
/// ход классифицировался как Unreachable, и цепочка фолбэка жгла 5 попыток за 12 с — смена модели
/// отсутствующий транскрипт не лечит. Здесь: классификатор на СЫРОМ тексте даёт отдельный класс
/// без фолбэка, ProcessDeathMessage несёт stderr в Details, адаптер завершает ход одной честной
/// ошибкой TurnFailureText.ResumeTranscriptMissing.
/// </summary>
public class FallbackResumeMissingTests
{
    private readonly List<ServerMessage> _downstream = [];
    // Взводится приёмником на первом ResultMessage: ждём событие, а не опрашиваем список по часам
    private readonly TaskCompletionSource _finalResult = new(TaskCreationOptions.RunContinuationsAsynchronously);

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
        public void Interrupt() { }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static ClaudeSubscriptionPool BuildPool(params string[] keys)
    {
        var dict = new Dictionary<string, string?>();
        foreach (var key in keys) dict[$"ClaudeSubscriptions:{key}:OAuthToken"] = $"token-{key}";
        return new ClaudeSubscriptionPool(new ConfigurationBuilder().AddInMemoryCollection(dict).Build());
    }

    private static LlmProviderRegistry BuildProviders()
    {
        var dict = new Dictionary<string, string?>
        {
            ["LlmProviders:p1:ApiKey"] = "sk-1",
            ["LlmProviders:p1:AnthropicBaseUrl"] = "https://p1.example.com",
            ["LlmProviders:p1:Models:0:Id"] = "m1",
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
            msg =>
            {
                lock (_downstream) _downstream.Add(msg);
                if (msg is ResultMessage) _finalResult.TrySetResult();
                return Task.CompletedTask;
            },
            pool, BuildProviders(), Path.GetTempPath(), launcher: null, initialProfileRoot: null,
            isChildTurn: () => false,
            effectiveChain: chain is null ? null : () => chain,
            events: new TurnEventBus());
        inner.Sink = sut.HandleMessageAsync;
        return (sut, inner);
    }

    private List<ServerMessage> Downstream()
    {
        lock (_downstream) return [.. _downstream];
    }

    // Потолок 30 с — страховка от зависания, а не проверяемая скорость
    private async Task WaitFinalResultAsync()
    {
        try
        {
            await _finalResult.Task.WaitAsync(TimeSpan.FromSeconds(30));
        }
        catch (TimeoutException)
        {
            throw new TimeoutException("не дождались: финальный result");
        }
    }

    // Канонический stderr CLI при провале --resume (инцидент 21.11, чат d8c46c2a)
    private const string StderrNoConversation =
        "No conversation found with session ID: 3f487ee3-e49e-4dde-acae-0b213dc9aa7f\n"
        + "Run without --resume to start a new conversation.";

    [Fact]
    public void Классификатор_МаркерВСыромТексте_ResumeTranscriptMissing_АНеUnreachable()
    {
        // Маркер в stderr без result-события — тот самый канал инцидента: раньше уходил
        // в «процесс умер без result → Unreachable» и гонял цепочку
        TurnErrorClassifier.Classify(new TurnAttemptOutcome { HasResult = false, ErrorText = StderrNoConversation })
            .Should().Be(FallbackErrorClass.ResumeTranscriptMissing);
        TurnErrorClassifier.WireName(FallbackErrorClass.ResumeTranscriptMissing)
            .Should().Be("resume_transcript_missing");
        // Маркер в середине текста с префиксом лога — тот же класс (Contains, не префикс)
        TurnErrorClassifier.Classify(new TurnAttemptOutcome
            { HasResult = false, ErrorText = "error: " + StderrNoConversation })
            .Should().Be(FallbackErrorClass.ResumeTranscriptMissing);
    }

    [Fact]
    public void Классификатор_МаркерВОшибкахResult_ТожеРезолвитсяКлассом()
    {
        // Второй канал того же отказа: result с subtype=error_during_execution и маркером
        // в тексте ошибок (TurnFailureText.ForResultErrors). Класс тот же — адаптер
        // подставит формулировку ResumeTranscriptMissing и не пойдёт по цепочке
        TurnErrorClassifier.Classify(new TurnAttemptOutcome
            { HasResult = true, Subtype = "error_during_execution", ErrorText = StderrNoConversation })
            .Should().Be(FallbackErrorClass.ResumeTranscriptMissing);
    }

    [Fact]
    public void Классификатор_ОбычныйОбрывБезМаркера_ОстаётсяUnreachable()
    {
        TurnErrorClassifier.Classify(new TurnAttemptOutcome
            { HasResult = false, ErrorText = "Процесс модели завершился во время хода — ответ не был получен" })
            .Should().Be(FallbackErrorClass.Unreachable);
    }

    [Fact]
    public void ProcessDeath_МаркерВStderrУходитВDetails_НезависимоОтСобытийХода()
    {
        // До первого события хода маркер обязан доехать до Details: классификатор работает
        // с сырым текстом, Text («Процесс модели завершился…») маркер не несёт
        var early = ClaudeSession.ProcessDeathMessage(
            pendingControlResponse: false, exitCode: 1, disposing: false,
            stderrTail: StderrNoConversation, turnGotEvent: false);
        early.Details.Should().Be(StderrNoConversation, "сырой stderr — единственный носитель маркера");

        // Инцидент 21.11: все 5 попыток умирали с gotEvent=True — CLI выдаёт system/hook-события
        // раньше проверки --resume, и гейт по turnGotEvent прятал маркер из Details →
        // Unreachable → цепочка. Маркер канонический (CLI пишет его только про --resume):
        // Details несёт его и на начавшемся ходе, классификатор даёт ResumeTranscriptMissing
        var late = ClaudeSession.ProcessDeathMessage(
            pendingControlResponse: false, exitCode: 1, disposing: false,
            stderrTail: StderrNoConversation, turnGotEvent: true);
        late.Details.Should().Be(StderrNoConversation, "маркер канонический — несём и при turnGotEvent");
        TurnErrorClassifier.Classify(new TurnAttemptOutcome { HasResult = false, ErrorText = late.Details! })
            .Should().Be(FallbackErrorClass.ResumeTranscriptMissing);
    }

    [Fact]
    public async Task ТранскриптНеНайден_ПараНеМеняется_ЧестнаяОшибкаБезПодмены()
    {
        var pool = BuildPool("acc-a", "acc-b");
        // Живая цепочка есть — но гнать её бессмысленно: resume идёт тем же файлом
        var (sut, inner) = BuildSut(pool, chain: ["sonnet", "m1"]);
        inner.Scripts.Enqueue(() =>
        {
            inner.Emit(new ErrorMessage(
                "Процесс модели завершился во время хода — ответ не был получен",
                Details: StderrNoConversation));
            inner.Emit(new ExitedMessage());
        });

        await sut.SendMessageAsync("продолжи");
        await WaitFinalResultAsync();

        inner.Attempts.Should().HaveCount(1, "отсутствующий транскрипт смена пары не лечит");
        Downstream().OfType<ProviderSwitchedMessage>().Should().BeEmpty();
        pool.IsExhausted("acc-a").Should().BeFalse("провал не в лимите подписки");
        var error = Downstream().OfType<ErrorMessage>().Should().ContainSingle().Subject;
        error.Text.Should().Be(TurnFailureText.ResumeTranscriptMissing);
        error.Details.Should().Contain("No conversation found",
            "сырой stderr остаётся человеку в «Подробностях»");
        Downstream().OfType<ResultMessage>().Should().ContainSingle()
            .Which.Subtype.Should().Be("error", "ход не состоялся — статус обязан это показать");
    }
}
