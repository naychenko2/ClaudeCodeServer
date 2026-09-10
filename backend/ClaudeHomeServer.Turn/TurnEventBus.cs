namespace ClaudeHomeServer.Services.Turn;

// Реализация шины событий хода. Контракты — в Core
// (`Core/Services/Turn/TurnEventContracts.cs`, ADR-013): их зовут издатели из
// других вертикалей, поэтому им нельзя жить внутри этой сборки.

// Шина событий хода. Один экземпляр на инстанс (подписки живут дольше сессии), изоляция
// владельцев — через TurnContext события, а не через отдельные шины.
public sealed class TurnEventBus : ITurnEventBus
{
    private readonly ILogger<TurnEventBus>? _log;
    private readonly object _gate = new();
    private readonly Dictionary<Type, List<NotificationEntry>> _notifications = new();
    private readonly Dictionary<Type, List<FilterEntry>> _filters = new();
    private int _seq;

    public TurnEventBus(ILogger<TurnEventBus>? log = null) => _log = log;

    private sealed record NotificationEntry(string Name, object Handler);

    // Seq — стабилизатор сортировки: у равных Order порядок подписки, а не случайный.
    private sealed record FilterEntry(int Order, int Seq, string Name, object Handler);

    public void OnNotification<T>(Func<T, Task> handler, string? name = null) where T : ITurnNotification
    {
        ArgumentNullException.ThrowIfNull(handler);
        lock (_gate)
        {
            if (!_notifications.TryGetValue(typeof(T), out var list))
                _notifications[typeof(T)] = list = new List<NotificationEntry>();
            list.Add(new NotificationEntry(name ?? DescribeHandler(handler), handler));
        }
    }

    public void OnFilter<T>(int order, TurnFilterHandler<T> handler, string? name = null) where T : ITurnFilter
    {
        ArgumentNullException.ThrowIfNull(handler);
        lock (_gate)
        {
            if (!_filters.TryGetValue(typeof(T), out var list))
                _filters[typeof(T)] = list = new List<FilterEntry>();
            list.Add(new FilterEntry(order, _seq++, name ?? DescribeHandler(handler), handler));
        }
    }

    public async Task PublishAsync<T>(T e) where T : ITurnNotification
    {
        ArgumentNullException.ThrowIfNull(e);
        NotificationEntry[] entries;
        lock (_gate)
        {
            entries = _notifications.TryGetValue(typeof(T), out var list)
                ? list.ToArray()
                : Array.Empty<NotificationEntry>();
        }

        foreach (var entry in entries)
        {
            try
            {
                await ((Func<T, Task>)entry.Handler)(e).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // Факт уже свершился: падение наблюдателя — не повод ронять ход.
                _log?.LogWarning(ex, "Подписчик {Handler} события {Event} упал (сессия {Session}); ход продолжается",
                    entry.Name, typeof(T).Name, e.Turn.SessionId);
            }
        }
    }

    public async Task<T> ApplyAsync<T>(T e) where T : ITurnFilter
    {
        ArgumentNullException.ThrowIfNull(e);
        FilterEntry[] entries;
        lock (_gate)
        {
            entries = _filters.TryGetValue(typeof(T), out var list)
                ? list.OrderBy(x => x.Order).ThenBy(x => x.Seq).ToArray()
                : Array.Empty<FilterEntry>();
        }

        await InvokeAsync(0).ConfigureAwait(false);
        return e;

        async Task InvokeAsync(int index)
        {
            if (index >= entries.Length) return;
            var entry = entries[index];
            var handler = (TurnFilterHandler<T>)entry.Handler;
            var nextCalls = 0;
            await handler(e, () =>
            {
                nextCalls++;
                if (nextCalls > 1) throw ChainBroken(entry, typeof(T), "позвал next() больше одного раза");
                return InvokeAsync(index + 1);
            }).ConfigureAwait(false);
            // Проверка ПОСЛЕ подписчика: он мог проглотить исключение из next() —
            // тогда обрыв цепочки не должен уйти в тишину.
            if (nextCalls == 0) throw ChainBroken(entry, typeof(T), "не позвал next()");
            if (nextCalls > 1) throw ChainBroken(entry, typeof(T), "позвал next() больше одного раза");
        }
    }

    private static InvalidOperationException ChainBroken(FilterEntry entry, Type eventType, string what) =>
        new($"Filter-подписчик '{entry.Name}' (Order {entry.Order}) события {eventType.Name} {what} — " +
            "цепочка обработки хода оборвана.");

    private static string DescribeHandler(Delegate handler) =>
        $"{handler.Method.DeclaringType?.Name}.{handler.Method.Name}";
}
