namespace ClaudeCodeServer.Models;

public enum SessionStatus { Starting, Active, Waiting, Finished, Error }
public enum ClaudeMode { Auto, Plan, Ask }

public class Session
{
    public string Id { get; init; } = Guid.NewGuid().ToString();
    public string ProjectId { get; init; } = "";
    public string? ClaudeSessionId { get; set; }
    public ClaudeMode Mode { get; init; } = ClaudeMode.Auto;
    public SessionStatus Status { get; set; } = SessionStatus.Starting;
    public string? LastMessage { get; set; }
    public int MessageCount { get; set; }
    public string? Name { get; set; }
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
