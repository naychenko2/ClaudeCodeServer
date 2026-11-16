using ClaudeHomeServer.Services.Llm;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
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

    // Реестра HardwareNodes нет → ровно один узел «worker» с ролью карт ["text"] (побайтно сегодняшнее).
    private static IConfiguration NoNodes() => new ConfigurationBuilder().Build();

    private static void Put(string dir, string file, string text) => File.WriteAllText(Path.Combine(dir, file), text);

    // ─── Файл помощника ──────────────────────────────────────────────────────────

    private static string HelperJson(DateTimeOffset at, string gpus = "[{\"busId\":\"0000:0b:00.0\",\"deviceId\":\"0x2204\",\"memTempC\":68,\"hotspotTempC\":75}]") =>
        $"{{\"v\":1,\"at\":{at.ToUnixTimeMilliseconds()},\"gpus\":{gpus}}}";

    [Fact]
    public void Helper_Fresh_ParsesValues()
    {
        var t = new ManualTime();
        var r = new Gddr6HelperReader(t).Parse(HelperJson(t.GetUtcNow().AddSeconds(-3)), t.GetUtcNow());
        r.Should().NotBeNull();
        r!.Value.fresh.Should().BeTrue();
        r.Value.gpus.Should().ContainSingle().Which.MemTempC.Should().Be(68);
        r.Value.gpus[0].HotspotTempC.Should().Be(75);
    }

    [Fact]
    public void Helper_Stale_NotFresh()
    {
        var t = new ManualTime();
        var r = new Gddr6HelperReader(t).Parse(HelperJson(t.GetUtcNow().AddSeconds(-11)), t.GetUtcNow());
        r!.Value.fresh.Should().BeFalse("метка at старше 10 с — данных нет");
    }

    [Fact]
    public void Helper_AtExactlyNow_IsFresh()
    {
        var t = new ManualTime();
        var r = new Gddr6HelperReader(t).Parse(HelperJson(t.GetUtcNow()), t.GetUtcNow());
        r!.Value.fresh.Should().BeTrue("возраст ровно 0 — граница свежести включительна");
    }

    [Fact]
    public void Helper_MarkInFuture_NotFresh()
    {
        var t = new ManualTime();
        // Метка "at" впереди часов отбора дальше окна → не свежесть (для локального файла
        // часы узла и есть часы сэмплера). Окно двустороннее ±StaleAfter (ADR-029): метка из
        // будущего за окном — сдвиг часов или битое значение, с односторонней проверкой такой
        // файл казался бы свежим, пока часы не догонят метку.
        var r = new Gddr6HelperReader(t).Parse(HelperJson(t.GetUtcNow().AddSeconds(11)), t.GetUtcNow());
        r!.Value.fresh.Should().BeFalse("отрицательный возраст — не свежесть");
    }

    [Theory]
    [InlineData("не json")]
    [InlineData("{\"v\":2,\"at\":1,\"gpus\":[]}")]
    [InlineData("")]
    public void Helper_Broken_ReturnsNull(string text)
    {
        var t = new ManualTime();
        new Gddr6HelperReader(t).Parse(text, t.GetUtcNow()).Should().BeNull();
    }

    [Fact]
    public void Helper_MissingFile_ReturnsNull()
    {
        new Gddr6HelperReader(new ManualTime()).Read(Path.Combine(_root, "нет.json")).Should().BeNull();
    }

    // Часы удалённого узла фермы идут впереди часов worker (обычный NTP-дрейф): свежесть gddr6
    // решается по часам узла (NodeReading.ClockMs), а не по часам worker (ADR-029, NodeSlice:55).
    // Мутация «взять в Parse _time.GetUtcNow() вместо now» краснит случай −11: с часами worker
    // возраст +6 с — в окне ±StaleAfter (свежесть), а по часам узла +11 с — за окном.
    [Theory]
    [InlineData(-1, true)] // возраст 1 с по часам узла — свежий
    [InlineData(1, true)] // метка на 1 с впереди часов узла — в окне ±StaleAfter: свежая (односторонняя
                          // проверка давала бы mem/hotspot = nodata на живом узле)
    [InlineData(11, false)] // метка на 11 с впереди часов узла — за окном — не свежесть (нижняя граница возраста)
    [InlineData(-11, false)] // метка на 11 с старше часов узла — протухло (StaleAfter = 10 с)
    public void Helper_FreshnessIsByNodeClock_NotWorkerClock(int atOffsetSec, bool expected)
    {
        var t = new ManualTime();
        var nodeClock = t.GetUtcNow().AddSeconds(5);
        var r = new Gddr6HelperReader(t).Parse(HelperJson(nodeClock.AddSeconds(atOffsetSec)), nodeClock);
        r!.Value.fresh.Should().Be(expected);
    }

    // Срез удалённого узла: текст помощника + настенные часы узла (как у RemoteReadingSource).
    private sealed class RemoteNodeSource(string? gddr6, long? clockMs, IGpuSampler gpu) : INodeReadingSource
    {
        public async Task<NodeReading> ReadAsync(CancellationToken ct = default) => new()
        {
            Status = NodeReadingStatus.Ok,
            Gpus = await gpu.ReadAsync(ct),
            Gddr6 = gddr6,
            ClockMs = clockMs,
        };
    }

    private HardwareSampler BuildRemoteSampler(ManualTime time, string helperText, DateTimeOffset nodeClock)
    {
        var src = new RemoteNodeSource(helperText, nodeClock.ToUnixTimeMilliseconds(), new FakeGpu(Gpu(70)));
        return new HardwareSampler(new FakeOptions(new LocalModelStatsOptions { Enabled = true }),
            [new HardwareSamplerNode("video", src)], new Gddr6HelperReader(time), time);
    }

    // Тот же сдвиг часов сквозом через сэмплер: файл помощника возрастом 1 с ПО ЧАСАМ УЗЛА —
    // свежий, mem/hotspot на месте. Сдвиг узла взят 15 с — больше окна ±StaleAfter, иначе
    // двустороннее окно делает выбор часов неразличимым. Мутация «передать в сэмплере
    // _time.GetUtcNow()» краснит этот тест: по часам worker возраст −14 с — за окном.
    [Fact]
    public async Task RemoteNode_ClockAheadOfWorker_Gddr6StaysFresh()
    {
        var time = new ManualTime();
        var nodeClock = time.GetUtcNow().AddSeconds(15);
        var s = await BuildRemoteSampler(time, HelperJson(nodeClock.AddSeconds(-1)), nodeClock).PollNodeAsync("video");

        var g = s!.Gpus!.Single();
        g.MemTempC.Should().Be(68, "свежесть по часам узла, а не worker");
        g.HotspotTempC.Should().Be(75);
        g.CoreTempC.Should().Be(70);
    }

    // Метка на 11 с старше часов узла — протухло: mem/hotspot = null, остальное работает.
    // Мутация «передать в сэмплере _time.GetUtcNow()» краснит этот тест: при сдвиге узла 15 с
    // возраст по часам worker +4 с — в окне (свежесть), а по часам узла +11 с — за окном.
    [Fact]
    public async Task RemoteNode_Gddr6StaleByNodeClock_MemNullCoreWorks()
    {
        var time = new ManualTime();
        var nodeClock = time.GetUtcNow().AddSeconds(15);
        var s = await BuildRemoteSampler(time, HelperJson(nodeClock.AddSeconds(-11)), nodeClock).PollNodeAsync("video");

        var g = s!.Gpus!.Single();
        g.MemTempC.Should().BeNull();
        g.HotspotTempC.Should().BeNull();
        g.CoreTempC.Should().Be(70);
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
        Put(cpu, "temp1_input", "76250"); Put(cpu, "temp1_label", "Tctl");
        Put(cpu, "temp3_input", "78000"); Put(cpu, "temp3_label", "Tccd1");
        Put(cpu, "temp4_input", "49750"); Put(cpu, "temp4_label", "Tccd2");

        var board = Dir("hwmon", "hwmon6");
        Put(board, "name", "asusec\n");
        Put(board, "temp1_input", "78000"); Put(board, "temp1_label", "Chipset");
        Put(board, "temp5_input", "53000"); Put(board, "temp5_label", "VRM");
        Put(board, "fan2_input", "1159"); Put(board, "fan2_label", "Chipset");
        Put(board, "temp4_input", "45000"); Put(board, "temp4_label", "T_Sensor");
        Put(board, "temp6_input", "-40000"); Put(board, "temp6_label", "Water_In");

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

        snap.Cpus!.Should().ContainSingle().Which.Tctl.Should().Be(76);
        snap.Cpus![0].Ccd.Should().Equal(78, 49);
        snap.Board!.Vrm.Should().Be(53);
        snap.Board.Chipset.Should().Be(78);
        snap.Board.ChipsetFanRpm.Should().Be(1159);
        snap.Board.Water.Should().Be(45, "вода СЖО — T_Sensor, а не Water_In");

        var kv = snap.Disks!.Single(d => d.Role == "kv");
        kv.Controller.Should().Be("nvme1");
        kv.TempC.Should().Be(82, "самый горячий датчик контроллера");
        kv.Temp2C.Should().Be(58);
        kv.CritC.Should().Be(84, "temp*_max 83,85 → 84 (WCTEMP)");
        var sys = snap.Disks!.Single(d => d.Role == "system");
        sys.Controller.Should().Be("nvme0");
        sys.CritC.Should().BeNull("предел не сообщён → пороги 70/80");

        // Вентиляторы корпуса: только подключённые (rpm > 0); температуры nct6798 (AUXTIN 127/96) не читаются вовсе.
        snap.CaseFans!.Select(f => f.Rpm).Should().BeEquivalentTo(new[] { 1159, 4821 });
    }

    [Fact]
    public void Hwmon_WaterProbeDisconnected_Minus40_IsNull()
    {
        var hw = Dir("hwW");
        var board = Dir("hwW", "hwmon4");
        Put(board, "name", "asusec\n");
        Put(board, "temp1_input", "73000"); Put(board, "temp1_label", "Chipset");
        Put(board, "temp4_input", "-40000"); Put(board, "temp4_label", "T_Sensor");
        var snap = new HwmonReader(hw, Dir("blockW"), Path.Combine(_root, "miW")).Read()!;
        snap.Board!.Water.Should().BeNull("−40 °C у asusec — датчик не подключён");
    }

    [Fact]
    public void Hwmon_NoRoot_ReturnsNull()
    {
        new HwmonReader(Path.Combine(_root, "нет"), Path.Combine(_root, "нет2"), Path.Combine(_root, "нет3"))
            .Read().Should().BeNull("чужая машина без hwmon — не исключение");
    }

    // ─── Диски: used/total — пара с одной ФС (DriveInfo подменяется делегатом) ─────

    // Фейковый DriveInfo: «точка монтирования → (всего, свободно) в ГБ»; неизвестная → (0,0) → null.
    private static Func<string, (long total, long free)> FakeFsSize(Dictionary<string, (long totalGb, long freeGb)> map) =>
        mp => map.TryGetValue(mp, out var v)
            ? (v.totalGb * 1024L * 1024 * 1024, v.freeGb * 1024L * 1024 * 1024)
            : (0, 0);

    // hwmon с двумя nvme-контроллерами (nvme0 — system, nvme1 — kv) + mountinfo и fsSize на подмену.
    private (HwmonReader reader, string mountinfo) BuildFakeHwmonDisks(string mountinfoContent, Func<string, (long, long)> fsSize)
    {
        var hw = Dir("hwmon-disk");
        var nvme1 = Dir("hwmon-disk", "hwmon1");
        Put(nvme1, "name", "nvme\n");
        File.CreateSymbolicLink(Path.Combine(nvme1, "device"), "../../nvme1");
        var nvme0 = Dir("hwmon-disk", "hwmon0");
        Put(nvme0, "name", "nvme\n");
        File.CreateSymbolicLink(Path.Combine(nvme0, "device"), "../../nvme0");

        var mountinfo = Path.Combine(_root, "mountinfo-disk");
        File.WriteAllText(mountinfo, mountinfoContent);
        return (new HwmonReader(hw, Dir("block-disk"), mountinfo, fsSize), mountinfo);
    }

    [Fact]
    public void Hwmon_KvDisk_UsesKvdiskFs_NotLargestOnController()
    {
        // Один контроллер (nvme1) с ДВУМЯ ФС: большая «не kvdisk» (/mnt/big) и меньшая в kvdisk.
        var map = new Dictionary<string, (long, long)>
        {
            ["/"] = (500, 100),
            ["/home/an/llm/kvdisk"] = (492, 122),
            ["/mnt/big"] = (1000, 500),
        };
        var content =
            "36 1 8:1 / / rw - ext4 /dev/nvme0n1p2 rw\n" +
            "364 43 259:1 / /home/an/llm/kvdisk rw shared:197 - ext4 /dev/nvme1n1p1 rw\n" +
            "365 44 259:2 / /mnt/big rw - ext4 /dev/nvme1n1p2 rw\n";
        var (reader, _) = BuildFakeHwmonDisks(content, FakeFsSize(map));
        var snap = reader.Read()!;

        var kv = snap.Disks!.Single(d => d.Role == "kv");
        kv.Controller.Should().Be("nvme1");
        kv.UsedGb.Should().Be(370, "kv — ФС kvdisk (492−122), а не наибольшая на контроллере /mnt/big");
        kv.TotalGb.Should().Be(492, "total — той же ФС kvdisk, не из /sys/block");

        var sys = snap.Disks!.Single(d => d.Role == "system");
        sys.UsedGb.Should().Be(400, "system — наибольшая ФС контроллера (/)");
        sys.TotalGb.Should().Be(500);
    }

    [Theory]
    [InlineData(492, 122)]   // used=370, total=492
    [InlineData(500, 100)]   // used=400, total=500
    [InlineData(100, 99)]    // почти полная: used=1, total=100
    [InlineData(1000, 0)]    // полная: used=1000, total=1000
    public void Hwmon_UsedLeqTotal_Always(long totalGb, long freeGb)
    {
        var map = new Dictionary<string, (long, long)>
        {
            ["/home/an/llm/kvdisk"] = (totalGb, freeGb),
        };
        var content = "364 43 259:1 / /home/an/llm/kvdisk rw shared:197 - ext4 /dev/nvme1n1p1 rw\n";
        var (reader, _) = BuildFakeHwmonDisks(content, FakeFsSize(map));
        var snap = reader.Read()!;

        var kv = snap.Disks!.Single(d => d.Role == "kv");
        kv.UsedGb.Should().NotBeNull();
        kv.TotalGb.Should().NotBeNull();
        kv.UsedGb!.Value.Should().BeLessOrEqualTo(kv.TotalGb!.Value, "used ≤ total всегда");
    }

    [Fact]
    public void Hwmon_NoMountedFs_UsedTotalNull()
    {
        // nvme1 — kv (kvdisk смонтирован, ФС читается); nvme0 — system, но ни одной ФС не смонтировано.
        var map = new Dictionary<string, (long, long)>
        {
            ["/home/an/llm/kvdisk"] = (492, 122),
        };
        var content = "364 43 259:1 / /home/an/llm/kvdisk rw shared:197 - ext4 /dev/nvme1n1p1 rw\n";
        var (reader, _) = BuildFakeHwmonDisks(content, FakeFsSize(map));
        var snap = reader.Read()!;

        var sys = snap.Disks!.Single(d => d.Role == "system");
        sys.UsedGb.Should().BeNull("контроллер без смонтированных ФС → null, не 0");
        sys.TotalGb.Should().BeNull();
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

    private const string CpuDir = "/sys/class/hwmon/hwmon0/";

    private (HardwareSampler sampler, ManualTime time, FakeNodeSource src) BuildSampler(IGpuSampler? gpu = null, string? helperFile = null)
    {
        var time = new ManualTime();
        var src = new FakeNodeSource(gpu, helperFile);
        src.Files["/sys/class/hwmon/hwmon0/name"] = "k10temp\n";
        src.Files[CpuDir + "temp1_label"] = "Tctl\n";
        var opts = new LocalModelStatsOptions { Enabled = true };
        var sampler = new HardwareSampler(new FakeOptions(opts), [new HardwareSamplerNode("worker", src)],
            new Gddr6HelperReader(time), time);
        return (sampler, time, src);
    }

    [Fact]
    public async Task Ring_BucketTakesMax_NullBreaks_NotZero()
    {
        var (sampler, time, src) = BuildSampler();
        var s = sampler.ForNode("worker")!;
        // t0: 60 °C, +2 с: 50 °C (максимум не последний!), +4 с: датчик исчез (null).
        src.Files[CpuDir + "temp1_input"] = "60000"; await sampler.PollNodeAsync("worker"); time.Advance(TimeSpan.FromSeconds(2));
        src.Files[CpuDir + "temp1_input"] = "50000"; await sampler.PollNodeAsync("worker"); time.Advance(TimeSpan.FromSeconds(2));
        src.Files.Remove(CpuDir + "temp1_input"); await sampler.PollNodeAsync("worker"); time.Advance(TimeSpan.FromSeconds(2));

        var pts = s.Aggregate(x => x.Cpus?[0].Tctl, TimeSpan.FromMinutes(1), TimeSpan.FromSeconds(10));
        pts.Should().HaveCount(6, "окно 1 мин / шаг 10 с");
        pts.Where(p => p.value is not null).Should().ContainSingle().Which.value.Should().Be(60,
            "в бакете максимум (50, 60), снимок с null значения не вносит");
        pts.Count(p => p.value is null).Should().Be(5, "пустой шаг — null (разрыв), не 0");
    }

    [Fact]
    public async Task Status_StaleAfter10s_NotBefore()
    {
        var (sampler, time, src) = BuildSampler();
        var s = sampler.ForNode("worker")!;
        s.Status.Should().Be("ok", "снимков ещё нет — не stale");
        src.Files[CpuDir + "temp1_input"] = "50000";
        await sampler.PollNodeAsync("worker");
        var at = time.GetUtcNow();
        time.Advance(TimeSpan.FromSeconds(9));
        s.Status.Should().Be("ok");
        time.Advance(TimeSpan.FromSeconds(2));
        s.Status.Should().Be("stale");
        s.Since.Should().Be(at.ToUnixTimeMilliseconds());
    }

    // (а) Метка Stale-снимка = время данных (now − AgeMs), не время получения; в кольцо он кладётся,
    // только когда его время данных новее последнего снимка.
    [Fact]
    public async Task Ring_StaleSample_AtIsDataTime_NotNow()
    {
        var (sampler, time, src) = BuildSampler();
        var s = sampler.ForNode("worker")!;
        src.Files[CpuDir + "temp1_input"] = "50000";

        // Ok-срез в t0 — контроль: метка = время получения (былое поведение).
        await sampler.PollNodeAsync("worker");
        var t0 = time.GetUtcNow();
        s.Current!.At.Should().Be(t0, "Ok-срез (AgeMs = 0) метится временем получения");

        // Stale-срез возрастом 12 с через +14 с после t0: время данных (t0+2 с) новее Ok-среза → в кольцо.
        src.Status = NodeReadingStatus.Stale;
        src.AgeMs = 12_000;
        time.Advance(TimeSpan.FromSeconds(14));
        await sampler.PollNodeAsync("worker");
        var t1 = time.GetUtcNow();
        s.Status.Should().Be("stale");
        s.Current!.At.Should().Be(t1 - TimeSpan.FromSeconds(12),
            "Stale-срез метится временем данных (now − AgeMs), а не now");
        sampler.RingCount("worker").Should().Be(2, "Stale-срез с более свежим временем данных — в кольцо");

        // Читатели кольца не видят «живого» устаревшего: Stale-снимок попадает в бакет своих данных
        // ([t1−20с, t1−10с)), а не в текущий — тот пустой разрыв.
        var pts = s.Aggregate(x => x.Cpus?[0].Tctl, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(10));
        pts.Count(p => p.value is not null).Should().Be(1);
        pts.Single(p => p.value is not null).value.Should().Be(50);
        pts.Last().value.Should().BeNull("текущий бакет пуст: устаревшее не выглядит живым");
    }

    // (б) Порядок кольца после возврата в Ok: метки не убывают, Ok-срез метится своим временем.
    [Fact]
    public async Task Ring_AfterStaleReturnToOk_MarksAscending()
    {
        var (sampler, time, src) = BuildSampler();
        var s = sampler.ForNode("worker")!;
        src.Files[CpuDir + "temp1_input"] = "50000";
        await sampler.PollNodeAsync("worker");

        // Stale-срез со временем данных t0+2 с (возраст 12 с через +14 с после Ok-среза в t0).
        src.Status = NodeReadingStatus.Stale;
        src.AgeMs = 12_000;
        time.Advance(TimeSpan.FromSeconds(14));
        await sampler.PollNodeAsync("worker");
        var staleAt = time.GetUtcNow() - TimeSpan.FromSeconds(12);

        // Возврат в Ok: следующий срез метится своим временем (позже Stale-метки), статус — ok.
        src.Status = NodeReadingStatus.Ok;
        src.AgeMs = 0;
        src.Files[CpuDir + "temp1_input"] = "60000";
        time.Advance(TimeSpan.FromSeconds(2));
        await sampler.PollNodeAsync("worker");
        var cur = s.Current!;
        cur.At.Should().Be(staleAt + TimeSpan.FromSeconds(14), "после Stale-среза метки кольца не убывают");
        cur.Cpus![0].Tctl.Should().Be(60);
        s.Status.Should().Be("ok", "свежий Ok-срез — статус ok");
        s.Since.Should().BeNull();
        sampler.RingCount("worker").Should().Be(3);
    }

    // Since у stale = время данных последнего снимка в кольце (Current.At), а не время последнего тика:
    // агент отдаёт тот же старый срез раз в 2 с — метка не едет вместе с тиками (api.md «staleSince —
    // момент последнего живого снимка»). Мутация «Since = _lastSampleTime (как было)» краснит тест:
    // после первого тика метка = время получения, дальше сдвигается на 2 с каждый тик.
    [Fact]
    public async Task Status_SinceStale_IsLastSampleDataTime_NotTickTime()
    {
        var (sampler, time, src) = BuildSampler();
        var s = sampler.ForNode("worker")!;
        src.Files[CpuDir + "temp1_input"] = "50000";

        // Ok-срез в t0, затем тот же срез от агента: в t0+2k с с возрастом 2k с — время данных всегда t0.
        await sampler.PollNodeAsync("worker");
        var t0 = time.GetUtcNow();
        src.Status = NodeReadingStatus.Stale;
        for (var k = 1; k <= 3; k++)
        {
            src.AgeMs = 2_000 * k;
            time.Advance(TimeSpan.FromSeconds(2));
            await sampler.PollNodeAsync("worker");
            s.Status.Should().Be("stale");
            s.Since.Should().Be(t0.ToUnixTimeMilliseconds(),
                $"Since после Stale-тика {k} — время данных последнего снимка (t0), не время тика");
        }
    }

    // Stale-срез с временем данных не новее последнего снимка кольца в кольцо не кладётся: обновляется
    // только статус. Мутация «убрать проверку sample.At ≤ At последнего» краснит тест: RingCount 4
    // вместо 1 — в кольце три дубли одного среза.
    [Fact]
    public async Task Ring_RepeatedStaleSlice_NotDuplicated()
    {
        var (sampler, time, src) = BuildSampler();
        var s = sampler.ForNode("worker")!;
        src.Files[CpuDir + "temp1_input"] = "50000";
        await sampler.PollNodeAsync("worker");
        var t0 = time.GetUtcNow();

        // Три Stale-тика одного среза (возраст растёт на 2000, время данных всегда t0): кольцо выросло
        // ровно на 1 (Ok-срез), а не на 3.
        src.Status = NodeReadingStatus.Stale;
        for (var k = 1; k <= 3; k++)
        {
            src.AgeMs = 2_000 * k;
            time.Advance(TimeSpan.FromSeconds(2));
            await sampler.PollNodeAsync("worker");
        }
        sampler.RingCount("worker").Should().Be(1, "тот же Stale-срез не дублируется в кольце");
        s.Current!.At.Should().Be(t0, "последний снимок кольца — Ok-срез, его метка не тронута");
        s.Since.Should().Be(t0.ToUnixTimeMilliseconds(), "Since указывает на тот же снимок");
    }

    // Джиттер ageMs на стороне агента: ageMs пересчитывается в момент отправки (ccs_node_stats.py),
    // поэтому время данных одного и того же среза At = now − AgeMs гуляет на единицы–десятки мс
    // в обе стороны. С допуском (половина интервала опроса) дублей нет. Мутация «вернуть строгое
    // > без допуска» краснит этот тест: на первом же тике с At = t0+3 мс кольцо растёт до 2,
    // Current смещается, Since дрогает на 3 мс.
    [Fact]
    public async Task Ring_StaleJitterWithinTolerance_NotDuplicated()
    {
        var (sampler, time, src) = BuildSampler();
        var s = sampler.ForNode("worker")!;
        src.Files[CpuDir + "temp1_input"] = "50000";
        await sampler.PollNodeAsync("worker");
        var t0 = time.GetUtcNow();

        // Шесть Stale-тиков одного среза: AgeMs = 2000k ± 3 мс, знаки чередуются — At = t0 ∓ 3 мс.
        src.Status = NodeReadingStatus.Stale;
        for (var k = 1; k <= 6; k++)
        {
            src.AgeMs = 2_000 * k + (k % 2 == 1 ? -3 : 3);
            time.Advance(TimeSpan.FromSeconds(2));
            await sampler.PollNodeAsync("worker");
            s.Since.Should().Be(t0.ToUnixTimeMilliseconds(),
                $"Since после джиттер-тика {k} — не двигается, срез тот же");
        }
        sampler.RingCount("worker").Should().Be(1, "джиттер в пределах допуска — дублей в кольце нет");
        s.Current!.At.Should().Be(t0, "Current не смещается джиттер-тиками");
    }

    // Новый срез агента: его время данных новее последнего в кольце на интервал сборки агента
    // (≥ интервала опроса) — новая запись кольца, Since переезжает на неё. Граница «ровно
    // интервал опроса» — тоже новая запись (строгий ≥ по постановке). Граница допуска: ровно
    // 1 с (допуск) — не считается новой: ни кольцо, ни Current не тронуты.
    [Fact]
    public async Task Ring_NewerStaleSliceByInterval_AddedAndSinceMoves()
    {
        var (sampler, time, src) = BuildSampler();
        var s = sampler.ForNode("worker")!;
        src.Files[CpuDir + "temp1_input"] = "50000";
        await sampler.PollNodeAsync("worker");
        var t0 = time.GetUtcNow();

        // Stale-тик того же среза: ни кольцо, ни Since не меняются.
        src.Status = NodeReadingStatus.Stale;
        src.AgeMs = 2_000;
        time.Advance(TimeSpan.FromSeconds(2));
        await sampler.PollNodeAsync("worker");
        sampler.RingCount("worker").Should().Be(1);
        s.Since.Should().Be(t0.ToUnixTimeMilliseconds());

        // Новый срез: в t0+16 с его время данных t0+2 с — ровно на интервал опроса новее
        // последнего в кольце.
        src.AgeMs = 14_000;
        time.Advance(TimeSpan.FromSeconds(14));
        await sampler.PollNodeAsync("worker");
        var newAt = t0 + TimeSpan.FromSeconds(2);
        sampler.RingCount("worker").Should().Be(2, "срез, новее на интервал опроса, — новая запись кольца");
        s.Current!.At.Should().Be(newAt);
        s.Since.Should().Be(newAt.ToUnixTimeMilliseconds(), "Since — время данных нового среза");

        // Граница допуска: в t0+18 с время данных t0+3 с — ровно на допуск (1 с) новее —
        // не считается новым.
        src.AgeMs = 15_000;
        time.Advance(TimeSpan.FromSeconds(2));
        await sampler.PollNodeAsync("worker");
        sampler.RingCount("worker").Should().Be(2, "ровно допуск — не новый срез");
        s.Current!.At.Should().Be(newAt, "Current остаётся предыдущая запись");
        s.Since.Should().Be(newAt.ToUnixTimeMilliseconds(), "Since не двигается");
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
        await s.PollNodeAsync("worker");

        var report = new HardwareReportService(s, NoNodes()).Build(TimeSpan.FromMinutes(15))!;
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
        await s.PollNodeAsync("worker");

        var g = new HardwareReportService(s, NoNodes()).Build(TimeSpan.FromMinutes(1))!.Gpus.Single();
        g.MemTempC.Should().BeNull();
        g.HotspotTempC.Should().BeNull();
        g.CoreTempC.Should().Be(70, "остальное работает");
    }

    [Fact]
    public async Task Report_Stale_LevelsNotPainted()
    {
        var file = Path.Combine(_root, "temps.json");
        var (s, time, _) = BuildSampler(new FakeGpu(Gpu(95)), file);
        await s.PollNodeAsync("worker");
        time.Advance(TimeSpan.FromSeconds(30));

        var report = new HardwareReportService(s, NoNodes()).Build(TimeSpan.FromMinutes(1))!;
        report.Status.Should().Be("stale");
        report.Level.Should().Be("ok", "при stale уровни не красятся");
        report.Gpus.Single().Level.Should().Be("ok");
        report.Nodes.Single(n => n.Kind == "gpu").Level.Should().Be("ok");
    }

    private sealed class FixedSampler(HardwareSample cur) : IHardwareSampler, INodeHardwareView
    {
        public IReadOnlyList<string> NodeKeys => ["worker"];
        public INodeHardwareView? ForNode(string key) => key == "worker" ? this : null;
        public string Key => "worker";
        public HardwareSample? Current => cur;
        public string Status => "ok";
        public long? Since => null;
        public IReadOnlyList<(long t, int? value)> Aggregate(Func<HardwareSample, int?> selector, TimeSpan window, TimeSpan step) =>
            [(0, selector(cur))];
    }

    [Fact]
    public void Report_Water_Node_And_Series_KvDisk_WithoutTemperature()
    {
        var sample = new HardwareSample
        {
            At = DateTimeOffset.UtcNow,
            Water = 52,
            RamUsedGb = 32, RamTotalGb = 64,
            Disks =
            [
                new HwmonDisk { Controller = "nvme1", Role = "kv", TempC = 83, Temp2C = 57, CritC = 84, UsedGb = 370, TotalGb = 492 },
                new HwmonDisk { Controller = "nvme0", Role = "system", TempC = 59 },
            ],
        };
        var report = new HardwareReportService(new FixedSampler(sample), NoNodes()).Build(TimeSpan.FromMinutes(15))!;

        var water = report.Nodes.Single(n => n.Key == "water");
        water.TempC.Should().Be(52);
        water.Level.Should().Be("warn", "предупреждение с 50 °C");
        water.DangerC.Should().Be(55);
        report.Series.Should().Contain(x => x.Key == "water" && x.Family == "water");

        var kv = report.Nodes.Single(n => n.Detail.Role == "kv");
        kv.TempC.Should().BeNull("датчики диска кэша врут — температуру не показываем");
        kv.Level.Should().Be("ok", "75 % заполненности — ниже порога 85 (порог заполненности теперь участвует)");
        kv.DangerC.Should().BeNull();
        kv.Detail.Temp2C.Should().BeNull();
        kv.Detail.UsedGb.Should().Be(370);
        kv.Detail.UsedPerc.Should().Be(75, "370 из 492 ГБ");
        kv.Spark15m.Should().Equal([75], "мини-график диска кэша — занято, %");
        report.Series.Should().NotContain(x => x.Key == "disk-nvme1");
        report.Series.Should().Contain(x => x.Key == "disk-nvme0");
        report.Level.Should().Be("warn", "уровень стенда — по воде; кэш 75 % и плата без данных — не тревога");

        var ram = report.Nodes.Single(n => n.Key == "ram");
        ram.Detail.UsedPerc.Should().Be(50);
        ram.Spark15m.Should().Equal([50], "мини-график RAM — занято, %");
        ram.Level.Should().Be("none");
    }

    [Fact]
    public void Report_NoSources_EmptyOk()
    {
        var (s, _, _) = BuildSampler();
        var report = new HardwareReportService(s, NoNodes()).Build(TimeSpan.FromMinutes(5))!;
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
