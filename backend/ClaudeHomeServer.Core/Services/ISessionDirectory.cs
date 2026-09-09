using ClaudeHomeServer.Models;

namespace ClaudeHomeServer.Services;

// Узкий шов SessionManager для выноса Spend в отдельный .csproj (Этап 5, шаг 1).
// Spend читает три метода: GetById (имя чата в pivot), GetAll (обход при backfill),
// ResolveOwnerId (владелец сессии в backfill-записях). Полный SessionManager —
// god-объект с десятками методов; Spend нужны ровно эти три.
//
// Совместимость с соседней линией `refactor/core-directories` (Этап 5, отдельная
// ветка): там в списке 4 god-объектов есть SessionManager (14 потребителей) и
// готовится «директория сессий» с тем же набором. Когда директория приедет в
// master, этот интерфейс подлежит слиянию с ней (добавить остальные методы
// SessionManager из его 14 потребителей и убрать дубль). Сейчас — три метода,
// покрывающие ТОЛЬКО нужды Spend.
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
}
