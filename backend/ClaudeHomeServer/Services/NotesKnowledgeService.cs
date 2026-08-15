using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using ClaudeHomeServer.Models;

namespace ClaudeHomeServer.Services;

// Семантический индекс заметок в Dify: отдельный dataset per-owner
// («{username}:notes», permission only_me). Синхронизация — полный дифф по хешам
// содержимого (устойчив к переименованиям/массовым правкам), с дебаунсом на мутации.
// Без настроенного Dify всё тихо выключено (graceful degradation).
public sealed class NotesKnowledgeService : Knowledge.IKnowledgeSyncParticipant
{
    // userId → { datasetId, noteId → { difyDocId, contentHash } }
    private sealed class Entry
    {
        public string? DatasetId { get; set; }
        public Dictionary<string, DocRef> Docs { get; set; } = new();
    }
    private sealed class DocRef
    {
        public string DocId { get; set; } = "";
        public string Hash { get; set; } = "";
    }

    private static readonly TimeSpan SyncDebounce = TimeSpan.FromSeconds(15);

    private readonly KnowledgeService _knowledge;
    private readonly NotesService _notes;
    private readonly UserStore _users;
    private readonly ILogger<NotesKnowledgeService> _logger;
    private readonly string _storePath;
    private readonly Dictionary<string, Entry> _store;
    private readonly Lock _saveLock = new();
    private readonly SemaphoreSlim _syncLock = new(1, 1);
    private readonly ConcurrentDictionary<string, Timer> _debounce = new();

    public NotesKnowledgeService(KnowledgeService knowledge, NotesService notes, UserStore users,
        IConfiguration config, ILogger<NotesKnowledgeService> logger)
    {
        _knowledge = knowledge;
        _notes = notes;
        _users = users;
        _logger = logger;
        var dataDir = Path.GetDirectoryName(
            config["DataPath"] ?? Path.Combine(AppContext.BaseDirectory, "data", "projects.json"))!;
        _storePath = Path.Combine(dataDir, "notes-knowledge.json");
        _store = JsonFileStore.Load<Dictionary<string, Entry>>(_storePath) ?? new();
    }

    public bool Available => _knowledge.IsConfigured;

    // Есть ли у пользователя хоть один проиндексированный документ (без похода в Dify —
    // дешёвая отсечка для auto-recall на пустом индексе)
    public bool HasIndex(string userId)
    {
        var entry = GetEntry(userId);
        return !string.IsNullOrEmpty(entry.DatasetId) && entry.Docs.Count > 0;
    }

    // Id Dify-датасета заметок владельца («{username}:notes»); null — индекс ещё не создавался.
    // Нужен привязкам персон (PersonaBindingsService): датасет заметок — валидная цель Knowledge.
    public string? GetDatasetId(string userId) => GetEntry(userId).DatasetId;

    // Markdown-блок с релевантными заметками для системного промпта хода (auto-recall).
    // Пустой список / все ниже порога → null (нечего подмешивать).
    internal static string? BuildRecallBlock(IReadOnlyList<NoteSemanticHit> hits, double minScore, int topK)
    {
        var top = hits.Where(h => h.Score >= minScore).Take(topK).ToList();
        if (top.Count == 0) return null;
        var sb = new StringBuilder();
        sb.AppendLine("Возможно релевантные заметки из базы знаний пользователя " +
                      "(авто-подбор по смыслу, может быть мимо — используй только если помогает):");
        foreach (var h in top)
            sb.AppendLine($"- [[{h.Title}]] ({h.SourceLabel}, id: {h.Id}) — {h.Snippet}");
        sb.AppendLine("Полный текст нужной заметки — через mcp__notes__notes_read по её id.");
        return sb.ToString();
    }

    // Отложенная синхронизация после мутации заметок (дебаунс — частые правки не спамят Dify)
    public void QueueSync(string userId)
    {
        if (!Available) return;
        _debounce.AddOrUpdate(userId,
            _ => new Timer(_ => RunSyncSafe(userId), null, SyncDebounce, Timeout.InfiniteTimeSpan),
            (_, timer) => { timer.Change(SyncDebounce, Timeout.InfiniteTimeSpan); return timer; });
    }

    private void RunSyncSafe(string userId) =>
        _ = Task.Run(async () =>
        {
            try { await SyncAllAsync(userId); }
            catch (Exception ex) { _logger.LogWarning(ex, "Синхронизация заметок в Dify для {User}", userId); }
        });

    // Полная дифф-синхронизация заметок пользователя с его dataset
    public async Task<int> SyncAllAsync(string userId)
    {
        if (!Available) return 0;
        await _syncLock.WaitAsync();
        try
        {
            var entry = GetEntry(userId);
            if (string.IsNullOrEmpty(entry.DatasetId))
            {
                var username = _users.GetById(userId)?.Username ?? userId;
                entry.DatasetId = await _knowledge.CreateDatasetAsync($"{username}:notes");
                Save();
            }

            // Комментарии к документам в семантический индекс не попадают (шум: цитаты
            // дублируют сами документы); исчезнувшие из выборки чистятся штатной веткой ниже.
            var summaries = _notes.GetSummaries(userId, null, null)
                .Where(s => s.Annotation is null).ToList();
            var alive = new HashSet<string>(summaries.Select(s => s.Id));
            var changed = 0;

            foreach (var s in summaries)
            {
                var detail = _notes.GetDetail(userId, s.Id);
                if (detail is null) continue;
                var hash = Hash($"{detail.Title}\n{detail.Content}\n{string.Join(',', detail.Tags)}");
                if (entry.Docs.TryGetValue(s.Id, out var doc) && doc.Hash == hash) continue;

                // Пересоздание документа (update_by_text у Dify капризен к типам — проще delete+create)
                if (doc is not null)
                    try { await _knowledge.DeleteDocumentAsync(entry.DatasetId!, doc.DocId); }
                    catch (Exception ex) { _logger.LogDebug(ex, "Удаление старого документа {Note}", s.Id); }

                var info = await _knowledge.IndexFileByTextAsync(
                    entry.DatasetId!, detail.Title, detail.Content, detail.Tags.ToList());
                entry.Docs[s.Id] = new DocRef { DocId = info.Id, Hash = hash };
                changed++;
            }

            // Удалённые заметки — из индекса
            foreach (var stale in entry.Docs.Keys.Where(k => !alive.Contains(k)).ToList())
            {
                try { await _knowledge.DeleteDocumentAsync(entry.DatasetId!, entry.Docs[stale].DocId); }
                catch (Exception ex) { _logger.LogDebug(ex, "Удаление документа исчезнувшей заметки"); }
                entry.Docs.Remove(stale);
                changed++;
            }

            if (changed > 0) Save();
            return changed;
        }
        finally { _syncLock.Release(); }
    }

    // Семантический поиск: чанки Dify → заметки пользователя (по маппингу docId → noteId)
    public async Task<IReadOnlyList<NoteSemanticHit>> SearchAsync(string userId, string query, int topK = 8)
    {
        if (!Available) return [];
        var entry = GetEntry(userId);
        if (string.IsNullOrEmpty(entry.DatasetId)) return [];

        var byDocId = entry.Docs.ToDictionary(kv => kv.Value.DocId, kv => kv.Key);
        var chunks = await _knowledge.RetrieveAsync(entry.DatasetId!, query, topK);
        var summaries = _notes.GetSummaries(userId, null, null).ToDictionary(s => s.Id);

        var hits = new List<NoteSemanticHit>();
        var seen = new HashSet<string>();
        foreach (var ch in chunks)
        {
            if (!byDocId.TryGetValue(ch.DocumentId, out var noteId)) continue;
            if (!summaries.TryGetValue(noteId, out var note)) continue;
            if (!seen.Add(noteId)) continue;   // одна заметка — лучший чанк
            var snippet = ch.Content.Length > 220 ? ch.Content[..217] + "…" : ch.Content;
            hits.Add(new NoteSemanticHit(noteId, note.Title, note.Source, note.SourceLabel,
                Math.Round(ch.Score, 3), snippet.Replace('\n', ' ')));
        }
        return hits;
    }

    // --- Участник реконсайлера error-документов (Knowledge.IKnowledgeSyncParticipant) ---
    // Цель на каждого пользователя с созданным датасетом заметок; ключ записи — noteId.
    // Мутации entry.Docs в SyncAllAsync идут под _syncLock — берём его же, внутри _saveLock.
    public IReadOnlyList<Knowledge.KnowledgeSyncTarget> ListTargets()
    {
        List<(string UserId, string DatasetId)> snapshot;
        lock (_saveLock)
            snapshot = _store
                .Where(kv => !string.IsNullOrEmpty(kv.Value.DatasetId))
                .Select(kv => (kv.Key, kv.Value.DatasetId!))
                .ToList();

        return snapshot
            .Select(t => new Knowledge.KnowledgeSyncTarget(
                t.DatasetId, [t.UserId], $"notes:{t.UserId}",
                docIds => ResolveDocsAsync(t.UserId, docIds),
                keys => InvalidateDocsAsync(t.UserId, keys),
                () => QueueSync(t.UserId)))
            .ToList();
    }

    private async Task<IReadOnlyList<(string DocId, string EntryKey)>> ResolveDocsAsync(
        string userId, IReadOnlyCollection<string> docIds)
    {
        await _syncLock.WaitAsync();
        try
        {
            lock (_saveLock)
            {
                if (!_store.TryGetValue(userId, out var entry)) return [];
                var byDocId = entry.Docs.ToDictionary(kv => kv.Value.DocId, kv => kv.Key);
                return docIds
                    .Where(byDocId.ContainsKey)
                    .Select(d => (d, byDocId[d]))
                    .ToList();
            }
        }
        finally { _syncLock.Release(); }
    }

    private async Task InvalidateDocsAsync(string userId, IReadOnlyCollection<string> entryKeys)
    {
        await _syncLock.WaitAsync();
        try
        {
            lock (_saveLock)
            {
                if (!_store.TryGetValue(userId, out var entry)) return;
                var changed = false;
                foreach (var key in entryKeys)
                    if (entry.Docs.TryGetValue(key, out var doc) && doc.Hash.Length > 0)
                    {
                        // Замена объекта, не правка поля — на случай общих снапшотов
                        entry.Docs[key] = new DocRef { DocId = doc.DocId, Hash = "" };
                        changed = true;
                    }
                if (changed) JsonFileStore.Save(_storePath, _store);
            }
        }
        finally { _syncLock.Release(); }
    }

    // Уборка локальной записи индекса заметок пользователя — каскад удаления пользователя.
    // Dify-датасет удаляет вызывающий (общий проход по префиксу имени датасета).
    public void DeleteUser(string userId)
    {
        lock (_saveLock)
        {
            if (_store.Remove(userId)) JsonFileStore.Save(_storePath, _store);
        }
    }

    private Entry GetEntry(string userId)
    {
        lock (_saveLock)
        {
            if (!_store.TryGetValue(userId, out var e)) _store[userId] = e = new Entry();
            return e;
        }
    }

    private void Save()
    {
        lock (_saveLock) JsonFileStore.Save(_storePath, _store);
    }

    private static string Hash(string s) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s)));
}

// Результат семантического поиска по заметкам
public record NoteSemanticHit(
    string Id, string Title, string Source, string SourceLabel, double Score, string Snippet);
