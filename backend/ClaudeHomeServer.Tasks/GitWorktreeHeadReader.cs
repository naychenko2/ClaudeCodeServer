using System.Diagnostics;
using ClaudeHomeServer.Services.Composition;

namespace ClaudeHomeServer.Services.Tasks;

// Текущий коммит дерева через `git rev-parse HEAD`. Бэкенд работает с хостовыми путями, git идёт
// на хосте; любой сбой (нет git, не репозиторий, таймаут) — null, а гейт тогда отказывает закрытию
public sealed class GitWorktreeHeadReader : IWorktreeHeadReader
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    public string? Head(string worktreePath)
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
            psi.ArgumentList.Add("rev-parse");
            psi.ArgumentList.Add("HEAD");
            using var process = Process.Start(psi);
            if (process is null) return null;
            var output = process.StandardOutput.ReadToEndAsync();
            process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(Timeout))
            {
                process.Kill(entireProcessTree: true);
                return null;
            }
            var sha = output.GetAwaiter().GetResult().Trim();
            return process.ExitCode == 0 && sha.Length >= 7 && sha.All(Uri.IsHexDigit) ? sha : null;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
