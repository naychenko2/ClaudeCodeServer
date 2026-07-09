namespace ClaudeHomeServer.Models;

// Заметка Obsidian-совместимой базы знаний. В отличие от задач, заметки — это
// НАСТОЯЩИЕ .md файлы на диске (личный vault data/notes/{userId} + notes/ проектов
// владельца), чтобы с ними работал и Claude (через файлы/MCP), и десктопный Obsidian.
// Здесь — DTO-проекции; хранилища-класса нет, источник правды — файлы (см. NotesService).

// Компактная запись для списка заметок.
public record NoteSummary(
    string Id,           // стабильный id = base64url(sourceKey|relPath)
    string Title,        // из frontmatter title или имени файла без .md
    string Source,       // "personal" | projectId
    string SourceLabel,  // "Личный" | имя проекта
    string Path,         // относительный путь внутри источника (forward slashes)
    IReadOnlyList<string> Tags,
    string CreatedAt,    // ISO 8601
    string UpdatedAt);

// Разрешённая исходящая ссылка [[...]] из заметки.
public record NoteLinkDto(
    string TargetId,        // id целевой заметки, либо "ghost:{normName}" для несозданной
    string TargetTitle,     // отображаемое имя цели
    bool Resolved);         // false = «призрачная» ссылка (цели ещё нет)

// Обратная ссылка: заметка, которая ссылается на текущую, + контекст-сниппет.
public record NoteBacklinkDto(
    string SourceId,
    string SourceTitle,
    string Source,
    string SourceLabel,
    string Snippet);        // строка-контекст, где стоит ссылка

// Полная карточка заметки.
public record NoteDetail(
    string Id,
    string Title,
    string Source,
    string SourceLabel,
    string Path,
    string Content,         // сырой markdown (весь файл, включая frontmatter) — для правки
    IReadOnlyList<string> Tags,
    IReadOnlyList<NoteLinkDto> Links,       // исходящие
    IReadOnlyList<NoteBacklinkDto> Backlinks,
    // «Несвязанные упоминания»: заголовки других заметок встречаются в тексте,
    // но без [[…]] — предложение связать (Snippet — строка-контекст).
    IReadOnlyList<NoteBacklinkDto> UnlinkedMentions,
    string CreatedAt,
    string UpdatedAt);

// Узел графа знаний. Ghost=true — «призрачная» заметка (на неё ссылаются, но её нет).
public record NoteGraphNode(
    string Id,
    string Title,
    string Source,
    string SourceLabel,
    int Degree,             // число связей (для размера узла)
    bool Ghost,
    IReadOnlyList<string>? Tags = null);   // теги заметки — для фильтра графа

public record NoteGraphEdge(string Source, string Target);

public record NoteGraph(
    IReadOnlyList<NoteGraphNode> Nodes,
    IReadOnlyList<NoteGraphEdge> Edges);

// Возможный источник для создания заметки (личный vault + проекты владельца) —
// отдаётся фронту, чтобы показать выбор «куда создать».
public record NoteSourceDto(string Key, string Label);

// Шаблон заметки (файл в templates/ личного vault)
public record NoteTemplateDto(string Id, string Title);

// --- Запросы ---

public record CreateNoteRequest(
    string Title,
    string? Content = null,
    string? Source = null,     // "personal" (по умолчанию) | projectId
    string? TemplateId = null, // имя шаблона из templates/ (без .md)
    string? Folder = null);    // папка внутри источника ("Идеи/Черновики"); пусто = корень

// Перенос заметки: в папку и/или другой источник (id меняется — источник+путь в id)
public record MoveNoteRequest(string? Folder = null, string? TargetSource = null);

// Переименование/перенос папки: newPath — полный новый путь ("Идеи" → "Архив/Идеи").
// Возвращается маппинг id заметок (старый → новый), чтобы фронт обновил выбор.
public record MoveFolderRequest(string Source, string Path, string NewPath);
public record MovedNoteId(string OldId, string NewId);

// Дата дня для daily note — клиент шлёт свою локальную (таймзона устройства)
public record DailyNoteRequest(string? Date = null);

// «Связать» несвязанное упоминание: обернуть первое вхождение заголовка в [[…]]
public record LinkMentionRequest(string TargetTitle);

public record UpdateNoteRequest(
    string? Title = null,     // непусто и отличается от текущего → переименование файла
    string? Content = null);
