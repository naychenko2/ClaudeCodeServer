using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.Memory;
using Microsoft.Extensions.Configuration;

namespace ClaudeHomeServer.Services.Dossiers;

// Хранилище паспортов изменений (ADR-004 §4): source of truth — JSON-стор
// data/dossiers/{owner}/{project}.json (по образцу team-memory.json, но с разбивкой по
// владельцу и проекту — растёт линейно с коммитами). Поверх — Dify-датасет
// «{username}:dossiers:{projectName}» (дифф по хешам, дебаунс) для будущего семантического
// recall (этап 2); без Dify — REST-фильтры (Find) работают полнотекстово прямо по стору,
// graceful degradation не требуется отдельно. Эталон структуры — TeamMemoryService.
public sealed class DossierStore
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

    public ChangeDossier Add(ChangeDossier dossier)
    {
        lock (_saveLock)
        {
            var list = Get(dossier.OwnerId, dossier.ProjectId);
            list.Add(dossier);
            EvictIfNeeded(dossier.OwnerId, dossier.ProjectId, list);
            Save(dossier.OwnerId, dossier.ProjectId);
        }
        QueueSync(dossier.OwnerId, dossier.ProjectId);
        return dossier;
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
