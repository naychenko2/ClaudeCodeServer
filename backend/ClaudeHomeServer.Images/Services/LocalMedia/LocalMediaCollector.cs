namespace ClaudeHomeServer.Services.Images.LocalMedia;

// Фоновый сбор результатов локальной генерации: агент мог закончить ход, не дождавшись
// видео, а файл всё равно должен появиться в проекте. Тумблер LocalMedia:Enabled читается
// живьём: выключенный сервер цикл не трогает ComfyUI вовсе. Здесь же — забывание старых задач
// и чистка их файлов в ComfyUI (LocalMediaCleanup)
public sealed class LocalMediaCollector(LocalMediaService media, LocalMediaCleanup cleanup,
    ILogger<LocalMediaCollector> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var options = media.Options;
            try
            {
                if (options.Enabled)
                {
                    await media.CollectPendingAsync(stoppingToken);
                    cleanup.Run(options, DateTime.UtcNow);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                log.LogWarning(ex, "Сбор результатов локальной генерации не удался — повторю позже");
            }

            // Коллектор реже опроса агента: он страхует, а не ведёт
            var delay = TimeSpan.FromMilliseconds(Math.Clamp(options.PollIntervalMs * 3, 3000, 60000));
            try
            {
                await Task.Delay(delay, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }
}
