using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.Knowledge;
using Microsoft.Extensions.Logging;

namespace ClaudeHomeServer.Services.Sessions;

// Рабочая папка чата (S3-3, docs/research/session-manager-split-2026-11.md §1): отдельное
// git-дерево чата — включение/снятие с переносом транскрипта, привязка к уже существующему
// дереву, уборка снимка графа и watcher'ов за снятым деревом, пометка «зафиксировано в git»
// (CommittedFilePaths) и её снятие по tool_use хода. Вынесено из SessionManager; фасад
// держит тонкие обёртки с прежними сигнатурами.
// Лок пометки — entry-составляющая стора (_store.SaveLock), тот же замок, что у оставшихся
// в фасаде write-сообщений хода: read-modify-write списка остаётся под одним локалом.
// Класс public ТОЛЬКО потому, что он параметр публичного конструктора SessionManager (CS0051);
// обход фасада закрывает сторож формы ядра (правило 4).
// Зависимости (8): git, codeGraphs, graphInspector, fileWatchers, warmup, projects, store, route.
public sealed class ChatWorktree
{
    private readonly Git.GitService? _git;
    private readonly CodeGraph.CodeGraphService? _codeGraphs;
    private readonly CodeGraph.ICodeGraphInspector? _graphInspector;
    private readonly FileWatcherService? _fileWatchers;
    private readonly WorktreeBuildWarmup _warmup;
    private readonly ProjectManager _projects;
    private readonly SessionStore _store;
    private readonly CliRoute _route;
    private readonly ILogger<ChatWorktree> _log;

    public ChatWorktree(
        Git.GitService? git,
        CodeGraph.CodeGraphService? codeGraphs,
        CodeGraph.ICodeGraphInspector? graphInspector,
        FileWatcherService? fileWatchers,
        WorktreeBuildWarmup warmup,
        ProjectManager projects,
        SessionStore store,
        CliRoute route,
        ILogger<ChatWorktree> log)
    {
        _git = git;
        _codeGraphs = codeGraphs;
        _graphInspector = graphInspector;
        _fileWatchers = fileWatchers;
        _warmup = warmup;
        _projects = projects;
        _store = store;
        _route = route;
        _log = log;
    }

    public void MarkFilesCommitted(
        IReadOnlyList<(string SessionId, IReadOnlyCollection<string> Paths, IReadOnlyCollection<string> RawPaths)> batch)
    {
        // Общий лок с UnmarkFileCommitted: пометка (поток статуса) и снятие (петля сообщений
        // хода) делают read-modify-write одного списка — без лока коммит ровно в момент
        // правки того же файла терял бы обновление. _saveLock (System.Threading.Lock)
        // реентерабелен (подсчёт рекурсии): вложенный SaveSessions берёт его повторно без клинча.
        lock (_store.SaveLock)
        {
            var changed = false;
            foreach (var (sessionId, paths, rawPaths) in batch)
            {
                if (!_store.Sessions.TryGetValue(sessionId, out var entry)) continue;
                var current = new HashSet<string>(entry.Info.CommittedFilePaths, StringComparer.Ordinal);
                var next = new HashSet<string>(current, StringComparer.Ordinal);
                next.UnionWith(paths);
                next.IntersectWith(rawPaths is HashSet<string> h ? h : [.. rawPaths]);
                if (next.SetEquals(current)) continue;
                entry.Info.CommittedFilePaths = [.. next.Order()];
                changed = true;
            }
            if (changed) _store.SaveSessions();
        }
    }

    // Вернуть путь в учёт атрибуции: чат снова правит файл после фиксации. Сидит на
    // ГОРЯЧЕМ пути (каждое write-сообщение хода) — ранний выход без записи, когда
    // пометки нет. path — уже нормализованный (SessionChangedPaths.Normalize).
    // UpdatedAt не двигаем (см. MarkFilesCommitted).
    public void UnmarkFileCommitted(string sessionId, string path)
    {
        if (!_store.Sessions.TryGetValue(sessionId, out var entry)) return;
        // Быстрая проверка ДО лока — горячий путь (каждое write-сообщение хода) не должен
        // толкаться с пометкой коммита; под локом перепроверяется (см. MarkFilesCommitted)
        if (!entry.Info.CommittedFilePaths.Contains(path, StringComparer.Ordinal)) return;
        lock (_store.SaveLock)
        {
            if (!entry.Info.CommittedFilePaths.Contains(path, StringComparer.Ordinal)) return;
            entry.Info.CommittedFilePaths = [.. entry.Info.CommittedFilePaths.Where(p => p != path)];
            _store.SaveSessions();
        }
    }

    // Ветка tool_use снятия пометки: file_changed НЕ хватает — событие не приходит, когда
    // содержимое не изменилось, путь отсечён TurnFileWatcher.ShouldIgnore или файл удалён;
    // такой файл после коммита выпал бы из атрибуции навсегда. Гейт worktree-хода зеркалит
    // SessionChangedPaths.Extract (TurnFileWatcher отдаёт пути относительно СВОЕГО корня —
    // правка в worktree не должна воскрешать атрибуцию одноимённого файла в корне).
    // Известное ограничение (принято): снятие идёт по ЗАЯВКЕ tool_use, а не по успешному
    // tool_result — отклонённый permission или упавший Edit вернут атрибуцию файлу,
    // которого чат фактически не менял (то же множество источников, что у Extract).
    internal void TryUnmarkCommittedOnToolUse(string sessionId, SessionEntry? entry, string toolName, object? input)
    {
        if (entry is null || entry.TurnInWorktree) return;
        if (!SessionChangedPaths.IsWriteTool(toolName)) return;
        // Ранний выход до резолва проекта: у чата без пометок здесь горячий no-op
        if (entry.Info.CommittedFilePaths.Count == 0) return;
        var root = entry.Info.ProjectId is { } pid ? _projects.GetById(pid)?.RootPath : null;
        if (root is null) return;
        if (SessionChangedPaths.NormalizedToolPath(input, root) is { } rel)
            UnmarkFileCommitted(sessionId, rel);
    }

    // Уборка за удалённым деревом чата (ADR-003): снимаем watcher его файлов и выбрасываем
    // снимок графа из data/code-graphs — иначе он остался бы сиротой на диске, а watcher
    // держал бы handle на исчезнувшую папку. Best-effort: уборка не должна ронять удаление.
    internal void ReleaseWorktreeGraph(string sessionId, string worktreePath)
    {
        try { _fileWatchers?.UnwatchPath("worktree:" + sessionId); } catch { /* уборка best-effort */ }
        try { _codeGraphs?.Invalidate(worktreePath); } catch { /* уборка best-effort */ }
    }

    // Начатый чат переезжает С КОНТЕКСТОМ: транскрипт CLI копируется в папку нового cwd
    // (--resume ищет его по уплощённому cwd); не удалось скопировать — операция отменяется,
    // контекст дороже фичи. У container-пользователя и профиль, и cwd берутся в песочной
    // раскладке (ConfigRootFor/CwdForOwner) — переезд работает так же, как на хосте.
    // Процесс не трогаем: между ходами его нет, AdapterStale пересоберёт контекст со
    // следующего хода.
    public async Task<Session?> SetWorktreeAsync(string sessionId, bool enabled,
        string? branch = null, bool force = false, string? userId = null)
    {
        if (!_store.Sessions.TryGetValue(sessionId, out var entry)) return null;
        var ownerId = _store.ResolveOwnerId(entry.Info);
        if (userId is not null && ownerId != userId) return null;
        if (_git is null)
            throw new InvalidOperationException("Git-операции недоступны");
        if (entry.Info.ProjectId is not string projectId)
            throw new InvalidOperationException("Отдельное дерево доступно только в чате проекта");
        var project = _projects.GetById(projectId)
            ?? throw new InvalidOperationException("Проект не найден");
        // Дерево и перенос транскрипта под новый cwd — git и диск проекта: у локального
        // проекта они на устройстве, отказ до обращения к диску
        Composition.ProjectCapabilityGuard.EnsureAllowed(project, Composition.ProjectCapabilityArea.FileBound);

        // Идемпотентность: повторное включение/выключение — no-op
        if (enabled == (entry.Info.WorktreePath is not null)) return entry.Info;

        if (enabled)
        {
            if (!Path.Exists(Path.Combine(project.RootPath, ".git")))
                throw new Git.GitCommandException("В папке проекта нет git-репозитория");

            // Ветка: заданная вручную либо wt/<slug имени чата>; коллизии решаем суффиксом
            var slug = PersonaManager.Slugify(entry.Info.Name ?? "");
            if (slug.Length == 0) slug = sessionId[..Math.Min(8, sessionId.Length)];
            var branchName = string.IsNullOrWhiteSpace(branch) ? $"wt/{slug}" : branch.Trim();
            var taken = (await _git.BranchesAsync(ownerId, project.RootPath))
                .Select(b => b.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var unique = branchName;
            for (var n = 2; taken.Contains(unique); n++) unique = $"{branchName}-{n}";
            branchName = unique;

            // Папка: {home}/.worktrees/<проект>/<ветка> — вне дерева главной репы и
            // гарантированно внутри Sandbox:ProjectsRoot у container-пользователей
            var wtPath = Path.Combine(_route.WorktreesBase(ownerId, project), branchName.Replace('/', '-'));
            Directory.CreateDirectory(Path.GetDirectoryName(wtPath)!);

            // Рабочие папки ГЛАЗАМИ CLI считаем ДО создания дерева: у container-пользователя
            // ToRuntime отвергает путь вне монтирований, а исключение после WorktreeAddAsync
            // обошло бы rollback ниже и оставило дерево-сироту. ClaudeSessionId читаем ОДИН
            // РАЗ здесь же: WorktreeAddAsync ниже содержит await, и повторное чтение после
            // него могло бы увидеть другое значение (null → появился, если параллельно
            // стартовал первый ход чата), а srcCwd/dstCwd остались бы не посчитаны
            (string Csid, string Src, string Dst)? migration = entry.Info.ClaudeSessionId is string csid0
                ? (csid0, _route.CwdForOwner(ownerId, project.RootPath), _route.CwdForOwner(ownerId, wtPath))
                : null;

            await _git.WorktreeAddAsync(ownerId, project.RootPath, wtPath, branchName);

            if (migration is { } m
                && !Llm.TranscriptMigrator.TryRelocateCwd(
                    _route.ConfigRootFor(ownerId, entry.Info.Provider), m.Src, m.Dst, m.Csid, out var err))
            {
                // Дерево без контекста бесполезно — откатываем и отдаём причину наружу
                try { await _git.WorktreeRemoveAsync(ownerId, project.RootPath, wtPath, force: true); }
                catch { /* уборка best-effort */ }
                throw new Git.GitCommandException($"Не удалось перенести контекст разговора: {err}");
            }

            entry.Info.WorktreePath = wtPath;
            entry.Info.WorktreeBranch = branchName;
            // Чат сам создал дерево — владеет им: при удалении чата/проекта дерево сносится
            entry.Info.WorktreeOwned = true;
            _warmup.TryStart(project, wtPath);
            _ = _graphInspector?.WarmIfNeeded(wtPath);
        }
        else
        {
            var wtPath = entry.Info.WorktreePath!;
            // Как и при включении — перевод путей до снятия дерева (см. выше), ClaudeSessionId
            // читаем один раз здесь же (ниже есть await StatusAsync/WorktreeRemoveAsync)
            (string Csid, string Src, string Dst)? migration = entry.Info.ClaudeSessionId is string csid0
                ? (csid0, _route.CwdForOwner(ownerId, wtPath), _route.CwdForOwner(ownerId, project.RootPath))
                : null;

            // Гейт: незакоммиченные правки в дереве пропадут вместе с ним (ветка остаётся)
            if (!force)
            {
                var st = await _git.StatusAsync(ownerId, wtPath);
                if (st.Staged.Count > 0 || st.Unstaged.Count > 0 || st.Untracked.Count > 0)
                    throw new Git.GitCommandException(
                        "В отдельном дереве есть несохранённые изменения — зафиксируйте их или подтвердите принудительное удаление");
            }

            if (migration is { } m
                && !Llm.TranscriptMigrator.TryRelocateCwd(
                    _route.ConfigRootFor(ownerId, entry.Info.Provider), m.Src, m.Dst, m.Csid, out var err))
                throw new Git.GitCommandException($"Не удалось перенести контекст разговора: {err}");

            await _git.WorktreeRemoveAsync(ownerId, project.RootPath, wtPath, force);
            entry.Info.WorktreePath = null;
            entry.Info.WorktreeBranch = null;
            // Дерево снято — владение сбрасываем вместе с путём (ветка остаётся в репе)
            entry.Info.WorktreeOwned = false;
            ReleaseWorktreeGraph(sessionId, wtPath);
        }

        // Следующий ход пересоздаст адаптер с новым cwd (между ходами процесса нет)
        entry.AdapterStale = true;
        _store.SaveSessions();
        return entry.Info;
    }

    /// <summary>
    /// Привязать чат к УЖЕ СУЩЕСТВУЮЩЕМУ дереву (чат-исполнитель задачи с worktree в её поле):
    /// проставляет Session.WorktreePath/WorktreeBranch без создания дерева и переноса
    /// транскрипта — этим он и отличается от SetWorktreeAsync. Вызывать до первого хода:
    /// cwd процесса подставит EffectiveRoot сам, а начатый чат переезжает только с контекстом
    /// (SetWorktreeAsync). Дерево должно лежать на диске и числиться в «git worktree list»
    /// репы проекта. false — привязка не состоялась: вызывающий стартует в корне проекта.
    /// </summary>
    public async Task<bool> AttachWorktreeAsync(string sessionId, string worktreePath, string? branch = null)
    {
        if (string.IsNullOrWhiteSpace(worktreePath)) return false;
        if (!_store.Sessions.TryGetValue(sessionId, out var entry)) return false;
        // Дерево бывает только у чата проекта; начатый чат не трогаем — его контекст
        // привязан к прежнему cwd (--resume ищет транскрипт по уплощённому пути)
        if (entry.Info.ProjectId is not string projectId || entry.Info.ClaudeSessionId is not null) return false;
        if (_projects.GetById(projectId) is not { } project || _git is null) return false;

        var path = Path.GetFullPath(worktreePath.Trim());
        if (!Directory.Exists(path)) return false;

        // Числится ли дерево в главной репе: чужая (или уже снятая) папка увела бы ход мимо
        // проекта. Сравнение нормализованными путями — как у worktree-чатов в CodeGraphController.
        var wanted = WorkspaceKnowledgeStore.NormalizePath(path);
        GitWorktreeInfo? known;
        try
        {
            known = (await _git.WorktreeListAsync(_store.ResolveOwnerId(entry.Info), project.RootPath))
                .FirstOrDefault(w => WorkspaceKnowledgeStore.NormalizePath(w.Path) == wanted);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Не удалось прочитать git worktree list проекта {Project}", project.Id);
            return false;
        }
        if (known is null) return false;

        entry.Info.WorktreePath = path;
        // Ветка задана вызывающим либо берётся из самого дерева (метка в git-баре чата)
        entry.Info.WorktreeBranch = string.IsNullOrWhiteSpace(branch) ? known.Branch : branch.Trim();
        // Дерево завёл человек/агент, а не чат: чат им не владеет — при удалении не сносим
        entry.Info.WorktreeOwned = false;
        entry.AdapterStale = true;
        _store.SaveSessions();
        // Дерево задачи заводит не сервер, а человек/агент, — прогрев здесь, при первой привязке;
        // уже собранное (obj/ есть) или уже прогретое дерево прогрев пропускает сам
        _warmup.TryStart(project, path);
        _ = _graphInspector?.WarmIfNeeded(path);
        return true;
    }
}
