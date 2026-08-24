namespace ClaudeHomeServer.Models;

// Значок проекта (ADR-009 §6). Номера закреплены ЯВНО, значение 1 выведено из
// обращения: в старом сторе им была растровая картинка (Image), enum лежит на диске
// числом, и без явных номеров каждая старая запись молча стала бы «значковой» с пустым
// значком. Ту же дисциплину обязан соблюдать любой, кто добавит сюда новое значение.
public enum ProjectIconKind { Initials = 0, Glyph = 2 }

// Тег проекта — элемент реестра общих тегов (имя, порядок, цвет)
public sealed class ProjectTag
{
    public string Name { get; set; } = "";
    public int Order { get; set; }
    public string? Color { get; set; }
}

// Иконка проекта: инициалы+цвет по умолчанию, подобранный значок опционально.
// Файлов у иконки больше нет — значок это данные записи, а не ассет (ADR-009 §6).
public class ProjectIcon
{
    public ProjectIconKind Kind { get; set; } = ProjectIconKind.Initials;
    // Ключ цвета из палитры AGENT_COLORS фронта (yellow/orange/blue/green/purple/red/brown/cyan/pink)
    public string? Color { get; set; }
    // Подобранный значок; null — не подбирался. Заполнен ⇔ Kind может быть Glyph.
    public ProjectGlyph? Glyph { get; set; }
}

// Значок (ADR-009 §6): имя иконки из белого списка lucide. Рисованные моделью пути
// вырезаны — значок всегда называется именем (инвариант держит валидатор на входе
// в стор, ProjectIconGlyphService.ValidateGlyph).
public sealed class ProjectGlyph
{
    // Имя из белого списка lucide
    public string? Name { get; set; }
    public DateTime SetAt { get; set; }
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
    // Дефолт-персона проекта («руководитель проекта»): итог онбординга проекта.
    // null — онбординг проекта не пройден.
    public string? DefaultPersonaId { get; set; }
    // Сессия незавершённого онбординга проекта — для резюма прерванного интервью.
    // Чистится при финализации (назначении дефолт-персоны проекта из этой сессии).
    // Сирота (сессию удалили через DELETE /api/chats/{id}) НЕ ломает повторный вход:
    // OnboardingController.StartProject нормализует ссылку на чтении — GetOwned по
    // несуществующему id даёт null и создаёт свежую сессию (каскада на удаление не нужно).
    public string? OnboardingSessionId { get; set; }
    // Дискриминатор «новый проект» для каркаса знакомства v2: null — создан до фичи
    // (каркас не предлагаем никогда), "pending" — новый, можно предложить (ставит Create),
    // "none" — человек отказался, иначе — ключ применённого пресета (ProjectPreset).
    // Миграции стора нет и быть не может: проход «всем null → none» неидемпотентен —
    // первый же рестарт погасил бы предложение у нового проекта. Nullable-поле в списке
    // projects.json формат стора не ломает, BackupSchema.Version не поднимаем.
    public string? PresetKey { get; set; }
    // Ключи серверов личного реестра MCP, включённых В ЭТОМ проекте — allow-модель доступа:
    // сервер доезжает в проект только при явном включении. null/пустой список = «не включён
    // никто». Ось каскада реестр → проект → персона (см. SessionManager.BuildExternalMcpProvider).
    public List<string>? McpServersOn { get; set; }
    // Фон рабочего пространства. null = генерацию НИКОГДА не пробовали —
    // единственный кандидат массового прогона (ADR-008 §10).
    public ProjectBackground? Background { get; set; }
    // Грань десктопного агента включена В ЭТОМ проекте (ADR-008 о десктопном агенте,
    // флаг desktop-agent) — вторая половина оси выдачи «проект + чат» (первая —
    // Session.DesktopChat). Ось намеренно проектная, а не персональная: привязка mcp:desktop
    // персоне действовала бы во ВСЕХ её чатах, включая ночной tasks-executor и регулярные.
    // Тумблер — рубильник, а не только состав: выключение гасит активные сеансы рук проекта
    // и рассылает cancel по их вызовам (живой процесс CLI иначе доработал бы ход со старым
    // составом инструментов).
    // Дефолт false: старые записи projects.json читаются штатно, BackupSchema.Version не
    // двигается (аддитивное поле с дефолтом формат не ломает).
    public bool DesktopAgentEnabled { get; set; }
    // Автоимпорт «Историй решений»: наблюдать ветку ccs/dossiers/v1 и подтягивать
    // паспорта по новому tip (сценарий «вторая машина / сосед по общей папке» —
    // ветку привозит чужой git pull, DossierAutoImporter только читает её).
    public bool AutoImportDossiers { get; set; } = false;
    // Порог автоправила архивации чатов проекта (флаг chat-auto-archive): убирать в архив
    // чаты проекта без сообщений дольше N дней. null = наследовать личный порог владельца
    // (User.ArchiveAfterDays); явное значение перекрывает его для этого проекта.
    // Nullable с дефолтом: старые записи projects.json читаются штатно, стор уже в бэкапе.
    public int? ArchiveAfterDays { get; set; }
}
