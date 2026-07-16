using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using ClaudeHomeServer.Hubs;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;
using Microsoft.AspNetCore.SignalR;

namespace ClaudeHomeServer.Services;

// Синхронизация «файл проекта ↔ документ базы знаний Dify»: файл, добавленный в БЗ,
// отслеживается по карте WorkspaceKnowledge.Docs (relativePath → {DocId, Hash}) — правка
// переиндексирует документ (delete+create, update_by_text у Dify капризен), удаление файла
// удаляет документ, перенос/переименование мигрирует его под новый путь. Триггеры: мутации
// файлового API (FileService.OnMutated), ватчеры (FileWatcherService, события хода Claude —
// ProjectKnowledgeTurnSync) и сверка при открытии панели БЗ. Дебаунс — как у заметок
// (NotesKnowledgeService). Без настроенного Dify всё тихо выключено; ошибки Dify best-effort:
// логируются и не роняют файловые операции.
public sealed class ProjectKnowledgeSyncService
{
    private static readonly TimeSpan SyncDebounce = TimeSpan.FromSeconds(15);

    private readonly KnowledgeService _knowledge;
    private readonly WorkspaceKnowledgeStore _wkStore;
    private readonly ProjectManager _projects;
    private readonly FileService _files;
    private readonly IHubContext<SessionHub> _hub;
    private readonly ILogger<ProjectKnowledgeSyncService> _logger;

    private sealed class Pending
    {
        public Timer? Timer;
        public readonly HashSet<string> Hints = new(StringComparer.OrdinalIgnoreCase);
    }

    // Нормализованный rootPath → отложенный синк + пути-хинты изменений (для детекта переноса)
    private readonly ConcurrentDictionary<string, Pending> _debounce = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _syncLock = new(1, 1);

    public ProjectKnowledgeSyncService(KnowledgeService knowledge, WorkspaceKnowledgeStore wkStore,
        ProjectManager projects, FileService files, IHubContext<SessionHub> hub,
        ILogger<ProjectKnowledgeSyncService> logger)
    {
        _knowledge = knowledge;
        _wkStore = wkStore;
        _projects = projects;
        _files = files;
        _hub = hub;
        _logger = logger;
        // Мутации через файловый API (UI, OnlyOffice, upload): правка/создание/удаление —
        // отложенный синк; перенос — миграция ключей карты
        _files.OnMutated += HandleFileMutation;
    }

    private void HandleFileMutation(string root, string rel, FileMutationKind kind, string? newRel)
    {
        if (kind == FileMutationKind.Rename && newRel is not null) HandleRename(root, rel, newRel);
        else QueueSync(root, [rel]);
    }

    // Отложенная синхронизация после изменения файлов (дебаунс — частые правки не спамят Dify).
    // changedPaths — подсказки «что менялось»: по ним детектится перенос файла вне файлового API.
    public void QueueSync(string rootPath, IEnumerable<string>? changedPaths = null)
    {
        if (!_knowledge.IsConfigured) return;
        var wk = _wkStore.GetByPath(rootPath);
        if (string.IsNullOrEmpty(wk?.DifyDatasetId)) return;   // базы знаний нет — нечего синхронизировать

        var key = WorkspaceKnowledgeStore.NormalizePath(rootPath);
        var pending = _debounce.GetOrAdd(key, _ => new Pending());
        lock (pending)
        {
            if (changedPaths is not null)
                foreach (var p in changedPaths)
                {
                    var norm = Normalize(p);
                    if (norm.Length > 0) pending.Hints.Add(norm);
                }
            if (pending.Timer is null)
                pending.Timer = new Timer(_ => RunSyncSafe(rootPath, key), null, SyncDebounce, Timeout.InfiniteTimeSpan);
            else
                pending.Timer.Change(SyncDebounce, Timeout.InfiniteTimeSpan);
        }
    }

    private void RunSyncSafe(string rootPath, string key)
    {
        string[] hints = [];
        if (_debounce.TryGetValue(key, out var pending))
            lock (pending)
            {
                hints = pending.Hints.ToArray();
                pending.Hints.Clear();
            }
        _ = Task.Run(async () =>
        {
            try { await SyncAsync(rootPath, hints); }
            catch (Exception ex) { _logger.LogWarning(ex, "Синхронизация базы знаний проекта {Root}", rootPath); }
        });
    }

    // Полный дифф отслеживаемых файлов с Dify: изменился — переиндексация, исчез — детект
    // переноса по хешу среди changedPaths либо удаление документа
    public async Task<int> SyncAsync(string rootPath, IReadOnlyCollection<string>? changedPaths = null)
    {
        if (!_knowledge.IsConfigured) return 0;
        var wk = _wkStore.GetByPath(rootPath);
        if (string.IsNullOrEmpty(wk?.DifyDatasetId)) return 0;

        await _syncLock.WaitAsync();
        try
        {
            var changed = await BootstrapDocsAsync(wk);
            var datasetId = wk.DifyDatasetId!;
            var hints = (changedPaths ?? []).Select(Normalize).Where(p => p.Length > 0).ToList();

            foreach (var path in wk.Docs!.Keys.ToList())
            {
                try
                {
                    if (await SyncOneAsync(wk, datasetId, path, hints)) changed++;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Синк документа {Path} в датасете {Dataset}", path, datasetId);
                }
            }

            if (changed > 0)
            {
                _wkStore.Save(wk);
                await BroadcastAsync(rootPath, datasetId);
            }
            return changed;
        }
        finally { _syncLock.Release(); }
    }

    private async Task<bool> SyncOneAsync(WorkspaceKnowledge wk, string datasetId, string path, List<string> hints)
    {
        var docRef = wk.Docs![path];
        var hash = TryComputeHash(wk.RootPath, path);
        if (hash is not null)
        {
            if (hash == docRef.Hash) return false;   // содержимое не менялось
            await ReindexAsync(wk, datasetId, path, hash, docRef);
            return true;
        }

        // Файл исчез: возможно перенесён — ищем среди изменившихся путей файл с тем же содержимым
        var (newPath, newHash) = FindMoveTarget(wk, docRef, hints);
        if (newPath is not null)
        {
            MigrateTags(wk, path, newPath);
            wk.Docs.Remove(path);
            await ReindexAsync(wk, datasetId, newPath, newHash!, docRef);
            return true;
        }

        // Удалён: убираем документ из Dify и из карты отслеживания
        try { await _knowledge.DeleteDocumentAsync(datasetId, docRef.DocId); }
        catch (Exception ex) { _logger.LogDebug(ex, "Удаление документа исчезнувшего файла {Path}", path); }
        wk.Docs.Remove(path);
        wk.DocumentTags?.Remove(path);
        return true;
    }

    // Пересоздание документа под путём path (delete+create) с восстановлением тегов
    private async Task<DifyDocumentInfo> ReindexAsync(
        WorkspaceKnowledge wk, string datasetId, string path, string hash, WorkspaceDocRef oldRef)
    {
        if (!string.IsNullOrEmpty(oldRef.DocId))
            try { await _knowledge.DeleteDocumentAsync(datasetId, oldRef.DocId); }
            catch (Exception ex) { _logger.LogDebug(ex, "Удаление старой версии документа {Path}", path); }

        var tags = wk.DocumentTags?.GetValueOrDefault(path);
        DifyDocumentInfo doc;
        if (KnowledgeService.IsTextIndexable(path))
            doc = await _knowledge.IndexFileByTextAsync(datasetId, path, _files.ReadFile(wk.RootPath, path), tags);
        else
            doc = await _knowledge.IndexFileByBytesAsync(datasetId, path, _files.ReadFileBytes(wk.RootPath, path), tags);

        if (tags is { Count: > 0 })
            try { await _knowledge.UpdateDocumentTagsAsync(datasetId, doc.Id, tags); }
            catch (Exception ex) { _logger.LogDebug(ex, "Восстановление тегов документа {Path}", path); }

        wk.Docs![path] = new WorkspaceDocRef { DocId = doc.Id, Hash = hash };
        return doc;
    }

    // Идемпотентная индексация файла (панель БЗ / файловый менеджер): повторный вызов
    // обновляет существующий документ, а не плодит дубль; файл попадает в карту отслеживания.
    public async Task<(string DatasetId, DifyDocumentInfo Document)> IndexPathAsync(
        Project project, string username, string relativePath)
    {
        var datasetId = await _knowledge.EnsureDatasetAsync(project, username);
        var path = Normalize(relativePath);

        await _syncLock.WaitAsync();
        try
        {
            var wk = _wkStore.GetOrCreate(project.RootPath);
            await BootstrapDocsAsync(wk);   // датасет мог существовать до фичи — не терять его документы
            var oldRef = wk.Docs!.GetValueOrDefault(path) ?? new WorkspaceDocRef();
            var hash = TryComputeHash(project.RootPath, path) ?? throw new FileNotFoundException(relativePath);
            var doc = await ReindexAsync(wk, datasetId, path, hash, oldRef);
            _wkStore.Save(wk);
            return (datasetId, doc);
        }
        finally { _syncLock.Release(); }
    }

    // Перенос/переименование через файловый API: мигрируем ключи карты (файл либо всё
    // поддерево папки); Hash="" заставит ближайший синк пересоздать документ под новым именем
    public void HandleRename(string rootPath, string oldRelative, string newRelative)
    {
        var wk = _wkStore.GetByPath(rootPath);
        if (wk?.Docs is null || string.IsNullOrEmpty(wk.DifyDatasetId)) return;
        var oldPath = Normalize(oldRelative);
        var newPath = Normalize(newRelative);
        if (oldPath.Length == 0 || newPath.Length == 0 ||
            oldPath.Equals(newPath, StringComparison.OrdinalIgnoreCase)) return;

        var migrated = false;
        foreach (var key in wk.Docs.Keys.ToList())
        {
            string? target = null;
            if (key.Equals(oldPath, StringComparison.OrdinalIgnoreCase)) target = newPath;
            else if (key.StartsWith(oldPath + "/", StringComparison.OrdinalIgnoreCase))
                target = newPath + key[oldPath.Length..];
            if (target is null) continue;

            var docRef = wk.Docs[key];
            wk.Docs.Remove(key);
            wk.Docs[target] = new WorkspaceDocRef { DocId = docRef.DocId, Hash = "" };
            MigrateTags(wk, key, target);
            migrated = true;
        }
        if (!migrated) return;
        _wkStore.Save(wk);
        QueueSync(rootPath);
    }

    // Ручное удаление документа из панели БЗ: снять запись из карты отслеживания и теги,
    // чтобы синк не пересоздал документ по живому файлу
    public void ForgetDocument(string rootPath, string documentId)
    {
        var wk = _wkStore.GetByPath(rootPath);
        if (wk?.Docs is null) return;
        var keys = wk.Docs.Where(kv => kv.Value.DocId == documentId).Select(kv => kv.Key).ToList();
        if (keys.Count == 0) return;
        foreach (var k in keys)
        {
            wk.Docs.Remove(k);
            wk.DocumentTags?.Remove(k);
        }
        _wkStore.Save(wk);
    }

    // Первичное построение карты для датасета, созданного до фичи (имя документа = путь).
    // Уникальные имена считаем синхронными (хеш от текущего файла — без массовой переиндексации
    // при раскатке); дубли имён (наследие append-индексации) схлопываем до одного документа с
    // принудительной переиндексацией; документ без файла вычистит обычный дифф.
    private async Task<int> BootstrapDocsAsync(WorkspaceKnowledge wk)
    {
        if (wk.Docs is not null) return 0;
        var docs = new Dictionary<string, WorkspaceDocRef>();
        var removed = 0;
        var page = await _knowledge.ListAllDocumentsAsync(wk.DifyDatasetId!);
        foreach (var d in page.Data)
        {
            var path = Normalize(d.Name);
            if (path.Length == 0) continue;
            if (docs.TryGetValue(path, out var kept))
            {
                try
                {
                    await _knowledge.DeleteDocumentAsync(wk.DifyDatasetId!, d.Id);
                    removed++;
                }
                catch (Exception ex) { _logger.LogDebug(ex, "Удаление дубля документа {Path}", path); }
                kept.Hash = "";
                continue;
            }
            docs[path] = new WorkspaceDocRef { DocId = d.Id, Hash = TryComputeHash(wk.RootPath, path) ?? "" };
        }
        wk.Docs = docs;
        _wkStore.Save(wk);
        return removed;
    }

    // Кандидат переноса: среди изменившихся путей — существующий неотслеживаемый файл
    // с тем же хешем содержимого
    private (string? Path, string? Hash) FindMoveTarget(WorkspaceKnowledge wk, WorkspaceDocRef docRef, List<string> hints)
    {
        if (string.IsNullOrEmpty(docRef.Hash)) return (null, null);
        foreach (var cand in hints)
        {
            if (wk.Docs!.ContainsKey(cand) || !KnowledgeService.IsKnowledgeIndexable(cand)) continue;
            var hash = TryComputeHash(wk.RootPath, cand);
            if (hash == docRef.Hash) return (cand, hash);
        }
        return (null, null);
    }

    private static void MigrateTags(WorkspaceKnowledge wk, string oldPath, string newPath)
    {
        if (wk.DocumentTags is null) return;
        if (wk.DocumentTags.Remove(oldPath, out var tags)) wk.DocumentTags[newPath] = tags;
    }

    // SHA-256 содержимого файла; null — файла нет (или не читается)
    private string? TryComputeHash(string root, string rel)
    {
        try
        {
            if (KnowledgeService.IsTextIndexable(rel))
                return Hash(_files.ReadFile(root, rel));
            return Convert.ToHexString(SHA256.HashData(_files.ReadFileBytes(root, rel)));
        }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Чтение файла {Rel} для хеша", rel);
            return null;
        }
    }

    private async Task BroadcastAsync(string rootPath, string datasetId)
    {
        // Датасет общий для проектов в одной папке — уведомляем владельцев каждого
        foreach (var ownerId in _projects.GetByRootPath(rootPath).Select(p => p.OwnerId).Distinct())
            await _hub.Clients.Group("user_" + ownerId)
                .SendAsync("message", new KnowledgeChangedMessage("doc_changed", datasetId));
    }

    private static string Normalize(string path) =>
        (path ?? "").Replace('\\', '/').Trim().TrimStart('/');

    private static string Hash(string s) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s)));
}

// Мост «ход Claude → синк базы знаний»: агент правит файлы напрямую в ФС (мимо файлового
// API), поэтому слушаем события хода: file_changed даёт точные пути (и хинты для детекта
// переноса), result — страховочный полный дифф по завершении хода. Отдельный IHostedService,
// чтобы не раздувать SessionManager и гарантированно инстанцировать синк-сервис на старте.
public sealed class ProjectKnowledgeTurnSync : IHostedService
{
    private readonly SessionManager _sessions;
    private readonly ProjectManager _projects;
    private readonly ProjectKnowledgeSyncService _sync;

    public ProjectKnowledgeTurnSync(SessionManager sessions, ProjectManager projects,
        ProjectKnowledgeSyncService sync)
    {
        _sessions = sessions;
        _projects = projects;
        _sync = sync;
    }

    public Task StartAsync(CancellationToken ct)
    {
        _sessions.OnSessionMessage += OnMsgAsync;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken ct)
    {
        _sessions.OnSessionMessage -= OnMsgAsync;
        return Task.CompletedTask;
    }

    private Task OnMsgAsync(Session session, ServerMessage msg)
    {
        if (string.IsNullOrEmpty(session.ProjectId)) return Task.CompletedTask;
        if (msg is not (FileChangedMessage or ResultMessage)) return Task.CompletedTask;
        var root = _projects.GetById(session.ProjectId)?.RootPath;
        if (root is null) return Task.CompletedTask;
        _sync.QueueSync(root, msg is FileChangedMessage fc ? [fc.Path] : null);
        return Task.CompletedTask;
    }
}
