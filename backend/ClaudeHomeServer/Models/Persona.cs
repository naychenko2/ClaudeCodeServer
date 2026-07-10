namespace ClaudeHomeServer.Models;

// Зона контекста персоны: Global — доступ ко всем данным владельца (заметки/задачи/проекты),
// Project — только к своему проекту.
public enum PersonaScope { Global, Project }

// Тип долгой памяти (таксономия 2026):
// Semantic — устойчивые факты/предпочтения; Episodic — резюме прошлых диалогов;
// Procedural — выученные приёмы/правила поведения.
public enum PersonaMemoryType { Semantic, Episodic, Procedural }

// Вид аватара: Initials — круг с инициалами на цветном фоне; Image — сгенерированная/загруженная картинка.
public enum PersonaAvatarKind { Initials, Image }

// Внешний вид персоны.
public class PersonaAvatar
{
    public PersonaAvatarKind Kind { get; set; } = PersonaAvatarKind.Initials;
    // Ключ цвета из палитры AGENT_COLORS фронта (yellow/orange/blue/green/purple/red/brown/cyan/pink)
    public string? Color { get; set; }
    // Имя файла картинки в data/personas/{id}/ (когда Kind == Image)
    public string? ImageFile { get; set; }
}

// «Олицетворённый агент» — сущность с именем, внешностью, характером, своей памятью и зоной контекста.
public class Persona
{
    public string Id { get; init; } = Guid.NewGuid().ToString();
    public string OwnerId { get; set; } = "";
    public string Name { get; set; } = "";
    // Короткий slug (латиница/цифры/дефис) — для Dify-датасета памяти и будущих @упоминаний
    public string Handle { get; set; } = "";
    // Краткое «кто это» — для карточки в списке
    public string? Description { get; set; }
    // Характер/роль/стиль — тело персоны, инжектится в системный промпт сессии
    public string? SystemPrompt { get; set; }
    // Модель CLI (алиас/id любого провайдера); null = дефолт сервера
    public string? Model { get; set; }
    public string? Effort { get; set; }
    public PersonaScope Scope { get; set; } = PersonaScope.Global;
    // Для Scope == Project — id проекта, к которому привязана персона
    public string? ProjectId { get; set; }
    public PersonaAvatar Avatar { get; set; } = new();
    // Первое приветственное сообщение при открытии чата (опционально)
    public string? Greeting { get; set; }
    public bool MemoryEnabled { get; set; } = true;
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

// Запись долгой памяти персоны. Хранится в data/persona-memory-{personaId}.json;
// семантическая часть дублируется в Dify-датасет для векторного retrieve.
public class PersonaMemoryEntry
{
    public string Id { get; init; } = Guid.NewGuid().ToString();
    public string PersonaId { get; set; } = "";
    public PersonaMemoryType Type { get; set; } = PersonaMemoryType.Semantic;
    public string Text { get; set; } = "";
    public List<string>? Tags { get; set; }
    // Значимость записи (0..1), влияет на скоринг при recall
    public double Salience { get; set; } = 1.0;
    // Сессия, из которой факт был запомнен (для трассировки)
    public string? SourceSessionId { get; set; }
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;
    public DateTime LastAccessedAt { get; set; } = DateTime.UtcNow;
}
