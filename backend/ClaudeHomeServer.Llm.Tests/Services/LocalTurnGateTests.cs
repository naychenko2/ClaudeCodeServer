using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Execution;
using ClaudeHomeServer.Services.Llm;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.Configuration;

namespace ClaudeHomeServer.Tests.Services;

/// <summary>
/// Гейт допуска ходов к локальному движку (B1b-1): потолок N одновременных попыток на локальном
/// провайдере, ограниченная очередь FIFO, таймаут. «Занято» — отдельный класс отказа: без
/// LocalModelDown, без смены пары и без пометки провайдера в _health (линия ADR-007).
/// </summary>
public class LocalTurnGateTests
{
    private const string Local = "local-qwen";
    private const string LocalModel = "qwen38-27b";

    // Часы, которые двигает тест: таймер срабатывает только по Advance — без Task.Delay.
    private sealed class ManualTime : TimeProvider
    {
        private readonly List<ManualTimer> _timers = [];
        private DateTimeOffset _now = new(2026, 10, 26, 12, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _now;

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var t = new ManualTimer(callback, state, _now + dueTime);
            lock (_timers) _timers.Add(t);
            return t;
        }

        public void Advance(TimeSpan by)
        {
            List<ManualTimer> due;
            lock (_timers)
            {
                _now += by;
                due = _timers.Where(t => !t.Fired && !t.Disposed && t.Due <= _now).ToList();
            }
            foreach (var t in due) t.Fire();
        }

        private sealed class ManualTimer(TimerCallback callback, object? state, DateTimeOffset due) : ITimer
        {
            public DateTimeOffset Due { get; } = due;
            public bool Fired { get; private set; }
            public bool Disposed { get; private set; }
            public void Fire() { Fired = true; callback(state); }
            public bool Change(TimeSpan dueTime, TimeSpan period) => false;
            public void Dispose() => Disposed = true;
            public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
        }
    }

    private sealed class FakeInner(Session info) : ILlmSessionAdapter
    {
        public Session Info { get; } = info;
        public Func<ServerMessage, Task>? Sink;
        public List<string> Attempts { get; } = [];
        public Queue<Action> Scripts { get; } = new();
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Exception? ThrowOnSend;

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
            if (ThrowOnSend is not null) throw ThrowOnSend;
            SubmittedTurnSeq++;
            Attempts.Add(Info.Provider ?? "");
            Started.TrySetResult();
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

    private sealed class Turn
    {
        public required FallbackLlmSessionAdapter Sut { get; init; }
        public required FakeInner Inner { get; init; }
        public List<ServerMessage> Out { get; init; } = [];
        public List<ServerMessage> Downstream() { lock (Out) return [.. Out]; }
        public bool Started => Inner.Started.Task.IsCompleted;
        public void FinishOk() => Inner.Emit(new ResultMessage("success", 10, 1, null, null));
    }

    private readonly ManualTime _clock = new();
    private readonly ProviderHealthRegistry _health = new();

    private static LlmProviderRegistry Providers(int max, int queued, string timeout) =>
        new(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            [$"LlmProviders:{Local}:AnthropicBaseUrl"] = "http://127.0.0.1:8080",
            [$"LlmProviders:{Local}:IsLocal"] = "true",
            [$"LlmProviders:{Local}:MaxConcurrentTurns"] = max.ToString(),
            [$"LlmProviders:{Local}:MaxQueuedTurns"] = queued.ToString(),
            [$"LlmProviders:{Local}:TurnQueueTimeout"] = timeout,
            [$"LlmProviders:{Local}:Models:0:Id"] = LocalModel,
            ["LlmProviders:glm:ApiKey"] = "zai-key",
            ["LlmProviders:glm:AnthropicBaseUrl"] = "https://api.z.ai/api/anthropic",
            ["LlmProviders:glm:Models:0:Id"] = "glm-5.2",
        }).Build());

    private static ClaudeSubscriptionPool Pool() => new(new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?> { ["ClaudeSubscriptions:acc-a:OAuthToken"] = "t" }).Build());

    private Turn NewTurn(LocalEngineBusyTracker gate, LlmProviderRegistry providers, string[]? chain = null)
    {
        var session = new Session { Model = LocalModel, Provider = Local };
        var inner = new FakeInner(session);
        var outList = new List<ServerMessage>();
        var sut = new FallbackLlmSessionAdapter(inner, () => session.Model,
            msg => { lock (outList) outList.Add(msg); return Task.CompletedTask; },
            Pool(), providers, Path.GetTempPath(), launcher: null, initialProfileRoot: null,
            effectiveChain: chain is null ? null : () => chain,
            health: _health, egressRetryDelay: TimeSpan.FromMilliseconds(10), localGate: gate);
        inner.Sink = sut.HandleMessageAsync;
        return new Turn { Inner = inner, Sut = sut, Out = outList };
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

    [Fact]
    public async Task ДесятьХодовПриN3_ЗапущеноНеБолееТрёх_СемеройЖдут()
    {
        var gate = new LocalEngineBusyTracker(_clock);
        var providers = Providers(3, 8, "00:15:00");
        var turns = Enumerable.Range(0, 10).Select(_ => NewTurn(gate, providers)).ToList();

        foreach (var t in turns) await t.Sut.SendMessageAsync("ход");
        await WaitForAsync(() => gate.ActiveFor(Local) == 3 && gate.QueuedFor(Local) == 7, "3 в работе, 7 в очереди");

        turns.Count(t => t.Started).Should().Be(3, "в фейковом лаунчере запущено не больше потолка");

        // Освобождаем по одному: слот переходит голове очереди, одновременно не больше трёх.
        var finished = new HashSet<Turn>();
        while (finished.Count < 10)
        {
            var running = turns.Where(t => t.Started && !finished.Contains(t)).ToList();
            running.Count.Should().BeLessThanOrEqualTo(3);
            running.Should().NotBeEmpty();
            var next = running[0];
            next.FinishOk();
            finished.Add(next);
            var all = finished.Count;
            await WaitForAsync(() => all == 10 || turns.Count(t => t.Started && !finished.Contains(t)) == Math.Min(3, 10 - all),
                "следующий ход из очереди стартовал");
        }
        // Слот снимается асинхронно по терминалу попытки — ждём, а не читаем сразу.
        await WaitForAsync(() => gate.Active == 0 && turns.All(t => t.Downstream().OfType<ResultMessage>().Any()),
            "все слоты освобождены, все ходы завершены");
        turns.Should().OnlyContain(t => t.Downstream().OfType<ResultMessage>().Any(r => r.Subtype == "success"));
    }

    [Fact]
    public async Task ОчередьПереполнена_ErrorLocalEngineBusy_БезПодменыИКулдауна()
    {
        var gate = new LocalEngineBusyTracker(_clock);
        var providers = Providers(1, 1, "00:15:00");
        var chain = new[] { LocalModel, "glm-5.2" };   // облачный сосед есть, но ходить к нему нельзя
        var a = NewTurn(gate, providers, chain);
        var b = NewTurn(gate, providers, chain);
        var c = NewTurn(gate, providers, chain);

        await a.Sut.SendMessageAsync("a");
        await WaitForAsync(() => a.Started, "A стартовал");
        await b.Sut.SendMessageAsync("b");
        await WaitForAsync(() => gate.QueuedFor(Local) == 1, "B в очереди");
        await c.Sut.SendMessageAsync("c");
        await WaitForAsync(() => c.Downstream().OfType<ResultMessage>().Any(), "C получил терминал");

        c.Inner.Attempts.Should().BeEmpty("отказанный ход CLI не запускает");
        var down = c.Downstream();
        down.OfType<ErrorMessage>().Single().Text.Should().Be(TurnFailureText.LocalEngineBusy(1));
        down.OfType<ResultMessage>().Single().Subtype.Should().Be("error");
        down.OfType<ProviderSwitchedMessage>().Should().BeEmpty();
        down.OfType<ErrorMessage>().Should().NotContain(e => e.Text == TurnFailureText.LocalModelDown);
        _health.IsUnavailable(Local).Should().BeFalse("«занято» провайдера не помечает");
        _health.IsUnavailable("glm").Should().BeFalse();
    }

    [Fact]
    public async Task ТаймаутОчереди_ТоЖеЧтоПереполнение()
    {
        var gate = new LocalEngineBusyTracker(_clock);
        var providers = Providers(1, 4, "00:15:00");
        var a = NewTurn(gate, providers, [LocalModel, "glm-5.2"]);
        var b = NewTurn(gate, providers, [LocalModel, "glm-5.2"]);

        await a.Sut.SendMessageAsync("a");
        await WaitForAsync(() => a.Started, "A стартовал");
        await b.Sut.SendMessageAsync("b");
        await WaitForAsync(() => gate.QueuedFor(Local) == 1, "B в очереди");

        _clock.Advance(TimeSpan.FromMinutes(14));
        b.Downstream().OfType<ResultMessage>().Should().BeEmpty("до таймаута ход ждёт");
        _clock.Advance(TimeSpan.FromMinutes(1));
        await WaitForAsync(() => b.Downstream().OfType<ResultMessage>().Any(), "B получил терминал по таймауту");

        b.Inner.Attempts.Should().BeEmpty();
        var down = b.Downstream();
        down.OfType<ErrorMessage>().Single().Text.Should().Be(TurnFailureText.LocalEngineBusy(1));
        down.OfType<ResultMessage>().Single().Subtype.Should().Be("error");
        down.OfType<ProviderSwitchedMessage>().Should().BeEmpty();
        _health.IsUnavailable(Local).Should().BeFalse();
        gate.QueuedFor(Local).Should().Be(0);
        gate.ActiveFor(Local).Should().Be(1, "слот A не тронут");
    }

    [Fact]
    public async Task СтопВОчереди_CliНеСтартовал_МестоОсвобождено_СчётчикНеМенялся()
    {
        var gate = new LocalEngineBusyTracker(_clock);
        var providers = Providers(1, 1, "00:15:00");
        var a = NewTurn(gate, providers);
        var b = NewTurn(gate, providers);
        var c = NewTurn(gate, providers);

        await a.Sut.SendMessageAsync("a");
        await WaitForAsync(() => a.Started, "A стартовал");
        await b.Sut.SendMessageAsync("b");
        await WaitForAsync(() => gate.QueuedFor(Local) == 1, "B в очереди");

        b.Sut.Interrupt();
        await WaitForAsync(() => b.Downstream().OfType<ExitedMessage>().Any(), "B получил терминал остановки");

        b.Inner.Attempts.Should().BeEmpty("CLI остановленного в очереди хода не запускался");
        b.Downstream().OfType<ErrorMessage>().Should().BeEmpty("остановка пользователем — не ошибка");
        gate.QueuedFor(Local).Should().Be(0, "место в очереди освободилось");
        gate.ActiveFor(Local).Should().Be(1, "счётчик не изменился");

        // Освободившееся место действительно пригодно: C встаёт в очередь, а не получает отказ.
        await c.Sut.SendMessageAsync("c");
        await WaitForAsync(() => gate.QueuedFor(Local) == 1, "C встал в очередь");
        c.Downstream().OfType<ErrorMessage>().Should().BeEmpty();
    }

    [Fact]
    public async Task СлотОсвобождаетсяНаИсключении()
    {
        var gate = new LocalEngineBusyTracker(_clock);
        var t = NewTurn(gate, Providers(1, 1, "00:15:00"));
        t.Inner.ThrowOnSend = new InvalidOperationException("сбой запуска");

        await t.Sut.SendMessageAsync("a");
        await WaitForAsync(() => !t.Sut.OrchestrationActive, "оркестрация свернулась");

        gate.Active.Should().Be(0);
        gate.ActiveFor(Local).Should().Be(0);
    }

    [Fact]
    public async Task СлотОсвобождаетсяНаОтмене()
    {
        var gate = new LocalEngineBusyTracker(_clock);
        var t = NewTurn(gate, Providers(1, 1, "00:15:00"));

        await t.Sut.SendMessageAsync("a");
        await WaitForAsync(() => gate.ActiveFor(Local) == 1, "слот взят");

        await t.Sut.DisposeAsync();   // отмена сессии посреди попытки
        await WaitForAsync(() => gate.Active == 0, "слот освобождён после отмены");
    }

    [Fact]
    public async Task ПопыткаУшлаНаНелокальнуюПару_СлотОсвобождёнСразу()
    {
        var gate = new LocalEngineBusyTracker(_clock);
        var t = NewTurn(gate, Providers(1, 1, "00:15:00"), [LocalModel, "glm-5.2"]);
        var activeDuringCloud = -1;
        // Попытка 1 (локаль) обрывается без result → Unreachable → шаг цепочки на glm.
        t.Inner.Scripts.Enqueue(() => t.Inner.Emit(new ExitedMessage()));
        t.Inner.Scripts.Enqueue(() =>
        {
            activeDuringCloud = gate.Active;
            t.Inner.Emit(new ResultMessage("success", 10, 1, null, null));
        });

        await t.Sut.SendMessageAsync("a");
        await WaitForAsync(() => t.Downstream().OfType<ResultMessage>().Any(), "ход завершён");

        t.Inner.Attempts.Should().HaveCount(2);
        activeDuringCloud.Should().Be(0, "пока ход идёт на облачной паре, слот локали не занят");
        gate.Active.Should().Be(0);
    }

    [Fact]
    public async Task БезГейта_СтартуютВсеДесять()
    {
        // Контроль к первому тесту: без гейта (localGate: null) стартуют все десять — значит,
        // потолок там держится именно на допуске.
        var providers = Providers(3, 8, "00:15:00");
        var turns = Enumerable.Range(0, 10).Select(_ =>
        {
            var session = new Session { Model = LocalModel, Provider = Local };
            var inner = new FakeInner(session);
            var sut = new FallbackLlmSessionAdapter(inner, () => session.Model, _ => Task.CompletedTask,
                Pool(), providers, Path.GetTempPath(), launcher: null, initialProfileRoot: null, localGate: null);
            inner.Sink = sut.HandleMessageAsync;
            return (sut, inner);
        }).ToList();

        foreach (var (sut, _) in turns) await sut.SendMessageAsync("ход");
        await WaitForAsync(() => turns.Count(t => t.inner.Started.Task.IsCompleted) == 10, "без гейта стартуют все");
    }

    // --- B1b-2: событие ожидания и one-shot в том же счётчике ---

    [Fact]
    public async Task ХодВОчереди_УходитСобытиеСПозициейИСчётчиком_ПослеСтартаСнимается()
    {
        var gate = new LocalEngineBusyTracker(_clock);
        var providers = Providers(1, 4, "00:15:00");
        var a = NewTurn(gate, providers);
        var b = NewTurn(gate, providers);

        await a.Sut.SendMessageAsync("a");
        await WaitForAsync(() => a.Started, "A стартовал");
        await b.Sut.SendMessageAsync("b");
        await WaitForAsync(() => gate.QueuedFor(Local) == 1, "B в очереди");

        // Событие идёт обычным потоком хода, а не LocalModelLiveMessage (тот под флагом
        // local-model-stats): у адаптера нет зависимости от флага вовсе.
        await WaitForAsync(() => b.Downstream().OfType<LocalTurnQueueMessage>().Any(), "событие ожидания");
        b.Downstream().OfType<LocalTurnQueueMessage>().Single()
            .Should().Be(new LocalTurnQueueMessage(Position: 1, Active: 1, Limit: 1));
        b.Downstream().OfType<LocalModelLiveMessage>().Should().BeEmpty();
        a.Downstream().OfType<LocalTurnQueueMessage>().Should().BeEmpty("A не ждал — строки нет");

        a.FinishOk();
        await WaitForAsync(() => b.Started, "B стартовал из очереди");
        await WaitForAsync(() => b.Downstream().OfType<LocalTurnQueueMessage>().Any(m => m.Position == 0),
            "строка снята при старте");
        b.Downstream().OfType<LocalTurnQueueMessage>().Last().Position.Should().Be(0);
    }

    [Fact]
    public async Task ПозицияСдвигается_ОстальнымВОчередиУходитНоваяПозиция()
    {
        var gate = new LocalEngineBusyTracker(_clock);
        var seen = new List<(int Pos, int Active, int Limit)>();
        using var first = (await gate.AcquireAsync("k", 1, 4, TimeSpan.FromMinutes(1), default)).Slot;
        var w1 = gate.AcquireAsync("k", 1, 4, TimeSpan.FromMinutes(1), default);
        var w2 = gate.AcquireAsync("k", 1, 4, TimeSpan.FromMinutes(1), default,
            (p, a, l) => { lock (seen) seen.Add((p, a, l)); });

        lock (seen) seen.Should().Equal([(2, 1, 1)], "второй в очереди");
        first!.Dispose();
        (await w1).Slot!.Dispose();
        (await w2).Slot.Should().NotBeNull();
        lock (seen) seen.Should().Equal([(2, 1, 1), (1, 1, 1)], "после ухода головы позиция сдвинулась");
    }

    private sealed class MarkerStartException : Exception;

    // Лаунчер, который не запускает процесс: фиксирует, сколько слотов занято В МОМЕНТ запуска.
    private sealed class RecordingLauncher(LocalEngineBusyTracker gate) : IProcessLauncher, ILauncherFactory
    {
        private static IProcessLauncher Real => LocalProcessRunner.Instance;
        public int ActiveAtStart = -1;
        public bool IsSandboxed => Real.IsSandboxed;
        public bool TargetIsWindows => Real.TargetIsWindows;
        public IPathMapper Paths => Real.Paths;
        public string ClaudeCliCommand => Real.ClaudeCliCommand;
        public string HostTempDir => Real.HostTempDir;
        public string? McpApiUrlOverride => Real.McpApiUrlOverride;
        public System.Diagnostics.Process Start(ProcessSpec spec)
        {
            ActiveAtStart = gate.ActiveFor(LocalTurnGateTests.Local);
            throw new MarkerStartException();
        }
        public void Kill(System.Diagnostics.Process process, string? turnId = null) { }
        public int EstimateCommandLineLength(ProcessSpec spec) => 0;
        public IProcessLauncher Local => this;
        public IProcessLauncher ForOwner(string? ownerId) => this;
        public IProcessLauncher ForProject(Project project) => this;
    }

    private (OneShotClaudeRunner Runner, RecordingLauncher Launcher) OneShot(LocalEngineBusyTracker gate)
    {
        var config = TestConfig.Build(new Dictionary<string, string?>
        {
            [$"LlmProviders:{Local}:AnthropicBaseUrl"] = "http://127.0.0.1:8080",
            [$"LlmProviders:{Local}:IsLocal"] = "true",
            [$"LlmProviders:{Local}:MaxConcurrentTurns"] = "1",
            [$"LlmProviders:{Local}:Models:0:Id"] = LocalModel,
        });
        var launcher = new RecordingLauncher(gate);
        return (new OneShotClaudeRunner(new LlmProviderRegistry(config), launcher, config, localGate: gate), launcher);
    }

    [Fact]
    public async Task OneShotНаЛокали_ДелитОдинСчётчикСХодами_ЗанятоОтказБезЗапуска()
    {
        var gate = new LocalEngineBusyTracker(_clock);
        var (runner, launcher) = OneShot(gate);
        using var turnSlot = (await gate.AcquireAsync(Local, 1, 4, TimeSpan.FromMinutes(1), default)).Slot;

        var act = () => runner.RunAsync("p", LocalModel);

        await act.Should().ThrowAsync<LocalEngineBusyException>("ход держит единственный слот провайдера");
        launcher.ActiveAtStart.Should().Be(-1, "CLI one-shot не запускался");
        gate.ActiveFor(Local).Should().Be(1, "отказанный one-shot счётчик не трогает");
    }

    [Fact]
    public async Task OneShotНаЛокали_СлотЗанятВоВремяЗапуска_ИОсвобождаетсяПоВыходу()
    {
        var gate = new LocalEngineBusyTracker(_clock);
        var (runner, launcher) = OneShot(gate);

        var act = () => runner.RunAsync("p", LocalModel);

        await act.Should().ThrowAsync<MarkerStartException>();
        launcher.ActiveAtStart.Should().Be(1, "one-shot занимает слот в том же счётчике, что и ходы");
        gate.Active.Should().Be(0, "на исключении слот отпущен");
    }
}
