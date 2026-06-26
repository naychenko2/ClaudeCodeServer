namespace ClaudeHomeServer.Models;

public class Project
{
    public string Id { get; init; } = Guid.NewGuid().ToString();
    public string Name { get; set; } = "";
    public string RootPath { get; set; } = "";
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public string? OwnerId { get; set; }
    public string? DifyDatasetId { get; set; }
    public string? SystemPrompt { get; set; }
    public Dictionary<string, List<string>>? DocumentTags { get; set; }
}
