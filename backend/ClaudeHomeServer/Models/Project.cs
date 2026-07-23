namespace ClaudeHomeServer.Models;

public enum ProjectIconKind { Initials, Image }

// Иконка проекта (по образцу PersonaAvatar). Кроп переиспользуем — AvatarCropState нейтрален.
public class ProjectIcon
{
    public ProjectIconKind Kind { get; set; } = ProjectIconKind.Initials;
    // Ключ цвета из палитры AGENT_COLORS фронта (yellow/orange/blue/green/purple/red/brown/cyan/pink)
    public string? Color { get; set; }
    // Имя файла картинки в data/project-icons/{id}/ (когда Kind == Image)
    public string? ImageFile { get; set; }
    // Оригинал загруженного файла (для перекропа); у сгенерированных иконок — null
    public string? OriginalFile { get; set; }
    // Параметры кропа, которыми получен ImageFile из OriginalFile
    public AvatarCropState? Crop { get; set; }
}

public class Project
{
    public string Id { get; init; } = Guid.NewGuid().ToString();
    public string Name { get; set; } = "";
    public string RootPath { get; set; } = "";
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public string? OwnerId { get; set; }
    // Группа проектов; null = проект вне групп (см. ProjectGroup)
    public string? GroupId { get; set; }
    public string? DifyDatasetId { get; set; }
    public string? SystemPrompt { get; set; }
    public bool ShowHiddenFiles { get; set; } = false;
    public bool ToolsEnabled { get; set; } = false;
    public Dictionary<string, List<string>>? DocumentTags { get; set; }
    // Правила авто-разрешений/запретов для permission-запросов (см. PermissionRule)
    public List<PermissionRule>? PermissionRules { get; set; }
    // Кастомные колонки Kanban-доски проекта; null = дефолтные 3 (по категориям статусов)
    public List<BoardColumn>? BoardColumns { get; set; }
    // Git: clone URL репозитория на Forgejo (null — remote не подключён; сам факт
    // «в папке есть git» определяется по .git, отдельного флага нет)
    public string? GitRemoteUrl { get; set; }
    // Режим документов: авто-commit после каждого хода Claude (+push при GitAutoPush)
    public bool GitAutoCommit { get; set; } = false;
    public bool GitAutoPush { get; set; } = false;
    // Иконка проекта: инициалы+цвет по умолчанию, картинка (сгенерированная/загруженная) опционально
    public ProjectIcon Icon { get; set; } = new();
}
