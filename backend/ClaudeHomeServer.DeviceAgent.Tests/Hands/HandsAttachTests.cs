using System.Text.Json.Nodes;
using ClaudeHomeServer.DeviceAgent.Exec;
using ClaudeHomeServer.DeviceAgent.Hands;
using ClaudeHomeServer.Protocol;

namespace ClaudeHomeServer.DeviceAgent.Tests.Hands;

/// <summary>
/// Подключение рук к ходу (ADR-016 §7): каждый отказ — честный <see cref="ExecRefusedException"/>
/// с текстом, а не ход без рук; при успехе узел маркера заменён на свой мост.
/// </summary>
public class HandsAttachTests : IDisposable
{
    private readonly HandsFixture _fx = new();

    public void Dispose() => _fx.Dispose();

    private static DeviceExecSpawn Spawn(IReadOnlyList<string>? args = null, params DeviceExecFile[] files) =>
        new("claude", args ?? HandsFixture.HandsArgs, "/work", new Dictionary<string, string>(),
            files.Length == 0 ? [HandsFixture.McpFile(HandsFixture.McpConfig())] : files, RedirectStdin: true);

    private static string RefusalOf(Action act) =>
        act.Should().Throw<ExecRefusedException>().Which.Message;

    [Fact]
    public void Без_маркера_рук_ничего_не_проверяется_и_не_подключается()
    {
        var spawn = Spawn(files: HandsFixture.McpFile(HandsFixture.McpConfig(hands: false)));

        HandsAttach.Prepare(spawn, "t1", runtime: null).Should().BeNull();
    }

    [Fact]
    public void Отказ_агент_без_рук() =>
        RefusalOf(() => HandsAttach.Prepare(Spawn(), "t1", runtime: null)).Should().Be(HandsAttach.UnsupportedText);

    [Fact]
    public void Отказ_моста_нет_в_каталоге_агента() =>
        RefusalOf(() => HandsAttach.Prepare(Spawn(), "t1", _fx.Runtime())).Should().Be(HandsComponent.MissingText);

    [Theory]
    [InlineData("--permission-mode bypassPermissions")]
    [InlineData("--permission-mode acceptEdits --dangerously-skip-permissions")]
    [InlineData("--print")]
    public void Отказ_режим_прав_хода(string args)
    {
        _fx.WithBridge();

        RefusalOf(() => HandsAttach.Prepare(Spawn(args.Split(' ')), "t1", _fx.Runtime()))
            .Should().StartWith("Руки не подключены:");
    }

    [Fact]
    public void Отказ_маркер_не_в_узле_hands()
    {
        _fx.WithBridge();
        var config = new JsonObject
        {
            ["mcpServers"] = new JsonObject { ["other"] = new JsonObject { ["type"] = DeviceExecPlaceholders.Hands } },
        }.ToJsonString();

        RefusalOf(() => HandsAttach.Prepare(Spawn(files: HandsFixture.McpFile(config)), "t1", _fx.Runtime()))
            .Should().Be(HandsAttach.MisplacedMarkerText);
    }

    [Fact]
    public void Отказ_маркер_в_двух_файлах_или_не_в_JSON()
    {
        _fx.WithBridge();
        var twice = Spawn(files: [HandsFixture.McpFile(HandsFixture.McpConfig()), new("f2", "b.json", HandsFixture.McpConfig())]);
        var prompt = Spawn(files: [HandsFixture.McpFile(HandsFixture.McpConfig()), new("f2", "prompt.md", "текст " + DeviceExecPlaceholders.Hands)]);

        RefusalOf(() => HandsAttach.Prepare(twice, "t1", _fx.Runtime())).Should().Be(HandsAttach.MisplacedMarkerText);
        RefusalOf(() => HandsAttach.Prepare(prompt, "t1", _fx.Runtime())).Should().Be(HandsAttach.MisplacedMarkerText);
    }

    [Fact]
    public void Отказ_руки_заняты_другим_ходом_и_свободны_после_его_конца()
    {
        _fx.WithBridge();
        var machine = new InProcessHandsMachineLock();

        var first = HandsAttach.Prepare(Spawn(), "t1", _fx.Runtime(machine))!;
        RefusalOf(() => HandsAttach.Prepare(Spawn(), "t2", _fx.Runtime(machine))).Should().Be(HandsMachineLock.BusyText);

        first.Dispose();
        using var again = HandsAttach.Prepare(Spawn(), "t2", _fx.Runtime(machine));
        again.Should().NotBeNull("конец хода отдаёт руки следующему");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Маркер_заменяется_узлом_своего_моста_в_Job_хода(bool vision)
    {
        _fx.WithBridge();
        var spawn = Spawn(files: HandsFixture.McpFile(HandsFixture.McpConfig(vision)));

        using var lease = HandsAttach.Prepare(spawn, "turn-7", _fx.Runtime())!;

        lease.JobName.Should().StartWith(HandsBridgeArgs.TurnJobPrefix + "turn-7.");
        var content = lease.Files.Single().Content;
        content.Should().NotContain(DeviceExecPlaceholders.Hands);
        var node = JsonNode.Parse(content)!["mcpServers"]![DeviceExecPlaceholders.HandsServerName]!;
        ((string?)node["type"]).Should().Be("stdio");
        ((string?)node["command"]).Should().Be(_fx.Component.BridgePath, "команду сервер не шлёт — её ставит агент");
        var args = node["args"]!.AsArray().Select(a => (string?)a).ToList();
        // Имя Job хода мосту не едет: граница «только свои окна» снята (ADR-016 §7)
        args.Should().NotContain("--turn-job").And.NotContain(lease.JobName);
        // Событие первого действия — своё у каждого хода, его создаёт агент
        var activity = lease.Activity!.Name;
        activity.Should().StartWith(HandsBridgeArgs.ActivityEventPrefix + "turn-7.");
        if (vision) args.Should().Equal(HandsBridgeArgs.ActivityEvent, activity);
        else args.Should().Equal(HandsBridgeArgs.ExcludeTools, "screenshot_control,browser_screenshot", HandsBridgeArgs.ActivityEvent, activity);
        JsonNode.Parse(content)!["mcpServers"]!["tasks"].Should().NotBeNull("прочие узлы не тронуты");
    }
}

/// <summary>
/// Профиль Chrome браузерной руки (ADR-016 §7.1): путь считает агент от проверенного корня
/// проекта, от хода он не зависит — сигнатура запуска CLI не меняется от хода к ходу.
/// </summary>
public class HandsBrowserProfileTests : IDisposable
{
    private readonly HandsFixture _fx = new();

    public void Dispose() => _fx.Dispose();

    private string? ProfileOf(string turnId, string? projectRoot, bool browserProfiles = true)
    {
        var spawn = new DeviceExecSpawn("claude", HandsFixture.HandsArgs, "/work", new Dictionary<string, string>(),
            [HandsFixture.McpFile(HandsFixture.McpConfig(vision: true))], RedirectStdin: true);
        using var lease = HandsAttach.Prepare(spawn, turnId, _fx.Runtime(browserProfiles: browserProfiles), projectRoot)!;
        var args = JsonNode.Parse(lease.Files.Single().Content)!["mcpServers"]![DeviceExecPlaceholders.HandsServerName]!["args"]!
            .AsArray().Select(a => (string)a!).ToList();
        var at = args.IndexOf(HandsBridgeArgs.BrowserProfile);
        return at < 0 ? null : args[at + 1];
    }

    [Fact]
    public void Два_хода_одного_проекта_получают_один_профиль_а_два_проекта_разные()
    {
        _fx.WithBridge();

        var first = ProfileOf("t1", "/work/app");
        var second = ProfileOf("t2", "/work/app");
        var other = ProfileOf("t3", "/work/other");

        first.Should().NotBeNull().And.Be(second);
        other.Should().NotBe(first);
        Path.GetDirectoryName(first).Should().Be(_fx.BrowserProfilesRoot);
        Path.GetFileName(first).Should().MatchRegex("^[0-9a-f]{16}$");
    }

    [Fact]
    public void Без_корня_профилей_или_проверенного_корня_аргумента_нет()
    {
        _fx.WithBridge();

        ProfileOf("t1", "/work/app", browserProfiles: false).Should().BeNull("старый рантайм без профилей");
        ProfileOf("t1", projectRoot: null).Should().BeNull("без сверенного корня путь не считается");
    }

    [Fact]
    public void На_Windows_регистр_и_хвостовой_слеш_корня_не_меняют_ключ()
    {
        HandsBrowserProfile.Key(@"C:\Work\App", ignoreCase: true)
            .Should().Be(HandsBrowserProfile.Key(@"c:\work\app\", ignoreCase: true));
        HandsBrowserProfile.Key("/work/App", ignoreCase: false)
            .Should().NotBe(HandsBrowserProfile.Key("/work/app", ignoreCase: false), "на Linux регистр значим");
    }

    [Fact]
    public void Профили_лежат_под_данными_агента_и_в_запретном_списке_выдачи_папок()
    {
        var paths = new ClaudeHomeServer.DeviceAgent.Hosting.AgentPaths(
            Path.Combine(_fx.AgentDirectory, "config"), Path.Combine(_fx.AgentDirectory, "data"));
        var profile = HandsBrowserProfile.PathFor(paths.BrowserProfilesRoot, "/work/app");

        var context = ClaudeHomeServer.DeviceAgent.Hosting.ProjectFolderBinder.ContextForCurrentMachine(paths);

        ClaudeHomeServer.DeviceAgent.Composition.AgentForbiddenPaths.RefusalOf(profile, context)
            .Should().NotBeNull("профиль браузера не выдаётся проектом и не читается ретранслятором");
    }
}

/// <summary>Мост рук едет в составе агента: ищется в каталоге его версии, отдельной установки нет.</summary>
public class HandsComponentTests : IDisposable
{
    private readonly HandsFixture _fx = new();

    public void Dispose() => _fx.Dispose();

    [Fact]
    public void Мост_найден_в_каталоге_версии()
    {
        _fx.WithBridge();

        _fx.Component.BridgePath.Should().Be(Path.Combine(_fx.AgentDirectory, HandsFiles.BridgeExe));
        _fx.Component.IsReady.Should().BeTrue();
        _fx.Component.Check().Should().Match<HandsComponentCheck>(c => c.Ready && c.Problem == null);
    }

    [Fact]
    public void Нет_моста_в_каталоге_версии_нет_рук() =>
        _fx.Component.Check().Should().Match<HandsComponentCheck>(c => !c.Ready && c.Problem == HandsComponent.MissingText);

    [Fact]
    public void Агент_ищет_мост_рядом_со_своим_исполняемым_файлом() =>
        HandsComponent.ForThisAgent().BridgePath.Should().Be(Path.Combine(AppContext.BaseDirectory, HandsFiles.BridgeExe));
}
