using ClaudeHomeServer.Hubs;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services.Notes;
using Microsoft.AspNetCore.SignalR;

namespace ClaudeHomeServer.Services.Composition;

// Реализация INotesHubNotifier (Core) поверх IHubContext<SessionHub>.
// Тонкая обёртка: конструирует NotesChangedMessage/TaskChangedMessage и шлёт
// в группу пользователя. Логика сообщений остаётся в Protocol/ServerMessage.cs
// (Main) — Core-контракт намеренно её не видит, чтобы обратной зависимости
// не было.
//
// Шов создан в Этап 5, волна 5 ради выноса Notes в отдельный .csproj: после
// выноса NoteTaskSyncService сидит в Notes-вертикали и больше не имеет
// прямого доступа к IHubContext<SessionHub>. Эта обёртка живёт в Main
// (Services/Composition, рядом с другими форвардерами подсистем).
public sealed class NotesHubNotifier(IHubContext<SessionHub> hub) : INotesHubNotifier
{
    public Task BroadcastNotesChangedAsync(string userId, string action, string noteId) =>
        hub.Clients.Group("user_" + userId)
            .SendAsync("message", new NotesChangedMessage(action, noteId));

    public Task BroadcastTaskChangedAsync(string userId, string action, string taskId) =>
        // TaskChangedMessage хранит весь TaskItem (контракт шире, чем нужно для
        // подписки фронта «карточка обновилась → перезагрузить»), но фронт
        // использует только Id. Передаём минимальный TaskItem c одним id —
        // клиент всё равно перезапросит задачу через REST для отображения.
        hub.Clients.Group("user_" + userId)
            .SendAsync("message", new TaskChangedMessage(action,
                new TaskItem { Id = taskId }));
}
