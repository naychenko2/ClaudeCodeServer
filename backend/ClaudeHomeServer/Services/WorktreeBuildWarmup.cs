using System.Diagnostics;
using ClaudeHomeServer.Services.Execution;

namespace ClaudeHomeServer.Services;

// Прогрев сборки свежего worktree: холодная сборка дерева занимает 60–90 с (restore + полный
// компил), и без прогрева её оплачивает модель первым же dotnet build хода. Сразу после
// заведения дерева под чат/задачу фоном запускается сборка тестового проекта — в среде
// владельца (ForOwner: та же изоляция и пределы памяти, что у ходов). Fire-and-forget:
// ход её не ждёт, результат — только в лог, исключения наружу не выходят.
//
// Не более одного прогрева на дерево за жизнь процесса, и только для дерева, где тестовый
// проект ещё не собирался (нет obj/): прогрев параллельно с уже идущей сборкой агента в том же
// дереве дрался бы с ней за obj/bin. Тумблер — Execution:WarmupBuild (дефолт true), читается
// на каждом вызове: выключается правкой конфига без рестарта.
public sealed class WorktreeBuildWarmup(ILauncherFactory launchers, IConfiguration config, ILogger log)
{
    // Путь проекта от корня дерева — прогревается ровно то, что агенты гоняют чаще всего
    internal const string TestsProject = "backend/ClaudeHomeServer.Tests";
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(15);

    private readonly HashSet<string> _warmed = new(StringComparer.Ordinal);

    internal static ProcessSpec BuildSpec(string worktreeRoot) => new()
    {
        FileName = "dotnet",
        Args = ["build", TestsProject, "-m:4", "-v:q", "-nologo"],
        WorkingDirectory = worktreeRoot,
        RedirectStdin = false,
    };

    // true — прогрев запущен (процесс стартовал)
    public bool TryStart(string? ownerId, string worktreeRoot)
    {
        try
        {
            if (!config.GetValue("Execution:WarmupBuild", true)) return false;
            var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(worktreeRoot));
            var project = Path.Combine(root, TestsProject.Replace('/', Path.DirectorySeparatorChar));
            if (!Directory.Exists(project) || Directory.Exists(Path.Combine(project, "obj"))) return false;
            lock (_warmed)
                if (!_warmed.Add(root)) return false;

            var launcher = launchers.ForOwner(ownerId);
            var process = launcher.Start(BuildSpec(root));
            _ = Task.Run(() => WatchAsync(launcher, process, root));
            return true;
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Прогрев сборки дерева {Root} не запустился", worktreeRoot);
            return false;
        }
    }

    private async Task WatchAsync(IProcessLauncher launcher, Process process, string root)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            using (process)
            {
                // Оба потока перенаправлены раннером — не вычитать их значит повесить сборку на
                // заполненной трубе
                var stdout = process.StandardOutput.ReadToEndAsync();
                var stderr = process.StandardError.ReadToEndAsync();
                using var cts = new CancellationTokenSource(Timeout);
                try { await process.WaitForExitAsync(cts.Token); }
                catch (OperationCanceledException)
                {
                    launcher.Kill(process);
                    log.LogWarning("Прогрев сборки дерева {Root} не уложился в {Minutes} мин — остановлен",
                        root, Timeout.TotalMinutes);
                    return;
                }
                var output = (await stdout) + (await stderr);
                if (process.ExitCode == 0)
                    log.LogInformation("Прогрев сборки дерева {Root} завершён за {Seconds:F0} с",
                        root, sw.Elapsed.TotalSeconds);
                else
                    log.LogWarning("Прогрев сборки дерева {Root} упал (код {Code}, {Seconds:F0} с): {Tail}",
                        root, process.ExitCode, sw.Elapsed.TotalSeconds, Tail(output));
            }
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Прогрев сборки дерева {Root} оборвался", root);
        }
    }

    private static string Tail(string text)
    {
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return string.Join('\n', lines.TakeLast(20));
    }
}
