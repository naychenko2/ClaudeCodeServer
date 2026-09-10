using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services.Desktop;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClaudeHomeServer.Tests.Services.Desktop;

// Маршрутизатор вызовов десктопного агента (ADR-008, «Протокол канала»): ack за 2 с,
// двухфазность (go), разведённые ожидание человека и дедлайн исполнения, одноразовый приём
// результата, отмена, индекс последнего применённого шага в любом исходе.
// Время — управляемый TimeProvider с ручными таймерами; фазу ждём по факту создания таймера
// (TaskCompletionSource), ни одного Task.Delay.
public class DesktopCallRouterTests
{
    private const string Owner = "owner-1";
    private const string Device = "device-1";
    private const string Session = "chat-1";
    private const string Conn = "conn-1";

    // ---------- инфраструктура теста ----------

    private sealed class FakeTime(DateTimeOffset now) : TimeProvider
    {
        private readonly object _lock = new();
        private readonly List<FakeTimer> _timers = [];
        private readonly List<(int Target, TaskCompletionSource Tcs)> _waiters = [];
        private int _created;

        public DateTimeOffset Now { get; private set; } = now;

        public override DateTimeOffset GetUtcNow()
        {
            lock (_lock) return Now;
        }

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            List<TaskCompletionSource> ready;
            FakeTimer timer;
            lock (_lock)
            {
                timer = new FakeTimer(this, callback, state, Due(dueTime));
                _timers.Add(timer);
                _created++;
                ready = _waiters.Where(w => w.Target <= _created).Select(w => w.Tcs).ToList();
                _waiters.RemoveAll(w => w.Target <= _created);
            }
            foreach (var tcs in ready) tcs.TrySetResult();
            return timer;
        }

        /// <summary>Дождаться, пока маршрутизатор дойдёт до N-й фазы ожидания (создаст N-й таймер).</summary>
        public Task WaitForTimersAsync(int count)
        {
            lock (_lock)
            {
                if (_created >= count) return Task.CompletedTask;
                var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _waiters.Add((count, tcs));
                return tcs.Task;
            }
        }

        /// <summary>Двинуть часы и выстрелить всеми созревшими таймерами.</summary>
        public void Advance(TimeSpan by)
        {
            FakeTimer[] due;
            lock (_lock)
            {
                Now += by;
                due = _timers.Where(t => t.DueAt is { } d && d <= Now).ToArray();
            }
            foreach (var timer in due) timer.Fire();
        }

        internal DateTimeOffset? Due(TimeSpan dueTime) =>
            dueTime == Timeout.InfiniteTimeSpan ? null : Now + dueTime;

        internal void Forget(FakeTimer timer)
        {
            lock (_lock) _timers.Remove(timer);
        }

        internal sealed class FakeTimer(FakeTime time, TimerCallback callback, object? state, DateTimeOffset? dueAt) : ITimer
        {
            public DateTimeOffset? DueAt { get; private set; } = dueAt;

            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                DueAt = time.Due(dueTime);
                return true;
            }

            public void Fire()
            {
                DueAt = null;
                callback(state);
            }

            public void Dispose() => time.Forget(this);

            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }
        }
    }

    private sealed class FakeSender : IDeviceCommandSender
    {
        public readonly List<DesktopCallCommand> Calls = [];
        public readonly List<DesktopGoCommand> Goes = [];
        public readonly List<DesktopCancelCommand> Cancels = [];
        public Exception? ThrowOnCall;

        public Task SendCallAsync(string connectionId, DesktopCallCommand command, CancellationToken ct = default)
        {
            if (ThrowOnCall is not null) throw ThrowOnCall;
            Calls.Add(command);
            return Task.CompletedTask;
        }

        public Task SendGoAsync(string connectionId, DesktopGoCommand go, CancellationToken ct = default)
        {
            Goes.Add(go);
            return Task.CompletedTask;
        }

        public Task SendCancelAsync(string connectionId, DesktopCancelCommand cancel, CancellationToken ct = default)
        {
            Cancels.Add(cancel);
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingObserver : IDeviceConnectionObserver
    {
        public readonly List<DeviceConnection> Online = [];
        public readonly List<DeviceConnection> Offline = [];

        public Task OnDeviceOnlineAsync(DeviceConnection connection, CancellationToken ct = default)
        {
            Online.Add(connection);
            return Task.CompletedTask;
        }

        public Task OnDeviceOfflineAsync(DeviceConnection connection, CancellationToken ct = default)
        {
            Offline.Add(connection);
            return Task.CompletedTask;
        }
    }

    private static (DesktopCallRouter Router, FakeSender Sender, FakeTime Time, RecordingObserver Observer) Build()
    {
        var time = new FakeTime(DateTimeOffset.UnixEpoch);
        var sender = new FakeSender();
        var observer = new RecordingObserver();
        var router = new DesktopCallRouter(sender, [observer], NullLogger<DesktopCallRouter>.Instance, time);
        return (router, sender, time, observer);
    }

    private static Task ConnectAsync(DesktopCallRouter router)
    {
        router.RegisterConnection(Conn, Owner, Device);
        return router.HelloAsync(Conn, new DeviceHello(DesktopProtocol.Version, ["click", "type"], "1.0.0"));
    }

    private static DesktopCallRequest Request(string kind = DesktopCallKinds.Act, bool confirm = true) =>
        new(Owner, Device, Session, kind, RequiresConfirmation: confirm, DeviceName: "home");

    // ---------- соединения ----------

    [Fact]
    public async Task Hello_ДелаетУстройствоОнлайнИЗоветНаблюдателя()
    {
        var (router, _, _, observer) = Build();
        router.RegisterConnection(Conn, Owner, Device);

        router.IsOnline(Owner, Device).Should().BeFalse("до Hello устройство командам недоступно");

        var ack = await router.HelloAsync(Conn, new DeviceHello(DesktopProtocol.Version, ["click"], "1.0.0"));

        ack.ProtocolVersion.Should().Be(DesktopProtocol.Version);
        ack.MaxResultBytes.Should().Be(DesktopProtocol.MaxResultBytes);
        router.IsOnline(Owner, Device).Should().BeTrue();
        router.Find(Owner, Device)!.SupportedSteps.Should().Contain("click");
        router.Online(Owner).Should().ContainSingle();
        observer.Online.Should().ContainSingle().Which.DeviceId.Should().Be(Device);
    }

    [Fact]
    public async Task Разрыв_ГаситУстройствоИЗакрываетВызовВПолётеИсходомUnknown()
    {
        var (router, sender, time, observer) = Build();
        await ConnectAsync(router);

        var invoke = router.InvokeAsync(Request());
        await time.WaitForTimersAsync(1);
        router.Ack(sender.Calls[0].CallId, Conn).Should().BeTrue();
        router.Progress(sender.Calls[0].CallId, Conn, 2).Should().BeTrue();

        await router.RemoveConnectionAsync(Conn);

        var result = await invoke;
        result.Outcome.Should().Be(DesktopOutcomes.Unknown);
        result.LastAppliedStep.Should().Be(2, "индекс последнего применённого шага возвращается в любом исходе");
        result.Message.Should().NotContainEquivalentOf("повтор",
            "у unknown нет подсказки «повтори» — авто-ретраев нет нигде");
        router.IsOnline(Owner, Device).Should().BeFalse();
        observer.Offline.Should().ContainSingle();
    }

    // ---------- фазы вызова ----------

    [Fact]
    public async Task Офлайн_ЭтоИсходВызоваАНеИсключение()
    {
        var (router, sender, _, _) = Build();

        var result = await router.InvokeAsync(Request());

        result.Outcome.Should().Be(DesktopOutcomes.DeviceOffline);
        result.LastAppliedStep.Should().Be(0);
        sender.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task НеизвестныйВидВызова_ProtocolError()
    {
        var (router, _, _, _) = Build();
        await ConnectAsync(router);

        var result = await router.InvokeAsync(Request("teleport"));

        result.Outcome.Should().Be(DesktopOutcomes.ProtocolError);
    }

    [Fact]
    public async Task КомандаНеУшлаВКанал_ProtocolErrorБезВисения()
    {
        var (router, sender, _, _) = Build();
        await ConnectAsync(router);
        sender.ThrowOnCall = new IOException("канал закрыт");

        var result = await router.InvokeAsync(Request());

        result.Outcome.Should().Be(DesktopOutcomes.ProtocolError);
        result.LastAppliedStep.Should().Be(0);
    }

    [Fact]
    public async Task НетAckЗа2Секунды_ЧестнаяОшибкаБезGo()
    {
        var (router, sender, time, _) = Build();
        await ConnectAsync(router);

        var invoke = router.InvokeAsync(Request());
        await time.WaitForTimersAsync(1);
        time.Advance(DesktopProtocol.AckTimeout);

        var result = await invoke;
        result.Outcome.Should().Be(DesktopOutcomes.NoAck);
        result.LastAppliedStep.Should().Be(0);
        sender.Goes.Should().BeEmpty("без ack исполнять нечего");
    }

    [Fact]
    public async Task КомандаНесётВерсиюПротоколаCallId128БитИДедлайнВида()
    {
        var (router, sender, time, _) = Build();
        await ConnectAsync(router);

        var invoke = router.InvokeAsync(Request());
        await time.WaitForTimersAsync(1);

        var command = sender.Calls.Should().ContainSingle().Subject;
        command.CallId.Should().MatchRegex("^[0-9a-f]{32}$", "callId — 128 бит, генерирует бэкенд");
        command.ProtocolVersion.Should().Be(DesktopProtocol.Version);
        command.DeadlineSeconds.Should().Be(30, "дедлайн act — 30 с");
        command.SessionId.Should().Be(Session);

        time.Advance(DesktopProtocol.AckTimeout);
        await invoke;
    }

    [Fact]
    public async Task ПодтверждениеЧеловека_ДаётGoИРезультатДоезжаетДоМодели()
    {
        var (router, sender, time, _) = Build();
        await ConnectAsync(router);

        var invoke = router.InvokeAsync(Request());
        await time.WaitForTimersAsync(1);
        var callId = sender.Calls[0].CallId;
        router.Ack(callId, Conn);

        await time.WaitForTimersAsync(2); // фаза ожидания человека
        sender.Goes.Should().BeEmpty("go не уходит, пока человек не подтвердил");
        router.Confirm(callId, Conn);

        await time.WaitForTimersAsync(3); // фаза исполнения — значит go уже ушёл
        sender.Goes.Should().ContainSingle().Which.CallId.Should().Be(callId);

        router.TryAcceptResult(callId, Owner, Device, new DesktopCallResult(callId, DesktopOutcomes.Ok, 3))
            .Should().Be(DesktopResultAcceptance.Accepted);

        var result = await invoke;
        result.Outcome.Should().Be(DesktopOutcomes.Ok);
        result.LastAppliedStep.Should().Be(3);
    }

    [Fact]
    public async Task ОжиданиеЧеловека_МеряетсяМинутамиИНеТратитДедлайнИсполнения()
    {
        var (router, sender, time, _) = Build();
        await ConnectAsync(router);

        var invoke = router.InvokeAsync(Request());
        await time.WaitForTimersAsync(1);
        router.Ack(sender.Calls[0].CallId, Conn);
        await time.WaitForTimersAsync(2);

        // Дедлайн исполнения act — 30 с; проматываем сильно больше, вызов обязан ЖИТЬ:
        // часы исполнения идут только после go.
        time.Advance(TimeSpan.FromMinutes(2));
        invoke.IsCompleted.Should().BeFalse();

        // Ожидание человека по умолчанию 3 минуты — добираем остаток
        time.Advance(TimeSpan.FromMinutes(1));

        var result = await invoke;
        result.Outcome.Should().Be(DesktopOutcomes.AwaitingConfirmation);
        result.AwaitMinutes.Should().Be(3);
        result.LastAppliedStep.Should().Be(0);
        sender.Goes.Should().BeEmpty();
        sender.Cancels.Should().ContainSingle("невыполненные шаги на устройстве надо погасить");
    }

    [Fact]
    public async Task УстройствоПопросилоБольшеМинут_ОкноПродлевается()
    {
        var (router, sender, time, _) = Build();
        await ConnectAsync(router);

        var invoke = router.InvokeAsync(Request());
        await time.WaitForTimersAsync(1);
        var callId = sender.Calls[0].CallId;
        router.Ack(callId, Conn);
        await time.WaitForTimersAsync(2);
        router.Awaiting(callId, Conn, 5).Should().BeTrue();

        time.Advance(DesktopProtocol.DefaultConfirmationWait);
        await time.WaitForTimersAsync(3); // продлённое окно
        invoke.IsCompleted.Should().BeFalse("устройство попросило 5 минут вместо 3");

        router.Confirm(callId, Conn);
        await time.WaitForTimersAsync(4);
        router.TryAcceptResult(callId, Owner, Device, new DesktopCallResult(callId, DesktopOutcomes.Ok, 1));

        (await invoke).Outcome.Should().Be(DesktopOutcomes.Ok);
    }

    [Fact]
    public async Task ОтказЧеловека_ВозвращаетDenied()
    {
        var (router, sender, time, _) = Build();
        await ConnectAsync(router);

        var invoke = router.InvokeAsync(Request());
        await time.WaitForTimersAsync(1);
        var callId = sender.Calls[0].CallId;
        router.Ack(callId, Conn);
        await time.WaitForTimersAsync(2);
        router.Decline(callId, Conn);

        var result = await invoke;
        result.Outcome.Should().Be(DesktopOutcomes.Denied);
        sender.Goes.Should().BeEmpty();
    }

    [Fact]
    public async Task ДедлайнПослеGo_ИстёкБезРезультата_ОтменаИИндексШага()
    {
        var (router, sender, time, _) = Build();
        await ConnectAsync(router);

        var invoke = router.InvokeAsync(Request(DesktopCallKinds.Screen, confirm: false));
        await time.WaitForTimersAsync(1);
        var callId = sender.Calls[0].CallId;
        router.Ack(callId, Conn);

        await time.WaitForTimersAsync(2); // фаза исполнения
        sender.Goes.Should().ContainSingle().Which.DeadlineSeconds.Should().Be(15, "дедлайн screen — 15 с");

        router.Progress(callId, Conn, 1);
        time.Advance(TimeSpan.FromSeconds(15));

        var result = await invoke;
        result.Outcome.Should().Be(DesktopOutcomes.DeadlineExceeded);
        result.LastAppliedStep.Should().Be(1);
        sender.Cancels.Should().ContainSingle();
    }

    [Fact]
    public async Task Interrupt_ОтменяетВызовИШлётCancelНаУстройство()
    {
        var (router, sender, time, _) = Build();
        await ConnectAsync(router);
        using var cts = new CancellationTokenSource();

        var invoke = router.InvokeAsync(Request(), cts.Token);
        await time.WaitForTimersAsync(1);
        var callId = sender.Calls[0].CallId;
        router.Ack(callId, Conn);
        await time.WaitForTimersAsync(2);

        await cts.CancelAsync();

        var result = await invoke;
        result.Outcome.Should().Be(DesktopOutcomes.Cancelled);
        sender.Cancels.Should().ContainSingle().Which.CallId.Should().Be(callId);
    }

    [Fact]
    public async Task CancelSession_ГаситВсеВызовыЧата()
    {
        var (router, sender, time, _) = Build();
        await ConnectAsync(router);

        var invoke = router.InvokeAsync(Request());
        await time.WaitForTimersAsync(1);
        var callId = sender.Calls[0].CallId;
        router.Ack(callId, Conn);
        await time.WaitForTimersAsync(2);

        await router.CancelSessionAsync(Session, "грань выключена в проекте");

        var result = await invoke;
        result.Outcome.Should().Be(DesktopOutcomes.Cancelled);
        sender.Cancels.Should().ContainSingle().Which.CallId.Should().Be(callId);
    }

    // ---------- приём результата ----------

    [Fact]
    public async Task ПовторныйРезультат_ЕдинственнаяПричинаДубля()
    {
        var (router, sender, time, _) = Build();
        await ConnectAsync(router);

        var invoke = router.InvokeAsync(Request(confirm: false));
        await time.WaitForTimersAsync(1);
        var callId = sender.Calls[0].CallId;
        router.Ack(callId, Conn);
        await time.WaitForTimersAsync(2);

        var result = new DesktopCallResult(callId, DesktopOutcomes.Ok, 1);
        router.TryAcceptResult(callId, Owner, Device, result).Should().Be(DesktopResultAcceptance.Accepted);
        router.TryAcceptResult(callId, Owner, Device, result).Should().Be(DesktopResultAcceptance.Duplicate);

        await invoke;
    }

    [Fact]
    public async Task ПозднийИЧастичныйРезультат_Принимается()
    {
        var (router, sender, time, _) = Build();
        await ConnectAsync(router);

        var invoke = router.InvokeAsync(Request(confirm: false));
        await time.WaitForTimersAsync(1);
        var callId = sender.Calls[0].CallId;
        router.Ack(callId, Conn);
        await time.WaitForTimersAsync(2);

        time.Advance(TimeSpan.FromSeconds(30));
        (await invoke).Outcome.Should().Be(DesktopOutcomes.DeadlineExceeded);

        // Устройство отвечает уже после того, как модель получила исход — приём обязан пройти
        router.TryAcceptResult(callId, Owner, Device,
                new DesktopCallResult(callId, DesktopOutcomes.Ok, 2, Partial: true))
            .Should().Be(DesktopResultAcceptance.Accepted);

        router.TryGetPostedResult(callId, Owner, Device, out var posted).Should().Be(DesktopResultLookup.Found);
        posted!.Partial.Should().BeTrue();
        posted.LastAppliedStep.Should().Be(2);
    }

    [Fact]
    public async Task РезультатОтЧужойПарыВладелецУстройство_Отвергается()
    {
        var (router, sender, time, _) = Build();
        await ConnectAsync(router);

        var invoke = router.InvokeAsync(Request(confirm: false));
        await time.WaitForTimersAsync(1);
        var callId = sender.Calls[0].CallId;
        router.Ack(callId, Conn);
        await time.WaitForTimersAsync(2);

        router.TryAcceptResult(callId, "owner-2", Device, new DesktopCallResult(callId, DesktopOutcomes.Ok, 1))
            .Should().Be(DesktopResultAcceptance.Forbidden);
        router.TryAcceptResult(callId, Owner, "device-2", new DesktopCallResult(callId, DesktopOutcomes.Ok, 1))
            .Should().Be(DesktopResultAcceptance.Forbidden);
        router.TryGetPostedResult(callId, "owner-2", Device, out _).Should().Be(DesktopResultLookup.Forbidden);

        time.Advance(TimeSpan.FromSeconds(30));
        await invoke;
    }

    [Fact]
    public void РезультатПоНеизвестномуCallId_UnknownCall()
    {
        var (router, _, _, _) = Build();

        router.TryAcceptResult("нет-такого", Owner, Device, new DesktopCallResult("нет-такого", DesktopOutcomes.Ok, 0))
            .Should().Be(DesktopResultAcceptance.UnknownCall);
        router.TryGetPostedResult("нет-такого", Owner, Device, out _).Should().Be(DesktopResultLookup.UnknownCall);
    }

    [Fact]
    public async Task РезультатаЕщёНет_Pending()
    {
        var (router, sender, time, _) = Build();
        await ConnectAsync(router);

        var invoke = router.InvokeAsync(Request(confirm: false));
        await time.WaitForTimersAsync(1);
        var callId = sender.Calls[0].CallId;

        router.TryGetPostedResult(callId, Owner, Device, out var result).Should().Be(DesktopResultLookup.Pending);
        result.Should().BeNull();

        time.Advance(DesktopProtocol.AckTimeout);
        await invoke;
    }

    [Fact]
    public async Task НеизвестныйИсходУстройства_СводитсяКUnknown()
    {
        var (router, sender, time, _) = Build();
        await ConnectAsync(router);

        var invoke = router.InvokeAsync(Request(confirm: false));
        await time.WaitForTimersAsync(1);
        var callId = sender.Calls[0].CallId;
        router.Ack(callId, Conn);
        await time.WaitForTimersAsync(2);

        router.TryAcceptResult(callId, Owner, Device, new DesktopCallResult(callId, "сочинил-исход", 1));

        (await invoke).Outcome.Should().Be(DesktopOutcomes.Unknown);
    }

    [Theory]
    [InlineData(DesktopOutcomes.SessionLocked)]
    [InlineData(DesktopOutcomes.SecureDesktop)]
    [InlineData(DesktopOutcomes.TargetElevated)]
    [InlineData(DesktopOutcomes.InputBlocked)]
    [InlineData(DesktopOutcomes.SelfTargetDenied)]
    [InlineData(DesktopOutcomes.WindowNotAvailable)]
    [InlineData(DesktopOutcomes.WindowMinimized)]
    public async Task ЯвныеИсходыУстройства_ДоезжаютДоМоделиКакЕсть(string outcome)
    {
        var (router, sender, time, _) = Build();
        await ConnectAsync(router);

        var invoke = router.InvokeAsync(Request(confirm: false));
        await time.WaitForTimersAsync(1);
        var callId = sender.Calls[0].CallId;
        router.Ack(callId, Conn);
        await time.WaitForTimersAsync(2);

        router.TryAcceptResult(callId, Owner, Device, new DesktopCallResult(callId, outcome, 0, "как есть"));

        var result = await invoke;
        result.Outcome.Should().Be(outcome);
        result.Message.Should().Be("как есть");
    }

    // ---------- донесения по чужому вызову ----------

    [Fact]
    public async Task ДонесениеИзЧужогоСоединения_НеПроводится()
    {
        var (router, sender, time, _) = Build();
        await ConnectAsync(router);

        var invoke = router.InvokeAsync(Request());
        await time.WaitForTimersAsync(1);
        var callId = sender.Calls[0].CallId;

        router.Ack(callId, "conn-чужой").Should().BeFalse();
        router.Confirm(callId, "conn-чужой").Should().BeFalse();
        router.Progress(callId, "conn-чужой", 5).Should().BeFalse();

        time.Advance(DesktopProtocol.AckTimeout);
        (await invoke).Outcome.Should().Be(DesktopOutcomes.NoAck);
    }
}
