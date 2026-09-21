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
//
// Прогрев — тяжёлый запуск и идёт под общий потолок BuildConcurrencyGate (три заведённые подряд
// задачи = три `dotnet build -m:4` разом, инцидент oomd 2026-09-21). Слот берётся БЕЗ ожидания:
// свободен — старт как раньше, занят — ожидание уезжает в фон, а вызывающий (заведение дерева
// в ходе чата/задачи) возвращается немедленно. Ждём, а не отменяем: дерево живёт долго, и
// сборка всё равно понадобится — прогрев через минуту ожидания по-прежнему опережает агента.
// Отменяем только потерявший смысл: за время в очереди агент мог начать свою сборку, поэтому
// перед стартом условие проверяется ЗАНОВО (obj/ появился — расходимся, драки за obj/bin нет).
public sealed class WorktreeBuildWarmup(
    ILauncherFactory launchers,
    IConfiguration config,
    ILogger log,
    BuildConcurrencyGate? gate = null)
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
        // Сборка тестового проекта — несколько гигабайт на прогон; под общий потолок
        Heavy = true,
    };

    private BuildConcurrencyGate Gate => gate ?? BuildConcurrencyGate.Instance;

    // Дерево ещё стоит прогревать: тестовый проект на месте и в нём не собирались. Проверяется
    // дважды — при заявке и повторно перед стартом, после ожидания слота (условие устаревает).
    private static bool CanWarm(string root)
    {
        var project = Path.Combine(root, TestsProject.Replace('/', Path.DirectorySeparatorChar));
        return Directory.Exists(project) && !Directory.Exists(Path.Combine(project, "obj"));
    }

    // true — прогрев принят: процесс стартовал либо ждёт слота в фоне. Вызывающий не ждёт ни в
    // одном из случаев.
    public bool TryStart(string? ownerId, string worktreeRoot)
    {
        try
        {
            if (!config.GetValue("Execution:WarmupBuild", true)) return false;
            var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(worktreeRoot));
            if (!CanWarm(root)) return false;
            lock (_warmed)
                if (!_warmed.Add(root)) return false;

            var spec = BuildSpec(root);
            // Слот на месте — стартуем прямо здесь: типичный случай не платит уходом в фон
            if (Gate.TryAcquire(spec) is { } slot)
            {
                Start(ownerId, root, spec, slot);
                return true;
            }
            log.LogInformation(
                "Прогрев сборки дерева {Root} ждёт слота (занято {Limit} тяжёлых запусков)", root, Gate.Limit);
            _ = Task.Run(() => WaitAndStartAsync(ownerId, root, spec));
            return true;
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Прогрев сборки дерева {Root} не запустился", worktreeRoot);
            return false;
        }
    }

    private void Start(string? ownerId, string root, ProcessSpec spec, IDisposable slot)
    {
        // Слот освобождаем на ЛЮБОМ сбое старта, включая резолв среды владельца: не отданный
        // слот утекает до рестарта и навсегда опускает потолок. Дальше его держит наблюдатель
        // до выхода процесса.
        try
        {
            var launcher = launchers.ForOwner(ownerId);
            var process = launcher.Start(spec);
            _ = Task.Run(() => WatchAsync(launcher, process, root, slot));
        }
        catch { slot.Dispose(); throw; }
    }

    private async Task WaitAndStartAsync(string? ownerId, string root, ProcessSpec spec)
    {
        try
        {
            var slot = await Gate.AcquireAsync(spec);
            // Пока стояли в очереди, агент мог начать собирать это дерево сам (появился obj/)
            // или дерево снесли вместе с закрытой задачей — в обоих случаях прогрев уже не
            // ускоряет, а мешает: уступаем слот и расходимся. Причины разводим: «уже собирается»
            // на месте снесённого дерева отправило бы разбирающегося искать несуществующую сборку
            if (!CanWarm(root))
            {
                slot.Dispose();
                log.LogInformation("Прогрев сборки дерева {Root} отменён: {Reason}", root,
                    Directory.Exists(Path.Combine(root, TestsProject.Replace('/', Path.DirectorySeparatorChar)))
                        ? "дерево уже собирается"
                        : "дерева больше нет");
                return;
            }
            Start(ownerId, root, spec, slot);
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Прогрев сборки дерева {Root} не дождался слота", root);
        }
    }

    private async Task WatchAsync(IProcessLauncher launcher, Process process, string root, IDisposable slot)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            using (slot)
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
