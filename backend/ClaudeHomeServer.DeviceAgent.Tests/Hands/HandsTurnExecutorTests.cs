using System.Runtime.Versioning;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
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
/// вместе с ним, причина уходит серверу. Замок машины держит мост, а не агент: агент только
/// говорит мосту «ход окончен» по строке result в выводе CLI.
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
    public async Task Ход_кончился_сам_руки_доступны()
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
    }

    [SkippableFact]
    public async Task Два_хода_с_руками_на_одной_машине_стартуют_одновременно()
    {
        _fx.WithBridge();
        // Два исполнителя над одним реестром и одними событиями — как два чата на одной машине
        var hands = _fx.Runtime();
        await using var first = NewHarness(hands);
        await using var second = NewHarness(hands);

        await first.StartAsync(HandsSpawn(first), turnId: "turn-a");
        await first.WaitStdoutAsync("\"init\"", new StringBuilder(), Wait);
        await second.StartAsync(HandsSpawn(second), turnId: "turn-b");
        await second.WaitStdoutAsync("\"init\"", new StringBuilder(), Wait);

        // Замок машины берёт мост при первом действии, а не агент на старте: отказа запуска нет
        hands.Registry.Active.Select(t => t.TurnId).Should().BeEquivalentTo("turn-a", "turn-b");
        _fx.Activity.Names.Should().HaveCount(2);
    }

    [SkippableFact]
    public async Task Конец_хода_в_выводе_CLI_отпускает_руки_моста_а_процесс_живёт_дальше()
    {
        _fx.WithBridge();
        var sink = new RecordingSink();
        var hands = _fx.Runtime(sink: sink);
        await using var h = NewHarness(hands);

        await h.StartAsync(HandsSpawn(h), turnId: "turn-end");
        await h.WaitStdoutAsync("\"init\"", new StringBuilder(), Wait);
        var name = _fx.Activity.Names.Should().ContainSingle().Subject;
        _fx.Activity.TurnEnds(name).Should().Be(0, "ход ещё идёт — мост держит замок, если уже действовал");

        // Первый ход разговора кончился: мост отпускает замок, CLI ждёт следующего ввода
        var ended = TurnEnded(name);
        await h.SendStdinAsync("result\n");
        await ended.WaitAsync(Wait);
        _fx.Activity.TurnEnds(name).Should().Be(1);
        await h.SendStdinAsync("ping\n");
        await h.WaitStdoutAsync("echo:ping", new StringBuilder(), Wait);
        hands.Registry.Active.Should().ContainSingle("конец хода в выводе не гасит процесс и не отцепляет руки");

        // Следующий ход — снова свой конец
        ended = TurnEnded(name);
        await h.SendStdinAsync("result\n");
        await ended.WaitAsync(Wait);
        _fx.Activity.TurnEnds(name).Should().Be(2);
        sink.Reports.Should().BeEmpty("без действий и занятости донесений нет");
    }

    [SkippableFact]
    public async Task Занятые_руки_доносятся_серверу_и_снимаются_концом_хода()
    {
        _fx.WithBridge();
        var sink = new RecordingSink();
        var hands = _fx.Runtime(sink: sink);
        await using var h = NewHarness(hands);

        await h.StartAsync(HandsSpawn(h), turnId: "turn-busy");
        await h.WaitStdoutAsync("\"init\"", new StringBuilder(), Wait);
        var name = _fx.Activity.Names.Should().ContainSingle().Subject;

        // Мост дважды упёрся в замок другого хода — бейдж «занято» уходит один раз
        _fx.Activity.RaiseBusy(name).Should().BeTrue();
        _fx.Activity.RaiseBusy(name).Should().BeTrue();
        (await sink.WaitAsync(r => r.State == HandsChatStates.Unavailable, Wait))
            .Should().Be(new DeviceHandsReport("turn-busy", HandsChatStates.Unavailable, HandsEndReason.Busy));

        await h.SendStdinAsync("result\n");
        await sink.WaitAsync(r => r.State == HandsChatStates.Allowed, Wait);
        sink.Reports.Select(r => r.State).Should().Equal(HandsChatStates.Unavailable, HandsChatStates.Allowed);
        hands.Registry.Active.Should().ContainSingle("занятость не отцепляет руки и не гасит ход");
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
        _fx.Activity.Names.Should().BeEmpty("ход без рук событий моста не создаёт");
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

    [SkippableFact]
    public async Task Оба_аргумента_моста_переданы_OCR_содержит_ключ_а_не_токен()
    {
        _fx.WithBridge();
        var hands = _fx.Runtime();
        await using var h = NewHarness(hands);

        await h.StartAsync(HandsSpawn(h), turnId: "turn-args");
        await h.WaitStdoutAsync("\"init\"", new StringBuilder(), Wait);

        // Конфиг, который увидел CLI: узел рук с --project-root (реальный корень) и --ocr-endpoint (ключ хода)
        var argv = File.ReadAllLines(Path.Combine(h.WorkDir, "cli-argv.txt"));
        var mcpPath = argv[Array.IndexOf(argv, "--mcp-config") + 1];
        var config = File.ReadAllText(Path.Combine(h.WorkDir, "cli-file-" + Path.GetFileName(mcpPath)));
        var bridgeArgs = BridgeArgsOf(JsonNode.Parse(config)!);

        // Корень записи — реальный каталог хода, а не произвольный путь из spec
        bridgeArgs.Should().ContainInConsecutiveOrder(HandsBridgeArgs.ProjectRoot, h.WorkDir);

        // OCR: адрес хода в сайдкаре с ключом (32 hex); токен хода и подписки в нём не содержатся
        var ocr = bridgeArgs[bridgeArgs.IndexOf(HandsBridgeArgs.OcrEndpoint) + 1];
        ocr.Should().MatchRegex("^" + Regex.Escape(TurnHarness.SidecarUrl) + @"/t/[0-9a-f]{32}/ocr$",
            "OCR — адрес хода в сайдкаре: ключ хода, не id сервера");
        foreach (var secret in new[] { TurnHarness.TurnToken }.Concat(TurnHarness.AgentSecrets.Values))
            ocr.Should().NotContain(secret, "токен хода и подписки в адрес OCR не попадают");
    }

    [Fact]
    public void Корень_проекта_не_существует_аргумента_нет()
    {
        _fx.WithBridge();
        var missing = Path.Combine(_fx.AgentDirectory, "no-such-dir");
        var spawn = HandsSpawnWith(HandsFixture.McpFile(HandsFixture.McpConfig()));

        using var lease = HandsAttach.Prepare(spawn, "t1", _fx.Runtime(), projectRoot: missing)!;

        BridgeArgsOf(lease).Should().NotContain(HandsBridgeArgs.ProjectRoot,
            "корня нет — saveTo моста отказывает, аргумент не передаём");
    }

    [Fact]
    public void Сервер_не_подменяет_корень_проекта()
    {
        _fx.WithBridge();
        var real = Directory.CreateDirectory(Path.Combine(_fx.AgentDirectory, "real-project")).FullName;
        // Сервер кладёт в узел рук свой --project-root: агент его не должен брать
        var config = new JsonObject
        {
            ["mcpServers"] = new JsonObject
            {
                [DeviceExecPlaceholders.HandsServerName] = new JsonObject
                {
                    ["type"] = DeviceExecPlaceholders.Hands,
                    ["args"] = new JsonArray { HandsBridgeArgs.ProjectRoot, "/evil/server-path" },
                },
            },
        }.ToJsonString();

        using var lease = HandsAttach.Prepare(HandsSpawnWith(HandsFixture.McpFile(config)), "t1", _fx.Runtime(),
            projectRoot: real)!;

        BridgeArgsOf(lease).Should().ContainInConsecutiveOrder(new[] { HandsBridgeArgs.ProjectRoot, real },
            "корень — реальный каталог хода, а не тот, что в аргументах, присланных сервером");
    }

    /// <summary>Аргументы узла моста из материализованного MCP-конфига.</summary>
    private static List<string> BridgeArgsOf(JsonNode config) =>
        (config!["mcpServers"]![DeviceExecPlaceholders.HandsServerName]!["args"]!.AsArray())
            .Select(a => a!.GetValue<string>()).ToList();

    /// <summary>Аргументы узла моста из аренды рук.</summary>
    private static List<string> BridgeArgsOf(HandsTurnLease lease) =>
        BridgeArgsOf(JsonNode.Parse(lease.Files.Single().Content)!);

    private static DeviceExecSpawn HandsSpawnWith(DeviceExecFile file) =>
        new("claude", HandsFixture.HandsArgs, "/work", new Dictionary<string, string>(), [file], RedirectStdin: true);

    /// <summary>Агент поднимет «ход окончен» для хода с этим событием «подействовал».</summary>
    private Task TurnEnded(string name)
    {
        var ended = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _fx.Activity.TurnEnded += n => { if (n == name) ended.TrySetResult(); };
        return ended.Task;
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
