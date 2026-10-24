using System.Diagnostics;
using ClaudeHomeServer.Services.Composition;

namespace ClaudeHomeServer.Services.Tasks;

// Состояние git дерева шага. Бэкенд работает с хостовыми путями, git идёт на хосте; любой сбой
// (нет git, не репозиторий, таймаут) — null, а гейт тогда отказывает закрытию
public sealed class GitWorktreeHeadReader : IWorktreeHeadReader
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

    public IReadOnlyList<string>? ChangedFiles(string worktreePath)
    {
        foreach (var baseBranch in BaseBranches)
        {
            var mergeBase = Git(worktreePath, "merge-base", baseBranch, "HEAD")?.Trim();
            if (string.IsNullOrEmpty(mergeBase)) continue;
            var diff = Git(worktreePath, "diff", "--name-only", mergeBase, "HEAD");
            if (diff is null) return null;
            return diff.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        }
        return null;
    }

    // Вывод git или null при любом сбое (ненулевой код выхода тоже сбой)
    private static string? Git(string worktreePath, params string[] args)
    {
        if (string.IsNullOrWhiteSpace(worktreePath) || !Directory.Exists(worktreePath)) return null;
        try
        {
            var psi = new ProcessStartInfo("git")
            {
                WorkingDirectory = worktreePath,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            foreach (var arg in args) psi.ArgumentList.Add(arg);
            using var process = Process.Start(psi);
            if (process is null) return null;
            var output = process.StandardOutput.ReadToEndAsync();
            process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(Timeout))
            {
                process.Kill(entireProcessTree: true);
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
