using ClaudeHomeServer.Core.Telemetry;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.Knowledge;

namespace ClaudeHomeServer.Services.Memory;

// Память сферы: общая полка решений/договорённостей/фактов/терминов, которую видят все проекты и
// персоны сферы. Тонкая обёртка над MemoryShelf: стор data/sphere-memory.json (ключ «owner:sphereId»),
// семантический слой в Dify-датасете «{username}:sphere:{sphereId}». Запись — TeamMemoryEntry
// (новых значений TeamMemorySource нет); поднятая из проекта запись несёт PromotedFrom. Autolearn
// в эту полку не пишет — только ручная запись и подъём из памяти проекта. Консолидирует полку тот же
// TeamMemoryConsolidationService (по AllScopes() обеих полок). При удалении сферы память удаляется
// вместе с ней (DeleteAllForSphereAsync).
public class SphereMemoryService : Knowledge.IKnowledgeSyncParticipant, IDisposable
{
    private readonly MemoryShelf _shelf;

    public SphereMemoryService(IConfiguration config, ILogger<SphereMemoryService>? log = null,
        IKnowledgeIndex? knowledge = null, IDifyMetrics? metrics = null, IUserStore? users = null,
        Llm.ICheapTextRunner? cheap = null)
    {
        _shelf = new MemoryShelf(new MemoryShelfOptions(
            StoreFileName: "sphere-memory.json",
            KnowledgeFileName: "sphere-memory-knowledge.json",
            DatasetName: (ownerId, sphereId) => DatasetName(users?.GetById(ownerId)?.Username ?? ownerId, sphereId),
            TargetLabel: "sphere",
            RecallHeader: "## Память сферы",
            RecallIntro: "Общие факты и договорённости сферы (помнят все проекты и персоны сферы):",
            RecallIntroTruncated: "Общие факты и договорённости сферы (помнят все проекты и персоны сферы; длинные записи обрезаны — полный текст записи в разделе памяти сферы):"),
            config, log, knowledge, metrics, resolver: null, cheap);
    }

    // Имя Dify-датасета полки сферы. Префикс «sphere:» скрывает базу из каталога знаний пользователя
    // (KnowledgeBaseCatalogService.Classify).
    public static string DatasetName(string username, string sphereId) => $"{username}:sphere:{sphereId}";

    // Настроен ли семантический слой (Dify). Без него — полнотекстовый fallback.
    public bool Available => _shelf.Available;

    public int MaxEntries => _shelf.MaxEntries;

    public IReadOnlyList<TeamMemoryEntry> List(string ownerId, string sphereId) =>
        _shelf.List(ownerId, sphereId);

    // Число записей сферы — для 409 при удалении сферы и счётчика на странице сферы
    public int Count(string ownerId, string sphereId) => _shelf.Count(ownerId, sphereId);

    // Все сферы с непустой памятью — для полной проходки консолидатора.
    public IReadOnlyList<(string OwnerId, string SphereId)> AllScopes() => _shelf.AllScopes();

    // Записать в память сферы. Source по умолчанию Manual; AutoTurn/AutoMeeting сюда не ходят.
    // promotedFrom — для записи, поднятой из памяти проекта.
    public TeamMemoryEntry Add(string ownerId, string sphereId, string text,
        TeamMemoryType type = TeamMemoryType.Fact, double? salience = null,
        MemoryPromotion? promotedFrom = null) =>
        _shelf.Add(ownerId, sphereId, text, type, TeamMemorySource.Manual, null, salience, promotedFrom);

    // Семантический write-path: при настроенном Dify ищет близкий по смыслу дубль того же типа
    public Task<TeamMemoryEntry> AddAsync(string ownerId, string sphereId, string text,
        TeamMemoryType type = TeamMemoryType.Fact, double? salience = null,
        MemoryPromotion? promotedFrom = null) =>
        _shelf.AddAsync(ownerId, sphereId, text, type, TeamMemorySource.Manual, null, salience, promotedFrom);

    // Подъём записи с полки проекта на полку сферы — ПЕРЕМЕЩЕНИЕ: запись уходит с полки проекта,
    // на полке сферы получает PromotedFrom. Единая точка для MCP sphere_memory_adopt и REST;
    // вызывающий обязан сам проверить, что проект входит в сферу и что у него есть право записи.
    // null — записи нет на полке проекта.
    public async Task<TeamMemoryEntry?> AdoptAsync(string ownerId, string sphereId, TeamMemoryService projectShelf,
        string projectId, string entryId)
    {
        var source = projectShelf.List(ownerId, projectId).FirstOrDefault(e => e.Id == entryId);
        if (source is null) return null;
        // Асинхронный путь: запись сразу уходит в Dify и находится семантическим поиском
        var promoted = await AddAsync(ownerId, sphereId, source.Text, source.Type, source.Salience,
            new MemoryPromotion(projectId, source.Id, DateTime.UtcNow));
        projectShelf.Remove(ownerId, projectId, entryId);
        return promoted;
    }

    public TeamMemoryEntry? Update(string ownerId, string sphereId, string entryId, string text) =>
        _shelf.Update(ownerId, sphereId, entryId, text);

    public bool Remove(string ownerId, string sphereId, string entryId) =>
        _shelf.Remove(ownerId, sphereId, entryId);

    // Полнотекстовый recall (fallback без Dify)
    public TeamMemoryService.TeamRecallResult BuildRecallBlock(string ownerId, string sphereId, string query, int topK = 4) =>
        _shelf.BuildRecallBlock(ownerId, sphereId, query, topK);

    // Семантический recall; без Dify / при ошибке — полнотекстовый
    public Task<TeamMemoryService.TeamRecallResult> BuildRecallBlockAsync(
        string ownerId, string sphereId, string query, int topK = 4) =>
        _shelf.BuildRecallBlockAsync(ownerId, sphereId, query, topK);

    public Task<IReadOnlyList<TeamMemoryEntry>> SearchAsync(string ownerId, string sphereId,
        string query, int topK = 8) =>
        _shelf.SearchAsync(ownerId, sphereId, query, topK);

    public int ApplyConsolidation(string ownerId, string sphereId, IReadOnlyList<TeamMemoryConsolidationOp> ops) =>
        _shelf.ApplyConsolidation(ownerId, sphereId, ops);

    public int EnforceCap(string ownerId, string sphereId) => _shelf.EnforceCap(ownerId, sphereId);

    // Удалить ВСЮ память сферы (решение: память живёт и умирает вместе со сферой): записи, состояние
    // семантического слоя и Dify-датасет. Возвращает число удалённых записей.
    public async Task<int> DeleteAllForSphereAsync(string ownerId, string sphereId)
    {
        var count = _shelf.Count(ownerId, sphereId);
        await _shelf.DeleteScopeAsync(ownerId, sphereId);
        return count;
    }

    // Уборка локальных сторов полки при каскадном удалении пользователя
    public void DeleteOwnerSphereMemory(string ownerId) => _shelf.DeleteOwnerMemory(ownerId);

    public Task DeleteAllAsync(string userId) => _shelf.DeleteAllAsync(userId);

    public IReadOnlyList<Knowledge.KnowledgeSyncTarget> ListTargets() => _shelf.ListTargets();

    public Task<int> SyncAsync(string ownerId, string sphereId) => _shelf.SyncAsync(ownerId, sphereId);

    public void Dispose() => _shelf.Dispose();
}
