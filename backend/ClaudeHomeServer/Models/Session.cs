namespace ClaudeHomeServer.Models;

public enum SessionStatus { Starting, Working, Active, Waiting, Finished, Error, Orphaned }

// Происхождение чата — вычисляется из TaskId/AutomationRuleId (см. Session.Origin),
// отдельно не хранится, чтобы не было второго источника истины.
public enum ChatOrigin { Manual, Task, Automation }

// Режимы прав — соответствуют значениям флага --permission-mode у claude CLI
public enum ClaudeMode { Default, AcceptEdits, Plan, Auto, DontAsk, Bypass }

public static class ClaudeModeExtensions
{
    // Значение флага --permission-mode для claude CLI
    public static string ToCliFlag(this ClaudeMode mode) => mode switch
    {
        ClaudeMode.AcceptEdits => "acceptEdits",
        ClaudeMode.Plan => "plan",
        ClaudeMode.Auto => "auto",
        ClaudeMode.DontAsk => "dontAsk",
        ClaudeMode.Bypass => "bypassPermissions",
        _ => "default",
    };

    // Wire-токен для фронта (совпадает с именами режимов в frontend/src/lib/modes.ts)
    public static string ToWireToken(this ClaudeMode mode) => mode switch
    {
        ClaudeMode.AcceptEdits => "acceptEdits",
        ClaudeMode.Plan => "plan",
        ClaudeMode.Auto => "auto",
        ClaudeMode.DontAsk => "dontAsk",
        ClaudeMode.Bypass => "bypass",
        _ => "default",
    };
}

// Состояние цикла «до готово» (флаг work-loop, идея ralph/ulw-loop из oh-my-openagent):
// присутствие объекта у сессии = цикл активен; остановка обнуляет поле.
public class SessionWorkLoop
{
    // Маркер завершения: агент выводит <promise>{Promise}</promise>, когда всё сделано
    public string Promise { get; set; } = "ГОТОВО";
    // Номер текущей итерации (растёт с каждым автопродолжением)
    public int Iteration { get; set; }
    // Потолок итераций — защита от бесконечного цикла (дефолт из конфига Loop:MaxIterations)
    public int MaxIterations { get; set; } = 20;
    // working — рабочие итерации; verifying — финальный верификационный ход после маркера
    public string Phase { get; set; } = "working";
}

public class Session
{
    public string Id { get; init; } = Guid.NewGuid().ToString();
    // null → чат вне проекта (project-less). set (не init) — задел под будущее «прикрепить проект»
    public string? ProjectId { get; set; }
    // Владелец project-less чата (JWT sub). Для проектных сессий null — владелец резолвится через проект
    public string? OwnerId { get; set; }
    // Закреплён в списке чатов («Закреплённые»)
    public bool IsPinned { get; set; }
    public string? ClaudeSessionId { get; set; }
    public ClaudeMode Mode { get; set; } = ClaudeMode.AcceptEdits;
    // Псевдоним или полный id модели для флага --model. null → дефолтная модель CLI
    public string? Model { get; set; }
    // Уровень reasoning effort для флага --effort (low/medium/high/xhigh/max). null → дефолт CLI
    public string? Effort { get; set; }
    public SessionStatus Status { get; set; } = SessionStatus.Starting;
    public string? LastMessage { get; set; }
    public int MessageCount { get; set; }
    public string? Name { get; set; }
    // Имя агента (.claude/agents/<name>.md), чей промпт инжектируется в системный контекст
    public string? AgentName { get; set; }
    // Персона, от лица которой ведётся чат: задаёт характер,
    // модель и зону контекста (см. Persona). null — обычная сессия.
    // В групповом чате — АКТИВНЫЙ спикер (∈ Participants).
    public string? PersonaId { get; set; }
    // Участники группового чата (2-4 id персон; первый — ведущая). null — обычный чат.
    public List<string>? Participants { get; set; }
    // Собеседника меняли по ходу разговора: в персона-промпт добавляется оговорка,
    // что прошлые ответы в транскрипте могли быть от другого собеседника.
    public bool PersonaSwitched { get; set; }
    // Заметка-итог сессии (кнопка «Итог сессии»): повторная генерация обновляет её, а не плодит дубли
    public string? SummaryNoteId { get; set; }
    // Временный чат: авто-удаление через N минут после последней активности (UpdatedAt). null — обычный
    public int? ExpiresAfterMinutes { get; set; }
    // Цикл «до готово» (флаг work-loop): не null — ход автопродолжается до маркера завершения
    public SessionWorkLoop? WorkLoop { get; set; }
    // Сессия-исполнитель задачи (создана TaskExecutionService): tasks-MCP форсируется включённым
    // независимо от Persona.Tools — исполнитель обязан управлять задачей через mcp__tasks__*.
    // Иначе персона с ограничением tools (без «tasks») теряет tasks-сервер и не может ни прочитать,
    // ни завершить задачу (fallback на встроенный Task-тул → «система задач недоступна»).
    public bool TaskExecution { get; set; }
    // Задача-владелец чата-исполнителя (TaskExecutionService): для отображения контекста
    // («в рамках какой задачи») на плашке чата, в шапке и в артефактах сессии.
    public string? TaskId { get; set; }
    // Origin автоматизации: null — обычный чат; иначе — id правила PersonaAutomationRule,
    // чат которого создан движком проактивности. Для фильтрации авто-чатов и трассировки.
    public string? AutomationRuleId { get; set; }
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    // Тип происхождения чата — производный от TaskId/AutomationRuleId, единая точка истины.
    public ChatOrigin Origin => TaskId != null ? ChatOrigin.Task
        : AutomationRuleId != null ? ChatOrigin.Automation
        : ChatOrigin.Manual;
}
