namespace ClaudeHomeServer.Models;

// Запись общей памяти команды проекта (③-3.4): факт/договорённость, которую recall'ят все
// персоны команды проекта наравне с личной памятью. Хранится в data/team-memory.json.
public class TeamMemoryEntry
{
    public string Id { get; init; } = Guid.NewGuid().ToString();
    public string OwnerId { get; set; } = "";
    public string ProjectId { get; set; } = "";
    public string Text { get; set; } = "";
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;
}
