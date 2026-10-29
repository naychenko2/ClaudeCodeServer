using ClaudeHomeServer.Protocol;

namespace ClaudeHomeServer.Services.Composition;

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
//   ToSession       — в конкретный чат (группа = sessionId);
//   ToSessionExcept — той же session-группе, КРОМЕ соединения-отправителя:
//                     ручной ввод пользователя отправитель уже нарисовал
//                     оптимистично (useSession.send), эхо ему не нужно, а остальные
//                     устройства того же чата должны видеть реплику live;
//   ToOwner         — во все вкладки владельца (группа = "user_" + ownerId);
//   ToProject       — всем, подписанным на проект (группа = "project_" + projectId);
//   ToPreviewLog    — подписчикам лога preview-сервиса проекта
//                   (группа = "preview_" + projectId + ":" + serviceId; см.
//                   DevServerService.LogGroup). Адресация специфическая, формат
//                   группы собирает шов, иначе префикс preview_ живёт в одном файле,
//                   а тело `DevServerService` собирает его вручную — тот же риск
//                   молчаливой недоставки, что и для user_/project_ до шва. Управление
//                   членством (Groups.AddToGroupAsync) остаётся в SessionHub — это
//                   другая операция, и на broadcast она не влияет.
//
// НЕ покрывает (см. задачу):
//   - DeviceExecChannel (Services/Desktop) — типизированный хаб устройств
//     (Clients.Client(connectionId).ExecOpen), другой канал (ADR-016);
//   - TerminalService (Services/Terminal) — IHubContext<TerminalHub> +
//     Groups.AddToGroupAsync, управление членством;
//   - FileWatcherService (Services) — шлёт в ту же project-группу, но СВОИМ методом хаба
//     («filesChanged», payload не ServerMessage): под контракт «один канал message» он не
//     подходит. Имя группы берёт из SessionHubBroadcaster.ProjectGroup, поэтому префикс
//     всё равно живёт в одном файле;
//   - BroadcastTaskChangedAsync (TaskHubExtensions) — после миграции
//     переезжает внутрь SessionHubBroadcaster под отдельным ToOwner-вызовом
//     (формирует TaskChangedMessage сразу из TaskItem).
public interface ISessionBroadcaster
{
    Task ToSession(string sessionId, ServerMessage message);
    Task ToSessionExcept(string sessionId, string exceptConnectionId, ServerMessage message);
    Task ToOwner(string ownerId, ServerMessage message);
    Task ToProject(string projectId, ServerMessage message);
    Task ToPreviewLog(string projectId, string serviceId, ServerMessage message);
}

// Проектные рассылки общих проектов (S1-5): событие про проект идёт всем участникам группы
// project_{id}, а не только владельцу. Условная форма нужна там, где сущность бывает личной.
public static class SessionBroadcasterExtensions
{
    // Проектное событие: есть projectId — группа проекта, нет — личное владельцу
    public static Task ToProjectOrOwner(this ISessionBroadcaster broadcaster, string ownerId, string? projectId, ServerMessage message) =>
        string.IsNullOrEmpty(projectId)
            ? broadcaster.ToOwner(ownerId, message)
            : broadcaster.ToProject(projectId, message);

    // Событие доски: у задачи проекта — участникам группы проекта и дополнительно владельцу
    // карточки, потому что общий стор задач (календарь, список вне проекта) подписан только
    // на user_{id} и без второй адресации не увидел бы собственных проектных задач.
    public static async Task ToTaskBoard(this ISessionBroadcaster broadcaster, string ownerId, string? projectId, ServerMessage message)
    {
        if (string.IsNullOrEmpty(projectId))
        {
            await broadcaster.ToOwner(ownerId, message);
            return;
        }
        await broadcaster.ToProject(projectId, message);
        await broadcaster.ToOwner(ownerId, message);
    }
}
