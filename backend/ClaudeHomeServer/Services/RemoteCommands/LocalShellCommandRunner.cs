using System.Diagnostics;
using System.Text;

namespace ClaudeHomeServer.Services.RemoteCommands;

/// <summary>
/// Реальный раннер пульта: команда исполняется процессом на машине сервера, всегда
/// локально (никакой per-owner среды — это админ-фича над хостом, как питание).
///
/// Два боевых фикса переехали сюда из сторожей чатов и обязательны:
/// 1. Windows-шелл получает аргументы СЫРОЙ строкой <c>/s /c "команда"</c>
///    (<see cref="ShellCommandLine"/>), а не через <c>ArgumentList</c>: .NET экранирует
///    внутренние кавычки как <c>\"</c>, cmd этих правил не знает, и команда с вложенными
///    кавычками разваливалась, давая ложный exit 0 (прод 01.09).
/// 2. Кодировка stdout/stderr задана явно UTF-8: без неё .NET читает в системной
///    (OEM/ANSI), кириллица превращается в кракозябры и ломает сопоставление с
///    <c>StatusRunningPattern</c>.
/// </summary>
public sealed class LocalShellCommandRunner : IShellCommandRunner
{
    public async Task<ShellRunResult> RunAsync(string command, string? workingDir, int timeoutSeconds,
        OutputRingBuffer? sink, CancellationToken ct)
    {
        Process proc;
        try { proc = Start(command, workingDir, redirect: true); }
        catch (Exception ex) { return ShellRunResult.Failed(ex.Message); }

        try
        {
            var stdoutTask = proc.StandardOutput.ReadToEndAsync(ct);
            var stderrTask = proc.StandardError.ReadToEndAsync(ct);
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
            try { await proc.WaitForExitAsync(timeoutCts.Token); }
            catch (OperationCanceledException)
            {
                // Kill безусловен — и по своему таймауту, и по внешней отмене: иначе процесс
                // остался бы сиротой, держа пайпы вывода. Внешняя отмена пробрасывается наверх,
                // свой таймаут — честный исход «не уложилась».
                KillTree(proc);
                // Читатели пайпов брошены на полпути: без явного наблюдения их падение
                // всплывёт позже как UnobservedTaskException в чужом месте программы.
                Observe(stdoutTask);
                Observe(stderrTask);
                if (ct.IsCancellationRequested) throw;
                return ShellRunResult.TimedOut();
            }

            var stdout = await stdoutTask;
            var text = Combine(stdout, await stderrTask);
            if (text.Length > 0) sink?.Append(text.EndsWith('\n') ? text : text + "\n");
            return ShellRunResult.Exited(proc.ExitCode, text, stdout);
        }
        finally { proc.Dispose(); }
    }

    public ShellSpawnResult Spawn(string command, string? workingDir, OutputRingBuffer? sink)
    {
        Process proc;
        try { proc = Start(command, workingDir, redirect: true); }
        catch (Exception ex) { return new ShellSpawnResult(null, ex.Message); }

        // Вывод демона читаем построчно в буфер: ReadToEnd тут заблокировал бы поток до
        // смерти процесса, а буфер нужен живым — хвост показывают в модалке.
        proc.OutputDataReceived += (_, e) => { if (e.Data is not null) sink?.Append(e.Data + "\n"); };
        proc.ErrorDataReceived += (_, e) => { if (e.Data is not null) sink?.Append(e.Data + "\n"); };
        try
        {
            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();
        }
        catch (InvalidOperationException)
        {
            // Процесс успел умереть до подписки — не повод считать спавн несостоявшимся:
            // это решает вызывающий по факту выхода после grace-паузы.
        }

        return new ShellSpawnResult(new LocalShellProcess(proc), null);
    }

    private static Process Start(string command, string? workingDir, bool redirect)
    {
        var windows = OperatingSystem.IsWindows();
        var psi = new ProcessStartInfo
        {
            FileName = ShellCommandLine.ShellFileName(windows),
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = redirect,
            RedirectStandardError = redirect,
            WorkingDirectory = string.IsNullOrWhiteSpace(workingDir) ? "" : workingDir,
        };

        if (windows) psi.Arguments = ShellCommandLine.WindowsCmdArguments(command);
        else foreach (var arg in ShellCommandLine.UnixShellArgs(command)) psi.ArgumentList.Add(arg);

        if (redirect)
        {
            psi.StandardOutputEncoding = new UTF8Encoding(false);
            psi.StandardErrorEncoding = new UTF8Encoding(false);
        }

        var proc = Process.Start(psi)
                   ?? throw new InvalidOperationException("Не удалось запустить процесс шелла.");
        return proc;
    }

    /// <summary>Брошенная задача чтения: её исключение забираем тихо, чтобы оно не улетело
    /// в <c>TaskScheduler.UnobservedTaskException</c>.</summary>
    private static void Observe(Task task) => _ = task.ContinueWith(
        t => _ = t.Exception,
        CancellationToken.None,
        TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
        TaskScheduler.Default);

    private static string Combine(string stdout, string stderr) =>
        string.Join("\n", new[] { stdout.TrimEnd(), stderr.TrimEnd() }.Where(s => s.Length > 0));

    internal static void KillTree(Process proc)
    {
        try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); }
        catch { /* умер сам между проверкой и убийством — ровно то, чего добивались */ }
    }

    /// <summary>Обёртка над живым процессом: наружу торчит только то, что нужно владельцу.</summary>
    private sealed class LocalShellProcess(Process proc) : IShellProcess
    {
        public bool HasExited
        {
            get { try { return proc.HasExited; } catch { return true; } }
        }

        public async Task WaitForExitAsync(CancellationToken ct)
        {
            try { await proc.WaitForExitAsync(ct); }
            catch (OperationCanceledException) { /* просто перестали ждать — процесс не трогаем */ }
        }

        public void KillTree()
        {
            LocalShellCommandRunner.KillTree(proc);
            try { proc.WaitForExit(5000); } catch { /* уже мёртв или недоступен */ }
        }

        public void Dispose() => proc.Dispose();
    }
}
