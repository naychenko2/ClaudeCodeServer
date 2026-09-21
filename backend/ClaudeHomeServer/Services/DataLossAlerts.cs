namespace ClaudeHomeServer.Services;

// Доставка алерта «стор не прочитан — стартуем с пустым состоянием» админам.
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
            var body = $"{alert.Path}: {alert.Reason}"
                       + (alert.BackupPath is null ? "" : $"\nПовреждённый файл сохранён как {alert.BackupPath}.");
            foreach (var admin in admins)
            {
                // Будим push'ом: чем позже это замечено, тем больше поверх пустого
                // состояния успеет записаться — и тем меньше остаётся, что восстанавливать.
                await notifications.SendSystemEventAsync(admin.Id,
                    $"Данные не прочитаны: {Path.GetFileName(alert.Path)}",
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
