using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ClaudeHomeServer.Models;

namespace ClaudeHomeServer.Services;

// Долгая память персоны. Типизированные записи
// (semantic/episodic/procedural) — источник правды в data/persona-memory.json;
// семантический слой дублируется в Dify-датасет per-persona для векторного retrieve.
// Скоринг recall — relevance × recency × typeWeight (подход 2026). Без Dify —
// graceful degradation к полнотекстовому поиску по стору.
public sealed class PersonaMemoryService
{
    // personaId → { datasetId, записи, рабочий фокус, entryId → { difyDocId, hash } }
    private sealed class MemState
    {
        public string? DatasetId { get; set; }
        public List<PersonaMemoryEntry> Entries { get; set; } = new();
        public Dictionary<string, DocRef> Docs { get; set; } = new();
        // Рабочий фокус (P3): одна ячейка «что я сейчас делаю», не запись памяти
        public PersonaWorkingFocus? Focus { get; set; }
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

    private readonly KnowledgeService _knowledge;
    private readonly PersonaManager _personas;
    private readonly UserStore _users;
    private readonly ILogger<PersonaMemoryService> _logger;
    private readonly string _storePath;
    private readonly MemoryScoringOptions _scoring;
    // Потолки памяти (не гейтятся флагом консолидации — жёсткая защита от разрастания)
    private readonly int _maxEntries;
    private readonly int _maxEpisodic;
    // Семантический дедуп на входе: порог близости и прирост важности при повторе (P1)
    private readonly double _dedupThreshold;
    private readonly double _dedupBoost;
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
        // Параметры скоринга (взвешенная сумма) — Persona:Score:*; дефолты см. MemoryScoringOptions.Default
        var d = MemoryScoringOptions.Default;
        _scoring = new MemoryScoringOptions(
            ReadDouble(config, "Persona:Score:RelevanceWeight", d.RelevanceWeight),
            ReadDouble(config, "Persona:Score:RecencyWeight", d.RecencyWeight),
            ReadDouble(config, "Persona:Score:SalienceWeight", d.SalienceWeight),
            ReadDouble(config, "Persona:Score:TypeWeight", d.TypeWeight),
            ReadDouble(config, "Persona:Score:RecencyHalfLifeDays", d.RecencyHalfLifeDays),
            ReadDouble(config, "Persona:Score:MinRelevance", d.MinRelevance));
        _maxEntries = int.TryParse(config["Persona:MemoryMaxEntries"], out var me) && me > 0 ? me : 150;
        _maxEpisodic = int.TryParse(config["Persona:MemoryMaxEpisodic"], out var mep) && mep > 0 ? mep : 40;
        _dedupThreshold = ReadDouble(config, "Persona:DedupThreshold", 0.85);
        _dedupBoost = ReadDouble(config, "Persona:DedupSalienceBoost", 0.1);
        _store = JsonFileStore.Load<Dictionary<string, MemState>>(_storePath, JsonOpts) ?? new();
    }

    private static double ReadDouble(IConfiguration config, string key, double fallback) =>
        double.TryParse(config[key], System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : fallback;

    public bool Available => _knowledge.IsConfigured;

    // Записать факт/событие/приём в память персоны (явный write-path).
    // salience — значимость 0..1 (клампится в 0.05..1); null = 1.0
    public PersonaMemoryEntry? Remember(string ownerId, string personaId, PersonaMemoryType type,
        string text, List<string>? tags, string? sourceSessionId, double? salience = null)
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
            Salience = salience is null ? 1.0 : Math.Clamp(salience.Value, 0.05, 1.0),
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

    // Семантический write-path (P1): перед добавлением ищет близкий дубль через Dify.
    // Нашёлся дубль того же типа (score ≥ DedupThreshold) — не плодим запись, а усиливаем
    // существующую (reinforcement важности + освежение + более полный текст) и возвращаем её.
    // Без Dify / без дубля — делегирует в точный Remember. Предпочтителен для авто-памяти.
    public async Task<PersonaMemoryEntry?> RememberAsync(string ownerId, string personaId,
        PersonaMemoryType type, string text, List<string>? tags, string? sourceSessionId, double? salience = null)
    {
        var persona = _personas.Get(personaId, ownerId);
        if (persona is null || string.IsNullOrWhiteSpace(text)) return null;
        var trimmed = text.Trim();

        var state = GetState(personaId);
        if (Available && !string.IsNullOrEmpty(state.DatasetId))
        {
            try
            {
                var dup = await FindSemanticDuplicateAsync(state, type, trimmed);
                if (dup is not null)
                {
                    Reinforce(personaId, dup.Id, trimmed, salience);
                    QueueSync(ownerId, personaId);   // текст мог смениться на более полный
                    return dup;
                }
            }
            catch (Exception ex) { _logger.LogDebug(ex, "Семантический дедуп памяти {Persona}", personaId); }
        }

        return Remember(ownerId, personaId, type, text, tags, sourceSessionId, salience);
    }

    // Найти запись того же типа, семантически близкую к тексту (Dify retrieve, порог DedupThreshold)
    private async Task<PersonaMemoryEntry?> FindSemanticDuplicateAsync(
        MemState state, PersonaMemoryType type, string text)
    {
        var chunks = await _knowledge.RetrieveAsync(state.DatasetId!, text, 5);
        if (chunks.Count == 0) return null;

        Dictionary<string, string> byDocId;
        List<PersonaMemoryEntry> entries;
        lock (_saveLock)
        {
            byDocId = state.Docs.ToDictionary(kv => kv.Value.DocId, kv => kv.Key);
            entries = state.Entries.ToList();
        }
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

    // Reinforcement при повторе факта: усилить важность, освежить обращение, взять более
    // полный текст (если новый длиннее). Двигает запись вверх в скоринге вместо дубля.
    private void Reinforce(string personaId, string entryId, string newText, double? salience)
    {
        lock (_saveLock)
        {
            var e = GetState(personaId).Entries.FirstOrDefault(x => x.Id == entryId);
            if (e is null) return;
            e.LastAccessedAt = DateTime.UtcNow;
            var baseSalience = salience is null ? e.Salience : Math.Max(e.Salience, Math.Clamp(salience.Value, 0.05, 1.0));
            e.Salience = Math.Clamp(baseSalience + _dedupBoost, 0.05, 1.0);
            if (newText.Length > e.Text.Length) e.Text = newText;   // более информативная формулировка
            JsonFileStore.Save(_storePath, _store, JsonOpts);
        }
    }

    // Все записи памяти персоны (для панели «что помнит персона»); type — необязательный фильтр
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

    // Поиск по памяти со скорингом взвешенной суммой (PersonaMemoryScorer):
    // wRel·relevance + wRec·recency + wSal·salience + wType·typeFactor.
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
                var score = PersonaMemoryScorer.Score(e, rel, now, _scoring);
                return new PersonaMemoryHit(e.Id, e.Type, e.Text, e.Tags ?? [], Math.Round(score, 4), e.CreatedAt);
            })
            .Where(h => h.Score > 0)
            .OrderByDescending(h => h.Score)
            .Take(topK)
            .ToList();
        return hits;
    }

    // Markdown-блок памяти для системного промпта хода (auto-recall персоны).
    // Рабочий фокус (если есть) — всегда первым блоком, без скоринга; при фокусе
    // результат не-null даже без хитов. null — нечего подмешивать / память выключена.
    public async Task<string?> BuildRecallAsync(string ownerId, string personaId, string query,
        int topK, double minScore)
    {
        var persona = _personas.Get(personaId, ownerId);
        if (persona is null || !persona.MemoryEnabled) return null;

        PersonaWorkingFocus? focus;
        lock (_saveLock) focus = GetState(personaId).Focus;

        IReadOnlyList<PersonaMemoryHit> hits;
        try { hits = await SearchAsync(ownerId, personaId, query, Math.Max(topK, 6)); }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Persona memory recall {Persona}", personaId);
            hits = [];
        }

        var top = hits.Where(h => h.Score >= minScore).Take(topK).ToList();
        if (top.Count == 0 && focus is null) return null;

        var sb = new StringBuilder();
        if (focus is not null)
        {
            sb.Append($"Твоё текущее дело (рабочая память): {focus.What}. Статус: {focus.Status}.");
            if (!string.IsNullOrWhiteSpace(focus.NextStep)) sb.Append($" Следующий шаг: {focus.NextStep}.");
            sb.AppendLine();
        }
        if (top.Count > 0)
        {
            if (focus is not null) sb.AppendLine();
            sb.AppendLine("Из твоей долгой памяти всплывает релевантное (используй, если помогает; можешь дополнять её через mcp__memory__memory_remember):");
            foreach (var h in top)
            {
                var text = h.Text.Length > 240 ? h.Text[..237] + "…" : h.Text;
                sb.AppendLine($"- ({TypeLabel(h.Type)}) {text.Replace('\n', ' ')}");
            }
            // Reinforcement: только записи, реально попавшие в блок, считаются «вспомненными»
            Touch(personaId, top.Select(h => h.Id).ToHashSet());
        }
        return sb.ToString();
    }

    // Reinforcement: отметить обращение к записям (LastAccessedAt = now). Dify не трогаем —
    // хеш документа (Type/Text/Tags) не включает LastAccessedAt, дифф-синк не сработает.
    private void Touch(string personaId, IReadOnlySet<string> entryIds)
    {
        if (entryIds.Count == 0) return;
        var state = GetState(personaId);
        lock (_saveLock)
        {
            var now = DateTime.UtcNow;
            var changed = false;
            foreach (var e in state.Entries)
                if (entryIds.Contains(e.Id)) { e.LastAccessedAt = now; changed = true; }
            if (changed) JsonFileStore.Save(_storePath, _store, JsonOpts);
        }
    }

    // --- Рабочий фокус (P3): «что я сейчас делаю» ---

    public PersonaWorkingFocus? GetFocus(string ownerId, string personaId)
    {
        if (_personas.Get(personaId, ownerId) is null) return null;
        lock (_saveLock) return GetState(personaId).Focus;
    }

    public PersonaWorkingFocus? SetFocus(string ownerId, string personaId,
        string what, string status, string? nextStep, string? sourceSessionId)
    {
        if (_personas.Get(personaId, ownerId) is null || string.IsNullOrWhiteSpace(what)) return null;
        var focus = new PersonaWorkingFocus
        {
            What = what.Trim(),
            Status = string.IsNullOrWhiteSpace(status) ? "в работе" : status.Trim(),
            NextStep = string.IsNullOrWhiteSpace(nextStep) ? null : nextStep.Trim(),
            SourceSessionId = sourceSessionId,
            UpdatedAt = DateTime.UtcNow,
        };
        var state = GetState(personaId);
        lock (_saveLock)
        {
            state.Focus = focus;
            JsonFileStore.Save(_storePath, _store, JsonOpts);
        }
        return focus;
    }

    public bool ClearFocus(string ownerId, string personaId)
    {
        if (_personas.Get(personaId, ownerId) is null) return false;
        var state = GetState(personaId);
        lock (_saveLock)
        {
            if (state.Focus is null) return false;
            state.Focus = null;
            JsonFileStore.Save(_storePath, _store, JsonOpts);
        }
        return true;
    }

    // --- Консолидация (P4): применение операций merge/drop под save-lock ---

    // Применить операции консолидации. Валидация (гейты) — на стороне вызывающего
    // (PersonaMemoryConsolidationService); здесь только атомарное применение:
    // merge = удалить источники + добавить сводную запись, drop = удалить.
    // Возвращает число затронутых записей. Merged-записи идут обычным путём —
    // Dify-дифф при синке сам удалит старые документы и добавит новый.
    public int ApplyConsolidation(string ownerId, string personaId,
        IReadOnlyList<MemoryConsolidationOp> ops)
    {
        var persona = _personas.Get(personaId, ownerId);
        if (persona is null || ops.Count == 0) return 0;

        var state = GetState(personaId);
        int affected = 0;
        lock (_saveLock)
        {
            foreach (var op in ops)
            {
                if (op.IsMerge)
                {
                    var sources = state.Entries.Where(e => op.Ids!.Contains(e.Id)).ToList();
                    if (sources.Count < 2 || string.IsNullOrWhiteSpace(op.Text)) continue;
                    state.Entries.RemoveAll(e => op.Ids!.Contains(e.Id));
                    state.Entries.Add(new PersonaMemoryEntry
                    {
                        PersonaId = personaId,
                        Type = op.Type ?? sources[0].Type,
                        Text = op.Text!.Trim(),
                        Salience = Math.Clamp(op.Salience ?? sources.Max(s => s.Salience), 0.05, 1.0),
                    });
                    affected += sources.Count;
                }
                else if (op.IsDrop)
                {
                    affected += state.Entries.RemoveAll(e => e.Id == op.Id);
                }
            }
            if (affected > 0) JsonFileStore.Save(_storePath, _store, JsonOpts);
        }
        if (affected > 0) QueueSync(ownerId, personaId);
        return affected;
    }

    // --- Потолок памяти (P0/P3): жёсткое вытеснение хвоста, НЕ гейтится флагом консолидации ---

    // Настроенные потолки памяти (из конфига Persona:MemoryMaxEntries/MemoryMaxEpisodic)
    public (int MaxEntries, int MaxEpisodic) Caps => (_maxEntries, _maxEpisodic);

    // Привести число записей к потолку: вытеснить лишние эпизоды сверх под-лимита и хвост
    // сверх общего лимита (по retention-скорингу). Возвращает число вытесненных записей.
    // Механическое, детерминированное — работает при одном лишь autolearn, без LLM-merge.
    public int EnforceCap(string ownerId, string personaId) =>
        EnforceCap(ownerId, personaId, _maxEntries, _maxEpisodic);

    public int EnforceCap(string ownerId, string personaId, int maxEntries, int maxEpisodic)
    {
        if (_personas.Get(personaId, ownerId) is null) return 0;
        List<PersonaMemoryEntry> snapshot;
        lock (_saveLock) snapshot = GetState(personaId).Entries.ToList();

        var evictIds = PersonaMemoryScorer.SelectEvictionIds(
            snapshot, maxEntries, maxEpisodic, _scoring, DateTime.UtcNow);
        if (evictIds.Count == 0) return 0;

        var dropOps = evictIds
            .Select(id => new MemoryConsolidationOp("drop", null, id, null, null, null))
            .ToList();
        return ApplyConsolidation(ownerId, personaId, dropOps);
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

    // Полное удаление памяти персоны — при удалении самой персоны: Dify-датасет + локальный
    // стор (data/persona-memory.json). Локальное состояние снимаем сразу, даже если вызов
    // Dify упадёт (чтобы не висеть сиротой); сбой Dify логируем, не роняем удаление персоны.
    public async Task DeletePersonaAsync(string personaId)
    {
        string? datasetId;
        lock (_saveLock)
        {
            datasetId = _store.GetValueOrDefault(personaId)?.DatasetId;
            _store.Remove(personaId);
        }
        Save();
        if (!string.IsNullOrEmpty(datasetId) && _knowledge.IsConfigured)
        {
            try { await _knowledge.DeleteDatasetAsync(datasetId); }
            catch (Exception ex) { _logger?.LogWarning(ex, "Не удалось удалить Dify-датасет памяти персоны {PersonaId}", personaId); }
        }
    }

    private static string Hash(string s) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s)));
}

// Результат поиска по памяти персоны
public record PersonaMemoryHit(
    string Id, PersonaMemoryType Type, string Text, IReadOnlyList<string> Tags, double Score, DateTime CreatedAt);

// Операция консолидации памяти (P4): merge — схлопнуть несколько записей одного типа
// в одну сводную (Ids → новая запись Text/Type/Salience); drop — удалить запись Id.
public sealed record MemoryConsolidationOp(
    string Op, List<string>? Ids, string? Id, PersonaMemoryType? Type, string? Text, double? Salience)
{
    public bool IsMerge => string.Equals(Op, "merge", StringComparison.OrdinalIgnoreCase);
    public bool IsDrop => string.Equals(Op, "drop", StringComparison.OrdinalIgnoreCase);
}
