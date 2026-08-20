using System.Text.Json;

namespace ClaudeHomeServer.Telemetry.Alerts;

/// <summary>
/// Чистая логика алертинга: разбор ответа SigNoz, вычисление изменений между опросами
/// и тексты уведомлений. Без сети и без состояния — всё здесь под тестом.
/// </summary>
public static class AlertDigest
{
    /// <summary>
    /// Разбирает ответ <c>GET /api/v1/alerts</c>. Битый или неожиданный JSON —
    /// это пустой список, а не исключение: фоновый опрос не должен падать из-за
    /// того, что SigNoz ответил чем-то новым после обновления.
    /// </summary>
    public static IReadOnlyList<SignozAlert> Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("data", out var data)
                || data.ValueKind != JsonValueKind.Array)
                return [];

            var result = new List<SignozAlert>();
            foreach (var item in data.EnumerateArray())
            {
                if (ParseOne(item) is { } alert) result.Add(alert);
            }
            return result;
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static SignozAlert? ParseOne(JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object) return null;

        // Без fingerprint дедупликация невозможна — такой алерт пришлось бы слать
        // на каждом тике. Пропускаем: лучше промолчать, чем спамить.
        var fingerprint = String(item, "fingerprint");
        if (string.IsNullOrEmpty(fingerprint)) return null;

        var state = (string?)null;
        var silenced = false;
        if (item.TryGetProperty("status", out var status) && status.ValueKind == JsonValueKind.Object)
        {
            state = String(status, "state");
            silenced = status.TryGetProperty("silencedBy", out var by)
                       && by.ValueKind == JsonValueKind.Array
                       && by.GetArrayLength() > 0;
        }

        return new SignozAlert
        {
            Fingerprint = fingerprint,
            Labels = Map(item, "labels"),
            Annotations = Map(item, "annotations"),
            StartsAt = Time(item, "startsAt"),
            State = state,
            IsSilenced = silenced,
        };
    }

    /// <summary>
    /// Алерты, о которых имеет смысл уведомлять: активные и не заглушенные.
    /// Заглушённые (silence в SigNoz) пропускаем сознательно — их выключил человек.
    /// </summary>
    /// <param name="environments">
    /// Ограничение по контурам (<c>deployment.environment</c>). Пусто — берём все.
    /// Алерт без метки контура проходит всегда: правило без разреза по среде касается
    /// инсталляции целиком, и отфильтровать его — значит промолчать о том, что важно.
    /// </param>
    public static IReadOnlyList<SignozAlert> Actionable(
        IEnumerable<SignozAlert> alerts, IReadOnlyCollection<string>? environments = null)
        => alerts.Where(a => !a.IsSilenced
                          && (a.State is null
                              || a.State.Equals("active", StringComparison.OrdinalIgnoreCase))
                          && MatchesEnvironment(a, environments))
                 .ToList();

    private static bool MatchesEnvironment(SignozAlert alert, IReadOnlyCollection<string>? environments)
    {
        if (environments is null || environments.Count == 0) return true;
        if (alert.Environment is not { } env) return true;
        return environments.Contains(env, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Сравнивает текущую выдачу с уже известными отпечатками.
    ///
    /// «Погас» = ИСЧЕЗ из выдачи. По полю endsAt судить нельзя: Alertmanager держит там
    /// время в БУДУЩЕМ и продлевает его, пока алерт горит (снято с живого стенда:
    /// startsAt 14:27 при endsAt 14:31 у активного алерта).
    /// </summary>
    public static AlertDiff Diff(IReadOnlyList<SignozAlert> current, IReadOnlySet<string> known)
    {
        var live = new HashSet<string>(current.Select(a => a.Fingerprint), StringComparer.Ordinal);

        var started = current.Where(a => !known.Contains(a.Fingerprint)).ToList();
        var resolved = known.Where(f => !live.Contains(f)).ToList();

        return started.Count == 0 && resolved.Count == 0
            ? AlertDiff.Empty
            : new AlertDiff(started, resolved);
    }

    /// <summary>Заголовок и тело уведомления о загоревшемся алерте.</summary>
    public static (string Title, string Body) Describe(SignozAlert alert)
    {
        var title = alert.Environment is { } env
            ? $"{alert.Name} — {ShortEnv(env)}"
            : alert.Name;

        var body = alert.Description ?? "Сработало правило телеметрии.";
        return (title, body);
    }

    /// <summary>Текст уведомления о том, что алерт погас. Восстановление — не повод будить.</summary>
    public static (string Title, string Body) DescribeResolved(SignozAlert alert)
    {
        var (title, _) = Describe(alert);
        return ($"Восстановлено: {title}", "Условие алерта больше не выполняется.");
    }

    /// <summary>
    /// Ссылка на алерт в UI SigNoz. Строится из ruleId и ВНЕШНЕГО адреса: в самих
    /// алертах generatorURL указывает на localhost:8080 — это адрес внутри контейнера,
    /// снаружи он не открывается.
    /// </summary>
    public static string? RuleUrl(string? publicBaseUrl, SignozAlert alert)
        => RuleUrl(publicBaseUrl, alert.RuleId);

    /// <summary>
    /// То же по голому ruleId — для случая, когда самого алерта на руках уже нет
    /// (погасший инцидент разбирается по памятке из <see cref="AlertStateStore"/>).
    /// Перегрузка, а не второй формат ссылки по месту: два таких формата разъезжаются
    /// при первой же правке.
    /// </summary>
    public static string? RuleUrl(string? publicBaseUrl, string? ruleId)
    {
        if (string.IsNullOrWhiteSpace(publicBaseUrl) || string.IsNullOrWhiteSpace(ruleId)) return null;
        return $"{publicBaseUrl.TrimEnd('/')}/alerts/overview?ruleId={Uri.EscapeDataString(ruleId)}";
    }

    /// <summary>
    /// Тот же адрес правила, но БЕЗ базы — для интерфейса, который открывает SigNoz
    /// своим пробросом. Уведомлениям нужна абсолютная ссылка (их читают вне приложения),
    /// карточке — относительная: базой у бэкенда служит localhost, недостижимый у клиента.
    /// </summary>
    public static string? RulePath(string? ruleId)
        => string.IsNullOrWhiteSpace(ruleId)
            ? null
            : $"/alerts/overview?ruleId={Uri.EscapeDataString(ruleId)}";

    /// <summary>«production» в заголовке уведомления слишком длинно для телефона.</summary>
    private static string ShortEnv(string env)
        => env.Equals("production", StringComparison.OrdinalIgnoreCase) ? "прод" : env;

    // ==== разбор примитивов ====

    private static string? String(JsonElement parent, string name)
        => parent.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    private static DateTimeOffset? Time(JsonElement parent, string name)
        => String(parent, name) is { } s
           && DateTimeOffset.TryParse(s, null,
                System.Globalization.DateTimeStyles.RoundtripKind, out var parsed)
            ? parsed
            : null;

    private static IReadOnlyDictionary<string, string> Map(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var obj) || obj.ValueKind != JsonValueKind.Object)
            return new Dictionary<string, string>();

        var dict = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var prop in obj.EnumerateObject())
        {
            if (prop.Value.ValueKind == JsonValueKind.String)
                dict[prop.Name] = prop.Value.GetString() ?? "";
        }
        return dict;
    }
}
