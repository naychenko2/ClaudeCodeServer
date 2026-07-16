using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ClaudeHomeServer.Models;

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
        public Dictionary<string, DocRef> Docs { get; set; } = new();
        // Проставляются в GetKnowledgeState для удобства (ключ и так их несёт) — в стор не пишем
        [JsonIgnore] public string OwnerId { get; set; } = "";
        [JsonIgnore] public string ProjectId { get; set; } = "";
    }
    private sealed class DocRef
    {
        public string DocId { get; set; } = "";
        public string Hash { get; set; } = "";
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

    // Sibling-стор семантического слоя: data/team-memory-knowledge.json (ключ «owner:project»)
    private readonly string _knowledgeStorePath;
    private readonly Dictionary<string, KnowledgeState> _kStore;
    private readonly Lock _kLock = new();
    private readonly SemaphoreSlim _syncLock = new(1, 1);
    private readonly ConcurrentDictionary<string, Timer> _debounce = new();

    // Параметры скоринга (взвешенная сумма) и потолок памяти
    private readonly MemoryScoringOptions _scoring;
    private readonly int _maxEntries;
    private readonly double _dedupThreshold;

    // Прибавка важности при повторе факта: дедуп-on-write усиливает существующую запись, а не плодит дубль
    private const double DedupBoost = 0.1;

    public TeamMemoryService(IConfiguration config, ILogger<TeamMemoryService>? log = null,
        KnowledgeService? knowledge = null, UserStore? users = null, ProjectManager? projects = null)
    {
        _log = log;
        _knowledge = knowledge;
        _users = users;
        _projects = projects;
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
        _maxEntries = int.TryParse(config["TeamMemory:MaxEntries"], out var me) && me > 0 ? me : 200;
        _dedupThreshold = ReadDouble(config, "TeamMemory:DedupThreshold", 0.85);

        _kStore = JsonFileStore.Load<Dictionary<string, KnowledgeState>>(_knowledgeStorePath, JsonOpts) ?? new();
        Load();
    }

    private static double ReadDouble(IConfiguration config, string key, double fallback) =>
        double.TryParse(config[key], System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : fallback;

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

        var ranked = FullTextRank(snapshot, query, topK);
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
        return FullTextRank(snapshot, query, topK);
    }

    // Ранжирование через Dify: релевантность по чанкам → взвешенная сумма TeamMemoryScorer → topK
    private async Task<List<TeamMemoryEntry>> RankViaDifyAsync(
        KnowledgeState state, IReadOnlyList<TeamMemoryEntry> snapshot, string query, int topK)
    {
        Dictionary<string, string> byDocId;
        lock (_kLock) byDocId = state.Docs.ToDictionary(kv => kv.Value.DocId, kv => kv.Key);

        var chunks = await _knowledge!.RetrieveAsync(state.DatasetId!, query, Math.Max(topK, 12));
        var relevance = new Dictionary<string, double>();
        foreach (var ch in chunks)
            if (byDocId.TryGetValue(ch.DocumentId, out var entryId))
                relevance[entryId] = Math.Max(relevance.GetValueOrDefault(entryId), ch.Score);

        var now = DateTime.UtcNow;
        return snapshot
            .Select(e => (e, score: TeamMemoryScorer.Score(e, relevance.GetValueOrDefault(e.Id, 0.0), now, _scoring)))
            .Where(x => x.score > 0)
            .OrderByDescending(x => x.score)
            .Take(topK)
            .Select(x => x.e)
            .ToList();
    }

    // Полнотекстовый ранкер: перекрытие слов запроса (устойчиво к отсутствию Dify)
    private static List<TeamMemoryEntry> FullTextRank(IReadOnlyList<TeamMemoryEntry> snapshot, string query, int topK)
    {
        var q = Tokenize(query);
        if (q.Length == 0) return [];
        return snapshot
            .Select(e => (e, score: Tokenize(e.Text).Count(t => q.Contains(t))))
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

    // --- Синхронизация с Dify (дифф по хешам, дебаунс) ---

    private void QueueSync(string ownerId, string projectId)
    {
        if (!Available) return;
        var key = Key(ownerId, projectId);
        _debounce.AddOrUpdate(key,
            _ => new Timer(_ => RunSyncSafe(ownerId, projectId), null, SyncDebounce, Timeout.InfiniteTimeSpan),
            (_, timer) => { timer.Change(SyncDebounce, Timeout.InfiniteTimeSpan); return timer; });
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
            Dictionary<string, DocRef> docsSnapshot;
            lock (_kLock) docsSnapshot = new Dictionary<string, DocRef>(state.Docs);

            var alive = new HashSet<string>(entries.Select(e => e.Id));
            var changed = 0;

            foreach (var e in entries)
            {
                var hash = Hash($"{e.Type}\n{e.Text}\n{string.Join(',', e.Tags ?? [])}");
                if (docsSnapshot.TryGetValue(e.Id, out var doc) && doc.Hash == hash) continue;

                if (doc is not null)
                    try { await _knowledge!.DeleteDocumentAsync(state.DatasetId!, doc.DocId); }
                    catch (Exception ex) { _log?.LogDebug(ex, "team-memory: удаление старого документа {Entry}", e.Id); }

                var info = await _knowledge!.IndexFileByTextAsync(
                    state.DatasetId!, $"{TypeLabel(e.Type)}-{e.Id}", e.Text, e.Tags);
                lock (_kLock) state.Docs[e.Id] = new DocRef { DocId = info.Id, Hash = hash };
                changed++;
            }

            foreach (var stale in docsSnapshot.Keys.Where(k => !alive.Contains(k)).ToList())
            {
                try { await _knowledge!.DeleteDocumentAsync(state.DatasetId!, docsSnapshot[stale].DocId); }
                catch (Exception ex) { _log?.LogDebug(ex, "team-memory: удаление документа исчезнувшей записи"); }
                lock (_kLock) state.Docs.Remove(stale);
                changed++;
            }

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

    private static readonly HashSet<string> Stop = new(StringComparer.OrdinalIgnoreCase)
    { "и", "в", "на", "с", "по", "для", "не", "что", "это", "как", "to", "the", "a", "of", "and", "for", "in" };

    private static string[] Tokenize(string s) =>
        s.ToLowerInvariant().Split([' ', ',', '.', ';', ':', '!', '?', '\n', '\r', '\t', '(', ')'],
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Where(t => t.Length > 2 && !Stop.Contains(t))
        .Distinct()
        .ToArray();

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

    private static string Hash(string s) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s)));
}

// Операция консолидации памяти команды (P4): merge — схлопнуть несколько записей одного типа
// в одну сводную (Ids → новая запись Text/Type/Salience); drop — удалить запись Id.
public sealed record TeamMemoryConsolidationOp(
    string Op, List<string>? Ids, string? Id, TeamMemoryType? Type, string? Text, double? Salience)
{
    public bool IsMerge => string.Equals(Op, "merge", StringComparison.OrdinalIgnoreCase);
    public bool IsDrop => string.Equals(Op, "drop", StringComparison.OrdinalIgnoreCase);
}
