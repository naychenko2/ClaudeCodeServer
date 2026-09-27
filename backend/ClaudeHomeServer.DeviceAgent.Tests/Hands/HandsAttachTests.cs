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
    public void Отказ_компонент_не_установлен() =>
        RefusalOf(() => HandsAttach.Prepare(Spawn(), "t1", _fx.Runtime())).Should().Be(HandsComponent.NotInstalledText);

    [Fact]
    public void Отказ_хеш_моста_не_сошёлся()
    {
        _fx.Installed();
        File.AppendAllText(_fx.Component.BridgePath, "подмена");

        RefusalOf(() => HandsAttach.Prepare(Spawn(), "t1", _fx.Runtime())).Should().Be(HandsComponent.NotVerifiedText);
    }

    [Theory]
    [InlineData("--permission-mode bypassPermissions")]
    [InlineData("--permission-mode acceptEdits --dangerously-skip-permissions")]
    [InlineData("--print")]
    public void Отказ_режим_прав_хода(string args)
    {
        _fx.Installed();

        RefusalOf(() => HandsAttach.Prepare(Spawn(args.Split(' ')), "t1", _fx.Runtime()))
            .Should().StartWith("Руки не подключены:");
    }

    [Fact]
    public void Отказ_маркер_не_в_узле_hands()
    {
        _fx.Installed();
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
        _fx.Installed();
        var twice = Spawn(files: [HandsFixture.McpFile(HandsFixture.McpConfig()), new("f2", "b.json", HandsFixture.McpConfig())]);
        var prompt = Spawn(files: [HandsFixture.McpFile(HandsFixture.McpConfig()), new("f2", "prompt.md", "текст " + DeviceExecPlaceholders.Hands)]);

        RefusalOf(() => HandsAttach.Prepare(twice, "t1", _fx.Runtime())).Should().Be(HandsAttach.MisplacedMarkerText);
        RefusalOf(() => HandsAttach.Prepare(prompt, "t1", _fx.Runtime())).Should().Be(HandsAttach.MisplacedMarkerText);
    }

    [Fact]
    public void Отказ_руки_заняты_другим_ходом_и_свободны_после_его_конца()
    {
        _fx.Installed();
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
        _fx.Installed();
        var spawn = Spawn(files: HandsFixture.McpFile(HandsFixture.McpConfig(vision)));

        using var lease = HandsAttach.Prepare(spawn, "turn-7", _fx.Runtime())!;

        lease.JobName.Should().StartWith(HandsBridgeArgs.TurnJobPrefix + "turn-7.");
        var content = lease.Files.Single().Content;
        content.Should().NotContain(DeviceExecPlaceholders.Hands);
        var node = JsonNode.Parse(content)!["mcpServers"]![DeviceExecPlaceholders.HandsServerName]!;
        ((string?)node["type"]).Should().Be("stdio");
        ((string?)node["command"]).Should().Be(_fx.Component.BridgePath, "команду сервер не шлёт — её ставит агент");
        var args = node["args"]!.AsArray().Select(a => (string?)a).ToList();
        args.Take(2).Should().Equal(HandsBridgeArgs.TurnJob, lease.JobName);
        if (vision) args.Should().HaveCount(2);
        else args.Skip(2).Should().Equal(HandsBridgeArgs.ExcludeTools, HandsAttach.ScreenshotTool);
        JsonNode.Parse(content)!["mcpServers"]!["tasks"].Should().NotBeNull("прочие узлы не тронуты");
    }
}

/// <summary>Компонент рук: установка по манифесту с сверкой SHA-256, сверка перед ходом, удаление.</summary>
public class HandsComponentTests : IDisposable
{
    private readonly HandsFixture _fx = new();

    public void Dispose() => _fx.Dispose();

    [Fact]
    public void Установка_сверяет_хеш_архива_и_пишет_запись()
    {
        var (path, offer) = _fx.Archive();

        var record = _fx.Component.Install(path, offer);

        record.ArchiveSha256.Should().Be(offer.Sha256);
        record.BridgeSha256.Should().Be(HandsComponent.FileSha256(_fx.Component.BridgePath));
        _fx.Component.Check().Ready.Should().BeTrue();
    }

    [Fact]
    public void Чужой_хеш_или_размер_архива_не_ставятся()
    {
        var (path, offer) = _fx.Archive();

        var wrongSha = () => _fx.Component.Install(path, offer with { Sha256 = new string('0', 64) });
        var wrongSize = () => _fx.Component.Install(path, offer with { Size = offer.Size + 1 });

        wrongSha.Should().Throw<HandsInstallException>().WithMessage("*SHA-256*");
        wrongSize.Should().Throw<HandsInstallException>().WithMessage("*Размер*");
        _fx.Component.Check().Ready.Should().BeFalse();
        Directory.Exists(_fx.Component.ComponentDirectory).Should().BeFalse();
    }

    [Fact]
    public void Архив_без_моста_не_ставится()
    {
        var (path, offer) = _fx.Archive(entry: "other.exe");

        var act = () => _fx.Component.Install(path, offer);

        act.Should().Throw<HandsInstallException>().WithMessage($"*{HandsFiles.BridgeExe}*");
        _fx.Component.Check().Ready.Should().BeFalse();
    }

    [Fact]
    public void Подменённый_мост_не_проходит_сверку()
    {
        _fx.Installed();

        File.WriteAllText(_fx.Component.BridgePath, "другой мост");

        _fx.Component.Check().Should().Match<HandsComponentCheck>(c => !c.Ready && c.Problem == HandsComponent.NotVerifiedText);
    }

    [Fact]
    public void Удаление_снимает_возможность_сразу()
    {
        _fx.Installed();

        _fx.Component.Remove().Should().BeTrue();

        _fx.Component.Check().Should().Match<HandsComponentCheck>(c => !c.Ready && c.Problem == HandsComponent.NotInstalledText);
        Directory.Exists(_fx.Component.ComponentDirectory).Should().BeFalse();
    }

    [Theory]
    [InlineData("1.2.3/win-x64/hands-1.2.3-win-x64.zip", true)]
    [InlineData("../win-x64/hands.zip", false)]
    [InlineData("1.2.3/../hands.zip", false)]
    [InlineData("1.2.3/win-x64/../../x.zip", false)]
    [InlineData("1.2.3\\win-x64\\hands.zip", false)]
    [InlineData("/etc/passwd", false)]
    [InlineData("1.2.3/win-x64/.hidden", false)]
    [InlineData("https://evil.example/x.zip", false)]
    public void Путь_архива_из_ответа_сервера_проверяется(string path, bool ok) =>
        HandsComponent.IsSafeArchivePath(path).Should().Be(ok);

    [Fact]
    public void Предложение_из_ответа_hello_только_полным_набором()
    {
        var ack = new DeviceHelloAck(1, 2, 3, 4, HandsArchivePath: "1.2.3/win-x64/hands.zip",
            HandsArchiveSha256: new string('A', 64), HandsArchiveSize: 10);

        HandsComponent.OfferFrom(ack, "1.2.3").Should().Be(new HandsOffer("1.2.3", "1.2.3/win-x64/hands.zip", new string('a', 64), 10));
        HandsComponent.OfferFrom(ack with { HandsArchiveSize = null }, "1.2.3").Should().BeNull();
        HandsComponent.OfferFrom(ack with { HandsArchivePath = "../x.zip" }, "1.2.3").Should().BeNull();
        HandsComponent.OfferFrom(ack with { HandsArchiveSha256 = "xyz" }, "1.2.3").Should().BeNull();
    }
}
