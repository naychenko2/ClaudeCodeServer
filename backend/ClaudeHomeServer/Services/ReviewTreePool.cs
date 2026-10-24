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
    // Пути слотов, которые прямо сейчас сносятся: Reserve их не отдаёт, пока дерево не убрано,
    // иначе снос с --force унёс бы дерево у задачи, взявшей путь в окне после снятия аренды.
    private readonly HashSet<string> _removing = new(StringComparer.Ordinal);

    // Признак «слот наш» — файл-маркер рядом со слотом (не внутри: clean -fd его бы стёр, а git status
    // показывал бы ревьюеру лишний файл). Переживает «Стоп» и рестарт сервера; по имени слот не узнать:
    // у дерева чата на ветке может быть похожее имя.
    internal const string MarkerSuffix = ".cc-review-slot";
    private static string MarkerPath(string slotPath) => slotPath + MarkerSuffix;

    // Швы для теста: после снимка деревьев в RemoveSlotsOfWaveAsync / RemoveSlotsOfProjectAsync
    // и в окне между снятием аренды и сносом дерева слота
    internal Func<Task>? AfterWaveSnapshot { get; set; }
    internal Func<Task>? AfterProjectSnapshot { get; set; }
    internal Func<Task>? BeforeSlotRemove { get; set; }

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

    // «/» → «~»: в имени ветки git «~» запрещён, поэтому отображение однозначно. Через «-» ветки
    // «a/b» и «a-b» давали один путь слота, и снос слотов одной волны уносил слот другой.
    internal static string SlotPrefix(string branch) => $"{branch.Replace('/', '~')}-review-";

    // Кодирование до дельты-2: «/» → «-»
    internal static string LegacySlotPrefix(string branch) => $"{branch.Replace('/', '-')}-review-";

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
        var prefix = SlotPrefix(branch);

        // Слот на ветке без маркера — дерево чата/волны или слот, созданный до маркера, в котором заветвились:
        // опознать его нельзя, поэтому чиним только слоты с маркером, а такой пропускаем и берём следующий
        var skipped = new HashSet<string>(StringComparer.Ordinal);
        string slotPath;
        while (true)
        {
            var reserved = Reserve(slotBase, prefix, taskId, skipped);
            if (reserved is null) return null;
            slotPath = reserved;

            try
            {
                // Путь ГЛАЗАМИ CLI считаем ДО создания дерева: у container-пользователя ToRuntime
                // отвергает путь вне монтирований, а исключение после worktree add оставило бы сироту
                _ = _cwdForOwner(ownerId, slotPath);

                var slotNorm = PathNormalizer.NormalizePath(slotPath);
                var existing = worktrees.FirstOrDefault(w => PathNormalizer.NormalizePath(w.Path) == slotNorm);
                if (existing is not null)
                {
                    var hasMarker = File.Exists(MarkerPath(slotPath));
                    if (existing.Branch is not null && !hasMarker)
                    {
                        skipped.Add(slotPath);
                        ReleasePath(slotPath, taskId);
                        continue;
                    }
                    // Свой слот, в котором ревьюер заветвился в прошлый раз, возвращаем в detached
                    await _git.ResetReviewWorktreeAsync(ownerId, project.RootPath, slotPath, sha, hasMarker, ct);
                }
                else
                {
                    // Прогрев (WorktreeBuildWarmup) для слота намеренно не зовём: слот живёт между ревью и
                    // obj/ переживает сброс, так что холодна только первая сборка в свежем слоте (60–90 с, один
                    // раз на слот); прогрев принадлежит SessionManager, а тащить его в пул ради этого — лишний
                    // шов и лишняя тяжёлая сборка при каждом заведении слота под гейтом OOM.
                    Directory.CreateDirectory(slotBase);
                    await _git.WorktreeAddDetachedAsync(ownerId, project.RootPath, slotPath, sha, ct);
                }
                File.WriteAllText(MarkerPath(slotPath), branch);
                LinkNodeModules(ownerId, project.RootPath, slotPath);
            }
            catch
            {
                Release(taskId);
                throw;
            }
            break;
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

    // Снимает аренду одного пути, если она у этой задачи: остальные слоты задачи не трогаем
    private void ReleasePath(string slotPath, string taskId)
    {
        lock (_gate)
        {
            if (_leases.TryGetValue(slotPath, out var holder) && holder == taskId) _leases.Remove(slotPath);
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
        if (AfterWaveSnapshot is { } hook) await hook();
        var waveNorm = PathNormalizer.NormalizePath(waveTreePath);
        var branch = worktrees.FirstOrDefault(w => PathNormalizer.NormalizePath(w.Path) == waveNorm)?.Branch;
        var slotBase = _worktreesBase(ownerId, project);

        if (string.IsNullOrEmpty(branch))
        {
            // Регистрация дерева пропала: ветку не узнать, а имя каталога уже с «-» вместо «/». Слоты волны
            // ищем среди зарегистрированных по тому же имени с точностью до «~» ↔ «-»
            var dirName = Path.GetFileName(Path.TrimEndingDirectorySeparator(waveTreePath));
            var baseNorm = PathNormalizer.NormalizePath(slotBase);
            // Имя каталога выведено из пути дерева вслепую: слот, чей префикс законно принадлежит другой
            // зарегистрированной волне (ветка «feat-wave» при сносе дерева «feat/wave»), не наш
            var claimed = worktrees.Where(w => !string.IsNullOrEmpty(w.Branch))
                .SelectMany(w => new[] { SlotPrefix(w.Branch!), LegacySlotPrefix(w.Branch!) })
                .ToHashSet(StringComparer.Ordinal);
            var fallback = worktrees
                .Where(w => PathNormalizer.NormalizePath(Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(w.Path)) ?? "") == baseNorm)
                .Select(w => Path.GetFileName(Path.TrimEndingDirectorySeparator(w.Path)))
                .Where(n => IsSlotName(n) && Enumerable.Range(1, SlotsPerWave)
                        .Any(i => n.Replace('~', '-') == $"{dirName}-review-{i}")
                    && !claimed.Any(c => Enumerable.Range(1, SlotsPerWave).Any(i => n == c + i)))
                .ToList();
            // В маркере — имя ветки («feat/wave»), а каталог дерева уже с «-»: сравниваем с точностью до «/» ↔ «-»
            foreach (var name in fallback)
                await RemoveSlotAsync(ownerId, project, Path.Combine(slotBase, name), ct,
                    ownMarker: name.Contains('~') ? null : m => m.Replace('/', '-') == dirName);
            return;
        }

        // Слоты, созданные до смены кодирования, лежат под именем с «-»: сносим оба префикса
        // — кроме случая, когда это же имя законно принадлежит другой живой волне (ветка «a-b» при сносе «a/b»)
        var newPrefix = SlotPrefix(branch);
        var legacy = LegacySlotPrefix(branch);
        var prefixes = new List<string> { newPrefix };
        if (legacy != newPrefix
            && !worktrees.Any(w => PathNormalizer.NormalizePath(w.Path) != waveNorm
                && !string.IsNullOrEmpty(w.Branch) && SlotPrefix(w.Branch) == legacy))
            prefixes.Add(legacy);

        foreach (var prefix in prefixes)
            for (var n = 1; n <= SlotsPerWave; n++)
                // Путь с «-» мог достаться ревью другой ветки («a/b» до обновления): трогаем его, только если он не
                // чужой — см. RemoveSlotAsync.ownMarker
                await RemoveSlotAsync(ownerId, project, Path.Combine(slotBase, prefix + n), ct,
                    ownMarker: prefix.Contains('~') ? null : m => m == branch);
    }

    // Все слоты проекта: detached-дерево в базе деревьев проекта с именем «*-review-1|2»
    public async Task RemoveSlotsOfProjectAsync(string? ownerId, Project project, CancellationToken ct = default)
    {
        ProjectCapabilityGuard.EnsureAllowed(project, ProjectCapabilityArea.FileBound);

        var baseNorm = PathNormalizer.NormalizePath(_worktreesBase(ownerId, project));
        var slotBase = _worktreesBase(ownerId, project);
        // Состав слотов перечитываем до сходимости: слот, взятый после снимка, иначе остался бы жить
        // с арендой (минимум два чтения). Уже обработанные имена в повторный проход не идут — не снесённый слот не зациклит.
        var done = new HashSet<string>(StringComparer.Ordinal);
        for (var pass = 0; ; pass++)
        {
            var worktrees = await _git.WorktreeListAsync(ownerId, project.RootPath, ct);
            if (pass == 0 && AfterProjectSnapshot is { } hook) await hook();
            var names = worktrees
                .Where(w => PathNormalizer.NormalizePath(Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(w.Path)) ?? "") == baseNorm)
                .Select(w => Path.GetFileName(Path.TrimEndingDirectorySeparator(w.Path)))
                .Where(n => IsSlotName(n) && done.Add(n))
                .ToList();
            // Первый проход пустым не считается: снимок мог устареть, нужно хотя бы одно перечитывание
            if (names.Count == 0 && pass > 0) break;
            foreach (var name in names)
                await RemoveSlotAsync(ownerId, project, Path.Combine(slotBase, name), ct);
        }
    }

    // После рестарта учёт аренд пуст, а чаты открытых ревью живут в своих слотах: без
    // восстановления вторая задача взяла бы слот ревьюера и сбросила его под ногами.
    // Закрытые задачи и задачи с остановленным исполнителем аренду не получают — их слот свободен
    // (на «Стопе» аренда снимается, поэтому после рестарта поведение то же). Исключение — пауза
    // фичи (feature_held): продолжение идёт в тот же чат, чей cwd — этот слот, поэтому аренда
    // живёт, пока жив чат. Терминальный feature_held_expired (чат удалён) под исключение не попадает.
    public async Task RestoreLeasesAsync()
    {
        // Старт сервера не должен зависеть от восстановления аренд: сбой — в лог, дальше без аренд
        try
        {
            var trees = new Dictionary<(string?, string), IReadOnlyList<GitWorktreeInfo>>();
            foreach (var task in _allTasks().ToList())
            {
                try { await RestoreLeaseAsync(task, trees); }
                catch (Exception ex)
                {
                    _log.LogWarning(ex, "Аренда слота задачи {TaskId} не восстановлена", task.Id);
                }
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Аренды слотов ревью не восстановлены: не прочитан список задач");
        }
    }

    private async Task RestoreLeaseAsync(TaskItem task, Dictionary<(string?, string), IReadOnlyList<GitWorktreeInfo>> trees)
    {
        if (task.Role != TaskRoles.Review || task.Status == TaskItemStatus.Done
            || (task.ExecutorStoppedAt is not null && task.ExecutorStopReason != ExecutorStopClassifier.FeatureHeldReason)
            || task.LinkedSessionId is null || task.ProjectId is null) return;
        if (_projectById(task.ProjectId) is not { } project || !ProjectCapabilities.FilesOnServer(project)) return;
        if (_sessionWorktree(task.LinkedSessionId) is not { } wt) return;

        var slotBase = _worktreesBase(task.OwnerId, project);
        var trimmed = Path.TrimEndingDirectorySeparator(wt);
        var name = Path.GetFileName(trimmed);
        if (!IsSlotName(name)
            || PathNormalizer.NormalizePath(Path.GetDirectoryName(trimmed) ?? "") != PathNormalizer.NormalizePath(slotBase))
            return;

        var slotPath = Path.Combine(slotBase, name);
        // Дерево чата или волны на ветке с таким именем каталога слотом пула не является: аренду ему не ставим
        // (иначе RemoveSlotAsync сочло бы его своим и снесло с --force) и маркер не пишем. Сбой чтения базы
        // деревьев или незарегистрированный путь — аренда как раньше: безопаснее лишний раз занять слот
        var hasMarker = File.Exists(MarkerPath(slotPath));
        var detached = false;
        if (!hasMarker)
        {
            try
            {
                var key = (task.OwnerId, project.RootPath);
                if (!trees.TryGetValue(key, out var list))
                    trees[key] = list = await _git.WorktreeListAsync(task.OwnerId, project.RootPath);
                var norm = PathNormalizer.NormalizePath(slotPath);
                var registered = list.FirstOrDefault(w => PathNormalizer.NormalizePath(w.Path) == norm);
                if (registered is { Branch: not null }) return;
                detached = registered is not null;
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Дерево слота ревью {Slot} не опознано", slotPath);
            }
        }
        lock (_gate) _leases.TryAdd(slotPath, task.Id);
        // Маркер — только слоту пула: detached-дерево, зарегистрированное по этому пути
        if (!detached) return;
        try { File.WriteAllBytes(MarkerPath(slotPath), []); }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Маркер слота ревью {Slot} не записан", slotPath);
        }
    }

    private static bool IsSlotName(string name)
    {
        for (var n = 1; n <= SlotsPerWave; n++)
            if (name.Length > 8 && name.EndsWith("-review-" + n, StringComparison.Ordinal)) return true;
        return false;
    }

    // ownMarker != null — путь неоднозначный (имя с «-»: слот ветки «a-b» или старый слот «a/b»). Слот не наш и
    // не сносится, если в маркере записано то, что ownMarker не признаёт, или он в аренде — при любом маркере:
    // новый код слот «a/b» под «-» не занимает, так что держит его ревью другой волны или старый слот с живым
    // ревью (ветка неизвестна). Аренда в этом случае остаётся.
    private async Task RemoveSlotAsync(string? ownerId, Project project, string slotPath, CancellationToken ct,
        Func<string, bool>? ownMarker = null)
    {
        // Аренда снимается и путь закрывается от Reserve одним входом под замком: между проверкой и
        // снятием, а затем и до конца сноса путь другой задаче не достанется. Список деревьев читаем
        // уже после этого — снимок вызывающего мог устареть.
        bool ours;
        lock (_gate)
        {
            if (ownMarker is not null)
            {
                string? marker = null;
                try { if (File.Exists(MarkerPath(slotPath))) marker = File.ReadAllText(MarkerPath(slotPath)).Trim(); }
                catch (IOException) { /* нечитаемый маркер = пустой */ }
                var held = _leases.ContainsKey(slotPath);
                if (held || (!string.IsNullOrEmpty(marker) && !ownMarker(marker))) return;
            }
            var leased = _leases.Remove(slotPath);
            ours = File.Exists(MarkerPath(slotPath)) || leased;
            _removing.Add(slotPath);
        }
        try
        {
            if (BeforeSlotRemove is { } hook) await hook();
            var worktrees = await _git.WorktreeListAsync(ownerId, project.RootPath, ct);
            var norm = PathNormalizer.NormalizePath(slotPath);
            var found = worktrees.FirstOrDefault(w => PathNormalizer.NormalizePath(w.Path) == norm);
            if (found is null) { DeleteMarker(slotPath); return; }
            // Слот — detached; дерево на ветке (чата, волны) с похожим именем слотом не является.
            // Исключение — наш слот (в аренде или с маркером): ревьюер мог заветвиться в нём
            if (found.Branch is not null && !ours) return;
            try
            {
                await _git.WorktreeRemoveAsync(ownerId, project.RootPath, slotPath, force: true, ct);
                // Маркер снимаем только после сноса: не сошлось — слот остаётся опознанным для следующей уборки
                DeleteMarker(slotPath);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.LogWarning(ex, "Слот ревью {Slot} не снесён", slotPath);
            }
        }
        finally
        {
            lock (_gate) _removing.Remove(slotPath);
        }
    }

    private void DeleteMarker(string slotPath)
    {
        try { File.Delete(MarkerPath(slotPath)); }
        catch (Exception ex) { _log.LogWarning(ex, "Маркер слота ревью {Slot} не удалён", slotPath); }
    }

    private string? Reserve(string slotBase, string prefix, string taskId, ISet<string> skipped)
    {
        lock (_gate)
        {
            for (var n = 1; n <= SlotsPerWave; n++)
            {
                var path = Path.Combine(slotBase, prefix + n);
                if (!skipped.Contains(path) && _leases.TryGetValue(path, out var holder) && holder == taskId) return path;
            }
            for (var n = 1; n <= SlotsPerWave; n++)
            {
                var path = Path.Combine(slotBase, prefix + n);
                if (skipped.Contains(path) || _leases.ContainsKey(path) || _removing.Contains(path)) continue;
                _leases[path] = taskId;
                return path;
            }
            return null;
        }
    }

    // Как scripts/worktree-setup.sh: node_modules в worktree пуст, зависимости лежат в основном
    // checkout. Реальную папку и уже стоящую ссылку не трогаем; нет источника — молча пропускаем.
    // Цель ссылки — путь глазами CLI владельца (у container-владельца хостового корня в песочнице нет).
    private void LinkNodeModules(string? ownerId, string mainRoot, string slotPath)
    {
        try
        {
            var src = Path.Combine(mainRoot, "frontend", "node_modules");
            var dst = Path.Combine(slotPath, "frontend", "node_modules");
            if (!Directory.Exists(src) || !Directory.Exists(Path.GetDirectoryName(dst)!)) return;
            if (Directory.Exists(dst) || new DirectoryInfo(dst).LinkTarget is not null) return;
            Directory.CreateSymbolicLink(dst, _cwdForOwner(ownerId, src));
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Не удалось связать node_modules в слоте ревью {Slot}", slotPath);
        }
    }
}
