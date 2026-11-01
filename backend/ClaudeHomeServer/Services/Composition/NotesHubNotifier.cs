using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services.Notes;

namespace ClaudeHomeServer.Services.Composition;

// Реализация INotesHubNotifier (Core) поверх ISessionBroadcaster.
// Тонкая обёртка: конструирует NotesChangedMessage/TaskChangedMessage и рассылает.
// Заметка (S2c-4): проектную — в группу проекта + группу вызывающего (та же форма,
// что ToTaskBoard для доски: участнику — «заметка изменилась», другие вкладки
// вызывающего и общий список владельца — по своей группе); личную — только владельцу.
// Полезная нагрузка одна для обоих адресов: id и действие — не шире Read-доступа
// (текста в сообщении нет вовсе, для группы резать нечего — текст клиент читает
// через REST, он участнику открыт).
// Логика сообщений остаётся в Protocol/ServerMessage.cs (Main) — Core-контракт
// намеренно её не видит, чтобы обратной зависимости не было.
//
// Шов создан в Этап 5, волна 5 ради выноса Notes в отдельный .csproj: после
// выноса NoteTaskSyncService сидит в Notes-вертикали и больше не имеет
// прямого доступа к IHubContext<SessionHub>. Эта обёртка живёт в Main
// (Services/Composition, рядом с другими форвардерами подсистем).
// Миграция Ф4: вместо IHubContext использует ISessionBroadcaster — префиксы
// собираются внутри SessionHubBroadcaster.
public sealed class NotesHubNotifier(ISessionBroadcaster broadcaster) : INotesHubNotifier
{
    public async Task BroadcastNotesChangedAsync(string userId, string action, string noteId, string? projectId = null)
    {
        var message = new NotesChangedMessage(action, noteId);
        // Проектная заметка — сначала группе проекта (участники: «заметка изменилась»),
        // затем вызывающему в его собственную группу: её держат другие вкладки и общий
        // список заметок, подписанный только на user_{id} (тот же резон, что у ToTaskBoard).
        // Членство fail-closed на стороне подписки (JoinProject), поэтому отозванный и
        // посторонний в группу не попадают и событие не получают. Личная — только владельцу.
        if (!string.IsNullOrEmpty(projectId))
            await broadcaster.ToProject(projectId, message);
        await broadcaster.ToOwner(userId, message);
    }

    public Task BroadcastTaskChangedAsync(string userId, string action, string taskId, string? projectId = null) =>
        // TaskChangedMessage хранит весь TaskItem (контракт шире, чем нужно для
        // подписки фронта «карточка обновилась → перезагрузить»), но фронт
        // использует только Id. Передаём минимальный TaskItem c одним id —
        // клиент всё равно перезапросит задачу через REST для отображения.
        broadcaster.ToTaskBoard(userId, projectId, new TaskChangedMessage(action,
            new TaskItem { Id = taskId, ProjectId = projectId }));
}
