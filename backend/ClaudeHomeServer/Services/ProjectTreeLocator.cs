using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.Execution;
using ClaudeHomeServer.Services.Git;

namespace ClaudeHomeServer.Services;

// Корень, который рассматривает локатор: абсолютный путь дерева, ветка, на которой оно стоит
// (null для основного корня и непроверенного анкера), и признак анкера — дерева, заявленного
// чатом или задачей.
public sealed record TreeRoot(string Path, string? Branch, bool IsAnchor);

// Найденный относительный путь: из какого корня, полный путь, ветка и является ли корень
// анкером. «из дерева <ветка>» в интерфейсе строится именно из branch/isAnchor: фолбэк
// есть, но он не тихий (мотив W4-LINK-B).
public sealed record TreeHit(string Root, string FullPath, string? Branch, bool IsAnchor);

// Единственная точка резолвинга относительных путей по всем worktree проекта (фича
// «Резолвинг ссылок по worktree», шаг B1b). Потребители — GetContent/Stream/Locate в
// FilesController (шаги B2a/B2b): «заявленное дерево» чата или задачи приходит сюда анкером —
// первым корнем списка. Само правило TreeRootForAsync здесь НЕ дублируется: проверка
// дерева идёт тем же примитивом GitService, а список worktree берётся только из
// WorktreeListAsync (кешированный путь).
//
// Смена решения W4-LINK-B, осознанная: анкер заявлен, но его дерева нет (сносили после
// мержа) — не 404, а поиск по основному дереву и остальным с меткой isAnchor=false.
// Главный сценарий фичи — чат задачи после мержа: закоммиченные отчёты уже в main.
public sealed class ProjectTreeLocator
{
    private readonly GitService? _git;
    private readonly ILauncherFactory _launchers;
    private readonly ILogger<ProjectTreeLocator> _log;

    // Git — отключаемая подсистема: без неё локатор деградирует до прежнего поведения —
    // анкер и корень, без поиска по worktree (fail-closed, без 500). Дефолт null обязателен:
    // при Subsystems:git:Enabled=false сервиса в контейнере нет, и резолв без значения
    // по умолчанию бросает InvalidOperationException (как в FilesController).
    public ProjectTreeLocator(ILauncherFactory launchers, ILogger<ProjectTreeLocator> log, GitService? git = null)
    {
        _git = git;
        _launchers = launchers;
        _log = log;
    }

    // Упорядоченный список корней: анкер → основное дерево → остальные worktree. Корень
    // принимается только если (а) <wt>/.git — файл и его gitdir: лежит под
    // <main>/.git/worktrees/ (список worktree — данные, которыми правит агент: ссылку
    // можно отредактировать, и git отдаст произвольный путь), и (б) путь внутри
    // монтирований владельца (для container — ToRuntime не бросает). Prunable и
    // отсутствующие на диске — мимо. Сбой git, не-репо или нет Git → [анкер?, RootPath].
    public async Task<IReadOnlyList<TreeRoot>> RootsAsync(
        string? owner, Project project, string? anchor = null, CancellationToken ct = default)
    {
        // Локальный проект: файлы на устройстве, серверный git его путей не знает.
        if (!ProjectCapabilities.FilesOnServer(project))
            return [new TreeRoot(project.RootPath, null, false)];

        var main = Path.GetFullPath(project.RootPath);
        var mainNorm = PathNormalizer.NormalizePath(main);

        IReadOnlyList<GitWorktreeInfo>? list = null;
        if (_git is not null)
        {
            try
            {
                list = await _git.WorktreeListAsync(owner, project.RootPath, useCache: true, ct);
            }
            catch (Exception e)
            {
                _log.LogWarning("ProjectTreeLocator: git worktree list не ответил ({msg}); читаю только анкер и корень", e.Message);
                list = null;
            }
        }

        var roots = new List<TreeRoot>();
        string? anchorNorm = null;

        // Анкер — контекст автора ссылки. Равен корню — это сам корень; заявлен, но его
        // нет в списке проверенного git — он выпадает, поиск продолжается (см. шапку).
        if (!string.IsNullOrWhiteSpace(anchor))
        {
            string a;
            try { a = Path.GetFullPath(anchor.Trim()); } catch { a = ""; }
            if (a.Length > 0)
            {
                a = Path.GetFullPath(a);
                anchorNorm = PathNormalizer.NormalizePath(a);
                if (anchorNorm == mainNorm)
                {
                    roots.Add(new TreeRoot(main, null, true));
                }
                else if (Directory.Exists(a) && IsLinkedWorktree(owner, a, main))
                {
                    // Тот же критерий, что у элементов списка ниже: путь анкера должен
                    // быть в монтированиях владельца — иначе чтение внутри корня бросает.
                    bool inMounts;
                    try { _launchers.ForOwner(owner).Paths.ToRuntime(a); inMounts = true; }
                    catch { inMounts = false; }

                    var entry = inMounts
                        ? list?.FirstOrDefault(w => PathNormalizer.NormalizePath(w.Path) == anchorNorm)
                        : null;
                    // list == null и анкер не бросил — сверить нечем (нет Git / сбой): держим как есть.
                    // Git работает и анкера в списке нет, или путь вне монтирований — не корень.
                    if (inMounts && (list is null || entry is not null))
                        roots.Add(new TreeRoot(a, entry?.Branch, true));
                    else
                        anchorNorm = null;
                }
            }
        }

        // Основное дерево — всегда после анкера (дедуп: анкер == корень уже добавлен).
        if (anchorNorm != mainNorm) roots.Add(new TreeRoot(main, null, false));

        if (list is not null)
        {
            foreach (var w in list)
            {
                if (w.Prunable) continue;
                string path;
                try { path = Path.GetFullPath(w.Path); } catch { continue; }
                var norm = PathNormalizer.NormalizePath(path);
                if (norm == mainNorm || norm == anchorNorm) continue;
                if (!Directory.Exists(path) || !IsLinkedWorktree(owner, path, main)) continue;
                // Путь вне монтирований владельца — не наше дерево: catch
                // «hostPath = path» из WorktreeListAsync здесь не наследуется.
                try { _launchers.ForOwner(owner).Paths.ToRuntime(path); } catch { continue; }
                roots.Add(new TreeRoot(path, w.Branch, false));
            }
        }

        return roots;
    }

    // Поиск относительного пути по корням: анкер → main → остальные (среди остальных —
    // самый свежий mtime). Абсолютный rel отклоняется до цикла: SafePath.Join на Linux
    // молча делает его относительным, и проверка «ссылка наружу» исчезает. Внутри корня —
    // только SafePath.Join, своей склейки нет.
    public async Task<TreeHit?> LocateAsync(
        string? owner, Project project, string rel, string? anchor = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(rel) || Path.IsPathRooted(rel)) return null;

        var mainNorm = PathNormalizer.NormalizePath(project.RootPath);
        TreeHit? bestOther = null;
        DateTimeOffset bestMtime = default;

        foreach (var root in await RootsAsync(owner, project, anchor, ct))
        {
            string full;
            try { full = SafePath.Join(root.Path, rel); }
            catch (UnauthorizedAccessException) { continue; }
            if (!File.Exists(full)) continue;

            if (root.IsAnchor) return new TreeHit(root.Path, full, root.Branch, true);
            if (PathNormalizer.NormalizePath(root.Path) == mainNorm)
                return new TreeHit(root.Path, full, root.Branch, false);

            // Среди чужих деревьев коллизия редка (материалы именуются с датой), но если
            // она есть — берём свежайший файл; риск «не тот файл» гасит чип ветки.
            var mtime = new FileInfo(full).LastWriteTimeUtc;
            if (bestOther is null || mtime > bestMtime)
            {
                bestOther = new TreeHit(root.Path, full, root.Branch, false);
                bestMtime = mtime;
            }
        }

        return bestOther;
    }

    // Признак настоящего linked-worktree этой репы: <wt>/.git — файл (в главной репе
    // .git — директория), и его gitdir: лежит под <main>/.git/worktrees/. gitdir пишется
    // в среде владельца — маппим на хост; немаппящийся путь — отказ.
    private bool IsLinkedWorktree(string? owner, string treePath, string mainRoot)
    {
        var dotGit = Path.Combine(treePath, ".git");
        if (Directory.Exists(dotGit) || !File.Exists(dotGit)) return false;

        string? gitdir = null;
        try
        {
            foreach (var line in File.ReadAllLines(dotGit))
            {
                if (line.StartsWith("gitdir: "))
                {
                    gitdir = line["gitdir: ".Length..].Trim();
                    break;
                }
            }
        }
        catch { return false; }
        if (gitdir is null) return false;

        try { gitdir = _launchers.ForOwner(owner).Paths.ToHost(gitdir); } catch { return false; }

        var worktreesDir = Path.GetFullPath(Path.Combine(mainRoot, ".git", "worktrees"));
        var g = Path.GetFullPath(gitdir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        // Регистронезависимость — по правилам ФС хоста (S8 F2-15), не OrdinalIgnoreCase.
        var comparison = SafePath.ComparisonFor(OperatingSystem.IsWindows());
        return g.Equals(worktreesDir, comparison) ||
            g.StartsWith(worktreesDir + Path.DirectorySeparatorChar, comparison);
    }
}
