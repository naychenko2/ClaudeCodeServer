using System.Text.Json.Serialization;
using Microsoft.Extensions.Configuration;

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
    // Плата (System Temp / Peripheral Temp): пороги BMC worker — Upper Non-Critical 80, Upper Critical 85
    // (ipmitool sensor get, 16.11); у asusec своих порогов нет, берём те же.
    public static readonly (int? Warn, int Danger) Board = (80, 85);
    // Заполненность диска, %: предупреждение с 85, опасность с 95 (предложение макета v3, допущение 4).
    public static readonly (int? Warn, int Danger) DiskFill = (85, 95);
    // Вода СЖО: обычно 44–45 °C; предел акрила и ПВХ — около 60 °C.
    public static readonly (int? Warn, int Danger) Water = (50, 55);
    // Диск без известного предела.
    public static readonly (int? Warn, int Danger) DiskNoLimit = (70, 80);
    // Нагрузка ИБП, %: свыше 80 запас на пусковые токи мал, с 95 ИБП близок к перегрузке.
    public static readonly (int? Warn, int Danger) UpsLoad = (80, 95);
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

// Node/UptimeS/CpuLoadPerc — узел, к которому относится отчёт (Ш3a). Значения по умолчанию держат
// пустой отчёт контроллера (ветка без Llm), где узла нет вовсе.
// CpuLoadSpark15m — история загрузки CPU крупной KPI узла: та же агрегация, что spark15m плиток
// (окно 15 мин, 90 бакетов по 10 с, максимум в бакете, null — разрыв); null — поле не пишется в JSON.
public sealed record HardwareReport(
    long At, string Status, long? StaleSince, string Level,
    HardwareGpuDto[] Gpus, HardwareNodeDto[] Nodes, HardwareFanDto[] CaseFans,
    HardwareSeriesDto[] Series, long StepMs,
    HardwareNodeInfoDto? Node = null, long? UptimeS = null, int? CpuLoadPerc = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int?[]? CpuLoadSpark15m = null);

public sealed record HardwareNodeInfoDto(string Key, string Title);

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
    // Peripheral Temp платы (только IPMI); null — нет значения (ключа в JSON нет).
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public int? PeripheralC { get; init; }
    // Предел диска; null — не сообщает (пороги 70/80). Поле пишется всегда, в т.ч. null.
    public int? CritC { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public long? UsedGb { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public long? TotalGb { get; init; }
    // Занято, % (RAM и диск кэша: плитка показывает его вместо температуры, spark15m — тоже %).
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public int? UsedPerc { get; init; }
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
public sealed record HardwareBriefNode(string Key, string Level);
public sealed record HardwareBrief(string Level, HardwareBriefGpu[] Gpus, HardwareBriefNode[] Nodes);

// Сервис отчёта «Железо»: уровни считаются здесь (бэкенд), а не на фронте.
public interface IHardwareReportService
{
    // Отчёт одного узла по Key из реестра HardwareNodes; узла в реестре нет — null (ручка и её 404 — Ш3b).
    HardwareReport? Build(string node, TimeSpan window);

    // Старый контракт ручки — узел «worker» (он же при отсутствии реестра).
    HardwareReport? Build(TimeSpan window);

    HardwareBrief Brief();

    // Сводка всех узлов фермы (карточка «Ферма», макет А).
    HardwareFarmReport Farm();
}

public sealed class HardwareReportService(IHardwareSampler sampler, IConfiguration configuration, IUpsSampler? ups = null) : IHardwareReportService
{
    private static readonly TimeSpan SparkWindow = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan SparkStep = TimeSpan.FromSeconds(10);

    // Узлы — тот же реестр, по которому сэмплер строит свои видящие (IConfiguration — единственный
    // источник, ADR-029 «Реестр HardwareNodes»). Без секции EffectiveNodes даёт ровно один «worker».
    private readonly NodeRegistry _reg = ReadRegistry(configuration);

    private sealed record NodeRegistry(IReadOnlyList<HardwareNodeDef> Nodes, bool NoSection);

    private static NodeRegistry ReadRegistry(IConfiguration configuration)
    {
        var options = HardwareNodesOptions.FromConfiguration(configuration);
        return new NodeRegistry(options.EffectiveNodes(), options.Nodes.Count == 0);
    }

    // Шаг бакета по окну: 1m → 2 с, 5m → 5 с, 15m → 10 с, 1h → 30 с.
    public static TimeSpan StepFor(TimeSpan window) =>
        window <= TimeSpan.FromMinutes(1) ? TimeSpan.FromSeconds(2)
        : window <= TimeSpan.FromMinutes(5) ? TimeSpan.FromSeconds(5)
        : window <= TimeSpan.FromMinutes(15) ? TimeSpan.FromSeconds(10)
        : TimeSpan.FromSeconds(30);

    // Узел по Key из реестра; в реестре его нет — null (неизвестный узел, а не пустой отчёт).
    private HardwareNodeDef? DefFor(string key) => _reg.Nodes.FirstOrDefault(n => n.Key == key);

    // Данные всех узлов реестра — в порядке конфига (порядок фермы и переключателя узлов).
    private List<NodeData> AllNodes()
    {
        var list = new List<NodeData>();
        foreach (var def in _reg.Nodes)
            if (DataFor(def.Key) is { } d) list.Add(d);
        return list;
    }

    // Подпись узла: Title, при пустом Title — Key (ADR-029 «Реестр HardwareNodes»).
    private static string TitleOf(HardwareNodeDef def) => string.IsNullOrEmpty(def.Title) ? def.Key : def.Title;

    // Роли карт узла — из реестра: Engines непуст → «text», Media → «media», оба — обе (ADR-029).
    // Реестра нет (фолбэк-узел worker) → роль одна, текстовая: побайтно сегодняшнее поведение
    // (тот же раздел ADR, «Без секции»).
    private string[] RolesFor(HardwareNodeDef def)
    {
        var roles = new List<string>();
        if (def.Engines.Count > 0 || _reg.NoSection) roles.Add("text");
        if (def.Media) roles.Add("media");
        return [.. roles];
    }

    // Статус отчёта остаётся «ok»|«stale»: down/incompatible узла для ручки — тот же «не отвечает».
    private static string ReportStatus(INodeHardwareView? v) => v is null || v.Status == "ok" ? "ok" : "stale";

    private static HwmonCpu? Socket(HardwareSample? s, int i) => s?.Cpus is { } a && i < a.Length ? a[i] : null;
    private static int? VrmAt(HardwareSample? s, int i) => s?.Vrms is { } a && i < a.Length ? a[i] : null;

    // Плитка сокета: один сокет — побайтно сегодняшние ключи «cpu»/«vrm» и имена «Процессор»/
    // «Питание процессора»; два и больше — «cpu-0»/«vrm-1», «Процессор 1»/«Питание процессора 2» (ADR-029).
    private static string CpuKey(int i, bool multi) => multi ? $"cpu-{i}" : "cpu";
    private static string CpuName(int i, bool multi) => multi ? $"Процессор {i + 1}" : "Процессор";
    private static string VrmKey(int i, bool multi) => multi ? $"vrm-{i}" : "vrm";
    private static string VrmName(int i, bool multi) => multi ? $"Питание процессора {i + 1}" : "Питание процессора";

    // Всё об одном узле, общее для Build/Brief/Farm: снимок, нештатность, карты, плитки и уровень узла
    // (уровень — худший из плиток; при stale/down/incompatible не красится).
    private sealed record NodeData(HardwareNodeDef Def, INodeHardwareView? View, HardwareSample? Cur,
        bool NotOk, string Level, List<GpuView> Gpus, HardwareNodeDto[] Tiles);

    private NodeData? DataFor(string key)
    {
        var def = DefFor(key);
        if (def is null) return null;
        var view = sampler.ForNode(key);
        var cur = view?.Current;
        var notOk = view is not null && view.Status != "ok";
        var gpus = BuildGpus(cur, def, notOk);
        var tiles = BuildNodes(cur, gpus, notOk);
        var level = notOk ? "ok" : HardwareThresholds.Worst(tiles.Select(t => t.Level));
        return new NodeData(def, view, cur, notOk, level, gpus, tiles);
    }

    public HardwareReport? Build(string node, TimeSpan window)
    {
        var d = DataFor(node);
        if (d is null) return null;
        var status = ReportStatus(d.View);
        var step = StepFor(window);

        // spark15m и series — max по бакетам из кольца.
        var tiles = d.Tiles.Select(t => t with { Spark15m = SparkFor(t.Key, d.View) }).ToArray();
        var series = BuildSeries(d.View, d.Cur, window, step);

        var fans = d.Cur?.CaseFans?.Select(f => new HardwareFanDto(f.Label, f.Rpm)).ToArray() ?? [];
        return new HardwareReport(
            (d.Cur?.At ?? DateTimeOffset.UtcNow).ToUnixTimeMilliseconds(), status, status == "stale" ? d.View!.Since : null, d.Level,
            d.Gpus.Select(x => x.Dto).ToArray(), tiles, fans, series, (long)step.TotalMilliseconds,
            new HardwareNodeInfoDto(d.Def.Key, TitleOf(d.Def)), d.Cur?.UptimeS, d.Cur?.CpuLoadPerc,
            // История загрузки CPU — та же агрегация кольца, что spark15m плиток (макс в бакете, null — разрыв).
            d.View?.Aggregate(s => s.CpuLoadPerc, SparkWindow, SparkStep).Select(p => p.value).ToArray() ?? []);
    }

    public HardwareReport? Build(TimeSpan window) => Build("worker", window);

    public HardwareBrief Brief()
    {
        var all = AllNodes();
        // Карты сводки — локального узла: их uuid связываются с карточками движков этой же вкладки,
        // чужие uuid там не о чём. Уровень — худший по узлам фермы.
        var gpus = (all.FirstOrDefault(d => d.Def.Key == "worker") ?? all.FirstOrDefault())?.Gpus
            .Select(g => new HardwareBriefGpu(g.Dto.Uuid, g.Dto.MemTempC, g.Dto.Level, g.Dto.Throttle.PowerCap, g.Dto.Throttle.Thermal)).ToArray() ?? [];
        return new HardwareBrief(
            HardwareThresholds.Worst(all.Select(d => d.Level).Append(Ups()?.Level ?? "ok")), gpus,
            [.. all.Select(d => new HardwareBriefNode(d.Def.Key, d.Level))]);
    }

    // ИБП фермы; null — не настроен. Нештатный (stale/down) не красится, как молчащий узел.
    // Уровень: от батареи — warn, батарея садится — danger; нагрузка — по порогу UpsLoad.
    private HardwareUpsDto? Ups()
    {
        if (ups is not { Enabled: true }) return null;
        var status = ups.Status;
        var cur = ups.Current;
        var flags = cur?.Status?.Split(' ', StringSplitOptions.RemoveEmptyEntries) ?? [];
        string? power = flags.Contains("LB") ? "lowBattery"
            : flags.Contains("OB") ? "battery"
            : flags.Contains("OL") ? "line"
            : null;
        var ok = status == "ok";
        var powerLevel = !ok ? "ok" : power switch { "lowBattery" => "danger", "battery" => "warn", null => "none", _ => "ok" };
        var loadLevel = !ok ? "ok" : HardwareThresholds.Level(cur?.LoadPerc, HardwareThresholds.UpsLoad);
        var level = HardwareThresholds.Worst([powerLevel, loadLevel]);
        var loadW = cur?.LoadPerc is { } lp && ups.Options.RatedW is { } rw ? (int?)Math.Round(lp * rw / 100.0) : null;
        int?[] Spark(Func<UpsSample, int?> sel) => [.. ups.Aggregate(sel, SparkWindow, SparkStep)];
        return new HardwareUpsDto(
            ups.Options.Title, status, ups.Since, level, power, powerLevel, loadLevel,
            cur?.LoadPerc, loadW, cur?.ChargePerc, cur?.BatteryV, cur?.BatteryNominalV,
            cur?.InputV, cur?.InputHz, cur?.OutputV, cur?.TempC,
            Spark(s => s.LoadPerc), Spark(s => s.ChargePerc),
            Spark(s => s.InputV is { } v ? (int)Math.Round(v) : null),
            Spark(s => s.BatteryV is { } v ? (int)Math.Round(v * 10) : null));
    }

    public HardwareFarmReport Farm()
    {
        var nodes = AllNodes().Select(d => new HardwareFarmNodeDto(
            d.Def.Key, TitleOf(d.Def), RolesFor(d.Def), [.. d.Def.Engines], d.Def.Media,
            // Видящего узла нет (реестр и сэмплер разошлись) — узел молчит, как недоступный агент.
            d.View?.Status ?? "down", d.View?.Since, d.Level, Hottest(d),
            [.. d.Gpus.Select(g => new HardwareBriefGpu(g.Dto.Uuid, g.Dto.MemTempC, g.Dto.Level, g.Dto.Throttle.PowerCap, g.Dto.Throttle.Thermal))],
            d.Cur?.UptimeS, d.Cur?.CpuLoadPerc, Perc(d.Cur?.RamUsedGb, d.Cur?.RamTotalGb))).ToArray();
        var upsDto = Ups();
        return new HardwareFarmReport(
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            HardwareThresholds.Worst(nodes.Select(n => n.Level).Append(upsDto?.Level ?? "ok")), nodes, upsDto);
    }

    // «Горячо» узла — самый горячий измеренный показатель (ряды температуры плиток; заполненность %
    // не температура). Уровень — тот же, что у плитки (при stale/down он «ok»: красится только измеренное).
    private static HardwareFarmHottestDto? Hottest(NodeData d) =>
        d.Tiles.Where(t => t.TempC is not null).MaxBy(t => t.TempC!.Value) is { } t
            ? new HardwareFarmHottestDto(t.Name, t.TempC, t.Level)
            : null;

    // ─── Карты ──────────────────────────────────────────────────────────────────

    private sealed record GpuView(HardwareGpuDto Dto, int? ShownTemp, (int? Warn, int Danger) ShownThresholds);

    private List<GpuView> BuildGpus(HardwareSample? cur, HardwareNodeDef def, bool notOk)
    {
        var roles = RolesFor(def);
        var result = new List<GpuView>();
        foreach (var g in cur?.Gpus ?? [])
        {
            var lm = HardwareThresholds.Level(g.MemTempC, HardwareThresholds.GpuMem);
            var lc = HardwareThresholds.Level(g.CoreTempC, HardwareThresholds.GpuCore);
            var lh = HardwareThresholds.Level(g.HotspotTempC, HardwareThresholds.GpuHotspot);
            var level = notOk ? "ok" : HardwareThresholds.Worst([lm, lc, lh]);
            var dto = new HardwareGpuDto(
                g.Index, g.Uuid, g.Name, roles,
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

    // ─── Узлы (порядок фиксирован: карты, сокеты CPU, VRM сокетов, VRM платы, плата, чипсет,
    //          вода, диски, RAM) ──────────────────────────────────────────────────────

    private static HardwareNodeDto[] BuildNodes(HardwareSample? cur, List<GpuView> gpus, bool notOk)
    {
        var nodes = new List<HardwareNodeDto>();
        var empty = Array.Empty<int?>();
        string Lvl(int? v, (int? Warn, int Danger) t) => notOk ? (v is null ? "none" : "ok") : HardwareThresholds.Level(v, t);

        foreach (var g in gpus)
        {
            nodes.Add(new HardwareNodeDto(
                $"gpu{g.Dto.Index}", "gpu", $"Карта {g.Dto.Index + 1}", g.ShownTemp,
                g.ShownTemp is null ? "nodata" : "ok", g.Dto.Level,
                g.ShownThresholds.Warn, g.ShownThresholds.Danger, new HardwareNodeDetailDto(), empty));
        }

        var cpus = cur?.Cpus ?? [];
        var multi = cpus.Length > 1;
        for (var i = 0; i < cpus.Length; i++)
        {
            var cpu = cpus[i];
            var t = HardwareThresholds.Cpu;
            nodes.Add(new HardwareNodeDto(CpuKey(i, multi), "cpu", CpuName(i, multi), cpu.Tctl,
                cpu.Tctl is null ? "nodata" : "ok", Lvl(cpu.Tctl, t), t.Warn, t.Danger,
                new HardwareNodeDetailDto { CcdC = cpu.Ccd.Where(x => x is not null).Select(x => x!.Value).ToArray() },
                empty));
        }

        // VRM — по одному на сокет. Пустого значения нет нигде — плитку не заводим; исключение — один
        // сокет с читавшейся платой asusec: сегодняшняя плитка «vrm» (sensor «nodata») остаётся.
        var vrms = cur?.Vrms ?? [];
        for (var i = 0; i < vrms.Length; i++)
        {
            if (vrms[i] is null && !(i == 0 && !multi && cur?.Chipset is not null)) continue;
            var tv = HardwareThresholds.Vrm;
            nodes.Add(new HardwareNodeDto(VrmKey(i, multi), "vrm", VrmName(i, multi), vrms[i],
                vrms[i] is null ? "nodata" : "ok", Lvl(vrms[i], tv), tv.Warn, tv.Danger, new HardwareNodeDetailDto(), empty));
        }

        // Неразведённый VRM — общий датчик платы при двух сокетах и больше (ADR-029 «Модель и ответ»).
        if (cur?.BoardVrm is { } boardVrm)
        {
            var tv = HardwareThresholds.Vrm;
            nodes.Add(new HardwareNodeDto("vrm-board", "vrm", "Питание (плата)", boardVrm, "ok",
                Lvl(boardVrm, tv), tv.Warn, tv.Danger, new HardwareNodeDetailDto(), empty));
        }

        // Плитка «Плата» есть у каждого узла со снимком всегда: значение — BoardTempC (hwmon Motherboard
        // или IPMI System Temp), порог 80/85. Значения нет: BoardProbe — «нет данных» (датчик есть,
        // не ответил), иначе «датчика нет» (video). Ни то, ни другое тревогой не считается.
        if (cur is { } b)
        {
            var tb = HardwareThresholds.Board;
            nodes.Add(new HardwareNodeDto("board", "board", "Плата", b.BoardTempC,
                b.BoardTempC is not null ? "ok" : b.BoardProbe ? "nodata" : "none",
                Lvl(b.BoardTempC, tb), tb.Warn, tb.Danger,
                new HardwareNodeDetailDto { PeripheralC = b.PeripheralTempC }, empty));
        }

        if (cur is { } c && c.Chipset is not null)
        {
            var tc = HardwareThresholds.Chipset;
            nodes.Add(new HardwareNodeDto("chipset", "chipset", "Чипсет платы", c.Chipset,
                "ok", Lvl(c.Chipset, tc), tc.Warn, tc.Danger,
                new HardwareNodeDetailDto { FanRpm = c.ChipsetFanRpm }, empty));
        }

        if (cur?.Water is { } water)
        {
            var tw = HardwareThresholds.Water;
            nodes.Add(new HardwareNodeDto("water", "water", "Вода СЖО", water,
                "ok", Lvl(water, tw), tw.Warn, tw.Danger, new HardwareNodeDetailDto(), empty));
        }

        // Диски: сначала кэша (kv), затем системные.
        foreach (var d in (cur?.Disks ?? []).OrderBy(x => x.Role == "kv" ? 0 : 1).ThenBy(x => x.Controller))
        {
            // У диска кэша датчики врут (Sensor 2 на 25 °C выше Composite) — только заполненность:
            // её уровень (пороги 85/95 %) и есть уровень плитки (температуры нет). Пороги здесь не
            // отдаются: warnC/dangerC — только температурные, в °C.
            if (d.Role == "kv")
            {
                var perc = Perc(d.UsedGb, d.TotalGb);
                nodes.Add(new HardwareNodeDto($"disk-{d.Controller}", "disk", "Диск кэша", null, "none",
                    perc is null ? "none" : Lvl(perc, HardwareThresholds.DiskFill), null, null,
                    new HardwareNodeDetailDto { Role = d.Role, UsedGb = d.UsedGb, TotalGb = d.TotalGb, UsedPerc = perc }, empty));
                continue;
            }
            var t = HardwareThresholds.Disk(d.CritC);
            nodes.Add(new HardwareNodeDto(
                $"disk-{d.Controller}", "disk", "Системный диск", d.TempC,
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
                new HardwareNodeDetailDto { UsedGb = cur.RamUsedGb, TotalGb = cur.RamTotalGb, UsedPerc = Perc(cur.RamUsedGb, cur.RamTotalGb) }, empty));
        }
        return nodes.ToArray();
    }

    // ─── Ряды ───────────────────────────────────────────────────────────────────

    private static int? Perc(long? used, long? total) =>
        used is { } u && total is { } t && t > 0 ? (int)Math.Round(100.0 * u / t) : null;

    // Селектор значения узла по ключу (для spark15m): температура; у RAM и диска кэша — занято, %.
    private static Func<HardwareSample, int?>? SelectorFor(string key)
    {
        if (key.StartsWith("gpu") && int.TryParse(key[3..], out var gi))
            return s => Gpu(s, gi) is { } g ? g.MemTempC ?? g.CoreTempC : null;
        // Индекс в ключе — тот же, что у Cpus[]/Vrms[] (плитка и ряд одного сокета).
        if (key.StartsWith("cpu-") && int.TryParse(key[4..], out var ci))
            return s => Socket(s, ci)?.Tctl;
        if (key.StartsWith("vrm-") && int.TryParse(key[4..], out var vi))
            return s => VrmAt(s, vi);
        return key switch
        {
            "cpu" => s => Socket(s, 0)?.Tctl,
            "vrm" => s => VrmAt(s, 0),
            "vrm-board" => s => s.BoardVrm,
            "board" => s => s.BoardTempC,
            "chipset" => s => s.Chipset,
            "water" => s => s.Water,
            "ram" => s => Perc(s.RamUsedGb, s.RamTotalGb),
            _ when key.StartsWith("disk-") => s => s.Disks?.FirstOrDefault(d => "disk-" + d.Controller == key) is { } d
                ? (d.Role == "kv" ? Perc(d.UsedGb, d.TotalGb) : d.TempC)
                : null,
            _ => null,
        };
    }

    private static HardwareGpuSample? Gpu(HardwareSample s, int index) => s.Gpus?.FirstOrDefault(g => g.Index == index);

    private static int?[] SparkFor(string key, INodeHardwareView? view)
    {
        var sel = SelectorFor(key);
        if (sel is null) return [];
        return view?.Aggregate(sel, SparkWindow, SparkStep).Select(p => p.value).ToArray() ?? [];
    }

    private static HardwareSeriesDto[] BuildSeries(INodeHardwareView? view, HardwareSample? cur, TimeSpan window, TimeSpan step)
    {
        var list = new List<HardwareSeriesDto>();
        int?[] Pts(Func<HardwareSample, int?> sel) => view?.Aggregate(sel, window, step).Select(p => p.value).ToArray() ?? [];

        foreach (var g in cur?.Gpus ?? [])
        {
            var i = g.Index;
            var n = i + 1;
            list.Add(new HardwareSeriesDto($"mem-{i}", "mem", i, $"Память · карта {n}", Pts(s => Gpu(s, i)?.MemTempC)));
            list.Add(new HardwareSeriesDto($"core-{i}", "core", i, $"Ядро · карта {n}", Pts(s => Gpu(s, i)?.CoreTempC)));
            list.Add(new HardwareSeriesDto($"hotspot-{i}", "hotspot", i, $"Горячая точка · карта {n}", Pts(s => Gpu(s, i)?.HotspotTempC)));
        }
        var cpus = cur?.Cpus ?? [];
        var multi = cpus.Length > 1;
        for (var i = 0; i < cpus.Length; i++)
            list.Add(new HardwareSeriesDto(CpuKey(i, multi), "cpu", null, CpuName(i, multi), Pts(s => Socket(s, i)?.Tctl)));
        // Ряд платы — только если значение есть в текущем снимке (как у чипсета). Семейство board:
        // под ним фронт (Ф3) заводит свою группу ряда.
        if (cur?.BoardTempC is not null)
            list.Add(new HardwareSeriesDto("board", "board", null, "Плата", Pts(s => s.BoardTempC)));
        if (cur?.Chipset is not null)
            list.Add(new HardwareSeriesDto("chipset", "chipset", null, "Чипсет", Pts(s => s.Chipset)));
        if (cur?.Water is not null)
            list.Add(new HardwareSeriesDto("water", "water", null, "Вода СЖО", Pts(s => s.Water)));
        foreach (var d in (cur?.Disks ?? []).Where(x => x.Role != "kv").OrderBy(x => x.Controller))
        {
            var key = "disk-" + d.Controller;
            list.Add(new HardwareSeriesDto(key, "disk", null, "Системный диск",
                Pts(s => s.Disks?.FirstOrDefault(x => x.Controller == d.Controller)?.TempC)));
        }
        return list.ToArray();
    }
}
