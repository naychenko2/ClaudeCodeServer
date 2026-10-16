using ClaudeHomeServer.Models;
using Microsoft.Extensions.Options;

namespace ClaudeHomeServer.Services.Llm;

// Фоновый поллер метрик локального стенда (vLLM) + кольцо истории на 1 час в памяти.
// Источник метрик выбирается по провайдеру, помеченному IsLocal, и его секции Stats:
//   • нет локального провайдера / нет Stats → notConnected (configured=false), сеть не трогаем;
//   • Stats.Engine = "vllm" + MetricsUrl → VllmMetricsSource (именованный HttpClient);
//   • Stats.Engine = "llama-server" | "ollama" (или пусто) → NullEngineMetricsSource.
// Автоугадывания движка нет: он задаётся явно.
//
// Состояния: working (нет запросов) | busy (running>0 или waiting>0) | down (HTTP-сбой,
// DownSince — момент ПЕРВОГО сбоя) | notConnected. Интервалы: PollInterval (5 с) в покое,
// ActivePollInterval (1 с) пока есть запросы, DownPollInterval (15 с) когда движок мёртв.
public sealed class LocalModelStatsPoller : BackgroundService, ILocalModelStatsReader
{
    // Имя именованного HttpClient для /metrics (регистрация — в LlmSubsystem, AddQuietHttpClient).
    public const string HttpClientName = "local-model-stats";

    // При Enabled=false не ходим в сеть: цикл ждёт и перечитывает настройку раз в 5 с.
    private static readonly TimeSpan DisabledPollInterval = TimeSpan.FromSeconds(5);

    // Окно для расчёта hit-долей и «скорости на поток»: ~60 с, как чувствует чат.
    private static readonly TimeSpan StreamWindow = TimeSpan.FromSeconds(60);

    // Шаг точки кольца: одна точка на 5 с (720 точек = 1 ч).
    private static readonly TimeSpan PointStep = TimeSpan.FromSeconds(5);

    private readonly IOptionsMonitor<LocalModelStatsOptions> _options;
    private readonly LlmProviderRegistry _providers;
    private readonly IHttpClientFactory _httpFactory;
    private readonly TimeProvider _time;
    private readonly ILogger<LocalModelStatsPoller> _log;

    // Кольцо истории, буфер счётчиков и текущий снимок: мутации только под _gate
    // (ExecuteAsync) и атомарная замена ссылки (Current/GetHistory) — без блокировки на чтении.
    private readonly object _gate = new();
    private List<LocalModelStatsPoint> _history = [];
    private List<CounterPoint> _counters = [];
    private LocalModelStatsCurrent _current = new() { Status = "notConnected", Configured = false };

    // Момент ПЕРВОГО сбоя: не сбрасывается повторными сбоями, только возвратом к working.
    private DateTimeOffset? _downSince;

    // Последняя успешная EngineSnapshot + её время: для расчёта дельты скорости в кольце.
    private EngineSnapshot? _last;
    private DateTimeOffset _lastTime;

    // M6: момент и снимок ПОСЛЕДНЕЙ точки кольца: прореживание по времени (не чаще PointStep),
    // чтобы частый опрос (1 с) не сжимал окно «1 ч» до ~12 мин. null — точек ещё нет.
    private DateTimeOffset? _lastPointTime;
    private EngineSnapshot? _lastPointSnap;

    // Bегущий максимум vllm:kv_cache_usage_perc по опросам с прошлой точки (v2.1, kvUsage):
    // при нагрузке опрос 1×/с, пик давления между точками не теряется. Сбрасывается на точке.
    // single-writer: читается и пишется только из UpdateRing фонового опроса.
    private double? _kvUsageMaxSincePoint;

    // Живое «сейчас»: дельта generation_tokens от предыдущего опроса (каждый опрос, не только на точке)
    private double? _liveThroughput;

    // Коэффициент живой скорости (ADR-029): токенов на шаг декодирования
    // (generation_tokens_total / inter_token_latency_seconds_count). DFlash отдаёт несколько
    // токенов за SSE-дельту; наблюдатель кольца Ctx масштабирует счётчик дельт этим
    // коэффициентом. null — поллер выключен или движок не отдал обе метрики.
    private double? _tokensPerStep;

    // Ожидаемый интервал: по умолчанию Task.Delay; тест подменяет, чтобы гонять цикл
    // на фейковых часах (без реального Task.Delay).
    internal Func<TimeSpan, CancellationToken, Task>? WaitFunc { get; set; }

    public LocalModelStatsPoller(
        IOptionsMonitor<LocalModelStatsOptions> options,
        LlmProviderRegistry providers,
        IHttpClientFactory httpFactory,
        TimeProvider time,
        ILogger<LocalModelStatsPoller> log)
    {
        _options = options;
        _providers = providers;
        _httpFactory = httpFactory;
        _time = time;
        _log = log;
    }

    public LocalModelStatsCurrent Current
    {
        get { lock (_gate) return _current; }
    }

    // Коэффициент живой скорости (ADR-029): токенов на шаг декодирования. null — нет данных
    // (поллер выключен или движок не отдал обе метрики). Чтение под _gate: Nullable<double>
    // не атомарна (в отличие от double), а читает её счётчик хода на другом потоке.
    public double? TokensPerStep { get { lock (_gate) return _tokensPerStep; } }

    public IReadOnlyList<LocalModelStatsPoint> GetHistory(TimeSpan window, long? sinceUnixMs = null)
    {
        lock (_gate)
        {
            var since = _time.GetUtcNow() - window;
            var sinceUtc = sinceUnixMs is long ms ? DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime : since.UtcDateTime;
            var list = _history.Where(p => p.Timestamp >= sinceUtc).ToList();
            return list;
        }
    }

    // Средняя скорость на поток за окно (15m/1h, те же окна, что у графика «Нагрузка»):
    // Δgeneration_tokens/Δinter_token_latency (генерация) и Δprefill_kv_computed_tokens/Δprefill_time
    // (чтение промпта). Знаменатель генерации — суммарное inter-token-время движка (пишется на
    // каждом шаге, ADR-029), поэтому простой (движок молчит) в среднее не входит: ночью среднее
    // не падает к нулю. Окно режется по разрыву (рестарт счётчика → отрицательная дельта → null),
    // ровно как график рисует разрыв. Текущие значения счётчиков — из последнего снимка;
    // старейшая точка окна — из буфера.
    public LocalModelStatsAverage GetAverageSpeed(TimeSpan window)
    {
        var now = _time.GetUtcNow();
        var basePt = OldestInWindow(now, window);
        if (basePt is null || _last is not { } cur)
            return new LocalModelStatsAverage(); // null/null: нет данных за окно
        return new LocalModelStatsAverage
        {
            GenTps = SpeedOverWindow(basePt, cur,
                p => p.GenerationTokens, s => (double?)s.GenerationTokens,
                p => p.InterTokenLatencySum, s => s.InterTokenLatencySum),
            PromptTps = SpeedOverWindow(basePt, cur,
                p => p.PrefillKvComputedTokens, s => s.PrefillKvComputedTokens,
                p => p.PrefillTimeSum, s => s.PrefillTimeSum),
        };
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                var opts = _options.CurrentValue;
                if (!opts.Enabled)
                {
                    // Не ходим в сеть: ждём и перечитываем настройку.
                    await WaitAsync(DisabledPollInterval, stoppingToken);
                    continue;
                }

                var interval = await PollOnceAsync(stoppingToken);
                await WaitAsync(interval, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Нормальная остановка хоста.
        }
    }

    // Один опрос: читает источник, обновляет кольцо/буфер/Current/DownSince и возвращает
    // интервал до следующего опроса (зависит от достигнутых состояния). internal — тесты
    // гоняют опросы напрямую, двигая фейковые часы, без фонового цикла.
    internal async Task<TimeSpan> PollOnceAsync(CancellationToken ct = default)
    {
        var opts = _options.CurrentValue;
        var (source, engine, model, tensorParallel, configured) = ResolveSource();
        if (!configured)
        {
            // notConnected: сбрасываем _downSince (движок «не мёртв» — его просто нет).
            _downSince = null;
            SetState(new LocalModelStatsCurrent
            {
                Status = "notConnected",
                Engine = engine,
                Model = model,
                TensorParallel = tensorParallel,
                Configured = false,
            });
            return opts.PollInterval;
        }

        EngineSnapshot? snapshot;
        try
        {
            snapshot = await source.ReadAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Источник не должен бросать наружу (VllmMetricsSource гасит), но на всякий случай.
            _log.LogDebug(ex, "Поллер метрик локального стенда: сбой источника");
            snapshot = null;
        }

        var now = _time.GetUtcNow();

        if (snapshot is null)
        {
            // HTTP-сбой/таймаут: down. DownSince фиксируем на ПЕРВОМ сбое.
            if (_downSince is null)
            {
                _downSince = now;
                _log.LogInformation("Локальный стенд: движок не отвечает (первый сбой {Since:u})", now);
            }
            SetState(new LocalModelStatsCurrent
            {
                Status = "down",
                DownSince = _downSince,
                Engine = engine,
                Model = model,
                TensorParallel = tensorParallel,
                Configured = true,
            });
            return opts.DownPollInterval;
        }

        // Успех: снимаем down (возврат к working/busy), обновляем кольцо, буфер и Current.
        if (_downSince is not null)
        {
            _downSince = null;
            _log.LogInformation("Локальный стенд: движок снова отвечает");
        }
        UpdateRing(now, snapshot);
        UpdateCounters(now, snapshot);
        UpdateCurrent(now, snapshot, engine, model, tensorParallel);

        var busy = (snapshot.Running ?? 0) > 0 || (snapshot.Waiting ?? 0) > 0;
        return busy ? opts.ActivePollInterval : opts.PollInterval;
    }

    // Фабрика источника: по умолчанию ResolveSource (по провайдеру). Тест подменяет,
    // чтобы подставить фейковый IEngineMetricsSource.
    internal Func<(IEngineMetricsSource, string?, string?, int?, bool)>? SourceFactoryOverride { get; set; }

    // Источник метрик по локальному провайдеру с секцией Stats.
    // (source, engine, model, tensorParallel, configured): configured=false — notConnected.
    private (IEngineMetricsSource, string?, string?, int?, bool) ResolveSource()
    {
        if (SourceFactoryOverride is { } f)
            return f();
        var local = _providers.All.FirstOrDefault(p => p.IsLocal);
        if (local is null || local.Stats is null)
            return (new NullEngineMetricsSource(), null, FirstModel(local), null, false);

        var engine = local.Stats.Engine.Trim().ToLowerInvariant();
        var model = FirstModel(local);
        var tensorParallel = local.Stats.TensorParallel;
        if (engine == "vllm" && !string.IsNullOrWhiteSpace(local.Stats.MetricsUrl))
            return (new VllmMetricsSource(_httpFactory.CreateClient(HttpClientName), local.Stats.MetricsUrl), engine, model, tensorParallel, true);
        // llama-server / ollama / пусто — заглушка (метрики не отдают Prometheus).
        return (new NullEngineMetricsSource(), string.IsNullOrEmpty(engine) ? null : engine, model, tensorParallel, true);
    }

    private static string? FirstModel(LlmProviderConfig? p) =>
        p?.Models.FirstOrDefault()?.Id;

    // Точка кольца: скорость = Δgeneration_tokens/Δвремя; отрицательная дельта (рестарт) → null.
    private void UpdateRing(DateTimeOffset now, EngineSnapshot snap)
    {
        // Живое «сейчас»: дельта от предыдущего опроса (каждый опрос, не только на точке кольца).
        // dt — время с прошлого опроса; считается только когда есть прошлый опрос.
        _liveThroughput = null;
        if (_last is { GenerationTokens: not null } prev && snap.GenerationTokens is not null)
        {
            var dt = (now - _lastTime).TotalSeconds;
            var dTokens = snap.GenerationTokens.Value - prev.GenerationTokens.Value;
            if (dt > 0)
                _liveThroughput = dTokens < 0 ? null : dTokens / dt;
        }

        // Коэффициент живой скорости (ADR-029): токенов на шаг декодирования.
        // DFlash отдаёт несколько токенов за SSE-дельту; наблюдатель кольца Ctx масштабирует
        // счётчик дельт этим коэффициентом. Считаем по накопленным счётчикам (стабильно).
        // Запись под _gate: Nullable<double> не атомарна, а свойство читает на другом потоке.
        if (snap.GenerationTokens is { } g && snap.InterTokenLatencyCount is { } c && c > 0)
        {
            lock (_gate)
                _tokensPerStep = (double)g / c;
        }

        // v2.1 (kvUsage): бегущий максимум доли KV-кэша по опросам с прошлой точки.
        // При нагрузке опрос 1×/с: пик давления между точками не теряется. Сброс — на точке.
        if (snap.KvUsagePerc is { } kv)
            _kvUsageMaxSincePoint = _kvUsageMaxSincePoint is { } m ? Math.Max(m, kv) : kv;

        // M6: прореживание по времени — точка в кольцо не чаще PointStep (5 с). Частый опрос (1 с)
        // иначе сжимает окно «1 ч» до ~12 мин (720 точек × 1 с). Дельта скорости — за интервал от
        // предыдущей ТОЧКИ (не от предыдущего опроса), поэтому скорость в кольце — среднее за шаг.
        var due = _lastPointTime is null || now - _lastPointTime.Value >= PointStep;

        if (due)
        {
            double? throughput = null;
            if (_lastPointSnap is { GenerationTokens: not null } prevPoint
                && snap.GenerationTokens is not null)
            {
                var pointDt = (now - _lastPointTime!.Value).TotalSeconds;
                var dTokens = snap.GenerationTokens.Value - prevPoint.GenerationTokens.Value;
                if (pointDt > 0)
                    throughput = dTokens < 0 ? null : dTokens / pointDt;
            }

            // Серии кэша (v2.1): kvUsage — максимум по опросам с прошлой точки; пять счётчиков —
            // приращение с прошлой ТОЧКИ (как GenThroughput через _lastPointSnap), не с прошлого
            // опроса: иначе при опросе 1×/с между точками потерялись бы 4 опроса из 5.
            var point = new LocalModelStatsPoint
            {
                Timestamp = now,
                GenThroughput = throughput,
                Running = snap.Running,
                Waiting = snap.Waiting,
                KvUsage = _kvUsageMaxSincePoint,
                PrefixHits = CounterDelta(_lastPointSnap, snap, s => s.PrefixCacheHits),
                PrefixQueries = CounterDelta(_lastPointSnap, snap, s => s.PrefixCacheQueries),
                RamHits = CounterDelta(_lastPointSnap, snap, s => s.RamTierHits),
                RamQueries = CounterDelta(_lastPointSnap, snap, s => s.RamTierQueries),
                Preemptions = CounterDelta(_lastPointSnap, snap, s => s.Preemptions),
                // Скорость на поток (вторая линия графика): приращения с прошлой ТОЧКИ,
                // фронт сводит в интервалы и делит ΣgenTokens/ΣitlSeconds (ток/с).
                // ItlCount — число шагов декодирования: фронт считает из трёх приращений
                // время шага (ΣitlSeconds/ΣitlCount), токенов за шаг (ΣgenTokens/ΣitlCount)
                // и агентов в генерации (ΣitlSeconds/длина интервала).
                GenTokens = CounterDelta(_lastPointSnap, snap, s => s.GenerationTokens),
                ItlSeconds = DoubleCounterDelta(_lastPointSnap, snap, s => s.InterTokenLatencySum),
                ItlCount = CounterDelta(_lastPointSnap, snap, s => s.InterTokenLatencyCount),
            };

            lock (_gate)
            {
                _history.Add(point);
                var maxPoints = Math.Max(1, (int)Math.Ceiling(_options.CurrentValue.HistoryWindow.TotalSeconds / PointStep.TotalSeconds));
                while (_history.Count > maxPoints)
                    _history.RemoveAt(0);
            }

            _lastPointTime = now;
            _lastPointSnap = snap;
            _kvUsageMaxSincePoint = null; // максимум «с прошлой точки» собран: сбрасываем
        }

        // Живое «сейчас» — каждый опрос (не только на точке): ThroughputOf отдаёт свежую дельту
        _last = snap;
        _lastTime = now;
    }

    // Точка накопленных счётчиков для оконных расчётов (hit-доли, скорость, preemptions).
    // Все значения — монотонно возрастающие счётчики движка; отрицательная дельта = рестарт.
    private sealed class CounterPoint
    {
        public DateTimeOffset Time;
        public long? PrefixCacheHits;
        public long? PrefixCacheQueries;
        public long? RamTierHits;
        public long? RamTierQueries;
        public long? Preemptions;
        public double? DecodeTimeSum;
        public double? PrefillTimeSum;
        public long? GenerationTokens;
        public double? PrefillKvComputedTokens;
        public double? InterTokenLatencySum;
        public long? InterTokenLatencyCount;
    }

    // Добавляет текущий снимок в буфер счётчиков, обрывает старые точки (за PreemptionsWindow)
    // и сбрасывает буфер при разрыве (рестарт счётчика: отрицательная дельта относительно
    // предыдущей точки). Мутации под _gate.
    private void UpdateCounters(DateTimeOffset now, EngineSnapshot snap)
    {
        var point = new CounterPoint
        {
            Time = now,
            PrefixCacheHits = snap.PrefixCacheHits,
            PrefixCacheQueries = snap.PrefixCacheQueries,
            RamTierHits = snap.RamTierHits,
            RamTierQueries = snap.RamTierQueries,
            Preemptions = snap.Preemptions,
            DecodeTimeSum = snap.DecodeTimeSum,
            PrefillTimeSum = snap.PrefillTimeSum,
            GenerationTokens = snap.GenerationTokens,
            PrefillKvComputedTokens = snap.PrefillKvComputedTokens,
            InterTokenLatencySum = snap.InterTokenLatencySum,
            InterTokenLatencyCount = snap.InterTokenLatencyCount,
        };

        lock (_gate)
        {
            // Разрыв: любой счётчик упал относительно предыдущей точки → рестарт движка.
            if (_counters.Count > 0 && IsBreak(_counters[^1], point))
                _counters.Clear();
            _counters.Add(point);

            // Обрыв: держим только точки за HistoryWindow (1 ч) — самое длинное окно
            // (средняя скорость и график «Нагрузка» используют окна 15m/1h; preemptions — 15m).
            var windowStart = now - _options.CurrentValue.HistoryWindow;
            while (_counters.Count > 1 && _counters[0].Time < windowStart)
                _counters.RemoveAt(0);
        }
    }

    // Рестарт счётчика: любое значение новой точки меньше предыдущей (обе непустые).
    private static bool IsBreak(CounterPoint prev, CounterPoint cur)
    {
        return
            (prev.PrefixCacheHits is not null && cur.PrefixCacheHits is not null && cur.PrefixCacheHits < prev.PrefixCacheHits)
            || (prev.PrefixCacheQueries is not null && cur.PrefixCacheQueries is not null && cur.PrefixCacheQueries < prev.PrefixCacheQueries)
            || (prev.RamTierHits is not null && cur.RamTierHits is not null && cur.RamTierHits < prev.RamTierHits)
            || (prev.RamTierQueries is not null && cur.RamTierQueries is not null && cur.RamTierQueries < prev.RamTierQueries)
            || (prev.Preemptions is not null && cur.Preemptions is not null && cur.Preemptions < prev.Preemptions)
            || (prev.DecodeTimeSum is not null && cur.DecodeTimeSum is not null && cur.DecodeTimeSum < prev.DecodeTimeSum)
            || (prev.PrefillTimeSum is not null && cur.PrefillTimeSum is not null && cur.PrefillTimeSum < prev.PrefillTimeSum)
            || (prev.GenerationTokens is not null && cur.GenerationTokens is not null && cur.GenerationTokens < prev.GenerationTokens)
            || (prev.PrefillKvComputedTokens is not null && cur.PrefillKvComputedTokens is not null && cur.PrefillKvComputedTokens < prev.PrefillKvComputedTokens)
            || (prev.InterTokenLatencySum is not null && cur.InterTokenLatencySum is not null && cur.InterTokenLatencySum < prev.InterTokenLatencySum)
            || (prev.InterTokenLatencyCount is not null && cur.InterTokenLatencyCount is not null && cur.InterTokenLatencyCount < prev.InterTokenLatencyCount);
    }

    // Самая старая точка буфера не раньше now-window; null — буфер короче окна (не хватает истории).
    private CounterPoint? OldestInWindow(DateTimeOffset now, TimeSpan window)
    {
        var since = now - window;
        CounterPoint? oldest = null;
        lock (_gate)
        {
            foreach (var p in _counters)
            {
                if (p.Time >= since)
                {
                    oldest = p;
                    break;
                }
            }
        }
        return oldest;
    }

    // Δ за окно: cur - basePt; null — одно из значений не заполнено или отрицательная дельта (рестарт).
    private static long? DeltaLong(long? basePt, long? cur)
    {
        if (basePt is not { } a || cur is not { } b) return null;
        var d = b - a;
        return d < 0 ? null : d;
    }

    private static double? DeltaDouble(double? basePt, double? cur)
    {
        if (basePt is not { } a || cur is not { } b) return null;
        var d = b - a;
        return d < 0 ? null : d;
    }

    // Приращение счётчика с прошлой ТОЧКИ кольца (v2.1, серии кэша): cur − prevPoint.
    // null — прошлой точки нет, метрика не на прошлой или текущей точке, или счётчик упал (рестарт).
    // Отрицательных значений наружу не идёт (тот же приём, что DeltaLong/DeltaDouble).
    private static long? CounterDelta(EngineSnapshot? prevPoint, EngineSnapshot cur, Func<EngineSnapshot, long?> sel)
    {
        if (prevPoint is null) return null;
        var a = sel(prevPoint);
        var b = sel(cur);
        if (a is null || b is null) return null;
        var d = b - a;
        return d < 0 ? null : d;
    }

    // Приращение double-счётчика с прошлой ТОЧКИ кольца (скорость на поток): cur − prevPoint.
    // null и правило «без отрицательных» — те же, что CounterDelta (long-вариант).
    private static double? DoubleCounterDelta(EngineSnapshot? prevPoint, EngineSnapshot cur, Func<EngineSnapshot, double?> sel)
    {
        if (prevPoint is null) return null;
        var a = sel(prevPoint);
        var b = sel(cur);
        if (a is null || b is null) return null;
        var d = b - a;
        return d < 0 ? null : d;
    }

    // Hit-доля за окно: Δчислитель/Δзнаменатель; basePt — старейшая точка окна (null — окна нет).
    // Деление на ноль/отрицательная дельта → null.
    private static double? RatioOverWindow(
        CounterPoint? basePt, EngineSnapshot cur,
        Func<CounterPoint, long?> num, Func<EngineSnapshot, long?> numCur,
        Func<CounterPoint, long?> den, Func<EngineSnapshot, long?> denCur)
    {
        if (basePt is null) return null;
        var dNum = DeltaLong(num(basePt), numCur(cur));
        var dDen = DeltaLong(den(basePt), denCur(cur));
        if (dNum is null || dDen is null || dDen <= 0) return null;
        return (double)dNum.Value / dDen.Value; // double, иначе целочисленное деление (90/900=0)
    }

    // Скорость на поток за окно: Δсчётчика/Δсуммы времени; basePt — старейшая точка окна.
    // Деление на ноль/отрицательная дельта → null.
    private static double? SpeedOverWindow(
        CounterPoint? basePt, EngineSnapshot cur,
        Func<CounterPoint, double?> num, Func<EngineSnapshot, double?> numCur,
        Func<CounterPoint, double?> den, Func<EngineSnapshot, double?> denCur)
    {
        if (basePt is null) return null;
        var dNum = DeltaDouble(num(basePt), numCur(cur));
        var dDen = DeltaDouble(den(basePt), denCur(cur));
        if (dNum is null || dDen is null || dDen <= 0) return null;
        return dNum.Value / dDen.Value;
    }

    // Current: status по running/waiting, KV-блок (hit-доли и скорость за окно), preemptions за окно.
    private void UpdateCurrent(DateTimeOffset now, EngineSnapshot snap, string? engine, string? model, int? tensorParallel)
    {
        var busy = (snap.Running ?? 0) > 0 || (snap.Waiting ?? 0) > 0;
        var status = busy ? "busy" : "working";

        var base60 = OldestInWindow(now, StreamWindow);
        var base15 = OldestInWindow(now, _options.CurrentValue.PreemptionsWindow);

        var kv = new LocalModelStatsKv
        {
            UsagePerc = snap.KvUsagePerc,
            RamTierPerc = snap.RamTierPerc,
            PrefixHitPerc = RatioOverWindow(base60, snap,
                p => p.PrefixCacheHits, s => s.PrefixCacheHits,
                p => p.PrefixCacheQueries, s => s.PrefixCacheQueries),
            RamTierHitPerc = RatioOverWindow(base60, snap,
                p => p.RamTierHits, s => s.RamTierHits,
                p => p.RamTierQueries, s => s.RamTierQueries),
            Preemptions = PreemptionsOverWindow(base15, snap),
            // Знаменатель — inter_token_latency (пишется на каждом шаге), а не decode_time
            // (только при завершении запроса): окна числителя/знаменателя совпадают, и при
            // идущей генерации «—» не бывает (ADR-029).
            GenPerStream = SpeedOverWindow(base60, snap,
                p => p.GenerationTokens, s => (double?)s.GenerationTokens,
                p => p.InterTokenLatencySum, s => s.InterTokenLatencySum),
            PrefillPerStream = SpeedOverWindow(base60, snap,
                p => p.PrefillKvComputedTokens, s => s.PrefillKvComputedTokens,
                p => p.PrefillTimeSum, s => s.PrefillTimeSum),
        };

        SetState(new LocalModelStatsCurrent
        {
            Status = status,
            DownSince = _downSince,
            Running = snap.Running,
            Waiting = snap.Waiting,
            Speed = ThroughputOf(),
            Kv = kv,
            Unsupported = snap.Unsupported,
            Engine = engine,
            Model = model,
            TensorParallel = tensorParallel,
            Configured = true,
        });
    }

    // Дельта счётчика preemptions за PreemptionsWindow (15 мин). basePt — старейшая точка окна.
    private static long? PreemptionsOverWindow(CounterPoint? basePt, EngineSnapshot cur)
    {
        if (basePt is null || basePt.Preemptions is not { } a || cur.Preemptions is not { } b) return null;
        var d = b - a;
        return d < 0 ? null : d;
    }

    // Ток/с суммарно: живое «сейчас» — дельта от предыдущего опроса (каждый опрос, не только
    // на точке кольца: кольцо прорежено по PointStep, и его последняя точка может быть до 5 с старше).
    // Кэшируется в UpdateRing, чтобы не зависеть от момента вызова. null — счётчика нет или разрыв.
    private double? ThroughputOf() => _liveThroughput;

    private void SetState(LocalModelStatsCurrent snapshot)
    {
        lock (_gate)
        {
            _current = snapshot;
        }
    }

    // Ожидание: по умолчанию Task.Delay; тест подменяет WaitFunc, чтобы гонять цикл
    // на фейковых часах. OperationCanceledException пробрасывается (нормальная остановка).
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
