using ClaudeHomeServer.Services.Llm;
using FluentAssertions;
using Microsoft.Extensions.Options;
using Xunit;

namespace ClaudeHomeServer.Tests.Services.LocalStats;

/// <summary>
/// Вкладка «Железо» (Б1): файл помощника ccs-gddr6, hwmon с подменой корня, уровни по таблице
/// порогов, сэмплер + кольцо + бакеты (максимум, null рвёт), отчёт. Все пути — во временных
/// каталогах (Path.GetTempPath), время — фейковые часы, без Task.Delay.
/// </summary>
public class HardwareTests : IDisposable
{
    private sealed class ManualTime : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 10, 18, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now += by;
    }

    private sealed class FakeOptions(LocalModelStatsOptions v) : IOptionsMonitor<LocalModelStatsOptions>
    {
        public LocalModelStatsOptions CurrentValue { get; } = v;
        public LocalModelStatsOptions Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<LocalModelStatsOptions, string?> listener) => null;
    }

    private sealed class FakeGpu(params GpuSample[] samples) : IGpuSampler
    {
        public Task<IReadOnlyList<GpuSample>> ReadAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<GpuSample>>(samples);
    }

    private readonly string _root = Path.Combine(Path.GetTempPath(), "hw_" + Guid.NewGuid().ToString("N"));

    public HardwareTests() => Directory.CreateDirectory(_root);
    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    private string Dir(params string[] parts)
    {
        var p = Path.Combine(new[] { _root }.Concat(parts).ToArray());
        Directory.CreateDirectory(p);
        return p;
    }

    private static void Put(string dir, string file, string text) => File.WriteAllText(Path.Combine(dir, file), text);

    // ─── Файл помощника ──────────────────────────────────────────────────────────

    private static string HelperJson(DateTimeOffset at, string gpus = "[{\"busId\":\"0000:0b:00.0\",\"deviceId\":\"0x2204\",\"memTempC\":68,\"hotspotTempC\":75}]") =>
        $"{{\"v\":1,\"at\":{at.ToUnixTimeMilliseconds()},\"gpus\":{gpus}}}";

    [Fact]
    public void Helper_Fresh_ParsesValues()
    {
        var t = new ManualTime();
        var r = new Gddr6HelperReader(t).Parse(HelperJson(t.GetUtcNow().AddSeconds(-3)));
        r.Should().NotBeNull();
        r!.Value.fresh.Should().BeTrue();
        r.Value.gpus.Should().ContainSingle().Which.MemTempC.Should().Be(68);
        r.Value.gpus[0].HotspotTempC.Should().Be(75);
    }

    [Fact]
    public void Helper_Stale_NotFresh()
    {
        var t = new ManualTime();
        var r = new Gddr6HelperReader(t).Parse(HelperJson(t.GetUtcNow().AddSeconds(-11)));
        r!.Value.fresh.Should().BeFalse("метка at старше 10 с — данных нет");
    }

    [Theory]
    [InlineData("не json")]
    [InlineData("{\"v\":2,\"at\":1,\"gpus\":[]}")]
    [InlineData("")]
    public void Helper_Broken_ReturnsNull(string text)
    {
        new Gddr6HelperReader(new ManualTime()).Parse(text).Should().BeNull();
    }

    [Fact]
    public void Helper_MissingFile_ReturnsNull()
    {
        new Gddr6HelperReader(new ManualTime()).Read(Path.Combine(_root, "нет.json")).Should().BeNull();
    }

    // ─── hwmon ───────────────────────────────────────────────────────────────────

    private (HwmonReader reader, string mountinfo) BuildFakeHwmon()
    {
        // Номера hwmonN нарочно «перемешаны»: имена определяются по name, не по номеру.
        var hw = Dir("hwmon");
        var nvmeKv = Dir("hwmon", "hwmon9");
        Put(nvmeKv, "name", "nvme\n");
        Put(nvmeKv, "temp1_input", "58850"); Put(nvmeKv, "temp1_label", "Composite");
        Put(nvmeKv, "temp3_input", "82850"); Put(nvmeKv, "temp3_label", "Sensor 2");
        Put(nvmeKv, "temp1_max", "83850"); Put(nvmeKv, "temp1_crit", "88850");
        File.CreateSymbolicLink(Path.Combine(nvmeKv, "device"), "../../nvme1");

        var nvmeSys = Dir("hwmon", "hwmon3");
        Put(nvmeSys, "name", "nvme\n");
        Put(nvmeSys, "temp1_input", "59850"); Put(nvmeSys, "temp1_label", "Composite");
        File.CreateSymbolicLink(Path.Combine(nvmeSys, "device"), "../../nvme0");

        var cpu = Dir("hwmon", "hwmon0");
        Put(cpu, "name", "k10temp\n");
        Put(cpu, "temp1_input", "76250"); Put(cpu, "temp3_input", "78000"); Put(cpu, "temp4_input", "49750");

        var board = Dir("hwmon", "hwmon6");
        Put(board, "name", "asusec\n");
        Put(board, "temp1_input", "78000"); Put(board, "temp1_label", "Chipset");
        Put(board, "temp5_input", "53000"); Put(board, "temp5_label", "VRM");
        Put(board, "fan2_input", "1159"); Put(board, "fan2_label", "Chipset");

        var nct = Dir("hwmon", "hwmon1");
        Put(nct, "name", "nct6798\n");
        Put(nct, "temp4_input", "127000"); Put(nct, "temp4_label", "AUXTIN1");
        Put(nct, "temp5_input", "96000"); Put(nct, "temp5_label", "AUXTIN2");
        Put(nct, "fan1_input", "0"); Put(nct, "fan2_input", "1159"); Put(nct, "fan7_input", "4821");

        var mountinfo = Path.Combine(_root, "mountinfo");
        File.WriteAllText(mountinfo,
            "36 1 8:1 / / rw - ext4 /dev/nvme0n1p2 rw\n" +
            "364 43 259:1 / /home/an/llm/kvdisk rw shared:197 - ext4 /dev/nvme1n1p1 rw\n");
        return (new HwmonReader(hw, Dir("block"), mountinfo), mountinfo);
    }

    [Fact]
    public void Hwmon_ReadsByName_RolesKvAndSystem_AuxtinDropped()
    {
        var (reader, _) = BuildFakeHwmon();
        var snap = reader.Read()!;

        snap.Cpu!.Tctl.Should().Be(76);
        snap.Cpu.Tccd1.Should().Be(78);
        snap.Board!.Vrm.Should().Be(53);
        snap.Board.Chipset.Should().Be(78);
        snap.Board.ChipsetFanRpm.Should().Be(1159);

        var kv = snap.Disks!.Single(d => d.Role == "kv");
        kv.Controller.Should().Be("nvme1");
        kv.TempC.Should().Be(82, "самый горячий датчик контроллера");
        kv.Temp2C.Should().Be(58);
        kv.CritC.Should().Be(84, "temp*_max 83,85 → 84 (WCTEMP)");
        var sys = snap.Disks.Single(d => d.Role == "system");
        sys.Controller.Should().Be("nvme0");
        sys.CritC.Should().BeNull("предел не сообщён → пороги 70/80");

        // Вентиляторы корпуса: только подключённые (rpm > 0); температуры nct6798 (AUXTIN 127/96) не читаются вовсе.
        snap.CaseFans!.Select(f => f.Rpm).Should().BeEquivalentTo(new[] { 1159, 4821 });
    }

    [Fact]
    public void Hwmon_NoRoot_ReturnsNull()
    {
        new HwmonReader(Path.Combine(_root, "нет"), Path.Combine(_root, "нет2"), Path.Combine(_root, "нет3"))
            .Read().Should().BeNull("чужая машина без hwmon — не исключение");
    }

    // ─── Уровни по таблице порогов (границы) ─────────────────────────────────────

    [Theory]
    [InlineData(94, "ok")] [InlineData(95, "warn")] [InlineData(104, "warn")] [InlineData(105, "danger")]
    public void Level_GpuMemory(int v, string expected) =>
        HardwareThresholds.Level(v, HardwareThresholds.GpuMem).Should().Be(expected);

    [Theory]
    [InlineData(84, "ok")] [InlineData(85, "warn")] [InlineData(91, "warn")] [InlineData(92, "danger")]
    public void Level_GpuCore(int v, string expected) =>
        HardwareThresholds.Level(v, HardwareThresholds.GpuCore).Should().Be(expected);

    [Theory]
    [InlineData(99, "ok", false)] [InlineData(100, "danger", false)]
    public void Level_Hotspot_NoWarnStep(int v, string expected, bool _) =>
        HardwareThresholds.Level(v, HardwareThresholds.GpuHotspot).Should().Be(expected);

    [Theory]
    [InlineData(89, "ok")] [InlineData(90, "warn")] [InlineData(105, "danger")]
    public void Level_Vrm(int v, string expected) =>
        HardwareThresholds.Level(v, HardwareThresholds.Vrm).Should().Be(expected);

    [Theory]
    [InlineData(84, 73, "ok")] [InlineData(84, 74, "warn")] [InlineData(84, 80, "warn")] [InlineData(84, 81, "danger")]
    public void Level_DiskWithLimit_FromCrit(int crit, int v, string expected) =>
        HardwareThresholds.Level(v, HardwareThresholds.Disk(crit)).Should().Be(expected);

    [Theory]
    [InlineData(69, "ok")] [InlineData(70, "warn")] [InlineData(79, "warn")] [InlineData(80, "danger")]
    public void Level_DiskNoLimit_70_80(int v, string expected) =>
        HardwareThresholds.Level(v, HardwareThresholds.Disk(null)).Should().Be(expected);

    [Fact]
    public void Level_NullValue_IsNone_AndWorst()
    {
        HardwareThresholds.Level(null, HardwareThresholds.Cpu).Should().Be("none");
        HardwareThresholds.Worst(["ok", "none", "warn"]).Should().Be("warn");
        HardwareThresholds.Worst(["warn", "danger"]).Should().Be("danger");
    }

    // ─── Сэмплер: кольцо, бакеты, свежесть ───────────────────────────────────────

    private (HardwareSampler sampler, ManualTime time, string cpuDir) BuildSampler(IGpuSampler? gpu = null, string? helperFile = null)
    {
        var time = new ManualTime();
        var hw = Dir("hw2");
        var cpu = Dir("hw2", "hwmon0");
        Put(cpu, "name", "k10temp");
        var opts = new LocalModelStatsOptions { Enabled = true, Gddr6HelperFile = helperFile ?? Path.Combine(_root, "none.json") };
        var sampler = new HardwareSampler(new FakeOptions(opts), gpu, new Gddr6HelperReader(time),
            new HwmonReader(hw, Dir("block2"), Path.Combine(_root, "mi")), new MeminfoReader(Path.Combine(_root, "mem")), time);
        return (sampler, time, cpu);
    }

    [Fact]
    public async Task Ring_BucketTakesMax_NullBreaks_NotZero()
    {
        var (s, time, cpu) = BuildSampler();
        // t0: 60 °C, +2 с: 50 °C (максимум не последний!), +4 с: датчик исчез (null).
        Put(cpu, "temp1_input", "60000"); await s.PollOnceAsync(); time.Advance(TimeSpan.FromSeconds(2));
        Put(cpu, "temp1_input", "50000"); await s.PollOnceAsync(); time.Advance(TimeSpan.FromSeconds(2));
        File.Delete(Path.Combine(cpu, "temp1_input")); await s.PollOnceAsync(); time.Advance(TimeSpan.FromSeconds(2));

        var pts = s.Aggregate(x => x.Cpu?.Tctl, TimeSpan.FromMinutes(1), TimeSpan.FromSeconds(10));
        pts.Should().HaveCount(6, "окно 1 мин / шаг 10 с");
        pts.Where(p => p.value is not null).Should().ContainSingle().Which.value.Should().Be(60,
            "в бакете максимум (50, 60), снимок с null значения не вносит");
        pts.Count(p => p.value is null).Should().Be(5, "пустой шаг — null (разрыв), не 0");
    }

    [Fact]
    public async Task Status_StaleAfter10s_NotBefore()
    {
        var (s, time, cpu) = BuildSampler();
        s.Status.Should().Be("ok", "снимков ещё нет — не stale");
        Put(cpu, "temp1_input", "50000");
        await s.PollOnceAsync();
        var at = time.GetUtcNow();
        time.Advance(TimeSpan.FromSeconds(9));
        s.Status.Should().Be("ok");
        time.Advance(TimeSpan.FromSeconds(2));
        s.Status.Should().Be("stale");
        s.StaleSince.Should().Be(at.ToUnixTimeMilliseconds());
    }

    // ─── Отчёт ───────────────────────────────────────────────────────────────────

    private static GpuSample Gpu(int temp, string bus = "00000000:0B:00.0") => new()
    {
        Index = 0, Uuid = "GPU-1", Name = "RTX 3090", Temperature = temp, BusId = bus, SwPowerCap = true,
    };

    [Fact]
    public async Task Report_MemFromHelper_LevelWorst_PowerCapNotThermal()
    {
        var file = Path.Combine(_root, "temps.json");
        var (s, time, _) = BuildSampler(new FakeGpu(Gpu(70)), file);
        File.WriteAllText(file, HelperJson(time.GetUtcNow(),
            "[{\"busId\":\"0000:0b:00.0\",\"deviceId\":\"0x2204\",\"memTempC\":96,\"hotspotTempC\":75}]"));
        await s.PollOnceAsync();

        var report = new HardwareReportService(s).Build(TimeSpan.FromMinutes(15));
        var g = report.Gpus.Single();
        g.MemTempC.Should().Be(96, "связка helper↔nvidia-smi по каноническому busId");
        g.Level.Should().Be("warn");
        g.Levels.Mem.Should().Be("warn");
        g.Levels.Core.Should().Be("ok");
        g.Throttle.PowerCap.Should().BeTrue();
        g.Throttle.Thermal.Should().BeFalse();
        var node = report.Nodes.Single(n => n.Kind == "gpu");
        node.TempC.Should().Be(96);
        node.WarnC.Should().Be(95);
        node.Spark15m.Should().HaveCount(90);
        report.Level.Should().Be("warn");
        report.Series.Select(x => x.Family).Should().Contain(["mem", "core", "hotspot"]);
    }

    [Fact]
    public async Task Report_HelperStale_MemNull_TileFallsBackToCore()
    {
        var file = Path.Combine(_root, "temps.json");
        var (s, time, _) = BuildSampler(new FakeGpu(Gpu(70)), file);
        File.WriteAllText(file, HelperJson(time.GetUtcNow().AddSeconds(-30)));
        await s.PollOnceAsync();

        var g = new HardwareReportService(s).Build(TimeSpan.FromMinutes(1)).Gpus.Single();
        g.MemTempC.Should().BeNull();
        g.HotspotTempC.Should().BeNull();
        g.CoreTempC.Should().Be(70, "остальное работает");
    }

    [Fact]
    public async Task Report_Stale_LevelsNotPainted()
    {
        var file = Path.Combine(_root, "temps.json");
        var (s, time, _) = BuildSampler(new FakeGpu(Gpu(95)), file);
        await s.PollOnceAsync();
        time.Advance(TimeSpan.FromSeconds(30));

        var report = new HardwareReportService(s).Build(TimeSpan.FromMinutes(1));
        report.Status.Should().Be("stale");
        report.Level.Should().Be("ok", "при stale уровни не красятся");
        report.Gpus.Single().Level.Should().Be("ok");
        report.Nodes.Single(n => n.Kind == "gpu").Level.Should().Be("ok");
    }

    [Fact]
    public void Report_NoSources_EmptyOk()
    {
        var (s, _, _) = BuildSampler();
        var report = new HardwareReportService(s).Build(TimeSpan.FromMinutes(5));
        report.Status.Should().Be("ok");
        report.Nodes.Should().BeEmpty();
        report.Gpus.Should().BeEmpty();
        report.StepMs.Should().Be(5000);
    }

    [Theory]
    [InlineData(1, 2000)] [InlineData(5, 5000)] [InlineData(15, 10000)] [InlineData(60, 30000)]
    public void StepFor_ByWindow(int minutes, int stepMs) =>
        HardwareReportService.StepFor(TimeSpan.FromMinutes(minutes)).TotalMilliseconds.Should().Be(stepMs);
}
