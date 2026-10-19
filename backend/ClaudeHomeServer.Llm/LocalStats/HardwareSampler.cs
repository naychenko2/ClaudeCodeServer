using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace ClaudeHomeServer.Services.Llm;

// Одна видеокарта в снимке «Железо»: данные nvidia-smi (ядро, вентилятор, частоты, PCIe,
// троттлинг) + память GDDR6X и hotspot из файла помощника ccs-gddr6 (связка по busId).
public sealed record HardwareGpuSample
{
    public int Index { get; init; }
    public string Uuid { get; init; } = "";
    public string Name { get; init; } = "";
    public string BusId { get; init; } = "";
    public int? Utilization { get; init; }
    public int? UtilizationMemory { get; init; }
    public long? MemoryUsed { get; init; }
    public long? MemoryTotal { get; init; }
    public int? CoreTempC { get; init; }
    // Память GDDR6X и hotspot — из файла помощника; null — датчик не ответил (sensor:'nodata').
    public int? MemTempC { get; init; }
    public int? HotspotTempC { get; init; }
    public int? FanSpeed { get; init; }
    public int? ClocksSm { get; init; }
    public int? ClocksMaxSm { get; init; }
    public int? ClocksMem { get; init; }
    public string? Pstate { get; init; }
    public int? PcieGen { get; init; }
    public int? PcieWidth { get; init; }
    public double? PowerDraw { get; init; }
    public double? PowerLimit { get; init; }
    public bool? SwPowerCap { get; init; }
    public bool? HwThermalSlowdown { get; init; }
    public bool? SwThermalSlowdown { get; init; }
}

// Снимок «Железо» за 2 с: карты, CPU, VRM, чипсет (+вентилятор), диски, вентиляторы, RAM.
// null-поля — датчика нет (недоверенные источники: nvidia-smi, файл помощника, hwmon, meminfo).
public sealed record HardwareSample
{
    public DateTimeOffset At { get; init; }
    public HardwareGpuSample[]? Gpus { get; init; }
    public HwmonCpu? Cpu { get; init; }
    public int? Vrm { get; init; }
    public int? Chipset { get; init; }
    public int? ChipsetFanRpm { get; init; }
    public HwmonDisk[]? Disks { get; init; }
    public HwmonFan[]? CaseFans { get; init; }
    public long? RamUsedGb { get; init; }
    public long? RamTotalGb { get; init; }
}

// Сэмплер «Железо» (подсистема Llm, рядом с GpuSampler): снимок раз в 2 с в фоне
// (BackgroundService), кольцо истории на 1 ч в памяти. Источники (все недоверенные):
//   • nvidia-smi (GpuSampler) — ядро, вентилятор, частоты, PCIe, троттлинг;
//   • файл помощника ccs-gddr6 (Gddr6HelperReader) — память GDDR6X и hotspot;
//   • /sys/class/hwmon (HwmonReader) — CPU, VRM, чипсет, диски, вентиляторы;
//   • /proc/meminfo (MeminfoReader) — RAM.
// Любой сбой разбора не выходит из BackgroundService (иначе упадёт весь хост): узел получает
// sensor:'nodata'. Нет hwmon/nvidia-smi (чужая машина) — пустые списки, ручка отдаёт status:'ok'
// и пустые узлы, не 500. Сэмплер ТОЛЬКО читает (кроме уже существующего вызова nvidia-smi).
public interface IHardwareSampler
{
    // Последний снимок; null — снимков ещё нет (сэмплер не снял ни одного).
    HardwareSample? Current { get; }

    // "ok" | "stale". stale — последний снимок старше StaleAfter (цикл не успевает).
    string Status { get; }

    // Момент ПЕРВОГО stale (unix-мс); null — не stale.
    long? StaleSince { get; }

    // Агрегация кольца в бакеты по step, МАКСИМУМ в бакете (пики важнее среднего);
    // null — нет данных в бакете (разрыв). t — unix-мс начала бакета.
    IReadOnlyList<(long t, int? value)> Aggregate(Func<HardwareSample, int?> selector, TimeSpan window, TimeSpan step);
}

public sealed class HardwareSampler : BackgroundService, IHardwareSampler
{
    // Шаг снимка: 2 с (кольцо 1 ч = 1800 снимков).
    public static readonly TimeSpan SampleInterval = TimeSpan.FromSeconds(2);

    // Кольцо: 1 ч.
    public static readonly TimeSpan HistoryWindow = TimeSpan.FromHours(1);

    // Последний снимок старше этого → статус "stale" (цикл не успевает / источник завис).
    public static readonly TimeSpan StaleAfter = TimeSpan.FromSeconds(10);

    // Число точек кольца: 1 ч / 2 с = 1800.
    private static int MaxSamples => (int)Math.Ceiling(HistoryWindow.TotalSeconds / SampleInterval.TotalSeconds);

    private readonly IOptionsMonitor<LocalModelStatsOptions> _options;
    private readonly IGpuSampler? _gpus;
    private readonly Gddr6HelperReader _gddr6;
    private readonly HwmonReader _hwmon;
    private readonly MeminfoReader _meminfo;
    private readonly TimeProvider _time;
    private readonly ILogger<HardwareSampler> _log;

    // Кольцо + текущий снимок: мутации только в ExecuteAsync (single-writer),
    // атомарная замена ссылки на чтение (Current/Status) — без блокировки.
    private readonly object _gate = new();
    private List<HardwareSample> _ring = [];
    private HardwareSample? _current;
    private DateTimeOffset? _lastSampleTime;

    // Ожидаемый интервал: по умолчанию Task.Delay; тест подменяет (гонит цикл на фейковых часах).
    internal Func<TimeSpan, CancellationToken, Task>? WaitFunc { get; set; }

    public HardwareSampler(
        IOptionsMonitor<LocalModelStatsOptions> options,
        IGpuSampler? gpus,
        Gddr6HelperReader gddr6,
        HwmonReader hwmon,
        MeminfoReader meminfo,
        TimeProvider time,
        ILogger<HardwareSampler>? log = null)
    {
        _options = options;
        _gpus = gpus;
        _gddr6 = gddr6;
        _hwmon = hwmon;
        _meminfo = meminfo;
        _time = time;
        _log = log ?? NullLogger<HardwareSampler>.Instance;
    }

    public HardwareSample? Current { get { lock (_gate) return _current; } }
    // stale считается в момент чтения: цикл мог зависнуть и не обновить снимок. Снимков ещё нет — "ok".
    public string Status { get { lock (_gate) return IsStaleLocked() ? "stale" : "ok"; } }
    // Момент последнего живого снимка (с него стенд «не отвечает»); null — не stale.
    public long? StaleSince { get { lock (_gate) return IsStaleLocked() ? _lastSampleTime!.Value.ToUnixTimeMilliseconds() : null; } }

    private bool IsStaleLocked() =>
        _lastSampleTime is { } last && _time.GetUtcNow() - last > StaleAfter;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    if (_options.CurrentValue.Enabled)
                        await PollOnceAsync(stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    // Недоверенные источники: любое исключение не роняет хост (BackgroundService).
                    _log.LogDebug(ex, "Сэмплер «Железо»: сбой снимка");
                }
                await WaitAsync(SampleInterval, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Нормальная остановка хоста.
        }
    }

    // Один снимок: собирает все источники (недоверенные), строит HardwareSample, кладёт в кольцо,
    // обновляет Current/Status/StaleSince. internal — тесты гоняют снимки напрямую, двигая часы.
    internal async Task<HardwareSample> PollOnceAsync(CancellationToken ct = default)
    {
        var now = _time.GetUtcNow();

        // 1) nvidia-smi (ядро, вентилятор, частоты, PCIe, троттлинг, busId).
        var gpuSamples = _gpus is { } smi ? await smi.ReadAsync(ct) : [];

        // 2) Файл помощника ccs-gddr6 (память GDDR6X, hotspot): связка по каноническому busId.
        //    Протухший (at > 10 с) или отсутствующий → mem/hotspot = null (sensor:'nodata').
        var gddr6Result = _gddr6.Read(_options.CurrentValue.Gddr6HelperFile);
        var gddr6Map = new Dictionary<string, Gddr6GpuTemp>();
        if (gddr6Result is { } gr && gr.fresh)
        {
            foreach (var g in gr.gpus)
                gddr6Map[GpuSampler.CanonicalBusId(g.BusId)] = g;
        }

        var gpus = new List<HardwareGpuSample>();
        foreach (var gs in gpuSamples)
        {
            var gddr6 = gddr6Map.TryGetValue(GpuSampler.CanonicalBusId(gs.BusId), out var t) ? t : null;
            gpus.Add(new HardwareGpuSample
            {
                Index = gs.Index,
                Uuid = gs.Uuid,
                Name = gs.Name,
                BusId = gs.BusId,
                Utilization = gs.Utilization,
                UtilizationMemory = gs.UtilizationMemory,
                MemoryUsed = gs.MemoryUsed,
                MemoryTotal = gs.MemoryTotal,
                CoreTempC = gs.Temperature,
                MemTempC = gddr6?.MemTempC,
                HotspotTempC = gddr6?.HotspotTempC,
                FanSpeed = gs.FanSpeed,
                ClocksSm = gs.ClocksSm,
                ClocksMaxSm = gs.ClocksMaxSm,
                ClocksMem = gs.ClocksMem,
                Pstate = gs.Pstate,
                PcieGen = gs.PcieGen,
                PcieWidth = gs.PcieWidth,
                PowerDraw = gs.PowerDraw,
                PowerLimit = gs.PowerLimit,
                SwPowerCap = gs.SwPowerCap,
                HwThermalSlowdown = gs.HwThermalSlowdown,
                SwThermalSlowdown = gs.SwThermalSlowdown,
            });
        }

        // 3) hwmon (CPU, VRM, чипсет, диски, вентиляторы) + 4) meminfo (RAM).
        var hw = _hwmon.Read();
        var mem = _meminfo.Read();

        var sample = new HardwareSample
        {
            At = now,
            Gpus = gpus.Count > 0 ? gpus.ToArray() : null,
            Cpu = hw?.Cpu,
            Vrm = hw?.Board?.Vrm,
            Chipset = hw?.Board?.Chipset,
            ChipsetFanRpm = hw?.Board?.ChipsetFanRpm,
            Disks = hw?.Disks,
            CaseFans = hw?.CaseFans,
            RamUsedGb = mem?.UsedGb,
            RamTotalGb = mem?.TotalGb,
        };

        lock (_gate)
        {
            _ring.Add(sample);
            while (_ring.Count > MaxSamples)
                _ring.RemoveAt(0);
            _current = sample;
            _lastSampleTime = now;
        }
        return sample;
    }

    // Агрегация кольца в бакеты по step, МАКСИМУМ в бакете; null — нет данных (разрыв).
    // t — unix-мс начала бакета. Бакеты идут от (now - window) до now, шаг step.
    public IReadOnlyList<(long t, int? value)> Aggregate(Func<HardwareSample, int?> selector, TimeSpan window, TimeSpan step)
    {
        List<HardwareSample> ring;
        lock (_gate)
            ring = _ring.ToList();

        var now = _time.GetUtcNow();
        var start = now - window;
        var count = (int)Math.Ceiling(window.TotalSeconds / step.TotalSeconds);
        var maxes = new int?[count];

        // Один проход по кольцу: индекс бакета из времени снимка.
        foreach (var s in ring)
        {
            var idx = (int)Math.Floor((s.At - start).TotalSeconds / step.TotalSeconds);
            if (idx < 0 || idx >= count) continue;
            var v = selector(s);
            if (v is null) continue;
            if (maxes[idx] is null || v > maxes[idx])
                maxes[idx] = v;
        }

        var result = new List<(long, int?)>(count);
        for (var i = 0; i < count; i++)
            result.Add(((start + step * i).ToUnixTimeMilliseconds(), maxes[i]));
        return result;
    }

    // Ожидание: по умолчанию Task.Delay; тест подменяет WaitFunc (гонит цикл на фейковых часах).
    private async Task WaitAsync(TimeSpan delay, CancellationToken ct)
    {
        if (WaitFunc is { } f)
        {
            await f(delay, ct);
            return;
        }
        await Task.Delay(delay, ct);
    }
}
