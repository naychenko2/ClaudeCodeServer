namespace ClaudeHomeServer.Models;

// Колонка Kanban-доски проекта. Category — семантическая категория статуса
// (To-Do/In-Progress/Done): за ней стоят поведения (recurrence, календарь, Claude, MCP).
// Несколько колонок могут иметь одну категорию; порядок — по позиции в списке.
// Role — признак «колонка ревью»: для дефектов требует заполненные шаги Repro.Steps
// (DefectRules.EnsureReproOnReview). Сейчас единственное значение "review"; null — обычная
// колонка, правило не действует. Имя строки стабильно для стора и фронта.
public class BoardColumn
{
    public string Id { get; init; } = Guid.NewGuid().ToString();
    public string Name { get; set; } = "";
    public TaskItemStatus Category { get; set; } = TaskItemStatus.Todo;
    public string? Color { get; set; }
    public string? Role { get; set; }
}
