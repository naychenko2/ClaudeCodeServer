using System.Runtime.Versioning;
using System.Text;
using ClaudeHomeServer.DeviceAgent.Exec;
using ClaudeHomeServer.DeviceAgent.Processes;
using ClaudeHomeServer.Protocol;

namespace ClaudeHomeServer.DeviceAgent.Tests.Exec;

/// <summary>
/// Сквозной ход на Unix: spawn по связи → управляемая копия (фейковый CLI) → stdio кадрами
/// → Exit. Ветка Linux/macOS (setsid, группа процессов); на Windows — свой набор.
/// </summary>
[UnsupportedOSPlatform("windows")]
public class TurnExecutorUnixTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(20);

    private static TurnHarness NewHarness(string? leaseProblem = null)
    {
        Skip.If(OperatingSystem.IsWindows(), "ветка Unix: группа процессов через setsid");
        Skip.If(UnixGroupProcess.FindSetsid() is null, "нет setsid на этой машине");
        var cliDir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "fake-cli-" + Guid.NewGuid().ToString("N")[..8]));
        return new TurnHarness(FakeUnixCli.Write(cliDir.FullName), leaseProblem);
    }

    [SkippableFact]
    public async Task Ход_гоняет_stdio_и_завершается_кодом_CLI()
    {
        await using var h = NewHarness();
        await h.StartAsync(h.Spawn());
        var info = await h.Server.Received.ReadAsync().AsTask().WaitAsync(Wait);
        info.Channel.Should().Be(DeviceExecFrameChannel.Info, "первым агент сообщает pid и версию копии");
        System.Text.Encoding.UTF8.GetString(info.Payload.Span).Should().Contain("9.9.9-test");
        var stdout = new StringBuilder();
        await h.WaitStdoutAsync("\"init\"", stdout, Wait);

        await h.SendStdinAsync("привет\n");
        await h.WaitStdoutAsync("echo:привет", stdout, Wait);
        await h.SendStdinAsync("exit\n");

        var frames = await h.Server.ReadUntilExitAsync(Wait);
        await h.Run!.WaitAsync(Wait);

        var exit = TurnHarness.ExitOf(frames);
        exit.Code.Should().Be(3);
        exit.Signal.Should().BeNull();
    }

    [SkippableFact]
    public async Task Окружение_CLI_только_по_allow_list_и_без_секретов_агента()
    {
        await using var h = NewHarness();
        var env = new Dictionary<string, string>
        {
            ["CLAUDE_CODE_DISABLE_CLAUDE_MDS"] = "1",
            // Сервер не вправе увести CLI на свой адрес или подложить ключ
            ["ANTHROPIC_BASE_URL"] = "https://evil-from-server.example",
            ["ANTHROPIC_API_KEY"] = "sk-from-server-SECRET",
        };
        await h.StartAsync(h.Spawn(env: env));
        await h.WaitStdoutAsync("\"init\"", new StringBuilder(), Wait);
        await h.Server.SendAsync(DeviceExecFrameChannel.StdinEof, ReadOnlyMemory<byte>.Empty);
        await h.Server.ReadUntilExitAsync(Wait);
        await h.Run!.WaitAsync(Wait);

        var dumped = File.ReadAllLines(Path.Combine(h.WorkDir, "cli-env.txt"))
            .Where(l => l.Contains('='))
            .ToDictionary(l => l[..l.IndexOf('=')], l => l[(l.IndexOf('=') + 1)..]);
        // sh сам добавляет PWD/SHLVL/_ — это не наследование от агента
        var own = dumped.Keys.Except(["PWD", "SHLVL", "_", "OLDPWD"]).ToHashSet();
        own.Should().BeSubsetOf(CliEnvironment.AllNames(windows: false));

        dumped["ANTHROPIC_BASE_URL"].Should().StartWith(TurnHarness.SidecarUrl + "/t/").And.EndWith("/llm");
        dumped["ANTHROPIC_AUTH_TOKEN"].Should().Be(CliEnvironment.AuthPlaceholder);
        // Прокси — тот же сайдкар с учёткой хода: по ней сайдкар опознаёт ход CONNECT
        var turnKey = dumped["ANTHROPIC_BASE_URL"].Split('/')[^2];
        dumped["HTTPS_PROXY"].Should().Be(DeviceEgressRoutes.ProxyUrl(TurnHarness.SidecarUrl, turnKey))
            .And.Be($"http://turn:{turnKey}@127.0.0.1:65000");
        dumped["CLAUDE_CONFIG_DIR"].Should().Be(h.Profile);
        dumped["DISABLE_AUTOUPDATER"].Should().Be("1");
        dumped["DISABLE_UPDATES"].Should().Be("1");
        dumped["CLAUDE_CODE_DISABLE_CLAUDE_MDS"].Should().Be("1");

        var all = string.Join('\n', dumped.Select(kv => kv.Key + "=" + kv.Value));
        foreach (var secret in TurnHarness.AgentSecrets.Values.Append("sk-from-server-SECRET").Append(TurnHarness.TurnToken))
            all.Should().NotContain(secret);
        all.Should().NotContain("evil");
    }

    [SkippableFact]
    public async Task Файлы_spec_материализуются_в_каталоге_хода_и_убираются_по_концу()
    {
        await using var h = NewHarness();
        var mcp = """{"mcpServers":{"tasks":{"type":"http","url":"{{ccs-sidecar}}/mcp/tasks/s1"}}}""";
        var files = new[] { new DeviceExecFile("f1", "mcp.json", mcp), new DeviceExecFile("f2", "prompt.md", "Системный промпт") };
        var args = new[] { "-p", "--mcp-config", DeviceExecPlaceholders.File("f1"),
            "--append-system-prompt-file", DeviceExecPlaceholders.File("f2") };

        await h.StartAsync(h.Spawn(args, files));
        await h.WaitStdoutAsync("\"init\"", new StringBuilder(), Wait);

        var argv = File.ReadAllLines(Path.Combine(h.WorkDir, "cli-argv.txt"));
        var mcpPath = argv[2];
        mcpPath.Should().StartWith(Path.Combine(h.TurnsRoot, "turn1"));
        File.Exists(mcpPath).Should().BeTrue("пока ход идёт, файл на месте");
        if (!OperatingSystem.IsWindows())
        {
            File.GetUnixFileMode(mcpPath).Should().Be(UnixFileMode.UserRead | UnixFileMode.UserWrite);
            File.GetUnixFileMode(Path.GetDirectoryName(mcpPath)!).Should()
                .Be(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        var materialized = File.ReadAllText(Path.Combine(h.WorkDir, "cli-file-" + Path.GetFileName(mcpPath)));
        materialized.Should().Contain(TurnHarness.SidecarUrl + "/t/").And.Contain("/mcp/tasks/s1").And.NotContain("{{");
        File.ReadAllText(Path.Combine(h.WorkDir, "cli-file-" + Path.GetFileName(argv[4]))).Should().Be("Системный промпт");

        await h.Server.SendAsync(DeviceExecFrameChannel.StdinEof, ReadOnlyMemory<byte>.Empty);
        await h.Server.ReadUntilExitAsync(Wait);
        await h.Run!.WaitAsync(Wait);

        Directory.Exists(Path.Combine(h.TurnsRoot, "turn1")).Should().BeFalse("каталог хода убирается по его концу");
        h.Cli.Open.Should().Be(0, "аренда копии CLI закрывается по концу хода");
        h.Grants.Count.Should().Be(0, "выдача шлюза живёт ровно ход");
        h.Journal.ReadAll().Should().BeEmpty();
    }

    [SkippableFact]
    public async Task Kill_по_TurnId_убивает_CLI_с_внуком_и_убирает_файлы_сессии()
    {
        await using var h = NewHarness();
        await h.StartAsync(h.Spawn(), turnId: "turn-kill");
        await h.WaitStdoutAsync("\"init\"", new StringBuilder(), Wait);

        var cliPid = h.ReadPid("cli.pid");
        var grandchild = h.ReadPid("grandchild.pid");
        UnixGroupProcess.GroupOf(cliPid).Should().Be(cliPid, "CLI — лидер своей группы процессов (setsid)");
        UnixGroupProcess.GroupOf(grandchild).Should().Be(cliPid, "внук в группе CLI");
        var sessions = Path.Combine(h.Profile, "sessions");
        File.Exists(Path.Combine(sessions, $"{cliPid}.json")).Should().BeTrue();

        // Чужой ход того же устройства kill не трогает
        await h.Server.SendControlAsync(new DeviceExecControl(DeviceExecControlOps.Kill, "other-turn"));
        await h.Server.SendControlAsync(new DeviceExecControl(DeviceExecControlOps.Kill, "turn-kill"));
        var frames = await h.Server.ReadUntilExitAsync(Wait);
        await h.Run!.WaitAsync(Wait);

        TurnHarness.ExitOf(frames).Signal.Should().Be("SIGKILL");
        UnixGroupProcess.IsAlive(cliPid).Should().BeFalse();
        await WaitDeadAsync(grandchild);
        Directory.EnumerateFiles(sessions).Should().BeEmpty("sessions/<pid>.json и .key убиваемого CLI убраны агентом");
    }

    [SkippableFact]
    public async Task Потеря_связи_насовсем_убивает_ход()
    {
        await using var h = NewHarness();
        await h.StartAsync(h.Spawn(), turnId: "turn-lost");
        await h.WaitStdoutAsync("\"init\"", new StringBuilder(), Wait);
        var cliPid = h.ReadPid("cli.pid");
        var grandchild = h.ReadPid("grandchild.pid");

        h.Server.RefuseReconnect = true;
        h.Server.Break();
        await h.Run!.WaitAsync(Wait);

        h.Link!.FailureReason.Should().Contain("404");
        UnixGroupProcess.IsAlive(cliPid).Should().BeFalse();
        await WaitDeadAsync(grandchild);
        h.Executor.LiveCount.Should().Be(0);
    }

    [SkippableFact]
    public async Task Stdout_досылается_после_обрыва_посреди_стрима_без_потерь_и_дублей()
    {
        await using var h = NewHarness();
        const int lines = 20000;
        h.Server.BreakAfterFrames = 3;
        await h.StartAsync(h.Spawn(["--burst", lines.ToString()]), turnId: "turn-burst");

        var frames = await h.Server.ReadUntilExitAsync(TimeSpan.FromSeconds(60));
        await h.Run!.WaitAsync(Wait);

        h.Server.Connections.Should().BeGreaterThan(1, "связь рвалась посреди стрима и поднялась заново");
        frames.Select(f => f.Sequence).Should().BeInAscendingOrder().And.OnlyHaveUniqueItems();
        frames.Select(f => (long)f.Sequence).Should().Equal(Enumerable.Range(1, frames.Count).Select(i => (long)i),
            "номера без пропусков: ни один кадр не потерян");

        var stdout = TurnHarness.Text(frames, DeviceExecFrameChannel.Stdout).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        stdout.Should().Equal(new[] { """{"type":"system","subtype":"init"}""" }
            .Concat(Enumerable.Range(0, lines).Select(i => $"line-{i}")));
        TurnHarness.ExitOf(frames).Code.Should().Be(0);
    }

    [SkippableFact]
    public async Task Повтор_кадров_stdin_после_реконнекта_не_исполняется_дважды()
    {
        await using var h = NewHarness();
        await h.StartAsync(h.Spawn(), turnId: "turn-dup");
        var stdout = new StringBuilder();
        await h.WaitStdoutAsync("\"init\"", stdout, Wait);

        // Подтверждения агента «теряются» — после реконнекта сервер пошлёт те же кадры заново
        h.Server.IgnoreAcks = true;
        await h.SendStdinAsync("a\n");
        await h.SendStdinAsync("b\n");
        await h.WaitStdoutAsync("echo:b", stdout, Wait);
        h.Server.Break();
        h.Server.IgnoreAcks = false;

        await h.SendStdinAsync("c\n");
        await h.WaitStdoutAsync("echo:c", stdout, Wait);
        await h.SendStdinAsync("exit\n");
        var frames = await h.Server.ReadUntilExitAsync(Wait);
        stdout.Append(TurnHarness.Text(frames, DeviceExecFrameChannel.Stdout));

        h.Server.Connections.Should().BeGreaterThan(1);
        var echoes = stdout.ToString().Split('\n').Where(l => l.StartsWith("echo:")).ToList();
        echoes.Should().Equal("echo:a", "echo:b", "echo:c");
    }

    [SkippableFact]
    public async Task Без_аренды_ход_отказывает_сразу_с_причиной_харнес_не_готов()
    {
        var launched = 0;
        Skip.If(OperatingSystem.IsWindows());
        await using var h = new TurnHarness("/nonexistent/claude", "Харнес не готов: на устройстве нет CLI 2.1.0",
            launcher: l => { launched++; throw new InvalidOperationException("не должен запускаться"); });

        await h.StartAsync(h.Spawn());
        var frames = await h.Server.ReadUntilExitAsync(Wait);
        await h.Run!.WaitAsync(Wait);

        var exit = TurnHarness.ExitOf(frames);
        exit.Code.Should().Be(TurnExecutor.RefusedExitCode);
        exit.Error.Should().StartWith("Харнес не готов");
        TurnHarness.Text(frames, DeviceExecFrameChannel.Stderr).Should().Contain("Харнес не готов: на устройстве нет CLI 2.1.0");
        launched.Should().Be(0);
        Directory.Exists(Path.Combine(h.TurnsRoot, "turn1")).Should().BeFalse();
    }

    [SkippableTheory]
    [InlineData("claude.cmd")]
    [InlineData("/usr/bin/claude")]
    [InlineData("bash")]
    public async Task Запускается_только_claude_из_аренды(string fileName)
    {
        await using var h = NewHarness();
        await h.StartAsync(h.Spawn(fileName: fileName));
        var frames = await h.Server.ReadUntilExitAsync(Wait);
        await h.Run!.WaitAsync(Wait);

        TurnHarness.ExitOf(frames).Code.Should().Be(TurnExecutor.RefusedExitCode);
        h.Cli.Acquired.Should().Be(0);
    }

    [SkippableFact]
    public async Task Токен_хода_не_попадает_ни_в_лог_ни_в_файлы_устройства()
    {
        await using var h = NewHarness();
        var files = new[] { new DeviceExecFile("f1", "mcp.json", """{"mcpServers":{}}""") };
        await h.StartAsync(h.Spawn(["--mcp-config", DeviceExecPlaceholders.File("f1")], files));
        await h.WaitStdoutAsync("\"init\"", new StringBuilder(), Wait);

        var onDisk = Directory.EnumerateFiles(h.Root, "*", SearchOption.AllDirectories)
            .Select(f => { try { return File.ReadAllText(f); } catch (IOException) { return ""; } });
        onDisk.Should().NotContain(text => text.Contains(TurnHarness.TurnToken));

        await h.Server.SendControlAsync(new DeviceExecControl(DeviceExecControlOps.Kill, "turn1"));
        await h.Server.ReadUntilExitAsync(Wait);
        await h.Run!.WaitAsync(Wait);

        h.Logs.Should().NotBeEmpty();
        h.Logs.Should().NotContain(l => l.Contains(TurnHarness.TurnToken));
    }

    private static async Task WaitDeadAsync(int pid)
    {
        for (var i = 0; i < 100 && UnixGroupProcess.IsAlive(pid); i++) await Task.Delay(50);
        UnixGroupProcess.IsAlive(pid).Should().BeFalse($"процесс {pid} обязан умереть вместе с ходом");
    }
}
