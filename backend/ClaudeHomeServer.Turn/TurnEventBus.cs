namespace ClaudeHomeServer.Services.Turn;

// Контекст хода — общая часть КАЖДОГО события шины. Владелец берётся отсюда, а не из
// «текущего пользователя»: подписчик может исполняться вне HTTP-запроса (фоновый ход,
// ход-исполнитель задачи), и единственный достоверный источник per-owner изоляции — само
// событие. TurnSeq — номер хода в сессии, AgentDepth — глубина делегирования
// (агентные ходы), ProjectId — null у чата вне проекта.
public sealed record TurnContext(
    string SessionId,
    string? OwnerId,
    int TurnSeq,
    int AgentDepth,
    string? ProjectId = null);

// Общий предок событий шины. Наследники делятся на два вида, и вид — это КОНТРАКТ ОТКАЗА,
// а не оформление: у Notification падение подписчика гасится, у Filter — роняет ход.
public interface ITurnEvent
{
    TurnContext Turn { get; }
}

// Notification — факт свершился (fire-and-forget). Подписчик стоит в стороне от пути хода:
// порядка между подписчиками нет, исключение гасится и логируется, ход не падает.
public interface ITurnNotification : ITurnEvent
{
}

// Filter (waterfall) — подписчик стоит В ПУТИ хода: правит событие и обязан позвать next().
// Порядок явный (Order), исключение — честный сбой хода: молча пропустить правку промпта
// или подмену модели хуже, чем упасть.
public interface ITurnFilter : ITurnEvent
{
}

// Подписчик Filter: правит событие и передаёт управление дальше вызовом next().
// Не позвал next() — цепочка оборвана, шина бросает внятную ошибку (тихого зависания нет).
public delegate Task TurnFilterHandler<in T>(T e, Func<Task> next) where T : ITurnFilter;

public interface ITurnEventBus
{
    // Подписаться на факт. name — для логов при падении подписчика.
    void OnNotification<T>(Func<T, Task> handler, string? name = null) where T : ITurnNotification;

    // Встать в путь хода. order — явный порядок (меньше = раньше); равные order идут
    // в порядке подписки.
    void OnFilter<T>(int order, TurnFilterHandler<T> handler, string? name = null) where T : ITurnFilter;

    // Разослать факт всем подписчикам. Не бросает никогда.
    Task PublishAsync<T>(T e) where T : ITurnNotification;

    // Прогнать событие через цепочку Filter-подписчиков по Order. Бросает при сбое
    // подписчика и при обрыве цепочки (не вызван next()).
    Task<T> ApplyAsync<T>(T e) where T : ITurnFilter;
}

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
