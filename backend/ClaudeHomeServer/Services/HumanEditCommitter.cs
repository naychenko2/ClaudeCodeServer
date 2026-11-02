using System.Collections.Concurrent;
using ClaudeHomeServer.Services.Composition;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.Git;

namespace ClaudeHomeServer.Services;

// Журнал правок человека в общем проекте (S3-3): буфер «путь → автор, время» и дебаунс-коммит.
// Вызывающая сторона сообщает Record(...) на каждую запись файла человеком; после паузы без
// новых записей по пути (HumanEdit:DebounceSeconds) в git уходит коммит ТОЛЬКО записанных
// путей (pathspec) с автором-человеком. StageAll запрещён: он подхватил бы незакоммиченную
// работу агента в том же дереве.
//
// Принятые допущения (согласованы в разрезе шага):
// - рестарт процесса теряет буфер: файлы остаются незакоммиченными в дереве, их подберёт
//   следующий коммит (человека или автокоммит хода) — но уже без атрибуции;
// - если агент правит тот же файл между сохранением человеком и коммитом, правка агента
//   уходит с автором-человеком: журнал знает путь, а не содержимое;
// - сбой git при коммите журнал не повторяет: запись снимается, ошибка — в лог.
//
// Конкурентность: буфер — ConcurrentDictionary, запись по пути заменяется целиком новым
// объектом (ссылочное равенство). Срабатывание «проверка-и-удаление» делает TryRemove по паре
// «ключ + значение, которое видели»: если между осмотром и удалением пришёл новый Record по
// тому же пути, удаление не пройдёт и новая запись (с её автором) доживёт до своего окна.
// Замок репозитория (GitService.LockFor) держится только на время самого git, не на ожидание
// дебаунса. На время коммита записи живут в _flushing — PendingAuthor не теряет путь между
// снятием из буфера и появлением коммита.
public sealed class HumanEditCommitter(
    ProjectManager projects,
    UserStore users,
    GitService git,
    IConfiguration config,
    ILogger<HumanEditCommitter> logger,
    TimeProvider? clock = null) : IHostedService, IHumanEditJournal
{
    private const int DefaultDebounceSeconds = 20;
    private const int MaxNamesInMessage = 5;

    // Запись буфера — класс, а не record: TryRemove(KeyValuePair) сравнивает значения, и
    // value-equality приняла бы за «ту же» новую запись с совпавшим временем.
    private sealed class Pending(string projectId, string root, string relPath, string userId, DateTimeOffset at)
    {
        public string ProjectId { get; } = projectId;
        public string Root { get; } = root;
        public string RelPath { get; } = relPath;
        public string UserId { get; } = userId;
        public DateTimeOffset At { get; } = at;
    }

    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly ConcurrentDictionary<(string Root, string Rel), Pending> _pending = new();
    private readonly ConcurrentDictionary<(string Root, string Rel), Pending> _flushing = new();
    // Один сброс за раз: сторож цикла и StopAsync не гоняют коммиты параллельно.
    private readonly SemaphoreSlim _flushGate = new(1, 1);
    // Тестовый шов гонки: вызывается между осмотром записи и её атомарным снятием.
    internal Action<(string Root, string Rel)>? BeforeClaim { get; set; }
    private CancellationTokenSource? _loopCts;
    private Task? _loop;

    private TimeSpan Debounce => TimeSpan.FromSeconds(Math.Max(1, config.GetValue("HumanEdit:DebounceSeconds", DefaultDebounceSeconds)));

    public void Record(string projectId, string root, string relPath, string userId)
    {
        if (string.IsNullOrEmpty(projectId) || string.IsNullOrEmpty(root)
            || string.IsNullOrEmpty(relPath) || string.IsNullOrEmpty(userId)) return;
        var project = projects.GetById(projectId);
        // Только общий проект на серверных файлах и только git-репозиторий: личный проект
        // и каталог без репозитория — молчаливый no-op.
        if (project is null || project.Members is not { Count: > 0 }) return;
        if (!ProjectCapabilities.FilesOnServer(project)) return;
        if (!git.IsGitRepo(root)) return;
        // Корень обязан быть корнем самого проекта (или лежать под ним): иначе журнал закоммитил
        // бы чужой репозиторий от имени владельца проекта.
        if (!IsUnderProjectRoot(root, project.RootPath)) return;
        // Автор — владелец или участник проекта; чужой userId — fail-closed.
        if (userId != project.OwnerId && !project.Members.Any(m => m.UserId == userId)) return;
        string full;
        try { full = SafePath.JoinBelowRoot(root, relPath); }
        catch { return; }
        // Каталог в pathspec индексирует всё поддерево, включая незакоммиченную работу агента.
        // Удалённый файл (путь не существует) допустим — удаление тоже правка.
        if (Directory.Exists(full)) return;

        var rel = relPath.Replace('\\', '/');
        _pending[(root, rel)] = new Pending(projectId, root, rel, userId, _clock.GetUtcNow());
    }

    private static bool IsUnderProjectRoot(string root, string projectRoot)
    {
        if (string.IsNullOrEmpty(projectRoot)) return false;
        var r = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var p = Path.TrimEndingDirectorySeparator(Path.GetFullPath(projectRoot));
        return r.Equals(p, StringComparison.Ordinal)
            || r.StartsWith(p + Path.DirectorySeparatorChar, StringComparison.Ordinal);
    }

    public HumanEditEntry? PendingAuthor(string root, string relPath)
    {
        var key = (root, relPath.Replace('\\', '/'));
        // Порядок важен: при сбросе запись сначала попадает в _flushing и лишь потом уходит
        // из _pending — в этом порядке чтения путь не пропадает ни на мгновение.
        var p = _pending.TryGetValue(key, out var a) ? a : _flushing.TryGetValue(key, out var b) ? b : null;
        return p is null ? null : new HumanEditEntry(p.UserId, p.At);
    }

    public Task StartAsync(CancellationToken ct)
    {
        _loopCts = new CancellationTokenSource();
        var token = _loopCts.Token;
        _loop = Task.Run(() => LoopAsync(token), CancellationToken.None);
        return Task.CompletedTask;
    }

    // Остановка сбрасывает буфер коммитом: ожидающие окна не ждём.
    public async Task StopAsync(CancellationToken ct)
    {
        if (_loopCts is not null)
        {
            await _loopCts.CancelAsync();
            if (_loop is not null)
            {
                try { await _loop.WaitAsync(ct); }
                catch (OperationCanceledException) { }
            }
        }
        try { await FlushAsync(all: true, ct); }
        catch (OperationCanceledException) { }
    }

    private async Task LoopAsync(CancellationToken token)
    {
        var interval = TimeSpan.FromSeconds(Math.Clamp(Debounce.TotalSeconds / 4, 1, 10));
        using var timer = new PeriodicTimer(interval);
        try
        {
            while (await timer.WaitForNextTickAsync(token))
            {
                try { await FlushAsync(all: false, token); }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                catch (Exception ex) { logger.LogWarning(ex, "Сброс правок человека не удался"); }
            }
        }
        catch (OperationCanceledException) { }
    }

    // all=false — только созревшие (после окна дебаунса), all=true — весь буфер (остановка).
    internal async Task FlushAsync(bool all, CancellationToken ct = default)
    {
        await _flushGate.WaitAsync(ct);
        try
        {
            var now = _clock.GetUtcNow();
            var debounce = Debounce;
            var claimed = new List<KeyValuePair<(string Root, string Rel), Pending>>();
            foreach (var kv in _pending)
            {
                if (!all && now - kv.Value.At < debounce) continue;
                // Сначала маркер «в полёте», затем атомарное снятие именно этой записи.
                BeforeClaim?.Invoke(kv.Key);
                _flushing[kv.Key] = kv.Value;
                if (_pending.TryRemove(kv)) claimed.Add(kv);
                else _flushing.TryRemove(kv);
            }

            foreach (var group in claimed.GroupBy(c => (c.Value.Root, c.Value.ProjectId, c.Value.UserId)))
            {
                try { await CommitGroupAsync(group.Key.Root, group.Key.ProjectId, group.Key.UserId, group.Select(g => g.Value.RelPath).ToList()); }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Коммит правок человека в {Root} не удался ({Count} путей)", group.Key.Root, group.Count());
                }
                finally
                {
                    foreach (var c in group) _flushing.TryRemove(c);
                }
            }
        }
        finally { _flushGate.Release(); }
    }

    private async Task CommitGroupAsync(string root, string projectId, string userId, List<string> paths)
    {
        // Состояние проекта могло измениться за окно дебаунса (участников убрали, репозиторий
        // удалили) — проверяем заново, лишних коммитов не делаем.
        var project = projects.GetById(projectId);
        if (project is null || project.Members is not { Count: > 0 } || !git.IsGitRepo(root)) return;
        var user = users.GetById(userId);
        if (user is null)
        {
            logger.LogWarning("Правки человека в {Root}: пользователь {User} не найден, коммит пропущен", root, userId);
            return;
        }

        var author = new GitCommitAuthor(userId, user.DisplayName ?? user.Username, user.Username);
        paths.Sort(StringComparer.Ordinal);
        var names = string.Join(", ", paths.Take(MaxNamesInMessage));
        var subject = paths.Count > MaxNamesInMessage
            ? $"Правка: {names} и ещё {paths.Count - MaxNamesInMessage}"
            : $"Правка: {names}";
        await git.CommitAsync(project.OwnerId, root, subject, author: author, paths: paths);
    }
}
