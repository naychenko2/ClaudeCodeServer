namespace ClaudeHomeServer.Telemetry.Incidents;

/// <summary>Состояние сбора досье — от него зависит, что показывает карточка.</summary>
public enum IncidentStatus
{
    /// <summary>Досье собрано.</summary>
    Ok,

    /// <summary>Телеметрия не настроена (нет ключа SigNoz) — это не авария, а выключенный раздел.</summary>
    NotConfigured,

    /// <summary>SigNoz настроен, но не ответил: не поднят, перезапускается, таймаут.</summary>
    Unavailable,
}

/// <summary>Строка списка инцидентов: горящий алерт или недавно погасший.</summary>
public sealed record IncidentSummary(
    string Fingerprint,
    string Title,
    string? Description,
    string? Severity,
    string? Environment,
    DateTimeOffset? StartedAt,
    DateTimeOffset? ResolvedAt,
    bool IsFiring,
    // Заглушён человеком: в списке остаётся, в счётчик и в push не идёт
    bool IsMuted = false);

/// <summary>Строка разреза: значение тега и число срабатываний за окно.</summary>
public sealed record IncidentBreakdownRow(string Label, double Count);

/// <summary>Упавший ход из трейсов.</summary>
public sealed record IncidentTurn(
    string TraceId,
    string? ChatId,
    DateTimeOffset? At,
    string? Model,
    string? Provider,
    string? ErrorType,
    long DurationMs);

/// <summary>Строка лога уровня Warning/Error за окно инцидента.</summary>
public sealed record IncidentLogLine(DateTimeOffset? At, string Severity, string Message);

/// <summary>
/// Затронутый чат с локальным контекстом: то, чего в телеметрии нет и быть не должно
/// (имя чата, проект, расход, отказы MCP). Ради этой склейки досье и затевалось —
/// SigNoz отвечает «что», локальные сторы «почему».
/// </summary>
public sealed record IncidentChat(
    string ChatId,
    string? ProjectId,
    string? Title,
    int Failures,
    long TotalTokens,
    IReadOnlyList<string> McpFailures);

/// <summary>
/// Досье по инциденту. Собирается ДЕТЕРМИНИРОВАННО, без участия модели: LLM зовётся
/// только по кнопке «Объяснить» (место <c>incident-explain</c>).
/// </summary>
public sealed record IncidentDossier
{
    public required IncidentSummary Incident { get; init; }

    public IncidentStatus Status { get; init; } = IncidentStatus.Ok;

    /// <summary>Окно, за которое собраны данные.</summary>
    public DateTimeOffset From { get; init; }
    public DateTimeOffset To { get; init; }

    /// <summary>
    /// Алерт другого контура: локальных чатов по нему нет и не будет (боевой SigNoz
    /// видит оба инстанса). Карточка обязана сказать это прямо, а не показать пустой список.
    /// </summary>
    public bool IsForeignEnvironment { get; init; }

    public IReadOnlyList<IncidentBreakdownRow> Breakdown { get; init; } = [];

    /// <summary>По какому тегу сделан разрез (error_type, tool_name…).</summary>
    public string BreakdownTag { get; init; } = "";

    public IReadOnlyList<IncidentTurn> Turns { get; init; } = [];

    /// <summary>Сколько упавших ходов всего за окно (Turns усечён до десяти).</summary>
    public int TurnsTotal { get; init; }

    public IReadOnlyList<IncidentLogLine> Logs { get; init; } = [];

    public int LogsTotal { get; init; }

    public IReadOnlyList<IncidentChat> Chats { get; init; } = [];

    /// <summary>
    /// Путь правила ВНУТРИ SigNoz, относительный: <c>/alerts/overview?ruleId=…</c>.
    /// Абсолютной ссылки здесь нет намеренно — <c>SignozUrl</c> это адрес, по которому
    /// в SigNoz ходит БЭКЕНД (обычно localhost), и в браузере пользователя он не
    /// открывается: на боевом инстансе такая ссылка вела в никуда. Фронт клеит этот
    /// путь со своим пробросом и открывает правило на соседней вкладке раздела.
    /// </summary>
    public string? RulePath { get; init; }
}
