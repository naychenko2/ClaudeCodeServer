using ClaudeHomeServer.Services.Turn;

namespace ClaudeHomeServer.Services.ChatContext;

// Контекст чата живёт и умирает вместе с чатом (ADR-023): session/deleted сносит файл
// data/chat-context/{ownerId}/{sessionId}.json, session/branched копирует его под ветку.
// Файл трогаем по точному имени, никогда по маске. Без шины (тесты без DI) — молча ничего
// не делает. Сбой подписчика шина гасит сама, здесь он только пишется в лог.
public sealed class ChatContextLifecycle(
    string root,
    ILogger<ChatContextLifecycle> logger,
    ITurnEventBus? events = null) : IHostedService
{
    private int _subscribed;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        // Отписки у шины нет: повторный старт хоста не должен удваивать подписчиков
        if (events is not null && Interlocked.Exchange(ref _subscribed, 1) == 0)
        {
            events.OnNotification<SessionDeleted>(OnDeleted, "ChatContextLifecycle.Deleted");
            events.OnNotification<SessionBranched>(OnBranched, "ChatContextLifecycle.Branched");
            events.OnNotification<SessionContinued>(OnContinued, "ChatContextLifecycle.Continued");
        }
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public string StatePath(string ownerId, string sessionId) =>
        Path.Combine(root, Safe(ownerId), Safe(sessionId) + ".json");

    private Task OnDeleted(SessionDeleted e)
    {
        if (e.Turn.OwnerId is { } owner)
        {
            try
            {
                var path = StatePath(owner, e.Turn.SessionId);
                if (File.Exists(path)) File.Delete(path);
            }
            catch (Exception ex) { logger.LogWarning(ex, "Контекст удалённого чата {SessionId} не убран", e.Turn.SessionId); }
        }
        return Task.CompletedTask;
    }

    private Task OnBranched(SessionBranched e) => CopyState(e.Turn, e.SourceSessionId, "ветку");

    // Продолжение в новом чате: контекст (закреплённые материалы) переезжает вместе с работой
    private Task OnContinued(SessionContinued e) => CopyState(e.Turn, e.SourceSessionId, "продолжение");

    private Task CopyState(TurnContext turn, string sourceSessionId, string target)
    {
        if (turn.OwnerId is { } owner)
        {
            try
            {
                var from = StatePath(owner, sourceSessionId);
                if (File.Exists(from))
                {
                    var to = StatePath(owner, turn.SessionId);
                    Directory.CreateDirectory(Path.GetDirectoryName(to)!);
                    File.Copy(from, to, overwrite: true);
                }
            }
            catch (Exception ex) { logger.LogWarning(ex, "Контекст чата {Source} не скопирован в {Target} {SessionId}", sourceSessionId, target, turn.SessionId); }
        }
        return Task.CompletedTask;
    }

    private static string Safe(string segment)
    {
        if (string.IsNullOrWhiteSpace(segment) || segment.Contains("..")
            || segment.IndexOfAny(['/', '\\', ':']) >= 0 || segment.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new ArgumentException("Недопустимый идентификатор", nameof(segment));
        return segment;
    }
}
