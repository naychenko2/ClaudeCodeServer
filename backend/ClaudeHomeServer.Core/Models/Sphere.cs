namespace ClaudeHomeServer.Models;

// Сфера — бывшая группа проектов на вкладке «Проекты»: долгоживущая область со своей
// командой персон и памятью. Проекты ссылаются на неё через Project.GroupId (JSON-имя
// и файл groups.json сохранены ради совместимости).
public class Sphere
{
    // Потолок хартии сферы, символов
    public const int CharterMaxLength = 4000;

    public string Id { get; init; } = Guid.NewGuid().ToString();
    public string Name { get; set; } = "";
    public string Color { get; set; } = "";      // hex из палитры, напр. "#3E7CA6"
    public int Order { get; set; }                // порядок в списке
    public string? OwnerId { get; set; }
    // Имя глифа lucide из белого списка (ADR-009); null — значка нет
    public string? Icon { get; set; }
    // Хартия сферы (markdown), не длиннее CharterMaxLength
    public string? Charter { get; set; }
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
