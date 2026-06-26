namespace ClaudeHomeServer.Models;

public class WorkspaceKnowledge
{
    public string RootPath { get; set; } = "";
    public string? DifyDatasetId { get; set; }
    public Dictionary<string, List<string>>? DocumentTags { get; set; }
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
