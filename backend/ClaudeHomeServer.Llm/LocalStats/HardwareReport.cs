using System.Text.Json.Serialization;

namespace ClaudeHomeServer.Services.Llm;

// Пороги температур вкладки «Железо», °C: ЕДИНСТВЕННАЯ таблица (фронт их не дублирует,
// спецификация docs/mockups/local-model-hardware-spec.md). Warn == null — предупреждения нет.
public static class HardwareThresholds
{
    public static readonly (int? Warn, int Danger) GpuMem = (95, 105);
    public static readonly (int? Warn, int Danger) GpuCore = (85, 92);
    public static readonly (int? Warn, int Danger) GpuHotspot = (null, 100);
    public static readonly (int? Warn, int Danger) Cpu = (85, 95);
    public static readonly (int? Warn, int Danger) Vrm = (90, 105);
    public static readonly (int? Warn, int Danger) Chipset = (85, 95);
    // Диск без известного предела.
    public static readonly (int? Warn, int Danger) DiskNoLimit = (70, 80);
    // Диск с пределом: предупреждение за 10 °C до него, опасность за 3 °C.
    public const int DiskWarnBelowLimit = 10;
    public const int DiskDangerBelowLimit = 3;

    public static (int? Warn, int Danger) Disk(int? limitC) =>
        limitC is { } l ? (l - DiskWarnBelowLimit, l - DiskDangerBelowLimit) : DiskNoLimit;

    // "ok" | "warn" | "danger"; "none" — значения нет.
    public static string Level(int? value, (int? Warn, int Danger) t)
    {
        if (value is not { } v) return "none";
        if (v >= t.Danger) return "danger";
        if (t.Warn is { } w && v >= w) return "warn";
        return "ok";
    }

    // Худший из уровней ("none" не считается).
    public static string Worst(IEnumerable<string> levels)
    {
        var worst = "ok";
        foreach (var l in levels)
        {
            if (l == "danger") return "danger";
            if (l == "warn") worst = "warn";
        }
        return worst;
    }
}

// ─── Wire-контракт ответа GET /api/local-model/hardware (camelCase, null опускается там, где опционально) ───

public sealed record HardwareReport(
    long At, string Status, long? StaleSince, string Level,
    HardwareGpuDto[] Gpus, HardwareNodeDto[] Nodes, HardwareFanDto[] CaseFans,
    HardwareSeriesDto[] Series, long StepMs);

public sealed record HardwareThrottleDto(bool PowerCap, bool Thermal);
public sealed record HardwarePcieDto(int? Gen, int? Width);
public sealed record HardwareGpuLevelsDto(string Mem, string Core, string Hotspot);

public sealed record HardwareGpuDto(
    int Index, string Uuid, string Name, string[] Roles,
    int? UtilPerc, long? MemUsedMb, long? MemTotalMb, int? MemBusPerc,
    int? CoreTempC, int? MemTempC, int? HotspotTempC, int? FanPerc,
    int? ClockMhz, int? ClockMaxMhz, int? MemClockMhz, double? PowerW, double? PowerLimitW,
    HardwareThrottleDto Throttle, string? Pstate, HardwarePcieDto Pcie, string BusId,
    string Level, HardwareGpuLevelsDto Levels);

public sealed record HardwareNodeDetailDto
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public int[]? CcdC { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public int? FanRpm { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Role { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public int? Temp2C { get; init; }
    // Предел диска; null — не сообщает (пороги 70/80). Поле пишется всегда, в т.ч. null.
    public int? CritC { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public long? UsedGb { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public long? TotalGb { get; init; }
}

public sealed record HardwareNodeDto(
    string Key, string Kind, string Name, int? TempC, string Sensor, string Level,
    int? WarnC, int? DangerC, HardwareNodeDetailDto Detail, int?[] Spark15m);

public sealed record HardwareFanDto(string Label, int Rpm);

public sealed record HardwareSeriesDto(
    string Key, string Family,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? GpuIndex,
    string Label, int?[] Points);

// Краткая сводка для строки-ссылки и точки на вкладке (добавляется в stats и stats/media ТОЛЬКО админу).
public sealed record HardwareBriefGpu(string Uuid, int? MemTempC, string Level, bool PowerCap, bool Thermal);
public sealed record HardwareBrief(string Level, HardwareBriefGpu[] Gpus);

// Сервис отчёта «Железо»: уровни считаются здесь (бэкенд), а не на фронте.
public interface IHardwareReportService
{
    HardwareReport Build(TimeSpan window);
    HardwareBrief Brief();
}

public sealed class HardwareReportService(IHardwareSampler sampler) : IHardwareReportService
{
    private static readonly TimeSpan SparkWindow = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan SparkStep = TimeSpan.FromSeconds(10);

    // Шаг бакета по окну: 1m → 2 с, 5m → 5 с, 15m → 10 с, 1h → 30 с.
    public static TimeSpan StepFor(TimeSpan window) =>
        window <= TimeSpan.FromMinutes(1) ? TimeSpan.FromSeconds(2)
        : window <= TimeSpan.FromMinutes(5) ? TimeSpan.FromSeconds(5)
        : window <= TimeSpan.FromMinutes(15) ? TimeSpan.FromSeconds(10)
        : TimeSpan.FromSeconds(30);

    public HardwareReport Build(TimeSpan window)
    {
        var cur = sampler.Current;
        var status = sampler.Status;
        var stale = status == "stale";
        var step = StepFor(window);

        var gpus = BuildGpus(cur, stale);
        var nodes = BuildNodes(cur, gpus, stale);

        // spark15m и series — max по бакетам из кольца.
        nodes = nodes.Select(n => n with { Spark15m = SparkFor(n.Key, cur) }).ToArray();
        var series = BuildSeries(cur, window, step);

        var fans = cur?.CaseFans?.Select(f => new HardwareFanDto(f.Label, f.Rpm)).ToArray() ?? [];
        var level = stale ? "ok" : HardwareThresholds.Worst(nodes.Select(n => n.Level));
        return new HardwareReport(
            (cur?.At ?? DateTimeOffset.UtcNow).ToUnixTimeMilliseconds(), status, sampler.StaleSince, level,
            gpus.Select(x => x.Dto).ToArray(), nodes, fans, series, (long)step.TotalMilliseconds);
    }

    public HardwareBrief Brief()
    {
        var cur = sampler.Current;
        var stale = sampler.Status == "stale";
        var gpus = BuildGpus(cur, stale);
        var nodes = BuildNodes(cur, gpus, stale);
        var level = stale ? "ok" : HardwareThresholds.Worst(nodes.Select(n => n.Level));
        var briefGpus = gpus.Select(g => new HardwareBriefGpu(
            g.Dto.Uuid, g.Dto.MemTempC, g.Dto.Level, g.Dto.Throttle.PowerCap, g.Dto.Throttle.Thermal)).ToArray();
        return new HardwareBrief(level, briefGpus);
    }

    // ─── Карты ──────────────────────────────────────────────────────────────────

    private sealed record GpuView(HardwareGpuDto Dto, int? ShownTemp, (int? Warn, int Danger) ShownThresholds);

    private static List<GpuView> BuildGpus(HardwareSample? cur, bool stale)
    {
        var result = new List<GpuView>();
        foreach (var g in cur?.Gpus ?? [])
        {
            var lm = HardwareThresholds.Level(g.MemTempC, HardwareThresholds.GpuMem);
            var lc = HardwareThresholds.Level(g.CoreTempC, HardwareThresholds.GpuCore);
            var lh = HardwareThresholds.Level(g.HotspotTempC, HardwareThresholds.GpuHotspot);
            var level = stale ? "ok" : HardwareThresholds.Worst([lm, lc, lh]);
            var dto = new HardwareGpuDto(
                g.Index, g.Uuid, g.Name, ["text"],
                g.Utilization, g.MemoryUsed, g.MemoryTotal, g.UtilizationMemory,
                g.CoreTempC, g.MemTempC, g.HotspotTempC, g.FanSpeed,
                g.ClocksSm, g.ClocksMaxSm, g.ClocksMem, g.PowerDraw, g.PowerLimit,
                new HardwareThrottleDto(g.SwPowerCap == true, g.HwThermalSlowdown == true || g.SwThermalSlowdown == true),
                g.Pstate, new HardwarePcieDto(g.PcieGen, g.PcieWidth), g.BusId,
                level, new HardwareGpuLevelsDto(lm, lc, lh));
            // Значение плитки: память; нет — ядро.
            var useMem = g.MemTempC is not null;
            result.Add(new GpuView(dto, useMem ? g.MemTempC : g.CoreTempC,
                useMem ? HardwareThresholds.GpuMem : HardwareThresholds.GpuCore));
        }
        return result;
    }

    // ─── Узлы (порядок фиксирован: карты, CPU, VRM, чипсет, диски, RAM) ──────────

    private static HardwareNodeDto[] BuildNodes(HardwareSample? cur, List<GpuView> gpus, bool stale)
    {
        var nodes = new List<HardwareNodeDto>();
        var empty = Array.Empty<int?>();
        string Lvl(int? v, (int? Warn, int Danger) t) => stale ? (v is null ? "none" : "ok") : HardwareThresholds.Level(v, t);

        foreach (var g in gpus)
        {
            nodes.Add(new HardwareNodeDto(
                $"gpu{g.Dto.Index}", "gpu", $"Карта {g.Dto.Index + 1}", g.ShownTemp,
                g.ShownTemp is null ? "nodata" : "ok", g.Dto.Level,
                g.ShownThresholds.Warn, g.ShownThresholds.Danger, new HardwareNodeDetailDto(), empty));
        }

        if (cur?.Cpu is { } cpu)
        {
            var t = HardwareThresholds.Cpu;
            nodes.Add(new HardwareNodeDto("cpu", "cpu", "Процессор", cpu.Tctl,
                cpu.Tctl is null ? "nodata" : "ok", Lvl(cpu.Tctl, t), t.Warn, t.Danger,
                new HardwareNodeDetailDto { CcdC = new[] { cpu.Tccd1, cpu.Tccd2 }.Where(x => x is not null).Select(x => x!.Value).ToArray() },
                empty));
        }

        // Плата есть, если hwmon asusec присутствовал (VRM/чипсет/вентилятор — хоть что-то прочитано).
        if (cur is { } c && (c.Vrm is not null || c.Chipset is not null))
        {
            var tv = HardwareThresholds.Vrm;
            nodes.Add(new HardwareNodeDto("vrm", "vrm", "Питание процессора", c.Vrm,
                c.Vrm is null ? "nodata" : "ok", Lvl(c.Vrm, tv), tv.Warn, tv.Danger, new HardwareNodeDetailDto(), empty));
            var tc = HardwareThresholds.Chipset;
            nodes.Add(new HardwareNodeDto("chipset", "chipset", "Чипсет платы", c.Chipset,
                c.Chipset is null ? "nodata" : "ok", Lvl(c.Chipset, tc), tc.Warn, tc.Danger,
                new HardwareNodeDetailDto { FanRpm = c.ChipsetFanRpm }, empty));
        }

        // Диски: сначала кэша (kv), затем системные.
        foreach (var d in (cur?.Disks ?? []).OrderBy(x => x.Role == "kv" ? 0 : 1).ThenBy(x => x.Controller))
        {
            var t = HardwareThresholds.Disk(d.CritC);
            nodes.Add(new HardwareNodeDto(
                $"disk-{d.Controller}", "disk", d.Role == "kv" ? "Диск кэша" : "Системный диск", d.TempC,
                d.TempC is null ? "nodata" : "ok", Lvl(d.TempC, t), t.Warn, t.Danger,
                new HardwareNodeDetailDto
                {
                    Role = d.Role, Temp2C = d.Temp2C, CritC = d.CritC,
                    UsedGb = d.UsedGb, TotalGb = d.TotalGb,
                }, empty));
        }

        if (cur?.RamTotalGb is not null)
        {
            nodes.Add(new HardwareNodeDto("ram", "ram", "Оперативная память", null, "none", "none", null, null,
                new HardwareNodeDetailDto { UsedGb = cur.RamUsedGb, TotalGb = cur.RamTotalGb }, empty));
        }
        return nodes.ToArray();
    }

    // ─── Ряды ───────────────────────────────────────────────────────────────────

    // Селектор значения узла по ключу (для spark15m).
    private static Func<HardwareSample, int?>? SelectorFor(string key)
    {
        if (key.StartsWith("gpu") && int.TryParse(key[3..], out var gi))
            return s => Gpu(s, gi) is { } g ? g.MemTempC ?? g.CoreTempC : null;
        return key switch
        {
            "cpu" => s => s.Cpu?.Tctl,
            "vrm" => s => s.Vrm,
            "chipset" => s => s.Chipset,
            _ when key.StartsWith("disk-") => s => s.Disks?.FirstOrDefault(d => "disk-" + d.Controller == key)?.TempC,
            _ => null,
        };
    }

    private static HardwareGpuSample? Gpu(HardwareSample s, int index) => s.Gpus?.FirstOrDefault(g => g.Index == index);

    private int?[] SparkFor(string key, HardwareSample? cur)
    {
        var sel = SelectorFor(key);
        if (sel is null) return [];
        return sampler.Aggregate(sel, SparkWindow, SparkStep).Select(p => p.value).ToArray();
    }

    private HardwareSeriesDto[] BuildSeries(HardwareSample? cur, TimeSpan window, TimeSpan step)
    {
        var list = new List<HardwareSeriesDto>();
        int?[] Pts(Func<HardwareSample, int?> sel) => sampler.Aggregate(sel, window, step).Select(p => p.value).ToArray();

        foreach (var g in cur?.Gpus ?? [])
        {
            var i = g.Index;
            var n = i + 1;
            list.Add(new HardwareSeriesDto($"mem-{i}", "mem", i, $"Память · карта {n}", Pts(s => Gpu(s, i)?.MemTempC)));
            list.Add(new HardwareSeriesDto($"core-{i}", "core", i, $"Ядро · карта {n}", Pts(s => Gpu(s, i)?.CoreTempC)));
            list.Add(new HardwareSeriesDto($"hotspot-{i}", "hotspot", i, $"Горячая точка · карта {n}", Pts(s => Gpu(s, i)?.HotspotTempC)));
        }
        if (cur?.Cpu is not null)
            list.Add(new HardwareSeriesDto("cpu", "cpu", null, "Процессор", Pts(s => s.Cpu?.Tctl)));
        if (cur?.Chipset is not null)
            list.Add(new HardwareSeriesDto("chipset", "chipset", null, "Чипсет", Pts(s => s.Chipset)));
        foreach (var d in (cur?.Disks ?? []).OrderBy(x => x.Role == "kv" ? 0 : 1).ThenBy(x => x.Controller))
        {
            var key = "disk-" + d.Controller;
            list.Add(new HardwareSeriesDto(key, "disk", null, d.Role == "kv" ? "Диск кэша" : "Системный диск",
                Pts(s => s.Disks?.FirstOrDefault(x => x.Controller == d.Controller)?.TempC)));
        }
        return list.ToArray();
    }
}
