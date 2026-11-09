namespace ClaudeHomeServer.Services.Llm;

// Итог завершённого хода на локальной модели: сумма по запросам хода. Числа nullable —
// движок не отдал поле (на экране «—»). QueueMsApprox — оценка времени в очереди в
// МИЛЛИСЕКУНДАХ, округлённая вверх до целой секунды (контракт ServerMessage/фронт).
public sealed record LocalModelTurnSummary
{
    // Сумма output_tokens по запросам хода.
    public int? OutTokens { get; init; }

    // Суммарное время генерации (последняя дельта − первая дельта, по всем запросам).
    public TimeSpan? GenDuration { get; init; }

    // GenTps = output_tokens / суммарная генерация; null при нулевой/отсутствующей длительности.
    public double? GenTps { get; init; }

    // TtftMs — time-to-first-token ПЕРВОГО запроса хода (в мс).
    public int? TtftMs { get; init; }

    // QueueMsApprox ≈ суммарное время фаз queued: МИЛЛИСЕКУНДЫ, округлённые вверх до целой секунды.
    public int? QueueMsApprox { get; init; }

    // CacheHitPerc по сумме токенов хода (cache_read / (input + cache_read + cache_creation)).
    public double? CacheHitPerc { get; init; }

    // ── Атрибуция по подсказочным заголовкам CLI (ADR-029, GatewayHintHeaders) ──────────
    // Разрез «основной ход / субагенты / компакция»: сколько запросов хода в каждом классе.
    // Числа 0 — класс не встречался (не null: 0 — честное «не было»).

    // Запросы основного хода (request-class=main).
    public int MainRequests { get; init; }

    // Запросы сабагентов (request-class=subagent) и workflow-агентов (request-class=workflow).
    public int SubagentRequests { get; init; }

    // Запросы-суммаризации (request-class=compaction).
    public int CompactionRequests { get; init; }

    // Боковые запросы (request-class=auxiliary: заголовки сессии, классификаторы, саммари).
    public int AuxiliaryRequests { get; init; }

    // Типы сабагентов, встреченные в ходе (agent-type): distinct, порядок первый-встреченный.
    // Пусто/null — сабагентов не было.
    public string[]? AgentTypes { get; init; }

    // Вид компакции (auto | manual | reactive), если ход её содержал; null — не было.
    public string? CompactionKind { get; init; }

    // ── Утечки вызова инструмента в текст (локальная модель, vLLM qwen3_coder) ───────────
    // Число text-блоков основного агента, в которых детектор нашёл маркеры XML-формата
    // вызова. 0 — утечек не было (не null: 0 — честное «не было»).
    // Tail — хвост после успешного tool_use (вызов был, параметр мог потеряться);
    // WholeCall — вызов целиком ушёл текстом (tool_use в сообщении не было).
    public int ToolCallLeakTail { get; init; }
    public int ToolCallLeakWholeCall { get; init; }
}

// Шов живых событий локальной модели: счётчик хода (LocalModelTurnMeter) отдаёт сюда
// тиковые снимки (OnLive) и итог завершённого хода (OnTurn). Реализацию (рассылка
// ServerMessage в чат) делает Main в шаге 19; без зарегистрированной реализации события
// уходят в никуда.
public interface ILocalModelLiveSink
{
    // Тиковый снимок: фаза (queued | prefill | generating), скорость, сколько прогонов
    // ждут GPU. НЕ чаще 1 раза в секунду на сессию (счётчик дросселирует через TimeProvider).
    void OnLive(string sessionId, string phase, double? genTps, int? waitingTotal);

    // Итог завершённого хода (TurnCompleted).
    void OnTurn(string sessionId, LocalModelTurnSummary summary);
}
