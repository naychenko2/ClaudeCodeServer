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
        if (vision) args.Should().BeEmpty();
        else args.Should().Equal(HandsBridgeArgs.ExcludeTools, HandsAttach.ScreenshotTool);
        JsonNode.Parse(content)!["mcpServers"]!["tasks"].Should().NotBeNull("прочие узлы не тронуты");
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
