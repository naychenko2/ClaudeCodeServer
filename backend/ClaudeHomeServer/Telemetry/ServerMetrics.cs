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
    ///
    /// Тега <c>direction</c> (input/output/cache_read/cache_creation) здесь нет намеренно:
    /// он размечает ТОКЕНЫ, а учёт токенов в OTel запрещён решением C4 (source of truth —
    /// SpendStore). Ни один Record*-метод его и не принимал — разрешение висело мёртвым
    /// и противоречило собственному тесту <c>ServerMetrics_HasNoTokenMetrics</c>.
    /// </summary>
    public static readonly HashSet<string> AllowedTags = new()
    {
        "provider",    // claude, deepseek, glm, ollama, ...
        "model",       // claude-sonnet-4-5, glm-4, ...
        "execution",   // local | docker — среда исполнения хода (ровно два значения)
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

    /// <summary>
    /// Явные границы бакетов для <see cref="LlmDuration"/> (мс). Регистрируются как View
    /// в ObservabilityExtensions.
    ///
    /// Зачем: дефолтные границы OTel заканчиваются на 10 000 мс, а ход LLM идёт от единиц
    /// до сотен секунд — практически ВСЕ замеры падали в последний бакет (10000, +Inf].
    /// Квантили считаются интерполяцией по бакетам, поэтому p95/p99 упирались в 10 000
    /// и не различали ход на 30 секунд и ход на 10 минут: метрика формально была,
    /// а ответить «какие ходы самые долгие» ею было нельзя (на живых данных p99 = 9975).
    ///
    /// Шкала до 20 минут с сгущением на 5–60 с — там основная масса ходов.
    /// </summary>
    public static readonly double[] LlmDurationBoundaries =
        [1_000, 2_500, 5_000, 10_000, 20_000, 30_000, 60_000, 120_000, 300_000, 600_000, 1_200_000];

    // ── Counters ─────────────────────────────────────────────────────────────
    // unit по конвенции OTel: для счётчиков событий — фигурные скобки с сущностью,
    // которую считаем ({error}, {call}); это не единица измерения, а пометка смысла.

    public static readonly Counter<long> LlmErrors = _meter.CreateCounter<long>(
        "ccs.llm.errors",
        unit: "{error}",
        description: "Ошибки LLM-провайдеров");

    public static readonly Counter<long> LlmRateLimitHits = _meter.CreateCounter<long>(
        "ccs.llm.rate_limit_hits",
        unit: "{hit}",
        description: "Срабатывания rate-limit");

    public static readonly Counter<long> McpCalls = _meter.CreateCounter<long>(
        "ccs.mcp.calls",
        unit: "{call}",
        description: "Вызовы MCP-инструментов");

    public static readonly Counter<long> McpErrors = _meter.CreateCounter<long>(
        "ccs.mcp.errors",
        unit: "{error}",
        description: "Ошибки MCP-инструментов");

    public static readonly Counter<long> DifySyncErrors = _meter.CreateCounter<long>(
        "ccs.dify.sync.errors",
        unit: "{error}",
        description: "Ошибки синхронизации с Dify (DiffSync)");

    public static readonly Counter<long> TelemetryHeartbeat = _meter.CreateCounter<long>(
        "ccs.telemetry.heartbeat",
        unit: "{tick}",
        description: "Heartbeat телеметрии — если остановился, pipeline сломан");

    // Знакомство вместо обязательного онбординга (фича default-personas-onboarding, план 2.10).
    // После снятия гейта доля прохождения — единственный способ узнать, не убило ли изменение
    // персонализацию. Без разрезов по пользователю (PII): только факт события.
    public static readonly Counter<long> IntroStarted = _meter.CreateCounter<long>(
        "ccs.intro.started",
        unit: "{event}",
        description: "Начато знакомство (создана онбординг-сессия)");

    public static readonly Counter<long> IntroCompleted = _meter.CreateCounter<long>(
        "ccs.intro.completed",
        unit: "{event}",
        description: "Знакомство завершено (дефолт назначен из онбординг-сессии)");

    // Решение по каркасу проекта (знакомство v2): применение или отказ. Тег reason —
    // ключ пресета либо "none" (отказ): без разбивки не посчитать долю принявших каркас
    // и не решить про снятие флага. Значения из кода (каталог + зарезервированное "none"),
    // не снаружи — ограничитель кардинальности не нужен, но форму держим через MetricTagGuard.
    public static readonly Counter<long> IntroPresetApplied = _meter.CreateCounter<long>(
        "ccs.intro.preset_applied",
        unit: "{event}",
        description: "Решение по каркасу проекта: применён (reason=ключ пресета) или отклонён (reason=none)");

    // ── Recording API ────────────────────────────────────────────────────────
    // Строковые параметры = теги (camelCase ↔ snake_case ↔ AllowedTags).
    // Числовой параметр (где есть) = скалярное значение метрики.
    //
    // Имена тегов ограничены allowlist'ом выше, ЗНАЧЕНИЯ — через MetricTagGuard:
    // tool_name и model приходят снаружи (заголовок MCP-сервера, поле сессии из PUT),
    // и без ограничителя каждое новое значение заводит вечный ряд в ClickHouse.

    /// <summary>
    /// Записать длительность хода LLM. Теги захардкожены в сигнатуре.
    ///
    /// <paramref name="execution"/> — среда исполнения хода (<c>local</c>/<c>docker</c>,
    /// см. <see cref="TurnTelemetry.ExecutionKind"/>). Ограничитель значений ей не нужен:
    /// значений ровно два и берутся они из кода, а не снаружи.
    /// </summary>
    public static void RecordLlmDuration(
        double durationMs,
        string provider,
        string model,
        string outcome = "success",
        string execution = "local")
    {
        LlmDuration.Record(durationMs,
            new KeyValuePair<string, object?>("provider", provider),
            new KeyValuePair<string, object?>("model", MetricTagGuard.Model(model)),
            new KeyValuePair<string, object?>("outcome", outcome),
            new KeyValuePair<string, object?>("execution", execution));
    }

    public static void RecordLlmError(string provider, string errorType, string execution = "local")
    {
        LlmErrors.Add(1,
            new KeyValuePair<string, object?>("provider", provider),
            new KeyValuePair<string, object?>("error_type", errorType),
            new KeyValuePair<string, object?>("execution", execution));
    }

    public static void RecordRateLimitHit(string provider)
    {
        LlmRateLimitHits.Add(1,
            new KeyValuePair<string, object?>("provider", provider));
    }

    public static void RecordMcpCall(string toolName, string outcome)
    {
        McpCalls.Add(1,
            new KeyValuePair<string, object?>("tool_name", MetricTagGuard.Tool(toolName)),
            new KeyValuePair<string, object?>("outcome", outcome));
    }

    public static void RecordMcpError(string toolName, string errorType)
    {
        McpErrors.Add(1,
            new KeyValuePair<string, object?>("tool_name", MetricTagGuard.Tool(toolName)),
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

    // Знакомство (план 2.10): без тегов — только счётчик события.
    public static void RecordIntroStarted() => IntroStarted.Add(1);
    public static void RecordIntroCompleted() => IntroCompleted.Add(1);

    /// <summary>
    /// Решение по каркасу проекта. <paramref name="reason"/> — ключ пресета ("docs" / "dev" /
    /// "personal") или "none" при отказе: значения из кода, поэтому без MetricTagGuard —
    /// но форма всё равно проверяется, чтобы случайный путь/свободный текст не завёл ряд.
    /// </summary>
    public static void RecordPresetApplied(string reason)
    {
        var tag = reason is not null && reason.Length <= 16
                  && reason.All(c => char.IsAsciiLetterOrDigit(c))
            ? reason : MetricTagGuard.Overflow;
        IntroPresetApplied.Add(1, new KeyValuePair<string, object?>("reason", tag));
    }

    // ObservableGauges (sessions.active, websocket.connections) регистрируются
    // извне в задаче T9 через _meter.CreateObservableGauge — здесь только декларация
    // Meter-поля выше. Не плодить их здесь, чтобы не тащить runtime-зависимости.
}
