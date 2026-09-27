using System.Runtime.Versioning;
using System.Text;
using System.Text.Json.Nodes;
using ClaudeHomeServer.DeviceAgent.Exec;
using ClaudeHomeServer.DeviceAgent.Hands;
using ClaudeHomeServer.DeviceAgent.Processes;
using ClaudeHomeServer.DeviceAgent.Tests.Exec;
using ClaudeHomeServer.Protocol;

namespace ClaudeHomeServer.DeviceAgent.Tests.Hands;

/// <summary>
/// Ход с руками целиком (Unix, фейковый CLI): мост — потомок CLI, как у настоящего CLI со
/// stdio-узлом MCP; у фейка его роль играет долгоживущий внук. «Стоп» из трея гасит дерево хода
/// вместе с ним, причина уходит серверу; руки машины — одни на всех.
/// </summary>
[UnsupportedOSPlatform("windows")]
public class HandsTurnExecutorTests : IDisposable
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(20);

    private readonly HandsFixture _fx = new();

    public void Dispose() => _fx.Dispose();

    private static TurnHarness NewHarness(HandsRuntime hands)
    {
        Skip.If(OperatingSystem.IsWindows(), "ветка Unix: группа процессов через setsid");
        Skip.If(UnixGroupProcess.FindSetsid() is null, "нет setsid на этой машине");
        var cliDir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "fake-cli-" + Guid.NewGuid().ToString("N")[..8]));
        return new TurnHarness(FakeUnixCli.Write(cliDir.FullName), hands: hands);
    }

    private static DeviceExecSpawn HandsSpawn(TurnHarness h, bool vision = false) =>
        h.Spawn(args: HandsFixture.HandsArgs, files: [HandsFixture.McpFile(HandsFixture.McpConfig(vision))]);

    [SkippableFact]
    public async Task Стоп_из_трея_гасит_ход_вместе_с_мостом_и_сообщает_причину()
    {
        _fx.Installed();
        var sink = new RecordingSink();
        var hands = _fx.Runtime(sink: sink);
        await using var h = NewHarness(hands);

        await h.StartAsync(HandsSpawn(h), turnId: "turn-hands");
        await h.WaitStdoutAsync("\"init\"", new StringBuilder(), Wait);
        var bridge = h.ReadPid("grandchild.pid");
        await sink.WaitAsync(r => r.State == HandsChatStates.Active, Wait);
        hands.Registry.Active.Should().ContainSingle().Which.TurnId.Should().Be("turn-hands");

        // Конфиг, который увидел CLI: узел рук — наш мост с Job хода, маркера нет
        var argv = File.ReadAllLines(Path.Combine(h.WorkDir, "cli-argv.txt"));
        var mcpPath = argv[Array.IndexOf(argv, "--mcp-config") + 1];
        var config = File.ReadAllText(Path.Combine(h.WorkDir, "cli-file-" + Path.GetFileName(mcpPath)));
        config.Should().NotContain(DeviceExecPlaceholders.Hands);
        var node = JsonNode.Parse(config)!["mcpServers"]![DeviceExecPlaceholders.HandsServerName]!;
        ((string?)node["command"]).Should().Be(_fx.Component.BridgePath);
        node["args"]!.AsArray().Select(a => (string?)a).Should().Contain(HandsAttach.ScreenshotTool, "у провайдера без зрения");

        hands.Registry.Stop(null, HandsEndReason.StoppedFromTray).Should().Be(1);

        var frames = await h.Server.ReadUntilExitAsync(Wait);
        await h.Run!.WaitAsync(Wait);
        TurnHarness.ExitOf(frames).Signal.Should().Be("SIGKILL");
        await WaitDeadAsync(bridge);
        var stopped = await sink.WaitAsync(r => r.State == HandsChatStates.Stopped, Wait);
        stopped.Should().Be(new DeviceHandsReport("turn-hands", HandsChatStates.Stopped, HandsEndReason.StoppedFromTray));
        hands.Registry.Active.Should().BeEmpty();
        h.Cli.Open.Should().Be(0);

        // Следующий ход снова может взять руки: «Стоп» гасит ход, а не руки машины навсегда
        using var next = hands.MachineLock.TryAcquire();
        next.Should().NotBeNull();
    }

    [SkippableFact]
    public async Task Ход_кончился_сам_руки_доступны_и_замок_свободен()
    {
        _fx.Installed();
        var sink = new RecordingSink();
        var hands = _fx.Runtime(sink: sink);
        await using var h = NewHarness(hands);

        await h.StartAsync(HandsSpawn(h, vision: true), turnId: "turn-own");
        await h.WaitStdoutAsync("\"init\"", new StringBuilder(), Wait);
        await h.SendStdinAsync("exit\n");
        await h.Server.ReadUntilExitAsync(Wait);
        await h.Run!.WaitAsync(Wait);

        (await sink.WaitAsync(r => r.State == HandsChatStates.Allowed, Wait)).TurnId.Should().Be("turn-own");
        sink.Reports.Should().NotContain(r => r.State == HandsChatStates.Stopped);
        using var next = hands.MachineLock.TryAcquire();
        next.Should().NotBeNull("конец хода отдаёт руки машины");
    }

    [SkippableFact]
    public async Task Второй_ход_с_руками_на_той_же_машине_получает_отказ()
    {
        _fx.Installed();
        // Два агента одной машины делят один замок — как именованный семафор сеанса входа
        var machine = new InProcessHandsMachineLock();
        await using var first = NewHarness(_fx.Runtime(machine));
        await using var second = NewHarness(_fx.Runtime(machine));

        await first.StartAsync(HandsSpawn(first), turnId: "turn-a");
        await first.WaitStdoutAsync("\"init\"", new StringBuilder(), Wait);

        await second.StartAsync(HandsSpawn(second), turnId: "turn-b");
        var frames = await second.Server.ReadUntilExitAsync(Wait);
        await second.Run!.WaitAsync(Wait);

        var exit = TurnHarness.ExitOf(frames);
        exit.Code.Should().Be(TurnExecutor.RefusedExitCode);
        exit.Error.Should().Be(HandsMachineLock.BusyText);
        second.Cli.Acquired.Should().Be(0, "отказ по рукам не занимает копию CLI");
        Directory.Exists(Path.Combine(second.TurnsRoot, "turn-b")).Should().BeFalse();
    }

    [SkippableFact]
    public async Task Ход_без_маркера_идёт_без_рук_даже_при_установленном_компоненте()
    {
        _fx.Installed();
        var sink = new RecordingSink();
        var hands = _fx.Runtime(sink: sink);
        await using var h = NewHarness(hands);

        await h.StartAsync(h.Spawn(args: ["-p", "--mcp-config", DeviceExecPlaceholders.File("f1")],
            files: [HandsFixture.McpFile(HandsFixture.McpConfig(hands: false))]));
        await h.WaitStdoutAsync("\"init\"", new StringBuilder(), Wait);

        hands.Registry.Active.Should().BeEmpty();
        using (var free = hands.MachineLock.TryAcquire()) free.Should().NotBeNull("ход без рук замок машины не берёт");
        await h.SendStdinAsync("exit\n");
        await h.Server.ReadUntilExitAsync(Wait);
        sink.Reports.Should().BeEmpty();
    }

    private static async Task WaitDeadAsync(int pid)
    {
        // Внук — не наш потомок, события выхода у него нет: тот же опрос, что у соседних тестов убийства дерева
        for (var i = 0; i < 100 && UnixGroupProcess.IsAlive(pid); i++) await Task.Delay(50);
        UnixGroupProcess.IsAlive(pid).Should().BeFalse("мост (внук CLI) погашен вместе с ходом");
    }
}
