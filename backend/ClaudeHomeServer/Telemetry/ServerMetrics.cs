using System.Diagnostics.Metrics;

namespace ClaudeHomeServer.Telemetry;

/// <summary>
/// Типизированный фасад над OTel Meter. Запрещает ad-hoc теги — все теги
/// захардкожены в сигнатурах методов, что предотвращает cardinality bomb
/// и утечку PII (user_id, session_id, file_path).
///
/// Дисциплина тегов (enforced contract, см. MetricTagAllowlistTests):
/// - Каждый строковый параметр public static Record* метода = имя тега в camelCase.
/// - snake_case варианта этого имени обязан состоять в <see cref="AllowedTags"/>.
/// - Скалярное значение метрики (duration, count) — всегда числовой параметр.
///
/// Архитектурное решение (C4): токены НЕ учитываются здесь — SpendStore
/// (JSONL) остаётся source of truth для billing. OTel = операционные метрики
/// (latency, error rates, rate-limiting), не бухгалтерия.
/// </summary>
public static class ServerMetrics
{
    public const string MeterName = "ClaudeHomeServer.Core";
    public const string MeterVersion = "1.0";

    /// <summary>
    /// Разрешённые теги для метрик. Любой тег ВНЕ этого списка = баг.
    /// Состав контролируется тестом <c>AllowedTags_ContainsExactly_ExpectedSet</c>.
    /// </summary>
    public static readonly HashSet<string> AllowedTags = new()
    {
        "provider",    // claude, deepseek, glm, ollama, ...
        "model",       // claude-sonnet-4-5, glm-4, ...
        "direction",   // input, output, cache_read, cache_creation
        "tool_name",   // идентификатор MCP-инструмента (≤80-90 значений)
        "outcome",     // success, error, timeout
        "error_type",  // rate_limit, network, auth, ...
        "reason",      // ошибки Dify-синхронизации: 401, 404, 429, timeout, other
    };

    private static readonly Meter _meter = new(MeterName, MeterVersion);

    /// <summary>Доступ к Meter для регистрации ObservableGauges (T9).</summary>
    public static Meter MeterInstance => _meter;

    // ── Histograms ──────────────────────────────────────────────────────────

    public static readonly Histogram<double> LlmDuration = _meter.CreateHistogram<double>(
        "ccs.llm.duration",
        unit: "ms",
        description: "Длительность хода LLM (ms)");

    // ── Counters ─────────────────────────────────────────────────────────────

    public static readonly Counter<long> LlmErrors = _meter.CreateCounter<long>(
        "ccs.llm.errors",
        description: "Ошибки LLM-провайдеров");

    public static readonly Counter<long> LlmRateLimitHits = _meter.CreateCounter<long>(
        "ccs.llm.rate_limit_hits",
        description: "Срабатывания rate-limit");

    public static readonly Counter<long> McpCalls = _meter.CreateCounter<long>(
        "ccs.mcp.calls",
        description: "Вызовы MCP-инструментов");

    public static readonly Counter<long> McpErrors = _meter.CreateCounter<long>(
        "ccs.mcp.errors",
        description: "Ошибки MCP-инструментов");

    public static readonly Counter<long> DifySyncErrors = _meter.CreateCounter<long>(
        "ccs.dify.sync.errors",
        description: "Ошибки синхронизации с Dify (DiffSync)");

    public static readonly Counter<long> TelemetryHeartbeat = _meter.CreateCounter<long>(
        "ccs.telemetry.heartbeat",
        description: "Heartbeat телеметрии — если остановился, pipeline сломан");

    // ── Recording API ────────────────────────────────────────────────────────
    // Строковые параметры = теги (camelCase ↔ snake_case ↔ AllowedTags).
    // Числовой параметр (где есть) = скалярное значение метрики.

    /// <summary>Записать длительность хода LLM. Теги захардкожены в сигнатуре.</summary>
    public static void RecordLlmDuration(
        double durationMs,
        string provider,
        string model,
        string outcome = "success")
    {
        LlmDuration.Record(durationMs,
            new KeyValuePair<string, object?>("provider", provider),
            new KeyValuePair<string, object?>("model", model),
            new KeyValuePair<string, object?>("outcome", outcome));
    }

    public static void RecordLlmError(string provider, string errorType)
    {
        LlmErrors.Add(1,
            new KeyValuePair<string, object?>("provider", provider),
            new KeyValuePair<string, object?>("error_type", errorType));
    }

    public static void RecordRateLimitHit(string provider)
    {
        LlmRateLimitHits.Add(1,
            new KeyValuePair<string, object?>("provider", provider));
    }

    public static void RecordMcpCall(string toolName, string outcome)
    {
        McpCalls.Add(1,
            new KeyValuePair<string, object?>("tool_name", toolName),
            new KeyValuePair<string, object?>("outcome", outcome));
    }

    public static void RecordMcpError(string toolName, string errorType)
    {
        McpErrors.Add(1,
            new KeyValuePair<string, object?>("tool_name", toolName),
            new KeyValuePair<string, object?>("error_type", errorType));
    }

    public static void RecordDifySyncError(string reason)
    {
        DifySyncErrors.Add(1,
            new KeyValuePair<string, object?>("reason", reason));
    }

    public static void RecordHeartbeat()
    {
        TelemetryHeartbeat.Add(1);
    }

    // ObservableGauges (sessions.active, websocket.connections) регистрируются
    // извне в задаче T9 через _meter.CreateObservableGauge — здесь только декларация
    // Meter-поля выше. Не плодить их здесь, чтобы не тащить runtime-зависимости.
}
