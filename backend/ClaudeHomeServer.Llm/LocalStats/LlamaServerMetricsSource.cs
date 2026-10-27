namespace ClaudeHomeServer.Services.Llm;

/// <summary>
/// Источник метрик llama-server (стенд дежурной модели): ходит GET в /metrics (флаг
/// --metrics движка), разбирает текст через <see cref="PrometheusTextParser"/> и заполняет
/// <see cref="EngineSnapshot"/> по таблице соответствия (разрез v2, §2).
///
/// Отличия от vLLM-адаптера (сверено с GET 192.168.7.107:18030/metrics живого стенда 27.10):
/// - счётчики растут по завершении запроса, гистограмм нет — на коротком окне график рывками;
/// - часть блоков снимка llama-server не отдаёт ни при какой конфигурации: они перечислены
///   в <see cref="UnsupportedBlocks"/> и попадают в Unsupported при любом ответе;
/// - метрики, которой нет в тексте, дают null в поле и имя поля в Unsupported.
///
/// Ответ движка — недоверенный: мусорный или пустый текст не бросает исключения, снимок
/// остаётся с null-полями (ADR-029 «Недоверенный внешний ответ»).
///
/// HttpClient принимается в конструкторе (дорожка и регистрация клиента — шаг Б2b);
/// таймаут запроса 2 с, как у vLLM-адаптера.
/// </summary>
public sealed class LlamaServerMetricsSource : IEngineMetricsSource
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(2);

    private readonly HttpClient _http;
    private readonly string _metricsUrl;

    public LlamaServerMetricsSource(HttpClient http, string metricsUrl)
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
            // Таймаут, ECONNREFUSED (стенд выгружен арбитром), 5xx — движок не отвечает.
            return null;
        }

        var series = PrometheusTextParser.Parse(text);
        return BuildSnapshot(series);
    }

    // Метрики llama-server и поля снимка, которые они наполняют (разрез v2, §2).
    private const string RequestsProcessing = "llamacpp:requests_processing";                    // Running
    private const string RequestsDeferred = "llamacpp:requests_deferred";                        // Waiting
    private const string TokensPredictedTotal = "llamacpp:tokens_predicted_total";              // GenerationTokens
    private const string PromptTokensTotal = "llamacpp:prompt_tokens_total";                    // PromptTokens (без кэша)
    private const string PromptTokensCachedTotal = "llamacpp:prompt_tokens_cached_total";       // PrefixCacheHits
    private const string TokensPredictedSecondsTotal = "llamacpp:tokens_predicted_seconds_total"; // InterTokenLatencySum
    private const string PromptSecondsTotal = "llamacpp:prompt_seconds_total";                  // PrefillTimeSum

    // Полный список читаемых метрик — источник правды для тестов.
    public static readonly string[] AllMetrics =
    [
        RequestsProcessing,
        RequestsDeferred,
        TokensPredictedTotal,
        PromptTokensTotal,
        PromptTokensCachedTotal,
        TokensPredictedSecondsTotal,
        PromptSecondsTotal,
    ];

    // Блоки снимка, которых llama-server не отдаёт никогда (в /metrics живого стенда их нет):
    // добавляются в Unsupported при любом ответе, фронт прячет эти блоки. Имена — поля
    // EngineSnapshot (их читает фронт), а не метрики движка.
    public static readonly string[] UnsupportedBlocks =
    [
        nameof(EngineSnapshot.KvUsagePerc),
        nameof(EngineSnapshot.RamTierPerc),
        nameof(EngineSnapshot.RamTierHits),
        nameof(EngineSnapshot.RamTierQueries),
        nameof(EngineSnapshot.Preemptions),
        nameof(EngineSnapshot.QueueTimeSum),
        nameof(EngineSnapshot.PrefillKvComputedTokens),
        nameof(EngineSnapshot.DecodeTimeSum),
    ];

    private static EngineSnapshot BuildSnapshot(PrometheusSeries series)
    {
        var unsupported = new List<string>(UnsupportedBlocks);

        // Метрики llama-server одиночные (без меток), как Single() в vLLM-адаптере:
        // нет в тексте → null в поле и имя поля в Unsupported.
        double? Single(string metric, string field)
        {
            var v = series.GetValue(metric);
            if (v is null)
                unsupported.Add(field);
            return v;
        }

        var running = Single(RequestsProcessing, nameof(EngineSnapshot.Running));
        var waiting = Single(RequestsDeferred, nameof(EngineSnapshot.Waiting));
        var generation = Single(TokensPredictedTotal, nameof(EngineSnapshot.GenerationTokens));
        var prompt = Single(PromptTokensTotal, nameof(EngineSnapshot.PromptTokens));
        var genSeconds = Single(TokensPredictedSecondsTotal, nameof(EngineSnapshot.InterTokenLatencySum));
        var promptSeconds = Single(PromptSecondsTotal, nameof(EngineSnapshot.PrefillTimeSum));

        // Доля кэша промпта — ПАРА счётчиков: hits = prompt_tokens_cached_total,
        // queries = prompt_tokens_total + cached (первая метрика отдаёт токены БЕЗ кэша).
        // Половины пары нет: если одного из двух нет, оба поля null и оба имени в
        // Unsupported — иначе доля кэша врёт (0 попаданий при неизвестных запросах).
        long? prefixHits = null;
        long? prefixQueries = null;
        var cached = series.GetValue(PromptTokensCachedTotal);
        if (cached is not null && prompt is not null)
        {
            prefixHits = ToLong(cached);
            prefixQueries = ToLong(cached!.Value + prompt.Value);
        }
        else
        {
            unsupported.Add(nameof(EngineSnapshot.PrefixCacheHits));
            unsupported.Add(nameof(EngineSnapshot.PrefixCacheQueries));
        }

        return new EngineSnapshot
        {
            Running = ToInt(running),
            Waiting = ToInt(waiting),
            PrefixCacheHits = prefixHits,
            PrefixCacheQueries = prefixQueries,
            GenerationTokens = ToLong(generation),
            PromptTokens = ToLong(prompt),
            // Подмена смысла (разрез v2, §2): у llama это сумма секунд генерации по слотам,
            // а не сумма inter-token latency по шагам, как у vLLM. Знаменатель GenPerStream
            // в EngineStatsTrack — Δтокены/Δсекунды, то есть ток/с на поток: по этой сумме
            // он остаётся корректным. Но «токенов на шаг» по ней не вывести — поэтому ниже
            // InterTokenLatencyCount null.
            InterTokenLatencySum = genSeconds,
            // «Токенов на шаг» (EngineStatsTrack.SpeculativeSpeedup) из llama-счётчиков не
            // получится: llamacpp:n_decode_total считает и llama_decode() батчи промпта, не
            // только шаги генерации, поэтому знаменатель остаётся null (разрез v2, §2).
            InterTokenLatencyCount = null,
            PrefillTimeSum = promptSeconds,
            Unsupported = unsupported,
        };
    }

    // Прочее из /metrics llama-server в снимок не берётся: n_tokens_max, n_decode_total,
    // spec_decode_* и gauge средних скоростей (prompt_tokens_seconds, predicted_tokens_seconds,
    // n_busy_slots_per_decode) — это средние с запуска движка, а не за окно наблюдения.
    private static int? ToInt(double? v) => v is null ? null : (int)v!.Value;
    private static long? ToLong(double? v) => v is null ? null : (long)v!.Value;
}
