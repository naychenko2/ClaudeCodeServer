using System.Runtime.Versioning;
using System.Text;
using System.Text.Json.Nodes;
using ClaudeHomeServer.DeviceAgent.Composition;
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

    private static TurnHarness NewHarness(HandsRuntime hands, TimeSpan? drainTimeout = null)
    {
        Skip.If(OperatingSystem.IsWindows(), "ветка Unix: группа процессов через setsid");
        Skip.If(UnixGroupProcess.FindSetsid() is null, "нет setsid на этой машине");
        var cliDir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "fake-cli-" + Guid.NewGuid().ToString("N")[..8]));
        return new TurnHarness(FakeUnixCli.Write(cliDir.FullName), hands: hands, drainTimeout: drainTimeout);
    }

    private static DeviceExecSpawn HandsSpawn(TurnHarness h, bool vision = false) =>
        h.Spawn(args: HandsFixture.HandsArgs, files: [HandsFixture.McpFile(HandsFixture.McpConfig(vision))]);

    [SkippableFact]
    public async Task Стоп_из_трея_гасит_ход_вместе_с_мостом_и_сообщает_причину()
    {
        _fx.WithBridge();
        var sink = new RecordingSink();
        var hands = _fx.Runtime(sink: sink, browserProfiles: true);
        await using var h = NewHarness(hands);

        await h.StartAsync(HandsSpawn(h), turnId: "turn-hands");
        await h.WaitStdoutAsync("\"init\"", new StringBuilder(), Wait);
        var bridge = h.ReadPid("grandchild.pid");
        hands.Registry.Active.Should().ContainSingle().Which.TurnId.Should().Be("turn-hands");
        await ActAsync(hands, sink);

        // Конфиг, который увидел CLI: узел рук — наш мост с Job хода, маркера нет
        var argv = File.ReadAllLines(Path.Combine(h.WorkDir, "cli-argv.txt"));
        var mcpPath = argv[Array.IndexOf(argv, "--mcp-config") + 1];
        var config = File.ReadAllText(Path.Combine(h.WorkDir, "cli-file-" + Path.GetFileName(mcpPath)));
        config.Should().NotContain(DeviceExecPlaceholders.Hands);
        var node = JsonNode.Parse(config)!["mcpServers"]![DeviceExecPlaceholders.HandsServerName]!;
        ((string?)node["command"]).Should().Be(_fx.Component.BridgePath);
        var bridgeArgs = node["args"]!.AsArray().Select(a => (string?)a).ToList();
        bridgeArgs.Should().Contain(string.Join(",", HandsVision.ImageTools), "у провайдера без зрения");
        // Профиль — от корня, который агент сверил сам, а не от сырого каталога из spec
        bridgeArgs.Should().ContainInConsecutiveOrder(HandsBridgeArgs.BrowserProfile,
            HandsBrowserProfile.PathFor(_fx.BrowserProfilesRoot, AgentPathPolicy.RealPath(h.WorkDir)));

        hands.Registry.Stop(null, HandsEndReason.StoppedFromTray).Should().Be(1);

        var frames = await h.Server.ReadUntilExitAsync(Wait);
        await h.Run!.WaitAsync(Wait);
        TurnHarness.ExitOf(frames).Signal.Should().Be("SIGKILL");
        TurnHarness.ExitOf(frames).StoppedBy.Should().Be(HandsEndReason.StoppedFromTray,
            "причина едет в кадре конца процесса — сервер узнаёт о «Стопе» раньше, чем о смерти CLI");
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
    public async Task Стоп_из_трея_без_связи_гасит_руки_сразу_а_причина_доходит_после_возврата_связи()
    {
        _fx.WithBridge();
        var sink = new RecordingSink();
        var hands = _fx.Runtime(sink: sink);
        // Обычный конец хода ждёт подтверждения 300 мс — «Стоп» обязан ждать связи дольше
        await using var h = NewHarness(hands, drainTimeout: TimeSpan.FromMilliseconds(300));

        await h.StartAsync(HandsSpawn(h), turnId: "turn-offline");
        await h.WaitStdoutAsync("\"init\"", new StringBuilder(), Wait);
        var bridge = h.ReadPid("grandchild.pid");
        // Руки подключены, но ещё не действовали — «Стоп» гасит ход и в этом состоянии
        hands.Registry.Active.Should().ContainSingle().Which.IsActing.Should().BeFalse();

        // Сервер пропал, «Стоп» нажат без связи: руки гаснут на машине, не дожидаясь сервера
        h.Server.ReconnectGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Server.Break();
        hands.Registry.Stop(null, HandsEndReason.StoppedFromTray).Should().Be(1);
        await WaitDeadAsync(bridge);
        await sink.WaitAsync(r => r.State == HandsChatStates.Stopped, Wait);
        hands.Registry.Active.Should().BeEmpty("трей не показывает руки занятыми, пока агент ждёт сервер");
        using (var free = hands.MachineLock.TryAcquire())
            free.Should().NotBeNull("руки машины отпущены сразу, а не по подтверждению сервера");

        // Связи нет дольше обычного ожидания подтверждения — кадр конца хода не брошен
        await Task.Delay(TimeSpan.FromMilliseconds(1500));
        h.Run!.IsCompleted.Should().BeFalse("ход, погашенный человеком, ждёт связи, чтобы донести причину");

        h.Server.ReconnectGate.SetResult();
        var frames = await h.Server.ReadUntilExitAsync(Wait);
        await h.Run!.WaitAsync(Wait);
        TurnHarness.ExitOf(frames).StoppedBy.Should().Be(HandsEndReason.StoppedFromTray,
            "по возврату связи сервер узнаёт, что ход остановил человек, — и не уводит его в фолбэк");
    }

    [SkippableFact]
    public async Task Ход_кончился_сам_руки_доступны_и_замок_свободен()
    {
        _fx.WithBridge();
        var sink = new RecordingSink();
        var hands = _fx.Runtime(sink: sink);
        await using var h = NewHarness(hands);

        await h.StartAsync(HandsSpawn(h, vision: true), turnId: "turn-own");
        await h.WaitStdoutAsync("\"init\"", new StringBuilder(), Wait);
        await h.SendStdinAsync("exit\n");
        await h.Server.ReadUntilExitAsync(Wait);
        await h.Run!.WaitAsync(Wait);

        (await sink.WaitAsync(r => r.State == HandsChatStates.Allowed, Wait)).TurnId.Should().Be("turn-own");
        sink.Reports.Should().ContainSingle("ход без действия рук доносит только итог — «действует руками» не уходит")
            .Which.State.Should().Be(HandsChatStates.Allowed);
        using var next = hands.MachineLock.TryAcquire();
        next.Should().NotBeNull("конец хода отдаёт руки машины");
    }

    [SkippableFact]
    public async Task Второй_ход_с_руками_на_той_же_машине_получает_отказ()
    {
        _fx.WithBridge();
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
        _fx.WithBridge();
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

    [SkippableFact]
    public async Task Действует_руками_уходит_только_по_событию_моста_и_ровно_один_раз()
    {
        _fx.WithBridge();
        var sink = new RecordingSink();
        var hands = _fx.Runtime(sink: sink);
        await using var h = NewHarness(hands);

        await h.StartAsync(HandsSpawn(h), turnId: "turn-act");
        await h.WaitStdoutAsync("\"init\"", new StringBuilder(), Wait);

        // Разговор: руки подключены, мост ещё не действовал — ни плашки, ни донесения
        var name = _fx.Activity.Names.Should().ContainSingle().Subject;
        name.Should().StartWith(HandsBridgeArgs.ActivityEventPrefix + "turn-act.");
        var argv = File.ReadAllLines(Path.Combine(h.WorkDir, "cli-argv.txt"));
        var config = File.ReadAllText(Path.Combine(h.WorkDir, "cli-file-" + Path.GetFileName(argv[Array.IndexOf(argv, "--mcp-config") + 1])));
        JsonNode.Parse(config)!["mcpServers"]![DeviceExecPlaceholders.HandsServerName]!["args"]!.AsArray()
            .Select(a => (string?)a).Should().ContainInConsecutiveOrder(HandsBridgeArgs.ActivityEvent, name);
        hands.Registry.Active.Should().ContainSingle().Which.IsActing.Should().BeFalse();
        sink.Reports.Should().BeEmpty();

        // Первое действие моста — «действует руками»; повторный сигнал ничего не добавляет
        _fx.Activity.Raise(name).Should().BeTrue();
        _fx.Activity.Raise(name).Should().BeTrue();
        (await sink.WaitAsync(r => r.State == HandsChatStates.Active, Wait)).TurnId.Should().Be("turn-act");
        hands.Registry.Active.Should().ContainSingle().Which.IsActing.Should().BeTrue();

        await h.SendStdinAsync("exit\n");
        await h.Server.ReadUntilExitAsync(Wait);
        await h.Run!.WaitAsync(Wait);
        await sink.WaitAsync(r => r.State == HandsChatStates.Allowed, Wait);
        sink.Reports.Select(r => r.State).Should().Equal(HandsChatStates.Active, HandsChatStates.Allowed);
        _fx.Activity.Names.Should().BeEmpty("событие хода закрыто вместе с руками");
        _fx.Activity.Raise(name).Should().BeFalse("после конца хода поднимать некому");
    }

    [SkippableFact]
    public async Task Без_событий_у_агента_ход_с_руками_не_становится_действующим()
    {
        _fx.WithBridge();
        var sink = new RecordingSink();
        var hands = _fx.Runtime(sink: sink, activityEvents: false);
        await using var h = NewHarness(hands);

        await h.StartAsync(HandsSpawn(h), turnId: "turn-quiet");
        await h.WaitStdoutAsync("\"init\"", new StringBuilder(), Wait);
        var argv = File.ReadAllLines(Path.Combine(h.WorkDir, "cli-argv.txt"));
        File.ReadAllText(Path.Combine(h.WorkDir, "cli-file-" + Path.GetFileName(argv[Array.IndexOf(argv, "--mcp-config") + 1])))
            .Should().NotContain(HandsBridgeArgs.ActivityEvent);

        await h.SendStdinAsync("exit\n");
        await h.Server.ReadUntilExitAsync(Wait);
        await h.Run!.WaitAsync(Wait);
        await sink.WaitAsync(r => r.State == HandsChatStates.Allowed, Wait);
        sink.Reports.Should().NotContain(r => r.State == HandsChatStates.Active);
    }

    /// <summary>Мост подействовал: поднять событие хода и дождаться «действует руками».</summary>
    private async Task ActAsync(HandsRuntime hands, RecordingSink sink)
    {
        _fx.Activity.Raise(_fx.Activity.Names.Should().ContainSingle().Subject).Should().BeTrue();
        await sink.WaitAsync(r => r.State == HandsChatStates.Active, Wait);
        hands.Registry.Active.Should().ContainSingle().Which.IsActing.Should().BeTrue();
    }

    private static async Task WaitDeadAsync(int pid)
    {
        // Внук — не наш потомок, события выхода у него нет: тот же опрос, что у соседних тестов убийства дерева
        for (var i = 0; i < 100 && UnixGroupProcess.IsAlive(pid); i++) await Task.Delay(50);
        UnixGroupProcess.IsAlive(pid).Should().BeFalse("мост (внук CLI) погашен вместе с ходом");
    }
}
