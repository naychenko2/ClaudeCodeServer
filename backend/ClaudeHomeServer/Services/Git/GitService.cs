using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.Execution;

namespace ClaudeHomeServer.Services.Git;

// Результат запуска git-команды.
public sealed record GitResult(int ExitCode, string Stdout, string Stderr)
{
    public bool Ok => ExitCode == 0;
}

// git-фейл с текстом stderr — контроллер превращает его в 409/400.
public sealed class GitCommandException(string message) : Exception(message);

// Креды HTTP-remote (Forgejo): логин + персональный токен пользователя
public sealed record GitCredentials(string Username, string Token);

// Единая точка ЛОКАЛЬНЫХ git-операций над рабочим деревом проекта.
// Запуск — через слой Execution (ILauncherFactory.ForOwner): для container-пользователей
// git исполняется внутри песочницы cc-sandbox с маппингом путей, для local — на хосте.
// Работа с remote (Forgejo) вынесена в отдельный GitServerService.
public sealed class GitService(ILauncherFactory launchers)
{
    // Сериализация write-операций одного репозитория: git из UI, авто-коммит хода и
    // сессия Claude могут столкнуться на .git/index.lock. Чтение (status/log/diff) — без блокировки.
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _repoLocks = new();

    private const int DefaultTimeoutMs = 10_000;
    private const int NetworkTimeoutMs = 120_000;

    private SemaphoreSlim LockFor(string root) =>
        _repoLocks.GetOrAdd(root, _ => new SemaphoreSlim(1, 1));

    public static bool IsGitRepo(string root) => Path.Exists(Path.Combine(root, ".git"));

    // Конвенция проекта: все относительные пути — через SafeJoin (защита от traversal)
    // до передачи в git. Git и сам отвергает пути вне репо, но валидируем единообразно.
    private static string ValidateRel(string root, string relPath)
    {
        FileService.SafeJoinPublic(root, relPath);
        return relPath;
    }

    // Низкоуровневый запуск git. args передаются раздельно (ArgumentList — без shell,
    // защита от инъекций); stdin — для commit-сообщений и патчей (не через argv).
    public async Task<GitResult> RunAsync(
        string? ownerId, string root, IReadOnlyList<string> args,
        string? stdin = null, IReadOnlyDictionary<string, string>? env = null,
        int timeoutMs = DefaultTimeoutMs, CancellationToken ct = default)
    {
        var spec = new ProcessSpec
        {
            FileName = "git",
            Args = args,
            WorkingDirectory = root,
            Env = env,
            RedirectStdin = stdin is not null,
            // git выводит UTF-8; без явной кодировки .NET читает в системной (OEM/ANSI)
            // и кириллица в сообщениях коммитов превращается в кракозябры.
            StdioEncoding = new UTF8Encoding(false),
            TurnId = Guid.NewGuid().ToString("N"),
        };

        var launcher = launchers.ForOwner(ownerId);
        Process proc;
        try { proc = launcher.Start(spec); }
        catch (Exception ex) { throw new GitCommandException($"Не удалось запустить git: {ex.Message}"); }

        try
        {
            if (stdin is not null)
            {
                await proc.StandardInput.WriteAsync(stdin);
                proc.StandardInput.Close();
            }
            var stdoutTask = proc.StandardOutput.ReadToEndAsync(ct);
            var stderrTask = proc.StandardError.ReadToEndAsync(ct);

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(timeoutMs);
            try { await proc.WaitForExitAsync(timeoutCts.Token); }
            catch (OperationCanceledException)
            {
                launcher.Kill(proc, spec.TurnId);
                throw new GitCommandException("git не ответил вовремя (таймаут)");
            }

            var stdout = await stdoutTask;
            var stderr = await stderrTask;
            return new GitResult(proc.ExitCode, stdout, stderr);
        }
        finally { proc.Dispose(); }
    }

    // Бросает GitCommandException, если git завершился с ошибкой.
    private async Task<GitResult> RunOkAsync(
        string? ownerId, string root, IReadOnlyList<string> args,
        string? stdin = null, int timeoutMs = DefaultTimeoutMs, CancellationToken ct = default)
    {
        var r = await RunAsync(ownerId, root, args, stdin, timeoutMs: timeoutMs, ct: ct);
        if (!r.Ok)
            throw new GitCommandException(FirstLine(r.Stderr) ?? "git завершился с ошибкой");
        return r;
    }

    // ---------- Чтение (без блокировки) ----------

    public async Task<GitStatusDto> StatusAsync(string? ownerId, string root, CancellationToken ct = default)
    {
        if (!IsGitRepo(root))
            return new GitStatusDto(false, null, null, 0, 0, false, [], [], []);

        var r = await RunAsync(ownerId, root, ["status", "--porcelain=v2", "--branch", "-z"], ct: ct);
        if (!r.Ok)
            return new GitStatusDto(false, null, null, 0, 0, false, [], [], []);
        return ParsePorcelainV2(r.Stdout);
    }

    public async Task<string?> DiffFileAsync(string? ownerId, string root, string relPath, bool staged, CancellationToken ct = default)
    {
        if (!IsGitRepo(root)) return null;
        ValidateRel(root, relPath);
        string[] args = staged
            ? ["diff", "--cached", "--", relPath]
            : ["diff", "--", relPath];
        var r = await RunAsync(ownerId, root, args, ct: ct);
        if (r.Ok && !string.IsNullOrWhiteSpace(r.Stdout)) return r.Stdout;
        // Для нового (untracked) файла обычный diff пуст — показываем содержимое как добавление
        if (!staged)
        {
            var untracked = await RunAsync(ownerId, root,
                ["diff", "--no-index", "--", "/dev/null", relPath], ct: ct);
            if (!string.IsNullOrWhiteSpace(untracked.Stdout)) return untracked.Stdout;
        }
        return string.IsNullOrWhiteSpace(r.Stdout) ? null : r.Stdout;
    }

    public async Task<IReadOnlyList<GitLogEntry>> LogAsync(string? ownerId, string root, int limit = 100, string? branch = null, CancellationToken ct = default)
    {
        if (!IsGitRepo(root)) return [];
        // %x1f/%x1e — unit/record separators: subject может содержать что угодно
        var args = new List<string> { "log", "-n", limit.ToString(),
            "--pretty=format:%H%x1f%h%x1f%an%x1f%ae%x1f%aI%x1f%s%x1e" };
        if (!string.IsNullOrWhiteSpace(branch)) args.Add(branch);
        var r = await RunAsync(ownerId, root, args, ct: ct);
        if (!r.Ok) return [];

        var list = new List<GitLogEntry>();
        foreach (var rec in r.Stdout.Split('\x1e', StringSplitOptions.RemoveEmptyEntries))
        {
            var f = rec.Trim('\n', '\r').Split('\x1f');
            if (f.Length < 6) continue;
            if (!DateTimeOffset.TryParse(f[4], out var date)) continue;
            list.Add(new GitLogEntry(f[0], f[1], f[2], f[3], date, f[5]));
        }
        return list;
    }

    // sha валидируем формально (hex 6-40) — защита от передачи опций/ссылок вместо хеша
    private static bool IsValidSha(string sha) =>
        sha.Length is >= 6 and <= 40 && sha.All(c => c is (>= '0' and <= '9') or (>= 'a' and <= 'f') or (>= 'A' and <= 'F'));

    public async Task<GitCommitDetail?> CommitDetailAsync(string? ownerId, string root, string sha, CancellationToken ct = default)
    {
        if (!IsGitRepo(root) || !IsValidSha(sha)) return null;
        var meta = await RunAsync(ownerId, root,
            ["show", "--no-patch", "--format=%H%x1f%h%x1f%an%x1f%ae%x1f%aI%x1f%s%x1f%b", sha], ct: ct);
        if (!meta.Ok) return null;
        var f = meta.Stdout.TrimEnd('\n', '\r').Split('\x1f');
        if (f.Length < 7 || !DateTimeOffset.TryParse(f[4], out var date)) return null;

        // Список файлов коммита: -m + --first-parent, чтобы merge-коммиты тоже давали дифф к первому родителю
        var names = await RunAsync(ownerId, root,
            ["show", "--name-status", "--format=", "--first-parent", "-m", sha], ct: ct);
        var files = new List<GitFileChange>();
        foreach (var line in names.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.Trim('\r').Split('\t');
            if (parts.Length < 2) continue;
            var status = parts[0].Trim();
            if (status.Length == 0) continue;
            // R100/C75 → R/C с oldPath
            if ((status[0] == 'R' || status[0] == 'C') && parts.Length >= 3)
                files.Add(new GitFileChange(parts[2], status[0].ToString(), parts[1]));
            else
                files.Add(new GitFileChange(parts[1], status[0].ToString()));
        }
        return new GitCommitDetail(f[0], f[1], f[2], f[3], date, f[5], f[6].Trim(), files);
    }

    public async Task<string?> CommitFileDiffAsync(string? ownerId, string root, string sha, string relPath, CancellationToken ct = default)
    {
        if (!IsGitRepo(root) || !IsValidSha(sha)) return null;
        ValidateRel(root, relPath);
        var r = await RunAsync(ownerId, root,
            ["show", "--format=", "--first-parent", "-m", sha, "--", relPath], ct: ct);
        return r.Ok && !string.IsNullOrWhiteSpace(r.Stdout) ? r.Stdout : null;
    }

    public async Task<IReadOnlyList<GitBranchInfo>> BranchesAsync(string? ownerId, string root, CancellationToken ct = default)
    {
        if (!IsGitRepo(root)) return [];
        // %00 — NUL-разделитель полей внутри строки ветки
        var r = await RunAsync(ownerId, root,
            ["branch", "--format=%(HEAD)%00%(refname:short)%00%(upstream:short)"], ct: ct);
        if (!r.Ok) return [];

        var list = new List<GitBranchInfo>();
        foreach (var line in r.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var f = line.Trim('\r').Split('\0');
            if (f.Length < 2) continue;
            var current = f[0].Trim() == "*";
            var name = f[1];
            var upstream = f.Length > 2 && !string.IsNullOrWhiteSpace(f[2]) ? f[2] : null;
            if (!string.IsNullOrWhiteSpace(name)) list.Add(new GitBranchInfo(name, current, upstream));
        }
        return list;
    }

    // ---------- Запись (под per-repo семафором) ----------

    public Task StageAsync(string? ownerId, string root, string relPath, CancellationToken ct = default) =>
        WriteOp(ownerId, root, ["add", "--", ValidateRel(root, relPath)], ct: ct);

    public Task UnstageAsync(string? ownerId, string root, string relPath, CancellationToken ct = default) =>
        WriteOp(ownerId, root, ["restore", "--staged", "--", ValidateRel(root, relPath)], ct: ct);

    public Task StageAllAsync(string? ownerId, string root, CancellationToken ct = default) =>
        WriteOp(ownerId, root, ["add", "-A"], ct: ct);

    // Откат правок файла к HEAD (теряет несохранённые изменения — вызывающий гейтит опасность).
    public Task DiscardAsync(string? ownerId, string root, string relPath, CancellationToken ct = default) =>
        WriteOp(ownerId, root, ["checkout", "HEAD", "--", ValidateRel(root, relPath)], ct: ct);

    // Зернистый stage: патч (целый хунк или синтезированный из выбранных строк) — через stdin,
    // в индекс без изменения рабочего дерева. --recount: фронт мог пересчитать заголовки неточно.
    public async Task StageHunkAsync(string? ownerId, string root, string patch, CancellationToken ct = default)
    {
        var sem = LockFor(root);
        await sem.WaitAsync(ct);
        try { await RunOkAsync(ownerId, root, ["apply", "--cached", "--recount", "--whitespace=nowarn", "-"], stdin: patch, ct: ct); }
        finally { sem.Release(); }
    }

    public async Task UnstageHunkAsync(string? ownerId, string root, string patch, CancellationToken ct = default)
    {
        var sem = LockFor(root);
        await sem.WaitAsync(ct);
        try { await RunOkAsync(ownerId, root, ["apply", "--cached", "--reverse", "--recount", "--whitespace=nowarn", "-"], stdin: patch, ct: ct); }
        finally { sem.Release(); }
    }

    // ---------- Stash ----------

    public async Task<IReadOnlyList<GitStashEntry>> StashListAsync(string? ownerId, string root, CancellationToken ct = default)
    {
        if (!IsGitRepo(root)) return [];
        var r = await RunAsync(ownerId, root, ["stash", "list", "--format=%gd%x1f%s%x1f%cI"], ct: ct);
        if (!r.Ok) return [];
        var list = new List<GitStashEntry>();
        foreach (var line in r.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var f = line.Trim('\r').Split('\x1f');
            if (f.Length < 3) continue;
            DateTimeOffset.TryParse(f[2], out var date);
            list.Add(new GitStashEntry(list.Count, f[1], date));
        }
        return list;
    }

    public Task StashPushAsync(string? ownerId, string root, string? message, CancellationToken ct = default) =>
        WriteOp(ownerId, root,
            string.IsNullOrWhiteSpace(message)
                ? ["stash", "push", "--include-untracked"]
                : ["stash", "push", "--include-untracked", "-m", message],
            ct: ct);

    public Task StashPopAsync(string? ownerId, string root, int index, CancellationToken ct = default) =>
        WriteOp(ownerId, root, ["stash", "pop", $"stash@{{{index}}}"], ct: ct);

    // Удаление стэша необратимо — вызывающий гейтит подтверждением
    public Task StashDropAsync(string? ownerId, string root, int index, CancellationToken ct = default) =>
        WriteOp(ownerId, root, ["stash", "drop", $"stash@{{{index}}}"], ct: ct);

    // ---------- Revert (безопасная отмена — новый коммит, история не переписывается) ----------

    public async Task RevertCommitAsync(string? ownerId, string root, string sha, CancellationToken ct = default)
    {
        if (!IsValidSha(sha)) throw new GitCommandException("Некорректный sha");
        var sem = LockFor(root);
        await sem.WaitAsync(ct);
        try
        {
            var r = await RunAsync(ownerId, root, ["revert", "--no-edit", sha], ct: ct);
            if (!r.Ok)
            {
                // Конфликт — откатываем попытку, дерево остаётся чистым
                await RunAsync(ownerId, root, ["revert", "--abort"], ct: ct);
                throw new GitCommandException(FirstLine(r.Stderr) ?? "Не удалось откатить коммит (конфликт)");
            }
        }
        finally { sem.Release(); }
    }

    // ---------- Blame ----------

    public async Task<IReadOnlyList<GitBlameLine>> BlameAsync(string? ownerId, string root, string relPath, CancellationToken ct = default)
    {
        if (!IsGitRepo(root)) return [];
        ValidateRel(root, relPath);
        var r = await RunAsync(ownerId, root, ["blame", "--line-porcelain", "--", relPath], timeoutMs: 30_000, ct: ct);
        if (!r.Ok) throw new GitCommandException(FirstLine(r.Stderr) ?? "blame не удался");

        var lines = new List<GitBlameLine>();
        string sha = "", author = "";
        DateTimeOffset date = default;
        foreach (var raw in r.Stdout.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.Length == 0) continue;
            if (line[0] == '\t')
            {
                lines.Add(new GitBlameLine(lines.Count + 1, sha, sha.Length >= 7 ? sha[..7] : sha, author, date, line[1..]));
            }
            else if (line.StartsWith("author "))
                author = line["author ".Length..];
            else if (line.StartsWith("author-time "))
            {
                if (long.TryParse(line["author-time ".Length..], out var unix))
                    date = DateTimeOffset.FromUnixTimeSeconds(unix);
            }
            else
            {
                // Заголовок записи: "<40-hex sha> <строка-в-оригинале> <строка-в-файле> [<кол-во>]"
                var first = line.Split(' ')[0];
                if (first.Length == 40 && IsValidSha(first)) sha = first;
            }
        }
        return lines;
    }

    public async Task<string> CommitAsync(string? ownerId, string root, string message, bool amend = false, CancellationToken ct = default)
    {
        var sem = LockFor(root);
        await sem.WaitAsync(ct);
        try
        {
            // Сообщение — через stdin (-F -), не в argv: избегаем экранирования кавычек/переводов строк
            var args = new List<string> { "commit", "-F", "-" };
            if (amend) args.Add("--amend");
            await RunOkAsync(ownerId, root, args, stdin: message, ct: ct);
            var head = await RunAsync(ownerId, root, ["rev-parse", "HEAD"], ct: ct);
            return head.Stdout.Trim();
        }
        finally { sem.Release(); }
    }

    public Task CheckoutAsync(string? ownerId, string root, string branch, CancellationToken ct = default) =>
        WriteOp(ownerId, root, ["checkout", branch], ct: ct);

    // ---------- Сетевые операции (remote; таймаут длиннее) ----------

    // Токен НЕ в remote URL (утёк бы в .git/config и логи) и НЕ в argv (виден в списке
    // процессов): inline credential helper читает его из env процесса. Первый пустой
    // helper сбрасывает системные (Windows Credential Manager и т.п.).
    private static (string[] preArgs, Dictionary<string, string>? env) CredArgs(GitCredentials? creds)
    {
        if (creds is null) return ([], null);
        return (
            ["-c", "credential.helper=",
             "-c", $"credential.helper=!f() {{ echo username={creds.Username}; echo \"password=$GIT_REMOTE_TOKEN\"; }}; f"],
            new Dictionary<string, string> { ["GIT_REMOTE_TOKEN"] = creds.Token });
    }

    public Task FetchAsync(string? ownerId, string root, GitCredentials? creds = null, CancellationToken ct = default) =>
        NetworkOp(ownerId, root, ["fetch", "--prune"], creds, ct);

    // Только fast-forward: при расхождении веток git вернёт ошибку (409 наружу),
    // авто-merge/rebase не делаем — ручной разбор.
    public Task PullAsync(string? ownerId, string root, GitCredentials? creds = null, CancellationToken ct = default) =>
        NetworkOp(ownerId, root, ["pull", "--ff-only"], creds, ct);

    public Task PushAsync(string? ownerId, string root, GitCredentials? creds = null, CancellationToken ct = default) =>
        NetworkOp(ownerId, root, ["push"], creds, ct);

    // Первый push новой ветки: выставить upstream (origin/<branch>)
    public Task PushSetUpstreamAsync(string? ownerId, string root, string branch, GitCredentials? creds = null, CancellationToken ct = default) =>
        NetworkOp(ownerId, root, ["push", "-u", "origin", branch], creds, ct);

    private async Task NetworkOp(string? ownerId, string root, string[] args, GitCredentials? creds, CancellationToken ct)
    {
        var (pre, env) = CredArgs(creds);
        var sem = LockFor(root);
        await sem.WaitAsync(ct);
        try
        {
            var r = await RunAsync(ownerId, root, [.. pre, .. args], env: env, timeoutMs: NetworkTimeoutMs, ct: ct);
            if (!r.Ok)
                throw new GitCommandException(FirstLine(r.Stderr) ?? "git завершился с ошибкой");
        }
        finally { sem.Release(); }
    }

    // ---------- Инициализация и remote ----------

    public async Task InitAsync(string? ownerId, string root, CancellationToken ct = default)
    {
        if (IsGitRepo(root)) return; // идемпотентно: папка уже репозиторий
        await RunOkAsync(ownerId, root, ["init", "-b", "main"], ct: ct);
    }

    // Подключить/обновить origin (идемпотентно)
    public async Task SetRemoteAsync(string? ownerId, string root, string url, CancellationToken ct = default)
    {
        var existing = await RunAsync(ownerId, root, ["remote", "get-url", "origin"], ct: ct);
        if (existing.Ok)
            await RunOkAsync(ownerId, root, ["remote", "set-url", "origin", url], ct: ct);
        else
            await RunOkAsync(ownerId, root, ["remote", "add", "origin", url], ct: ct);
    }

    public Task CreateBranchAsync(string? ownerId, string root, string name, string? from, CancellationToken ct = default) =>
        WriteOp(ownerId, root, from is null ? ["checkout", "-b", name] : ["checkout", "-b", name, from], ct: ct);

    private async Task WriteOp(string? ownerId, string root, IReadOnlyList<string> args, int timeoutMs = DefaultTimeoutMs, CancellationToken ct = default)
    {
        var sem = LockFor(root);
        await sem.WaitAsync(ct);
        try { await RunOkAsync(ownerId, root, args, timeoutMs: timeoutMs, ct: ct); }
        finally { sem.Release(); }
    }

    // ---------- Парсер porcelain v2 ----------

    private static GitStatusDto ParsePorcelainV2(string output)
    {
        string? branch = null, upstream = null;
        int ahead = 0, behind = 0;
        bool detached = false;
        var staged = new List<GitFileChange>();
        var unstaged = new List<GitFileChange>();
        var untracked = new List<GitFileChange>();

        var tokens = output.Split('\0');
        for (int i = 0; i < tokens.Length; i++)
        {
            var t = tokens[i];
            if (t.Length == 0) continue;

            if (t.StartsWith("# branch.head "))
            {
                var head = t["# branch.head ".Length..];
                if (head == "(detached)") detached = true; else branch = head;
            }
            else if (t.StartsWith("# branch.upstream "))
                upstream = t["# branch.upstream ".Length..];
            else if (t.StartsWith("# branch.ab "))
            {
                foreach (var part in t["# branch.ab ".Length..].Split(' '))
                {
                    if (part.StartsWith('+') && int.TryParse(part[1..], out var a)) ahead = a;
                    else if (part.StartsWith('-') && int.TryParse(part[1..], out var b)) behind = b;
                }
            }
            else if (t[0] == '1')
            {
                // 1 <XY> <sub> <mH> <mI> <mW> <hH> <hI> <path>
                var f = t.Split(' ', 9);
                if (f.Length < 9) continue;
                AddXy(f[1], f[8], null, staged, unstaged);
            }
            else if (t[0] == '2')
            {
                // 2 <XY> <sub> <mH> <mI> <mW> <hH> <hI> <Xscore> <path>\0<origPath>
                var f = t.Split(' ', 10);
                if (f.Length < 10) continue;
                var origPath = (i + 1 < tokens.Length) ? tokens[++i] : null;
                AddXy(f[1], f[9], origPath, staged, unstaged);
            }
            else if (t[0] == 'u')
            {
                // Конфликт слияния: показываем в unstaged со статусом U
                var f = t.Split(' ', 11);
                var path = f.Length > 0 ? f[^1] : t;
                unstaged.Add(new GitFileChange(path, "U"));
            }
            else if (t[0] == '?')
                untracked.Add(new GitFileChange(t[2..], "?"));
        }

        return new GitStatusDto(true, branch, upstream, ahead, behind, detached, staged, unstaged, untracked);
    }

    // X — статус в индексе (staged), Y — в рабочем дереве (unstaged). '.' = без изменений.
    private static void AddXy(string xy, string path, string? origPath, List<GitFileChange> staged, List<GitFileChange> unstaged)
    {
        if (xy.Length < 2) return;
        char x = xy[0], y = xy[1];
        if (x != '.') staged.Add(new GitFileChange(path, x.ToString(), origPath));
        if (y != '.') unstaged.Add(new GitFileChange(path, y.ToString(), origPath));
    }

    private static string? FirstLine(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        var nl = s.IndexOf('\n');
        return (nl < 0 ? s : s[..nl]).Trim();
    }
}
