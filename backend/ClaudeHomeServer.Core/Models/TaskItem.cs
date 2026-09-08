using ClaudeHomeServer.Services;

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
    // Время жизни чата исполнения (мин от последней активности); null — бессрочно.
    // Применяется один раз при создании сессии в TaskExecutionService.ExecuteAsync;
    // имеет смысл только при Assignee=Claude. Дефолт 1440 (сутки) на новых задачах.
    public int? ExecutionExpiresAfterMinutes { get; set; } = 1440;
    // Уровень модели исполнителя: под что именно эта работа — проектирование (Strong),
    // реализация по плану (Medium), механическая правка (Weak). null — не задан: модель
    // возьмётся от персоны-исполнителя и назначения места (см. TaskExecutionService.ResolveExecutorModel).
    public ModelTier? ModelTier { get; set; }
    // Отдельное git worktree для чата-исполнителя: путь СУЩЕСТВУЮЩЕГО дерева (хостовый) и
    // его ветка. Дерево здесь не создаётся — поле означает «работай в этом дереве» (штаб
    // «Командной реализации» раздаёт исполнителям своё). null — исполнитель стартует в корне
    // проекта; у личной задачи (ProjectId == null) поля не хранятся — worktree без репы
    // проекта не бывает (TaskManager.NormalizeWorktree).
    public string? WorktreePath { get; set; }
    public string? WorktreeBranch { get; set; }
    // Claude-исполнитель: отметка запуска (идемпотентность автозапуска, переживает рестарт)
    public DateTime? ClaudeStartedAt { get; set; }
    // Итог последнего запуска: success | error; null — ещё выполняется или не запускалась
    public string? ClaudeResult { get; set; }
    // Исполнитель встал насовсем (не «ход упал», а «дальше работать нечем»): отметка времени
    // и причина на проводе (auth_failed — не удалось авторизоваться у провайдера модели).
    // Отдельного статуса нет намеренно — задача остаётся InProgress, признак лишь снимает
    // с «в работе» немой характер (уведомление владельцу + пометка в карточке).
    // Сбрасывается при повторном запуске исполнителя (TaskManager.MarkClaudeStarted).
    public DateTime? ExecutorStoppedAt { get; set; }
    public string? ExecutorStopReason { get; set; }
    // Страховка «ход кончился, а задачу никто не закрыл» (TaskExecutionService.CheckStalledExecutorAsync):
    // отметки о напоминании исполнителю и об уведомлении человека. Ровно по одному на запуск —
    // обе сбрасываются перезапуском исполнителя (MarkClaudeStarted), а не каждым ходом:
    // иначе многошаговая задача получала бы напоминание после каждого своего хода.
    public DateTime? ExecutorNudgedAt { get; set; }
    public DateTime? ExecutorStaleAlertedAt { get; set; }
    // Идемпотентность join двух независимых сигналов завершения (CT-8): R — конец хода
    // (ClaudeResult) и D — Status=Done (tasks_complete/PUT). CAS TaskManager.TryMarkCompletionDelivered
    // гарантирует ровно одну доставку L0/Z-доклада вне зависимости от порядка их прихода.
    // НЕ переносится в SpawnNextOccurrence (как ClaudeResult — серия начинается заново).
    public bool CompletionDelivered { get; set; }
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
    // Происхождение задачи: персона-постановщик (создала задачу через tasks_create из своего
    // чата) и чат, породивший задачу. Оба null у задач из UI/API без персоны (старые задачи —
    // тоже null, обратная совместимость). SourceSessionId — общее поле двух фич: уведомление
    // постановщика о завершении (L0) и иерархия чатов (Session.ParentSessionId вычисляется
    // из него — второй источник истины не заводим).
    public string? CreatedByPersonaId { get; set; }
    public string? SourceSessionId { get; set; }
    // Глубина цепочки делегирования: 0 — создана не из чата-исполнителя (обычный чат/UI/API);
    // из чата-исполнителя родительской задачи — её DelegationDepth + 1 (TaskManager.Create).
    // Гейт TASKS_EXECUTE (ClaudeSession, через Session.TaskDelegationDepth) запрещает запуск
    // нового исполнителя при >= 3 — обрывает рекурсивное размножение задач-исполнителей.
    public int DelegationDepth { get; set; }
    // Порядок карточки на Kanban-доске (ручная сортировка внутри колонки).
    // double — чтобы вставлять между соседями через midpoint без перенумерации.
    // 0 = не назначен (миграция/сортировка по дефолту); задаётся в Create и при drag.
    public double Order { get; set; }
    // Вид карточки: обычная задача или дефект. Дефект обязан пройти правила DefectRules
    // (см. Services/DefectRules.cs): нельзя создать сразу в Done, требуются шаги Repro
    // на review-колонке и вердикт Verification (или ClosedWithoutCheck) при закрытии.
    public TaskKind Kind { get; set; } = TaskKind.Task;
    // Шаги воспроизведения дефекта. Steps обязателен только при попадании в review-колонку
    // (прочерчено в DefectRules.EnsureReproOnReview). Expected/Actual — свободные поля
    // постановщика для контекста.
    public DefectRepro? Repro { get; set; }
    // Вердикт проверки дефекта. VerifiedAt ставит сервер, PersonaId — из X-Caller-Session-Id
    // (заголовок подделываем — это гигиена атрибуции, не защита). What/Result присылает клиент.
    // Сбрасывается при уходе из Done вместе с Outcome.
    public TaskVerification? Verification { get; set; }
    // Итоговый исход дефекта. ClosedWithoutCheck — для внутренних путей закрытия (галочка
    // заметки, снятие волной), которые не оставляют системного Verification. Прочие значения
    // ставит человек/персона. Сбрасывается при уходе из Done.
    public DefectOutcome? Outcome { get; set; }
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    // Дата+время завершения: когда статус стал Done. null — не завершена.
    // Сбрасывается при переоткрытии (статус ≠ Done). Не переносится в следующий
    // экземпляр регулярной задачи (как ClaudeResult/ResultMarkdown — серия начинается заново).
    public DateTime? CompletedAt { get; set; }
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
