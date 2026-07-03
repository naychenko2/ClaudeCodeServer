namespace ClaudeHomeServer.Models;

// Задача пользователя; может быть привязана к проекту или быть личной (ProjectId == null).
// Хранение — data/tasks.json (TaskManager).
public class TaskItem
{
    public string Id { get; init; } = Guid.NewGuid().ToString();
    public string? ProjectId { get; set; }
    public string? OwnerId { get; set; }
    public string Title { get; set; } = "";
    public string Description { get; set; } = "";   // markdown
    public TaskItemStatus Status { get; set; } = TaskItemStatus.Todo;
    public TaskItemPriority Priority { get; set; } = TaskItemPriority.Medium;
    public string? DueDate { get; set; }   // ISO: YYYY-MM-DD
    public string? DueTime { get; set; }   // HH:MM
    // Напоминание: офсет до срока в минутах (0 = в момент срока); null — без напоминания
    public int? ReminderMinutes { get; set; }
    // UTC-отметка отправленного напоминания (идемпотентность планировщика);
    // сбрасывается при изменении срока или офсета
    public DateTime? ReminderSentAt { get; set; }
    public TaskItemAssignee? Assignee { get; set; }
    public string? LinkedSessionId { get; set; }
    public List<string> LinkedFiles { get; set; } = [];
    public List<TaskSubtask> Subtasks { get; set; } = [];
    public List<string> Labels { get; set; } = [];
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

public class TaskSubtask
{
    public string Id { get; init; } = Guid.NewGuid().ToString();
    public string Title { get; set; } = "";
    public bool IsDone { get; set; }
}

public enum TaskItemStatus { Todo, InProgress, Done }
public enum TaskItemPriority { Low, Medium, High, Urgent }
public enum TaskItemAssignee { Me, Claude }
