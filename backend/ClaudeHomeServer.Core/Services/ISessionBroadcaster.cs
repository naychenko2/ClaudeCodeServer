using ClaudeHomeServer.Protocol;

namespace ClaudeHomeServer.Core.Services;

// Шов для рассылки WS-событий фронту (Этап 5, Ф4). Вертикали и корневые сервисы
// больше не ссылаются на IHubContext<SessionHub> напрямую — это стена для выноса,
// потому что SignalR остаётся в Main (транспорт). Реализация в Main сидит в
// Services/Composition/SessionHubBroadcaster.cs.
//
// Три метода — единственная допустимая форма шва: «user_»/«project_» сейчас собираются
// вручную в двух десятках мест, и опечатка в префиксе даёт молчаливую недоставку
// (сообщение уходит в несуществующую группу, исключения нет, в логах пусто). Здесь
// префиксы собирает один файл, и любая правка делается за один коммит.
//
// Канал событий — ровно один, метод «message» (контракт с фронтом, см.
// docs/architecture/api.md §SignalR Hub). Под него и лежит тип параметра
// ServerMessage — record-иерархия в Core/Protocol/ServerMessage.cs.
//
// Покрытие:
//   ToSession     — в конкретный чат (группа = sessionId);
//   ToOwner       — во все вкладки владельца (группа = "user_" + ownerId);
//   ToProject     — всем, подписанным на проект (группа = "project_" + projectId);
//   ToPreviewLog  — подписчикам лога preview-сервиса проекта
//                   (группа = "preview_" + projectId + ":" + serviceId; см.
//                   DevServerService.LogGroup). Адресация специфическая, формат
//                   группы собирает шов, иначе префикс preview_ живёт в одном файле,
//                   а тело `DevServerService` собирает его вручную — тот же риск
//                   молчаливой недоставки, что и для user_/project_ до шва. Управление
//                   членством (Groups.AddToGroupAsync) остаётся в SessionHub — это
//                   другая операция, и на broadcast она не влияет.
//
// НЕ покрывает (см. задачу):
//   - DesktopCallRouter (Services/Desktop) — типизированный хаб устройств
//     (Clients.Client(connectionId).Call/Go/Cancel), другой канал (ADR-008);
//   - TerminalService (Services/Terminal) — IHubContext<TerminalHub> +
//     Groups.AddToGroupAsync, управление членством;
//   - BroadcastTaskChangedAsync (TaskHubExtensions) — после миграции
//     переезжает внутрь SessionHubBroadcaster под отдельным ToOwner-вызовом
//     (формирует TaskChangedMessage сразу из TaskItem).
public interface ISessionBroadcaster
{
    Task ToSession(string sessionId, ServerMessage message);
    Task ToOwner(string ownerId, ServerMessage message);
    Task ToProject(string projectId, ServerMessage message);
    Task ToPreviewLog(string projectId, string serviceId, ServerMessage message);
}
