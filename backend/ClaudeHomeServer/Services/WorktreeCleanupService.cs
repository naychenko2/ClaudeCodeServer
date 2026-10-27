using ClaudeHomeServer.Services.Files;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.Git;
using ClaudeHomeServer.Services.Tasks;

namespace ClaudeHomeServer.Services;

// Ночная уборка worktree и /tmp + тревога о нехватке места на корне.
//
// Зачем: каждый worktree исполнителя копит ~15 ГБ bin/obj, дерево закрытой задачи никто не
// чистит (25.10.2026 корень дошёл до 99 %, 352 ГБ сборок в 69 деревьях). Основная защита —
// вынос артефактов (CCS_BUILD_ROOT, WorktreeBuildArtifacts) и снос деревьев при закрытии
// (GitService.WorktreeRemoveAsync); этот сервис — страховка для тех, кто остался.
//
// Раз в час: проверка свободного места (тревога админам с кулдауном). Раз в сутки, в ночном
// окне: проход по worktree (busy/простой/влито → CleanArtifacts или RemoveWorktree) и /tmp.
// Итог каждого прохода — строка в журнале data/worktree-cleanup.log.
public class WorktreeCleanupService(
    IConfiguration config,
    ProjectManager projects,
    GitService git,
    TaskManager tasks,
    UserStore users,
    NotificationService notifications,
    ILogger<WorktreeCleanupService> log) : BackgroundService
{
    private static readonly TimeSpan Tick = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan StartupDelay = TimeSpan.FromMinutes(5);

    private DateTime _lastPassLocalDate = DateTime.MinValue;
    private DateTime _lastAlertUtc = DateTime.MinValue;

    public record PassReport(int Scanned, int Cleaned, int Removed, int Skipped, long FreedBytes,
        TmpDirCleaner.Result Tmp, string? Aborted);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(StartupDelay, stoppingToken); }
        catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var options = WorktreeCleanupOptions.From(config);
                if (options.Enabled)
                {
                    await CheckFreeSpaceAsync(options);
                    var now = DateTime.Now;
                    if (options.IsNight(now) && _lastPassLocalDate != now.Date)
                    {
                        _lastPassLocalDate = now.Date;
                        await RunPassAsync(options, stoppingToken);
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception ex)
            {
                log.LogError(ex, "Уборка worktree: проход не выполнен");
            }

            try { await Task.Delay(Tick, stoppingToken); }
            catch (OperationCanceledException) { return; }
        }
    }

    public async Task<PassReport> RunPassAsync(WorktreeCleanupOptions options, CancellationToken ct)
    {
        var nowUtc = DateTime.UtcNow;
        var idle = TimeSpan.FromHours(options.IdleHours);
        var probe = new ProcWorktreeProcessProbe();
        if (!probe.Available)
        {
            // Не знаем, кто что держит, — ничего не удаляем
            var aborted = new PassReport(0, 0, 0, 0, 0, new TmpDirCleaner.Result(0, 0, 0), "нет /proc");
            WriteJournal(aborted);
            return aborted;
        }

        int scanned = 0, cleaned = 0, removed = 0, skipped = 0;
        long freed = 0;
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var project in projects.GetAll())
        {
            ct.ThrowIfCancellationRequested();
            // Worktree локального проекта лежат на его устройстве — сервер их не чистит (G10).
            if (!ProjectCapabilities.FilesOnServer(project) || string.IsNullOrEmpty(project.RootPath)) continue;
            IReadOnlyList<GitWorktreeInfo> trees;
            try { trees = await git.WorktreeListAsync(project.OwnerId, project.RootPath, ct); }
            catch (Exception ex) { log.LogWarning(ex, "Уборка worktree: список для {Root} не получен", project.RootPath); continue; }
            if (trees.Count == 0) continue;

            var mainPath = PathNormalizer.NormalizePath(trees[0].Path);
            // Один репозиторий может быть проектом у нескольких владельцев — второй раз не идём
            if (!seen.Add(mainPath)) continue;

            var mainHead = trees[0].Head;
            // Незавершённые задачи, привязанные к worktree: дерево им ещё понадобится
            var openTaskTrees = tasks.GetByProject(project.Id)
                .Where(t => t.Status != TaskItemStatus.Done && !string.IsNullOrEmpty(t.WorktreePath))
                .Select(t => PathNormalizer.NormalizePath(t.WorktreePath!))
                .ToHashSet(StringComparer.Ordinal);
            foreach (var tree in trees)
            {
                scanned++;
                var isPrimary = PathNormalizer.NormalizePath(tree.Path) == mainPath;
                var exists = Directory.Exists(tree.Path);
                var facts = new WorktreeFacts(
                    tree.Path, isPrimary, exists, tree.Locked, exists && probe.IsBusy(tree.Path),
                    exists ? LastActivityUtc(tree.Path) : DateTime.MinValue,
                    exists && HasArtifacts(tree.Path, options),
                    HasOpenTask: openTaskTrees.Contains(PathNormalizer.NormalizePath(tree.Path)));

                if (WorktreeCleanupPlanner.SkipReason(facts, nowUtc, idle) is not null) { skipped++; continue; }

                if (options.RemoveMergedWorktrees && !tree.Locked)
                    facts = facts with { IsMerged = await IsMergedAsync(project, tree, mainHead, ct), IsClean = null };
                if (facts.IsMerged == true)
                    facts = facts with { IsClean = await IsCleanAsync(project, tree, ct) };

                var decision = WorktreeCleanupPlanner.Decide(facts, nowUtc, idle, options.RemoveMergedWorktrees);
                switch (decision.Action)
                {
                    case CleanupAction.RemoveWorktree:
                        try
                        {
                            var size = WorktreeBuildArtifacts.SizeOf(tree.Path);
                            await git.WorktreeRemoveAsync(project.OwnerId, project.RootPath, tree.Path, force: false, ct);
                            freed += size;
                            removed++;
                        }
                        catch (Exception ex)
                        {
                            log.LogWarning(ex, "Уборка worktree: {Path} не снят", tree.Path);
                            skipped++;
                        }
                        break;
                    case CleanupAction.CleanArtifacts:
                        freed += WorktreeBuildArtifacts.Clean(tree.Path, options.CleanNodeModules);
                        cleaned++;
                        break;
                    default:
                        skipped++;
                        break;
                }
            }

            try { await git.WorktreePruneAsync(project.OwnerId, project.RootPath, ct); }
            catch (Exception ex) { log.LogDebug(ex, "Уборка worktree: prune не удался"); }
        }

        var tmp = TmpDirCleaner.Clean(Path.GetTempPath(), DateTime.UtcNow, TimeSpan.FromHours(options.TmpMaxAgeHours));
        var report = new PassReport(scanned, cleaned, removed, skipped, freed + tmp.Bytes, tmp, null);
        WriteJournal(report, probe.Unreadable);
        return report;
    }

    private async Task<bool> IsMergedAsync(Project project, GitWorktreeInfo tree, string? mainHead, CancellationToken ct) =>
        !string.IsNullOrEmpty(tree.Head) && !string.IsNullOrEmpty(mainHead)
        && await git.IsAncestorStrictAsync(project.OwnerId, project.RootPath, tree.Head, mainHead, ct);

    private async Task<bool?> IsCleanAsync(Project project, GitWorktreeInfo tree, CancellationToken ct)
    {
        try
        {
            var st = await git.StatusPathsAsync(project.OwnerId, tree.Path, ct);
            return st.IsRepo && st.Staged.Count == 0 && st.Unstaged.Count == 0 && st.Untracked.Count == 0;
        }
        catch (Exception) { return null; }
    }

    private static bool HasArtifacts(string path, WorktreeCleanupOptions options) =>
        WorktreeBuildArtifacts.InTreeDirs(path, options.CleanNodeModules).Count > 0
        || (WorktreeBuildArtifacts.ExternalDirFor(path) is { } ext && Directory.Exists(ext));

    // Последняя активность: ссылки на gitdir (HEAD/index — коммиты, add, checkout) и мелкая
    // выборка времён каталогов артефактов. Обход всех файлов дерева слишком дорог.
    public static DateTime LastActivityUtc(string worktreePath)
    {
        var newest = Directory.GetLastWriteTimeUtc(worktreePath);
        void Take(string p)
        {
            try
            {
                if (File.Exists(p)) newest = Max(newest, File.GetLastWriteTimeUtc(p));
                else if (Directory.Exists(p)) newest = Max(newest, Directory.GetLastWriteTimeUtc(p));
            }
            catch (Exception) { /* исчез */ }
        }

        var gitFile = Path.Combine(worktreePath, ".git");
        string? gitDir = null;
        try
        {
            if (File.Exists(gitFile))
            {
                var line = File.ReadLines(gitFile).FirstOrDefault(l => l.StartsWith("gitdir:", StringComparison.Ordinal));
                gitDir = line?["gitdir:".Length..].Trim();
            }
            else if (Directory.Exists(gitFile)) gitDir = gitFile;
        }
        catch (Exception) { /* без gitdir обойдёмся временем каталогов */ }
        if (gitDir is not null)
        {
            Take(Path.Combine(gitDir, "HEAD"));
            Take(Path.Combine(gitDir, "index"));
            Take(Path.Combine(gitDir, "logs", "HEAD"));
        }

        foreach (var dir in WorktreeBuildArtifacts.InTreeDirs(worktreePath, includeNodeModules: false))
        {
            Take(dir);
            try
            {
                foreach (var sub in Directory.EnumerateDirectories(dir))
                {
                    Take(sub);
                    foreach (var leaf in Directory.EnumerateDirectories(sub)) Take(leaf);
                }
            }
            catch (Exception) { /* исчез */ }
        }
        if (WorktreeBuildArtifacts.ExternalDirFor(worktreePath) is { } ext) Take(ext);
        return newest;
    }

    private static DateTime Max(DateTime a, DateTime b) => a > b ? a : b;

    private async Task CheckFreeSpaceAsync(WorktreeCleanupOptions options)
    {
        long free;
        try { free = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(AppContext.BaseDirectory)) ?? "/").AvailableFreeSpace; }
        catch (Exception) { return; }

        var freeGb = free / (1024L * 1024 * 1024);
        if (freeGb >= options.MinFreeGb) return;
        if (DateTime.UtcNow - _lastAlertUtc < TimeSpan.FromHours(options.AlertCooldownHours)) return;
        _lastAlertUtc = DateTime.UtcNow;

        log.LogWarning("На корневом диске свободно {Free} ГБ (порог {Min} ГБ)", freeGb, options.MinFreeGb);
        foreach (var admin in users.GetAll().Where(u => u.Role == "admin"))
        {
            try
            {
                await notifications.SendSystemEventAsync(admin.Id,
                    $"Мало места на диске: свободно {freeGb} ГБ",
                    $"Свободно {freeGb} ГБ при пороге {options.MinFreeGb} ГБ. Обычно это сборки в worktree " +
                    "(bin/obj) и временные каталоги тестов в /tmp: ночная уборка чистит их по расписанию, " +
                    "а сейчас можно убрать вручную или закрыть неактивные worktree.",
                    kind: "alert", type: "disk_low", url: "", tag: "Система", sendPush: true);
            }
            catch (Exception ex) { log.LogError(ex, "Не удалось разослать тревогу о месте"); }
        }
    }

    private void WriteJournal(PassReport r, int unreadable = 0)
    {
        var gb = r.FreedBytes / 1024.0 / 1024 / 1024;
        var line = r.Aborted is not null
            ? $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} проход прерван: {r.Aborted}"
            : $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} worktree: просмотрено {r.Scanned}, очищено {r.Cleaned}, " +
              $"снято {r.Removed}, пропущено {r.Skipped}; /tmp: удалено {r.Tmp.Removed}" +
              (r.Tmp.Failed > 0 ? $" (не удалось {r.Tmp.Failed})" : "") +
              $"; освобождено {gb:F1} ГБ" + (unreadable > 0 ? $"; процессов с нечитаемым cwd: {unreadable}" : "");
        log.LogInformation("Уборка worktree: {Line}", line);
        try
        {
            var dir = ClaudeHomeServer.Services.Composition.SubsystemHostingExtensions.ResolveDataDir(config);
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "worktree-cleanup.log"), line + Environment.NewLine);
        }
        catch (Exception ex) { log.LogDebug(ex, "Журнал уборки не записан"); }
    }
}
