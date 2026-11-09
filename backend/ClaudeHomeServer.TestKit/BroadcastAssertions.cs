using ClaudeHomeServer.Protocol;
using Xunit.Sdk;

namespace ClaudeHomeServer.Tests.Helpers;

// Контракт «ручка меняет состояние — вещает событие»: стор фронта живёт по WS-событиям,
// молчаливая мутация оставляет экран устаревшим до перезагрузки (дефекты линии паузы 24.10).
// Хелпер выполняет действие и требует, чтобы в группу владельца ПОСЛЕ его начала ушло событие
// нужного типа по нужному id; прежние сообщения (подготовка сценария) не засчитываются.
public static class BroadcastAssertions
{
    // ownerId — владелец-адресат: группа другого владельца не засчитывается.
    // type — тип WS-события; поддержан "task_changed" (id — Task.Id). Новый тип добавляется
    // строкой в SelectId, а не отдельным хелпером. Возвращает пойманные сообщения для
    // дополнительных проверок (action, поля задачи).
    public static async Task<IReadOnlyList<ServerMessage>> AssertBroadcasts(
        this TestSessionBroadcaster broadcaster, string type, string id, string ownerId, Func<Task> action)
    {
        // Неподдержанный тип отвергаем до действия: иначе на пустом списке сообщений SelectId
        // не вызовется и тест получит «не вызвало» вместо «тип не поддержан».
        SelectId(type, null);
        var before = broadcaster.Owner.Count;
        await action();
        var caught = broadcaster.Owner.Skip(before)
            .Where(m => m.OwnerId == ownerId)
            .Select(m => m.Message)
            .Where(m => SelectId(type, m) == id)
            .ToList();
        if (caught.Count == 0)
            throw new XunitException(
                $"Действие не вызвало {type} по id {id}: изменение состояния осталось без вещания");
        return caught;
    }

    private static string? SelectId(string type, ServerMessage? message) => type switch
    {
        "task_changed" => (message as TaskChangedMessage)?.Task.Id,
        _ => throw new ArgumentException($"Тип события не поддержан хелпером: {type}", nameof(type)),
    };
}
