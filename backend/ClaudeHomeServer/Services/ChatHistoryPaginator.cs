using ClaudeHomeServer.Protocol;

namespace ClaudeHomeServer.Services;

// Постраничная резка истории чата по курсору. Выделена из эндпоинта истории как чистая
// функция над уже загруженным списком сообщений — так её можно тестировать без файлов и
// I/O, а контроллер остаётся тонким. Подробности контракта — в SessionsController.GetHistory.
public static class ChatHistoryPaginator
{
    // Дефолтный размер страницы: хвост длинного чата (~100 сообщений) укладывается в целевые
    // ~150 КБ ответа против 5+ МБ у полной выдачи. Задано постановкой задачи на пагинацию.
    public const int DefaultLimit = 100;

    // Потолок страницы — защита от злоупотреблений (?limit=100000): бэкенд всё равно читает
    // весь history.json, но сериализация и сеть не должны страдать от раздутого запроса.
    public const int MaxLimit = 500;

    // Курсор before — индекс сообщения, ДО которого (эксклюзивно) нужно отдать сообщения.
    // Валиден диапазон [0, total]: 0 и total дают пустой край/хвост соответственно, а всё
    // что за пределами — несуществующий индекс → контроллер отдаёт 400.
    public static bool IsCursorValid(int total, int before) =>
        before >= 0 && before <= total;

    // Режет messages по курсору. limit — сколько взять (clamp [1, MaxLimit], дефолт DefaultLimit);
    // before — эксклюзивная верхняя граница (null → хвост, последние limit сообщений).
    // Возвращает страницу с хвостом массива перед before; hasMore=true, если за этой пачкой
    // есть более ранние сообщения, и курсором = индекс самого старого сообщения в пачке
    // (null, когда дошли до начала — дальше грузить нечего).
    public static HistoryPage Slice(IReadOnlyList<StoredMessage> messages, int? limit, int? before)
    {
        var take = Math.Clamp(limit ?? DefaultLimit, 1, MaxLimit);
        var total = messages.Count;
        // Подстраховка: даже если контроллер не отсёк кривой before, не выходим за пределы массива
        var endExclusive = Math.Min(before ?? total, total);
        var start = Math.Max(0, endExclusive - take);
        var count = endExclusive - start;

        var slice = new List<StoredMessage>(Math.Max(0, count));
        for (var i = start; i < endExclusive; i++)
            slice.Add(messages[i]);

        var hasMore = start > 0;
        return new HistoryPage(slice, hasMore, hasMore ? start : null);
    }
}

// Страница истории для постраничного ответа эндпоинта. Сериализуется в camelCase
// (System.Text.Json web defaults) → { messages, hasMore, cursor }.
public sealed record HistoryPage(IReadOnlyList<StoredMessage> Messages, bool HasMore, int? Cursor);
