using ClaudeHomeServer.DeviceAgent.Cli;
using ClaudeHomeServer.DeviceAgent.Exec;
using ClaudeHomeServer.DeviceAgent.Hosting;
using ClaudeHomeServer.DeviceAgent.Tests.Exec;
using ClaudeHomeServer.DeviceAgent.Update;
using ClaudeHomeServer.Protocol;

namespace ClaudeHomeServer.DeviceAgent.Tests.Hosting;

public class AgentCoordinatorTests
{
    private sealed class FakeControl : IControlConnection
    {
        public List<DeviceHello> Hellos { get; } = [];
        public string? RequiredCli { get; set; } = "2.1.0";
        public TaskCompletionSource<bool> SecondHello { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public event Func<DeviceExecOpenCommand, Task>? ExecOpen;
        public event Func<Task>? Reconnected;

        public Task<DeviceHelloAck> HelloAsync(DeviceHello hello, CancellationToken ct)
        {
            lock (Hellos)
            {
                Hellos.Add(hello);
                if (Hellos.Count >= 2) SecondHello.TrySetResult(true);
            }
            return Task.FromResult(new DeviceHelloAck(1, 2, 1024, 10, RequiredCli, hello.CliVersion == RequiredCli,
                hello.CliVersion == RequiredCli ? null : "Агент устройства не готов"));
        }

        public Task RaiseExecOpen(DeviceExecOpenCommand c) => ExecOpen!.Invoke(c);
        public Task RaiseReconnected() => Reconnected!.Invoke();
    }

    private sealed class FakeHarness : IHarness
    {
        public string? ActiveVersion { get; set; }
        public List<string?> Required { get; } = [];
        public event Action<HarnessStatus>? Changed;
        public void SetRequiredVersion(string? version) => Required.Add(version);
        public void Raise() => Changed?.Invoke(new HarnessStatus(HarnessState.Ready, "2.1.0", ActiveVersion, null, null));
    }

    private static AgentCoordinator Create(FakeControl control, FakeHarness harness, IExecSocketConnector? connector = null,
        Func<ExecLink, CancellationToken, Task>? run = null, Func<ExecLink, CancellationToken, Task>? relay = null) =>
        new(control, harness, connector ?? new ExecTestServer(), run ?? ((_, _) => Task.CompletedTask), "0.9.0", runRelay: relay);

    [Fact]
    public async Task Hello_объявляет_exec_платформу_и_версию_управляемой_копии()
    {
        var control = new FakeControl();
        var harness = new FakeHarness { ActiveVersion = "2.0.9" };
        await using var coordinator = Create(control, harness);

        await coordinator.HelloAsync();

        var hello = control.Hellos.Single();
        hello.ProtocolVersion.Should().Be(DesktopProtocol.Version);
        hello.Capabilities.Should().Equal(DeviceCapabilities.Exec, DeviceCapabilities.Files);
        hello.CliVersion.Should().Be("2.0.9");
        hello.AgentVersion.Should().Be("0.9.0");
        hello.Platform.Should().NotBeNullOrEmpty();
        hello.SupportedSteps.Should().BeEmpty("агент не исполняет шаги рук ADR-008");
        harness.Required.Should().Equal("2.1.0");
    }

    [Fact]
    public async Task Смена_активной_копии_повторяет_hello()
    {
        var control = new FakeControl();
        var harness = new FakeHarness { ActiveVersion = "2.0.9" };
        await using var coordinator = Create(control, harness);
        await coordinator.HelloAsync();

        harness.ActiveVersion = "2.1.0";
        harness.Raise();

        (await control.SecondHello.Task.WaitAsync(TimeSpan.FromSeconds(10))).Should().BeTrue();
        control.Hellos.Last().CliVersion.Should().Be("2.1.0");
    }

    [Fact]
    public async Task Changed_без_смены_копии_hello_не_повторяет()
    {
        var control = new FakeControl();
        var harness = new FakeHarness { ActiveVersion = "2.1.0" };
        await using var coordinator = Create(control, harness);
        await coordinator.HelloAsync();

        harness.Raise();
        await Task.Delay(300);

        control.Hellos.Should().HaveCount(1);
    }

    [Fact]
    public async Task Реконнект_канала_управления_повторяет_hello()
    {
        var control = new FakeControl();
        await using var coordinator = Create(control, new FakeHarness { ActiveVersion = "2.1.0" });
        await coordinator.HelloAsync();

        await control.RaiseReconnected();

        control.Hellos.Should().HaveCount(2);
    }

    [Fact]
    public async Task Команда_открытия_поднимает_связь_и_ход()
    {
        var control = new FakeControl();
        var server = new ExecTestServer();
        var started = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var coordinator = Create(control, new FakeHarness(), server,
            (link, _) => { started.TrySetResult(link.ExecId); return Task.CompletedTask; });

        await control.RaiseExecOpen(new DeviceExecOpenCommand("exec-42", DeviceExecProtocol.Version));

        (await started.Task.WaitAsync(TimeSpan.FromSeconds(10))).Should().Be("exec-42");
        server.Connections.Should().Be(1);
    }

    [Fact]
    public async Task С_ретранслятором_hello_объявляет_relay()
    {
        var control = new FakeControl();
        await using var coordinator = Create(control, new FakeHarness(), relay: (_, _) => Task.CompletedTask);

        await coordinator.HelloAsync();

        control.Hellos.Single().Capabilities.Should().Equal(DeviceCapabilities.Exec, DeviceCapabilities.Files, DeviceCapabilities.Relay);
    }

    [Fact]
    public async Task Назначение_relay_уходит_ретранслятору_а_не_ходу()
    {
        var control = new FakeControl();
        var server = new ExecTestServer();
        var turn = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var relay = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var coordinator = Create(control, new FakeHarness(), server,
            (link, _) => { turn.TrySetResult(link.ExecId); return Task.CompletedTask; },
            (link, _) => { relay.TrySetResult(link.ExecId); return Task.CompletedTask; });

        await control.RaiseExecOpen(new DeviceExecOpenCommand("r-1", DeviceExecProtocol.Version, DeviceExecPurposes.Relay));
        await control.RaiseExecOpen(new DeviceExecOpenCommand("t-1", DeviceExecProtocol.Version));

        (await relay.Task.WaitAsync(TimeSpan.FromSeconds(10))).Should().Be("r-1");
        (await turn.Task.WaitAsync(TimeSpan.FromSeconds(10))).Should().Be("t-1");
    }

    [Fact]
    public async Task Незнакомое_назначение_канал_не_открывает()
    {
        var control = new FakeControl();
        var server = new ExecTestServer();
        var ran = false;
        await using var coordinator = Create(control, new FakeHarness(), server,
            (_, _) => { ran = true; return Task.CompletedTask; }, (_, _) => { ran = true; return Task.CompletedTask; });

        await control.RaiseExecOpen(new DeviceExecOpenCommand("x-1", DeviceExecProtocol.Version, "write"));

        server.Connections.Should().Be(0);
        ran.Should().BeFalse();
    }

    private sealed class FakeUpdates : IAgentUpdates
    {
        public DeviceAgentUpdate Status { get; set; } = new(DeviceAgentUpdateStates.Idle);
        public List<DeviceHelloAck> Acks { get; } = [];
        public event Action<DeviceAgentUpdate>? Changed;
        public void OnAck(DeviceHelloAck ack) => Acks.Add(ack);
        public void Raise() => Changed?.Invoke(Status);
    }

    [Fact]
    public async Task Hello_несёт_состояние_обновления_ack_уходит_обновлятору_смена_повторяет_hello()
    {
        var control = new FakeControl();
        var updates = new FakeUpdates();
        await using var coordinator = new AgentCoordinator(control, new FakeHarness(), new ExecTestServer(),
            (_, _) => Task.CompletedTask, "1.5.0", updates: updates);
        await coordinator.HelloAsync();

        control.Hellos.Single().AgentUpdate.Should().Be(new DeviceAgentUpdate(DeviceAgentUpdateStates.Idle));
        updates.Acks.Should().ContainSingle();

        updates.Status = new DeviceAgentUpdate(DeviceAgentUpdateStates.WaitingIdle, "2.0.0", "идёт ход");
        updates.Raise();

        (await control.SecondHello.Task.WaitAsync(TimeSpan.FromSeconds(10))).Should().BeTrue();
        control.Hellos.Last().AgentUpdate!.State.Should().Be(DeviceAgentUpdateStates.WaitingIdle);
    }

    [Fact]
    public async Task Канал_исполнения_держит_аренду_до_конца_хода()
    {
        var control = new FakeControl();
        var activity = new ActivityRegistry();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var idle = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var coordinator = new AgentCoordinator(control, new FakeHarness(), new ExecTestServer(),
            async (_, _) => { started.TrySetResult(); await finish.Task; }, "1.5.0", activity: activity);

        await control.RaiseExecOpen(new DeviceExecOpenCommand("t-1", DeviceExecProtocol.Version));
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));

        activity.Describe().Should().Be("идёт ход");
        activity.TrySeal().Should().BeFalse();

        activity.Changed += () => { if (activity.IsIdle) idle.TrySetResult(); };
        finish.SetResult();
        await idle.Task.WaitAsync(TimeSpan.FromSeconds(10));
        activity.TrySeal().Should().BeTrue();
    }

    [Fact]
    public async Task Запечатанный_реестр_новый_канал_не_открывает()
    {
        var control = new FakeControl();
        var server = new ExecTestServer();
        var activity = new ActivityRegistry();
        var ran = false;
        await using var coordinator = new AgentCoordinator(control, new FakeHarness(), server,
            (_, _) => { ran = true; return Task.CompletedTask; }, "1.5.0", activity: activity);
        activity.TrySeal().Should().BeTrue();

        await control.RaiseExecOpen(new DeviceExecOpenCommand("t-1", DeviceExecProtocol.Version));

        server.Connections.Should().Be(0);
        ran.Should().BeFalse();
    }
}
