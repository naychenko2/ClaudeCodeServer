namespace ClaudeHomeServer.Models;

// Роль — «персонаж-собеседник» проекта: человеческое лицо (имя/аватар/характер)
// плюс набор компетенций (прикреплённые агенты из .claude/agents).
// Системный промпт роли = Persona + тела агентов (AgentNames) + опц. SystemPrompt.
public class Role
{
    public string Id { get; init; } = Guid.NewGuid().ToString();
    public string ProjectId { get; init; } = "";
    // Человеческое имя ("Игорь")
    public string Name { get; set; } = "";
    // Должность ("Backend-разработчик")
    public string Title { get; set; } = "";
    // Аватар-эмодзи; если пусто — UI рисует инициалы-фолбэк
    public string Avatar { get; set; } = "";
    // Цвет аватара/пузыря в UI (hex)
    public string Color { get; set; } = "";
    // Характер, стиль речи — «лицо» роли
    public string Persona { get; set; } = "";
    // Прикреплённые агенты (FileName из .claude/agents) — компетенции роли
    public List<string> AgentNames { get; set; } = [];
    // Опциональный свободный текст поверх агентов (доп. инструкции конкретно этой роли)
    public string? SystemPrompt { get; set; }
    // Модель по умолчанию для чатов с ролью (псевдоним/полный id для --model)
    public string? Model { get; set; }
    // Reasoning effort по умолчанию (low/medium/high/xhigh/max)
    public string? Effort { get; set; }
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
