using ClaudeHomeServer.Services.Llm;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace ClaudeHomeServer.Tests.Services.LocalStats;

/// <summary>
/// Отчётный слой поверх узлового сэмплера (Ш3a): Build(узел) с плитками по сокетам и плиткой «Плата»,
/// сводка фермы Farm(), Brief с узлами, роли карт из реестра, заполненность диска.
/// Датчики и время — фейковые: IHardwareSampler на три узла, реестр — in-memory конфиг.
/// </summary>
public class HardwareFarmReportTests
{
    private static readonly DateTimeOffset At = new(2026, 11, 16, 12, 0, 0, TimeSpan.Zero);

    // ─── Фейковый сэмплер и реестр ───────────────────────────────────────────────

    private sealed class FakeView(string key, HardwareSample? cur, string status, long? since) : INodeHardwareView
    {
        public string Key { get; } = key;
        public HardwareSample? Current { get; } = cur;
        public string Status { get; } = status;
        public long? Since { get; } = since;

        public IReadOnlyList<(long t, int? value)> Aggregate(Func<HardwareSample, int?> selector, TimeSpan window, TimeSpan step) =>
            cur is null ? [] : [(0, selector(cur))];
    }

    private sealed class FakeSampler(params FakeView[] views) : IHardwareSampler
    {
        public IReadOnlyList<string> NodeKeys => [.. views.Select(v => v.Key)];
        public INodeHardwareView? ForNode(string key) => Array.Find(views, v => v.Key == key);
    }

    private sealed record NodeCfg(string Key, string Title, string[] Engines, bool Media);

    private static IConfiguration Registry(params NodeCfg[] defs)
    {
        var data = new Dictionary<string, string?>();
        for (var i = 0; i < defs.Length; i++)
        {
            var p = $"{HardwareNodesOptions.Section}:{i}:";
            data[p + "Key"] = defs[i].Key;
            data[p + "Title"] = defs[i].Title;
            // URL агента (не local) — чтобы второй local не отбрасывался проверкой реестра.
            data[p + "Source"] = $"http://10.0.0.{10 + i}:9105";
            for (var e = 0; e < defs[i].Engines.Length; e++) data[$"{p}Engines:{e}"] = defs[i].Engines[e];
            data[p + "Media"] = defs[i].Media ? "true" : "false";
        }
        return new ConfigurationBuilder().AddInMemoryCollection(data).Build();
    }

    private static HardwareSample Sample(
        int? board = null, bool probe = true, int? periph = null, HwmonCpu[]? cpus = null, int?[]? vrms = null,
        int? boardVrm = null, int? chipset = null, HardwareGpuSample[]? gpus = null, HwmonDisk[]? disks = null,
        long? uptime = null, int? load = null, long? ramUsed = null, long? ramTotal = null) =>
        new()
        {
            At = At, BoardTempC = board, BoardProbe = probe, PeripheralTempC = periph,
            Cpus = cpus, Vrms = vrms, BoardVrm = boardVrm, Chipset = chipset, Gpus = gpus, Disks = disks,
            UptimeS = uptime, CpuLoadPerc = load, RamUsedGb = ramUsed, RamTotalGb = ramTotal,
        };

    private static HwmonCpu Cpu(int tctl) => new() { Tctl = tctl, Ccd = [tctl - 5] };
    private static HardwareGpuSample Gpu(int index, int? mem) => new() { Index = index, Uuid = $"GPU-{index}", Name = "RTX 3090", CoreTempC = 60, MemTempC = mem };

    // Три узла конфига: worker (движок main), ai (движок qwen, молчит), video (медиа + hyperqwen).
    private static (HardwareReportService Svc, FakeView Ai) FarmService(
        HardwareSample worker, HardwareSample? ai = null, HardwareSample? video = null,
        string aiStatus = "down", long? aiSince = 1_763_283_600_000)
    {
        var aiView = new FakeView("ai", ai, aiStatus, aiSince);
        var sampler = new FakeSampler(
            new FakeView("worker", worker, "ok", null), aiView,
            new FakeView("video", video, "ok", null));
        var cfg = Registry(
            new("worker", "worker", ["main"], false),
            new("ai", "СТЕНД ai", ["qwen"], false),
            new("video", "", ["hyperqwen"], true));
        return (new HardwareReportService(sampler, cfg), aiView);
    }

    // ─── Сводка фермы ────────────────────────────────────────────────────────────

    [Fact]
    public void Farm_NodesInConfigOrder_DownLevelOk_HotNodePaintsFarm()
    {
        // Мутация «у down-узла уровень брать из последнего снимка» → красный (у ai последний снимок горячий).
        var (svc, _) = FarmService(
            worker: Sample(cpus: [Cpu(60)], vrms: [50], board: 70, uptime: 12345, load: 7, ramUsed: 20, ramTotal: 64),
            ai: Sample(gpus: [Gpu(0, 104)]),
            video: Sample(gpus: [Gpu(0, 106)], probe: false));

        var farm = svc.Farm();

        // Порядок — как в конфиге; пустой Title показывается Key.
        farm.FarmNodes.Select(n => n.Key).Should().Equal("worker", "ai", "video");
        farm.FarmNodes.Select(n => n.Title).Should().Equal("worker", "СТЕНД ai", "video");

        var ai = farm.FarmNodes.Single(n => n.Key == "ai");
        ai.Status.Should().Be("down");
        ai.Since.Should().NotBeNull("момент начала нештатности");
        ai.Level.Should().Be("ok", "красным красится только измеренное: последний снимок ai не красится");
        ai.Engines.Should().Equal("qwen");

        var video = farm.FarmNodes.Single(n => n.Key == "video");
        video.Level.Should().Be("danger", "память карты 106 ≥ 105");
        // Engines непуст → text, Media → media: обе.
        video.Roles.Should().Equal("text", "media");
        video.Engines.Should().Equal("hyperqwen");
        video.Media.Should().BeTrue();
        video.Hottest.Should().NotBeNull();
        video.Hottest!.Label.Should().Be("Карта 1");
        video.Hottest.TempC.Should().Be(106);
        video.Gpus.Should().ContainSingle(g => g.MemTempC == 106 && g.Level == "danger");

        var worker = farm.FarmNodes.Single(n => n.Key == "worker");
        worker.Level.Should().Be("ok");
        worker.Roles.Should().Equal("text");
        worker.UptimeS.Should().Be(12345);
        worker.CpuLoadPerc.Should().Be(7);
        worker.RamUsedPerc.Should().Be(31, "20 из 64 ГБ");
        worker.Hottest!.Label.Should().Be("Плата", "70 °C — самый горячий измеренный показатель узла");

        farm.Level.Should().Be("danger", "ферма — худший из узлов");
    }

    [Fact]
    public void Brief_HasNodesAndWorstLevel()
    {
        var (svc, _) = FarmService(
            worker: Sample(cpus: [Cpu(60)], gpus: [Gpu(0, 70)], board: 70),
            video: Sample(gpus: [Gpu(0, 106)], probe: false));

        var brief = svc.Brief();

        brief.Level.Should().Be("danger", "уровень строки-ссылки — худший по узлам");
        brief.Nodes.Select(n => n.Key).Should().Equal("worker", "ai", "video");
        brief.Nodes.Select(n => n.Level).Should().Equal("ok", "ok", "danger");
        // Карты сводки — локального узла: их uuid связываются с карточками движков этой вкладки.
        brief.Gpus.Should().ContainSingle(g => g.Uuid == "GPU-0", "сводка карт — worker");
    }

    [Fact]
    public void Roles_FromNode_UnknownNode_Null()
    {
        var sampler = new FakeSampler(
            new FakeView("worker", Sample(gpus: [Gpu(0, 60)]), "ok", null),
            new FakeView("media", Sample(gpus: [Gpu(0, 60)], probe: false), "ok", null));
        var svc = new HardwareReportService(sampler, Registry(
            new("worker", "worker", ["main"], false),
            new("media", "media", [], true)));

        svc.Build("worker", TimeSpan.FromMinutes(5))!.Gpus.Single().Roles.Should().Equal("text");
        svc.Build("media", TimeSpan.FromMinutes(5))!.Gpus.Single().Roles.Should().Equal("media");
        svc.Build("nope", TimeSpan.FromMinutes(5)).Should().BeNull("узла в реестре нет — не пустой отчёт");
    }

    // ─── Сокеты ──────────────────────────────────────────────────────────────────

    [Fact]
    public void TwoSockets_TilesPerSocket_LevelOfHottestSocket()
    {
        // Мутация «уровень узла по Cpus[0]» → красный.
        var sample = Sample(
            cpus: [Cpu(60), Cpu(92)], vrms: [70, null], boardVrm: 55, board: 40, chipset: 41,
            disks: [new HwmonDisk { Controller = "nvme0", Role = "system", TempC = 40 }]);
        var report = Service(sample).Build("worker", TimeSpan.FromMinutes(5))!;

        // Порядок плиток: сокеты, VRM сокетов, VRM платы, плата, чипсет, дальше вода/диски/RAM.
        var tiles = report.Nodes;
        tiles.Select(t => t.Key).Should().Equal("cpu-0", "cpu-1", "vrm-0", "vrm-board", "board", "chipset", "disk-nvme0");

        var cpu0 = tiles.Single(t => t.Key == "cpu-0");
        cpu0.Kind.Should().Be("cpu");
        cpu0.Name.Should().Be("Процессор 1");
        cpu0.TempC.Should().Be(60);
        cpu0.Level.Should().Be("ok");
        cpu0.Detail.CcdC.Should().Equal([55], "кристаллы своего сокета");

        var cpu1 = tiles.Single(t => t.Key == "cpu-1");
        cpu1.Name.Should().Be("Процессор 2");
        cpu1.TempC.Should().Be(92);
        cpu1.Level.Should().Be("warn", "92 ≥ 85");

        tiles.Should().NotContain(t => t.Key == "cpu", "при двух сокетах ключа без индекса нет");
        tiles.Single(t => t.Key == "vrm-0").Name.Should().Be("Питание процессора 1");
        tiles.Single(t => t.Key == "vrm-0").Kind.Should().Be("vrm");
        tiles.Should().NotContain(t => t.Key == "vrm-1", "VRM без значения не становится плиткой");
        var boardVrm = tiles.Single(t => t.Key == "vrm-board");
        boardVrm.Name.Should().Be("Питание (плата)");
        boardVrm.TempC.Should().Be(55);

        report.Level.Should().Be(cpu1.Level, "уровень узла — худший из сокетов");

        report.Series.Should().Contain(s => s.Key == "cpu-0" && s.Family == "cpu" && s.Label == "Процессор 1");
        report.Series.Should().Contain(s => s.Key == "cpu-1" && s.Family == "cpu" && s.Label == "Процессор 2");
        cpu0.Spark15m.Should().Equal([60]);
        cpu1.Spark15m.Should().Equal([92]);
        tiles.Single(t => t.Key == "vrm-board").Spark15m.Should().Equal([55]);
    }

    [Fact]
    public void OneSocket_KeysNamesAndOrder_AsBefore()
    {
        var sample = Sample(cpus: [Cpu(76)], vrms: [53], chipset: 78, board: 70);
        var report = Service(sample).Build("worker", TimeSpan.FromMinutes(5))!;

        report.Nodes.Select(t => t.Key).Should().Equal("cpu", "vrm", "board", "chipset");
        var cpu = report.Nodes.Single(t => t.Key == "cpu");
        cpu.Name.Should().Be("Процессор");
        cpu.Kind.Should().Be("cpu");
        var vrm = report.Nodes.Single(t => t.Key == "vrm");
        vrm.Name.Should().Be("Питание процессора");
        vrm.TempC.Should().Be(53);
        report.Nodes.Should().NotContain(t => t.Key == "cpu-0" || t.Key == "vrm-0" || t.Key == "vrm-board");
        report.Series.Select(s => s.Key).Should().Equal("cpu", "board", "chipset");
    }

    // ─── Заполненность диска ─────────────────────────────────────────────────────

    [Theory]
    [InlineData(84, "ok")] [InlineData(85, "warn")] [InlineData(95, "danger")]
    public void DiskFill_LevelByPercent(int usedGb, string expected)
    {
        var sample = Sample(disks: [new HwmonDisk { Controller = "nvme1", Role = "kv", UsedGb = usedGb, TotalGb = 100 }]);
        var report = Service(sample).Build("worker", TimeSpan.FromMinutes(5))!;

        var kv = report.Nodes.Single(t => t.Key == "disk-nvme1");
        kv.Detail.UsedPerc.Should().Be(usedGb);
        kv.Level.Should().Be(expected, "пороги заполненности 85/95");
        report.Level.Should().Be(expected, "заполненность диска входит в уровень узла");
    }

    // ─── Плата ───────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(79, "ok")] [InlineData(80, "warn")] [InlineData(85, "danger")]
    public void Board_Thresholds_80_85(int tempC, string expected)
    {
        var report = Service(Sample(cpus: [Cpu(40)], board: tempC)).Build("worker", TimeSpan.FromMinutes(5))!;

        var board = report.Nodes.Single(t => t.Key == "board");
        board.Kind.Should().Be("board");
        board.Name.Should().Be("Плата");
        board.TempC.Should().Be(tempC);
        board.Sensor.Should().Be("ok");
        board.WarnC.Should().Be(80);
        board.DangerC.Should().Be(85);
        board.Level.Should().Be(expected);
        report.Level.Should().Be(expected, "при холодных сокетах уровень узла — уровень платы");
    }

    [Theory]
    [InlineData(true, "nodata")] [InlineData(false, "none")]
    public void Board_NoValue_NotAnxiety(bool probe, string sensor)
    {
        // Мутация «вернуть b.BoardTempC is null ? "ok"» → красный (плитка получила бы ok вместо none).
        var report = Service(Sample(cpus: [Cpu(40)], board: null, probe: probe)).Build("worker", TimeSpan.FromMinutes(5))!;

        var board = report.Nodes.Single(t => t.Key == "board");
        board.Sensor.Should().Be(sensor, probe ? "источник платы есть, значения нет" : "датчика нет");
        board.TempC.Should().BeNull();
        board.Level.Should().Be("none", "без значения — уровень none, как у остальных плиток");
        report.Level.Should().Be("ok", "плата без значения тревогой не считается (none в худший не входит)");
        report.Series.Should().NotContain(s => s.Key == "board", "ряда нет, если значения нет в текущем снимке");
    }

    [Fact]
    public void Board_Peripheral_InDetail_OnlyWithValue()
    {
        var json = System.Text.Json.JsonSerializer.Serialize(
            Service(Sample(cpus: [Cpu(40)], board: 70, periph: 37)).Build("worker", TimeSpan.FromMinutes(5))!,
            new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        var detail = doc.RootElement.GetProperty("nodes").EnumerateArray()
            .First(n => n.GetProperty("key").GetString() == "board").GetProperty("detail");
        detail.GetProperty("peripheralC").GetInt32().Should().Be(37);

        var none = System.Text.Json.JsonSerializer.Serialize(
            Service(Sample(cpus: [Cpu(40)], board: 70)).Build("worker", TimeSpan.FromMinutes(5))!,
            new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
        using var docNone = System.Text.Json.JsonDocument.Parse(none);
        var detailNone = docNone.RootElement.GetProperty("nodes").EnumerateArray()
            .First(n => n.GetProperty("key").GetString() == "board").GetProperty("detail");
        detailNone.TryGetProperty("peripheralC", out _).Should().BeFalse("нет значения — нет и ключа");
    }

    [Fact]
    public void Board_IsHottest_InFarm_AndHasItsRow()
    {
        var (svc, _) = FarmService(worker: Sample(cpus: [Cpu(40)], vrms: [30], board: 85, periph: 60));
        var report = svc.Build("worker", TimeSpan.FromMinutes(5))!;

        report.Series.Should().Contain(s => s.Key == "board" && s.Family == "board" && s.Label == "Плата");
        report.Nodes.Single(t => t.Key == "board").Spark15m.Should().Equal([85]);

        var node = svc.Farm().FarmNodes.Single(n => n.Key == "worker");
        node.Level.Should().Be("danger", "85 ≥ порог опасности платы");
        node.Hottest!.Label.Should().Be("Плата");
        node.Hottest.TempC.Should().Be(85);
        node.Hottest.Level.Should().Be("danger");
    }

    // ─── Узел отчёта ─────────────────────────────────────────────────────────────

    [Fact]
    public void Build_NodeInfoUptimeLoad_AndNoRegistryIsWorker()
    {
        var (svc, _) = FarmService(worker: Sample(cpus: [Cpu(40)], uptime: 987, load: 3));
        var report = svc.Build("ai", TimeSpan.FromMinutes(5))!;
        report.Node!.Key.Should().Be("ai");
        report.Node.Title.Should().Be("СТЕНД ai");
        report.UptimeS.Should().BeNull("у ai нет снимка");
        report.Status.Should().Be("stale", "down для ручки — тот же «не отвечает»");

        // Реестра нет → ровно один worker (побайтно прежняя ручка).
        var plain = new HardwareReportService(
            new FakeSampler(new FakeView("worker", Sample(cpus: [Cpu(40)], uptime: 12), "ok", null)),
            new ConfigurationBuilder().Build()).Build(TimeSpan.FromMinutes(5))!;
        plain.Node!.Key.Should().Be("worker");
        plain.Node.Title.Should().Be("worker");
        plain.UptimeS.Should().Be(12);
        plain.Gpus.Should().BeEmpty();
    }

    private static HardwareReportService Service(HardwareSample sample) =>
        new(new FakeSampler(new FakeView("worker", sample, "ok", null)), new ConfigurationBuilder().Build());
}
