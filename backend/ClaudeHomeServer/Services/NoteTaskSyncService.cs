using ClaudeHomeServer.Controllers;
using ClaudeHomeServer.Hubs;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;
using Microsoft.AspNetCore.SignalR;

namespace ClaudeHomeServer.Services;

// Двусторонняя связь чекбоксов заметок и задач (флаг notes-task-sync, MVP).
// Заметка — источник истины: чекбокс можно «промоутнуть» в настоящую задачу
// (появится в календаре, работают напоминания), а завершение с любой стороны
// синхронизирует галочку/статус. Связь — по (SourceNoteId, SourceNoteLine).
public sealed class NoteTaskSyncService(
    NotesService notes, TaskManager tasks, ProjectManager projects, NotesKnowledgeService kb,
    IHubContext<SessionHub> hub, ILogger<NoteTaskSyncService> log)
{
    // Чекбоксы заметки + связанные задачи (для панели «Задачи из заметки»)
    public IReadOnlyList<NoteTaskDto> ListForNote(string userId, string noteId)
    {
        var note = notes.GetDetail(userId, noteId)
            ?? throw new KeyNotFoundException("Заметка не найдена");

        var byLine = new Dictionary<int, TaskItem>();
        foreach (var t in tasks.GetBySourceNote(noteId))
            if (t.SourceNoteLine is int ln) byLine[ln] = t; // последняя выигрывает

        return NoteTaskParser.Parse(note.Content).Select(l =>
        {
            byLine.TryGetValue(l.Line, out var t);
            return new NoteTaskDto(l.Line, l.Text, l.Done, l.Due,
                t?.Id, t?.Status.ToString().ToLowerInvariant());
        }).ToList();
    }

    // Промоут чекбокса в настоящую задачу. Повторный промоут той же строки — no-op
    // (возвращает существующую задачу).
    public async Task<TaskItem> PromoteAsync(string userId, string noteId, int line)
    {
        var note = notes.GetDetail(userId, noteId)
            ?? throw new KeyNotFoundException("Заметка не найдена");
        var parsed = NoteTaskParser.Parse(note.Content).FirstOrDefault(l => l.Line == line)
            ?? throw new InvalidOperationException("На этой строке нет задачи-чекбокса");

        var existing = tasks.GetBySourceNote(noteId).FirstOrDefault(t => t.SourceNoteLine == line);
        if (existing is not null) return existing;

        // Проектная заметка → задача проекта; личный vault / read-only источник → личная
        var projectId = note.Source != "personal" && projects.GetById(note.Source)?.OwnerId == userId
            ? note.Source
            : null;

        var task = tasks.Create(projectId, userId, new CreateTaskRequest(
            Title: parsed.Text,
            DueDate: parsed.Due,
            Recurrence: parsed.Recurrence,
            Status: parsed.Done ? TaskItemStatus.Done : TaskItemStatus.Todo,
            SourceNoteId: noteId,
            SourceNoteLine: line));

        await hub.BroadcastTaskChangedAsync(userId, "created", task);
        log.LogInformation("Чекбокс заметки {NoteId}:{Line} промоутнут в задачу {TaskId}", noteId, line, task.Id);
        return task;
    }

    // Тоггл чекбокса из заметки: правит .md + синхронизирует связанную задачу.
    public async Task<NoteDetail> ToggleAsync(string userId, string noteId, int line, bool done)
    {
        var note = notes.GetDetail(userId, noteId)
            ?? throw new KeyNotFoundException("Заметка не найдена");
        var updatedContent = NoteTaskParser.SetChecked(note.Content, line, done)
            ?? throw new InvalidOperationException("На этой строке нет задачи-чекбокса");

        var saved = notes.Update(userId, noteId, new UpdateNoteRequest(Content: updatedContent))
            ?? throw new InvalidOperationException("Заметка не обновилась");
        kb.QueueSync(userId);
        await BroadcastNoteChangedAsync(userId, noteId);

        var linked = tasks.GetBySourceNote(noteId).FirstOrDefault(t => t.SourceNoteLine == line);
        if (linked is not null)
            await ApplyTaskStatusAsync(userId, linked, done);

        return saved;
    }

    // Установить/убрать срок 📅 на строке-чекбоксе из UI (дейт-пикер в секции «Задачи из
    // заметки») — без ручного ввода эмодзи. Правит .md + синхронизирует срок связанной задачи.
    // due=null/пусто — убрать срок.
    public async Task<NoteDetail> SetDueAsync(string userId, string noteId, int line, string? due)
    {
        var note = notes.GetDetail(userId, noteId)
            ?? throw new KeyNotFoundException("Заметка не найдена");
        var updatedContent = NoteTaskParser.SetDue(note.Content, line, due)
            ?? throw new InvalidOperationException("На этой строке нет задачи-чекбокса");

        var saved = notes.Update(userId, noteId, new UpdateNoteRequest(Content: updatedContent))
            ?? throw new InvalidOperationException("Заметка не обновилась");
        kb.QueueSync(userId);
        await BroadcastNoteChangedAsync(userId, noteId);

        // Синхронизируем срок связанной задачи (пусто → очистить)
        var linked = tasks.GetBySourceNote(noteId).FirstOrDefault(t => t.SourceNoteLine == line);
        if (linked is not null)
        {
            var updated = tasks.Update(linked.Id, new UpdateTaskRequest(DueDate: due ?? ""));
            if (updated is not null) await hub.BroadcastTaskChangedAsync(userId, "updated", updated);
        }
        return saved;
    }

    // Обратная запись: статус задачи → галочка в заметке. Вызывается из TasksController.Update
    // при смене done-состояния (покрывает UI, MCP tasks_complete, Claude-исполнителя).
    public async Task SyncTaskToNoteAsync(string userId, TaskItem task)
    {
        if (task.SourceNoteId is null || task.SourceNoteLine is null) return;
        var note = notes.GetDetail(userId, task.SourceNoteId);
        if (note is null) return;

        var done = task.Status == TaskItemStatus.Done;
        var updatedContent = NoteTaskParser.SetChecked(note.Content, task.SourceNoteLine.Value, done);
        if (updatedContent is null || updatedContent == note.Content) return; // строка сдвинулась/совпадает

        // Обратная запись — побочный эффект обновления задачи: не должна ронять запрос
        // (например, источник заметки стал read-only → NotesService бросит UnauthorizedAccess).
        try
        {
            if (notes.Update(userId, task.SourceNoteId, new UpdateNoteRequest(Content: updatedContent)) is null) return;
            kb.QueueSync(userId);
            await BroadcastNoteChangedAsync(userId, task.SourceNoteId);
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Не удалось синхронизировать галочку в заметке {NoteId} для задачи {TaskId}",
                task.SourceNoteId, task.Id);
        }
    }

    // Перевод связанной задачи в done/todo напрямую через TaskManager (не через контроллер —
    // чтобы не зациклить обратную запись). Регулярная задача при завершении спавнит следующую.
    private async Task ApplyTaskStatusAsync(string userId, TaskItem task, bool done)
    {
        var newStatus = done ? TaskItemStatus.Done : TaskItemStatus.Todo;
        if (task.Status == newStatus) return;

        var wasDone = task.Status == TaskItemStatus.Done;
        var updated = tasks.Update(task.Id, new UpdateTaskRequest(Status: newStatus));
        if (updated is null) return;
        await hub.BroadcastTaskChangedAsync(userId, "updated", updated);

        if (!wasDone && done && updated.Recurrence is not null)
        {
            var next = tasks.SpawnNextOccurrence(updated);
            if (next is not null) await hub.BroadcastTaskChangedAsync(userId, "created", next);
        }
    }

    private Task BroadcastNoteChangedAsync(string userId, string noteId) =>
        hub.Clients.Group("user_" + userId)
            .SendAsync("message", new NotesChangedMessage("updated", noteId));
}

// Строка-чекбокс заметки + связанная задача (если промоутнута)
public record NoteTaskDto(int Line, string Text, bool Done, string? Due, string? TaskId, string? TaskStatus);

// Запросы эндпоинтов «задачи из заметок»
public record PromoteTaskRequest(int Line);
public record ToggleTaskRequest(int Line, bool Done);
public record SetDueRequest(int Line, string? Due);
