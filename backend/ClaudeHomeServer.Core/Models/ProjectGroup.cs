namespace ClaudeHomeServer.Models;

// Группа проектов на вкладке «Проекты». Проекты ссылаются на неё через Project.GroupId.
public class ProjectGroup
{
    public string Id { get; init; } = Guid.NewGuid().ToString();
    public string Name { get; set; } = "";
    public string Color { get; set; } = "";      // hex из палитры, напр. "#3E7CA6"
    public int Order { get; set; }                // порядок в списке
    public string? OwnerId { get; set; }
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
