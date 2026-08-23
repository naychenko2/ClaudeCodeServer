namespace ClaudeHomeServer.Telemetry.Alerts;

/// <summary>
/// Активный алерт SigNoz — элемент ответа <c>GET /api/v1/alerts</c> (формат Alertmanager).
///
/// Важное свойство, снятое с живого стенда: ОДНО правило порождает СТОЛЬКО алертов,
/// сколько серий в его разрезе. Правило с groupBy по deployment.environment даёт два
/// алерта — dev и production — с одинаковым alertname, но РАЗНЫМИ fingerprint.
/// Поэтому единица дедупликации — fingerprint, а не имя правила.
/// </summary>
public sealed record SignozAlert
{
    /// <summary>Хеш набора меток. Стабилен, пока алерт горит; уникален на серию.</summary>
    public required string Fingerprint { get; init; }

    public IReadOnlyDictionary<string, string> Labels { get; init; }
        = new Dictionary<string, string>();

    public IReadOnlyDictionary<string, string> Annotations { get; init; }
        = new Dictionary<string, string>();

    public DateTimeOffset? StartsAt { get; init; }

    /// <summary>Состояние из Alertmanager: active / suppressed / unprocessed.</summary>
    public string? State { get; init; }

    /// <summary>Алерт заглушен silence-правилом — будить по нему не надо.</summary>
    public bool IsSilenced { get; init; }

    /// <summary>Имя правила. Пустым не бывает, но подстраховываемся.</summary>
    public string Name => Value("alertname") ?? "Алерт телеметрии";

    /// <summary>Контур: dev / production. null — правило без разреза по среде.</summary>
    public string? Environment => Value("deployment.environment");

    public string? Severity => Value("severity");

    /// <summary>Идентификатор правила — из него строится ссылка в UI SigNoz.</summary>
    public string? RuleId => Value("ruleId");

    /// <summary>
    /// Чат, на который указал сам алерт. Появляется у правил с разрезом по <c>chat_id</c>
    /// («Ходы массово встали»): виновник там известен из меток, а не из упавших ходов —
    /// ходы в этом инциденте успешные, просто долгие.
    /// </summary>
    public string? ChatId => Value("chat_id");

    public string? Description
        => Annotations.TryGetValue("description", out var d) && !string.IsNullOrWhiteSpace(d) ? d
         : Annotations.TryGetValue("summary", out var s) && !string.IsNullOrWhiteSpace(s) ? s
         : null;

    private string? Value(string key)
        => Labels.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v) ? v : null;
}

/// <summary>
/// Что изменилось между двумя опросами: какие алерты загорелись впервые
/// и какие погасли (исчезли из выдачи).
/// </summary>
public sealed record AlertDiff(
    IReadOnlyList<SignozAlert> Started,
    IReadOnlyList<string> Resolved)
{
    public static readonly AlertDiff Empty = new([], []);

    public bool IsEmpty => Started.Count == 0 && Resolved.Count == 0;
}
