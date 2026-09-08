namespace ClaudeHomeServer.Services.Notes;

// Мост к TaskManager (Main) для NoteTaskSyncService (Этап 5, волна 5):
// разрез цикла Notes → Tasks. Узкий Core-контракт под четыре метода, которые
// реально зовёт NoteTaskSyncService: GetBySourceNote/Create/Update/SpawnNextOccurrence.
//
// Типы Core-стороны (NoteTaskRef, NoteTaskCreateRequest, NoteTaskUpdateRequest,
// NoteTaskRecurrence, NoteTaskStatus) намеренно узкие — только то, что нужно
// Notes для моста чекбоксов заметок и задач. Полная модель TaskItem остаётся
// в Main: реализация моста в Main транслирует Core-запросы в TaskManager и
// обратно. Если позже Notes потребуется больше полей TaskItem — расширим
// Core-типы, но не раньше.

public enum NoteTaskStatus { Todo, InProgress, Done }

// Вид карточки — подмножество TaskKind (Main). Нужен NoteTaskSyncService, чтобы
// при тоггле чекбокса решить, проставлять ли DefectOutcome.ClosedWithoutCheck
// (см. ApplyTaskStatusAsync:150-152). Main-мост транслирует в TaskKind.
public enum NoteTaskKind { Task, Defect }

// Правило повторения — минимальное подмножество TaskRecurrence (Main):
// для чекбоксов заметок NoteTaskParser выставляет только Type, остальные поля
// (Interval/Weekdays/Until) остаются дефолтными. Core-тип несёт строку типа,
// Main-мост транслирует в TaskRecurrenceType.
public record NoteTaskRecurrence(string Type);

public record NoteTaskRef(
    string Id,
    string Title,
    int? SourceNoteLine,
    string? DueDate,
    NoteTaskStatus Status,
    NoteTaskRecurrence? Recurrence,
    NoteTaskKind Kind);

// Параметры создания задачи из чекбокса заметки (см. NoteTaskSyncService.PromoteAsync:53-59).
public record NoteTaskCreateRequest(
    string Title,
    string? DueDate,
    NoteTaskStatus Status,
    string SourceNoteId,
    int SourceNoteLine,
    NoteTaskRecurrence? Recurrence);

// Параметры апдейта задачи. Status — для тоггла чекбокса (см. ToggleAsync:153-155);
// DueDate — для срока 📅 (SetDueAsync), null = не менять, "" = очистить. Поле Kind
// не выставляется через мост: NoteTaskSyncService передаёт Outcome только при
// дефектах, и мост вычисляет Outcome по статусу Done + kind карточки сам.
public record NoteTaskUpdateRequest(NoteTaskStatus Status, string? DueDate = null);

public interface INoteTaskBridge
{
    // Все задачи, привязанные к чекбоксу указанной заметки (по SourceNoteId),
    // для панели «Задачи из заметки».
    IReadOnlyList<NoteTaskRef> GetBySourceNote(string noteId);

    // Создать задачу из чекбокса. Реализация в Main транслирует NoteTaskCreateRequest
    // в TaskManager.Create c CreateTaskRequest. Возвращает NoteTaskRef с id новой задачи.
    NoteTaskRef Create(string? projectId, string ownerId, NoteTaskCreateRequest req);

    // Правит статус задачи при тоггле чекбокса. Реализация в Main вызывает
    // TaskManager.Update c UpdateTaskRequest(Status: req.Status).
    NoteTaskRef? Update(string taskId, NoteTaskUpdateRequest req);

    // Спавн следующего вхождения повторяющейся задачи (см. ToggleAsync:161).
    // Возвращает null если повторение не настроено или следующего вхождения нет.
    NoteTaskRef? SpawnNextOccurrence(string completedTaskId);
}
