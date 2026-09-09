namespace ClaudeHomeServer.Services.Turn;

// Контракты шины событий хода (ADR-013). Живут в Core, а НЕ в вертикали Turn:
// шина по определению — то, во что вставляются все, её издатели (Llm, Spend,
// SessionManager) и подписчики разбросаны по разным вертикалям. Оставь контракты
// внутри `ClaudeHomeServer.Turn` — и каждый издатель получил бы связь
// «вертикаль → вертикаль», которую сторож границ не пропускает.
// Реализация `TurnEventBus` остаётся в вертикали: она одна, её резолвит DI.

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
