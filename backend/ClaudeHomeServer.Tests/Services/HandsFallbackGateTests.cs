using System.Diagnostics;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Llm;
using FluentAssertions;
using Microsoft.Extensions.Configuration;

namespace ClaudeHomeServer.Tests.Services;

// Фолбэк хода с руками (ADR-016 §7, план рук Ш4): шаг к провайдеру — это копия транскрипта со
// снимками окон ему, поэтому цепочка режется до провайдеров, которым владелец доверил руки.
// Идти некуда — ошибка «руки разрешены только для …», а не уход к чужому вендору.
// Проверяется поведением адаптера (калька DesktopFallbackGateTests).
public class HandsFallbackGateTests
{
    private readonly List<ServerMessage> _downstream = [];

    // Фейковый внутренний адаптер: на каждую попытку хода отыгрывает свой скрипт событий
    // (калька FakeInnerAdapter из FallbackLlmSessionAdapterTests — тот private в своём классе).
    private sealed class FakeInnerAdapter(Session info) : ILlmSessionAdapter
    {
        public Session Info { get; } = info;
        public Func<ServerMessage, Task>? Sink;
        public Queue<Action> Scripts { get; } = new();
        // (Provider, Model) на момент запуска каждой попытки
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
            int agentDepth = 0, bool suppressTasksExecute = false)
        {
            SubmittedTurnSeq++;
            lock (Attempts) Attempts.Add((Info.Provider ?? "", Info.Model));
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
        foreach (var key in keys)
            dict[$"ClaudeSubscriptions:{key}:OAuthToken"] = $"token-{key}";
        return new ClaudeSubscriptionPool(new ConfigurationBuilder().AddInMemoryCollection(dict).Build());
    }

    // Сторонние провайдеры p1..pN с моделями m1..mN — шаги цепочки, ради которых чат
    // и уехал бы к чужому вендору.
    private static LlmProviderRegistry BuildChainProviders(int count)
    {
        var dict = new Dictionary<string, string?>();
        for (var i = 1; i <= count; i++)
        {
            dict[$"LlmProviders:p{i}:ApiKey"] = $"sk-{i}";
            dict[$"LlmProviders:p{i}:AnthropicBaseUrl"] = $"https://p{i}.example.com";
            dict[$"LlmProviders:p{i}:Models:0:Id"] = $"m{i}";
        }
        return new LlmProviderRegistry(new ConfigurationBuilder().AddInMemoryCollection(dict).Build());
    }

    private (FallbackLlmSessionAdapter Sut, FakeInnerAdapter Inner) BuildSut(
        LlmProviderRegistry providers, string[] chain, bool handsEnabled, IReadOnlyList<string>? handsProviders)
    {
        var session = new Session { Model = chain[0], Provider = "acc-a" };
        var inner = new FakeInnerAdapter(session);
        var sut = new FallbackLlmSessionAdapter(inner,
            () => session.Model,
            msg => { lock (_downstream) _downstream.Add(msg); return Task.CompletedTask; },
            BuildPool("acc-a"), providers, Path.GetTempPath(), launcher: null, initialProfileRoot: null,
            effectiveChain: () => chain, handsEnabled: handsEnabled, handsProviders: handsProviders);
        inner.Sink = sut.HandleMessageAsync;
        return (sut, inner);
    }

    private static ResultMessage Success() => new("success", 100, 1, null, null);
    private static ResultMessage ApiError(string status) => new("success", 100, 1, null, null, ApiErrorStatus: status);

    // Провал доставки одной попытки: is_error-текст ПЕРЕД result — как шлёт CLI
    private static void EmitRateLimit(FakeInnerAdapter inner)
    {
        inner.Emit(new ErrorMessage("API Error: 429 rate limit", ExpectResultFollows: true));
        inner.Emit(ApiError("429"));
    }

    private List<ServerMessage> Downstream()
    {
        lock (_downstream) return [.. _downstream];
    }

    private static async Task WaitForAsync(Func<bool> condition, string what, int timeoutMs = 5000)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            if (condition()) return;
            await Task.Delay(20);
        }
        throw new TimeoutException($"Не дождался: {what}");
    }

    [Fact]
    public async Task РукиКClaude_СторонниеШагиВырезаны_ХодНаСледующейМоделиПула()
    {
        var (sut, inner) = BuildSut(BuildChainProviders(1), ["sonnet", "m1", "opus"],
            handsEnabled: true, handsProviders: [HandsProviders.Claude]);
        inner.Scripts.Enqueue(() => EmitRateLimit(inner));
        inner.Scripts.Enqueue(() => inner.Emit(Success()));

        await sut.SendMessageAsync("открой отчёт");
        await WaitForAsync(() => Downstream().OfType<ResultMessage>().Any(), "финальный result");

        inner.Attempts.Should().HaveCount(2);
        inner.Attempts[1].Model.Should().Be("opus");
        inner.Attempts.Should().NotContain(a => a.Provider == "p1" || a.Model == "m1",
            "провайдеру вне списка руки не доверены");
    }

    [Fact]
    public async Task РукиКДоверенномуСтороннему_ШагОстаётся()
    {
        var (sut, inner) = BuildSut(BuildChainProviders(2), ["sonnet", "m1", "m2"],
            handsEnabled: true, handsProviders: [HandsProviders.Claude, "p2"]);
        inner.Scripts.Enqueue(() => EmitRateLimit(inner));
        inner.Scripts.Enqueue(() => inner.Emit(Success()));

        await sut.SendMessageAsync("нажми «Сохранить»");
        await WaitForAsync(() => Downstream().OfType<ResultMessage>().Any(), "финальный result");

        inner.Attempts.Should().HaveCount(2);
        inner.Attempts[1].Provider.Should().Be("p2", "p1 не доверен и вырезан, p2 доверен");
    }

    [Fact]
    public async Task РукиИдтиНекуда_ОшибкаРукиРазрешеныТолькоДля_БезМиграции()
    {
        var (sut, inner) = BuildSut(BuildChainProviders(2), ["sonnet", "m1", "m2"],
            handsEnabled: true, handsProviders: [HandsProviders.Claude]);
        inner.Scripts.Enqueue(() => EmitRateLimit(inner));

        await sut.SendMessageAsync("закрой окно");
        await WaitForAsync(() => Downstream().OfType<ResultMessage>().Any(), "финальный result");

        inner.Attempts.Should().HaveCount(1);
        Downstream().OfType<ProviderSwitchedMessage>().Should().BeEmpty();
        Downstream().OfType<ErrorMessage>().Should().ContainSingle()
            .Which.Text.Should().Be(HandsTurnRules.FallbackNowhereText(["Claude"]));
        Downstream().OfType<ResultMessage>().Last().Subtype.Should().Be("error");
        sut.Info.Model.Should().Be("sonnet");
    }

    [Fact]
    public async Task БезРук_ТаЖеЦепочка_УходитНаСтороннего()
    {
        // Контроль: без признака рук цепочка не режется — иначе тесты выше зелёные и при
        // сломанном шлюзе
        var (sut, inner) = BuildSut(BuildChainProviders(1), ["sonnet", "m1", "opus"],
            handsEnabled: false, handsProviders: [HandsProviders.Claude]);
        inner.Scripts.Enqueue(() => EmitRateLimit(inner));
        inner.Scripts.Enqueue(() => inner.Emit(Success()));

        await sut.SendMessageAsync("сделай что-нибудь");
        await WaitForAsync(() => Downstream().OfType<ResultMessage>().Any(), "финальный result");

        inner.Attempts[1].Provider.Should().Be("p1");
    }
}
