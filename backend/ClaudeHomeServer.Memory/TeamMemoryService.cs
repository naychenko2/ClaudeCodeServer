using ClaudeHomeServer.Core.Telemetry;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.Knowledge;

namespace ClaudeHomeServer.Services.Memory;

// Память команды проекта (③-3.4): общее хранилище решений/договорённостей/фактов/терминов проекта,
// из которого ВСЕ персоны команды recall'ят наравне с личной памятью — команда учится вместе, а не
// каждая про себя. Тонкая обёртка над MemoryShelf: стор data/team-memory.json (ключ «owner:project»),
// семантический слой в Dify-датасете «{username}:team:{projectName}». Здесь остаются только то, что
// специфично для команды проекта: гейты записи персон и длины ручной записи, имя датасета.
public class TeamMemoryService : Knowledge.IKnowledgeSyncParticipant, IDisposable
{
    private readonly MemoryShelf _shelf;

    public TeamMemoryService(IConfiguration config, ILogger<TeamMemoryService>? log = null,
        IKnowledgeIndex? knowledge = null, IDifyMetrics? metrics = null, IUserStore? users = null, IProjectManager? projects = null,
        Memory.MemoryWriteResolver? resolver = null, Llm.ICheapTextRunner? cheap = null)
    {
        _shelf = new MemoryShelf(new MemoryShelfOptions(
            StoreFileName: "team-memory.json",
            KnowledgeFileName: "team-memory-knowledge.json",
            DatasetName: (ownerId, projectId) =>
                $"{users?.GetById(ownerId)?.Username ?? ownerId}:team:{projects?.GetById(projectId)?.Name ?? projectId}",
            TargetLabel: "team",
            RecallHeader: "## Память команды проекта",
            RecallIntro: "Общие факты и договорённости проекта (помнят все персоны команды):",
            RecallIntroTruncated: "Общие факты и договорённости проекта (помнят все персоны команды; длинные записи обрезаны — полный текст записи через team_memory_list):"),
            config, log, knowledge, metrics, resolver, cheap);
    }

    // Настроен ли семантический слой (Dify). Без него — полнотекстовый fallback.
    public bool Available => _shelf.Available;

    // Настроенный потолок памяти команды (TeamMemory:MaxEntries)
    public int MaxEntries => _shelf.MaxEntries;

    public IReadOnlyList<TeamMemoryEntry> List(string ownerId, string projectId) =>
        _shelf.List(ownerId, projectId);

    // Все проектные scope'ы с непустой памятью — для полной проходки консолидатора.
    public IReadOnlyList<(string OwnerId, string ProjectId)> AllScopes() => _shelf.AllScopes();

    // Добавить запись командной памяти (дедуп-on-write — в полке).
    public TeamMemoryEntry Add(string ownerId, string projectId, string text,
        TeamMemoryType type = TeamMemoryType.Fact,
        TeamMemorySource source = TeamMemorySource.Manual,
        string? sourceSessionId = null, double? salience = null) =>
        _shelf.Add(ownerId, projectId, text, type, source, sourceSessionId, salience);

    // Семантический write-path: при настроенном Dify ищет близкий по смыслу дубль того же типа.
    public Task<TeamMemoryEntry> AddAsync(string ownerId, string projectId, string text,
        TeamMemoryType type = TeamMemoryType.Fact,
        TeamMemorySource source = TeamMemorySource.Manual,
        string? sourceSessionId = null, double? salience = null) =>
        _shelf.AddAsync(ownerId, projectId, text, type, source, sourceSessionId, salience);

    // Авто-путь с разрешением ПРОТИВОРЕЧИЙ (Memory #2, Mem0 ADD/UPDATE/DELETE/NOOP).
    public Task<TeamMemoryEntry?> AddWithResolutionAsync(string ownerId, string projectId, string text,
        TeamMemoryType type = TeamMemoryType.Fact, TeamMemorySource source = TeamMemorySource.AutoTurn,
        string? sourceSessionId = null, double? salience = null) =>
        _shelf.AddWithResolutionAsync(ownerId, projectId, text, type, source, sourceSessionId, salience);

    // Отредактировать текст записи вручную (UI-редактирование)
    public TeamMemoryEntry? Update(string ownerId, string projectId, string entryId, string text) =>
        _shelf.Update(ownerId, projectId, entryId, text);

    public bool Remove(string ownerId, string projectId, string entryId) =>
        _shelf.Remove(ownerId, projectId, entryId);

    // Результат recall'а: markdown-блок для промпта + записи, реально попавшие в блок
    // (для манифеста атрибуции F3 — «персона опирается на…», см. SessionManager). Text=null — пусто.
    public sealed record TeamRecallResult(string? Text, IReadOnlyList<TeamMemoryEntry> Used);

    // Полнотекстовый recall (fallback без Dify)
    public TeamRecallResult BuildRecallBlock(string ownerId, string projectId, string query, int topK = 4) =>
        _shelf.BuildRecallBlock(ownerId, projectId, query, topK);

    // Семантический recall (③-3.4); без Dify / при ошибке — полнотекстовый
    public Task<TeamRecallResult> BuildRecallBlockAsync(string ownerId, string projectId, string query, int topK = 4) =>
        _shelf.BuildRecallBlockAsync(ownerId, projectId, query, topK);

    // Поиск по памяти команды (для endpoint/MCP team_memory_search)
    public Task<IReadOnlyList<TeamMemoryEntry>> SearchAsync(string ownerId, string projectId,
        string query, int topK = 8) =>
        _shelf.SearchAsync(ownerId, projectId, query, topK);

    // Предел записи в блоке recall (подробности — MemoryShelf)
    public const int RecallTextLimit = MemoryShelf.RecallTextLimit;

    // Потолок ОДНОЙ записи на ручной записи (UI и MCP team_memory_remember/team_memory_update).
    // Память команды — «одна мысль на запись»: всё, что длиннее, в recall всё равно обрежется,
    // а в сторе и в Dify будет висеть целиком. Авто-захват и консолидация через него не идут —
    // там потеря факта из-за отказа хуже длинной записи. Уже сохранённые длинные записи
    // читаются и правятся как раньше: гейт стоит только на новом тексте.
    public const int MaxTextLength = 1000;

    // Гейт записи в память команды (③-3.4, диета памяти команды, ч.3): пишет либо «свой»
    // вызов без персоны (обычный проектный чат, ручное редактирование через UI), либо
    // персона ЭТОГО ЖЕ проекта. Глобальные персоны и консультанты других проектов —
    // read-only (team_memory_list/search остаются). Пара (callerPersonaId, caller) разводит
    // два смысла null-персоны: пустой id — «персоны нет» (разрешено), непустой id при
    // null-персоне — «не резолвилась» (удалена/чужой owner), и это отказ, а не молчаливое
    // разрешение. Единая точка для ProjectsController (заголовок X-Caller-Persona-Id
    // stdio-ветки) и MCP memory-сервера (http-тулсет): текст отказа читает модель,
    // рассинхрон копий менял бы поведение веток по-разному.
    public static string? WriteDeniedFor(string? callerPersonaId, Persona? caller, string projectId)
    {
        if (string.IsNullOrEmpty(callerPersonaId)) return null;
        if (caller is null)
            return "Персона, от имени которой идёт запись, не найдена (удалена или недоступна) — "
                + "запись в память команды отклонена. Не меняй общую память, пока пользователь "
                + "не уточнит актуальную персону.";
        if (PersonaZone.IsProjectTeam(caller, projectId)) return null;
        return "Запись в память команды доступна только персоне ЭТОГО проекта. Ты — "
            + "глобальная персона или консультант другого проекта: можешь читать общую память "
            + "(team_memory_list/team_memory_search), но не менять её. Попроси персону проекта "
            + "записать это или предложи пользователю.";
    }

    // Гейт длины записи: в recall она всё равно обрежется (RecallTextLimit), а простыня на
    // 2-3 КБ засоряет общий стор и Dify. current — длина уже сохранённого текста (0 при
    // создании): запрет срабатывает только на РОСТ сверх лимита, чтобы уже раздутую запись
    // можно было пересохранить без изменений или сократить (иначе её вообще нельзя было бы
    // привести в порядок). null — лимит не превышен.
    public static string? LengthViolation(string text, int current)
    {
        var length = text.Trim().Length;
        if (length <= MaxTextLength || length <= current) return null;
        return $"Запись памяти команды длиннее {MaxTextLength} символов (сейчас {length}). "
            + "Одна запись — одна мысль: разбей на несколько коротких или сократи до сути; "
            + "подробности держи в заметке или документе проекта.";
    }

    public int ApplyConsolidation(string ownerId, string projectId, IReadOnlyList<TeamMemoryConsolidationOp> ops) =>
        _shelf.ApplyConsolidation(ownerId, projectId, ops);

    public int EnforceCap(string ownerId, string projectId) => _shelf.EnforceCap(ownerId, projectId);

    // Полное удаление памяти команды проекта — при удалении проекта: Dify-датасет + оба стора
    public Task DeleteProjectTeamMemoryAsync(string ownerId, string projectId) =>
        _shelf.DeleteScopeAsync(ownerId, projectId);

    // Best-effort переименование Dify-датасета памяти команды при переименовании проекта
    // (имя «{username}:team:{projectName}» иначе стухает; работа по id не ломается)
    public Task RenameProjectDatasetAsync(string ownerId, string projectId, string username, string newProjectName) =>
        _shelf.RenameDatasetAsync(ownerId, projectId, $"{username}:team:{newProjectName}");

    // Уборка локальных сторов памяти команды всех проектов владельца — каскад удаления пользователя.
    public void DeleteOwnerTeamMemory(string ownerId) => _shelf.DeleteOwnerMemory(ownerId);

    public Task DeleteAllAsync(string userId) => _shelf.DeleteAllAsync(userId);

    public IReadOnlyList<Knowledge.KnowledgeSyncTarget> ListTargets() => _shelf.ListTargets();

    public Task<int> SyncAsync(string ownerId, string projectId) => _shelf.SyncAsync(ownerId, projectId);

    public void Dispose() => _shelf.Dispose();
}

// Noop-метрика для TeamMemoryService, когда IDifyMetrics не задан DI (юнит-тесты без Dify).
// Сигнатура DiffSyncAsync не раздваивается: метрика нужна для прогресса синка на проде,
// в тестах синк не идёт (Available == false), вызов метода — тихий no-op.
// Используем общее EmptyDifyMetrics из Core.Telemetry (Этап 5, шов IDifyMetrics).

// Операция консолидации памяти команды (P4): merge — схлопнуть несколько записей одного типа
// в одну сводную (Ids → новая запись Text/Type/Salience); drop — удалить запись Id.
public sealed record TeamMemoryConsolidationOp(
    string Op, List<string>? Ids, string? Id, TeamMemoryType? Type, string? Text, double? Salience)
    : IMemoryConsolidationOp<TeamMemoryType>
{
    public bool IsMerge => string.Equals(Op, "merge", StringComparison.OrdinalIgnoreCase);
    public bool IsDrop => string.Equals(Op, "drop", StringComparison.OrdinalIgnoreCase);
}
