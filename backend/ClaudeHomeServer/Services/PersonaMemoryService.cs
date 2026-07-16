using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.Memory;

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
        public Dictionary<string, MemoryDocRef> Docs { get; set; } = new();
        // Рабочий фокус (P3): одна ячейка «что я сейчас делаю», не запись памяти
        public PersonaWorkingFocus? Focus { get; set; }
    }

    private static readonly TimeSpan SyncDebounce = TimeSpan.FromSeconds(15);
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private readonly KnowledgeService _knowledge;
    private readonly PersonaManager _personas;
    private readonly UserStore _users;
    private readonly TeamMemoryService? _teamMemory;
    // LLM-резолвер записи памяти (разрешение противоречий на авто-пути); null в юнит-тестах
    private readonly Memory.MemoryWriteResolver? _resolver;
    private readonly ILogger<PersonaMemoryService> _logger;
    private readonly string _storePath;
    private readonly MemoryScoringOptions _scoring;
    // Параметры гибридного retrieval (слияние semantic+keyword) — Memory:Fusion:*
    private readonly MemoryFusionOptions _fusion;
    // Потолки памяти (не гейтятся флагом консолидации — жёсткая защита от разрастания)
    private readonly int _maxEntries;
    private readonly int _maxEpisodic;
    // Семантический дедуп на входе: порог близости и прирост важности при повторе (P1)
    private readonly double _dedupThreshold;
    private readonly double _dedupBoost;
    // Порог зоны конфликта: кандидаты в [ConflictThreshold, DedupThreshold) идут в LLM-резолвер (Memory #2)
    private readonly double _conflictThreshold;
    private readonly Dictionary<string, MemState> _store;
    private readonly Lock _saveLock = new();
    private readonly SemaphoreSlim _syncLock = new(1, 1);
    private readonly MemoryDifyDebouncer _debounce = new(SyncDebounce);

    public PersonaMemoryService(KnowledgeService knowledge, PersonaManager personas, UserStore users,
        IConfiguration config, ILogger<PersonaMemoryService> logger,
        TeamMemoryService? teamMemory = null, Memory.MemoryWriteResolver? resolver = null)
    {
        _knowledge = knowledge;
        _personas = personas;
        _users = users;
        _teamMemory = teamMemory;
        _resolver = resolver;
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
        _fusion = ReadFusion(config);
        _maxEntries = int.TryParse(config["Persona:MemoryMaxEntries"], out var me) && me > 0 ? me : 150;
        _maxEpisodic = int.TryParse(config["Persona:MemoryMaxEpisodic"], out var mep) && mep > 0 ? mep : 40;
        _dedupThreshold = ReadDouble(config, "Persona:DedupThreshold", 0.85);
        _dedupBoost = ReadDouble(config, "Persona:DedupSalienceBoost", 0.1);
        _conflictThreshold = ReadDouble(config, "Memory:ConflictThreshold", 0.6);
        _store = JsonFileStore.Load<Dictionary<string, MemState>>(_storePath, JsonOpts) ?? new();
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

    public bool Available => _knowledge.IsConfigured;

    // Записать факт/событие/приём в память персоны (явный write-path).
    // salience — значимость 0..1 (клампится в 0.05..1); null = 1.0
    public PersonaMemoryEntry? Remember(string ownerId, string personaId, PersonaMemoryType type,
        string text, List<string>? tags, string? sourceSessionId, double? salience = null, bool pending = false)
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
            Pending = pending,
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
        PersonaMemoryType type, string text, List<string>? tags, string? sourceSessionId,
        double? salience = null, bool pending = false)
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

        return Remember(ownerId, personaId, type, text, tags, sourceSessionId, salience, pending);
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

    // Авто-путь с разрешением ПРОТИВОРЕЧИЙ (Memory #2, Mem0 ADD/UPDATE/DELETE/NOOP). Дубль
    // (≥DedupThreshold) — как RememberAsync (reinforcement). Иначе близкие кандидаты в зоне конфликта
    // [ConflictThreshold, DedupThreshold) отдаются LLM-резолверу: UPDATE дополняет существующий,
    // DELETE вытесняет устаревший + добавляет новый, ADD кладёт рядом, NOOP отбрасывает. Гейтится
    // Enabled+Available; без резолвера/датасета/кандидатов — обычный RememberAsync. Ошибки → ADD.
    public async Task<PersonaMemoryEntry?> RememberWithResolutionAsync(string ownerId, string personaId,
        PersonaMemoryType type, string text, List<string>? tags, string? sourceSessionId,
        double? salience = null, bool pending = false)
    {
        // Резолвер выключен / Dify недоступен → обычный семантический write-path (простой дедуп)
        if (_resolver is not { Enabled: true } || !Available)
            return await RememberAsync(ownerId, personaId, type, text, tags, sourceSessionId, salience, pending);

        var persona = _personas.Get(personaId, ownerId);
        if (persona is null || string.IsNullOrWhiteSpace(text)) return null;
        var trimmed = text.Trim();

        var state = GetState(personaId);
        if (string.IsNullOrEmpty(state.DatasetId))   // датасета ещё нет — сопоставлять не с чем
            return await RememberAsync(ownerId, personaId, type, text, tags, sourceSessionId, salience, pending);

        try
        {
            var (dup, candidates) = await FindDuplicateAndCandidatesAsync(state, type, trimmed);
            if (dup is not null)   // явный дубль — усиливаем, резолвер не нужен
            {
                Reinforce(personaId, dup.Id, trimmed, salience);
                QueueSync(ownerId, personaId);
                return dup;
            }
            if (candidates.Count == 0)   // нет соседей в зоне конфликта — обычное добавление
                return Remember(ownerId, personaId, type, text, tags, sourceSessionId, salience, pending);

            var decision = await _resolver.ResolveAsync(trimmed, TypeLabel(type), candidates);
            switch (decision.Op)
            {
                case Memory.MemoryWriteOp.Noop:
                    return null;   // дубль/незначимо — ничего не добавляем
                case Memory.MemoryWriteOp.Update when !string.IsNullOrEmpty(decision.TargetId):
                    // Новый уточняет существующий → заменяем текст target на объединённую формулировку
                    return Update(ownerId, personaId, decision.TargetId, decision.MergedText!)
                        ?? Remember(ownerId, personaId, type, text, tags, sourceSessionId, salience, pending);
                case Memory.MemoryWriteOp.Delete when !string.IsNullOrEmpty(decision.TargetId):
                    // Новый делает существующий устаревшим → удаляем target, добавляем новый
                    Forget(ownerId, personaId, decision.TargetId);
                    return Remember(ownerId, personaId, type, text, tags, sourceSessionId, salience, pending);
                default:   // Add и невалидные Update/Delete
                    return Remember(ownerId, personaId, type, text, tags, sourceSessionId, salience, pending);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Разрешение записи памяти персоны {Persona}", personaId);
            return Remember(ownerId, personaId, type, text, tags, sourceSessionId, salience, pending);
        }
    }

    // Найти дубль (≥DedupThreshold) и близких кандидатов зоны конфликта [ConflictThreshold, DedupThreshold)
    // ТОГО ЖЕ типа (Dify retrieve). Дубль (наивысший скор) возвращается сразу с пустым списком кандидатов.
    private async Task<(PersonaMemoryEntry? Dup, List<Memory.MemoryWriteCandidate> Candidates)>
        FindDuplicateAndCandidatesAsync(MemState state, PersonaMemoryType type, string text)
    {
        var candidates = new List<Memory.MemoryWriteCandidate>();
        var chunks = await _knowledge.RetrieveAsync(state.DatasetId!, text, 8);
        if (chunks.Count == 0) return (null, candidates);

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
            if (ch.Score < _conflictThreshold) break;   // дальше только менее близкие — не интересны
            if (!byDocId.TryGetValue(ch.DocumentId, out var entryId)) continue;
            if (!byId.TryGetValue(entryId, out var e) || e.Type != type) continue;
            if (ch.Score >= _dedupThreshold) return (e, candidates);   // явный дубль
            candidates.Add(new Memory.MemoryWriteCandidate(e.Id, e.Text));
        }
        return (null, candidates);
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

    // Отредактировать текст записи вручную (UI-редактирование) — Dify-документ пересинхронизируется
    // по изменившемуся хешу через обычный QueueSync/SyncAsync.
    public PersonaMemoryEntry? Update(string ownerId, string personaId, string entryId, string text)
    {
        if (_personas.Get(personaId, ownerId) is null || string.IsNullOrWhiteSpace(text)) return null;
        var trimmed = text.Trim();
        var state = GetState(personaId);
        PersonaMemoryEntry? entry;
        lock (_saveLock)
        {
            entry = state.Entries.FirstOrDefault(e => e.Id == entryId);
            if (entry is null) return null;
            entry.Text = trimmed;
            JsonFileStore.Save(_storePath, _store, JsonOpts);
        }
        QueueSync(ownerId, personaId);
        return entry;
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

    // Подтвердить предложенную (pending) запись autolearn — снимает флаг, попадает в recall (③-3.2).
    // false — записи нет, она чужая или уже подтверждена.
    public bool Confirm(string ownerId, string personaId, string entryId)
    {
        if (_personas.Get(personaId, ownerId) is null) return false;
        var state = GetState(personaId);
        lock (_saveLock)
        {
            var e = state.Entries.FirstOrDefault(x => x.Id == entryId);
            if (e is null || !e.Pending) return false;
            e.Pending = false;
            JsonFileStore.Save(_storePath, _store, JsonOpts);
        }
        QueueSync(ownerId, personaId);
        return true;
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
        // Pending-записи (предложены autolearn, ждут подтверждения) в recall/поиск не попадают
        lock (_saveLock) entriesSnapshot = state.Entries.Where(e => !e.Pending).ToList();
        if (entriesSnapshot.Count == 0) return [];

        // relevance по entryId (0..1)
        Dictionary<string, double> relevance;
        if (Available && !string.IsNullOrEmpty(state.DatasetId))
        {
            // Гибридный retrieval: semantic (Dify) + keyword (полнотекст) сливаются в единый relevance.
            // Keyword добирает точные термины/идентификаторы, которые вектор пропускает (③-#3).
            var byDocId = state.Docs.ToDictionary(kv => kv.Value.DocId, kv => kv.Key);
            var chunks = await _knowledge.RetrieveAsync(state.DatasetId!, query, Math.Max(topK, 12));
            var semantic = new Dictionary<string, double>();
            foreach (var ch in chunks)
                if (byDocId.TryGetValue(ch.DocumentId, out var entryId))
                    semantic[entryId] = Math.Max(semantic.GetValueOrDefault(entryId), ch.Score);
            // Keyword-сигнал считаем по всему скоупу (а не только по кандидатам Dify): дёшево
            // (in-memory подстроки по ≤150 записям) и позволяет всплыть точному совпадению, которое
            // в топ-чанки Dify не попало вовсе.
            var keyword = MemoryFulltext.Relevance(entriesSnapshot, query,
                e => e.Id, e => e.Text, e => e.Tags);
            relevance = MemoryRetrievalFusion.Fuse(semantic, keyword, _fusion);
        }
        else
        {
            relevance = MemoryFulltext.Relevance(entriesSnapshot, query,
                e => e.Id, e => e.Text, e => e.Tags);
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

    // Результат recall памяти: markdown-блок для промпта + hits, реально попавшие в блок
    // (для манифеста атрибуции F3 — «персона опирается на…»). Text=null — нечего подмешивать.
    // TeamHits — записи памяти команды проекта (③-3.4), попавшие в тот же блок, отдельно от
    // личных hits: это чужой тип данных (TeamMemoryEntry, не PersonaMemoryHit), без scoring.
    public sealed record PersonaRecallResult(string? Text, IReadOnlyList<PersonaMemoryHit> Hits,
        IReadOnlyList<TeamMemoryEntry> TeamHits);

    // Markdown-блок памяти для системного промпта хода (auto-recall персоны).
    // Рабочий фокус (если есть) — всегда первым блоком, без скоринга; при фокусе
    // результат не-null даже без хитов. null — персона не найдена / память выключена;
    // Text=null — память включена, но подмешивать нечего.
    public async Task<PersonaRecallResult?> BuildRecallAsync(string ownerId, string personaId, string query,
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

        // Память команды проекта (③-3.4): проектная персона recall'ит общую память команды
        string? teamBlock = null;
        IReadOnlyList<TeamMemoryEntry> teamHits = [];
        if (_teamMemory is not null && persona.Scope == PersonaScope.Project && !string.IsNullOrEmpty(persona.ProjectId))
        {
            try
            {
                var teamRecall = await _teamMemory.BuildRecallBlockAsync(ownerId, persona.ProjectId!, query);
                teamBlock = teamRecall.Text;
                teamHits = teamRecall.Used;
            }
            catch (Exception ex) { _logger.LogDebug(ex, "team-memory recall {Project}", persona.ProjectId); }
        }

        if (top.Count == 0 && focus is null && teamBlock is null)
            return new PersonaRecallResult(null, [], []);

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
        if (teamBlock is not null)
        {
            if (sb.Length > 0) sb.AppendLine();
            sb.Append(teamBlock);
        }
        return new PersonaRecallResult(sb.Length > 0 ? sb.ToString() : null, top, teamHits);
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

    // --- Синхронизация с Dify (дифф по хешам, дебаунс) ---

    private void QueueSync(string ownerId, string personaId)
    {
        if (!Available) return;
        _debounce.Schedule(personaId, () => RunSyncSafe(ownerId, personaId));
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
            Dictionary<string, MemoryDocRef> docsSnapshot;
            lock (_saveLock)
            {
                entries = state.Entries.ToList();
                docsSnapshot = new Dictionary<string, MemoryDocRef>(state.Docs);
            }

            // Дифф-синк — общее ядро MemoryDify; связка со стором (мутации Docs под _saveLock) тонкая
            var items = entries
                .Select(e => new MemorySyncItem(e.Id,
                    $"{e.Type}\n{e.Text}\n{string.Join(',', e.Tags ?? [])}",
                    $"{TypeLabel(e.Type)}-{e.Id}", e.Text, e.Tags))
                .ToList();

            var changed = await MemoryDify.DiffSyncAsync(_knowledge, state.DatasetId!, items, docsSnapshot,
                (id, doc) => { lock (_saveLock) state.Docs[id] = doc; },
                id => { lock (_saveLock) state.Docs.Remove(id); },
                _logger);

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
}

// Результат поиска по памяти персоны
public record PersonaMemoryHit(
    string Id, PersonaMemoryType Type, string Text, IReadOnlyList<string> Tags, double Score, DateTime CreatedAt);

// Операция консолидации памяти (P4): merge — схлопнуть несколько записей одного типа
// в одну сводную (Ids → новая запись Text/Type/Salience); drop — удалить запись Id.
public sealed record MemoryConsolidationOp(
    string Op, List<string>? Ids, string? Id, PersonaMemoryType? Type, string? Text, double? Salience)
    : IMemoryConsolidationOp<PersonaMemoryType>
{
    public bool IsMerge => string.Equals(Op, "merge", StringComparison.OrdinalIgnoreCase);
    public bool IsDrop => string.Equals(Op, "drop", StringComparison.OrdinalIgnoreCase);
}
