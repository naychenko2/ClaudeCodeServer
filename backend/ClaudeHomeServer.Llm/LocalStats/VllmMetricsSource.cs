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
    ];

    private static EngineSnapshot BuildSnapshot(PrometheusSeries series)
    {
        var unsupported = new List<string>();

        // Одиночная метрика (одна серия): значение или null + имя в Unsupported.
        double? Single(string name)
        {
            var v = series.GetValue(name);
            if (v is null)
                unsupported.Add(name);
            return v;
        }

        // Метрика с несколькими сериями (метки tier/engine/model_name): сумма по всем.
        double? Multi(string name)
        {
            var v = series.Sum(name);
            if (v is null)
                unsupported.Add(name);
            return v;
        }

        // Gauge с процентом (kv_cache_usage_perc, kv_offload_cpu_cache_usage_perc):
        // при нескольких сериях берём МАКСИМУМ (худшая загрузка), а не первую серию.
        double? GaugeMax(string name)
        {
            var v = series.Max(name);
            if (v is null)
                unsupported.Add(name);
            return v;
        }

        return new EngineSnapshot
        {
            Running = ToInt(Single(AllMetrics[0])),
            Waiting = ToInt(Single(AllMetrics[1])),
            KvUsagePerc = GaugeMax(AllMetrics[2]),
            RamTierPerc = GaugeMax(AllMetrics[3]),
            PrefixCacheHits = ToLong(Single(AllMetrics[4])),
            PrefixCacheQueries = ToLong(Single(AllMetrics[5])),
            Preemptions = ToLong(Single(AllMetrics[6])),
            GenerationTokens = ToLong(Single(AllMetrics[7])),
            PromptTokens = ToLong(Single(AllMetrics[8])),
            DecodeTimeSum = Single(AllMetrics[9]),
            PrefillTimeSum = Single(AllMetrics[10]),
            PrefillKvComputedTokens = Single(AllMetrics[11]),
            RamTierHits = ToLong(Multi(AllMetrics[12])),
            RamTierQueries = ToLong(Multi(AllMetrics[13])),
            InterTokenLatencySum = Single(AllMetrics[14]),
            InterTokenLatencyCount = ToLong(Single(AllMetrics[15])),
            Unsupported = unsupported,
        };
    }

    private static int? ToInt(double? v) => v is null ? null : (int)v!.Value;
    private static long? ToLong(double? v) => v is null ? null : (long)v!.Value;
}
