namespace ClaudeHomeServer.Services.Llm;

/// <summary>
/// Снимок состояния движка локальной модели. Все числовые поля nullable:
/// null — движок эту метрику не сообщает (отсутствует в /metrics).
/// </summary>
public sealed record EngineSnapshot
{
    // Число запросов в execution batch.
    public int? Running { get; init; }

    // Число запросов в ожидании.
    public int? Waiting { get; init; }

    // Доля занятых блоков KV-кэша (0..1). Это НЕ заполненность памяти.
    public double? KvUsagePerc { get; init; }

    // Доля занятого пространства RAM-тиера (KV offload CPU) (0..1).
    public double? RamTierPerc { get; init; }

    // Счётчики попаданий/запросов prefix-кэша (в токенах).
    public long? PrefixCacheHits { get; init; }
    public long? PrefixCacheQueries { get; init; }

    // Счётчик прерываний (preemptions).
    public long? Preemptions { get; init; }

    // Счётчики обработанных токенов.
    public long? GenerationTokens { get; init; }
    public long? PromptTokens { get; init; }

    // Суммы гистограмм времени (сек) для скорости «на поток».
    public double? DecodeTimeSum { get; init; }
    public double? PrefillTimeSum { get; init; }

    // Сумма inter-token latency (сек): пишется на каждом шаге декодирования (в отличие
    // от DecodeTimeSum, который растёт только при завершении запроса). Знаменатель живой
    // скорости «на поток» (ADR-029): Δgeneration_tokens/Δinter_token_latency.
    public double? InterTokenLatencySum { get; init; }

    // Число событий inter-token latency (= число шагов декодирования). Коэффициент живой
    // скорости кольца Ctx (ADR-029): Δgeneration_tokens/Δinter_token_latency_count =
    // токенов на шаг (DFlash отдаёт несколько токенов за SSE-дельту).
    public long? InterTokenLatencyCount { get; init; }

    // Сумма гистограммы времени в очереди (vllm:request_queue_time_seconds_sum, сек): время,
    // пока запрос ждал принятия стендом. Пишется при завершении запроса (ADR-029, v2.3):
    // на коротких точках приходит «рывком».
    public double? QueueTimeSum { get; init; }

    // Сумма гистограммы: новые KV-токены, вычисленные в prefill (без кэша).
    public double? PrefillKvComputedTokens { get; init; }

    // Счётчики попаданий/запросов RAM-тиера (KV offload tiering chunks).
    public long? RamTierHits { get; init; }
    public long? RamTierQueries { get; init; }

    // Имена метрик, которых движок не сообщил (отсутствуют в /metrics).
    public IReadOnlyList<string> Unsupported { get; init; } = Array.Empty<string>();
}

/// <summary>
/// Источник метрик движка локальной модели. ReadAsync ходит в /metrics движка
/// и возвращает снимок; null — движок не отвечает (HTTP-ошибка/таймаут).
/// </summary>
public interface IEngineMetricsSource
{
    Task<EngineSnapshot?> ReadAsync(CancellationToken ct = default);
}
