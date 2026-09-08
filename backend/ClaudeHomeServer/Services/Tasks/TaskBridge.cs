using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.Notes;

namespace ClaudeHomeServer.Services.Tasks;

// Реализация INoteTaskBridge (Core) поверх TaskManager (Main): транслирует
// Core-запросы (узкие NoteTaskRef/Create/Update/SpawnNextOccurrence) в TaskManager
// и обратно. Живёт рядом с TaskManager, регистрируется в DI как синглтон.
//
// Core-контракт намеренно узкий (Этап 5, волна 5): Notes нужны только id,
// SourceNoteLine, Status, Recurrence — этого хватает для моста чекбоксов
// заметок и задач. Полный TaskItem Notes не нужен, и перетаскивать его в
// Core ради двух полей — overkill.
public sealed class TaskBridge(TaskManager tasks) : INoteTaskBridge
{
    public IReadOnlyList<NoteTaskRef> GetBySourceNote(string noteId) =>
        tasks.GetBySourceNote(noteId)
            .Select(MapToCore)
            .ToList();

    public NoteTaskRef Create(string? projectId, string ownerId, NoteTaskCreateRequest req)
    {
        var task = tasks.Create(projectId, ownerId, new CreateTaskRequest(
            Title: req.Title,
            DueDate: req.DueDate,
            Status: MapStatus(req.Status),
            SourceNoteId: req.SourceNoteId,
            SourceNoteLine: req.SourceNoteLine,
            Recurrence: MapRecurrence(req.Recurrence)));
        return MapToCore(task);
    }

    public NoteTaskRef? Update(string taskId, NoteTaskUpdateRequest req)
    {
        // Outcome для дефектов выставляется здесь, в мосте — NoteTaskSyncService
        // держит только Core-тип NoteTaskUpdateRequest без Kind. Загружаем задачу
        // через bridge.GetBySourceNote не нужно: достаточно прочитать kind из TaskManager
        // (GetById уже в TaskManager).
        var existing = tasks.GetById(taskId);
        if (existing is null) return null;
        var outcome = req.Status == NoteTaskStatus.Done && existing.Kind == TaskKind.Defect
            ? DefectOutcome.ClosedWithoutCheck
            : (DefectOutcome?)null;
        var task = tasks.Update(taskId, new UpdateTaskRequest(
            Status: MapStatus(req.Status),
            DueDate: req.DueDate,
            Outcome: outcome));
        return task is null ? null : MapToCore(task);
    }

    public NoteTaskRef? SpawnNextOccurrence(string completedTaskId)
    {
        // NoteTaskRef хранит id; для Spawn нужен полный TaskItem. Догружаем через
        // GetBySourceNote неэффективно — даём прямой доступ к TaskManager.
        // TaskManager.Update(...) нам недоступен по id без загрузки, поэтому:
        var completed = tasks.GetById(completedTaskId);
        if (completed is null) return null;
        var next = tasks.SpawnNextOccurrence(completed);
        return next is null ? null : MapToCore(next);
    }

    // --- mapping ---

    private static NoteTaskRef MapToCore(TaskItem t) =>
        new(t.Id, t.Title, t.SourceNoteLine, t.DueDate, MapStatus(t.Status),
            MapRecurrence(t.Recurrence), MapKind(t.Kind));

    private static TaskItemStatus MapStatus(NoteTaskStatus s) => s switch
    {
        NoteTaskStatus.Todo => TaskItemStatus.Todo,
        NoteTaskStatus.InProgress => TaskItemStatus.InProgress,
        NoteTaskStatus.Done => TaskItemStatus.Done,
        _ => TaskItemStatus.Todo,
    };

    private static NoteTaskStatus MapStatus(TaskItemStatus s) => s switch
    {
        TaskItemStatus.Todo => NoteTaskStatus.Todo,
        TaskItemStatus.InProgress => NoteTaskStatus.InProgress,
        TaskItemStatus.Done => NoteTaskStatus.Done,
        _ => NoteTaskStatus.Todo,
    };

    private static NoteTaskKind MapKind(TaskKind k) => k switch
    {
        TaskKind.Defect => NoteTaskKind.Defect,
        _ => NoteTaskKind.Task,
    };

    private static TaskKind MapKind(NoteTaskKind k) => k switch
    {
        NoteTaskKind.Defect => TaskKind.Defect,
        _ => TaskKind.Task,
    };

    private static NoteTaskRecurrence? MapRecurrence(TaskRecurrence? r) =>
        r is null ? null : new NoteTaskRecurrence(r.Type.ToString().ToLowerInvariant());

    private static TaskRecurrence? MapRecurrence(NoteTaskRecurrence? r) =>
        r is null ? null : new TaskRecurrence { Type = ParseType(r.Type) };

    private static TaskRecurrenceType ParseType(string t) => t.ToLowerInvariant() switch
    {
        "daily" => TaskRecurrenceType.Daily,
        "weekly" => TaskRecurrenceType.Weekly,
        "monthly" => TaskRecurrenceType.Monthly,
        "yearly" => TaskRecurrenceType.Yearly,
        _ => TaskRecurrenceType.None,
    };
}
