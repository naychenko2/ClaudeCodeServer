using ClaudeHomeServer.Services.VideoEditor.Contracts;

namespace ClaudeHomeServer.Services.VideoEditor.Scenes;

// При старте модуля снимает с нитей запуски, оборванные перезапуском сервера (как AudioThreadRecovery).
// Реестр задач живёт в памяти и на старте пуст, поэтому каждый запуск в статусе running — мёртвый:
// результата не будет, карточка висела бы в «Снимаем…». Запуск становится interrupted с записью журнала
// для хода; версии, уже ставшие версиями, остаются. Локальные задачи не подхватываются: котировка и
// параметры живут только в памяти исполнителя — поднять задачу обратно не из чего.
public sealed class VideoThreadRecovery(VideoThreadStore store, ILogger<VideoThreadRecovery> logger) : IHostedService
{
    public const string InterruptedText = "Задача потеряна при перезапуске сервера";

    public Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            var chats = Recover(cancellationToken);
            if (chats > 0)
                logger.LogInformation("Видео: сняты прерванные перезапуском запуски в {Count} чатах", chats);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Видео: сверка нитей после старта не удалась");
        }
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    // Число чатов, где что-то снято. Сбой одного чата не мешает остальным
    public int Recover(CancellationToken ct)
    {
        var dropped = 0;
        foreach (var (ownerId, sessionId) in store.Chats())
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var state = store.InterruptRunning(ownerId, sessionId, (scene, launch) =>
                    new VideoThreadEvent(store.Now(), VideoThreadEventKinds.Interrupted, EventText(scene, launch),
                        scene.SceneId, launch.JobId));
                if (state is not null) dropped++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Видео: прерванные запуски нитей чата {SessionId} не сняты", sessionId);
            }
        }
        return dropped;
    }

    // «…: сцена Сцена 2, варианты недоступны — запусти заново»
    private static string EventText(VideoSceneDto scene, VideoLaunchDto launch)
    {
        var kept = scene.Versions.Where(v => v.JobId == launch.JobId).ToList();
        return kept.Count == 0
            ? $"{InterruptedText}: {scene.Name}, варианты недоступны — запусти заново"
            : $"{InterruptedText}: {scene.Name}, часть вариантов сохранена ({string.Join(", ", kept.Select(v => $"версия {v.Number}"))}), " +
              "остальные не готовы — запусти заново";
    }
}
