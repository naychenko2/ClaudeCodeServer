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

/// <summary>Команды машины <c>hands enable|disable|status</c>: скачивание по манифесту и сверка SHA-256.</summary>
public class HandsCommandsTests : IDisposable
{
    private static readonly Uri Server = new("https://home.example/");

    private readonly HandsFixture _fx = new();

    public void Dispose() => _fx.Dispose();

    private sealed class ArchiveServer(byte[] body) : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request.RequestUri!);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) });
        }
    }

    private (HandsCommands Commands, ArchiveServer Server, StringWriter Out, StringWriter Err) Commands(
        byte[] served, bool supported = true, string agentVersion = "1.2.3")
    {
        var server = new ArchiveServer(served);
        var output = new StringWriter();
        var error = new StringWriter();
        return (new HandsCommands(_fx.Component, () => Server, new HttpClient(server), agentVersion, output, error, supported),
            server, output, error);
    }

    [Fact]
    public async Task Enable_качает_архив_по_пути_из_манифеста_и_ставит_компонент()
    {
        var (path, offer) = _fx.Archive();
        _fx.Component.SaveOffer(offer);
        var (commands, server, output, _) = Commands(File.ReadAllBytes(path));

        (await commands.RunAsync(["enable"], CancellationToken.None)).Should().Be(0);

        server.Requests.Should().Equal(new Uri(Server, "agent/" + offer.Path));
        _fx.Component.Check().Ready.Should().BeTrue();
        output.ToString().Should().Contain("Руки установлены");
    }

    [Fact]
    public async Task Enable_с_подменённым_архивом_ничего_не_ставит()
    {
        var (path, offer) = _fx.Archive();
        _fx.Component.SaveOffer(offer);
        var tampered = File.ReadAllBytes(path);
        tampered[^1] ^= 0xFF;
        var (commands, _, _, error) = Commands(tampered);

        (await commands.RunAsync(["enable"], CancellationToken.None)).Should().Be(1);

        error.ToString().Should().Contain("SHA-256");
        _fx.Component.Check().Ready.Should().BeFalse();
    }

    [Fact]
    public async Task Enable_без_сведений_сервера_или_не_на_Windows_отказывает()
    {
        var (commands, server, _, error) = Commands([]);
        (await commands.RunAsync(["enable"], CancellationToken.None)).Should().Be(1);
        error.ToString().Should().Contain("Сервер ещё не прислал");

        var (_, offer) = _fx.Archive();
        _fx.Component.SaveOffer(offer);
        var (linux, _, _, linuxError) = Commands([], supported: false);
        (await linux.RunAsync(["enable"], CancellationToken.None)).Should().Be(1);
        linuxError.ToString().Should().Be(HandsAttach.UnsupportedText + Environment.NewLine);

        var (stale, _, _, staleError) = Commands([], agentVersion: "1.2.4");
        (await stale.RunAsync(["enable"], CancellationToken.None)).Should().Be(1);
        staleError.ToString().Should().Contain("для агента 1.2.3");
        server.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task Disable_и_status()
    {
        _fx.Installed();
        var (commands, _, output, _) = Commands([]);

        (await commands.RunAsync(["status"], CancellationToken.None)).Should().Be(0);
        (await commands.RunAsync(["disable"], CancellationToken.None)).Should().Be(0);
        (await commands.RunAsync(["status"], CancellationToken.None)).Should().Be(1);

        output.ToString().Should().Contain("Руки установлены и сверены").And.Contain(HandsComponent.NotInstalledText);
    }

    [Fact]
    public async Task Команд_сеанса_и_белого_списка_нет()
    {
        var (commands, _, _, _) = Commands([]);

        foreach (var args in new[] { new[] { "session", "start" }, ["allow-app", "C:\\x.exe"], ["deny-app", "C:\\x.exe"], ["apps"] })
            (await commands.RunAsync(args, CancellationToken.None)).Should().Be(64, string.Join(' ', args));
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
        var me = WindowsIdentity.GetCurrent().User!;

        server.GetAccessControl().AreAccessRulesProtected.Should().BeTrue("наследование от родителя отключено");
        rules.Should().ContainSingle();
        rules[0].IdentityReference.Should().Be(me);
        rules[0].AccessControlType.Should().Be(AccessControlType.Allow);
#pragma warning restore CA1416
    }
}

/// <summary>Hello объявляет руки только при сверенном компоненте; сведения о компоненте — из ответа сервера.</summary>
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

    private static readonly DeviceHelloAck Ack = new(1, 2, 3, 4, HandsArchivePath: "1.2.3/win-x64/hands-1.2.3-win-x64.zip",
        HandsArchiveSha256: new string('b', 64), HandsArchiveSize: 42);

    [Fact]
    public async Task Руки_в_hello_только_при_сверенном_компоненте_и_перемена_повторяет_hello()
    {
        var control = new Control(Ack);
        var hands = _fx.Runtime();
        await using var coordinator = new AgentCoordinator(control, new Harness(), new ExecTestServer(),
            (_, _) => Task.CompletedTask, "1.2.3", hands: hands);

        await coordinator.HelloAsync();
        control.Hellos.Last().Capabilities.Should().NotContain(DeviceCapabilities.Hands);
        _fx.Component.ReadOffer().Should().Be(new HandsOffer("1.2.3", Ack.HandsArchivePath!, Ack.HandsArchiveSha256!, 42));

        _fx.Installed();
        await coordinator.RefreshHandsAsync();
        control.Hellos.Should().HaveCount(2, "установка компонента — повод объявить руки");
        control.Hellos.Last().Capabilities.Should().Contain(DeviceCapabilities.Hands);

        await coordinator.RefreshHandsAsync();
        control.Hellos.Should().HaveCount(2, "без перемены hello не повторяется");
    }

    [Fact]
    public async Task Компонент_убран_посреди_хода_ход_с_руками_гасится()
    {
        _fx.Installed();
        var hands = _fx.Runtime();
        string? reason = null;
        using var turn = hands.Registry.Attach("turn-1", null, r => reason = r);
        await using var coordinator = new AgentCoordinator(new Control(Ack), new Harness(), new ExecTestServer(),
            (_, _) => Task.CompletedTask, "1.2.3", hands: hands);

        _fx.Component.Remove();
        await coordinator.RefreshHandsAsync();

        reason.Should().Be(HandsEndReason.HandsDisabled);
    }
}
