using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ClaudeHomeServer.Services.ImageEditor.Threads;

// При старте модуля снимает с нитей задачи, оборванные перезапуском сервера
// (ImageThreadService.RecoverInterruptedAsync). Задачи «Локальных моделей» не подхватываются:
// при штатной остановке драйвер сам отменяет задачу ComfyUI, а котировка, число вариантов и
// размер исходника живут только в памяти исполнителя — поднять задачу обратно не из чего.
public sealed class ImageThreadRecovery(
    ImageThreadService threads,
    ILogger<ImageThreadRecovery> logger,
    IImageEditJobs? jobs = null) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            var chats = await threads.RecoverInterruptedAsync(jobs, cancellationToken);
            if (chats > 0)
                logger.LogInformation("Редактор картинок: сняты прерванные перезапуском задачи в {Count} чатах", chats);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Редактор картинок: сверка нитей с задачами после старта не удалась");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
