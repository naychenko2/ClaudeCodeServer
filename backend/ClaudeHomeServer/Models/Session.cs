namespace ClaudeHomeServer.Models;

public enum SessionStatus { Starting, Working, Active, Waiting, Finished, Error, Orphaned }
public enum ClaudeMode { Auto, Plan, Ask }

public class Session
{
    public string Id { get; init; } = Guid.NewGuid().ToString();
    public string ProjectId { get; init; } = "";
    public string? ClaudeSessionId { get; set; }
    public ClaudeMode Mode { get; set; } = ClaudeMode.Auto;
    // Псевдоним или полный id модели для флага --model. null → дефолтная модель CLI
    public string? Model { get; set; }
    public SessionStatus Status { get; set; } = SessionStatus.Starting;
    public string? LastMessage { get; set; }
    public int MessageCount { get; set; }
    public string? Name { get; set; }
    // Имя агента (.claude/agents/<name>.md), чей промпт инжектируется в системный контекст
    public string? AgentName { get; set; }
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
