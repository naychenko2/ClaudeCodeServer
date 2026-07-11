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

    // Задача, к которой привязана сессия (Claude-исполнитель)
    public TaskItem? GetBySession(string sessionId) =>
        _tasks.Values.FirstOrDefault(t => t.LinkedSessionId == sessionId);

    public IReadOnlyCollection<TaskItem> GetByOwner(string userId) =>
        _tasks.Values.Where(t => t.OwnerId == userId)
            .OrderBy(t => t.DueDate ?? "9999").ThenBy(t => t.CreatedAt).ToList();

    public IReadOnlyCollection<TaskItem> GetByProject(string projectId) =>
        _tasks.Values.Where(t => t.ProjectId == projectId)
            .OrderBy(t => t.DueDate ?? "9999").ThenBy(t => t.CreatedAt).ToList();

    // Задачи, промоутнутые из чекбоксов конкретной заметки (флаг notes-task-sync)
    public IReadOnlyCollection<TaskItem> GetBySourceNote(string noteId) =>
        _tasks.Values.Where(t => t.SourceNoteId == noteId).ToList();

    public TaskItem Create(string? projectId, string ownerId, CreateTaskRequest req)
    {
        // Клиентский id (офлайн-создание) с идемпотентностью: повтор POST с тем же id
        // при потерянном ack возвращает существующую задачу — без дубля. Если id занят
        // другим владельцем — игнорируем присланный, генерируем новый.
        var id = string.IsNullOrEmpty(req.Id) ? Guid.NewGuid().ToString() : req.Id;
        if (_tasks.TryGetValue(id, out var dup))
        {
            if (dup.OwnerId == ownerId) return dup;
            id = Guid.NewGuid().ToString();
        }

        var task = new TaskItem
        {
            Id = id,
            ProjectId = projectId,
            OwnerId = ownerId,
            Title = req.Title,
            Description = req.Description ?? "",
            Status = req.Status ?? TaskItemStatus.Todo,
            ColumnId = string.IsNullOrEmpty(req.ColumnId) ? null : req.ColumnId,
            Priority = req.Priority ?? TaskItemPriority.Medium,
            DueDate = string.IsNullOrEmpty(req.DueDate) ? null : req.DueDate,
            DueTime = string.IsNullOrEmpty(req.DueTime) ? null : req.DueTime,
            ReminderMinutes = req.ReminderMinutes is < 0 ? null : req.ReminderMinutes,
            Recurrence = req.Recurrence is { Type: not TaskRecurrenceType.None } ? req.Recurrence : null,
            Assignee = req.Assignee,
            LinkedSessionId = req.LinkedSessionId,
            PersonaId = string.IsNullOrEmpty(req.PersonaId) ? null : req.PersonaId,
            LinkedFiles = req.LinkedFiles ?? [],
            Subtasks = req.Subtasks?.Select(s => new TaskSubtask { Title = s.Title }).ToList() ?? [],
            Labels = req.Labels ?? [],
            SourceNoteId = req.SourceNoteId,
            SourceNoteLine = req.SourceNoteLine,
            Order = NextOrder(ownerId),
        };
        // Серия регулярной задачи начинается с её первого экземпляра
        if (task.Recurrence is not null) task.SeriesId = task.Id;
        // Инвариант: исполнитель-персона подразумевает исполнение силами Claude
        NormalizePersonaAssignee(task);
        _tasks[task.Id] = task;
        Save();
        return task;
    }

    // Персона-исполнитель имеет смысл только у Claude-исполнения: если задаче назначена
    // персона, принудительно ставим Assignee=Claude (иначе автозапуск планировщиком,
    // завязанный на Assignee==Claude, не подхватит задачу — рассинхрон).
    private static void NormalizePersonaAssignee(TaskItem task)
    {
        if (task.PersonaId is not null) task.Assignee = TaskItemAssignee.Claude;
    }

    // Следующее значение Order для новой задачи владельца — в конец глобального порядка.
    // Внутри колонки относительный порядок сохраняется (сортировка на доске по Order).
    private double NextOrder(string ownerId) =>
        _tasks.Values.Where(t => t.OwnerId == ownerId).Select(t => t.Order).DefaultIfEmpty(0).Max() + 1000;

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
        // Персона-исполнитель: null = не менять, "" = убрать (как у строковых полей)
        if (req.PersonaId is not null)
            task.PersonaId = req.PersonaId == "" ? null : req.PersonaId;
        if (req.LinkedFiles is not null) task.LinkedFiles = req.LinkedFiles;
        if (req.Labels is not null) task.Labels = req.Labels;
        if (req.Order is not null) task.Order = req.Order.Value;
        // Колонка доски: явное значение ("" = сброс на дефолт), иначе — если статус сменили
        // НЕ через доску (columnId не прислали), колонка устаревает → сбрасываем на дефолт категории
        if (req.ColumnId is not null) task.ColumnId = req.ColumnId == "" ? null : req.ColumnId;
        else if (req.Status is not null) task.ColumnId = null;
        if (req.Subtasks is not null)
            task.Subtasks = req.Subtasks.Select(s => new TaskSubtask
            {
                Id = string.IsNullOrEmpty(s.Id) ? Guid.NewGuid().ToString() : s.Id,
                Title = s.Title,
                IsDone = s.IsDone,
            }).ToList();

        // Инвариант «персона ⇒ Claude» — после того как учли и Assignee, и PersonaId
        NormalizePersonaAssignee(task);
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
            // Исполнитель-персона переносится в следующий экземпляр — иначе регулярная
            // задача теряла бы персону и падала на обычного Claude (ClaudeStartedAt/
            // ClaudeResult/LinkedSessionId у нового экземпляра дефолтные → отработает заново)
            PersonaId = completed.PersonaId,
            Recurrence = completed.Recurrence,
            SeriesId = completed.SeriesId ?? completed.Id,
            LinkedFiles = [.. completed.LinkedFiles],
            Subtasks = completed.Subtasks.Select(s => new TaskSubtask { Title = s.Title }).ToList(),
            Labels = [.. completed.Labels],
            Order = NextOrder(completed.OwnerId ?? ""),
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

    // Запуск Claude-исполнителя: связка с сессией + перевод в работу
    public TaskItem? MarkClaudeStarted(string id, string sessionId, DateTime atUtc)
    {
        var task = _tasks.GetValueOrDefault(id);
        if (task is null) return null;
        task.LinkedSessionId = sessionId;
        task.ClaudeStartedAt = atUtc;
        task.ClaudeResult = null;
        if (task.Status == TaskItemStatus.Todo) task.Status = TaskItemStatus.InProgress;
        task.UpdatedAt = DateTime.UtcNow;
        Save();
        return task;
    }

    // Итог хода Claude-исполнителя (success/error)
    public TaskItem? MarkClaudeResult(string id, string result)
    {
        var task = _tasks.GetValueOrDefault(id);
        if (task is null) return null;
        task.ClaudeResult = result;
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
        var list = JsonFileStore.Load<List<TaskItem>>(_storePath,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        if (list is null) return;
        foreach (var t in list)
            _tasks[t.Id] = t;
        MigrateOrders();
    }

    // Одноразовая миграция Order: задачам с Order == 0 (созданным до появления доски)
    // присваиваем возрастающие значения (шаг 1000) в текущем порядке сортировки —
    // чтобы на доске не было «смешанных нулей». Выполняется на владельца.
    private void MigrateOrders()
    {
        var changed = false;
        foreach (var group in _tasks.Values.GroupBy(t => t.OwnerId))
        {
            var unset = group.Where(t => t.Order == 0)
                .OrderBy(t => t.DueDate ?? "9999").ThenBy(t => t.CreatedAt).ToList();
            if (unset.Count == 0) continue;
            var baseOrder = group.Select(t => t.Order).Where(o => o != 0).DefaultIfEmpty(0).Max();
            for (var i = 0; i < unset.Count; i++)
                unset[i].Order = baseOrder + (i + 1) * 1000;
            changed = true;
        }
        if (changed) Save();
    }

    private void Save()
    {
        lock (_saveLock)
        {
            JsonFileStore.Save(_storePath, _tasks.Values.ToList());
        }
    }
}

public record CreateTaskRequest(
    string Title,
    // Клиентский id для офлайн-создания (идемпотентный replay). null/пусто → сервер генерит Guid.
    string? Id = null,
    string? Description = null,
    TaskItemStatus? Status = null,
    string? ColumnId = null,
    TaskItemPriority? Priority = null,
    string? DueDate = null,
    string? DueTime = null,
    int? ReminderMinutes = null,
    TaskItemAssignee? Assignee = null,
    TaskRecurrence? Recurrence = null,
    string? LinkedSessionId = null,
    // Исполнение от лица персоны (assignee=Claude); null/пусто — обычный Claude
    string? PersonaId = null,
    List<string>? LinkedFiles = null,
    List<CreateSubtaskRequest>? Subtasks = null,
    List<string>? Labels = null,
    string? SourceNoteId = null,
    int? SourceNoteLine = null);

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
    // Персона-исполнитель: null = не менять, "" = убрать
    string? PersonaId = null,
    List<string>? LinkedFiles = null,
    List<UpdateSubtaskRequest>? Subtasks = null,
    List<string>? Labels = null,
    // Порядок карточки на доске (drag внутри/между колонок); null = не менять
    double? Order = null,
    // Колонка доски проекта; null = не менять, "" = сброс на дефолт категории
    string? ColumnId = null);

public record UpdateSubtaskRequest(string Id, string Title, bool IsDone);
