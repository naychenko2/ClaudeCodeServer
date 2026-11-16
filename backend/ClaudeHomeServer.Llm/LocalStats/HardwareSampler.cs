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

// Снимок «Железо» за 2 с одного узла: карты, CPU по сокетам, VRM, чипсет (+вентилятор), диски,
// вентиляторы, RAM, загрузка CPU и аптайм. null-поля — датчика нет (недоверенные источники:
// nvidia-smi, файл помощника, hwmon, meminfo).
public sealed record HardwareSample
{
    public DateTimeOffset At { get; init; }
    // Время работы узла, с (/proc/uptime); null — не прочитано.
    public long? UptimeS { get; init; }
    // Загрузка CPU, % (дельта /proc/stat двух соседних снимков узла); null — нет prev или счётчик ушёл назад.
    public int? CpuLoadPerc { get; init; }
    public HardwareGpuSample[]? Gpus { get; init; }
    // Сокеты (индекс = номер сокета, порядок — по PCI-адресу device, не по hwmonN); null — k10temp нет.
    public HwmonCpu[]? Cpus { get; init; }
    // VRM по сокетам, тот же индекс, что у Cpus. На узле с одним сокетом — VRM платы из hwmon (asusec).
    public int?[]? Vrms { get; init; }
    // VRM платы, когда сокетов два и больше (общий датчик, к сокету не привязан).
    public int? BoardVrm { get; init; }
    public int? Chipset { get; init; }
    public int? ChipsetFanRpm { get; init; }
    public int? Water { get; init; }
    public HwmonDisk[]? Disks { get; init; }
    public HwmonFan[]? CaseFans { get; init; }
    public long? RamUsedGb { get; init; }
    public long? RamTotalGb { get; init; }
    // Плата: hwmon asusec Motherboard (ai), иначе IPMI System Temp (worker); есть оба — hwmon.
    public int? BoardTempC { get; init; }
    // IPMI Peripheral Temp; только IPMI.
    public int? PeripheralTempC { get; init; }
    // На узле есть источник платы: чип asusec или текст IPMI (любой свежести). true при null-значениях —
    // «нет данных», false — «датчика нет».
    public bool BoardProbe { get; init; }
}

// Вид одного узла сэмплера: текущий снимок, статус, кольцо. Читается параллельно записи
// (блокировка этого узла, не общая).
public interface INodeHardwareView
{
    string Key { get; }

    // Последний снимок; null — снимков ещё нет.
    HardwareSample? Current { get; }

    // "ok" | "stale" | "down" | "incompatible". stale — последний снимок старше StaleAfter
    // или срез агента старше порога свежести; down — срез не получен; incompatible — другая версия.
    string Status { get; }

    // Момент начала нештатного статуса (unix-мс): у stale — последний живой снимок, у down/incompatible —
    // первое такое чтение; null — статус ok или снимков ещё нет.
    long? Since { get; }

    // Агрегация кольца в бакеты по step, МАКСИМУМ в бакете (пики важнее среднего);
    // null — нет данных в бакете (разрыв). t — unix-мс начала бакета.
    IReadOnlyList<(long t, int? value)> Aggregate(Func<HardwareSample, int?> selector, TimeSpan window, TimeSpan step);
}

// Сэмплер «Железо» (подсистема Llm, рядом с GpuSampler): снимок раз в 2 с в фоне на каждый узел
// реестра HardwareNodes, у каждого узла своё кольцо на 1 ч и свой статус. Срез узла — сырой
// NodeReading (локальный диск или агент), снимок собирается из него одинаково для любого узла:
// hwmon (HwmonReader), meminfo/uptime/stat (Ш1c), gddr6. Любой сбой разбора не выходит из
// BackgroundService (иначе упадёт весь хост): узел получает down или null-поля.
public interface IHardwareSampler
{
    // Ключи узлов в порядке реестра.
    IReadOnlyList<string> NodeKeys { get; }

    // Вид узла по ключу; null — такого узла нет.
    INodeHardwareView? ForNode(string key);
}

// Узел сэмплера: ключ + источник сырого среза.
public sealed record HardwareSamplerNode(string Key, INodeReadingSource Source);

public sealed class HardwareSampler : BackgroundService, IHardwareSampler
{
    // Шаг снимка: 2 с (кольцо 1 ч = 1800 снимков).
    public static readonly TimeSpan SampleInterval = TimeSpan.FromSeconds(2);

    // Кольцо: 1 ч.
    public static readonly TimeSpan HistoryWindow = TimeSpan.FromHours(1);

    // Последний снимок старше этого → статус "stale" (цикл не успевает / источник завис).
    public static readonly TimeSpan StaleAfter = TimeSpan.FromSeconds(10);
    // Тот же порог в мс — для ageMs удалённого среза (RemoteReadingSource, ADR-029 «Версии и статусы узла»:
    // stale по ageMs > 10 000 — «порог тот же, что у локального status:"stale"»).
    public const long StaleAfterMs = 10_000;

    // Тайм-аут одного тика узла: не уложившийся узел → down, опоздавший срез отбрасывается.
    public static readonly TimeSpan TickTimeout = TimeSpan.FromSeconds(4);

    // Предупреждение о нескольких чипах платы с одним name — не чаще раза в 5 мин на узел.
    private static readonly TimeSpan AmbiguousWarnEvery = TimeSpan.FromMinutes(5);

    // Предупреждение о несовпадении числа сокетов k10temp и IPMI — не чаще раза в 5 мин на узел.
    private static readonly TimeSpan IpmiWarnEvery = TimeSpan.FromMinutes(5);

    // Число точек кольца: 1 ч / 2 с = 1800.
    private static int MaxSamples => (int)Math.Ceiling(HistoryWindow.TotalSeconds / SampleInterval.TotalSeconds);

    private readonly IOptionsMonitor<LocalModelStatsOptions> _options;
    private readonly Gddr6HelperReader _gddr6;
    private readonly TimeProvider _time;
    private readonly ILogger<HardwareSampler> _log;
    private readonly Dictionary<string, NodeState> _nodes = new(StringComparer.Ordinal);

    // Ожидание: по умолчанию Task.Delay; тест подменяет (гонит цикл без реальных пауз).
    // Им же меряется тайм-аут тика — тест различает их по значению delay.
    internal Func<TimeSpan, CancellationToken, Task>? WaitFunc { get; set; }

    // Разбор SDR: по умолчанию IpmiSdrParser.Parse; тест подменяет (бросающий разбор).
    internal Func<string?, IpmiReading?> IpmiParse { get; set; } = IpmiSdrParser.Parse;

    public HardwareSampler(
        IOptionsMonitor<LocalModelStatsOptions> options,
        IReadOnlyList<HardwareSamplerNode> nodes,
        Gddr6HelperReader gddr6,
        TimeProvider time,
        ILogger<HardwareSampler>? log = null)
    {
        _options = options;
        _gddr6 = gddr6;
        _time = time;
        _log = log ?? NullLogger<HardwareSampler>.Instance;
        foreach (var n in nodes)
            _nodes[n.Key] = new NodeState(n.Key, n.Source, time);
        NodeKeys = nodes.Select(n => n.Key).ToArray();
    }

    public IReadOnlyList<string> NodeKeys { get; }

    public INodeHardwareView? ForNode(string key) => _nodes.GetValueOrDefault(key);

    // Число снимков в кольце узла (тесты).
    internal int RingCount(string key) => _nodes[key].RingCount;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Узлы опрашиваются параллельно: зависший не задерживает остальных.
        await Task.WhenAll(_nodes.Values.Select(n => NodeLoopAsync(n, stoppingToken)));
    }

    private async Task NodeLoopAsync(NodeState node, CancellationToken stoppingToken)
    {
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    if (_options.CurrentValue.Enabled)
                        await PollNodeAsync(node.Key, stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    // Недоверенные источники: исключение разбора одного узла — down этого узла, хост живёт.
                    _log.LogDebug(ex, "Сэмплер «Железо»: сбой снимка узла {Node}", node.Key);
                    node.MarkDown(_time.GetUtcNow());
                }
                await WaitAsync(SampleInterval, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Нормальная остановка хоста.
        }
    }

    // Один тик узла: читает срез (с тайм-аутом), строит HardwareSample, кладёт в кольцо этого узла.
    // internal — тесты гоняют тики напрямую, двигая часы. Срез, не пришедший за TickTimeout,
    // отбрасывается (метку времени ставит worker при получении, а тик уже закрыт).
    internal async Task<HardwareSample?> PollNodeAsync(string key, CancellationToken ct = default)
    {
        var node = _nodes[key];
        using var tickCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var read = node.Source.ReadAsync(tickCts.Token);
        // Опоздавшее исключение не должно остаться ненаблюдаемым.
        _ = read.ContinueWith(static t => _ = t.Exception, CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        var timeout = WaitAsync(TickTimeout, tickCts.Token);
        var done = await Task.WhenAny(read, timeout).ConfigureAwait(false);
        if (done != read)
        {
            tickCts.Cancel();
            ct.ThrowIfCancellationRequested();
            node.MarkDown(_time.GetUtcNow());
            return null;
        }
        tickCts.Cancel(); // гасит таймер тайм-аута
        var reading = await read.ConfigureAwait(false);

        var now = _time.GetUtcNow();
        if (reading.Status is NodeReadingStatus.Down or NodeReadingStatus.Incompatible)
        {
            node.MarkStatus(reading.Status, now);
            return null;
        }

        var sample = BuildSample(node, reading, now, out var ambiguous);
        node.Commit(sample, reading.Status, now);
        if (ambiguous.Length > 0 && node.ShouldWarnAmbiguous(now, AmbiguousWarnEvery))
            _log.LogWarning("Узел {Node}: несколько чипов hwmon с одним name ({Chips}) — взят первый по порядку device",
                node.Key, string.Join(", ", ambiguous));
        return sample;
    }

    // Снимок из сырого среза — одинаково для любого узла.
    private HardwareSample BuildSample(NodeState node, NodeReading reading, DateTimeOffset now, out string[] ambiguous)
    {
        // gddr6: связка по каноническому busId; протухший или отсутствующий → mem/hotspot = null.
        var gddr6Map = new Dictionary<string, Gddr6GpuTemp>();
        if (reading.Gddr6 is { } gtext && _gddr6.Parse(gtext) is { } gr && gr.fresh)
        {
            foreach (var g in gr.gpus)
                gddr6Map[GpuSampler.CanonicalBusId(g.BusId)] = g;
        }

        var gpus = new List<HardwareGpuSample>();
        foreach (var gs in reading.Gpus)
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

        var files = reading.Files;
        var hw = new HwmonReader(files).Read();
        ambiguous = hw?.AmbiguousChips ?? [];
        var mem = MeminfoReader.Parse(files.ReadText("/proc/meminfo"));
        var jiffies = ProcStatReader.ParseCpu(files.ReadText("/proc/stat"));
        var load = node.SwapJiffies(jiffies);

        // Сокеты: VRM платы из hwmon — единственному сокету (ключ vrm остаётся); при двух и больше
        // это общий датчик платы (BoardVrm), VRM сокетов придёт из IPMI (Ш2d4).
        var cpus = hw?.Cpus;
        var boardVrm = hw?.Board?.Vrm;
        int?[]? vrms;
        int? boardVrmOut = null;
        if (cpus is { Length: > 1 })
        {
            vrms = new int?[cpus.Length];
            boardVrmOut = boardVrm;
        }
        else
            vrms = boardVrm is null && cpus is null ? null : [boardVrm];

        // Возраст Stale-среза известен (AgeMs — monotonic-часы агента, NodeReadingSource.cs:33):
        // в кольцо снимок идёт с временем данных, а не получения — устаревшее не должно выглядеть
        // живым. Ok-срез метится временем получения, как раньше.
        var sampleAt = reading.Status == NodeReadingStatus.Stale
            ? now - TimeSpan.FromMilliseconds(reading.AgeMs)
            : now;

        var baseSample = new HardwareSample
        {
            At = sampleAt,
            UptimeS = ProcStatReader.ParseUptimeS(files.ReadText("/proc/uptime")),
            CpuLoadPerc = load,
            Gpus = gpus.Count > 0 ? gpus.ToArray() : null,
            Cpus = cpus,
            Vrms = vrms,
            BoardVrm = boardVrmOut,
            Chipset = hw?.Board?.Chipset,
            ChipsetFanRpm = hw?.Board?.ChipsetFanRpm,
            Water = hw?.Board?.Water,
            Disks = hw?.Disks,
            CaseFans = hw?.CaseFans,
            RamUsedGb = mem?.UsedGb,
            RamTotalGb = mem?.TotalGb,
            BoardTempC = hw?.Board?.Motherboard,
            BoardProbe = hw?.Board is not null || reading.Ipmi is not null,
        };

        // IPMI: несвежий, пустой или неразобранный текст даёт null — ни одно IPMI-значение не попадает в снимок.
        IpmiReading? ipmi = null;
        try
        {
            var clockMs = reading.ClockMs ?? now.ToUnixTimeMilliseconds();
            if (new NodeSlice { Ipmi = reading.Ipmi, ClockMs = clockMs }.TryReadIpmi() is { } fresh)
                ipmi = IpmiParse(fresh.sdr);
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Сэмплер «Железо»: сбой разбора IPMI узла {Node}", node.Key);
            ipmi = null;
        }

        if (ipmi is not null && ipmi.SocketTempsC.Length != (baseSample.Cpus?.Length ?? 0)
            && node.ShouldWarnIpmi(now, IpmiWarnEvery))
            _log.LogWarning("Узел {Node}: сокетов IPMI {Ipmi}, k10temp {Cpu} — VRM сокетов уходят в BoardVrm",
                node.Key, ipmi.SocketTempsC.Length, baseSample.Cpus?.Length ?? 0);

        return MergeIpmi(baseSample, ipmi);
    }

    // Слияние IPMI в снимок (чистая функция). ipmi == null — снимок как есть (hwmon-значения не трогаются).
    // Температуры сокетов остаются из k10temp; из IPMI идут VRM, вентиляторы и плата.
    internal static HardwareSample MergeIpmi(HardwareSample hw, IpmiReading? ipmi)
    {
        if (ipmi is null)
            return hw;

        var cpuCount = hw.Cpus?.Length ?? 0;
        var vrms = hw.Vrms;
        var boardVrm = hw.BoardVrm;
        if (hw.Cpus is not null && ipmi.SocketTempsC.Length == cpuCount)
        {
            // Номера совпадают: CPU<n> ↔ сокет n−1, VRM по сокетам.
            if (vrms is not null)
            {
                vrms = (int?[])vrms.Clone();
                for (var i = 0; i < vrms.Length && i < ipmi.Vrms.Length; i++)
                    vrms[i] ??= ipmi.Vrms[i];
            }
            boardVrm ??= ipmi.BoardVrm;
        }
        else
        {
            // Нумерация не сверяется: VRM сокетов складываем в общий VRM платы (максимум).
            if (boardVrm is null)
            {
                var all = ipmi.Vrms.Append(ipmi.BoardVrm).Where(v => v is not null).Select(v => v!.Value).ToArray();
                boardVrm = all.Length > 0 ? all.Max() : null;
            }
        }

        var fans = hw.CaseFans;
        if (ipmi.Fans.Length > 0)
            fans = [.. hw.CaseFans ?? [], .. ipmi.Fans];

        return hw with
        {
            Vrms = vrms,
            BoardVrm = boardVrm,
            CaseFans = fans,
            BoardTempC = hw.BoardTempC ?? ipmi.BoardTempC,
            PeripheralTempC = ipmi.PeripheralTempC,
        };
    }

    // Ожидание: по умолчанию Task.Delay; тест подменяет WaitFunc.
    private async Task WaitAsync(TimeSpan delay, CancellationToken ct)
    {
        if (WaitFunc is { } f)
        {
            await f(delay, ct);
            return;
        }
        await Task.Delay(delay, ct);
    }

    // Состояние узла: кольцо, текущий снимок, статус, prev jiffies. Пишет только цикл этого узла
    // (single-writer на ключ); чтение — параллельно, под блокировкой ЭТОГО узла.
    private sealed class NodeState(string key, INodeReadingSource source, TimeProvider time) : INodeHardwareView
    {
        private readonly object _gate = new();
        private readonly List<HardwareSample> _ring = [];
        private HardwareSample? _current;
        private DateTimeOffset? _lastSampleTime;
        private NodeReadingStatus _lastStatus = NodeReadingStatus.Ok;
        private DateTimeOffset? _statusSince;
        private CpuJiffies? _prevJiffies;
        private DateTimeOffset? _lastAmbiguousWarn;

        public string Key { get; } = key;
        public INodeReadingSource Source { get; } = source;

        public int RingCount { get { lock (_gate) return _ring.Count; } }

        public HardwareSample? Current { get { lock (_gate) return _current; } }

        public string Status { get { lock (_gate) return StatusLocked(); } }

        public long? Since
        {
            get
            {
                lock (_gate)
                {
                    return StatusLocked() switch
                    {
                        "ok" => null,
                        "stale" => _lastSampleTime?.ToUnixTimeMilliseconds(),
                        _ => _statusSince?.ToUnixTimeMilliseconds(),
                    };
                }
            }
        }

        // stale считается в момент чтения: цикл мог зависнуть и не обновить снимок. Снимков ещё нет — "ok".
        private string StatusLocked() => _lastStatus switch
        {
            NodeReadingStatus.Down => "down",
            NodeReadingStatus.Incompatible => "incompatible",
            NodeReadingStatus.Stale => "stale",
            _ => _lastSampleTime is { } last && time.GetUtcNow() - last > StaleAfter ? "stale" : "ok",
        };

        // Загрузка CPU по паре соседних снимков; сохраняет текущие счётчики как prev.
        // Единственный писатель — цикл узла, блокировка нужна только чтению в тестах.
        public int? SwapJiffies(CpuJiffies? cur)
        {
            lock (_gate)
            {
                var load = ProcStatReader.CpuLoadPerc(_prevJiffies, cur);
                _prevJiffies = cur;
                return load;
            }
        }

        public void Commit(HardwareSample sample, NodeReadingStatus status, DateTimeOffset now)
        {
            lock (_gate)
            {
                _ring.Add(sample);
                while (_ring.Count > MaxSamples)
                    _ring.RemoveAt(0);
                _current = sample;
                _lastSampleTime = now;
                SetStatusLocked(status, now);
            }
        }

        public void MarkStatus(NodeReadingStatus status, DateTimeOffset now)
        {
            lock (_gate)
            {
                _prevJiffies = null; // после обрыва дельта через разрыв — ложная загрузка
                SetStatusLocked(status, now);
            }
        }

        public void MarkDown(DateTimeOffset now) => MarkStatus(NodeReadingStatus.Down, now);

        private void SetStatusLocked(NodeReadingStatus status, DateTimeOffset now)
        {
            if (status != _lastStatus || _statusSince is null)
                _statusSince = now;
            _lastStatus = status;
        }

        private DateTimeOffset? _lastIpmiWarn;

        public bool ShouldWarnIpmi(DateTimeOffset now, TimeSpan every)
        {
            lock (_gate)
            {
                if (_lastIpmiWarn is { } w && now - w < every) return false;
                _lastIpmiWarn = now;
                return true;
            }
        }

        public bool ShouldWarnAmbiguous(DateTimeOffset now, TimeSpan every)
        {
            lock (_gate)
            {
                if (_lastAmbiguousWarn is { } w && now - w < every) return false;
                _lastAmbiguousWarn = now;
                return true;
            }
        }

        // Агрегация кольца в бакеты по step, МАКСИМУМ в бакете; null — нет данных (разрыв).
        // t — unix-мс начала бакета. Бакеты идут от (now - window) до now, шаг step.
        public IReadOnlyList<(long t, int? value)> Aggregate(Func<HardwareSample, int?> selector, TimeSpan window, TimeSpan step)
        {
            List<HardwareSample> ring;
            lock (_gate)
                ring = _ring.ToList();

            var now = time.GetUtcNow();
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
    }
}
