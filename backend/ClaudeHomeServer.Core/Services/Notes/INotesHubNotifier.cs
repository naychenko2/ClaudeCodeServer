namespace ClaudeHomeServer.Services.Notes;

// Нотификатор Hub-рассылки для вертикали Notes (Этап 5, волна 5). Это ПЕРВЫЙ
// прецедент использования IHubContext<SessionHub> вынесенной вертикалью —
// до сих пор шесть вынесенных вертикалей (Video/Yandex/Reader/CodeGraph/Skills/Git)
// Hub не используют. Notes шлёт `notes_changed` (NotesChangedMessage) и
// `task_changed` (TaskChangedMessage) из NoteTaskSyncService. По этому шву
// потом будут резать Team и Desktop.
//
// Образец цены: ILocalHandsNotifier (вертикаль) + тонкая обёртка LocalHandsNotifier
// (Main) поверх веера ядра сессий. Контракт намеренно узкий: NoteTaskSyncService
// отдаёт логическое событие (`action`/`noteId` или `taskId`), реализация в Main
// сама конструирует NotesChangedMessage/TaskChangedMessage и шлёт через Hub —
// Core не знает ни о Hub, ни о типе сообщений (NotesChangedMessage живёт в
// Protocol/ServerMessage.cs, Main).
//
// Реализация в Main подписывается на SendNotificationMessageAsync или
// равноценный сторож, чтобы старые клиенты ловили изменения заметки.

public interface INotesHubNotifier
{
    // Рассылает «заметка изменилась»: пользователь userId (вызывающий), действие action
    // (например "updated"/"created"/"deleted"), идентификатор noteId, projectId — проект
    // заметки (null для личного vault). Реализация в Main конструирует NotesChangedMessage:
    // проектная заметка уходит в группу project_{projectId} (как события доски через
    // ToTaskBoard, S2c-4) И дополнительно в user_{userId} — другие вкладки вызывающего
    // и общий список заметок владельца подписаны только на свою группу. Членство
    // fail-closed на стороне подписки (JoinProject проверяет доступ): посторонний и
    // отозванный в группе проекта не сидят. Личная заметка — только владельцу, как раньше.
    // Полезная нагрузка одна для обоих адресов: id и действие — ничего, чего Read-доступ
    // не даёт: текст клиент запросит через REST.
    Task BroadcastNotesChangedAsync(string userId, string action, string noteId, string? projectId = null);

    // Рассылает «задача изменилась»: пользователь userId, действие action
    // (например "created"/"updated"/"deleted"), идентификатор задачи taskId.
    // Реализация в Main конструирует TaskChangedMessage и шлёт в группу
    // user_{userId}.
    Task BroadcastTaskChangedAsync(string userId, string action, string taskId, string? projectId = null);
}
