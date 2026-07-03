using System.Collections.Concurrent;
using System.Text.Json;
using ClaudeHomeServer.Models;

namespace ClaudeHomeServer.Services;

// Задачи: in-memory + data/tasks.json (по образцу ProjectManager)
public class TaskManager
{
    private readonly ConcurrentDictionary<string, TaskItem> _tasks = new();
    private readonly string _storePath;
    private readonly Lock _saveLock = new();

    public TaskManager(IConfiguration config)
    {
        var dataDir = Path.GetDirectoryName(
            config["DataPath"] ?? Path.Combine(AppContext.BaseDirectory, "data", "projects.json"))!;
        _storePath = Path.Combine(dataDir, "tasks.json");
        Load();
    }

    public TaskItem? GetById(string id) => _tasks.GetValueOrDefault(id);

    public IReadOnlyCollection<TaskItem> GetByOwner(string userId) =>
        _tasks.Values.Where(t => t.OwnerId == userId)
            .OrderBy(t => t.DueDate ?? "9999").ThenBy(t => t.CreatedAt).ToList();

    public IReadOnlyCollection<TaskItem> GetByProject(string projectId) =>
        _tasks.Values.Where(t => t.ProjectId == projectId)
            .OrderBy(t => t.DueDate ?? "9999").ThenBy(t => t.CreatedAt).ToList();

    public TaskItem Create(string? projectId, string ownerId, CreateTaskRequest req)
    {
        var task = new TaskItem
        {
            ProjectId = projectId,
            OwnerId = ownerId,
            Title = req.Title,
            Description = req.Description ?? "",
            Status = req.Status ?? TaskItemStatus.Todo,
            Priority = req.Priority ?? TaskItemPriority.Medium,
            DueDate = string.IsNullOrEmpty(req.DueDate) ? null : req.DueDate,
            DueTime = string.IsNullOrEmpty(req.DueTime) ? null : req.DueTime,
            ReminderMinutes = req.ReminderMinutes is < 0 ? null : req.ReminderMinutes,
            Recurrence = req.Recurrence is { Type: not TaskRecurrenceType.None } ? req.Recurrence : null,
            Assignee = req.Assignee,
            LinkedSessionId = req.LinkedSessionId,
            LinkedFiles = req.LinkedFiles ?? [],
            Subtasks = req.Subtasks?.Select(s => new TaskSubtask { Title = s.Title }).ToList() ?? [],
            Labels = req.Labels ?? [],
        };
        // Серия регулярной задачи начинается с её первого экземпляра
        if (task.Recurrence is not null) task.SeriesId = task.Id;
        _tasks[task.Id] = task;
        Save();
        return task;
    }

    public TaskItem? Update(string id, UpdateTaskRequest req)
    {
        var task = _tasks.GetValueOrDefault(id);
        if (task is null) return null;

        if (req.Title is not null) task.Title = req.Title;
        if (req.Description is not null) task.Description = req.Description;
        if (req.Status is not null) task.Status = req.Status.Value;
        if (req.Priority is not null) task.Priority = req.Priority.Value;
        // Пустая строка = очистить поле, null = не менять
        var dueBefore = (task.DueDate, task.DueTime, task.ReminderMinutes);
        if (req.DueDate is not null) task.DueDate = req.DueDate == "" ? null : req.DueDate;
        if (req.DueTime is not null) task.DueTime = req.DueTime == "" ? null : req.DueTime;
        // Для int-поля семантика очистки — отрицательное значение (аналог "" у строк)
        if (req.ReminderMinutes is not null)
            task.ReminderMinutes = req.ReminderMinutes < 0 ? null : req.ReminderMinutes;
        // Срок или офсет поменялись — напоминание должно сработать заново
        if (dueBefore != (task.DueDate, task.DueTime, task.ReminderMinutes))
            task.ReminderSentAt = null;
        if (req.Assignee is not null) task.Assignee = req.Assignee;
        // Type=None — убрать повторение (сентинел), null — не менять
        if (req.Recurrence is not null)
        {
            task.Recurrence = req.Recurrence.Type == TaskRecurrenceType.None ? null : req.Recurrence;
            if (task.Recurrence is not null) task.SeriesId ??= task.Id;
        }
        if (req.LinkedSessionId is not null)
            task.LinkedSessionId = req.LinkedSessionId == "" ? null : req.LinkedSessionId;
        if (req.LinkedFiles is not null) task.LinkedFiles = req.LinkedFiles;
        if (req.Labels is not null) task.Labels = req.Labels;
        if (req.Subtasks is not null)
            task.Subtasks = req.Subtasks.Select(s => new TaskSubtask
            {
                Id = string.IsNullOrEmpty(s.Id) ? Guid.NewGuid().ToString() : s.Id,
                Title = s.Title,
                IsDone = s.IsDone,
            }).ToList();

        task.UpdatedAt = DateTime.UtcNow;
        Save();
        return task;
    }

    // Следующий экземпляр регулярной задачи после завершения текущего.
    // Подзадачи копируются со сброшенными галочками; напоминание и правило переносятся.
    // null — серия закончена (Until), нет срока/правила.
    public TaskItem? SpawnNextOccurrence(TaskItem completed)
    {
        if (completed.Recurrence is null || completed.DueDate is null) return null;
        var nextDate = TaskRecurrenceCalculator.NextDueDate(completed.DueDate, completed.Recurrence);
        if (nextDate is null) return null;

        var task = new TaskItem
        {
            ProjectId = completed.ProjectId,
            OwnerId = completed.OwnerId,
            Title = completed.Title,
            Description = completed.Description,
            Priority = completed.Priority,
            DueDate = nextDate,
            DueTime = completed.DueTime,
            ReminderMinutes = completed.ReminderMinutes,
            Assignee = completed.Assignee,
            Recurrence = completed.Recurrence,
            SeriesId = completed.SeriesId ?? completed.Id,
            LinkedFiles = [.. completed.LinkedFiles],
            Subtasks = completed.Subtasks.Select(s => new TaskSubtask { Title = s.Title }).ToList(),
            Labels = [.. completed.Labels],
        };
        _tasks[task.Id] = task;
        Save();
        return task;
    }

    // Отметка планировщика об отправленном напоминании (идемпотентность между тиками и рестартами)
    public TaskItem? MarkReminderSent(string id, DateTime atUtc)
    {
        var task = _tasks.GetValueOrDefault(id);
        if (task is null) return null;
        task.ReminderSentAt = atUtc;
        Save();
        return task;
    }

    public bool Delete(string id)
    {
        var removed = _tasks.TryRemove(id, out _);
        if (removed) Save();
        return removed;
    }

    // Зачистка при удалении проекта
    public IReadOnlyCollection<string> DeleteByProject(string projectId)
    {
        var ids = _tasks.Values.Where(t => t.ProjectId == projectId).Select(t => t.Id).ToList();
        foreach (var id in ids)
            _tasks.TryRemove(id, out _);
        if (ids.Count > 0) Save();
        return ids;
    }

    private void Load()
    {
        if (!File.Exists(_storePath)) return;
        try
        {
            var json = File.ReadAllText(_storePath);
            var list = JsonSerializer.Deserialize<List<TaskItem>>(json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (list is null) return;
            foreach (var t in list)
                _tasks[t.Id] = t;
        }
        catch { /* первый запуск или повреждённый файл */ }
    }

    private void Save()
    {
        lock (_saveLock)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_storePath)!);
            File.WriteAllText(_storePath, JsonSerializer.Serialize(_tasks.Values.ToList()));
        }
    }
}

public record CreateTaskRequest(
    string Title,
    string? Description = null,
    TaskItemStatus? Status = null,
    TaskItemPriority? Priority = null,
    string? DueDate = null,
    string? DueTime = null,
    int? ReminderMinutes = null,
    TaskItemAssignee? Assignee = null,
    TaskRecurrence? Recurrence = null,
    string? LinkedSessionId = null,
    List<string>? LinkedFiles = null,
    List<CreateSubtaskRequest>? Subtasks = null,
    List<string>? Labels = null);

public record CreateSubtaskRequest(string Title);

public record UpdateTaskRequest(
    string? Title = null,
    string? Description = null,
    TaskItemStatus? Status = null,
    TaskItemPriority? Priority = null,
    string? DueDate = null,
    string? DueTime = null,
    // null = не менять, отрицательное = убрать напоминание
    int? ReminderMinutes = null,
    TaskItemAssignee? Assignee = null,
    // null = не менять, Type=None = убрать повторение
    TaskRecurrence? Recurrence = null,
    string? LinkedSessionId = null,
    List<string>? LinkedFiles = null,
    List<UpdateSubtaskRequest>? Subtasks = null,
    List<string>? Labels = null);

public record UpdateSubtaskRequest(string Id, string Title, bool IsDone);
