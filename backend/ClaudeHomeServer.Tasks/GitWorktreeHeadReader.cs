using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using ClaudeHomeServer.Services.Composition;
using ClaudeHomeServer.Services.Execution;

namespace ClaudeHomeServer.Services.Tasks;

// Состояние git дерева шага. Бэкенд работает с хостовыми путями, git идёт на хосте через системный
// драйвер (ILauncherFactory.Local); любой сбой (нет git, не репозиторий, таймаут) — null, а гейт тогда
// отказывает закрытию
public sealed class GitWorktreeHeadReader(ILauncherFactory launchers) : IWorktreeHeadReader, IWorktreeCommitReader
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    // Основная ветка, от которой считается набор изменений шага
    private static readonly string[] BaseBranches = ["master", "main"];

    public string? Head(string worktreePath)
    {
        var sha = Git(worktreePath, "rev-parse", "HEAD")?.Trim();
        return sha is { Length: >= 7 } && sha.All(Uri.IsHexDigit) ? sha : null;
    }

    public bool? IsClean(string worktreePath)
    {
        var status = Git(worktreePath, "status", "--porcelain", "--untracked-files=all");
        return status is null ? null : status.Trim().Length == 0;
    }

    // База — та из master|main, от которой ветка шага отошла позже (меньше файлов в разнице): при ветке от main
    // merge-base с устаревшим master тянул бы чужие файлы и лишние проверки
    public IReadOnlyList<string>? ChangedFiles(string worktreePath)
    {
        IReadOnlyList<string>? best = null;
        foreach (var baseBranch in BaseBranches)
        {
            var mergeBase = Git(worktreePath, "merge-base", baseBranch, "HEAD")?.Trim();
            if (string.IsNullOrEmpty(mergeBase)) continue;
            var diff = Git(worktreePath, "diff", "--name-only", mergeBase, "HEAD");
            if (diff is null) return null;
            var files = diff.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (best is null || files.Length < best.Count) best = files;
        }
        return best;
    }

    // Разделители записей и полей формата log: управляющие символы не встречаются в сообщениях коммитов
    private const char RecordSep = '\u001e';
    private const char FieldSep = '\u001f';

    // Строка-трейлер задачи: id берётся целиком (\S+), поэтому префикс чужого id совпадением не считается
    private static readonly Regex TaskTrailer = new(@"(?m)^CCS-Task:[ \t]*(\S+)[ \t]*$", RegexOptions.Compiled);

    public IReadOnlyList<WorktreeCommit>? CommitsWithTaskTrailer(string worktreePath, string taskId, int limit)
    {
        if (string.IsNullOrWhiteSpace(taskId) || limit <= 0) return [];
        // --grep — лишь предфильтр git (подстрока без привязки к концу слова); точное совпадение id
        // проверяется ниже по телу коммита. Id — аргумент массива Args, не строка оболочки; regex-метасимволы
        // в нём экранируются
        var output = Git(worktreePath, "-c", "core.quotepath=false", "log", "HEAD", "-n", limit.ToString(),
            "--grep=^CCS-Task:[[:space:]]*" + Regex.Escape(taskId), "--name-only",
            $"--format={RecordSep}%H{FieldSep}%cI{FieldSep}%s{FieldSep}%B{FieldSep}");
        if (output is null) return null;

        var commits = new List<WorktreeCommit>();
        foreach (var record in output.Split(RecordSep, StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = record.Split(FieldSep);
            if (parts.Length < 5) continue;
            if (!TaskTrailer.Matches(parts[3]).Any(m => m.Groups[1].Value == taskId)) continue;
            var at = DateTimeOffset.TryParse(parts[1], out var parsed) ? parsed.UtcDateTime : (DateTime?)null;
            var files = parts[4].Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            commits.Add(new WorktreeCommit(parts[0].Trim(), parts[2], at, files));
        }
        // git отдаёт от нового к старому — экран хранит цепочку версий от старой к новой
        commits.Reverse();
        return commits;
    }

    public WorktreeDiff? CommitDiff(string worktreePath, string fullSha, int maxChars)
    {
        // Полный sha — только hex: иначе «sha» мог бы стать опцией git. Вызывающий и так берёт его из списка коммитов
        if (fullSha.Length < 7 || !fullSha.All(Uri.IsHexDigit) || maxChars <= 0) return null;
        return GitCapped(worktreePath, maxChars, "-c", "core.quotepath=false", "show", "--no-color",
            "--no-ext-diff", "--format=", "--patch", fullSha);
    }

    public WorktreeDiff? FileDiff(string worktreePath, string fromRef, string toRef, string file, int maxChars)
    {
        // Ссылки — только hex (fromRef допускает один хвостовой «^») и путь после «--»: ничто из запроса не станет опцией git
        static bool Ref(string r, bool parentAllowed)
        {
            if (parentAllowed && r.EndsWith('^')) r = r[..^1];
            return r.Length >= 7 && r.All(Uri.IsHexDigit);
        }
        if (!Ref(fromRef, true) || !Ref(toRef, false) || string.IsNullOrWhiteSpace(file) || maxChars <= 0) return null;
        return GitCapped(worktreePath, maxChars, "-c", "core.quotepath=false", "diff", "--no-color",
            "--no-ext-diff", fromRef, toRef, "--", file);
    }

    // Как Git, но вывод режется по maxChars: огромный дифф не читается в память целиком, процесс гасится
    private WorktreeDiff? GitCapped(string worktreePath, int maxChars, params string[] args)
    {
        if (string.IsNullOrWhiteSpace(worktreePath) || !Directory.Exists(worktreePath)) return null;
        var spec = new ProcessSpec
        {
            FileName = "git",
            Args = args,
            WorkingDirectory = worktreePath,
            RedirectStdin = false,
            StdioEncoding = new UTF8Encoding(false),
            TurnId = Guid.NewGuid().ToString("N"),
            Track = false,
        };
        var launcher = launchers.Local;
        Process? process = null;
        try
        {
            process = launcher.Start(spec);
            process.StandardError.ReadToEndAsync();
            var buffer = new char[maxChars + 1];
            var read = 0;
            // Читаем на один символ больше потолка: так отличаем «ровно потолок» от «длиннее»
            var reader = Task.Run(() =>
            {
                int n;
                while (read < buffer.Length && (n = process.StandardOutput.Read(buffer, read, buffer.Length - read)) > 0)
                    read += n;
            });
            if (!reader.Wait(Timeout))
            {
                launcher.Kill(process, spec.TurnId);
                return null;
            }
            var truncated = read > maxChars;
            if (truncated) launcher.Kill(process, spec.TurnId);
            else if (!process.WaitForExit(Timeout)) { launcher.Kill(process, spec.TurnId); return null; }
            else if (process.ExitCode != 0) return null;
            return new WorktreeDiff(new string(buffer, 0, Math.Min(read, maxChars)), truncated);
        }
        catch (Exception)
        {
            return null;
        }
        finally
        {
            process?.Dispose();
        }
    }

    // Вывод git или null при любом сбое (ненулевой код выхода тоже сбой)
    private string? Git(string worktreePath, params string[] args)
    {
        if (string.IsNullOrWhiteSpace(worktreePath) || !Directory.Exists(worktreePath)) return null;
        var spec = new ProcessSpec
        {
            FileName = "git",
            Args = args,
            WorkingDirectory = worktreePath,
            RedirectStdin = false,
            StdioEncoding = new UTF8Encoding(false),
            TurnId = Guid.NewGuid().ToString("N"),
            // Короткоживущий, убивается по своему таймауту — реестру процессов не нужен
            Track = false,
        };
        var launcher = launchers.Local;
        try
        {
            using var process = launcher.Start(spec);
            var output = process.StandardOutput.ReadToEndAsync();
            process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(Timeout))
            {
                launcher.Kill(process, spec.TurnId);
                return null;
            }
            return process.ExitCode == 0 ? output.GetAwaiter().GetResult() : null;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
