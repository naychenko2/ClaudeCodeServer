using ClaudeHomeServer.Models;

namespace ClaudeHomeServer.Services.Notes;

// Шов обратной записи (Этап 5, под-волна: разрыв Main → NoteTaskSyncService):
// TasksController/TasksToolset (Main) вызывают `SyncTaskToNoteAsync`, чтобы при
// смене done-состояния задачи поставить/снять галочку в заметке-источнике.
//
// Направление: Main → Notes (обратное к `INoteTaskBridge`, который даёт
// NoteTaskSyncService доступ К TaskManager). Реализация — сам
// `NoteTaskSyncService` в вертикали Notes (DI-фабрика в NotesSubsystem.Register).
//
// Сигнатура на `TaskItem` (Core): метод читает из него только Id/SourceNoteId/
// SourceNoteLine/Status, но тащит полный объект, чтобы call-site не делал
// ручного копирования. Когда vertical Notes окончательно оторвётся от Main,
// задача — заменить на узкий Core-record (см. комментарий NoteTaskSyncService:118-122).
public interface INoteTaskSync
{
    // Обратная запись: статус задачи → галочка в заметке-источнике.
    // no-op, если задача не привязана к заметке (SourceNoteId == null)
    // или заметка/строка уже совпадает (no diff).
    Task SyncTaskToNoteAsync(string userId, TaskItem task);
}
