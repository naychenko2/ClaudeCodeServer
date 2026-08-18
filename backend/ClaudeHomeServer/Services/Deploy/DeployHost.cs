using System.Text;
using ClaudeHomeServer.Services.Execution;
using ClaudeHomeServer.Services.Git;

namespace ClaudeHomeServer.Services.Deploy;

/// <summary>Срез рабочего дерева репозитория-источника на момент заявки.</summary>
public sealed record DeployGitSnapshot(string? Sha, IReadOnlyList<string> DirtyFiles, string? Error)
{
    public bool Dirty => DirtyFiles.Count > 0;
}

/// <summary>
/// Хостовые операции выкатки: проба репозитория и побудка задачи планировщика. Отдельный
/// шов (а не прямые вызовы git/schtasks из DeployService) нужен ровно для тестов: проверять
/// надо guard'ы, журнал и коды ответа, а не наличие git и Task Scheduler на раннере CI.
/// </summary>
public interface IDeployHost
{
    Task<DeployGitSnapshot> GitSnapshotAsync(string repoDir, CancellationToken ct = default);

    /// <summary>Разбудить агента. null — задача запущена, иначе текст отказа.</summary>
    Task<string?> WakeAgentAsync(DeployOptions options, CancellationToken ct = default);

    /// <summary>
    /// Взять мьютекс выкатки Global\ccs-deploy — тот же, что держит агент всё время работы.
    /// null — занят, агент жив. Под ним (и только под ним) серверу позволено писать в журнал
    /// после старта агента: иначе записи двух процессов затирают друг друга.
    /// ВАЖНО: владение мьютексом принадлежит ПОТОКУ — брать и отпускать без await между ними.
    /// </summary>
    IDisposable? TryLockAgent();
}

public sealed class DeployHost(
    GitService git,
    ILauncherFactory launchers,
    ILogger<DeployHost> log) : IDeployHost
{
    // Сколько ждём schtasks: он только ставит задачу в очередь и возвращается,
    // сама выкатка идёт минутами уже в отвязанном процессе
    private const int WakeTimeoutMs = 15_000;

    public async Task<DeployGitSnapshot> GitSnapshotAsync(string repoDir, CancellationToken ct = default)
    {
        if (!Directory.Exists(repoDir))
            return new DeployGitSnapshot(null, [], $"каталог репозитория не найден: {repoDir}");
        if (!GitService.IsGitRepo(repoDir))
            return new DeployGitSnapshot(null, [], $"это не git-репозиторий: {repoDir}");

        try
        {
            // ownerId=null — системный вызов, всегда локальная среда (см. LauncherFactory)
            var status = await git.RunAsync(null, repoDir, ["status", "--porcelain"], ct: ct);
            if (!status.Ok)
                return new DeployGitSnapshot(null, [], $"git status: {status.Stderr.Trim()}");

            var head = await git.RunAsync(null, repoDir, ["rev-parse", "--short", "HEAD"], ct: ct);
            var sha = head.Ok ? head.Stdout.Trim() : null;

            return new DeployGitSnapshot(sha, ParseDirty(status.Stdout), null);
        }
        catch (GitCommandException ex)
        {
            return new DeployGitSnapshot(null, [], ex.Message);
        }
    }

    // Строка porcelain — «XY путь»: статус фиксированной ширины 2 + пробел.
    // Переименование приходит как «old -> new» — оставляем как есть, читать это человеку.
    internal static List<string> ParseDirty(string stdout) =>
        [.. stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.TrimEnd('\r'))
            .Where(l => l.Length > 3)
            .Select(l => l[3..].Trim())
            .Where(p => p.Length > 0)];

    public async Task<string?> WakeAgentAsync(DeployOptions options, CancellationToken ct = default)
    {
        // Командная строка ФИКСИРОВАНА: имя задачи из конфига (проверено белым списком) и
        // ничего больше. Параметры заявки едут агенту журналом — см. DeployRequest.
        var spec = new ProcessSpec
        {
            FileName = "schtasks",
            Args = ["/run", "/tn", options.TaskName],
            WorkingDirectory = Directory.Exists(options.AgentDir) ? options.AgentDir : null,
            RedirectStdin = false,
            StdioEncoding = new UTF8Encoding(false),
            Track = false,
        };

        System.Diagnostics.Process proc;
        try { proc = launchers.Local.Start(spec); }
        catch (Exception ex)
        {
            log.LogError(ex, "Не удалось запустить schtasks для задачи {Task}", options.TaskName);
            return $"не удалось запустить планировщик: {ex.Message}";
        }

        try
        {
            var stdout = proc.StandardOutput.ReadToEndAsync(ct);
            var stderr = proc.StandardError.ReadToEndAsync(ct);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(WakeTimeoutMs);
            try { await proc.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException)
            {
                launchers.Local.Kill(proc);
                return "планировщик не ответил вовремя";
            }

            if (proc.ExitCode == 0) return null;
            var text = ((await stderr) + " " + (await stdout)).Trim();
            log.LogError("schtasks /run {Task} завершился с кодом {Code}: {Text}",
                options.TaskName, proc.ExitCode, text);
            return $"планировщик отказал (код {proc.ExitCode}): {text}";
        }
        finally { proc.Dispose(); }
    }

    public IDisposable? TryLockAgent() =>
        Backup.InstanceLock.TryAcquireDeploy() is { } mutex ? new MutexLease(mutex) : null;

    // Отпускаем в try/catch: мьютекс мог быть заброшен умершим агентом (владение перешло
    // к нам через AbandonedMutexException), и падение на ReleaseMutex не должно ронять
    // приём заявки — предмет вызова совсем в другом.
    private sealed class MutexLease(Mutex mutex) : IDisposable
    {
        public void Dispose()
        {
            try { mutex.ReleaseMutex(); } catch { /* не наш мьютекс или уже отпущен */ }
            mutex.Dispose();
        }
    }
}
