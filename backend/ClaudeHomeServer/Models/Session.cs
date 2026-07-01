namespace ClaudeHomeServer.Models;

public enum SessionStatus { Starting, Working, Active, Waiting, Finished, Error, Orphaned }

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
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
