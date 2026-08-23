using ClaudeHomeServer.Telemetry.Alerts;

namespace ClaudeHomeServer.Telemetry.Incidents;

/// <summary>
/// Сбор досье по инциденту: разрез по тегам, упавшие ходы, логи окна и затронутые чаты
/// с локальным контекстом.
///
/// <b>Модель здесь не участвует вообще</b> — жёсткое ограничение владельца. Досье собирает
/// детерминированный код; LLM зовётся только по кнопке «Объяснить» (место
/// <c>incident-explain</c>) и получает уже готовое досье.
///
/// Связка «трейс → чат» идёт по тегу <c>chat_id</c> (стабильный id чата CCS), а не по
/// <c>session_id</c>: там лежит csid claude CLI, который перезаписывается на каждом
/// <c>system/init</c> и в первом ходу вовсе отсутствует.
/// </summary>
public sealed class IncidentDossierService(
    IncidentsOptions options,
    ISignozQueryClient client,
    AlertStateStore state,
    IIncidentLocalContext localContext)
{
    /// <summary>
    /// Какой метрикой разрезать инцидент. Ключ — имя правила (label <c>alertname</c>):
    /// правила заданы кодом в <c>docker/observability/alerts/*.json</c>, поэтому имена
    /// стабильны. Незнакомое правило разрезаем ошибками LLM — самый частый класс
    /// инцидентов, и пустой разрез честнее выдуманного.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, (string Metric, string Tag)> BreakdownByRule =
        new Dictionary<string, (string, string)>(StringComparer.OrdinalIgnoreCase)
        {
            ["Всплеск ошибок LLM"] = ("ccs.llm.errors", "error_type"),
            ["Отказы MCP-инструментов"] = ("ccs.mcp.errors", "tool_name"),
            ["Сбой синхронизации знаний"] = ("ccs.dify.sync.errors", "reason"),
            ["Ходы стали медленнее"] = ("ccs.llm.duration.count", "provider"),
            ["Ходы массово встали"] = ("ccs.llm.duration.count", "provider"),
            ["Лимиты провайдера жмут"] = ("ccs.llm.rate_limit_hits", "provider"),
            ["Пульс телеметрии пропал"] = ("ccs.telemetry.heartbeat", "deployment.environment"),
        };

    private static readonly (string Metric, string Tag) DefaultBreakdown = ("ccs.llm.errors", "error_type");

    public bool IsConfigured => options.IsConfigured;

    /// <summary>Проект для обсуждения инцидента; null — чат вне проектов.</summary>
    public string? DiscussProjectId => options.DiscussProjectId;

    /// <summary>
    /// Список инцидентов: горящие сейчас + недавно погасшие (история в
    /// <see cref="AlertStateStore"/>). Порядок — свежие первыми, горящие выше погасших.
    /// </summary>
    public async Task<(IncidentStatus Status, IReadOnlyList<IncidentSummary> Items)> ListAsync(CancellationToken ct)
    {
        if (!options.IsConfigured) return (IncidentStatus.NotConfigured, []);

        var fetched = await client.FetchAlertsAsync(ct);
        if (fetched is null) return (IncidentStatus.Unavailable, []);

        var firing = AlertDigest.Actionable(fetched)
            .Select(a => Summarize(a, isFiring: true))
            .ToList();

        var live = firing.Select(i => i.Fingerprint).ToHashSet(StringComparer.Ordinal);
        var recent = state.Recent(AlertStateStore.MaxHistory)
            .Where(entry => entry.ResolvedAt is not null && !live.Contains(entry.Fingerprint))
            .Select(entry => new IncidentSummary(
                entry.Fingerprint, entry.Memo.Title, null, entry.Memo.Severity, entry.Memo.Environment,
                entry.Memo.FiredAt, entry.ResolvedAt, IsFiring: false));

        return (IncidentStatus.Ok,
            [.. firing.OrderByDescending(i => i.StartedAt), .. recent.OrderByDescending(i => i.ResolvedAt)]);
    }

    /// <summary>
    /// Досье по отпечатку. <c>null</c> — такого инцидента нет ни среди горящих, ни в истории
    /// (типичный случай — протухший диплинк из уведомления).
    /// </summary>
    /// <summary>
    /// Короткий кэш собранных досье. Сборка — это ТРИ живых запроса в SigNoz (разрез,
    /// ходы, логи), секунды при спокойном стеке и до минуты при медленном. А карточка
    /// собирает его по кругу: открыл инцидент, нажал «Обсудить» — ещё раз, «Завести
    /// задачу» — ещё раз, «Объяснить» — ещё. Человек при этом видел долгую паузу на
    /// каждое нажатие, хотя данные у него уже на экране.
    ///
    /// Минута — шаг опроса алертов: чаще картина всё равно не меняется.
    /// </summary>
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(1);
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, (DateTimeOffset At, IncidentDossier Dossier)> _cache = new(StringComparer.Ordinal);

    public async Task<IncidentDossier?> BuildAsync(string fingerprint, CancellationToken ct)
    {
        if (!options.IsConfigured)
            return Placeholder(fingerprint, IncidentStatus.NotConfigured);

        if (_cache.TryGetValue(fingerprint, out var cached)
            && DateTimeOffset.UtcNow - cached.At < CacheTtl)
            return cached.Dossier;

        var fetched = await client.FetchAlertsAsync(ct);
        if (fetched is null)
            return Placeholder(fingerprint, IncidentStatus.Unavailable);

        var alert = fetched.FirstOrDefault(a => a.Fingerprint == fingerprint);
        var summary = alert is not null
            ? Summarize(alert, isFiring: true)
            : FromHistory(fingerprint);
        if (summary is null) return null;

        var to = summary.ResolvedAt ?? DateTimeOffset.UtcNow;
        var from = (summary.StartedAt ?? to) - options.Window;
        // Окно всегда «до сих пор» для горящего: инцидент продолжается, и обрезать его
        // временем срабатывания значило бы прятать самое свежее.
        if (summary.IsFiring) to = DateTimeOffset.UtcNow;

        var foreign = summary.Environment is { } env
                      && !env.Equals(options.Environment, StringComparison.OrdinalIgnoreCase);

        // Чат-виновник у правил с разрезом по chat_id известен из самих меток. Живой алерт
        // отдаёт его прямо, погасший — только через памятку: метки исчезают вместе с ним.
        var alertChatId = alert?.ChatId ?? state.Recall(fingerprint)?.ChatId;
        var (metric, tag) = BreakdownByRule.TryGetValue(RuleNameOf(alert, summary), out var mapped)
            ? mapped : DefaultBreakdown;

        // Разрез и списки тянем из SigNoz; чужой контур разрезается по СВОЕЙ метке среды —
        // иначе карточка показала бы цифры нашего инстанса под чужим инцидентом.
        var environmentFilter = summary.Environment;
        var breakdownJson = await client.QueryRangeAsync(
            IncidentQueries.Breakdown(metric, tag, environmentFilter, from, to), ct);
        var turnsJson = await client.QueryRangeAsync(
            IncidentQueries.FailedTurns(environmentFilter, from, to), ct);
        var logsJson = await client.QueryRangeAsync(
            IncidentQueries.Logs(environmentFilter, from, to), ct);

        var breakdown = IncidentQueries.ParseBreakdown(breakdownJson, tag);
        var turns = IncidentQueries.ParseTurns(turnsJson);
        var logs = IncidentQueries.ParseLogs(logsJson);

        // Полный отказ SigNoz на всех трёх запросах при живом списке алертов — состояние
        // «данных не собрали», и говорить об этом надо прямо, а не показывать пустое досье.
        var status = breakdownJson is null && turnsJson is null && logsJson is null
            ? IncidentStatus.Unavailable
            : IncidentStatus.Ok;

        var dossier = new IncidentDossier
        {
            Incident = summary,
            Status = status,
            From = from,
            To = to,
            IsForeignEnvironment = foreign,
            Breakdown = breakdown,
            BreakdownTag = tag,
            Turns = turns,
            TurnsTotal = turns.Count,
            Logs = logs,
            LogsTotal = logs.Count,
            // Чужой контур: локальных чатов по нему нет и быть не может — карточка скажет
            // это плашкой, а не покажет пустой список как факт «чаты не пострадали».
            Chats = foreign ? [] : localContext.Describe(turns, from, to, alertChatId),
            RulePath = AlertDigest.RulePath(alert?.RuleId ?? state.Recall(fingerprint)?.RuleId),
        };

        // Неудачную сборку не кэшируем: SigNoz мог просто моргнуть, и на следующем
        // нажатии человек должен получить данные, а не минуту помнить пустоту.
        if (status == IncidentStatus.Ok) _cache[fingerprint] = (DateTimeOffset.UtcNow, dossier);
        return dossier;
    }

    private IncidentSummary Summarize(SignozAlert alert, bool isFiring)
    {
        var (title, body) = AlertDigest.Describe(alert);
        return new IncidentSummary(
            alert.Fingerprint, title, body, alert.Severity, alert.Environment,
            alert.StartsAt, ResolvedAt: null, IsFiring: isFiring,
            IsMuted: state.IsMuted(alert.Fingerprint));
    }

    /// <summary>
    /// Заглушить инцидент или вернуть ему звук. Памятка заводится из живого алерта, если
    /// её ещё нет: глушить приходится как раз то, о чём уведомления не приходили.
    /// </summary>
    public async Task SetMutedAsync(string fingerprint, bool muted, CancellationToken ct)
    {
        AlertMemo? fallback = null;
        if (muted && state.Recall(fingerprint) is null)
        {
            var alert = (await client.FetchAlertsAsync(ct))?
                .FirstOrDefault(a => a.Fingerprint == fingerprint);
            if (alert is not null)
                fallback = new AlertMemo(AlertDigest.Describe(alert).Title,
                    alert.StartsAt ?? DateTimeOffset.UtcNow,
                    alert.Severity, alert.Environment, alert.RuleId, ChatId: alert.ChatId);
        }
        state.SetMuted(fingerprint, muted, fallback);
    }

    /// <summary>Инцидент, которого уже нет в выдаче SigNoz, — из истории состояния алертов.</summary>
    private IncidentSummary? FromHistory(string fingerprint)
    {
        var memo = state.Recall(fingerprint);
        if (memo is null) return null;
        return new IncidentSummary(
            fingerprint, memo.Title, null, memo.Severity, memo.Environment,
            memo.FiredAt, memo.ResolvedAt, IsFiring: memo.ResolvedAt is null,
            IsMuted: memo.MutedAt is not null);
    }

    /// <summary>Имя правила: из меток алерта, иначе из заголовка памятки (там оно первым словом).</summary>
    private static string RuleNameOf(SignozAlert? alert, IncidentSummary summary)
        => alert?.Name ?? summary.Title.Split(" — ")[0];

    /// <summary>
    /// Карточка без данных, но с честным статусом: телеметрия выключена или SigNoz молчит.
    /// Название берём из истории, если она есть, — по диплинку из уведомления человек
    /// должен видеть, о каком инциденте речь, даже когда данных не собрать.
    /// </summary>
    private IncidentDossier Placeholder(string fingerprint, IncidentStatus status)
    {
        var summary = FromHistory(fingerprint)
                      ?? new IncidentSummary(fingerprint, "Инцидент", null, null, null, null, null, IsFiring: false);
        var to = DateTimeOffset.UtcNow;
        return new IncidentDossier
        {
            Incident = summary,
            Status = status,
            From = to - options.Window,
            To = to,
        };
    }
}
