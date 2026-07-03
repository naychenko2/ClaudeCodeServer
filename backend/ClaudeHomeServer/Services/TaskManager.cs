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
            Assignee = req.Assignee,
            LinkedSessionId = req.LinkedSessionId,
            LinkedFiles = req.LinkedFiles ?? [],
            Subtasks = req.Subtasks?.Select(s => new TaskSubtask { Title = s.Title }).ToList() ?? [],
            Labels = req.Labels ?? [],
        };
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
        if (req.DueDate is not null) task.DueDate = req.DueDate == "" ? null : req.DueDate;
        if (req.DueTime is not null) task.DueTime = req.DueTime == "" ? null : req.DueTime;
        if (req.Assignee is not null) task.Assignee = req.Assignee;
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
    TaskItemAssignee? Assignee = null,
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
    TaskItemAssignee? Assignee = null,
    string? LinkedSessionId = null,
    List<string>? LinkedFiles = null,
    List<UpdateSubtaskRequest>? Subtasks = null,
    List<string>? Labels = null);

public record UpdateSubtaskRequest(string Id, string Title, bool IsDone);
