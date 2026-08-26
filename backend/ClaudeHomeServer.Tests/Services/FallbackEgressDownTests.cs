using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Llm;
using FluentAssertions;
using Microsoft.Extensions.Configuration;

namespace ClaudeHomeServer.Tests.Services;

/// <summary>
/// Отказ ВЫХОДА В СЕТЬ против отказа провайдера. Класс ошибки один (Unreachable), а лечение
/// противоположное: мёртвый эндпоинт вендора чинится сменой пары «модель × подписка», мёртвый
/// общий канал — только повтором, потому что все шаги цепочки идут через тот же прокси.
///
/// Разбор суток 25.08.2026: 10 из 14 показанных человеку ошибок — один ConnectionRefused сразу
/// по трём вендорам (claude, alibabacloud, glm). Цепочка при этом честно жгла шаги, а сторонний
/// провайдер получал кулдаун за чужую вину.
/// </summary>
public class FallbackEgressDownTests
{
    private readonly List<ServerMessage> _downstream = [];

    // Подставная проба канала: живой TCP тут не нужен, проверяется РЕАКЦИЯ на её вердикт
    private sealed class FakeEgress(bool down) : IEgressProbe
    {
        public bool Down { get; set; } = down;
        public int Calls { get; private set; }

        public Task<bool> IsDownAsync(CancellationToken ct = default)
        {
            Calls++;
            return Task.FromResult(Down);
        }
    }

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
        public bool IsContinuationInFlight => false;
        public long SubmittedTurnSeq { get; private set; }

        public Task SendMessageAsync(string text, IReadOnlyList<string>? attachedPaths = null,
            int agentDepth = 0, bool suppressTasksExecute = false)
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
        public Action? OnInterrupt;
        public void Interrupt() { Interrupts++; OnInterrupt?.Invoke(); }
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
        ClaudeSubscriptionPool pool, IEgressProbe egress,
        ProviderHealthRegistry? health = null, TurnRunLog? turnRuns = null,
        string[]? chain = null)
    {
        var session = new Session { Model = "sonnet", Provider = "acc-a" };
        var inner = new FakeInnerAdapter(session);
        var sut = new FallbackLlmSessionAdapter(inner,
            () => session.Model,
            msg => { lock (_downstream) _downstream.Add(msg); return Task.CompletedTask; },
            pool, BuildProviders(), Path.GetTempPath(), launcher: null, initialProfileRoot: null,
            effectiveChain: chain is null ? null : () => chain,
            health: health,
            egress: egress,
            turnRuns: turnRuns,
            // Продовые 5 с в тесте ждать нельзя — важна логика повтора, а не длина паузы
            egressRetryDelay: TimeSpan.FromMilliseconds(10));
        inner.Sink = sut.HandleMessageAsync;
        return (sut, inner);
    }

    private List<ServerMessage> Downstream()
    {
        lock (_downstream) return [.. _downstream];
    }

    private async Task WaitForAsync(Func<bool> condition, string what)
    {
        // Ждём СОБЫТИЕ опросом с потолком, а не фиксированный Delay: на слабом Linux-раннере
        // CI фиксированная пауза даёт ложные падения (конвенция проекта о таймингах)
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return;
            await Task.Delay(10);
        }
        throw new TimeoutException($"не дождались: {what}");
    }

    [Fact]
    public async Task КаналЛёг_ПовторТойЖеПары_БезРотацииПодписки()
    {
        var pool = BuildPool("acc-a", "acc-b");
        var egress = new FakeEgress(down: true);
        var (sut, inner) = BuildSut(pool, egress);
        // Первая попытка: процесс умер без result — Unreachable
        inner.Scripts.Enqueue(() => inner.Emit(new ExitedMessage()));
        // Вторая (повтор) — канал поднялся, ход прошёл
        inner.Scripts.Enqueue(() =>
        {
            egress.Down = false;
            inner.Emit(new ResultMessage("success", 10, 1, null, null));
        });

        await sut.SendMessageAsync("сделай что-нибудь");
        await WaitForAsync(() => Downstream().OfType<ResultMessage>().Any(), "финальный result");

        inner.Attempts.Should().HaveCount(2);
        inner.Attempts[1].Should().Be(inner.Attempts[0],
            "канал общий для всех пар — повторяем ТУ ЖЕ, а не крутим ротацию подписок");
        pool.IsExhausted("acc-a").Should().BeFalse("сеть лежала — подписка ни при чём");
        Downstream().OfType<ResultMessage>().Should().ContainSingle()
            .Which.Subtype.Should().Be("success");
    }

    [Fact]
    public async Task КаналЛёг_ПовторНеПомог_ЧестнаяОшибкаБезПереборЦепочки()
    {
        var pool = BuildPool("acc-a", "acc-b");
        var health = new ProviderHealthRegistry();
        var egress = new FakeEgress(down: true);
        // Цепочка есть и она длинная — но перебирать её через мёртвый канал бессмысленно
        var (sut, inner) = BuildSut(pool, egress, health, chain: ["sonnet", "m1"]);
        for (var i = 0; i < 5; i++)
            inner.Scripts.Enqueue(() => inner.Emit(new ExitedMessage()));

        await sut.SendMessageAsync("сделай что-нибудь");
        await WaitForAsync(() => Downstream().OfType<ResultMessage>().Any(), "финальный result");

        inner.Attempts.Should().HaveCount(2, "исходная попытка + один повтор, дальше — отказ");
        health.IsUnavailable("p1").Should().BeFalse("шаг цепочки даже не пробовался");
        pool.IsExhausted("acc-a").Should().BeFalse();
        Downstream().OfType<ProviderSwitchedMessage>().Should().BeEmpty("подмены не было");

        var error = Downstream().OfType<ErrorMessage>().Should().ContainSingle().Subject;
        error.Text.Should().Be(TurnFailureText.EgressDown);
        Downstream().OfType<ResultMessage>().Should().ContainSingle()
            .Which.Subtype.Should().Be("error", "ход не состоялся — статус обязан это показать");
    }

    [Fact]
    public async Task КаналЖив_ПоведениеПрежнее_РотацияПодписки()
    {
        // Контрольный тест: проба говорит «канал жив» — значит Unreachable настоящий,
        // и фолбэк обязан работать ровно как до правки
        var pool = BuildPool("acc-a", "acc-b");
        var (sut, inner) = BuildSut(pool, new FakeEgress(down: false));
        inner.Scripts.Enqueue(() => inner.Emit(new ExitedMessage()));
        inner.Scripts.Enqueue(() => inner.Emit(new ResultMessage("success", 10, 1, null, null)));

        await sut.SendMessageAsync("сделай что-нибудь");
        await WaitForAsync(() => Downstream().OfType<ResultMessage>().Any(), "финальный result");

        inner.Attempts.Should().HaveCount(2);
        inner.Attempts[1].Provider.Should().Be("acc-b", "эндпоинт мёртв — уходим на другую подписку");
    }

    [Fact]
    public async Task ПробаНеСпрашиваетсяНаНеСетевыхКлассах()
    {
        // Проба стоит на пути ошибки и делает I/O: дёргать её на 429 незачем —
        // класс ошибки к каналу отношения не имеет
        var pool = BuildPool("acc-a", "acc-b");
        var egress = new FakeEgress(down: true);
        var (sut, inner) = BuildSut(pool, egress);
        inner.Scripts.Enqueue(() =>
        {
            inner.Emit(new ErrorMessage("API Error: 429 rate limit", ExpectResultFollows: true));
            inner.Emit(new ResultMessage("error", 1, 1, null, null, ApiErrorStatus: "429"));
        });
        inner.Scripts.Enqueue(() => inner.Emit(new ResultMessage("success", 10, 1, null, null)));

        await sut.SendMessageAsync("сделай что-нибудь");
        await WaitForAsync(() => Downstream().OfType<ResultMessage>().Any(), "финальный result");

        egress.Calls.Should().Be(0, "429 — не сетевой класс");
        inner.Attempts[1].Provider.Should().Be("acc-b");
    }

    [Fact]
    public async Task Стоп_ВоВремяПаузыПовтора_ХодЗакрывается_АНеВиснет()
    {
        // Блокер ревью: пауза перед повтором длится секунды, и это ровно тот момент, когда
        // человек жмёт «Стоп» — ход стоит, ошибка задержана. Если заглушить терминалы ДО паузы,
        // exited убитого процесса проглатывается, SettleAsync отдаёт пустоту, и чат остаётся
        // Working навсегда (sweep лечит только Active).
        var pool = BuildPool("acc-a");
        var (sut, inner) = BuildSut(pool, new FakeEgress(down: true));
        // Убийство процесса по «Стоп» досылает терминал — как настоящий ClaudeSession
        inner.OnInterrupt = () => inner.Emit(new ExitedMessage());
        inner.Scripts.Enqueue(() => inner.Emit(new ExitedMessage()));

        await sut.SendMessageAsync("сделай что-нибудь");
        // Ждём саму паузу повтора: попытка провалилась, оркестратор ушёл спать
        await WaitForAsync(() => inner.Attempts.Count == 1, "первая попытка");
        sut.Interrupt();

        await WaitForAsync(() => Downstream().OfType<ExitedMessage>().Any(),
            "терминал хода дошёл наружу");
        inner.Interrupts.Should().Be(1);
    }

    [Fact]
    public async Task ФиналОтказаКанала_НесётПаруОшибкаResult_ИСохраняетДанныеПопытки()
    {
        // ExpectResultFollows: по этой паре SessionManager разбирает конец хода штаба РОВНО ОДИН
        // раз — без флага командный чат получил бы две карточки эскалации на один ход.
        // Usage/стоимость реального result попытки при этом теряться не должны: отказной запрос
        // мог потратить токены (тот же довод, что в FailClosedAsync).
        var pool = BuildPool("acc-a");
        var (sut, inner) = BuildSut(pool, new FakeEgress(down: true));
        for (var i = 0; i < 3; i++)
            inner.Scripts.Enqueue(() =>
            {
                inner.Emit(new ErrorMessage("API Error: Connection refused (ECONNREFUSED)",
                    ExpectResultFollows: true));
                inner.Emit(new ResultMessage("error", 4242, 7, null, 0.5, ApiErrorStatus: "process_exit"));
            });

        await sut.SendMessageAsync("сделай что-нибудь");
        await WaitForAsync(() => Downstream().OfType<ResultMessage>().Any(), "финальный result");

        var error = Downstream().OfType<ErrorMessage>().Should().ContainSingle().Subject;
        error.Text.Should().Be(TurnFailureText.EgressDown);
        error.ExpectResultFollows.Should().BeTrue("пара «ошибка → result» — часть контракта конца хода");
        error.Details.Should().Contain("ECONNREFUSED", "сырой текст живёт в «Подробностях»");

        var result = Downstream().OfType<ResultMessage>().Should().ContainSingle().Subject;
        result.Subtype.Should().Be("error");
        result.DurationMs.Should().Be(4242, "данные реальной попытки не теряются");
        result.NumTurns.Should().Be(7);
        result.TotalCostUsd.Should().Be(0.5);
    }

    [Fact]
    public async Task ПаспортХода_ОтличаетОтказКаналаОтОтказаВендора()
    {
        var pool = BuildPool("acc-a");
        var runs = new TurnRunLog();
        var (sut, inner) = BuildSut(pool, new FakeEgress(down: true), turnRuns: runs);
        for (var i = 0; i < 3; i++)
            inner.Scripts.Enqueue(() => inner.Emit(new ExitedMessage()));

        await sut.SendMessageAsync("сделай что-нибудь");
        await WaitForAsync(() => runs.Recent(1).Count > 0, "паспорт хода");

        var passport = runs.Recent(1)[0];
        passport.Outcome.Should().Be("egress_down");
        passport.Failed.Should().BeTrue();
        passport.EgressRetries.Should().Be(FallbackLlmSessionAdapter.MaxEgressRetries);
        passport.LastErrorClass.Should().Be("unreachable");
        runs.Summary().ByOutcome.Should().Contain(s => s.Outcome == "egress_down" && s.Turns == 1);
    }

    [Fact]
    public async Task ПаспортХода_УспехТожеПишется()
    {
        // Журнал нужен не только для сбоев: без знаменателя доля отказов не считается
        var pool = BuildPool("acc-a");
        var runs = new TurnRunLog();
        var (sut, inner) = BuildSut(pool, new FakeEgress(down: false), turnRuns: runs);
        inner.Scripts.Enqueue(() => inner.Emit(new ResultMessage("success", 10, 1, null, null)));

        await sut.SendMessageAsync("сделай что-нибудь");
        await WaitForAsync(() => runs.Recent(1).Count > 0, "паспорт хода");

        var passport = runs.Recent(1)[0];
        passport.Outcome.Should().Be("success");
        passport.Failed.Should().BeFalse();
        passport.Attempts.Should().Be(1);
        passport.LastErrorClass.Should().BeNull();
    }
}
