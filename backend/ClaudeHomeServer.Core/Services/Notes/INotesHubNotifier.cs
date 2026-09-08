namespace ClaudeHomeServer.Services.Notes;

// Нотификатор Hub-рассылки для вертикали Notes (Этап 5, волна 5). Это ПЕРВЫЙ
// прецедент использования IHubContext<SessionHub> вынесенной вертикалью —
// до сих пор шесть вынесенных вертикалей (Video/Yandex/Reader/CodeGraph/Skills/Git)
// Hub не используют. Notes шлёт `notes_changed` (NotesChangedMessage) и
// `task_changed` (TaskChangedMessage) из NoteTaskSyncService. По этому шву
// потом будут резать Team и Desktop.
//
// Образец цены: IDesktopHandsNotifier (Core) + тонкая обёртка DesktopHandsNotifier
// (Main) поверх IHubContext<SessionHub>. Контракт намеренно узкий: NoteTaskSyncService
// отдаёт логическое событие (`action`/`noteId` или `taskId`), реализация в Main
// сама конструирует NotesChangedMessage/TaskChangedMessage и шлёт через Hub —
// Core не знает ни о Hub, ни о типе сообщений (NotesChangedMessage живёт в
// Protocol/ServerMessage.cs, Main).
//
// Реализация в Main подписывается на SendNotificationMessageAsync или
// равноценный сторож, чтобы старые клиенты ловили изменения заметки.

public interface INotesHubNotifier
{
    // Рассылает «заметка изменилась»: пользователь userId, действие action
    // (например "updated"/"created"/"deleted"), идентификатор noteId.
    // Реализация в Main конструирует NotesChangedMessage и шлёт в группу
    // user_{userId}.
    Task BroadcastNotesChangedAsync(string userId, string action, string noteId);

    // Рассылает «задача изменилась»: пользователь userId, действие action
    // (например "created"/"updated"/"deleted"), идентификатор задачи taskId.
    // Реализация в Main конструирует TaskChangedMessage и шлёт в группу
    // user_{userId}.
    Task BroadcastTaskChangedAsync(string userId, string action, string taskId);
}
