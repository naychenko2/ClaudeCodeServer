using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ClaudeHomeServer.Models;

namespace ClaudeHomeServer.Services;

// Долгая память персоны («олицетворённого агента»). Типизированные записи
// (semantic/episodic/procedural) — источник правды в data/persona-memory.json;
// семантический слой дублируется в Dify-датасет per-persona для векторного retrieve.
// Скоринг recall — relevance × recency × typeWeight (подход 2026). Без Dify —
// graceful degradation к полнотекстовому поиску по стору.
public sealed class PersonaMemoryService
{
    // personaId → { datasetId, записи, entryId → { difyDocId, hash } }
    private sealed class MemState
    {
        public string? DatasetId { get; set; }
        public List<PersonaMemoryEntry> Entries { get; set; } = new();
        public Dictionary<string, DocRef> Docs { get; set; } = new();
    }
    private sealed class DocRef
    {
        public string DocId { get; set; } = "";
        public string Hash { get; set; } = "";
    }

    // Веса типов памяти для скоринга recall (semantic важнее эпизодов и приёмов)
    private static double TypeWeight(PersonaMemoryType t) => t switch
    {
        PersonaMemoryType.Semantic => 0.6,
        PersonaMemoryType.Episodic => 0.3,
        PersonaMemoryType.Procedural => 0.1,
        _ => 0.3,
    };
    // Период полураспада значимости по свежести (дни)
    private const double RecencyHalfLifeDays = 30.0;

    private static readonly TimeSpan SyncDebounce = TimeSpan.FromSeconds(15);
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private readonly KnowledgeService _knowledge;
    private readonly PersonaManager _personas;
    private readonly UserStore _users;
    private readonly ILogger<PersonaMemoryService> _logger;
    private readonly string _storePath;
    private readonly Dictionary<string, MemState> _store;
    private readonly Lock _saveLock = new();
    private readonly SemaphoreSlim _syncLock = new(1, 1);
    private readonly ConcurrentDictionary<string, Timer> _debounce = new();

    public PersonaMemoryService(KnowledgeService knowledge, PersonaManager personas, UserStore users,
        IConfiguration config, ILogger<PersonaMemoryService> logger)
    {
        _knowledge = knowledge;
        _personas = personas;
        _users = users;
        _logger = logger;
        var dataDir = Path.GetDirectoryName(
            config["DataPath"] ?? Path.Combine(AppContext.BaseDirectory, "data", "projects.json"))!;
        _storePath = Path.Combine(dataDir, "persona-memory.json");
        _store = JsonFileStore.Load<Dictionary<string, MemState>>(_storePath, JsonOpts) ?? new();
    }

    public bool Available => _knowledge.IsConfigured;

    // Записать факт/событие/приём в память персоны (явный write-path)
    public PersonaMemoryEntry? Remember(string ownerId, string personaId, PersonaMemoryType type,
        string text, List<string>? tags, string? sourceSessionId)
    {
        var persona = _personas.Get(personaId, ownerId);
        if (persona is null || string.IsNullOrWhiteSpace(text)) return null;

        var trimmed = text.Trim();
        var entry = new PersonaMemoryEntry
        {
            PersonaId = personaId,
            Type = type,
            Text = trimmed,
            Tags = tags is { Count: > 0 } ? tags : null,
            SourceSessionId = sourceSessionId,
        };
        var state = GetState(personaId);
        lock (_saveLock)
        {
            // Дедуп: не плодим одинаковые записи (актуально для авто-памяти)
            if (state.Entries.Any(e => e.Type == type
                && string.Equals(e.Text, trimmed, StringComparison.OrdinalIgnoreCase)))
                return null;
            state.Entries.Add(entry);
            JsonFileStore.Save(_storePath, _store, JsonOpts);
        }
        QueueSync(ownerId, personaId);
        return entry;
    }

    // Все записи памяти персоны (для панели «что помнит агент»); type — необязательный фильтр
    public IReadOnlyList<PersonaMemoryEntry> List(string ownerId, string personaId, PersonaMemoryType? type)
    {
        if (_personas.Get(personaId, ownerId) is null) return [];
        var state = GetState(personaId);
        lock (_saveLock)
        {
            return state.Entries
                .Where(e => type is null || e.Type == type)
                .OrderByDescending(e => e.CreatedAt)
                .ToList();
        }
    }

    // Забыть запись
    public bool Forget(string ownerId, string personaId, string entryId)
    {
        if (_personas.Get(personaId, ownerId) is null) return false;
        var state = GetState(personaId);
        bool removed;
        lock (_saveLock)
        {
            removed = state.Entries.RemoveAll(e => e.Id == entryId) > 0;
            if (removed) JsonFileStore.Save(_storePath, _store, JsonOpts);
        }
        if (removed) QueueSync(ownerId, personaId);
        return removed;
    }

    // Поиск по памяти со скорингом relevance × recency × typeWeight.
    // С Dify — семантический retrieve; без — полнотекст по стору.
    public async Task<IReadOnlyList<PersonaMemoryHit>> SearchAsync(string ownerId, string personaId,
        string query, int topK = 8)
    {
        var persona = _personas.Get(personaId, ownerId);
        if (persona is null) return [];
        var state = GetState(personaId);
        List<PersonaMemoryEntry> entriesSnapshot;
        lock (_saveLock) entriesSnapshot = state.Entries.ToList();
        if (entriesSnapshot.Count == 0) return [];

        // relevance по entryId (0..1)
        Dictionary<string, double> relevance;
        if (Available && !string.IsNullOrEmpty(state.DatasetId))
        {
            var byDocId = state.Docs.ToDictionary(kv => kv.Value.DocId, kv => kv.Key);
            var chunks = await _knowledge.RetrieveAsync(state.DatasetId!, query, Math.Max(topK, 12));
            relevance = new();
            foreach (var ch in chunks)
                if (byDocId.TryGetValue(ch.DocumentId, out var entryId))
                    relevance[entryId] = Math.Max(relevance.GetValueOrDefault(entryId), ch.Score);
        }
        else
        {
            relevance = FullTextRelevance(entriesSnapshot, query);
        }

        var now = DateTime.UtcNow;
        var hits = entriesSnapshot
            .Select(e =>
            {
                var rel = relevance.GetValueOrDefault(e.Id, 0.0);
                var ageDays = (now - e.CreatedAt).TotalDays;
                var recency = Math.Pow(2, -Math.Max(ageDays, 0) / RecencyHalfLifeDays);
                var score = rel * recency * TypeWeight(e.Type) * Math.Clamp(e.Salience, 0.1, 1.0);
                return new PersonaMemoryHit(e.Id, e.Type, e.Text, e.Tags ?? [], Math.Round(score, 4), e.CreatedAt);
            })
            .Where(h => h.Score > 0)
            .OrderByDescending(h => h.Score)
            .Take(topK)
            .ToList();
        return hits;
    }

    // Markdown-блок памяти для системного промпта хода (auto-recall персоны).
    // null — нечего подмешивать / память выключена.
    public async Task<string?> BuildRecallAsync(string ownerId, string personaId, string query,
        int topK, double minScore)
    {
        var persona = _personas.Get(personaId, ownerId);
        if (persona is null || !persona.MemoryEnabled) return null;
        IReadOnlyList<PersonaMemoryHit> hits;
        try { hits = await SearchAsync(ownerId, personaId, query, Math.Max(topK, 6)); }
        catch (Exception ex) { _logger.LogWarning(ex, "Persona memory recall {Persona}", personaId); return null; }

        var top = hits.Where(h => h.Score >= minScore).Take(topK).ToList();
        if (top.Count == 0) return null;

        var sb = new StringBuilder();
        sb.AppendLine("Из твоей долгой памяти всплывает релевантное (используй, если помогает; можешь дополнять её через mcp__memory__memory_remember):");
        foreach (var h in top)
        {
            var text = h.Text.Length > 240 ? h.Text[..237] + "…" : h.Text;
            sb.AppendLine($"- ({TypeLabel(h.Type)}) {text.Replace('\n', ' ')}");
        }
        return sb.ToString();
    }

    private static string TypeLabel(PersonaMemoryType t) => t switch
    {
        PersonaMemoryType.Semantic => "факт",
        PersonaMemoryType.Episodic => "эпизод",
        PersonaMemoryType.Procedural => "приём",
        _ => "память",
    };

    // Полнотекстовый fallback: доля слов запроса, встретившихся в записи (0..1)
    private static Dictionary<string, double> FullTextRelevance(
        IReadOnlyList<PersonaMemoryEntry> entries, string query)
    {
        var terms = query.ToLowerInvariant()
            .Split(new[] { ' ', ',', '.', ';', ':', '!', '?', '\n', '\t' }, StringSplitOptions.RemoveEmptyEntries)
            .Where(w => w.Length > 2).Distinct().ToArray();
        var result = new Dictionary<string, double>();
        if (terms.Length == 0)
        {
            // Пустой запрос — все записи равнозначно релевантны (свежесть решит)
            foreach (var e in entries) result[e.Id] = 0.5;
            return result;
        }
        foreach (var e in entries)
        {
            var hay = (e.Text + " " + string.Join(' ', e.Tags ?? [])).ToLowerInvariant();
            var matched = terms.Count(t => hay.Contains(t));
            if (matched > 0) result[e.Id] = (double)matched / terms.Length;
        }
        return result;
    }

    // --- Синхронизация с Dify (дифф по хешам, дебаунс) ---

    private void QueueSync(string ownerId, string personaId)
    {
        if (!Available) return;
        _debounce.AddOrUpdate(personaId,
            _ => new Timer(_ => RunSyncSafe(ownerId, personaId), null, SyncDebounce, Timeout.InfiniteTimeSpan),
            (_, timer) => { timer.Change(SyncDebounce, Timeout.InfiniteTimeSpan); return timer; });
    }

    private void RunSyncSafe(string ownerId, string personaId) =>
        _ = Task.Run(async () =>
        {
            try { await SyncAsync(ownerId, personaId); }
            catch (Exception ex) { _logger.LogWarning(ex, "Синхронизация памяти персоны {Persona} в Dify", personaId); }
        });

    public async Task<int> SyncAsync(string ownerId, string personaId)
    {
        if (!Available) return 0;
        var persona = _personas.Get(personaId, ownerId);
        if (persona is null) return 0;

        await _syncLock.WaitAsync();
        try
        {
            var state = GetState(personaId);
            if (string.IsNullOrEmpty(state.DatasetId))
            {
                var username = _users.GetById(ownerId)?.Username ?? ownerId;
                var datasetId = await _knowledge.CreateDatasetAsync($"{username}:persona:{persona.Handle}");
                lock (_saveLock) { state.DatasetId = datasetId; JsonFileStore.Save(_storePath, _store, JsonOpts); }
            }

            // Снапшоты под локом — конкурентные Remember/Forget/Save не должны видеть полу-мутацию
            List<PersonaMemoryEntry> entries;
            Dictionary<string, DocRef> docsSnapshot;
            lock (_saveLock)
            {
                entries = state.Entries.ToList();
                docsSnapshot = new Dictionary<string, DocRef>(state.Docs);
            }
            var alive = new HashSet<string>(entries.Select(e => e.Id));
            var changed = 0;

            foreach (var e in entries)
            {
                var hash = Hash($"{e.Type}\n{e.Text}\n{string.Join(',', e.Tags ?? [])}");
                if (docsSnapshot.TryGetValue(e.Id, out var doc) && doc.Hash == hash) continue;

                if (doc is not null)
                    try { await _knowledge.DeleteDocumentAsync(state.DatasetId!, doc.DocId); }
                    catch (Exception ex) { _logger.LogDebug(ex, "Удаление старой записи памяти {Entry}", e.Id); }

                var info = await _knowledge.IndexFileByTextAsync(
                    state.DatasetId!, $"{TypeLabel(e.Type)}-{e.Id}", e.Text, e.Tags);
                lock (_saveLock) state.Docs[e.Id] = new DocRef { DocId = info.Id, Hash = hash };
                changed++;
            }

            foreach (var stale in docsSnapshot.Keys.Where(k => !alive.Contains(k)).ToList())
            {
                try { await _knowledge.DeleteDocumentAsync(state.DatasetId!, docsSnapshot[stale].DocId); }
                catch (Exception ex) { _logger.LogDebug(ex, "Удаление документа исчезнувшей записи памяти"); }
                lock (_saveLock) state.Docs.Remove(stale);
                changed++;
            }

            if (changed > 0) Save();
            return changed;
        }
        finally { _syncLock.Release(); }
    }

    private MemState GetState(string personaId)
    {
        lock (_saveLock)
        {
            if (!_store.TryGetValue(personaId, out var s)) _store[personaId] = s = new MemState();
            return s;
        }
    }

    private void Save()
    {
        lock (_saveLock) JsonFileStore.Save(_storePath, _store, JsonOpts);
    }

    private static string Hash(string s) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s)));
}

// Результат поиска по памяти персоны
public record PersonaMemoryHit(
    string Id, PersonaMemoryType Type, string Text, IReadOnlyList<string> Tags, double Score, DateTime CreatedAt);
