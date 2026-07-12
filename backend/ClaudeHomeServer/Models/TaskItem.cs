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
    // Колонка на доске проекта (BoardColumn.Id); null = дефолтная колонка своей категории.
    // Status остаётся категорией — за ним вся семантика; ColumnId лишь уточняет размещение.
    public string? ColumnId { get; set; }
    public TaskItemPriority Priority { get; set; } = TaskItemPriority.Medium;
    public string? DueDate { get; set; }   // ISO: YYYY-MM-DD
    public string? DueTime { get; set; }   // HH:MM
    // Напоминание: офсет до срока в минутах (0 = в момент срока); null — без напоминания
    public int? ReminderMinutes { get; set; }
    // UTC-отметка отправленного напоминания (идемпотентность планировщика);
    // сбрасывается при изменении срока или офсета
    public DateTime? ReminderSentAt { get; set; }
    public TaskItemAssignee? Assignee { get; set; }
    // Правило повторения; при завершении экземпляра создаётся следующий (см. TaskManager)
    public TaskRecurrence? Recurrence { get; set; }
    // Общий id серии повторяющейся задачи (= id первого экземпляра)
    public string? SeriesId { get; set; }
    public string? LinkedSessionId { get; set; }
    // Исполнение от лица персоны: сессия-исполнитель ведётся с её характером,
    // моделью и памятью (null — обычный Claude)
    public string? PersonaId { get; set; }
    // Claude-исполнитель: отметка запуска (идемпотентность автозапуска, переживает рестарт)
    public DateTime? ClaudeStartedAt { get; set; }
    // Итог последнего запуска: success | error; null — ещё выполняется или не запускалась
    public string? ClaudeResult { get; set; }
    // Markdown-описание итога выполнения (прикрепляет исполнитель через tasks_complete/
    // tasks_update). null — результата нет; "" — очищен. Не переносится в следующий
    // экземпляр регулярной задачи (как ClaudeResult/LinkedSessionId — серия начинается заново).
    public string? ResultMarkdown { get; set; }
    public List<string> LinkedFiles { get; set; } = [];
    public List<TaskSubtask> Subtasks { get; set; } = [];
    public List<string> Labels { get; set; } = [];
    // Связь с чекбоксом заметки (флаг notes-task-sync): задача создана из строки-чекбокса
    // заметки. Завершение задачи ставит галочку в заметке и наоборот. Line — 0-based
    // индекс строки в контенте заметки на момент промоута (best-effort при правках).
    public string? SourceNoteId { get; set; }
    public int? SourceNoteLine { get; set; }
    // Порядок карточки на Kanban-доске (ручная сортировка внутри колонки).
    // double — чтобы вставлять между соседями через midpoint без перенумерации.
    // 0 = не назначен (миграция/сортировка по дефолту); задаётся в Create и при drag.
    public double Order { get; set; }
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

// Правило повторения задачи. Weekdays — ISO-дни недели (1=Пн … 7=Вс), только для Weekly.
// Until — последняя допустимая дата серии (YYYY-MM-DD, включительно).
public class TaskRecurrence
{
    public TaskRecurrenceType Type { get; set; } = TaskRecurrenceType.Daily;
    public int Interval { get; set; } = 1;   // каждые N дней/недель/месяцев/лет
    public List<int>? Weekdays { get; set; }
    public string? Until { get; set; }
}

// None — wire-сентинел в UpdateTaskRequest: «убрать повторение» (аналог "" у строк)
public enum TaskRecurrenceType { None, Daily, Weekly, Monthly, Yearly }
