using System.Diagnostics;
using ClaudeHomeServer.Services.Execution;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Services;

/// <summary>
/// Гашение хода в песочнице (DockerProcessRunner.KillTurnScript) — живьём, тем же sh, что
/// внутри контейнера. Сценарий — run_tests с webServer Playwright'а: группа хода из setsid
/// (как run-turn.sh), внутри неё «стенд» в СВОЕЙ группе и сессии (detached: true). Сигнал
/// одной группе хода стенд не задевает — он оставался сиротой в общем cc-sandbox и держал порт.
///
/// Каждый участник сцены сам называет себя в общий stdout: «S» — стенд (строку пишет уже
/// после setsid(), перед exec sleep), «C» — обычный потомок. Строка стенда в пайпе и есть
/// событие «он в своей группе»: группа читается после него. Прежняя форма — родитель сразу
/// сообщал оба pid, тест сразу читал /proc — краснела под параллельной нагрузкой: fork'нутый
/// потомок не успевает дойти до setsid() и ещё состоит в группе хода (2 падения из 10
/// прогонов под загрузкой, «Did not expect PgidOf(children[0]) to be <pid>»).
///
/// Признак смерти всех — EOF на общем stdout: пайп держит каждый из процессов, и он
/// закрывается только когда умрут все (сигнал, а не опрос по таймеру). Выход лидера таким
/// признаком не является: последняя строка kill-скрипта бьёт по группе хода без условий, и
/// лидер умирает даже когда стенд остался держать пайп.
/// Нужны /proc, setsid, dash и awk — только Linux (CI); на Windows честный skip.
/// </summary>
public sealed class DockerProcessRunnerKillTests : IDisposable
{
    // Потолок guards'а, а не способ ждать: прежнее значение (15 с), одно место. Само
    // ожидание — на сигналах (самоотчёт участника, EOF пайпа); потолок нужен, чтобы
    // несостоявшийся сигнал был красным, а не висящим прогоном.
    private static readonly TimeSpan Ceiling = TimeSpan.FromSeconds(15);

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ccs-kill-" + Guid.NewGuid().ToString("N"));
    private readonly List<int> _pids = [];

    public DockerProcessRunnerKillTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        // Страховка: тест не должен оставить sleep 300 на раннере CI, даже упав
        foreach (var pid in _pids)
            try { using var p = Process.GetProcessById(pid); p.Kill(); } catch { /* уже мёртв */ }
        try { Directory.Delete(_dir, recursive: true); } catch { /* занят */ }
    }

    [SkippableFact]
    public async Task Гашение_ДетачПотомокВСвоейГруппе_ГаситсяВместеСХодом()
    {
        Skip.IfNot(OperatingSystem.IsLinux(), "нужны /proc, setsid, dash и awk — только Linux");
        var pidFile = Path.Combine(_dir, "turn.pid");

        // Как run-turn.sh: лидер группы хода пишет свой pid; внутри — «стенд» через setsid
        // (своя группа и сессия, как detached: true у Playwright) и обычный потомок. Оба
        // сообщают о себе сами; стенд — только после setsid(), поэтому его строка в пайпе
        // невозможна раньше, чем он стал лидером своей группы.
        using var turn = Process.Start(new ProcessStartInfo
        {
            FileName = "setsid",
            ArgumentList =
            {
                "sh", "-c",
                $"echo $$ > '{pidFile}'; "
                + "setsid sh -c 'echo \"S $$\"; exec sleep 300' & "
                + "sleep 300 & echo \"C $!\"; wait",
            },
            RedirectStandardOutput = true,
            UseShellExecute = false,
        })!;

        var selfReported = new TaskCompletionSource<Dictionary<string, int>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var pipeClosed = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var reading = ReadTurnOutputAsync(turn, selfReported, pipeClosed);

        var reported = await selfReported.Task.WaitAsync(Ceiling);
        var (stand, child) = (reported["S"], reported["C"]);
        _pids.Add(turn.Id);
        _pids.Add(stand);
        _pids.Add(child);

        var leader = int.Parse((await File.ReadAllTextAsync(pidFile)).Trim());
        PgidOf(stand).Should().NotBe(leader, "стенд обязан жить в своей группе — иначе сценарий ничего не проверяет");
        PgidOf(child).Should().Be(leader);

        using (var killer = Process.Start("sh", ["-c", DockerProcessRunner.KillTurnScript(pidFile)]))
            await killer.WaitForExitAsync().WaitAsync(Ceiling);

        // Ждём сигнала, а не времени: EOF вернётся, когда пайп закроют все — лидер,
        // обычный потомок и стенд в своей группе. Осиротевший стенд держал бы пайп открытым.
        var tail = await pipeClosed.Task.WaitAsync(Ceiling);
        tail.Should().BeEmpty("после гашения в общем stdout не осталось живых writers");
        await reading;
    }

    [SkippableFact]
    public async Task Гашение_ПустойPidФайл_ТихоВыходит()
    {
        Skip.IfNot(OperatingSystem.IsLinux(), "нужен sh — только Linux");
        var pidFile = Path.Combine(_dir, "missing.pid");

        using var killer = Process.Start("sh", ["-c", DockerProcessRunner.KillTurnScript(pidFile)]);
        await killer.WaitForExitAsync().WaitAsync(Ceiling);

        killer.ExitCode.Should().Be(0, "хода уже нет — гашение не ошибка");
    }

    /// <summary>
    /// Читает общий stdout хода и даёт два сигнала: selfReported — когда оба участника
    /// назвались (порядок строк не важен: их пишут разные процессы), pipeClosed — когда
    /// пайп закрыт всеми (EOF). Ход закрылся раньше самоотчёта — сигнал с причиной.
    /// </summary>
    private static async Task ReadTurnOutputAsync(
        Process turn,
        TaskCompletionSource<Dictionary<string, int>> selfReported,
        TaskCompletionSource<string> pipeClosed)
    {
        try
        {
            var pidByTag = new Dictionary<string, int>();
            while (pidByTag.Count < 2)
            {
                var line = await turn.StandardOutput.ReadLineAsync();
                if (line is null)
                {
                    selfReported.TrySetException(new InvalidOperationException(
                        $"ход закрылся раньше, чем участники сообщили о себе; назвали себя: [{string.Join(", ", pidByTag.Keys)}]"));
                    return;
                }
                var parts = line.Split(' ');
                if (parts.Length == 2 && int.TryParse(parts[1], out var pid))
                    pidByTag[parts[0]] = pid;
            }
            selfReported.TrySetResult(pidByTag);
            pipeClosed.TrySetResult(await turn.StandardOutput.ReadToEndAsync());
        }
        catch (Exception e)
        {
            // Тест мог уйти по упавшему ассерту и уже погасить ход: исключение читателя
            // обязано быть погашено в сигналах, а не висеть в пуле ненаблюдённым.
            selfReported.TrySetException(e);
            pipeClosed.TrySetException(e);
        }
    }

    // Группа процесса — третье поле после «(comm)» в /proc/<pid>/stat
    private static int PgidOf(int pid)
    {
        var stat = File.ReadAllText($"/proc/{pid}/stat");
        return int.Parse(stat[(stat.LastIndexOf(')') + 2)..].Split(' ')[2]);
    }
}
