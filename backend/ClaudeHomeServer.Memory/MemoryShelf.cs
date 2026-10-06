using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ClaudeHomeServer.Core.Telemetry;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.Knowledge;
using TeamRecallResult = ClaudeHomeServer.Services.Memory.TeamMemoryService.TeamRecallResult;

namespace ClaudeHomeServer.Services.Memory;

// Что отличает полку: файлы стора, имя Dify-датасета scope'а, подпись цели реконсайлера и тексты
// recall-блока. Имя датасета считает владелец полки (ему известны пользователи/проекты/сферы).
internal sealed record MemoryShelfOptions(
    string StoreFileName,
    string KnowledgeFileName,
    Func<string, string, string> DatasetName,
    string TargetLabel,
    string RecallHeader,
    string RecallIntro,
    string RecallIntroTruncated);

// Полка памяти — общее ядро хранилищ «память команды проекта» и «память сферы»: JSON-стор
// (ключ «owner:scope»), семантический слой в Dify-датасете (дифф по хешам, дебаунс), скоринг,
// гибридный recall, дедуп-on-write, резолвер противоречий, консолидация и потолок. Полки различаются
// только путями файлов, именем датасета и текстами recall-блока (MemoryShelfOptions). Без настроенного
// Dify — graceful degradation к полнотекстовому recall по стору. Эталон — PersonaMemoryService.
internal sealed class MemoryShelf : Knowledge.IKnowledgeSyncParticipant, IDisposable
{
    // Состояние семантического слоя scope'а: id датасета + entryId → { difyDocId, hash }
    private sealed class KnowledgeState
    {
        public string? DatasetId { get; set; }
        public Dictionary<string, MemoryDocRef> Docs { get; set; } = new();
        // Проставляются в GetKnowledgeState для удобства (ключ и так их несёт) — в стор не пишем
        [JsonIgnore] public string OwnerId { get; set; } = "";
        [JsonIgnore] public string ScopeId { get; set; } = "";
    }

    private static readonly TimeSpan SyncDebounce = TimeSpan.FromSeconds(15);
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private readonly MemoryShelfOptions _opts;
    private readonly ConcurrentDictionary<string, List<TeamMemoryEntry>> _store = new();
    private readonly string _storePath;
    private readonly Lock _saveLock = new();
    private readonly ILogger? _log;

    // Опциональные зависимости семантического слоя (nullable-паттерн: в юнит-тестах Волны 1 не заданы)
    private readonly IKnowledgeIndex? _knowledge;
    private readonly IDifyMetrics _metrics;
    // LLM-резолвер записи памяти (разрешение противоречий на авто-пути); null в юнит-тестах
    private readonly Memory.MemoryWriteResolver? _resolver;
    // Сжатие авто-записи (autolearn/резолвер), длиннее AutoCompressThreshold; null в юнит-тестах —
    // тогда сразу жёсткая обрезка (см. CompressIfLongAsync)
    private readonly Llm.ICheapTextRunner? _cheap;

    // Sibling-стор семантического слоя (ключ «owner:scope»)
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

    public MemoryShelf(MemoryShelfOptions opts, IConfiguration config, ILogger? log = null,
        IKnowledgeIndex? knowledge = null, IDifyMetrics? metrics = null,
        Memory.MemoryWriteResolver? resolver = null, Llm.ICheapTextRunner? cheap = null)
    {
        _opts = opts;
        _log = log;
        _knowledge = knowledge;
        // Метрика Dify-синка — обязательная зависимость: без неё прогресс синка
        // теряется молча (коммит ревью 2026-08). В юнит-тестах без Dify DI даёт null —
        // подменяем no-op реализацией, чтобы сигнатура DiffSyncAsync не раздваивалась.
        _metrics = metrics ?? new Core.Telemetry.EmptyDifyMetrics();
        _resolver = resolver;
        _cheap = cheap;
        var dataDir = Path.GetDirectoryName(
            config["DataPath"] ?? Path.Combine(AppContext.BaseDirectory, "data", "projects.json"))!;
        _storePath = Path.Combine(dataDir, opts.StoreFileName);
        _knowledgeStorePath = Path.Combine(dataDir, opts.KnowledgeFileName);

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

    // Настроенный потолок памяти полки (TeamMemory:MaxEntries)
    public int MaxEntries => _maxEntries;

    // Число записей scope'а (без копирования списка)
    public int Count(string ownerId, string scopeId)
    {
        lock (_saveLock) return Get(ownerId, scopeId).Count;
    }

    public IReadOnlyList<TeamMemoryEntry> List(string ownerId, string scopeId) =>
        Snapshot(ownerId, scopeId);

    // Все scope'ы с непустой памятью — для полной проходки консолидатора.
    public IReadOnlyList<(string OwnerId, string ScopeId)> AllScopes()
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
    public TeamMemoryEntry Add(string ownerId, string scopeId, string text,
        TeamMemoryType type = TeamMemoryType.Fact,
        TeamMemorySource source = TeamMemorySource.Manual,
        string? sourceSessionId = null, double? salience = null, MemoryPromotion? promotedFrom = null)
    {
        var trimmed = text.Trim();
        TeamMemoryEntry result;
        lock (_saveLock)
        {
            var list = Get(ownerId, scopeId);
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
                    ProjectId = scopeId,
                    Text = trimmed,
                    Type = type,
                    Source = source,
                    SourceSessionId = sourceSessionId,
                    PromotedFrom = promotedFrom,
                    Salience = salience is null ? 1.0 : Math.Clamp(salience.Value, 0.05, 1.0),
                };
                list.Add(entry);
                Save();
                result = entry;
            }
        }
        QueueSync(ownerId, scopeId);
        return result;
    }

    // Семантический write-path: при настроенном Dify перед добавлением ищет близкий по смыслу дубль
    // ТОГО ЖЕ типа (retrieve, порог DedupThreshold) → усиливает существующую запись, а не плодит новую;
    // иначе делегирует в точный Add (там ещё и текстовый дедуп). Предпочтителен для авто-памяти.
    public async Task<TeamMemoryEntry> AddAsync(string ownerId, string scopeId, string text,
        TeamMemoryType type = TeamMemoryType.Fact,
        TeamMemorySource source = TeamMemorySource.Manual,
        string? sourceSessionId = null, double? salience = null, MemoryPromotion? promotedFrom = null)
    {
        var trimmed = await CompressIfLongAsync(source, text.Trim());
        var state = GetKnowledgeState(ownerId, scopeId);
        if (Available && !string.IsNullOrEmpty(state.DatasetId))
        {
            try
            {
                var dup = await FindSemanticDuplicateAsync(state, type, trimmed);
                if (dup is not null)
                {
                    Reinforce(ownerId, scopeId, dup.Id, trimmed, salience);
                    QueueSync(ownerId, scopeId);   // текст мог смениться на более полный
                    return dup;
                }
            }
            catch (Exception ex) { _log?.LogDebug(ex, "memory-shelf: семантический дедуп {Scope}", scopeId); }
        }
        return Add(ownerId, scopeId, trimmed, type, source, sourceSessionId, salience, promotedFrom);
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
        lock (_saveLock) entries = Get(state.OwnerId, state.ScopeId).ToList();
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
    public async Task<TeamMemoryEntry?> AddWithResolutionAsync(string ownerId, string scopeId, string text,
        TeamMemoryType type = TeamMemoryType.Fact, TeamMemorySource source = TeamMemorySource.AutoTurn,
        string? sourceSessionId = null, double? salience = null)
    {
        // Резолвер выключен / Dify недоступен → обычный семантический write-path (простой дедуп)
        if (_resolver is not { Enabled: true } || !Available)
            return await AddAsync(ownerId, scopeId, text, type, source, sourceSessionId, salience);

        var trimmed = await CompressIfLongAsync(source, text.Trim());
        var state = GetKnowledgeState(ownerId, scopeId);
        if (string.IsNullOrEmpty(state.DatasetId))   // датасета ещё нет — сопоставлять не с чем
            return await AddAsync(ownerId, scopeId, trimmed, type, source, sourceSessionId, salience);

        try
        {
            var (dup, candidates) = await FindDuplicateAndCandidatesAsync(state, type, trimmed);
            if (dup is not null)   // явный дубль — усиливаем, резолвер не нужен
            {
                Reinforce(ownerId, scopeId, dup.Id, trimmed, salience);
                QueueSync(ownerId, scopeId);
                return dup;
            }
            if (candidates.Count == 0)   // нет соседей в зоне конфликта — обычное добавление
                return Add(ownerId, scopeId, trimmed, type, source, sourceSessionId, salience);

            var decision = await _resolver.ResolveAsync(ownerId, trimmed, TypeLabel(type), candidates);
            switch (decision.Op)
            {
                case Memory.MemoryWriteOp.Noop:
                    return null;   // дубль/незначимо — ничего не добавляем
                case Memory.MemoryWriteOp.Update when !string.IsNullOrEmpty(decision.TargetId):
                    // Новый уточняет существующий → заменяем текст target на объединённую формулировку
                    // (тоже LLM-текст резолвера — тот же гейт длины, что и у обычной авто-записи)
                    var merged = await CompressIfLongAsync(source, decision.MergedText!.Trim());
                    return Update(ownerId, scopeId, decision.TargetId, merged)
                        ?? Add(ownerId, scopeId, trimmed, type, source, sourceSessionId, salience);
                case Memory.MemoryWriteOp.Delete when !string.IsNullOrEmpty(decision.TargetId):
                    // Новый делает существующий устаревшим → удаляем target, добавляем новый
                    Remove(ownerId, scopeId, decision.TargetId);
                    return Add(ownerId, scopeId, trimmed, type, source, sourceSessionId, salience);
                default:   // Add и невалидные Update/Delete
                    return Add(ownerId, scopeId, trimmed, type, source, sourceSessionId, salience);
            }
        }
        catch (Exception ex)
        {
            _log?.LogDebug(ex, "memory-shelf: разрешение записи {Scope}", scopeId);
            return Add(ownerId, scopeId, trimmed, type, source, sourceSessionId, salience);
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
        lock (_saveLock) entries = Get(state.OwnerId, state.ScopeId).ToList();
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
    private void Reinforce(string ownerId, string scopeId, string entryId, string newText, double? salience)
    {
        lock (_saveLock)
        {
            var e = Get(ownerId, scopeId).FirstOrDefault(x => x.Id == entryId);
            if (e is null) return;
            var baseSal = salience is null ? e.Salience : Math.Max(e.Salience, Math.Clamp(salience.Value, 0.05, 1.0));
            e.Salience = Math.Clamp(baseSal + DedupBoost, 0.05, 1.0);
            if (newText.Length > e.Text.Length) e.Text = newText;
            Save();
        }
    }

    // Отредактировать текст записи вручную (UI-редактирование)
    public TeamMemoryEntry? Update(string ownerId, string scopeId, string entryId, string text)
    {
        TeamMemoryEntry? entry;
        lock (_saveLock)
        {
            entry = Get(ownerId, scopeId).FirstOrDefault(e => e.Id == entryId);
            if (entry is null) return null;
            entry.Text = text.Trim();
            Save();
        }
        QueueSync(ownerId, scopeId);
        return entry;
    }

    public bool Remove(string ownerId, string scopeId, string entryId)
    {
        bool ok;
        lock (_saveLock)
        {
            var list = Get(ownerId, scopeId);
            ok = list.RemoveAll(e => e.Id == entryId) > 0;
            if (ok) Save();
        }
        if (ok) QueueSync(ownerId, scopeId);
        return ok;
    }


    // Полнотекстовый recall (fallback без Dify): записи, разделяющие слова запроса, топ по перекрытию.
    // Сохранён как graceful degradation — используется, когда семантический слой недоступен.
    public TeamRecallResult BuildRecallBlock(string ownerId, string scopeId, string query, int topK = 4)
    {
        var snapshot = Snapshot(ownerId, scopeId);
        if (snapshot.Count == 0) return new TeamRecallResult(null, []);

        var ranked = MemoryFulltext.Rank(snapshot, query, topK, e => e.Text);
        if (ranked.Count == 0) return new TeamRecallResult(null, []);
        return new TeamRecallResult(FormatRecall(ranked), ranked);
    }

    // Семантический recall (③-3.4): при настроенном Dify ранжирует через retrieve + TeamMemoryScorer;
    // без Dify / при ошибке — graceful degradation к полнотекстовому BuildRecallBlock.
    public async Task<TeamRecallResult> BuildRecallBlockAsync(string ownerId, string scopeId, string query, int topK = 4)
    {
        var snapshot = Snapshot(ownerId, scopeId);
        if (snapshot.Count == 0) return new TeamRecallResult(null, []);

        var state = GetKnowledgeState(ownerId, scopeId);
        if (Available && !string.IsNullOrEmpty(state.DatasetId))
        {
            try
            {
                var ranked = await RankViaDifyAsync(state, snapshot, query, topK);
                if (ranked.Count == 0) return new TeamRecallResult(null, []);
                return new TeamRecallResult(FormatRecall(ranked), ranked);
            }
            catch (Exception ex) { _log?.LogDebug(ex, "memory-shelf: семантический recall {Scope}", scopeId); }
        }
        return BuildRecallBlock(ownerId, scopeId, query, topK);
    }

    // Поиск по памяти команды (для endpoint/MCP team_memory_search): при Dify — семантический
    // скоринг, иначе — полнотекст. Возвращает записи (не markdown), ранжированные по релевантности.
    public async Task<IReadOnlyList<TeamMemoryEntry>> SearchAsync(string ownerId, string scopeId,
        string query, int topK = 8)
    {
        var snapshot = Snapshot(ownerId, scopeId);
        if (snapshot.Count == 0) return [];

        var state = GetKnowledgeState(ownerId, scopeId);
        if (Available && !string.IsNullOrEmpty(state.DatasetId))
        {
            try { return await RankViaDifyAsync(state, snapshot, query, topK); }
            catch (Exception ex) { _log?.LogDebug(ex, "memory-shelf: семантический поиск {Scope}", scopeId); }
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

    // Предел записи в блоке recall. Записи разрослись до 2-3 КБ каждая, и блок командной памяти
    // съедал 8-10 КБ КАЖДОГО хода — при том, что личная память давно режется по 240 символов
    // (PersonaMemoryService.BuildRecallAsync). Сам стор не трогаем: обрезка только на выдаче
    // в промпт, полный текст персона всегда доберёт через team_memory_list.
    public const int RecallTextLimit = 280;


    // Гейт авто-записи (autolearn/резолвер противоречий): ручную запись контроллер уже держит
    // в MaxTextLength, но экстрактор-LLM иногда игнорирует «кратко» и отдаёт абзац — такие
    // записи реально доходили до 2-3 КБ в сторе и Dify. Порог срабатывания — 700 (не хотим
    // трогать записи, которые и так укладываются в разумный размер), цель сжатия — 500
    // (тот же порядок, что у ручной «одна мысль на запись»).
    private const int AutoCompressThreshold = 700;
    private const int AutoCompressTarget = 500;

    // Сжать длинную АВТО-запись (Source != Manual) до сути через CheapTextRunner; ручную не
    // трогаем — она уже ограничена MaxTextLength на контроллере. LLM недоступна, ошиблась или
    // не сократила (пустой/длиннее исходного ответ) — жёсткая обрезка по границе слова (Shorten),
    // потеря хвоста лучше простыни на 2-3 КБ в сторе и Dify.
    private async Task<string> CompressIfLongAsync(TeamMemorySource source, string text)
    {
        if (source == TeamMemorySource.Manual || text.Length <= AutoCompressThreshold) return text;
        if (_cheap is not null)
        {
            try
            {
                var prompt = "Сократи текст ниже до сути, не длиннее " + AutoCompressTarget +
                    " символов, сохрани ключевой факт. Ответь ТОЛЬКО сокращённым текстом на русском, " +
                    "без кавычек, пояснений и вступлений.\n\nТекст:\n" + text;
                var compressed = (await _cheap.RunFreeAsync(Llm.LocalActionCatalog.TeamMemoryCompress, prompt))
                    ?.Trim().Trim('"', '«', '»');
                if (!string.IsNullOrWhiteSpace(compressed) && compressed.Length < text.Length)
                {
                    var softTruncated = false;
                    return compressed.Length <= AutoCompressTarget
                        ? compressed : Shorten(compressed, AutoCompressTarget, ref softTruncated);
                }
            }
            catch (Exception ex) { _log?.LogDebug(ex, "memory-shelf: сжатие авто-записи не удалось"); }
        }
        var hardTruncated = false;
        return Shorten(text, AutoCompressTarget, ref hardTruncated);
    }

    private string FormatRecall(IReadOnlyList<TeamMemoryEntry> ranked)
    {
        var sb = new StringBuilder();
        var truncated = false;
        var lines = new List<string>(ranked.Count);
        foreach (var e in ranked)
        {
            var text = Shorten(e.Text.ReplaceLineEndings(" "), RecallTextLimit, ref truncated);
            lines.Add($"- {text}");
        }

        sb.AppendLine(_opts.RecallHeader);
        sb.AppendLine(truncated
            ? _opts.RecallIntroTruncated
            : _opts.RecallIntro);
        foreach (var line in lines)
            sb.AppendLine(line);
        return sb.ToString();
    }

    // Обрезка по границе слова: рубим по последнему пробелу в пределах лимита, чтобы фраза
    // не обрывалась посреди слова. Слова длиннее лимита (пути, ссылки) режем как есть.
    private static string Shorten(string text, int limit, ref bool truncated)
    {
        if (text.Length <= limit) return text;
        truncated = true;
        var head = text[..limit];
        var space = head.LastIndexOf(' ');
        if (space > limit / 2) head = head[..space];
        return head.TrimEnd(' ', ',', ';', '.', '—', '-') + "…";
    }

    // --- Консолидация (P4): применение операций merge/drop под save-lock ---

    // Применить операции консолидации. Валидация (гейты) — на стороне вызывающего
    // (TeamMemoryConsolidationService.FilterOps); здесь только атомарное применение:
    // merge = удалить источники + добавить сводную запись, drop = удалить.
    // Возвращает число затронутых записей; Dify-дифф при синке сам подчистит документы.
    public int ApplyConsolidation(string ownerId, string scopeId, IReadOnlyList<TeamMemoryConsolidationOp> ops)
    {
        if (ops.Count == 0) return 0;
        int affected = 0;
        lock (_saveLock)
        {
            var list = Get(ownerId, scopeId);
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
                        ProjectId = scopeId,
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
        if (affected > 0) QueueSync(ownerId, scopeId);
        return affected;
    }

    // Привести число записей к потолку: вытеснить хвост сверх MaxEntries по retention-скорингу.
    // Механическое, детерминированное — работает при одном лишь autolearn, без LLM-merge.
    public int EnforceCap(string ownerId, string scopeId)
    {
        var snapshot = Snapshot(ownerId, scopeId);
        var evictIds = TeamMemoryScorer.SelectEvictionIds(snapshot, _maxEntries, _scoring, DateTime.UtcNow);
        if (evictIds.Count == 0) return 0;
        var ops = evictIds.Select(id => new TeamMemoryConsolidationOp("drop", null, id, null, null, null)).ToList();
        return ApplyConsolidation(ownerId, scopeId, ops);
    }

    // Полное удаление памяти scope'а (проекта/сферы): Dify-датасет + оба стора. Локальное состояние
    // снимаем сразу, сбой Dify логируем.
    public async Task DeleteScopeAsync(string ownerId, string scopeId)
    {
        var key = Key(ownerId, scopeId);
        // Отложенный синк снимаем до удаления, иначе он воссоздал бы датасет уже удалённого scope'а;
        // идущий синк дожидаемся — он мог создать датасет, которого мы ещё не видим в _kStore
        _debounce.Cancel(key);
        await _syncLock.WaitAsync();
        string? datasetId;
        try
        {
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
        }
        finally { _syncLock.Release(); }
        if (!string.IsNullOrEmpty(datasetId) && _knowledge?.IsConfigured == true)
        {
            try { await _knowledge.DeleteDatasetAsync(datasetId); }
            catch (Exception ex) { _log?.LogWarning(ex, "memory-shelf: не удалить Dify-датасет {Scope}", scopeId); }
        }
    }

    // Best-effort переименование Dify-датасета scope'а (имя иначе стухает; работа по id не ломается)
    public async Task RenameDatasetAsync(string ownerId, string scopeId, string newName)
    {
        string? datasetId;
        lock (_kLock) datasetId = _kStore.GetValueOrDefault(Key(ownerId, scopeId))?.DatasetId;
        if (string.IsNullOrEmpty(datasetId) || _knowledge?.IsConfigured != true) return;
        await _knowledge.RenameDatasetAsync(datasetId, newName);
    }

    // Уборка локальных сторов полки всех scope'ов владельца — каскад удаления
    // пользователя. Dify-датасеты удаляет вызывающий общим проходом по префиксу имени.
    public void DeleteOwnerMemory(string ownerId)
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

    // Каскадное удаление знаний владельца через участник синка — обёртка над DeleteOwnerTeamMemory.
    // Dify на этом шаге не трогаем (общий проход в UserKnowledgeCascade).
    public Task DeleteAllAsync(string userId)
    {
        DeleteOwnerMemory(userId);
        return Task.CompletedTask;
    }

    // --- Участник реконсайлера error-документов (Knowledge.IKnowledgeSyncParticipant) ---
    // Цель на каждый scope «owner:project» с созданным датасетом; ключ записи — id записи.
    // Карту Docs защищает _kLock (не _saveLock — тот про записи памяти); порядок _syncLock → _kLock.
    public IReadOnlyList<Knowledge.KnowledgeSyncTarget> ListTargets()
    {
        List<(string Key, string DatasetId)> snapshot;
        lock (_kLock)
            snapshot = _kStore
                .Where(kv => !string.IsNullOrEmpty(kv.Value.DatasetId))
                .Select(kv => (kv.Key, kv.Value.DatasetId!))
                .ToList();

        var targets = new List<Knowledge.KnowledgeSyncTarget>();
        foreach (var (key, datasetId) in snapshot)
        {
            var idx = key.IndexOf(':');
            if (idx <= 0 || idx >= key.Length - 1) continue;
            var ownerId = key[..idx];
            var scopeId = key[(idx + 1)..];
            targets.Add(new Knowledge.KnowledgeSyncTarget(
                datasetId, [ownerId], $"{_opts.TargetLabel}:{key}",
                docIds => ResolveDocsAsync(key, docIds),
                keys => InvalidateDocsAsync(key, keys),
                () => QueueSync(ownerId, scopeId)));
        }
        return targets;
    }

    private async Task<IReadOnlyList<(string DocId, string EntryKey)>> ResolveDocsAsync(
        string key, IReadOnlyCollection<string> docIds)
    {
        await _syncLock.WaitAsync();
        try
        {
            lock (_kLock)
            {
                if (!_kStore.TryGetValue(key, out var state)) return [];
                var byDocId = state.Docs.ToDictionary(kv => kv.Value.DocId, kv => kv.Key);
                return docIds
                    .Where(byDocId.ContainsKey)
                    .Select(d => (d, byDocId[d]))
                    .ToList();
            }
        }
        finally { _syncLock.Release(); }
    }

    private async Task InvalidateDocsAsync(string key, IReadOnlyCollection<string> entryKeys)
    {
        await _syncLock.WaitAsync();
        try
        {
            lock (_kLock)
            {
                if (!_kStore.TryGetValue(key, out var state)) return;
                var changed = false;
                foreach (var entryKey in entryKeys)
                    if (state.Docs.TryGetValue(entryKey, out var doc) && doc.Hash.Length > 0)
                    {
                        // Замена объекта, не правка поля: снапшот активного синка делит объекты
                        state.Docs[entryKey] = new MemoryDocRef { DocId = doc.DocId, Hash = "" };
                        changed = true;
                    }
                if (changed) SaveKnowledge();
            }
        }
        finally { _syncLock.Release(); }
    }

    // --- Синхронизация с Dify (дифф по хешам, дебаунс) ---

    private void QueueSync(string ownerId, string scopeId)
    {
        if (!Available) return;
        _debounce.Schedule(Key(ownerId, scopeId), () => RunSyncSafe(ownerId, scopeId));
    }

    private void RunSyncSafe(string ownerId, string scopeId) =>
        _ = Task.Run(async () =>
        {
            try { await SyncAsync(ownerId, scopeId); }
            catch (Exception ex) { _log?.LogWarning(ex, "memory-shelf: синхронизация {Scope} в Dify", scopeId); }
        });

    public async Task<int> SyncAsync(string ownerId, string scopeId)
    {
        if (!Available) return 0;

        await _syncLock.WaitAsync();
        try
        {
            var state = GetKnowledgeState(ownerId, scopeId);
            if (string.IsNullOrEmpty(state.DatasetId))
            {
                // Пустой scope без датасета (память удалена вместе с владельцем) — создавать нечего
                if (Count(ownerId, scopeId) == 0)
                {
                    lock (_kLock) _kStore.Remove(Key(ownerId, scopeId));
                    return 0;
                }
                var datasetId = await _knowledge!.CreateDatasetAsync(_opts.DatasetName(ownerId, scopeId));
                lock (_kLock) { state.DatasetId = datasetId; SaveKnowledge(); }
            }

            // Снапшоты под локами — конкурентные мутации не должны видеть полу-состояние
            var entries = Snapshot(ownerId, scopeId);
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
                _log, _metrics);

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

    private List<TeamMemoryEntry> Get(string ownerId, string scopeId) =>
        _store.GetOrAdd(Key(ownerId, scopeId), _ => new List<TeamMemoryEntry>());

    private List<TeamMemoryEntry> Snapshot(string ownerId, string scopeId)
    {
        lock (_saveLock) return Get(ownerId, scopeId).ToList();
    }

    private KnowledgeState GetKnowledgeState(string ownerId, string scopeId)
    {
        var key = Key(ownerId, scopeId);
        lock (_kLock)
        {
            if (!_kStore.TryGetValue(key, out var s)) _kStore[key] = s = new KnowledgeState();
            s.OwnerId = ownerId;
            s.ScopeId = scopeId;
            return s;
        }
    }

    private static string Key(string ownerId, string scopeId) => $"{ownerId}:{scopeId}";

    private void Load()
    {
        try
        {
            var dict = JsonFileStore.Load<Dictionary<string, List<TeamMemoryEntry>>>(_storePath, JsonOpts);
            if (dict is null) return;
            foreach (var kv in dict) _store[kv.Key] = kv.Value;
        }
        catch (Exception ex) { _log?.LogWarning(ex, "memory-shelf: не загрузился стор"); }
    }

    private void Save()
    {
        lock (_saveLock)
            JsonFileStore.Save(_storePath, _store.ToDictionary(kv => kv.Key, kv => kv.Value), JsonOpts);
    }

    // Вызывается под _kLock
    private void SaveKnowledge() => JsonFileStore.Save(_knowledgeStorePath, _kStore, JsonOpts);

    // Уборка при остановке хоста (DI dispose'ит синглтон): таймеры дебаунса Dify-синка
    // не должны переживать остановку и запускать синк по мёртвому приложению
    public void Dispose() => _debounce.Dispose();
}

