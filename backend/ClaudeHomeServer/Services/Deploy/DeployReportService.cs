using System.Text;
using ClaudeHomeServer.Models;

namespace ClaudeHomeServer.Services.Deploy;

/// <summary>
/// Доклад об итоге выкатки (ADR-010). Заказчик спрашивал из чата прода, а этот чат умер
/// вместе со старым инстансом — поэтому докладывает уже НОВЫЙ процесс: на старте читает
/// журнал и, если итог есть и не доложен, шлёт уведомление и сообщение в чат-инициатор,
/// после чего ставит отметку. Без этого «публикация не прошла» узнаётся по молчащему сайту.
/// </summary>
public sealed class DeployReportService(
    DeployService deploy,
    NotificationService notifications,
    SessionManager sessions,
    BuildIdProvider build,
    ILogger<DeployReportService> log) : BackgroundService
{
    // Пауза на подъём: сообщение в чат запускает ход, а ходу нужен уже поднятый хост
    private static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(10);

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        try
        {
            await Task.Delay(StartupDelay, ct);
            await ReportPendingAsync(ct);
        }
        catch (OperationCanceledException) { /* остановка приложения */ }
        catch (Exception ex) { log.LogError(ex, "Доклад об итоге выкатки сорвался"); }
    }

    internal async Task ReportPendingAsync(CancellationToken ct = default)
    {
        if (deploy.PendingReport() is not { } record) return;

        log.LogInformation("Итог выкатки {Id} ({Status}) ещё не доложен — сообщаем заказчику",
            record.Id, record.Result?.Status);

        var (title, body) = Compose(record, build.BuildId);

        if (record.InitiatedBy?.UserId is { Length: > 0 } userId)
        {
            try
            {
                await notifications.SendAsync(userId, new CreateNotificationRequest
                {
                    Kind = record.Result?.Ok == true ? "info" : "alert",
                    Type = "deploy_result",
                    Title = title,
                    Body = body,
                    SessionId = record.InitiatedBy.SessionId,
                    Source = "Выкатка",
                    Tag = "deploy-" + record.Id,
                }, sendPush: record.Result?.Ok != true);
            }
            catch (Exception ex)
            {
                // Не доставилось уведомление — доклад в чат всё равно должен уйти
                log.LogWarning(ex, "Уведомление об итоге выкатки {Id} не доставлено", record.Id);
            }
        }

        if (record.InitiatedBy?.SessionId is { Length: > 0 } sessionId && sessions.GetById(sessionId) is not null)
        {
            try
            {
                // Общая для сервера точка отправки в чат: занят своим ходом — встанет в очередь
                await sessions.SendOrEnqueueAsync(sessionId, $"{title}\n\n{body}",
                    suppressTasksExecute: true);
            }
            catch (Exception ex)
            {
                log.LogWarning(ex, "Доклад об итоге выкатки {Id} не ушёл в чат {SessionId}", record.Id, sessionId);
            }
        }

        // Отметка ставится в любом случае: доложить повторно на следующем рестарте
        // хуже, чем не доложить вовсе — устаревший отчёт вводит в заблуждение
        await deploy.MarkReportedAsync(record.Id, ct);
    }

    /// <summary>Текст доклада: заголовок и тело. Вынесено ради теста формулировок.</summary>
    internal static (string Title, string Body) Compose(DeployRecord record, string? buildId)
    {
        var rollback = record.Kind == DeployKinds.Rollback;
        var status = record.Result?.Status ?? record.Phase;
        var title = status switch
        {
            DeployPhases.Succeeded => rollback ? "Откат выполнен" : "Выкатка прошла",
            DeployPhases.RolledBack => "Выкатка не сошлась — вернули прошлый релиз",
            _ => rollback ? "Откат не удался" : "Выкатка не удалась",
        };

        var body = new StringBuilder();
        body.Append("Выкатка ").Append(record.Id);
        if (record.Ref is { Length: > 0 } r) body.Append(", ветка ").Append(r);
        if (record.Sha is { Length: > 0 } sha) body.Append(", коммит ").Append(sha);
        if (record.Dirty) body.Append(", рабочее дерево было грязным");
        body.Append('.');

        if (record.Result?.Message is { Length: > 0 } message)
            body.Append(' ').Append(message);

        // Статусы шагов у агента: ok | skipped | failed. Пропущенный шаг — не беда
        // (нечего было делать), в докладе называем только по-настоящему упавшие.
        var failed = record.Steps
            .Where(s => !string.Equals(s.Status, "ok", StringComparison.OrdinalIgnoreCase)
                     && !string.Equals(s.Status, "skipped", StringComparison.OrdinalIgnoreCase))
            .Select(s => s.Name)
            .ToList();
        if (failed.Count > 0)
            body.Append(" Не прошли шаги: ").Append(string.Join(", ", failed)).Append('.');

        if (buildId is { Length: > 0 })
            body.Append(" Сейчас работает сборка ").Append(buildId).Append('.');

        return (title, body.ToString());
    }
}
