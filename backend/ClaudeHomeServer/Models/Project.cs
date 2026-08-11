namespace ClaudeHomeServer.Models;

public enum ProjectIconKind { Initials, Image }

// Тег проекта — элемент реестра общих тегов (имя, порядок, цвет)
public sealed class ProjectTag
{
    public string Name { get; set; } = "";
    public int Order { get; set; }
    public string? Color { get; set; }
}

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

// Состояние фона проекта (ADR-008 §6). Pending — генерация в работе (протухает через
// 10 минут, если сервер упал), Standard — пользователь вернул стандартный дудл,
// Failed — генерация не удалась (повтор только руками).
public enum ProjectBackgroundKind { Pending, Generated, Standard, Failed }

// Фон проекта: сам тайл лежит файлом в data/project-backgrounds/{id}/, в записи — только
// ссылка и состояние (стор читается и пишется целиком, тайл на 2-6 КБ ему не место).
public sealed class ProjectBackground
{
    public ProjectBackgroundKind Kind { get; set; }
    // Имя файла в data/project-backgrounds/{id}/ (только при Kind == Generated)
    public string? TileFile { get; set; }
    public DateTime? StartedAt { get; set; }
    public DateTime? GeneratedAt { get; set; }
    public int Attempts { get; set; }
    // no-model | bad-json | rejected | io
    public string? FailReason { get; set; }
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
    public Dictionary<string, List<string>>? DocumentTags { get; set; }
    // Область документации для панели «Документы». Везде null — «дефолт», а пустой
    // список — осознанный выбор «ничего отсюда»:
    //   DocsFolders    — папки (относительные пути от RootPath), дефолт docs/;
    //   DocsRootFiles  — файлы в корне поимённо, дефолт README.md (в корне лежит и код,
    //                    поэтому папкой он не выбирается — только конкретные файлы);
    //   DocsTypes      — группы типов файлов («markdown», «pdf», «visio»…), дефолт markdown;
    //   DocsHome       — документ «Начала» панели; null — авто (README в корне).
    public List<string>? DocsFolders { get; set; }
    public List<string>? DocsRootFiles { get; set; }
    public List<string>? DocsTypes { get; set; }
    public string? DocsHome { get; set; }
    // Реестр общих тегов проекта (имя, порядок, цвет) — per-owner изоляция
    public List<ProjectTag> TagRegistry { get; set; } = [];
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
    // Проектный override промпта AI-генерации сообщения коммита (панель «Изменения»);
    // null — использовать глобальный (User.GitCommitPrompt) или дефолт. Пусто ("") = очищено.
    public string? CommitPromptOverride { get; set; }
    // Иконка проекта: инициалы+цвет по умолчанию, картинка (сгенерированная/загруженная) опционально
    public ProjectIcon Icon { get; set; } = new();
    // Дефолт-персона проекта («руководитель проекта», фича default-personas-onboarding):
    // итог обязательного онбординга проекта. null — онбординг проекта не пройден.
    public string? DefaultPersonaId { get; set; }
    // Сессия незавершённого онбординга проекта — для резюма прерванного интервью.
    // Чистится при финализации (назначении дефолт-персоны проекта из этой сессии).
    // Сирота (сессию удалили через DELETE /api/chats/{id}) НЕ ломает повторный вход:
    // OnboardingController.StartProject нормализует ссылку на чтении — GetOwned по
    // несуществующему id даёт null и создаёт свежую сессию (каскада на удаление не нужно).
    public string? OnboardingSessionId { get; set; }
    // Ключи серверов личного реестра MCP, включённых В ЭТОМ проекте — allow-модель доступа:
    // сервер доезжает в проект только при явном включении. null/пустой список = «не включён
    // никто». Ось каскада реестр → проект → персона (см. SessionManager.BuildExternalMcpProvider).
    public List<string>? McpServersOn { get; set; }
    // Фон рабочего пространства (фича project-backgrounds). null = генерацию НИКОГДА не
    // пробовали — единственный кандидат массового прогона (ADR-008 §10).
    public ProjectBackground? Background { get; set; }
}
