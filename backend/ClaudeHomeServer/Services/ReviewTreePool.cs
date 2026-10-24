using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.Composition;
using ClaudeHomeServer.Services.Git;
using ClaudeHomeServer.Services.Tasks;

namespace ClaudeHomeServer.Services;

// Что получает ревьюер: готовый слот, коммит волны, на котором он стоит, и грязные пути
// дерева волны (незакоммиченное в слот не попадает — ревьюеру о нём говорят отдельно).
public sealed record ReviewTreeLease(string SlotPath, string Sha, IReadOnlyList<string> DirtyPaths);

// Пул слотов ревью: два detached worktree на волну фичи, чтобы ревьюер читал и собирал
// зафиксированный коммит волны, а не живое дерево, в котором исполнитель правит файлы.
// Слот живёт между ревью (obj/, node_modules переживают сброс), поэтому сборка в нём
// инкрементальная. Занятость слотов — один словарь slotPath→taskId под одним замком на
// процесс; слот резервируется ДО обращения к git, при сбое git резерв снимается.
public sealed class ReviewTreePool
{
    internal const int SlotsPerWave = 2;

    private readonly GitService _git;
    private readonly Func<string?, Project, string> _worktreesBase;
    private readonly Func<string?, string, string> _cwdForOwner;
    private readonly ILogger<ReviewTreePool> _log;
    private readonly Func<IEnumerable<TaskItem>> _allTasks;
    private readonly Func<string, string?> _sessionWorktree;
    private readonly Func<string, Project?> _projectById;

    private readonly object _gate = new();
    private readonly Dictionary<string, string> _leases = new(StringComparer.Ordinal);

    public ReviewTreePool(GitService git, SessionManager sessions, TaskManager tasks, IProjectManager projects,
        ILogger<ReviewTreePool> log)
        : this(git, sessions.WorktreesBase, sessions.CwdForOwner, log,
            () => projects.GetAll().SelectMany(p => tasks.GetByProject(p.Id)), id => sessions.GetById(id)?.WorktreePath, projects.GetById) { }

    internal ReviewTreePool(GitService git, Func<string?, Project, string> worktreesBase,
        Func<string?, string, string> cwdForOwner, ILogger<ReviewTreePool> log,
        Func<IEnumerable<TaskItem>>? allTasks = null, Func<string, string?>? sessionWorktree = null,
        Func<string, Project?>? projectById = null)
    {
        _allTasks = allTasks ?? (() => []);
        _sessionWorktree = sessionWorktree ?? (_ => null);
        _projectById = projectById ?? (_ => null);
        _git = git;
        _worktreesBase = worktreesBase;
        _cwdForOwner = cwdForOwner;
        _log = log;
    }

    // null — оба слота волны заняты другими задачами. Повторный вызов той же задачи отдаёт
    // её же слот (заново сброшенный на актуальный коммит волны).
    public async Task<ReviewTreeLease?> TryAcquireAsync(
        string? ownerId, Project project, string waveTreePath, string taskId, CancellationToken ct = default)
    {
        // Git и диск проекта — у локального проекта на устройстве: отказ до обращения к диску
        ProjectCapabilityGuard.EnsureAllowed(project, ProjectCapabilityArea.FileBound);

        var sha = await _git.LocalTipAsync(ownerId, waveTreePath, "HEAD", ct)
            ?? throw new InvalidOperationException("Не удалось определить HEAD дерева волны");
        var snapshot = await _git.RepoSnapshotAsync(ownerId, waveTreePath, ct);
        var worktrees = await _git.WorktreeListAsync(ownerId, project.RootPath, ct);

        var waveNorm = PathNormalizer.NormalizePath(waveTreePath);
        var branch = worktrees.FirstOrDefault(w => PathNormalizer.NormalizePath(w.Path) == waveNorm)?.Branch
            ?? Path.GetFileName(Path.TrimEndingDirectorySeparator(waveTreePath));
        var slotBase = _worktreesBase(ownerId, project);
        var prefix = $"{branch.Replace('/', '-')}-review-";

        var slotPath = Reserve(slotBase, prefix, taskId);
        if (slotPath is null) return null;

        try
        {
            // Путь ГЛАЗАМИ CLI считаем ДО создания дерева: у container-пользователя ToRuntime
            // отвергает путь вне монтирований, а исключение после worktree add оставило бы сироту
            _ = _cwdForOwner(ownerId, slotPath);

            var slotNorm = PathNormalizer.NormalizePath(slotPath);
            if (worktrees.Any(w => PathNormalizer.NormalizePath(w.Path) == slotNorm))
                await _git.ResetReviewWorktreeAsync(ownerId, project.RootPath, slotPath, sha, ct);
            else
            {
                Directory.CreateDirectory(slotBase);
                await _git.WorktreeAddDetachedAsync(ownerId, project.RootPath, slotPath, sha, ct);
            }
            LinkNodeModules(project.RootPath, slotPath);
        }
        catch
        {
            Release(taskId);
            throw;
        }

        return new ReviewTreeLease(slotPath, sha, snapshot.DirtyPaths);
    }

    // Идемпотентно: неизвестная задача и повторный вызов — no-op
    public void Release(string taskId)
    {
        lock (_gate)
        {
            foreach (var slot in _leases.Where(kv => kv.Value == taskId).Select(kv => kv.Key).ToList())
                _leases.Remove(slot);
        }
    }

    // Волны больше нет — проверять в слотах нечего, поэтому слот сносится и под живой арендой.
    // Звать ДО сноса дерева волны: имя слота строится по ветке дерева, а после его удаления
    // ветку из списка worktree уже не узнать.
    public async Task RemoveSlotsOfWaveAsync(
        string? ownerId, Project project, string waveTreePath, CancellationToken ct = default)
    {
        ProjectCapabilityGuard.EnsureAllowed(project, ProjectCapabilityArea.FileBound);

        var worktrees = await _git.WorktreeListAsync(ownerId, project.RootPath, ct);
        var waveNorm = PathNormalizer.NormalizePath(waveTreePath);
        var branch = worktrees.FirstOrDefault(w => PathNormalizer.NormalizePath(w.Path) == waveNorm)?.Branch
            ?? Path.GetFileName(Path.TrimEndingDirectorySeparator(waveTreePath));
        var slotBase = _worktreesBase(ownerId, project);
        var prefix = $"{branch.Replace('/', '-')}-review-";

        for (var n = 1; n <= SlotsPerWave; n++)
            await RemoveSlotAsync(ownerId, project, worktrees, Path.Combine(slotBase, prefix + n), ct);
    }

    // Все слоты проекта: дерево в базе деревьев проекта с именем «*-review-1|2»
    public async Task RemoveSlotsOfProjectAsync(string? ownerId, Project project, CancellationToken ct = default)
    {
        ProjectCapabilityGuard.EnsureAllowed(project, ProjectCapabilityArea.FileBound);

        var worktrees = await _git.WorktreeListAsync(ownerId, project.RootPath, ct);
        var baseNorm = PathNormalizer.NormalizePath(_worktreesBase(ownerId, project));
        var slotBase = _worktreesBase(ownerId, project);
        foreach (var w in worktrees)
        {
            var norm = PathNormalizer.NormalizePath(w.Path);
            var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(w.Path));
            if (PathNormalizer.NormalizePath(Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(w.Path)) ?? "") != baseNorm
                || !IsSlotName(name)) continue;
            await RemoveSlotAsync(ownerId, project, worktrees, Path.Combine(slotBase, name), ct);
        }
    }

    // После рестарта учёт аренд пуст, а чаты открытых ревью живут в своих слотах: без
    // восстановления вторая задача взяла бы слот ревьюера и сбросила его под ногами.
    // Закрытые задачи и задачи с остановленным исполнителем аренду не получают — их слот свободен
    // (на «Стопе» аренда снимается, поэтому после рестарта поведение то же).
    public Task RestoreLeasesAsync()
    {
        foreach (var task in _allTasks())
        {
            if (task.Role != TaskRoles.Review || task.Status == TaskItemStatus.Done || task.ExecutorStoppedAt is not null
                || task.LinkedSessionId is null || task.ProjectId is null) continue;
            if (_projectById(task.ProjectId) is not { } project || !ProjectCapabilities.FilesOnServer(project)) continue;
            if (_sessionWorktree(task.LinkedSessionId) is not { } wt) continue;

            string slotBase;
            try { slotBase = _worktreesBase(task.OwnerId, project); }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Аренда слота задачи {TaskId} не восстановлена: нет базы деревьев", task.Id);
                continue;
            }
            var trimmed = Path.TrimEndingDirectorySeparator(wt);
            var name = Path.GetFileName(trimmed);
            if (!IsSlotName(name)
                || PathNormalizer.NormalizePath(Path.GetDirectoryName(trimmed) ?? "") != PathNormalizer.NormalizePath(slotBase))
                continue;

            var slotPath = Path.Combine(slotBase, name);
            lock (_gate) _leases.TryAdd(slotPath, task.Id);
        }
        return Task.CompletedTask;
    }

    private static bool IsSlotName(string name)
    {
        for (var n = 1; n <= SlotsPerWave; n++)
            if (name.Length > 8 && name.EndsWith("-review-" + n, StringComparison.Ordinal)) return true;
        return false;
    }

    private async Task RemoveSlotAsync(string? ownerId, Project project,
        IReadOnlyList<GitWorktreeInfo> worktrees, string slotPath, CancellationToken ct)
    {
        var norm = PathNormalizer.NormalizePath(slotPath);
        lock (_gate) _leases.Remove(slotPath);
        if (!worktrees.Any(w => PathNormalizer.NormalizePath(w.Path) == norm)) return;
        try { await _git.WorktreeRemoveAsync(ownerId, project.RootPath, slotPath, force: true, ct); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogWarning(ex, "Слот ревью {Slot} не снесён", slotPath);
        }
    }

    private string? Reserve(string slotBase, string prefix, string taskId)
    {
        lock (_gate)
        {
            for (var n = 1; n <= SlotsPerWave; n++)
            {
                var path = Path.Combine(slotBase, prefix + n);
                if (_leases.TryGetValue(path, out var holder) && holder == taskId) return path;
            }
            for (var n = 1; n <= SlotsPerWave; n++)
            {
                var path = Path.Combine(slotBase, prefix + n);
                if (_leases.ContainsKey(path)) continue;
                _leases[path] = taskId;
                return path;
            }
            return null;
        }
    }

    // Как scripts/worktree-setup.sh: node_modules в worktree пуст, зависимости лежат в основном
    // checkout. Реальную папку и уже стоящую ссылку не трогаем; нет источника — молча пропускаем.
    private void LinkNodeModules(string mainRoot, string slotPath)
    {
        try
        {
            var src = Path.Combine(mainRoot, "frontend", "node_modules");
            var dst = Path.Combine(slotPath, "frontend", "node_modules");
            if (!Directory.Exists(src) || !Directory.Exists(Path.GetDirectoryName(dst)!)) return;
            if (Directory.Exists(dst) || new DirectoryInfo(dst).LinkTarget is not null) return;
            Directory.CreateSymbolicLink(dst, src);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Не удалось связать node_modules в слоте ревью {Slot}", slotPath);
        }
    }
}
