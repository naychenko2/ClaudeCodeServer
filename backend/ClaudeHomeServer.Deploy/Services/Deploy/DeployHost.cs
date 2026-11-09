using System.Text;
using ClaudeHomeServer.Services.Composition;
using ClaudeHomeServer.Services.Execution;

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

    /// <summary>
    /// Разбудить агента. null — задача запущена, иначе текст отказа. deployId — id выкатки
    /// (UTC-штамп, он же имя папки снимка): на Linux уходит в имя systemd-юнита, на Windows
    /// в argv не едет (там имя задачи из конфига).
    /// </summary>
    Task<string?> WakeAgentAsync(DeployOptions options, string deployId, CancellationToken ct = default);

    /// <summary>
    /// Взять лок выкатки — тот же, что держит агент всё время работы: на Windows именованный
    /// мьютекс Global\ccs-deploy, на Linux файл deploy.lock (flock). null — занят, агент жив.
    /// Под ним (и только под ним) серверу позволено писать в журнал после старта агента:
    /// иначе записи двух процессов затирают друг друга.
    /// ВАЖНО: владение локом принадлежит ПОТОКУ — брать и отпускать без await между ними.
    /// </summary>
    IDisposable? TryLockAgent(string releasesDir);
}

public sealed class DeployHost(
    IGitRepoChecker git,
    ILauncherFactory launchers,
    IDeployAgentLock agentLock,
    ILogger<DeployHost> log) : IDeployHost
{
    // Сколько ждём schtasks: он только ставит задачу в очередь и возвращается,
    // сама выкатка идёт минутами уже в отвязанном процессе
    private const int WakeTimeoutMs = 15_000;

    public async Task<DeployGitSnapshot> GitSnapshotAsync(string repoDir, CancellationToken ct = default)
    {
        if (!Directory.Exists(repoDir))
            return new DeployGitSnapshot(null, [], $"каталог репозитория не найден: {repoDir}");
        if (!git.IsGitRepo(repoDir))
            return new DeployGitSnapshot(null, [], $"это не git-репозиторий: {repoDir}");

        try
        {
            // ownerId=null — системный вызов, всегда локальная среда (см. LauncherFactory)
            var snap = await git.RepoSnapshotAsync(null, repoDir, ct);
            // Пробрасываем Error от RepoSnapshot (битый .git/лок/диск → git status упал) —
            // иначе DeployService решил бы, что дерево чистое, и guard грязного дерева
            // пропустил бы выкатку с непрочитанным состоянием.
            return new DeployGitSnapshot(snap.ShortHeadSha, snap.DirtyPaths, snap.Error);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new DeployGitSnapshot(null, [], ex.Message);
        }
    }

    public async Task<string?> WakeAgentAsync(DeployOptions options, string deployId, CancellationToken ct = default)
    {
        // Командная строка ФИКСИРОВАНА: параметры заявки НЕ едут в argv — они лежат в
        // журнале (deploy-state.json), а агент читает его сам. argv различает только
        // запуск: на Linux — имя systemd-юнита (id прошёл IsValidBuildId) и каталог релизов
        // из конфига; на Windows — имя задачи планировщика (белый список).
        // id выкатки уходит в имя юнита — принимаем только прошедший IsValidBuildId, иначе
        // CR/LF в имени = инъекция. На Windows id в argv не едет, проверка не нужна.
        if (options.IsLinux && !DeployValidation.IsValidBuildId(deployId))
            return $"недопустимый id выкатки для имени systemd-юнита: {deployId}";

        var spec = options.IsLinux
            ? BuildLinuxWakeSpec(options, deployId)
            : BuildWindowsWakeSpec(options);

        System.Diagnostics.Process proc;
        try { proc = launchers.Local.Start(spec); }
        catch (Exception ex)
        {
            log.LogError(ex, "Не удалось запустить побудку ({File})", spec.FileName);
            return $"не удалось запустить побудку: {ex.Message}";
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
                return "побудка не ответила вовремя";
            }

            if (proc.ExitCode == 0) return null;
            var text = ((await stderr) + " " + (await stdout)).Trim();
            log.LogError("{File} завершился с кодом {Code}: {Text}", spec.FileName, proc.ExitCode, text);
            return $"побудка отказала (код {proc.ExitCode}): {text}";
        }
        finally { proc.Dispose(); }
    }

    // Linux: systemd-run --user --collect --quiet --unit=ccs-deploy-<id> -p
    // ManagedOOMPreference=avoid --setenv=CCS_RELEASES_DIR=<ReleasesDir> <AgentDir>/scripts/ops/deploy-linux.sh
    // systemd-run ставит one-shot юнит и возвращается сразу; сам скрипт идёт уже в
    // отвязанном процессе. -p ManagedOOMPreference=avoid — не дать systemd-oomd убить
    // выкатку при нехватке памяти; CCS_RELEASES_DIR — каталог из конфига, не из заявки.
    // <id> — id выкатки (UTC-штамп), он прошёл IsValidBuildId в WakeAgentAsync, поэтому
    // безопасен в имени юнита.
    private static ProcessSpec BuildLinuxWakeSpec(DeployOptions options, string deployId)
    {
        var script = Path.Combine(options.AgentDir, "scripts", "ops", "deploy-linux.sh");
        return new ProcessSpec
        {
            FileName = "systemd-run",
            Args =
            [
                "--user",
                "--collect",
                "--quiet",
                $"--unit=ccs-deploy-{deployId}",
                "-p", "ManagedOOMPreference=avoid",
                $"--setenv=CCS_RELEASES_DIR={options.ReleasesDir}",
                script,
            ],
            RedirectStdin = false,
            StdioEncoding = new UTF8Encoding(false),
            Track = false,
        };
    }

    // Windows: прежний schtasks /run /tn <TaskName>. Имя задачи из конфига (белый список).
    private static ProcessSpec BuildWindowsWakeSpec(DeployOptions options) => new()
    {
        FileName = "schtasks",
        Args = ["/run", "/tn", options.TaskName],
        WorkingDirectory = Directory.Exists(options.AgentDir) ? options.AgentDir : null,
        RedirectStdin = false,
        StdioEncoding = new UTF8Encoding(false),
        Track = false,
    };

    public IDisposable? TryLockAgent(string releasesDir) => agentLock.TryAcquireDeploy(releasesDir);
}
