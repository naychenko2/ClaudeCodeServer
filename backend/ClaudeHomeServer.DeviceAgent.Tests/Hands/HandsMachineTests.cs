using System.IO.Pipes;
using System.Net;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using ClaudeHomeServer.DeviceAgent.Exec;
using ClaudeHomeServer.DeviceAgent.Hands;
using ClaudeHomeServer.DeviceAgent.Hosting;
using ClaudeHomeServer.DeviceAgent.Tests.Exec;
using ClaudeHomeServer.Protocol;

namespace ClaudeHomeServer.DeviceAgent.Tests.Hands;

/// <summary>Справка <c>hands status</c>; <c>enable|disable</c> больше ничего не ставят и не убирают.</summary>
public class HandsCommandsTests : IDisposable
{
    private readonly HandsFixture _fx = new();

    public void Dispose() => _fx.Dispose();

    private (int Code, string Out, string Err) Run(string[] args, bool supported = true)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var code = new HandsCommands(_fx.Component, output, error, supported).Run(args);
        return (code, output.ToString(), error.ToString());
    }

    [Fact]
    public void Status_показывает_мост_из_каталога_версии()
    {
        _fx.WithBridge();

        var (code, output, _) = Run(["status"]);

        code.Should().Be(0);
        output.Should().Contain("Мост рук на месте").And.Contain(_fx.Component.BridgePath);
    }

    [Fact]
    public void Status_без_моста_или_не_на_Windows_честно_отказывает()
    {
        Run(["status"]).Should().Match<(int Code, string Out, string Err)>(r => r.Code == 1 && r.Err.Contains(HandsComponent.MissingText));
        _fx.WithBridge();
        Run(["status"], supported: false).Err.Should().Contain(HandsAttach.UnsupportedText);
    }

    [Theory]
    [InlineData("enable")]
    [InlineData("disable")]
    public void Машинного_выключателя_нет_и_мост_не_трогается(string command)
    {
        _fx.WithBridge();

        var (code, _, error) = Run([command]);

        code.Should().Be(1);
        error.Should().Contain(HandsCommands.NoMachineSwitchText);
        _fx.Component.IsReady.Should().BeTrue();
    }

    [Fact]
    public void Команд_сеанса_и_белого_списка_нет()
    {
        foreach (var args in new[] { new[] { "session", "start" }, ["allow-app", "C:\\x.exe"], ["deny-app", "C:\\x.exe"], ["apps"] })
            Run(args).Code.Should().Be(64, string.Join(' ', args));
    }
}

/// <summary>Pipe «агент ↔ трей»: статус, «Стоп» без сервера, доступ только текущему пользователю.</summary>
public class HandsTrayPipeTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    private static async Task<string> RoundTripAsync(StreamReader reader, StreamWriter writer, HandsPipeMessage request)
    {
        await writer.WriteLineAsync(JsonSerializer.Serialize(request, HandsPipe.Json));
        return (await reader.ReadLineAsync().WaitAsync(Wait))!;
    }

    [Fact]
    public async Task Трей_получает_статус_и_Стоп_гасит_ход_с_руками()
    {
        var registry = new HandsRegistry();
        string? stoppedWith = null;
        using var attached = registry.Attach("turn-1", "/work", reason => stoppedWith = reason);
        var name = "ccs-test-" + Guid.NewGuid().ToString("N")[..8];
        await using var pipe = new HandsTrayPipe(name, registry, () => true, () => false);
        pipe.Start();

        await using var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await client.ConnectAsync((int)Wait.TotalMilliseconds);
        using var reader = new StreamReader(client, Encoding.UTF8);
        await using var writer = new StreamWriter(client, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };

        var status = JsonSerializer.Deserialize<HandsPipeMessage>(
            await RoundTripAsync(reader, writer, new HandsPipeMessage(HandsPipeTypes.Hello, Version: HandsPipe.Version)), HandsPipe.Json)!;
        status.Type.Should().Be(HandsPipeTypes.Status);
        status.Status!.Installed.Should().BeTrue();
        status.Status.ServerOnline.Should().BeFalse("«Стоп» работает и без сервера");
        status.Status.ActiveTurns.Should().ContainSingle().Which.TurnId.Should().Be("turn-1");

        var reply = JsonSerializer.Deserialize<HandsPipeMessage>(
            await RoundTripAsync(reader, writer, new HandsPipeMessage(HandsPipeTypes.TurnStop)), HandsPipe.Json)!;
        reply.Type.Should().Be(HandsPipeTypes.Status);
        stoppedWith.Should().Be(HandsEndReason.StoppedFromTray);
    }

    [Fact]
    public void Стоп_без_ходов_с_руками_и_незнакомые_кадры()
    {
        var pipe = new HandsTrayPipe("unused", new HandsRegistry(), () => false, () => true);

        pipe.Handle(new HandsPipeMessage(HandsPipeTypes.TurnStop))!.Type.Should().Be(HandsPipeTypes.Error);
        pipe.Handle(new HandsPipeMessage("session-start")).Should().BeNull("сеанса на машине нет — кадр пропускается");
        pipe.Handle(new HandsPipeMessage(HandsPipeTypes.Hello, Version: 99))!.Type.Should().Be(HandsPipeTypes.Error);
    }

    /// <summary>
    /// ACL pipe на Windows: ровно одно разрешающее правило — текущему пользователю, наследование
    /// выключено. Linux-раннер CI его не гоняет; живую проверку чужой учёткой — Вера в Ш10.
    /// </summary>
    [SkippableFact]
    public async Task ACL_pipe_на_Windows_только_текущий_пользователь()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "ACL pipe есть только на Windows");
#pragma warning disable CA1416 // проверено Skip.IfNot выше
        var name = "ccs-test-" + Guid.NewGuid().ToString("N")[..8];
        await using var pipe = new HandsTrayPipe(name, new HandsRegistry(), () => true, () => true);
        await using var server = pipe.Create(firstInstance: true);

        var rules = server.GetAccessControl().GetAccessRules(true, true, typeof(SecurityIdentifier))
            .Cast<PipeAccessRule>().ToList();
        using var identity = WindowsIdentity.GetCurrent();
        var me = identity.User!;

        server.GetAccessControl().AreAccessRulesProtected.Should().BeTrue("наследование от родителя отключено");
        server.GetAccessControl().GetOwner(typeof(SecurityIdentifier)).Should().Be(identity.Owner,
            "клиент с CurrentUserOnly сверяет владельца с WindowsIdentity.Owner; у повышенного процесса это Administrators");
        rules.Should().ContainSingle();
        rules[0].IdentityReference.Should().Be(me);
        rules[0].AccessControlType.Should().Be(AccessControlType.Allow);
#pragma warning restore CA1416
    }
}

/// <summary>Hello объявляет руки только агентом, в каталоге версии которого лежит мост.</summary>
public class HandsHelloTests : IDisposable
{
    private readonly HandsFixture _fx = new();

    public void Dispose() => _fx.Dispose();

    private sealed class Control(DeviceHelloAck ack) : IControlConnection
    {
        public List<DeviceHello> Hellos { get; } = [];
        public event Func<DeviceExecOpenCommand, Task>? ExecOpen { add { } remove { } }
        public event Func<Task>? Reconnected { add { } remove { } }

        public Task<DeviceHelloAck> HelloAsync(DeviceHello hello, CancellationToken ct)
        {
            Hellos.Add(hello);
            return Task.FromResult(ack);
        }
    }

    private sealed class Harness : IHarness
    {
        public string? ActiveVersion => "2.1.0";
        public void SetRequiredVersion(string? version) { }
        public event Action<ClaudeHomeServer.DeviceAgent.Cli.HarnessStatus>? Changed { add { } remove { } }
    }

    private static readonly DeviceHelloAck Ack = new(1, 2, 3, 4);

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Руки_в_hello_только_когда_мост_лежит_в_каталоге_версии(bool bridge)
    {
        if (bridge) _fx.WithBridge();
        var control = new Control(Ack);
        await using var coordinator = new AgentCoordinator(control, new Harness(), new ExecTestServer(),
            (_, _) => Task.CompletedTask, "1.2.3", hands: _fx.Runtime());

        await coordinator.HelloAsync();

        if (bridge) control.Hellos.Single().Capabilities.Should().Contain(DeviceCapabilities.Hands);
        else control.Hellos.Single().Capabilities.Should().NotContain(DeviceCapabilities.Hands);
    }
}
