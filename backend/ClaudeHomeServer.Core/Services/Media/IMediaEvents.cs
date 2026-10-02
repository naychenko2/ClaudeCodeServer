using Microsoft.Extensions.Logging;

namespace ClaudeHomeServer.Services.Media;

// Хаб событий между редакторами (ADR-022 §3, «Видео ↔ Картинки, Звук»): у вертикалей нет прямых ссылок друг
// на друга (ADR-014), поэтому «в Картинках появилась версия» доходит до «Видео» событием через Core.
// Это in-memory pub/sub: события не хранятся и не переживают рестарт, подписчик, которого не было дома,
// их не получит — потерянное догоняется чтением состояния. Публикует тот, кто УЖЕ записал версию в нить:
// событие про факт, а не про намерение.
public interface IMediaEvents
{
    // Подписка на события типа T; Dispose снимает её. Обработчик зовётся в потоке публикующего, по очереди
    IDisposable Subscribe<T>(Func<T, Task> handler) where T : class;

    // Падение подписчика гасится и логируется: публикующий (редактор картинок или звука) о подписчиках не знает
    // и из-за них не падает
    Task PublishAsync<T>(T evt) where T : class;
}

// В нить картинок чата добавлена версия (после записи). Initiator — SpendInitiators.*; ProjectId — область
// нити (id проекта или «personal»)
public sealed record ImageVersionAdded(
    string OwnerId, string SessionId, string ProjectId, string ThreadId, string VersionId, string Initiator);

// В нить звука чата добавлена версия (после записи); поля как у ImageVersionAdded
public sealed record AudioVersionAdded(
    string OwnerId, string SessionId, string ProjectId, string ThreadId, string VersionId, string Initiator);

public sealed class MediaEventHub(ILogger<MediaEventHub>? log = null) : IMediaEvents
{
    private readonly Lock _gate = new();
    private readonly List<(Type Type, Delegate Handler)> _subscribers = [];

    public IDisposable Subscribe<T>(Func<T, Task> handler) where T : class
    {
        var entry = (typeof(T), (Delegate)handler);
        lock (_gate) _subscribers.Add(entry);
        return new Subscription(this, entry);
    }

    public async Task PublishAsync<T>(T evt) where T : class
    {
        (Type Type, Delegate Handler)[] snapshot;
        lock (_gate) snapshot = [.. _subscribers.Where(s => s.Type == typeof(T))];
        foreach (var (_, handler) in snapshot)
        {
            try { await ((Func<T, Task>)handler)(evt); }
            catch (Exception ex)
            {
                log?.LogWarning(ex, "Подписчик события {Event} упал — публикующий не затронут", typeof(T).Name);
            }
        }
    }

    private sealed class Subscription(MediaEventHub hub, (Type, Delegate) entry) : IDisposable
    {
        public void Dispose()
        {
            lock (hub._gate) hub._subscribers.Remove(entry);
        }
    }
}
