using System.Text;
using ClaudeHomeServer.Services.Composition;
using ClaudeHomeServer.Services.Execution;

namespace ClaudeHomeServer.Services.Tasks;

// Состояние git дерева шага. Бэкенд работает с хостовыми путями, git идёт на хосте через системный
// драйвер (ILauncherFactory.Local); любой сбой (нет git, не репозиторий, таймаут) — null, а гейт тогда
// отказывает закрытию
public sealed class GitWorktreeHeadReader(ILauncherFactory launchers) : IWorktreeHeadReader
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
