using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.Memory;

namespace ClaudeHomeServer.Services.Dossiers;

// Хранилище паспортов изменений (ADR-004 §4): source of truth — JSON-стор
// data/dossiers/{owner}/{project}.json (по образцу team-memory.json, но с разбивкой по
// владельцу и проекту — растёт линейно с коммитами). Поверх — Dify-датасет
// «{username}:dossiers:{projectName}» (дифф по хешам, дебаунс) для будущего семантического
// recall (этап 2); без Dify — REST-фильтры (Find) работают полнотекстово прямо по стору,
// graceful degradation не требуется отдельно. Эталон структуры — TeamMemoryService.
public sealed class DossierStore : Knowledge.IKnowledgeSyncParticipant
{
    private sealed class KnowledgeState
    {
        public string? DatasetId { get; set; }
        public Dictionary<string, MemoryDocRef> Docs { get; set; } = new();
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };
    private static readonly TimeSpan SyncDebounce = TimeSpan.FromSeconds(15);

    // Триггер перехода на SQLite (ADR-004 §4) — измеримый, не «когда-нибудь»: файл проекта
    // перешёл за 15 МБ ИЛИ записей больше 3000. При подходе к порогу стор пишет warning в лог.
    private const long MigrationSizeThreshold = 15 * 1024 * 1024;
    private const int MigrationRecordThreshold = 3000;

    private readonly ConcurrentDictionary<string, List<ChangeDossier>> _store = new();
    private readonly string _dataDir;
    private readonly Lock _saveLock = new();
    private readonly ILogger<DossierStore>? _log;

    private readonly KnowledgeService? _knowledge;
    private readonly UserStore? _users;
    private readonly ProjectManager? _projects;

    private readonly string _knowledgeStorePath;
    private readonly Dictionary<string, KnowledgeState> _kStore;
    private readonly Lock _kLock = new();
    private readonly SemaphoreSlim _syncLock = new(1, 1);
    private readonly MemoryDifyDebouncer _debounce = new(SyncDebounce);

    private readonly int _maxEntries;

    // Проекты, для которых уже залогировано приближение к порогу миграции — чтобы не спамить
    // warning на каждый коммит. In-memory: при рестарте предупредит повторно (приемлемо — раз).
    private readonly HashSet<string> _migrationWarned = [];

    public DossierStore(IConfiguration config, ILogger<DossierStore>? log = null,
        KnowledgeService? knowledge = null, UserStore? users = null, ProjectManager? projects = null)
    {
        _log = log;
        _knowledge = knowledge;
        _users = users;
        _projects = projects;
        var dataRoot = Path.GetDirectoryName(Path.GetFullPath(
            config["DataPath"] ?? Path.Combine(AppContext.BaseDirectory, "data", "projects.json")))!;
        _dataDir = Path.Combine(dataRoot, "dossiers");
        _knowledgeStorePath = Path.Combine(_dataDir, "knowledge.json");
        // Дефолт 5000 (замер прода 2026-08-08: ~5.2 КБ на запись → 5000 ≈ 26 МБ). Это верхняя
        // граница терпимого для whole-file JSON: Save() переписывает файл ЦЕЛИКОМ на каждый
        // коммит, плюс список держится в памяти, плюс data/ целиком едет в бэкап. Крутить потолок
        // вверх — не выход: вытесненные в архивный JSONL записи в поиске и recall не участвуют,
        // ценность фичи они всё равно теряют. Решение объёма — фильтр захвата (§2) либо SQLite
        // (триггер — константы ниже, ADR-004 §4). Ранняя оценка «1.5 КБ на паспорт» была в 3.5×
        // занижена против факта — отсюда и прежний «запас на годы» не держался.
        _maxEntries = int.TryParse(config["Dossiers:MaxEntries"], out var me) && me > 0 ? me : 5000;
        _kStore = JsonFileStore.Load<Dictionary<string, KnowledgeState>>(_knowledgeStorePath, JsonOpts) ?? new();
    }

    public bool Available => _knowledge?.IsConfigured == true;
    public int MaxEntries => _maxEntries;

    public IReadOnlyList<ChangeDossier> List(string ownerId, string projectId) => Snapshot(ownerId, projectId);

    // Полнотекстовый фильтр по якорям — используется REST-эндпоинтом; независим от Dify.
    public IReadOnlyList<ChangeDossier> Find(string ownerId, string projectId,
        string? file = null, string? symbol = null, string? commit = null)
    {
        IEnumerable<ChangeDossier> list = Snapshot(ownerId, projectId);
        if (!string.IsNullOrWhiteSpace(commit))
            list = list.Where(d => string.Equals(d.CommitSha, commit, StringComparison.OrdinalIgnoreCase)
                || d.SupersededSha.Contains(commit, StringComparer.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(file))
            list = list.Where(d => d.Files.Any(f => string.Equals(f, file, StringComparison.OrdinalIgnoreCase)));
        if (!string.IsNullOrWhiteSpace(symbol))
            list = list.Where(d => d.Symbols.Any(s => string.Equals(s, symbol, StringComparison.OrdinalIgnoreCase)));
        return list.OrderByDescending(d => d.CommittedAt).ToList();
    }

    // Дедуп/идемпотентность (§4, §7): коммит уже представлен записью — текущим sha либо
    // одним из supersededSha (после squash старый sha остаётся «известным» этим списком).
    public ChangeDossier? FindByAnyCommitSha(string ownerId, string projectId, string sha) =>
        Snapshot(ownerId, projectId).FirstOrDefault(d =>
            string.Equals(d.CommitSha, sha, StringComparison.OrdinalIgnoreCase)
            || d.SupersededSha.Contains(sha, StringComparer.OrdinalIgnoreCase));

    // Запись по id (MCP dossier_get / REST): только свой владелец и свой проект — изоляция та же,
    public ChangeDossier? GetById(string ownerId, string projectId, string id) =>
        Snapshot(ownerId, projectId).FirstOrDefault(d => d.Id == id);

    // Персист статусов после ленивого пересчёта устаревания (этап 2): вызывающий уже смутировал
    // Status у объектов из List/Snapshot (те же ссылки живут в _store). Хеш Dify-документа
    // (HashSource) статус не включает — пересинк семантического слоя не провоцируется.
    public void PersistStatuses(string ownerId, string projectId)
    {
        lock (_saveLock) Save(ownerId, projectId);
    }

    public ChangeDossier Add(ChangeDossier dossier)
    {
        lock (_saveLock)
        {
            var list = Get(dossier.OwnerId, dossier.ProjectId);
            list.Add(dossier);
            EvictIfNeeded(dossier.OwnerId, dossier.ProjectId, list);
            Save(dossier.OwnerId, dossier.ProjectId);
            WarnIfApproachingMigration(dossier.OwnerId, dossier.ProjectId, list);
        }
        QueueSync(dossier.OwnerId, dossier.ProjectId);
        return dossier;
    }

    // Импорт партии из ветки ccs/dossiers/v1 (этап 4, ADR-004 §6): записи добавляются
    // с ключевым правилом коллизий — СВОЙ паспорт по тому же commitSha не перезаписывается
    // никогда, импортированный живёт рядом отдельной записью; уже существующий импортированный
    // с тем же sha (включая SupersededSha — переякоренный после squash) — дубль и пропускается,
    // поэтому повторный импорт того же состояния ветки — no-op: существующие записи не меняются.
    // Дедуп — под _saveLock (гоны с Add одного и того же списка), один Save на партию.
    // Возвращает число реально добавленных записей.
    public int AddImportedRange(string ownerId, string projectId, IEnumerable<ChangeDossier> imported)
    {
        var added = 0;
        lock (_saveLock)
        {
            var list = Get(ownerId, projectId);

            // Представленные импортированными записями sha: их CommitSha + SupersededSha
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var d in list.Where(d => d.Origin == DossierOrigin.Imported))
            {
                seen.Add(d.CommitSha);
                foreach (var s in d.SupersededSha) seen.Add(s);
            }

            foreach (var d in imported)
            {
                // Вторая проверка — от дублей внутри самой партии (index с одним sha дважды)
                if (!seen.Add(d.CommitSha)) continue;
                list.Add(d);
                added++;
            }

            if (added > 0)
            {
                EvictIfNeeded(ownerId, projectId, list);
                Save(ownerId, projectId);
                WarnIfApproachingMigration(ownerId, projectId, list);
            }
        }
        if (added > 0) QueueSync(ownerId, projectId);
        return added;
    }

    // Предупреждение о приближении к порогу миграции на SQLite — once per project: размер файла
    // после Save (только что записан) ИЛИ число записей. Архивный JSONL ценности не вернёт, рост
    // потолка проблему не решает — пишем это в лог, чтобы узнать о необходимости переезда до
    // того, как whole-file Save() и бэкап data/ станут ощутимо тяжёлыми (ADR-004 §4).
    private void WarnIfApproachingMigration(string ownerId, string projectId, List<ChangeDossier> list)
    {
        var key = Key(ownerId, projectId);
        if (_migrationWarned.Contains(key)) return;

        long size;
        try { size = new FileInfo(StorePath(ownerId, projectId)).Length; }
        catch { size = 0; }

        if (list.Count > MigrationRecordThreshold || size > MigrationSizeThreshold)
        {
            _migrationWarned.Add(key);
            _log?.LogWarning(
                "dossiers: стор проекта {Project} приближается к порогу миграции на SQLite: " +
                "{Count} записей, {SizeMB:F1} МБ (порог >{Recs} записей или >{SizeMB:F0} МБ, ADR-004 §4). " +
                "Архивный JSONL в поиске и recall не участвует — рост потолка ценности не вернёт, нужен переход модели хранения.",
                projectId, list.Count, size / 1048576.0, MigrationRecordThreshold, MigrationSizeThreshold / 1048576.0);
        }
    }

    // Переякорение при squash (§7): вызывающий уже смутировал поля объекта, полученного
    // из List/Snapshot (та же ссылка живёт в _store — Snapshot копирует список, не элементы) —
    // здесь только персист + постановка в очередь Dify-синка.
    public void Reanchor(ChangeDossier dossier)
    {
        lock (_saveLock) Save(dossier.OwnerId, dossier.ProjectId);
        QueueSync(dossier.OwnerId, dossier.ProjectId);
    }

    public async Task DeleteProjectDossiersAsync(string ownerId, string projectId)
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
            try { File.Delete(StorePath(ownerId, projectId)); } catch { /* уборка best-effort */ }
            try { File.Delete(ArchivePath(ownerId, projectId)); } catch { /* уборка best-effort */ }
        }
        if (!string.IsNullOrEmpty(datasetId) && _knowledge?.IsConfigured == true)
        {
            try { await _knowledge.DeleteDatasetAsync(datasetId); }
            catch (Exception ex) { _log?.LogWarning(ex, "dossiers: не удалить Dify-датасет проекта {Project}", projectId); }
        }
    }

    public void DeleteOwnerDossiers(string ownerId)
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
        }
        var ownerDir = Path.Combine(_dataDir, ownerId);
        try { if (Directory.Exists(ownerDir)) Directory.Delete(ownerDir, recursive: true); }
        catch { /* уборка best-effort */ }
    }

    // --- Участник реконсайлера error-документов (Knowledge.IKnowledgeSyncParticipant) ---
    // Цель на каждый scope «owner:project» с созданным датасетом; ключ записи — id паспорта.
    // Карту Docs защищает _kLock (не _saveLock); порядок _syncLock → _kLock.
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
            var projectId = key[(idx + 1)..];
            targets.Add(new Knowledge.KnowledgeSyncTarget(
                datasetId, [ownerId], $"dossiers:{key}",
                docIds => ResolveDocsAsync(key, docIds),
                keys => InvalidateDocsAsync(key, keys),
                () => QueueSync(ownerId, projectId)));
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

    // --- Синхронизация с Dify (дифф по хешам, дебаунс) — эталон TeamMemoryService ---

    private void QueueSync(string ownerId, string projectId)
    {
        if (!Available) return;
        _debounce.Schedule(Key(ownerId, projectId), () => RunSyncSafe(ownerId, projectId));
    }

    private void RunSyncSafe(string ownerId, string projectId) =>
        _ = Task.Run(async () =>
        {
            try { await SyncAsync(ownerId, projectId); }
            catch (Exception ex) { _log?.LogWarning(ex, "dossiers: синхронизация {Project} в Dify", projectId); }
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
                var datasetId = await _knowledge!.CreateDatasetAsync($"{username}:dossiers:{projectName}");
                lock (_kLock) { state.DatasetId = datasetId; SaveKnowledge(); }
            }

            // Скелеты (SummaryFailed) в семантический слой не идут — нет содержательной выжимки
            var entries = Snapshot(ownerId, projectId).Where(d => !d.SummaryFailed).ToList();
            Dictionary<string, MemoryDocRef> docsSnapshot;
            lock (_kLock) docsSnapshot = new Dictionary<string, MemoryDocRef>(state.Docs);

            var items = entries
                .Select(d => new MemorySyncItem(d.Id, HashSource(d),
                    $"dossier-{ShortSha(d.CommitSha)}", FormatText(d), BuildTags(d)))
                .ToList();

            var changed = await MemoryDify.DiffSyncAsync(_knowledge!, state.DatasetId!, items, docsSnapshot,
                (id, doc) => { lock (_kLock) state.Docs[id] = doc; },
                id => { lock (_kLock) state.Docs.Remove(id); },
                _log);

            if (changed > 0)
            {
                lock (_kLock) SaveKnowledge();
                lock (_saveLock)
                {
                    var list = Get(ownerId, projectId);
                    Dictionary<string, MemoryDocRef> docsNow;
                    lock (_kLock) docsNow = new Dictionary<string, MemoryDocRef>(state.Docs);
                    foreach (var d in list)
                        if (docsNow.TryGetValue(d.Id, out var doc)) d.DifyDocId = doc.DocId;
                    Save(ownerId, projectId);
                }
            }
            return changed;
        }
        finally { _syncLock.Release(); }
    }

    private static string ShortSha(string sha) => sha.Length <= 7 ? sha : sha[..7];

    private static string HashSource(ChangeDossier d) => string.Join('\n',
        d.CommitSha, d.Why, string.Join(',', d.Decisions), string.Join(',', d.Rejected),
        string.Join(',', d.Pitfalls), string.Join(',', d.Invariants),
        string.Join(',', d.Files), string.Join(',', d.Symbols));

    private static string FormatText(ChangeDossier d)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Коммит {ShortSha(d.CommitSha)}: {d.CommitSubject}");
        if (!string.IsNullOrWhiteSpace(d.Why)) sb.AppendLine("Зачем: " + d.Why);
        if (d.Decisions.Count > 0) sb.AppendLine("Решения: " + string.Join("; ", d.Decisions));
        if (d.Rejected.Count > 0) sb.AppendLine("Отвергнуто: " + string.Join("; ", d.Rejected));
        if (d.Pitfalls.Count > 0) sb.AppendLine("Грабли: " + string.Join("; ", d.Pitfalls));
        if (d.Invariants.Count > 0) sb.AppendLine("Инварианты: " + string.Join("; ", d.Invariants));
        return sb.ToString();
    }

    // Файлы+символы+sha как теги — по ним фильтрует knowledge_search (метаданные полного
    // набора files/symbols/commit/task_id/persona/session/date из ADR — точка пересмотра
    // этапа 2 recall, когда понадобится фильтр не только по идентификатору, но и по датам)
    private static List<string> BuildTags(ChangeDossier d) => [.. d.Files, .. d.Symbols, d.CommitSha];

    // --- Внутреннее хранилище ---

    private string StorePath(string ownerId, string projectId) =>
        Path.Combine(_dataDir, ownerId, projectId + ".json");

    private string ArchivePath(string ownerId, string projectId) =>
        Path.Combine(_dataDir, ownerId, projectId + ".archive.jsonl");

    private static string Key(string ownerId, string projectId) => $"{ownerId}:{projectId}";

    private List<ChangeDossier> Get(string ownerId, string projectId) =>
        _store.GetOrAdd(Key(ownerId, projectId),
            _ => JsonFileStore.Load<List<ChangeDossier>>(StorePath(ownerId, projectId), JsonOpts, _log) ?? []);

    private List<ChangeDossier> Snapshot(string ownerId, string projectId)
    {
        lock (_saveLock) return Get(ownerId, projectId).ToList();
    }

    private void Save(string ownerId, string projectId) =>
        JsonFileStore.Save(StorePath(ownerId, projectId), Get(ownerId, projectId), JsonOpts);

    // Потолок Dossiers:MaxEntries — вытесняем самые старые (по CommittedAt) в архивный JSONL.
    // Вызывается под _saveLock.
    private void EvictIfNeeded(string ownerId, string projectId, List<ChangeDossier> list)
    {
        if (list.Count <= _maxEntries) return;
        list.Sort((a, b) => a.CommittedAt.CompareTo(b.CommittedAt));
        var toEvict = list.Count - _maxEntries;
        var evicted = list.GetRange(0, toEvict);
        list.RemoveRange(0, toEvict);
        AppendArchive(ownerId, projectId, evicted);
    }

    private void AppendArchive(string ownerId, string projectId, IEnumerable<ChangeDossier> items)
    {
        var path = ArchivePath(ownerId, projectId);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var writer = new StreamWriter(path, append: true, Encoding.UTF8);
        foreach (var d in items)
            writer.WriteLine(JsonSerializer.Serialize(d, JsonOpts));
    }

    private KnowledgeState GetKnowledgeState(string ownerId, string projectId)
    {
        var key = Key(ownerId, projectId);
        lock (_kLock)
        {
            if (!_kStore.TryGetValue(key, out var s)) _kStore[key] = s = new KnowledgeState();
            return s;
        }
    }

    private void SaveKnowledge() => JsonFileStore.Save(_knowledgeStorePath, _kStore, JsonOpts);
}
