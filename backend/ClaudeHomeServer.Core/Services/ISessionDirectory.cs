using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;

namespace ClaudeHomeServer.Services;

// Шов SessionManager (Этап 5, вынос Spend). Spend читает три метода: GetById (имя чата в
// pivot), GetAll (обход при backfill), ResolveOwnerId (владелец сессии в backfill-записях).
//
// Этап 5 (сцепка Memory + Dossiers): добавлен `GetHistoryAsync` — обе вертикали читают
// историю сообщений сессии (Memory autolearn, Dossiers discussion/transcript вокруг
// коммита). Расширяем тот же интерфейс, а не множим швы: контракт остаётся «узкий
// каталог сессии», метод чтения истории естественно живёт рядом с GetById.
//
// Подписка на события хода — отдельный шов `ISessionMessageObserver` в Composition: смешивать
// чтение (каталог) и подписку (push) в одном контракте — путь к разрастанию god-объекта.
//
// Полный SessionManager — god-объект с десятками методов; вертикалям нужны ровно эти.
//
// Совместимость с соседней линией `refactor/core-directories` (Этап 5, отдельная
// ветка): там в списке 4 god-объектов есть SessionManager (14 потребителей) и
// готовится «директория сессий» с тем же набором. Когда директория приедет в
// master, этот интерфейс подлежит слиянию с ней.
public interface ISessionDirectory
{
    // Возвращает сессию по id или null, если её нет.
    Session? GetById(string id);

    // Полный список всех сессий в системе. Используется SpendMaintenanceService
    // при первичном backfill для обхода всех чатов.
    IReadOnlyCollection<Session> GetAll();

    // Возвращает владельца сессии (по правилам делегирования: владелец чата
    // может отличаться от создателя, если чат делегирован в другого пользователя
    // через задачу/команду). null — сессия не имеет явного владельца.
    string? ResolveOwnerId(Session s);

    // История сообщений сессии: используется Memory-Autolearn (Persona/Team) и
    // Dossiers (конспект обсуждения, окно реплик вокруг коммита). Загружает
    // ленту через ChatHistoryService; пустой список — сессии нет или истории нет.
    Task<IReadOnlyList<StoredMessage>> GetHistoryAsync(string sessionId);
}
