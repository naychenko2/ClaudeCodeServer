using ClaudeHomeServer.Services.Memory;

namespace ClaudeHomeServer.Models;

// Тип знания в командной памяти (③-3.4). ВАЖНО: порядок значений менять НЕЛЬЗЯ — legacy-записи
// без поля Type десериализуются в значение 0 (Fact), это и есть бесплатная миграция старого стора.
public enum TeamMemoryType { Fact, Decision, Convention, Glossary }

// Как запись попала в командную память. ВАЖНО: порядок менять НЕЛЬЗЯ — legacy без поля Source → 0 (Manual).
public enum TeamMemorySource { Manual, AutoTurn, AutoMeeting }

// Запись общей памяти команды проекта (③-3.4): решение/договорённость/факт/термин, которую recall'ят
// все персоны команды проекта наравне с личной памятью. Хранится в data/team-memory.json.
public class TeamMemoryEntry : IMemoryEntry<TeamMemoryType>
{
    public string Id { get; init; } = Guid.NewGuid().ToString();
    public string OwnerId { get; set; } = "";
    public string ProjectId { get; set; } = "";
    public string Text { get; set; } = "";
    // Тип знания: решение / договорённость(конвенция) / факт / термин. Дефолт Fact — и для legacy-записей.
    public TeamMemoryType Type { get; set; } = TeamMemoryType.Fact;
    // Необязательные метки — задел под фильтрацию/группировку.
    public List<string>? Tags { get; set; }
    // Важность 0..1 (для гигиены/вытеснения в Волне 2); инициализатор 1.0 сохраняется у legacy без поля.
    public double Salience { get; set; } = 1.0;
    // Источник: руками / из хода / из совещания-группы. Дефолт Manual — и для legacy-записей.
    public TeamMemorySource Source { get; set; } = TeamMemorySource.Manual;
    // Сессия-источник авто-записи (атрибуция); null для ручного ввода.
    public string? SourceSessionId { get; set; }
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;
}
