namespace ClaudeHomeServer.Services;

// Доставка админам алертов о неполном чтении стора: и «не прочитан вовсе — стартуем с пустым
// состоянием», и «часть записей пропущена» (о втором иначе знал бы только WARN в логе).
// Живёт отдельно от JsonFileStore: тот статический и читается из конструкторов сторов,
// DI туда не дотягивается (прецедент — WorkflowAgentParser.Log). 19.09.2026 такая потеря
// была видна только как одна строка LogError в консоли, и полчаса её никто не заметил.
public static class DataLossAlerts
{
    /// <summary>
    /// Подписывает доставку на события потери данных. Копившиеся до подписки алерты
    /// (чтение сторов идёт раньше — на старте) уходят в этот же момент.
    /// </summary>
    public static void Attach(IServiceProvider services)
    {
        JsonFileStore.DataLossSink = alert => _ = NotifyAsync(services, alert);
    }

    private static async Task NotifyAsync(IServiceProvider services, JsonFileStore.DataLossAlert alert)
    {
        var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger(nameof(DataLossAlerts));
        try
        {
            var admins = services.GetRequiredService<UserStore>().GetAll()
                .Where(u => u.Role == "admin")
                .ToList();
            if (admins.Count == 0) return;

            var notifications = services.GetRequiredService<NotificationService>();
            var name = Path.GetFileName(alert.Path);
            // Частичный подъём и полная потеря лечатся по-разному, поэтому и в тексте
            // различимы: там записи живы и чинятся по копии исходника, тут состояние пустое.
            var partial = alert.SkippedItems > 0;
            var title = partial
                ? $"Прочитаны не все записи: {name}"
                : $"Данные не прочитаны: {name}";
            var body = partial
                ? $"{alert.Path}: поднято записей {alert.LoadedItems}, пропущено битых {alert.SkippedItems} ({alert.Reason})."
                  + (alert.BackupPath is null
                      ? "\nКопию исходника сохранить не удалось — пропущенные записи исчезнут при первом сохранении стора."
                      : $"\nКопия исходника с полными данными — {alert.BackupPath}.")
                : $"{alert.Path}: {alert.Reason}"
                  + (alert.BackupPath is null ? "" : $"\nПовреждённый файл сохранён как {alert.BackupPath}.");
            foreach (var admin in admins)
            {
                // Будим push'ом: чем позже это замечено, тем больше поверх пустого
                // состояния успеет записаться — и тем меньше остаётся, что восстанавливать.
                await notifications.SendSystemEventAsync(admin.Id,
                    title,
                    body,
                    kind: "alert", type: "data_loss", url: "", tag: "Система", sendPush: true);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "не удалось разослать алерт о потере данных ({Path})", alert.Path);
        }
    }
}
