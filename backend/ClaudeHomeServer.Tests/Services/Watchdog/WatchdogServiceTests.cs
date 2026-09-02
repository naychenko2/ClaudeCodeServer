using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.Watchdog;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClaudeHomeServer.Tests.Services.Watchdog;

// Юниты цикла сторожей (шаг 2 плана): все терминальные пути (fired/timed_out/
// launch_failed/cancelled), per-poll таймаут, семантика «ещё нет», ретраи доставки и
// недоставка флагом — на fake-раннере/fake-окружении/fake-будильнике и fake-часах
// (время передаётся в TickAsync). Реальных процессов нет — CI Linux безопасен.
public class WatchdogServiceTests : IDisposable
{
    private readonly string _tempDir;
    private readonly WatchdogStore _store;

    // Фейковое окружение: чаты в памяти (ArchivedAt задаёт архивность), каталог — константа.
    // Событие — с сигналом первой подписки: StartAsync стартует ExecuteAsync через Task.Run
    // (поведение .NET 10, подписка асинхронна), и событийный путь ждёт её перед инvoke
    private sealed class FakeEnvironment : IWatchdogEnvironment
    {
        private readonly TaskCompletionSource _subscribed =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private Action<Session>? _chatDeleted;

        public event Action<Session>? ChatDeleted
        {
            add { _chatDeleted += value; _subscribed.TrySetResult(); }
            remove => _chatDeleted -= value;
        }

        public Task Subscribed => _subscribed.Task;
        public Dictionary<string, Session> Chats { get; } = new();
        public string? WorkDir { get; set; } = "C:\\work";

        public Session? FindChat(string sessionId, string ownerId) =>
            Chats.TryGetValue(sessionId, out var s) && s.OwnerId == ownerId ? s : null;

        public string? ResolveWorkDir(WatchdogRecord w) => WorkDir;
        public void RaiseChatDeleted(Session s) => _chatDeleted?.Invoke(s);
    }

    // Фейковый раннер: скрипт исходов по вызовам (какие команды, с каким каталогом).
    // Скрипт — Func<Task<...>>: тест может придержать опрос TCS-гейтом без блокировки;
    // вариант с CancellationToken — придержать на токене, как реальный WaitForExitAsync
    private sealed class FakeRunner : IWatchdogCommandRunner
    {
        public List<(string Owner, string WorkDir, string Command)> Calls { get; } = [];

        // Сигнал первого вызова: тесту отмены нужно знать, что poll УЖЕ идёт (токен
        // зарегистрирован), прежде чем снимать сторожа — иначе проверка гонки превратилась
        // бы в флейк
        private readonly TaskCompletionSource _firstCall =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task FirstCall => _firstCall.Task;

        private readonly Queue<Func<CancellationToken, Task<PollOutcome>>> _script = new();

        public void Enqueue(PollOutcome outcome) => _script.Enqueue(_ => Task.FromResult(outcome));
        public void Enqueue(Func<Task<PollOutcome>> factory) => _script.Enqueue(_ => factory());
        public void Enqueue(Func<CancellationToken, Task<PollOutcome>> factory) => _script.Enqueue(factory);

        public async Task<PollOutcome> RunAsync(string ownerId, string workDir, string command,
            int timeoutSeconds, CancellationToken ct)
        {
            Calls.Add((ownerId, workDir, command));
            _firstCall.TrySetResult();
            return _script.Count > 0 ? await _script.Dequeue()(ct) : PollOutcome.ExitedZero;
        }
    }

    // Фейковый будильник: фиксированная серия успехов/провалов + журнал доставок
    private sealed class FakeAlarm : IWatchdogAlarm
    {
        private readonly Queue<bool> _results = new();
        public List<(string Owner, string Session, string Text)> Delivered { get; } = [];

        public FakeAlarm(params bool[] results) { foreach (var r in results) _results.Enqueue(r); }

        public Task<bool> DeliverAsync(string ownerId, string sessionId, string text)
        {
            Delivered.Add((ownerId, sessionId, text));
            return Task.FromResult(_results.Count > 0 ? _results.Dequeue() : true);
        }
    }

    public WatchdogServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "watchdog_svc_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _store = new WatchdogStore(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DataPath"] = Path.Combine(_tempDir, "projects.json")
            }).Build());
    }

    private (WatchdogService Sut, FakeRunner Runner, FakeAlarm Alarm, FakeEnvironment Env) Setup(
        FakeRunner? runner = null, FakeAlarm? alarm = null, FakeEnvironment? env = null)
    {
        var r = runner ?? new FakeRunner();
        var a = alarm ?? new FakeAlarm();
        var e = env ?? new FakeEnvironment();
        var sut = new WatchdogService(_store, e, r, a, NullLogger<WatchdogService>.Instance);
        return (sut, r, a, e);
    }

    private WatchdogRecord NewWatchdog(FakeEnvironment env, int intervalSec = 60, int ttlMin = 240)
    {
        var chat = new Session { Id = "chat-1", OwnerId = "owner-1", ProjectId = null };
        env.Chats[chat.Id] = chat;
        return _store.Create("owner-1", chat.Id, null, "Билд", "py check.py",
            intervalSec, ttlMin, out var error)!;
    }

    [Fact]
    public async Task Tick_ExitZero_FiresAndDeliversAlarm()
    {
        var (sut, runner, alarm, env) = Setup();
        runner.Enqueue(PollOutcome.Exited(0, "reindex: ok"));
        var w = NewWatchdog(env);
        var now = DateTime.UtcNow;

        await sut.TickAsync(now);

        w.Status.Should().Be(WatchdogStatus.Fired);
        w.FiredAt.Should().Be(now);
        w.DeliveredAt.Should().Be(now);
        w.LastOutput.Should().Be("reindex: ok");
        alarm.Delivered.Should().ContainSingle().Which.Text.Should()
            .Contain("⏰ Сторож «Билд»").And.Contain("условие выполнено").And.Contain("reindex: ok");
    }

    [Fact]
    public async Task Tick_ExitNonZero_IsStillWaiting()
    {
        var (sut, runner, _, env) = Setup();
        runner.Enqueue(PollOutcome.Exited(1, "pending"));
        var w = NewWatchdog(env);

        await sut.TickAsync(DateTime.UtcNow);

        w.Status.Should().Be(WatchdogStatus.Active);
        w.LastPollAt.Should().NotBeNull();
        w.ConsecutiveLaunchFailures.Should().Be(0);
    }

    [Fact]
    public async Task Tick_PollTimeout_KillsAndWaits()
    {
        var (sut, runner, _, env) = Setup();
        runner.Enqueue(new PollOutcome(PollOutcomeKind.PollTimeout));
        var w = NewWatchdog(env);

        await sut.TickAsync(DateTime.UtcNow);

        // Таймаут запуска = «ещё нет»: сторож жив, счётчик сбоев запуска не растёт
        w.Status.Should().Be(WatchdogStatus.Active);
        w.ConsecutiveLaunchFailures.Should().Be(0);
        w.LastOutput.Should().Contain("снят");
    }

    [Fact]
    public async Task Tick_IntervalNotElapsed_DoesNotPoll()
    {
        var (sut, runner, _, env) = Setup();
        // «ещё нет» на каждый опрос — сторож живёт, расписание видно по числу вызовов
        runner.Enqueue(PollOutcome.Exited(1, "pending"));
        runner.Enqueue(PollOutcome.Exited(1, "pending"));
        var w = NewWatchdog(env, intervalSec: 60);
        var t0 = DateTime.UtcNow;

        await sut.TickAsync(t0);
        runner.Calls.Count.Should().Be(1);
        await sut.TickAsync(t0.AddSeconds(30));
        runner.Calls.Count.Should().Be(1);

        // Интервал истёк — опрос вернулся в расписание
        await sut.TickAsync(t0.AddSeconds(61));
        runner.Calls.Count.Should().Be(2);
    }

    [Fact]
    public async Task Tick_TtlExceeded_TimesOut()
    {
        var (sut, runner, alarm, env) = Setup();
        var w = NewWatchdog(env, ttlMin: 5);
        var now = DateTime.UtcNow;

        // TTL тикает от создания: на +6 мин — терминал, даже не опрашивая
        await sut.TickAsync(now.AddMinutes(6));

        w.Status.Should().Be(WatchdogStatus.TimedOut);
        runner.Calls.Should().BeEmpty();
        alarm.Delivered.Should().ContainSingle().Which.Text.Should().Contain("истёк потолок жизни (5 мин)");
        w.DeliveredAt.Should().NotBeNull();
    }

    [Fact]
    public async Task Tick_LaunchFailedThreeTimesInARow_Terminates()
    {
        var (sut, runner, alarm, env) = Setup();
        var w = NewWatchdog(env, intervalSec: 30);
        var t0 = DateTime.UtcNow;

        // Две неудачи подряд — ещё жив
        runner.Enqueue(PollOutcome.LaunchFailed("песочница недоступна"));
        runner.Enqueue(PollOutcome.LaunchFailed("песочница недоступна"));
        await sut.TickAsync(t0);
        await sut.TickAsync(t0.AddSeconds(30));
        w.Status.Should().Be(WatchdogStatus.Active);
        w.ConsecutiveLaunchFailures.Should().Be(2);

        // Состоявшийся запуск обнуляет счётчик
        runner.Enqueue(PollOutcome.Exited(1, "ещё нет"));
        await sut.TickAsync(t0.AddSeconds(60));
        w.ConsecutiveLaunchFailures.Should().Be(0);

        // Три подряд — терминал
        for (var i = 0; i < 3; i++) runner.Enqueue(PollOutcome.LaunchFailed("песочница недоступна"));
        await sut.TickAsync(t0.AddSeconds(90));
        await sut.TickAsync(t0.AddSeconds(120));
        await sut.TickAsync(t0.AddSeconds(150));
        w.Status.Should().Be(WatchdogStatus.LaunchFailed);
        alarm.Delivered.Should().ContainSingle().Which.Text.Should()
            .Contain("запуск невозможен").And.Contain("песочница недоступна");
    }

    [Fact]
    public async Task Tick_WorkDirMissing_CountsAsLaunchFailure()
    {
        var (sut, runner, _, env) = Setup();
        env.WorkDir = null;
        var w = NewWatchdog(env, intervalSec: 30);
        var t0 = DateTime.UtcNow;

        for (var i = 0; i < WatchdogLimits.MaxConsecutiveLaunchFailures; i++)
            await sut.TickAsync(t0.AddSeconds(30 * i));

        w.Status.Should().Be(WatchdogStatus.LaunchFailed);
        runner.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task Tick_ChatDeleted_CancelsSilently()
    {
        var (sut, runner, alarm, env) = Setup();
        var w = NewWatchdog(env);

        env.Chats.Remove("chat-1");
        await sut.TickAsync(DateTime.UtcNow);

        w.Status.Should().Be(WatchdogStatus.Cancelled);
        runner.Calls.Should().BeEmpty();
        alarm.Delivered.Should().BeEmpty();
        w.DeliveredAt.Should().BeNull();
    }

    [Fact]
    public async Task ChatDeletedEvent_CancelsImmediatelyWithoutTick()
    {
        // Событийный путь (п.5 ревью): OnSessionDeleted в SessionManager дёргает гашение
        // сразу, не дожидаясь тика. Подписку ждём TCS-сигналом фейка: StartAsync в .NET 10
        // стартует ExecuteAsync через Task.Run, и без ожидания инvoke мог бы опередить её
        var (sut, runner, alarm, env) = Setup();
        var w = NewWatchdog(env);
        using var cts = new CancellationTokenSource();
        await sut.StartAsync(cts.Token);
        var subscribed = await Task.WhenAny(env.Subscribed, Task.Delay(TimeSpan.FromSeconds(5)));
        subscribed.Should().Be(env.Subscribed, "не дождались подписки сервиса на ChatDeleted");
        try { env.RaiseChatDeleted(new Session { Id = "chat-1" }); }
        finally { await sut.StopAsync(cts.Token); }

        w.Status.Should().Be(WatchdogStatus.Cancelled);
        runner.Calls.Should().BeEmpty();
        alarm.Delivered.Should().BeEmpty();
    }

    [Fact]
    public async Task Tick_ChatArchived_CancelsSilently()
    {
        var (sut, _, alarm, env) = Setup();
        var w = NewWatchdog(env);
        var chat = env.Chats["chat-1"];
        chat.ArchivedAt = DateTime.UtcNow;
        chat.UpdatedAt = chat.ArchivedAt.Value.AddMinutes(-1);

        await sut.TickAsync(DateTime.UtcNow);

        w.Status.Should().Be(WatchdogStatus.Cancelled);
        alarm.Delivered.Should().BeEmpty();
    }

    [Fact]
    public async Task Delivery_AllRetriesFail_StaysUndeliveredFlag()
    {
        var (sut, runner, alarm, env) = Setup(alarm: new FakeAlarm(false, false, false, false));
        runner.Enqueue(PollOutcome.ExitedZero);
        var w = NewWatchdog(env, intervalSec: 30);
        var t0 = DateTime.UtcNow;

        await sut.TickAsync(t0);            // терминал + попытка 1 (неудача)
        w.Status.Should().Be(WatchdogStatus.Fired);

        // Ретраи по расписанию: FiredAt + k*интервал
        await sut.TickAsync(t0.AddSeconds(15));
        alarm.Delivered.Should().HaveCount(1);
        await sut.TickAsync(t0.AddSeconds(31));
        alarm.Delivered.Should().HaveCount(2);
        await sut.TickAsync(t0.AddSeconds(61));
        alarm.Delivered.Should().HaveCount(3);
        w.DeliveryAttempts.Should().Be(WatchdogLimits.DeliveryAttempts);

        // Исчерпаны — больше не пытаемся, статус сохранён, недоставка флагом
        await sut.TickAsync(t0.AddSeconds(91));
        await sut.TickAsync(t0.AddSeconds(121));
        alarm.Delivered.Should().HaveCount(3);
        w.Status.Should().Be(WatchdogStatus.Fired);
        w.DeliveredAt.Should().BeNull();
    }

    [Fact]
    public async Task Delivery_SecondAttemptSucceeds_MarksDelivered()
    {
        var (sut, runner, alarm, env) = Setup(alarm: new FakeAlarm(false, true));
        runner.Enqueue(PollOutcome.ExitedZero);
        var w = NewWatchdog(env, intervalSec: 30);
        var t0 = DateTime.UtcNow;

        await sut.TickAsync(t0);
        w.DeliveryAttempts.Should().Be(1);
        w.DeliveredAt.Should().BeNull();

        await sut.TickAsync(t0.AddSeconds(31));
        w.DeliveredAt.Should().NotBeNull();
        w.DeliveryAttempts.Should().Be(1);
    }

    [Fact]
    public async Task Tick_CancelledWhilePollRuns_OutcomeDoesNotOverwriteTerminal()
    {
        // watch_cancel при идущем опросе: команда дорабатывает (fake держит её до тика),
        // но её исход не должен перезаписать Cancelled — ни fired, ни счётчики
        var (sut, runner, alarm, env) = Setup();
        var w = NewWatchdog(env);
        var t0 = DateTime.UtcNow;

        var gate = new TaskCompletionSource<PollOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        runner.Enqueue(() => gate.Task);
        var ticking = sut.TickAsync(t0);

        // Тик ушёл в опрос — снимаем сторожа «со стороны» (как watch_cancel из хода)
        w.Status = WatchdogStatus.Cancelled;
        gate.SetResult(PollOutcome.ExitedZero);
        await ticking;

        w.Status.Should().Be(WatchdogStatus.Cancelled, "exit 0 после отмены не фаерит снятого сторожа");
        w.FiredAt.Should().BeNull();
        alarm.Delivered.Should().BeEmpty("будильника у отменённого сторожа не бывает");
    }

    [Fact]
    public async Task WatchCancelledWhilePollRuns_PollTokenCancelsAndOutcomeDropped()
    {
        // Дефект smoke 01.09: watch_cancel при живом poll-процессе. Стор эмитит
        // ActiveCancelled → сервис отменяет per-сторож токен → раннер (как реальный —
        // WaitForExitAsync на linked-токене) Kill'ит процесс и бросает OCE. Исход
        // отброшен, сторож Cancelled, опрос не трактуется ни как «ещё нет», ни как сбой
        var (sut, runner, alarm, env) = Setup();
        var w = NewWatchdog(env);
        var t0 = DateTime.UtcNow;

        runner.Enqueue(async ct =>
        {
            // Висим на токене отмены, как настоящий раннер на WaitForExitAsync
            await Task.Delay(-1, ct);
            return PollOutcome.Exited(1, "pending");
        });
        var ticking = sut.TickAsync(t0);
        // Ждём, пока poll действительно идёт (токен зарегистрирован) — без этого снятие
        // могло бы опередить регистрацию и проверяло бы не ту ветку
        var running = await Task.WhenAny(runner.FirstCall, Task.Delay(TimeSpan.FromSeconds(10)));
        running.Should().Be(runner.FirstCall, "опрос не стартовал за 10 с");

        // Снятие сторожа «со стороны» (путь watch_cancel): токен опроса отменяется
        var record = _store.Cancel(w.Id, "owner-1", out var error);
        record.Should().NotBeNull();
        error.Should().BeNull();

        // Тик развязался отменой (не завис и не ждал per-poll таймаута)
        var done = await Task.WhenAny(ticking, Task.Delay(TimeSpan.FromSeconds(10)));
        done.Should().Be(ticking, "отмена токена должна развязать придержанный опрос");

        w.Status.Should().Be(WatchdogStatus.Cancelled);
        w.LastPollAt.Should().BeNull("исход отменённого опроса не учитывается");
        w.LastOutput.Should().BeEmpty();
        alarm.Delivered.Should().BeEmpty("будильника у отменённого сторожа не бывает");
    }

    [Fact]
    public void ClipOutput_TrimsToTenLinesAndChars()
    {
        var eleven = string.Join("\n", Enumerable.Range(1, 11).Select(i => $"line{i}"));
        WatchdogService.ClipOutput(eleven).Should().Contain("line10").And.Contain("1 строк обрезано").And.NotContain("line11");
        WatchdogService.ClipOutput(new string('x', 2500)).Length.Should().Be(2001);
        WatchdogService.ClipOutput(null).Should().Be("");
    }

    [Fact]
    public void ShouldPoll_FirstPollImmediate_ThenByInterval()
    {
        var w = new WatchdogRecord { IntervalSeconds = 30 };
        var t0 = DateTime.UtcNow;
        WatchdogService.ShouldPoll(w, t0).Should().BeTrue();
        w.LastPollAt = t0;
        WatchdogService.ShouldPoll(w, t0.AddSeconds(29)).Should().BeFalse();
        WatchdogService.ShouldPoll(w, t0.AddSeconds(30)).Should().BeTrue();
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* временный каталог — мусор не критичен */ }
    }
}
