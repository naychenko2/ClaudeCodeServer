using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;

namespace ClaudeHomeServer.Telemetry.Alerts;

/// <summary>
/// Опрашивает SigNoz и превращает загоревшиеся алерты в уведомления администраторам.
///
/// Доставка целиком переиспользует <see cref="NotificationService"/>: одно обращение
/// кладёт запись в колокол, шлёт тост по SignalR и push на PWA. Своего пути отправки
/// здесь нет намеренно.
/// </summary>
public sealed class AlertPollingService(
    SignozAlertsClient client,
    AlertStateStore state,
    NotificationService notifications,
    UserStore users,
    AlertsOptions options,
    ILogger<AlertPollingService> log) : BackgroundService
{
    /// <summary>
    /// Потолок на разовую лавину: если правила загорелись пачкой, вместо десятка
    /// уведомлений уходит одно сводное. Иначе телефон превращается в пулемёт,
    /// а уведомления — в то, что отключают.
    /// </summary>
    private const int BurstLimit = 5;

    /// <summary>Вкладка «Инциденты» — внутренний роут, а не адрес SigNoz.</summary>
    private const string IncidentsListUrl = "#/telemetry/incidents";

    /// <summary>Карточка конкретного инцидента (разбирает её App.openNotificationUrl).</summary>
    private static string IncidentUrl(string fingerprint)
        => $"#/telemetry/incident/{Uri.EscapeDataString(fingerprint)}";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        log.LogInformation("Опрос алертов запущен: {Url}, интервал {Sec}с",
            options.SignozUrl, options.Interval.TotalSeconds);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await TickAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                // Доставка алертов не должна ронять приложение — иначе сторож
                // окажется опаснее того, что он сторожит
                log.LogWarning(ex, "Тик опроса алертов не удался");
            }

            try
            {
                await Task.Delay(options.Interval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    internal async Task TickAsync(CancellationToken ct)
    {
        var fetched = await client.FetchAsync(ct);
        if (fetched is null) return;   // опрос не удался — состояние не трогаем

        var actionable = AlertDigest.Actionable(fetched, options.Environments);
        var diff = AlertDigest.Diff(actionable, state.KnownFingerprints);
        if (diff.IsEmpty) return;

        var admins = users.GetAll().Where(u => u.Role == "admin").ToList();
        if (admins.Count == 0)
        {
            log.LogWarning("Алерты некому отправить: администраторов нет");
            return;
        }

        await ReportStartedAsync(diff.Started, admins);
        await ReportResolvedAsync(diff.Resolved, admins);
    }

    private async Task ReportStartedAsync(IReadOnlyList<SignozAlert> started, List<User> admins)
    {
        if (started.Count == 0) return;

        if (started.Count > BurstLimit)
        {
            var names = string.Join(", ", started.Take(BurstLimit).Select(a => AlertDigest.Describe(a).Title));
            await FanOutAsync(admins, "alert", "telemetry_alert",
                $"Сработало правил: {started.Count}",
                $"{names} и ещё {started.Count - BurstLimit}.",
                // Конкретного инцидента при лавине нет — ведём на список, а не в SigNoz:
                // текст обещает разбор, и открывать вместо него сырой дашборд странно
                url: IncidentsListUrl,
                tag: "telemetry-burst", environment: null, sendPush: true);
        }
        else
        {
            foreach (var alert in started)
            {
                var (title, body) = AlertDigest.Describe(alert);
                await FanOutAsync(admins, "alert", "telemetry_alert", title, body,
                    // Внутренний роут вместо ссылки в SigNoz: тап открывает карточку
                    // с готовым досье, ради которой фича и делалась. Ссылка на само
                    // правило осталась вторичной — внутри карточки.
                    url: IncidentUrl(alert.Fingerprint),
                    tag: alert.Fingerprint, environment: alert.Environment, sendPush: true);
            }
        }

        foreach (var alert in started)
        {
            // Памятка несёт всё, что нужно карточке инцидента после того, как алерт погас:
            // важность, контур и ruleId для ссылки в SigNoz (самого алерта тогда уже нет).
            state.Remember(alert.Fingerprint, new AlertMemo(
                AlertDigest.Describe(alert).Title,
                alert.StartsAt ?? DateTimeOffset.UtcNow,
                Severity: alert.Severity,
                Environment: alert.Environment,
                RuleId: alert.RuleId));
        }
    }

    private async Task ReportResolvedAsync(IReadOnlyList<string> resolved, List<User> admins)
    {
        if (resolved.Count == 0) return;

        // Восстановление — хорошая новость, ради неё не будят: уведомление уходит
        // в колокол и тост, но без push
        foreach (var fingerprint in resolved)
        {
            var memo = state.Recall(fingerprint);
            if (memo is null) continue;

            await FanOutAsync(admins, "success", "telemetry_alert_resolved",
                $"Восстановлено: {memo.Title}",
                "Условие алерта больше не выполняется.",
                // Погасший инцидент остаётся в истории — карточка по нему открывается
                url: IncidentUrl(fingerprint),
                tag: fingerprint, environment: null, sendPush: false);
        }

        // Не забываем, а помечаем погасшим: запись остаётся историей для раздела
        // «Инциденты» (разобрать инцидент часто хочется уже после того, как он погас).
        state.MarkResolved(resolved);
    }

    private async Task FanOutAsync(List<User> admins, string kind, string type,
        string title, string body, string? url, string tag, string? environment, bool sendPush)
    {
        foreach (var admin in admins)
        {
            try
            {
                await notifications.SendAsync(admin.Id, new CreateNotificationRequest
                {
                    Kind = kind,
                    Type = type,
                    Title = title,
                    Body = body,
                    Url = url,
                    // Тег схлопывает повторы по одному алерту в шторке браузера
                    Tag = tag,
                    Source = environment is null ? "Телеметрия" : $"Телеметрия · {environment}",
                }, sendPush);
            }
            catch (Exception ex)
            {
                // Отказ одного получателя не должен обрывать рассылку остальным
                log.LogWarning(ex, "Не удалось отправить алерт пользователю {UserId}", admin.Id);
            }
        }
    }
}
