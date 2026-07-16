using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.Memory;

namespace ClaudeHomeServer.Services;

// Память команды проекта (③-3.4): общее хранилище решений/договорённостей/фактов/терминов проекта,
// из которого ВСЕ персоны команды recall'ят наравне с личной памятью — команда учится вместе, а не
// каждая про себя. Источник правды — JSON-стор data/team-memory.json (ключ «owner:project»). Поверх
// него — семантический слой в Dify-датасете «{username}:team:{projectName}» (векторный retrieve со
// скорингом, дифф по хешам, дебаунс). Без настроенного Dify — graceful degradation к полнотекстовому
// recall по стору (Волна 1). Эталон — PersonaMemoryService/NotesKnowledgeService.
public class TeamMemoryService
{
    // Состояние семантического слоя проекта: id датасета + entryId → { difyDocId, hash }
    private sealed class KnowledgeState
    {
        public string? DatasetId { get; set; }
        public Dictionary<string, MemoryDocRef> Docs { get; set; } = new();
        // Проставляются в GetKnowledgeState для удобства (ключ и так их несёт) — в стор не пишем
        [JsonIgnore] public string OwnerId { get; set; } = "";
        [JsonIgnore] public string ProjectId { get; set; } = "";
    }

    private static readonly TimeSpan SyncDebounce = TimeSpan.FromSeconds(15);
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private readonly ConcurrentDictionary<string, List<TeamMemoryEntry>> _store = new();
    private readonly string _storePath;
    private readonly Lock _saveLock = new();
    private readonly ILogger<TeamMemoryService>? _log;

    // Опциональные зависимости семантического слоя (nullable-паттерн: в юнит-тестах Волны 1 не заданы)
    private readonly KnowledgeService? _knowledge;
    private readonly UserStore? _users;
    private readonly ProjectManager? _projects;
    // LLM-резолвер записи памяти (разрешение противоречий на авто-пути); null в юнит-тестах
    private readonly Memory.MemoryWriteResolver? _resolver;

    // Sibling-стор семантического слоя: data/team-memory-knowledge.json (ключ «owner:project»)
    private readonly string _knowledgeStorePath;
    private readonly Dictionary<string, KnowledgeState> _kStore;
    private readonly Lock _kLock = new();
    private readonly SemaphoreSlim _syncLock = new(1, 1);
    private readonly MemoryDifyDebouncer _debounce = new(SyncDebounce);

    // Параметры скоринга (взвешенная сумма) и потолок памяти
    private readonly MemoryScoringOptions _scoring;
    // Параметры гибридного retrieval (слияние semantic+keyword) — Memory:Fusion:*
    private readonly MemoryFusionOptions _fusion;
    private readonly int _maxEntries;
    private readonly double _dedupThreshold;
    // Порог зоны конфликта: кандидаты в [ConflictThreshold, DedupThreshold) идут в LLM-резолвер (Memory #2)
    private readonly double _conflictThreshold;

    // Прибавка важности при повторе факта: дедуп-on-write усиливает существующую запись, а не плодит дубль
    private const double DedupBoost = 0.1;

    public TeamMemoryService(IConfiguration config, ILogger<TeamMemoryService>? log = null,
        KnowledgeService? knowledge = null, UserStore? users = null, ProjectManager? projects = null,
        Memory.MemoryWriteResolver? resolver = null)
    {
        _log = log;
        _knowledge = knowledge;
        _users = users;
        _projects = projects;
        _resolver = resolver;
        var dataDir = Path.GetDirectoryName(
            config["DataPath"] ?? Path.Combine(AppContext.BaseDirectory, "data", "projects.json"))!;
        _storePath = Path.Combine(dataDir, "team-memory.json");
        _knowledgeStorePath = Path.Combine(dataDir, "team-memory-knowledge.json");

        // Параметры скоринга — TeamMemory:Score:*; дефолты как у персоны (MemoryScoringOptions.Default)
        var d = MemoryScoringOptions.Default;
        _scoring = new MemoryScoringOptions(
            ReadDouble(config, "TeamMemory:Score:RelevanceWeight", d.RelevanceWeight),
            ReadDouble(config, "TeamMemory:Score:RecencyWeight", d.RecencyWeight),
            ReadDouble(config, "TeamMemory:Score:SalienceWeight", d.SalienceWeight),
            ReadDouble(config, "TeamMemory:Score:TypeWeight", d.TypeWeight),
            ReadDouble(config, "TeamMemory:Score:RecencyHalfLifeDays", d.RecencyHalfLifeDays),
            ReadDouble(config, "TeamMemory:Score:MinRelevance", d.MinRelevance));
        _fusion = ReadFusion(config);
        _maxEntries = int.TryParse(config["TeamMemory:MaxEntries"], out var me) && me > 0 ? me : 200;
        _dedupThreshold = ReadDouble(config, "TeamMemory:DedupThreshold", 0.85);
        _conflictThreshold = ReadDouble(config, "Memory:ConflictThreshold", 0.6);

        _kStore = JsonFileStore.Load<Dictionary<string, KnowledgeState>>(_knowledgeStorePath, JsonOpts) ?? new();
        Load();
    }

    private static double ReadDouble(IConfiguration config, string key, double fallback) =>
        double.TryParse(config[key], System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : fallback;

    // Параметры гибридного retrieval из конфига Memory:Fusion:* (общие для персоны и команды)
    private static MemoryFusionOptions ReadFusion(IConfiguration config)
    {
        var d = MemoryFusionOptions.Default;
        return new MemoryFusionOptions(
            SemanticWeight: ReadDouble(config, "Memory:Fusion:SemanticWeight", d.SemanticWeight),
            KeywordWeight: ReadDouble(config, "Memory:Fusion:KeywordWeight", d.KeywordWeight),
            Method: string.Equals(config["Memory:Fusion:Method"], "rrf", StringComparison.OrdinalIgnoreCase)
                ? MemoryFusionMethod.Rrf : MemoryFusionMethod.WeightedSum,
            RrfK: ReadDouble(config, "Memory:Fusion:RrfK", d.RrfK));
    }

    // Настроен ли семантический слой (Dify). Без него — полнотекстовый fallback.
    public bool Available => _knowledge?.IsConfigured == true;

    // Настроенный потолок памяти команды (TeamMemory:MaxEntries)
    public int MaxEntries => _maxEntries;

    public IReadOnlyList<TeamMemoryEntry> List(string ownerId, string projectId) =>
        Snapshot(ownerId, projectId);

    // Все проектные scope'ы с непустой памятью — для полной проходки консолидатора.
    public IReadOnlyList<(string OwnerId, string ProjectId)> AllScopes()
    {
        var result = new List<(string, string)>();
        foreach (var key in _store.Keys.ToList())
        {
            var idx = key.IndexOf(':');
            if (idx <= 0 || idx >= key.Length - 1) continue;
            if (_store.TryGetValue(key, out var list) && list.Count > 0)
                result.Add((key[..idx], key[(idx + 1)..]));
        }
        return result;
    }

    // Добавить запись командной памяти. Дедуп-on-write (внутри _saveLock): одинаковый текст того же
    // типа не плодим — усиливаем существующую (важность + более полный текст), чтобы авто-захват
    // не засорял общий стор. Старый вызов Add(owner, project, text) остаётся валидным (дефолты).
    public TeamMemoryEntry Add(string ownerId, string projectId, string text,
        TeamMemoryType type = TeamMemoryType.Fact,
        TeamMemorySource source = TeamMemorySource.Manual,
        string? sourceSessionId = null, double? salience = null)
    {
        var trimmed = text.Trim();
        TeamMemoryEntry result;
        lock (_saveLock)
        {
            var list = Get(ownerId, projectId);
            var dup = list.FirstOrDefault(e => e.Type == type
                && string.Equals(e.Text, trimmed, StringComparison.OrdinalIgnoreCase));
            if (dup is not null)
            {
                var baseSal = salience is null
                    ? dup.Salience
                    : Math.Max(dup.Salience, Math.Clamp(salience.Value, 0.05, 1.0));
                dup.Salience = Math.Clamp(baseSal + DedupBoost, 0.05, 1.0);
                if (trimmed.Length > dup.Text.Length) dup.Text = trimmed;   // более информативная формулировка
                Save();
                result = dup;
            }
            else
            {
                var entry = new TeamMemoryEntry
                {
                    OwnerId = ownerId,
                    ProjectId = projectId,
                    Text = trimmed,
                    Type = type,
                    Source = source,
                    SourceSessionId = sourceSessionId,
                    Salience = salience is null ? 1.0 : Math.Clamp(salience.Value, 0.05, 1.0),
                };
                list.Add(entry);
                Save();
                result = entry;
            }
        }
        QueueSync(ownerId, projectId);
        return result;
    }

    // Семантический write-path: при настроенном Dify перед добавлением ищет близкий по смыслу дубль
    // ТОГО ЖЕ типа (retrieve, порог DedupThreshold) → усиливает существующую запись, а не плодит новую;
    // иначе делегирует в точный Add (там ещё и текстовый дедуп). Предпочтителен для авто-памяти.
    public async Task<TeamMemoryEntry> AddAsync(string ownerId, string projectId, string text,
        TeamMemoryType type = TeamMemoryType.Fact,
        TeamMemorySource source = TeamMemorySource.Manual,
        string? sourceSessionId = null, double? salience = null)
    {
        var trimmed = text.Trim();
        var state = GetKnowledgeState(ownerId, projectId);
        if (Available && !string.IsNullOrEmpty(state.DatasetId))
        {
            try
            {
                var dup = await FindSemanticDuplicateAsync(state, type, trimmed);
                if (dup is not null)
                {
                    Reinforce(ownerId, projectId, dup.Id, trimmed, salience);
                    QueueSync(ownerId, projectId);   // текст мог смениться на более полный
                    return dup;
                }
            }
            catch (Exception ex) { _log?.LogDebug(ex, "team-memory: семантический дедуп {Project}", projectId); }
        }
        return Add(ownerId, projectId, text, type, source, sourceSessionId, salience);
    }

    // Найти запись того же типа, семантически близкую к тексту (Dify retrieve, порог DedupThreshold)
    private async Task<TeamMemoryEntry?> FindSemanticDuplicateAsync(
        KnowledgeState state, TeamMemoryType type, string text)
    {
        var chunks = await _knowledge!.RetrieveAsync(state.DatasetId!, text, 5);
        if (chunks.Count == 0) return null;

        Dictionary<string, string> byDocId;
        lock (_kLock) byDocId = state.Docs.ToDictionary(kv => kv.Value.DocId, kv => kv.Key);

        List<TeamMemoryEntry> entries;
        lock (_saveLock) entries = Get(state.OwnerId, state.ProjectId).ToList();
        var byId = entries.ToDictionary(e => e.Id);

        foreach (var ch in chunks.OrderByDescending(c => c.Score))
        {
            if (ch.Score < _dedupThreshold) break;   // дальше только менее близкие
            if (byDocId.TryGetValue(ch.DocumentId, out var entryId)
                && byId.TryGetValue(entryId, out var e) && e.Type == type)
                return e;
        }
        return null;
    }

    // Авто-путь с разрешением ПРОТИВОРЕЧИЙ (Memory #2, Mem0 ADD/UPDATE/DELETE/NOOP). Дубль
    // (≥DedupThreshold) — как AddAsync (reinforcement). Иначе близкие кандидаты зоны конфликта
    // [ConflictThreshold, DedupThreshold) отдаются LLM-резолверу: UPDATE дополняет существующий,
    // DELETE вытесняет устаревший + добавляет новый, ADD кладёт рядом, NOOP отбрасывает (→ null).
    // Гейтится Enabled+Available; без резолвера/датасета/кандидатов — обычный AddAsync. Ошибки → ADD.
    public async Task<TeamMemoryEntry?> AddWithResolutionAsync(string ownerId, string projectId, string text,
        TeamMemoryType type = TeamMemoryType.Fact, TeamMemorySource source = TeamMemorySource.AutoTurn,
        string? sourceSessionId = null, double? salience = null)
    {
        // Резолвер выключен / Dify недоступен → обычный семантический write-path (простой дедуп)
        if (_resolver is not { Enabled: true } || !Available)
            return await AddAsync(ownerId, projectId, text, type, source, sourceSessionId, salience);

        var trimmed = text.Trim();
        var state = GetKnowledgeState(ownerId, projectId);
        if (string.IsNullOrEmpty(state.DatasetId))   // датасета ещё нет — сопоставлять не с чем
            return await AddAsync(ownerId, projectId, text, type, source, sourceSessionId, salience);

        try
        {
            var (dup, candidates) = await FindDuplicateAndCandidatesAsync(state, type, trimmed);
            if (dup is not null)   // явный дубль — усиливаем, резолвер не нужен
            {
                Reinforce(ownerId, projectId, dup.Id, trimmed, salience);
                QueueSync(ownerId, projectId);
                return dup;
            }
            if (candidates.Count == 0)   // нет соседей в зоне конфликта — обычное добавление
                return Add(ownerId, projectId, text, type, source, sourceSessionId, salience);

            var decision = await _resolver.ResolveAsync(trimmed, TypeLabel(type), candidates);
            switch (decision.Op)
            {
                case Memory.MemoryWriteOp.Noop:
                    return null;   // дубль/незначимо — ничего не добавляем
                case Memory.MemoryWriteOp.Update when !string.IsNullOrEmpty(decision.TargetId):
                    // Новый уточняет существующий → заменяем текст target на объединённую формулировку
                    return Update(ownerId, projectId, decision.TargetId, decision.MergedText!)
                        ?? Add(ownerId, projectId, text, type, source, sourceSessionId, salience);
                case Memory.MemoryWriteOp.Delete when !string.IsNullOrEmpty(decision.TargetId):
                    // Новый делает существующий устаревшим → удаляем target, добавляем новый
                    Remove(ownerId, projectId, decision.TargetId);
                    return Add(ownerId, projectId, text, type, source, sourceSessionId, salience);
                default:   // Add и невалидные Update/Delete
                    return Add(ownerId, projectId, text, type, source, sourceSessionId, salience);
            }
        }
        catch (Exception ex)
        {
            _log?.LogDebug(ex, "team-memory: разрешение записи {Project}", projectId);
            return Add(ownerId, projectId, text, type, source, sourceSessionId, salience);
        }
    }

    // Найти дубль (≥DedupThreshold) и близких кандидатов зоны конфликта [ConflictThreshold, DedupThreshold)
    // ТОГО ЖЕ типа (Dify retrieve). Дубль (наивысший скор) возвращается сразу с пустым списком кандидатов.
    private async Task<(TeamMemoryEntry? Dup, List<Memory.MemoryWriteCandidate> Candidates)>
        FindDuplicateAndCandidatesAsync(KnowledgeState state, TeamMemoryType type, string text)
    {
        var candidates = new List<Memory.MemoryWriteCandidate>();
        var chunks = await _knowledge!.RetrieveAsync(state.DatasetId!, text, 8);
        if (chunks.Count == 0) return (null, candidates);

        Dictionary<string, string> byDocId;
        lock (_kLock) byDocId = state.Docs.ToDictionary(kv => kv.Value.DocId, kv => kv.Key);

        List<TeamMemoryEntry> entries;
        lock (_saveLock) entries = Get(state.OwnerId, state.ProjectId).ToList();
        var byId = entries.ToDictionary(e => e.Id);

        foreach (var ch in chunks.OrderByDescending(c => c.Score))
        {
            if (ch.Score < _conflictThreshold) break;   // дальше только менее близкие — не интересны
            if (!byDocId.TryGetValue(ch.DocumentId, out var entryId)) continue;
            if (!byId.TryGetValue(entryId, out var e) || e.Type != type) continue;
            if (ch.Score >= _dedupThreshold) return (e, candidates);   // явный дубль
            candidates.Add(new Memory.MemoryWriteCandidate(e.Id, e.Text));
        }
        return (null, candidates);
    }

    // Reinforcement при повторе факта: усилить важность и взять более полный текст (если новый длиннее).
    // Двигает запись вверх в скоринге вместо дубля (у командной памяти нет LastAccessedAt — recency
    // считается от создания, поэтому обновляем только salience/текст).
    private void Reinforce(string ownerId, string projectId, string entryId, string newText, double? salience)
    {
        lock (_saveLock)
        {
            var e = Get(ownerId, projectId).FirstOrDefault(x => x.Id == entryId);
            if (e is null) return;
            var baseSal = salience is null ? e.Salience : Math.Max(e.Salience, Math.Clamp(salience.Value, 0.05, 1.0));
            e.Salience = Math.Clamp(baseSal + DedupBoost, 0.05, 1.0);
            if (newText.Length > e.Text.Length) e.Text = newText;
            Save();
        }
    }

    // Отредактировать текст записи вручную (UI-редактирование)
    public TeamMemoryEntry? Update(string ownerId, string projectId, string entryId, string text)
    {
        TeamMemoryEntry? entry;
        lock (_saveLock)
        {
            entry = Get(ownerId, projectId).FirstOrDefault(e => e.Id == entryId);
            if (entry is null) return null;
            entry.Text = text.Trim();
            Save();
        }
        QueueSync(ownerId, projectId);
        return entry;
    }

    public bool Remove(string ownerId, string projectId, string entryId)
    {
        bool ok;
        lock (_saveLock)
        {
            var list = Get(ownerId, projectId);
            ok = list.RemoveAll(e => e.Id == entryId) > 0;
            if (ok) Save();
        }
        if (ok) QueueSync(ownerId, projectId);
        return ok;
    }

    // Результат recall'а: markdown-блок для промпта + записи, реально попавшие в блок
    // (для манифеста атрибуции F3 — «персона опирается на…», см. SessionManager). Text=null — пусто.
    public sealed record TeamRecallResult(string? Text, IReadOnlyList<TeamMemoryEntry> Used);

    // Полнотекстовый recall (fallback без Dify): записи, разделяющие слова запроса, топ по перекрытию.
    // Сохранён как graceful degradation — используется, когда семантический слой недоступен.
    public TeamRecallResult BuildRecallBlock(string ownerId, string projectId, string query, int topK = 4)
    {
        var snapshot = Snapshot(ownerId, projectId);
        if (snapshot.Count == 0) return new TeamRecallResult(null, []);

        var ranked = MemoryFulltext.Rank(snapshot, query, topK, e => e.Text);
        if (ranked.Count == 0) return new TeamRecallResult(null, []);
        return new TeamRecallResult(FormatRecall(ranked), ranked);
    }

    // Семантический recall (③-3.4): при настроенном Dify ранжирует через retrieve + TeamMemoryScorer;
    // без Dify / при ошибке — graceful degradation к полнотекстовому BuildRecallBlock.
    public async Task<TeamRecallResult> BuildRecallBlockAsync(string ownerId, string projectId, string query, int topK = 4)
    {
        var snapshot = Snapshot(ownerId, projectId);
        if (snapshot.Count == 0) return new TeamRecallResult(null, []);

        var state = GetKnowledgeState(ownerId, projectId);
        if (Available && !string.IsNullOrEmpty(state.DatasetId))
        {
            try
            {
                var ranked = await RankViaDifyAsync(state, snapshot, query, topK);
                if (ranked.Count == 0) return new TeamRecallResult(null, []);
                return new TeamRecallResult(FormatRecall(ranked), ranked);
            }
            catch (Exception ex) { _log?.LogDebug(ex, "team-memory: семантический recall {Project}", projectId); }
        }
        return BuildRecallBlock(ownerId, projectId, query, topK);
    }

    // Поиск по памяти команды (для endpoint/MCP team_memory_search): при Dify — семантический
    // скоринг, иначе — полнотекст. Возвращает записи (не markdown), ранжированные по релевантности.
    public async Task<IReadOnlyList<TeamMemoryEntry>> SearchAsync(string ownerId, string projectId,
        string query, int topK = 8)
    {
        var snapshot = Snapshot(ownerId, projectId);
        if (snapshot.Count == 0) return [];

        var state = GetKnowledgeState(ownerId, projectId);
        if (Available && !string.IsNullOrEmpty(state.DatasetId))
        {
            try { return await RankViaDifyAsync(state, snapshot, query, topK); }
            catch (Exception ex) { _log?.LogDebug(ex, "team-memory: семантический поиск {Project}", projectId); }
        }
        return MemoryFulltext.Rank(snapshot, query, topK, e => e.Text);
    }

    // Ранжирование через Dify: гибридная релевантность (semantic Dify + keyword полнотекст) →
    // взвешенная сумма TeamMemoryScorer → topK
    private async Task<List<TeamMemoryEntry>> RankViaDifyAsync(
        KnowledgeState state, IReadOnlyList<TeamMemoryEntry> snapshot, string query, int topK)
    {
        Dictionary<string, string> byDocId;
        lock (_kLock) byDocId = state.Docs.ToDictionary(kv => kv.Value.DocId, kv => kv.Key);

        // Гибридный retrieval: semantic (Dify) + keyword (полнотекст) сливаются в единый relevance.
        // Keyword добирает точные термины/идентификаторы, которые вектор пропускает (③-#3).
        var chunks = await _knowledge!.RetrieveAsync(state.DatasetId!, query, Math.Max(topK, 12));
        var semantic = new Dictionary<string, double>();
        foreach (var ch in chunks)
            if (byDocId.TryGetValue(ch.DocumentId, out var entryId))
                semantic[entryId] = Math.Max(semantic.GetValueOrDefault(entryId), ch.Score);
        // Keyword-сигнал считаем по всему снапшоту (не только по кандидатам Dify): дёшево и позволяет
        // всплыть точному совпадению, не попавшему в топ-чанки Dify.
        var keyword = MemoryFulltext.Relevance(snapshot, query, e => e.Id, e => e.Text, e => e.Tags);
        var relevance = MemoryRetrievalFusion.Fuse(semantic, keyword, _fusion);

        var now = DateTime.UtcNow;
        return snapshot
            .Select(e => (e, score: TeamMemoryScorer.Score(e, relevance.GetValueOrDefault(e.Id, 0.0), now, _scoring)))
            .Where(x => x.score > 0)
            .OrderByDescending(x => x.score)
            .Take(topK)
            .Select(x => x.e)
            .ToList();
    }

    private static string FormatRecall(IReadOnlyList<TeamMemoryEntry> ranked)
    {
        var sb = new StringBuilder();
        sb.AppendLine("## Память команды проекта");
        sb.AppendLine("Общие факты и договорённости проекта (помнят все персоны команды):");
        foreach (var e in ranked)
            sb.AppendLine($"- {e.Text}");
        return sb.ToString();
    }

    // --- Консолидация (P4): применение операций merge/drop под save-lock ---

    // Применить операции консолидации. Валидация (гейты) — на стороне вызывающего
    // (TeamMemoryConsolidationService.FilterOps); здесь только атомарное применение:
    // merge = удалить источники + добавить сводную запись, drop = удалить.
    // Возвращает число затронутых записей; Dify-дифф при синке сам подчистит документы.
    public int ApplyConsolidation(string ownerId, string projectId, IReadOnlyList<TeamMemoryConsolidationOp> ops)
    {
        if (ops.Count == 0) return 0;
        int affected = 0;
        lock (_saveLock)
        {
            var list = Get(ownerId, projectId);
            foreach (var op in ops)
            {
                if (op.IsMerge)
                {
                    var sources = list.Where(e => op.Ids!.Contains(e.Id)).ToList();
                    if (sources.Count < 2 || string.IsNullOrWhiteSpace(op.Text)) continue;
                    list.RemoveAll(e => op.Ids!.Contains(e.Id));
                    list.Add(new TeamMemoryEntry
                    {
                        OwnerId = ownerId,
                        ProjectId = projectId,
                        Type = op.Type ?? sources[0].Type,
                        Text = op.Text!.Trim(),
                        Source = TeamMemorySource.AutoTurn,
                        Salience = Math.Clamp(op.Salience ?? sources.Max(s => s.Salience), 0.05, 1.0),
                    });
                    affected += sources.Count;
                }
                else if (op.IsDrop)
                {
                    affected += list.RemoveAll(e => e.Id == op.Id);
                }
            }
            if (affected > 0) Save();
        }
        if (affected > 0) QueueSync(ownerId, projectId);
        return affected;
    }

    // Привести число записей к потолку: вытеснить хвост сверх MaxEntries по retention-скорингу.
    // Механическое, детерминированное — работает при одном лишь autolearn, без LLM-merge.
    public int EnforceCap(string ownerId, string projectId)
    {
        var snapshot = Snapshot(ownerId, projectId);
        var evictIds = TeamMemoryScorer.SelectEvictionIds(snapshot, _maxEntries, _scoring, DateTime.UtcNow);
        if (evictIds.Count == 0) return 0;
        var ops = evictIds.Select(id => new TeamMemoryConsolidationOp("drop", null, id, null, null, null)).ToList();
        return ApplyConsolidation(ownerId, projectId, ops);
    }

    // Полное удаление памяти команды проекта — при удалении проекта: Dify-датасет + оба стора
    // (data/team-memory.json и sibling-стор). Локальное состояние снимаем сразу, сбой Dify логируем.
    public async Task DeleteProjectTeamMemoryAsync(string ownerId, string projectId)
    {
        var key = Key(ownerId, projectId);
        string? datasetId;
        lock (_kLock)
        {
            datasetId = _kStore.GetValueOrDefault(key)?.DatasetId;
            _kStore.Remove(key);
            SaveKnowledge();
        }
        lock (_saveLock)
        {
            _store.TryRemove(key, out _);
            Save();
        }
        if (!string.IsNullOrEmpty(datasetId) && _knowledge?.IsConfigured == true)
        {
            try { await _knowledge.DeleteDatasetAsync(datasetId); }
            catch (Exception ex) { _log?.LogWarning(ex, "team-memory: не удалить Dify-датасет проекта {Project}", projectId); }
        }
    }

    // Best-effort переименование Dify-датасета памяти команды при переименовании проекта
    // (имя «{username}:team:{projectName}» иначе стухает; работа по id не ломается)
    public async Task RenameProjectDatasetAsync(string ownerId, string projectId, string username, string newProjectName)
    {
        string? datasetId;
        lock (_kLock) datasetId = _kStore.GetValueOrDefault(Key(ownerId, projectId))?.DatasetId;
        if (string.IsNullOrEmpty(datasetId) || _knowledge?.IsConfigured != true) return;
        await _knowledge.RenameDatasetAsync(datasetId, $"{username}:team:{newProjectName}");
    }

    // Уборка локальных сторов памяти команды всех проектов владельца — каскад удаления
    // пользователя. Dify-датасеты удаляет вызывающий общим проходом по префиксу имени.
    public void DeleteOwnerTeamMemory(string ownerId)
    {
        var prefix = ownerId + ":";
        lock (_kLock)
        {
            var keys = _kStore.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).ToList();
            foreach (var k in keys) _kStore.Remove(k);
            if (keys.Count > 0) SaveKnowledge();
        }
        lock (_saveLock)
        {
            var keys = _store.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).ToList();
            foreach (var k in keys) _store.TryRemove(k, out _);
            if (keys.Count > 0) Save();
        }
    }

    // --- Синхронизация с Dify (дифф по хешам, дебаунс) ---

    private void QueueSync(string ownerId, string projectId)
    {
        if (!Available) return;
        _debounce.Schedule(Key(ownerId, projectId), () => RunSyncSafe(ownerId, projectId));
    }

    private void RunSyncSafe(string ownerId, string projectId) =>
        _ = Task.Run(async () =>
        {
            try { await SyncAsync(ownerId, projectId); }
            catch (Exception ex) { _log?.LogWarning(ex, "team-memory: синхронизация {Project} в Dify", projectId); }
        });

    public async Task<int> SyncAsync(string ownerId, string projectId)
    {
        if (!Available) return 0;

        await _syncLock.WaitAsync();
        try
        {
            var state = GetKnowledgeState(ownerId, projectId);
            if (string.IsNullOrEmpty(state.DatasetId))
            {
                var username = _users?.GetById(ownerId)?.Username ?? ownerId;
                var projectName = _projects?.GetById(projectId)?.Name ?? projectId;
                var datasetId = await _knowledge!.CreateDatasetAsync($"{username}:team:{projectName}");
                lock (_kLock) { state.DatasetId = datasetId; SaveKnowledge(); }
            }

            // Снапшоты под локами — конкурентные мутации не должны видеть полу-состояние
            var entries = Snapshot(ownerId, projectId);
            Dictionary<string, MemoryDocRef> docsSnapshot;
            lock (_kLock) docsSnapshot = new Dictionary<string, MemoryDocRef>(state.Docs);

            // Дифф-синк — общее ядро MemoryDify; связка со стором (мутации Docs под _kLock) тонкая
            var items = entries
                .Select(e => new MemorySyncItem(e.Id,
                    $"{e.Type}\n{e.Text}\n{string.Join(',', e.Tags ?? [])}",
                    $"{TypeLabel(e.Type)}-{e.Id}", e.Text, e.Tags))
                .ToList();

            var changed = await MemoryDify.DiffSyncAsync(_knowledge!, state.DatasetId!, items, docsSnapshot,
                (id, doc) => { lock (_kLock) state.Docs[id] = doc; },
                id => { lock (_kLock) state.Docs.Remove(id); },
                _log);

            if (changed > 0) lock (_kLock) SaveKnowledge();
            return changed;
        }
        finally { _syncLock.Release(); }
    }

    private static string TypeLabel(TeamMemoryType t) => t switch
    {
        TeamMemoryType.Decision => "решение",
        TeamMemoryType.Convention => "договорённость",
        TeamMemoryType.Fact => "факт",
        TeamMemoryType.Glossary => "термин",
        _ => "факт",
    };

    // --- Внутреннее хранилище ---

    private List<TeamMemoryEntry> Get(string ownerId, string projectId) =>
        _store.GetOrAdd(Key(ownerId, projectId), _ => new List<TeamMemoryEntry>());

    private List<TeamMemoryEntry> Snapshot(string ownerId, string projectId)
    {
        lock (_saveLock) return Get(ownerId, projectId).ToList();
    }

    private KnowledgeState GetKnowledgeState(string ownerId, string projectId)
    {
        var key = Key(ownerId, projectId);
        lock (_kLock)
        {
            if (!_kStore.TryGetValue(key, out var s)) _kStore[key] = s = new KnowledgeState();
            s.OwnerId = ownerId;
            s.ProjectId = projectId;
            return s;
        }
    }

    private static string Key(string ownerId, string projectId) => $"{ownerId}:{projectId}";

    private void Load()
    {
        try
        {
            var dict = JsonFileStore.Load<Dictionary<string, List<TeamMemoryEntry>>>(_storePath, JsonOpts);
            if (dict is null) return;
            foreach (var kv in dict) _store[kv.Key] = kv.Value;
        }
        catch (Exception ex) { _log?.LogWarning(ex, "team-memory: не загрузился стор"); }
    }

    private void Save()
    {
        lock (_saveLock)
            JsonFileStore.Save(_storePath, _store.ToDictionary(kv => kv.Key, kv => kv.Value), JsonOpts);
    }

    // Вызывается под _kLock
    private void SaveKnowledge() => JsonFileStore.Save(_knowledgeStorePath, _kStore, JsonOpts);
}

// Операция консолидации памяти команды (P4): merge — схлопнуть несколько записей одного типа
// в одну сводную (Ids → новая запись Text/Type/Salience); drop — удалить запись Id.
public sealed record TeamMemoryConsolidationOp(
    string Op, List<string>? Ids, string? Id, TeamMemoryType? Type, string? Text, double? Salience)
    : IMemoryConsolidationOp<TeamMemoryType>
{
    public bool IsMerge => string.Equals(Op, "merge", StringComparison.OrdinalIgnoreCase);
    public bool IsDrop => string.Equals(Op, "drop", StringComparison.OrdinalIgnoreCase);
}
