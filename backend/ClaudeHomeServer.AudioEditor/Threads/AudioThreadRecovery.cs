namespace ClaudeHomeServer.Services.AudioEditor.Threads;

// При старте модуля снимает с нитей запуски, оборванные перезапуском сервера (как
// ImageThreadRecovery). Реестр задач живёт в памяти и на старте пуст, поэтому каждый запуск в
// статусе running — мёртвый: результата не будет, карточка висела бы в «Генерируем…». Запуск
// становится interrupted с записью журнала для хода; версии, уже ставшие версиями, остаются.
// Задачи «Локальных моделей» не подхватываются: котировка и параметры живут только в памяти
// исполнителя — поднять задачу обратно не из чего.
public sealed class AudioThreadRecovery(AudioThreadStore store, ILogger<AudioThreadRecovery> logger) : IHostedService
{
    public const string InterruptedText = "Задача потеряна при перезапуске сервера";

    public Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            var chats = Recover(cancellationToken);
            if (chats > 0)
                logger.LogInformation("Звук: сняты прерванные перезапуском запуски в {Count} чатах", chats);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Звук: сверка нитей после старта не удалась");
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
                var state = store.InterruptRunning(ownerId, sessionId, (thread, launch) =>
                    new AudioThreadEvent(store.Now(), AudioThreadEventKinds.Interrupted, EventText(thread, launch),
                        thread.Id, launch.JobId));
                if (state is not null) dropped++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Звук: прерванные запуски нитей чата {SessionId} не сняты", sessionId);
            }
        }
        return dropped;
    }

    // «…: звук voice/intro.mp3, варианты недоступны — запусти заново»
    private static string EventText(AudioThread thread, AudioThreadLaunch launch)
    {
        var name = thread.File ?? "новый звук";
        var kept = thread.Versions.Where(v => v.JobId == launch.JobId).ToList();
        return kept.Count == 0
            ? $"{InterruptedText}: звук {name}, варианты недоступны — запусти заново"
            : $"{InterruptedText}: звук {name}, часть вариантов сохранена ({string.Join(", ", kept.Select(AudioThread.Label))}), " +
              "остальные не готовы — запусти заново";
    }
}
