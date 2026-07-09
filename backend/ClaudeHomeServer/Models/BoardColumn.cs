namespace ClaudeHomeServer.Models;

// Колонка Kanban-доски проекта. Category — семантическая категория статуса
// (To-Do/In-Progress/Done): за ней стоят поведения (recurrence, календарь, Claude, MCP).
// Несколько колонок могут иметь одну категорию; порядок — по позиции в списке.
public class BoardColumn
{
    public string Id { get; init; } = Guid.NewGuid().ToString();
    public string Name { get; set; } = "";
    public TaskItemStatus Category { get; set; } = TaskItemStatus.Todo;
    public string? Color { get; set; }
}
