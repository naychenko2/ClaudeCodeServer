namespace ClaudeHomeServer.Services.Llm;

// Снимок состояния локального движка для панели «Локальные модели» (контроллер — шаг 11).
// Status: "working" (нет запросов) | "busy" (running>0 или waiting>0) | "down" (HTTP-сбой)
// | "notConnected" (нет локального провайдера/Stats).
public sealed record LocalModelStatsCurrent
{
    public string Status { get; init; } = "notConnected";

    // Момент ПЕРВОГО сбоя (не сбрасывается повторными сбоями); null — движок не мёртв.
    public DateTimeOffset? DownSince { get; init; }

    public int? Running { get; init; }
    public int? Waiting { get; init; }

    // Ток/с суммарно по стенду (последняя точка кольца).
    public double? Speed { get; init; }

    public LocalModelStatsKv? Kv { get; init; }

    // Метрики, которых движок не сообщил (отсутствуют в /metrics).
    public IReadOnlyList<string> Unsupported { get; init; } = [];

    // Тип движка из секции Stats ("vllm" | "llama-server" | "ollama"); null — не задан.
    public string? Engine { get; init; }

    // Число карт тензор-параллелизма из секции Stats; null — не задано (фронт покажет число карт).
    public int? TensorParallel { get; init; }

    // Идентификатор модели стенда (из каталога провайдера); null — неизвестен.
    public string? Model { get; init; }

    // true — локальный провайдер с секцией Stats найден; false — notConnected.
    public bool Configured { get; init; }
}

// Блок KV-кэша снимка Current.
public sealed record LocalModelStatsKv
{
    // Доля занятых блоков KV-кэша (0..1), максимум по сериям.
    public double? UsagePerc { get; init; }

    // Доля занятого RAM-тиера (KV offload CPU) (0..1), максимум по сериям.
    public double? RamTierPerc { get; init; }

    // Δprefix_cache_hits/Δprefix_cache_queries за окно ~60 с (как чувствует чат).
    public double? PrefixHitPerc { get; init; }

    // ΔramTierHits/ΔramTierQueries за окно ~60 с.
    public double? RamTierHitPerc { get; init; }

    // Дельта счётчика preemptions за PreemptionsWindow (15 мин).
    public long? Preemptions { get; init; }

    // Скорость генерации на поток: Δgeneration_tokens / Δsum(decode_time) за окно ~60 с.
    // null — деление на ноль (нет decode-времени) или отрицательная дельта (рестарт движка).
    public double? GenPerStream { get; init; }

    // Скорость prefill на поток: Δprefill_kv_computed_tokens / Δsum(prefill_time) за окно ~60 с.
    // null — деление на ноль или отрицательная дельта (рестарт движка).
    public double? PrefillPerStream { get; init; }
}

// Точка кольца истории: момент, скорость, running, waiting + серии кэша (v2.1).
public sealed record LocalModelStatsPoint
{
    public DateTimeOffset Timestamp { get; init; }

    // Ток/с суммарно по стенду (Δgeneration_tokens/Δвремя). null — разрыв (рестарт движка).
    public double? GenThroughput { get; init; }

    public int? Running { get; init; }
    public int? Waiting { get; init; }

    // ─── Серии кэша (спецификация v2.1, «Что нужно от бэкенда») ─────────────────────
    //
    // Отдаём приращения счётчиков (не проценты): фронт сводит точки в интервалы
    // 15/60 с и считает долю из сумм (окна графика «Нагрузка» — 5m/15m/1h).
    // Доля за интервал из точек 5 с сложить честно нельзя (короткий запрос даст 0 %
    // или 100 %).

    // Доля занятых блоков KV-кэша (0..1): МАКСИМУМ vllm:kv_cache_usage_perc по опросам
    // с прошлой точки (при нагрузке опрос 1×/с — пик давления между точками не теряется).
    // null — метрики не было ни на одном опросе с прошлой точки.
    public double? KvUsage { get; init; }

    // Приращение счётчика prefix-кэша (vllm:prefix_cache_hits_total) с прошлой точки, токены.
    public long? PrefixHits { get; init; }

    // Приращение счётчика prefix-кэша (vllm:prefix_cache_queries_total) с прошлой точки, токены.
    public long? PrefixQueries { get; init; }

    // Приращение счётчика RAM-тиера (vllm:kv_offload_tiering_chunk_hits_total) с прошлой точки, чанки.
    public long? RamHits { get; init; }

    // Приращение счётчика RAM-тиера (vllm:kv_offload_tiering_chunk_queries_total) с прошлой точки, чанки.
    public long? RamQueries { get; init; }

    // Приращение счётчика вытеснений (vllm:num_preemptions_total) с прошлой точки, штуки.
    public long? Preemptions { get; init; }

    // Правило приращений (все пять счётчиков): null — нет прошлой точки, метрика не
    // отдана на прошлой или текущей точке, либо счётчик уменьшился (рестарт движка).
    // Отрицательных значений в JSON быть не должно.

    // ─── Скорость на поток: приращения для второй линии графика «Нагрузка и кэш» ───────
    //
    // Фронт сводит точки в интервалы (15/60 с) и считает среднюю скорость на поток:
    // ΣgenTokens/ΣitlSeconds (ток/с) — та же формула «генерации на поток» (ADR-029).
    // Приращения, а не готовая доля: как серии кэша, интервал честнее из сумм.
    // Правило null то же, что у серий кэша: нет прошлой точки, метрика не на прошлой
    // или текущей точке, либо счётчик упал (рестарт) → null; отрицательных значений нет.

    // Приращение счётчика сгенерированных токенов (vllm:generation_tokens_total) с прошлой точки.
    public long? GenTokens { get; init; }

    // Приращение суммы inter-token-времени (vllm:inter_token_latency_seconds_sum, сек) с прошлой точки.
    public double? ItlSeconds { get; init; }
}

// Средняя скорость на поток за окно (5m/15m/1h, те же окна, что у графика «Нагрузка»):
// ток/с, считаем только время, когда генерация реально шла (знаменатель — суммарное
// время движка, простой не входит). null — нет данных за окно или рестарт счётчика.
public sealed record LocalModelStatsAverage
{
    // Средняя скорость генерации на поток (ток/с) за окно: Δgeneration_tokens/Δdecode_time.
    public double? GenTps { get; init; }

    // Средняя скорость чтения промпта (ток/с) за окно: Δprefill_kv_computed_tokens/Δprefill_time.
    public double? PromptTps { get; init; }
}

// Публичный API поллера (для контроллера шага 11). Чтение без блокировки фоновых циклов:
// Current — атомарный снимок, GetHistory — копия среза кольца.
public interface ILocalModelStatsReader
{
    LocalModelStatsCurrent Current { get; }

    // Коэффициент живой скорости (ADR-029): токенов на шаг декодирования
    // (generation_tokens_total / inter_token_latency_seconds_count). DFlash отдаёт несколько
    // токенов за SSE-дельту; наблюдатель кольца Ctx масштабирует счётчик дельт этим
    // коэффициентом. null — поллер выключен или движок не отдал обе метрики (поведение
    // живого счётчика прежнее: 1 токен на дельту).
    double? TokensPerStep { get; }

    // Точки кольца за окно `window` (от настоящего момента назад); sinceUnixMs — фильтр
    // «только новее этого момента» (unix-мс). Пусто — окно пустое или движок не ходил.
    IReadOnlyList<LocalModelStatsPoint> GetHistory(TimeSpan window, long? sinceUnixMs = null);

    // Средняя скорость на поток за окно `window` (5m/15m/1h, те же окна, что у графика):
    // Δсчётчика/Δсуммы времени движка. Простой (движок молчит) в среднее не входит;
    // нет данных за окно или рестарт счётчика (отрицательная дельта) → null, не 0.
    LocalModelStatsAverage GetAverageSpeed(TimeSpan window);
}
