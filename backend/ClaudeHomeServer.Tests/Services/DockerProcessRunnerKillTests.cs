using System.Diagnostics;
using ClaudeHomeServer.Services.Execution;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Services;

/// <summary>
/// Гашение хода в песочнице (DockerProcessRunner.KillTurnScript) — живьём, тем же sh, что
/// внутри контейнера. Сценарий — run_tests с webServer Playwright'а: группа хода из setsid
/// (как run-turn.sh), внутри неё «стенд» в СВОЕЙ группе и сессии (detached: true). Сигнал
/// одной группе хода стенд не задевает — он оставался сиротой в общем cc-sandbox и держал порт.
/// Признак смерти всех — EOF на общем stdout: пайп держит каждый из процессов, и он
/// закрывается только когда умрут все (событие, а не опрос по таймеру).
/// Нужны /proc, setsid, dash и awk — только Linux (CI); на Windows честный skip.
/// </summary>
public sealed class DockerProcessRunnerKillTests : IDisposable
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(15);

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
        // (своя группа и сессия, как detached: true у Playwright) и обычный потомок
        using var turn = Process.Start(new ProcessStartInfo
        {
            FileName = "setsid",
            ArgumentList =
            {
                "sh", "-c",
                $"echo $$ > '{pidFile}'; setsid sleep 300 & S=$!; sleep 300 & C=$!; echo \"$S $C\"; wait",
            },
            RedirectStandardOutput = true,
            UseShellExecute = false,
        })!;
        var children = (await turn.StandardOutput.ReadLineAsync().WaitAsync(Wait))!.Split(' ').Select(int.Parse).ToArray();
        _pids.Add(turn.Id);
        _pids.AddRange(children);
        var leader = int.Parse((await File.ReadAllTextAsync(pidFile)).Trim());
        PgidOf(children[0]).Should().NotBe(leader, "стенд обязан жить в своей группе — иначе сценарий ничего не проверяет");
        PgidOf(children[1]).Should().Be(leader);

        using (var killer = Process.Start("sh", ["-c", DockerProcessRunner.KillTurnScript(pidFile)]))
            await killer.WaitForExitAsync().WaitAsync(Wait);

        // EOF — только когда закрыли пайп все: лидер, обычный потомок и стенд
        var eof = turn.StandardOutput.ReadToEndAsync();
        (await Task.WhenAny(eof, Task.Delay(Wait))).Should().BeSameAs(eof,
            "стенд в своей группе обязан погаснуть вместе с ходом, а не остаться сиротой");
    }

    [SkippableFact]
    public async Task Гашение_ПустойPidФайл_ТихоВыходит()
    {
        Skip.IfNot(OperatingSystem.IsLinux(), "нужен sh — только Linux");
        var pidFile = Path.Combine(_dir, "missing.pid");

        using var killer = Process.Start("sh", ["-c", DockerProcessRunner.KillTurnScript(pidFile)]);
        await killer.WaitForExitAsync().WaitAsync(Wait);

        killer.ExitCode.Should().Be(0, "хода уже нет — гашение не ошибка");
    }

    // Группа процесса — третье поле после «(comm)» в /proc/<pid>/stat
    private static int PgidOf(int pid)
    {
        var stat = File.ReadAllText($"/proc/{pid}/stat");
        return int.Parse(stat[(stat.LastIndexOf(')') + 2)..].Split(' ')[2]);
    }
}
