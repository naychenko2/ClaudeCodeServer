namespace ClaudeHomeServer.Services.Llm;

/// <summary>
/// Источник метрик vLLM: ходит GET в /metrics, парсит текстовый формат Prometheus
/// и заполняет <see cref="EngineSnapshot"/>. Метрики с несколькими сериями (метки
/// model_name/engine/tier) суммируются по всем сериям. Метрики, которых нет в тексте,
/// дают null в снимке и имя в <see cref="EngineSnapshot.Unsupported"/>.
///
/// HttpClient принимается в конструкторе (регистрация в DI — в следующем шаге);
/// таймаут запроса 2 с.
/// </summary>
public sealed class VllmMetricsSource : IEngineMetricsSource
{
    // Таймаут запроса: локальный vLLM отвечает быстро, но с холодной моделью может
    // задержаться; 2 с — запас над EgressProbe (400ms), но меньше паузы опроса.
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(2);

    private readonly HttpClient _http;
    private readonly string _metricsUrl;

    public VllmMetricsSource(HttpClient http, string metricsUrl)
    {
        _http = http;
        _metricsUrl = metricsUrl;
    }

    public async Task<EngineSnapshot?> ReadAsync(CancellationToken ct = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(RequestTimeout);

        string text;
        try
        {
            text = await _http.GetStringAsync(_metricsUrl, cts.Token);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            // Таймаут, ECONNREFUSED, 5xx — движок не отвечает: снимаем null.
            return null;
        }

        var series = PrometheusTextParser.Parse(text);
        return BuildSnapshot(series);
    }

    // Имена метрик vLLM (сверено с фикстурой vllm-metrics.txt). Единый список —
    // источник правды для заглушки (NullEngineMetricsSource) и тестов.
    public static readonly string[] AllMetrics =
    [
        "vllm:num_requests_running",
        "vllm:num_requests_waiting",
        "vllm:kv_cache_usage_perc",
        "vllm:kv_offload_cpu_cache_usage_perc",
        "vllm:prefix_cache_hits_total",
        "vllm:prefix_cache_queries_total",
        "vllm:num_preemptions_total",
        "vllm:generation_tokens_total",
        "vllm:prompt_tokens_total",
        "vllm:request_decode_time_seconds_sum",
        "vllm:request_prefill_time_seconds_sum",
        "vllm:request_prefill_kv_computed_tokens_sum",
        "vllm:kv_offload_tiering_chunk_hits_total",
        "vllm:kv_offload_tiering_chunk_queries_total",
        "vllm:inter_token_latency_seconds_sum",
        "vllm:inter_token_latency_seconds_count",
        "vllm:request_queue_time_seconds_sum",
    ];

    // Маппинг HTTP-имя метрики → каноническое имя поля EngineSnapshot. Во front
    // приходит именно каноническое имя (Unsupported скрывает блоки по нему, а не
    // по сырому «vllm:*» — см. ThroughputChart.hiddenField). Все поля перечислены
    // в том же порядке, что AllMetrics.
    public static readonly (string Metric, string Field)[] MetricFields =
    [
        (AllMetrics[0], nameof(EngineSnapshot.Running)),
        (AllMetrics[1], nameof(EngineSnapshot.Waiting)),
        (AllMetrics[2], nameof(EngineSnapshot.KvUsagePerc)),
        (AllMetrics[3], nameof(EngineSnapshot.RamTierPerc)),
        (AllMetrics[4], nameof(EngineSnapshot.PrefixCacheHits)),
        (AllMetrics[5], nameof(EngineSnapshot.PrefixCacheQueries)),
        (AllMetrics[6], nameof(EngineSnapshot.Preemptions)),
        (AllMetrics[7], nameof(EngineSnapshot.GenerationTokens)),
        (AllMetrics[8], nameof(EngineSnapshot.PromptTokens)),
        (AllMetrics[9], nameof(EngineSnapshot.DecodeTimeSum)),
        (AllMetrics[10], nameof(EngineSnapshot.PrefillTimeSum)),
        (AllMetrics[11], nameof(EngineSnapshot.PrefillKvComputedTokens)),
        (AllMetrics[12], nameof(EngineSnapshot.RamTierHits)),
        (AllMetrics[13], nameof(EngineSnapshot.RamTierQueries)),
        (AllMetrics[14], nameof(EngineSnapshot.InterTokenLatencySum)),
        (AllMetrics[15], nameof(EngineSnapshot.InterTokenLatencyCount)),
        (AllMetrics[16], nameof(EngineSnapshot.QueueTimeSum)),
    ];

    // Канонические имена всех полей vLLM-адаптера (в порядке MetricFields).
    public static readonly string[] CanonicalFields =
        Array.ConvertAll(MetricFields, m => m.Field);

    private static EngineSnapshot BuildSnapshot(PrometheusSeries series)
    {
        var unsupported = new List<string>();

        string Field(int i) => MetricFields[i].Field;

        // Одиночная метрика (одна серия): значение или null + каноническое имя в Unsupported.
        double? Single(int i)
        {
            var v = series.GetValue(AllMetrics[i]);
            if (v is null)
                unsupported.Add(Field(i));
            return v;
        }

        // Метрика с несколькими сериями (метки tier/engine/model_name): сумма по всем.
        double? Multi(int i)
        {
            var v = series.Sum(AllMetrics[i]);
            if (v is null)
                unsupported.Add(Field(i));
            return v;
        }

        // Gauge с процентом (kv_cache_usage_perc, kv_offload_cpu_cache_usage_perc):
        // при нескольких сериях берём МАКСИМУМ (худшая загрузка), а не первую серию.
        double? GaugeMax(int i)
        {
            var v = series.Max(AllMetrics[i]);
            if (v is null)
                unsupported.Add(Field(i));
            return v;
        }

        return new EngineSnapshot
        {
            Running = ToInt(Single(0)),
            Waiting = ToInt(Single(1)),
            KvUsagePerc = GaugeMax(2),
            RamTierPerc = GaugeMax(3),
            PrefixCacheHits = ToLong(Single(4)),
            PrefixCacheQueries = ToLong(Single(5)),
            Preemptions = ToLong(Single(6)),
            GenerationTokens = ToLong(Single(7)),
            PromptTokens = ToLong(Single(8)),
            DecodeTimeSum = Single(9),
            PrefillTimeSum = Single(10),
            PrefillKvComputedTokens = Single(11),
            RamTierHits = ToLong(Multi(12)),
            RamTierQueries = ToLong(Multi(13)),
            InterTokenLatencySum = Single(14),
            InterTokenLatencyCount = ToLong(Single(15)),
            QueueTimeSum = Single(16),
            Unsupported = unsupported,
        };
    }

    private static int? ToInt(double? v) => v is null ? null : (int)v!.Value;
    private static long? ToLong(double? v) => v is null ? null : (long)v!.Value;
}
