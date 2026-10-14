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

// Точка кольца истории: момент, скорость, running, waiting.
public sealed record LocalModelStatsPoint
{
    public DateTimeOffset Timestamp { get; init; }

    // Ток/с суммарно по стенду (Δgeneration_tokens/Δвремя). null — разрыв (рестарт движка).
    public double? GenThroughput { get; init; }

    public int? Running { get; init; }
    public int? Waiting { get; init; }
}

// Публичный API поллера (для контроллера шага 11). Чтение без блокировки фоновых циклов:
// Current — атомарный снимок, GetHistory — копия среза кольца.
public interface ILocalModelStatsReader
{
    LocalModelStatsCurrent Current { get; }

    // Точки кольца за окно `window` (от настоящего момента назад); sinceUnixMs — фильтр
    // «только новее этого момента» (unix-мс). Пусто — окно пустое или движок не ходил.
    IReadOnlyList<LocalModelStatsPoint> GetHistory(TimeSpan window, long? sinceUnixMs = null);
}
