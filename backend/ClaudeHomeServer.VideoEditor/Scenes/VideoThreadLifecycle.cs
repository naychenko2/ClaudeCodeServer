using ClaudeHomeServer.Services.Turn;

namespace ClaudeHomeServer.Services.VideoEditor.Scenes;

// Нити сцен живут и умирают вместе с чатом (ADR-022 §2, как AudioThreadLifecycle): session/deleted сносит
// файл нитей, session/branched копирует его под ветку. Hosted service ради одного: подписка на шину
// должна жить с первого удаления, а не с первого резолва хранилища. Без шины (тесты без DI) молча
// ничего не делает. Сбой подписчика шина гасит сама, здесь он только пишется в лог.
public sealed class VideoThreadLifecycle(
    VideoThreadStore store,
    ILogger<VideoThreadLifecycle> logger,
    ITurnEventBus? events = null) : IHostedService
{
    private int _subscribed;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        // Отписки у шины нет: повторный старт хоста не должен удваивать подписчиков
        if (events is not null && Interlocked.Exchange(ref _subscribed, 1) == 0)
        {
            events.OnNotification<SessionDeleted>(OnDeleted, "VideoThreadLifecycle.Deleted");
            events.OnNotification<SessionBranched>(OnBranched, "VideoThreadLifecycle.Branched");
        }
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private Task OnDeleted(SessionDeleted e)
    {
        if (e.Turn.OwnerId is { } owner)
        {
            try { store.Delete(owner, e.Turn.SessionId); }
            catch (Exception ex) { logger.LogWarning(ex, "Нити сцен удалённого чата {SessionId} не убраны", e.Turn.SessionId); }
        }
        return Task.CompletedTask;
    }

    private Task OnBranched(SessionBranched e)
    {
        if (e.Turn.OwnerId is { } owner)
        {
            try { store.Copy(owner, e.SourceSessionId, e.Turn.SessionId); }
            catch (Exception ex) { logger.LogWarning(ex, "Нити сцен чата {Source} не скопированы в ветку {SessionId}", e.SourceSessionId, e.Turn.SessionId); }
        }
        return Task.CompletedTask;
    }
}
