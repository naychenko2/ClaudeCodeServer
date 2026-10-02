using System.Diagnostics;
using System.Runtime.Versioning;
using ClaudeHomeServer.DeviceAgent.Composition;
using ClaudeHomeServer.DeviceAgent.Processes;
using ClaudeHomeServer.DeviceAgent.Tests.Processes;
using ClaudeHomeServer.Services.Execution;

namespace ClaudeHomeServer.DeviceAgent.Tests.Composition;

/// <summary>
/// Процессы вертикалей в агенте (задача 4.3): долгоживущие (терминал, дев-сервер —
/// <see cref="ProcessSpec.Track"/>) живут как ход — своей группой (Unix) или Job Object
/// (Windows) и в журнале ходов; остановка гасит дерево целиком. Короткий git — как есть.
/// </summary>
public sealed class AgentLauncherFactoryTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("agent-launch-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* временная папка */ }
    }

    // Потолок под холодный раннер CI: ждём по часам, а не счётчиком итераций
    private static readonly TimeSpan Ceiling = TimeSpan.FromSeconds(30);

    private static async Task<T> WaitForAsync<T>(Func<T?> probe, string failure) where T : class
    {
        var deadline = DateTime.UtcNow + Ceiling;
        while (true)
        {
            if (probe() is { } found) return found;
            if (DateTime.UtcNow > deadline) throw new TimeoutException(failure);
            await Task.Delay(50);
        }
    }

    private static async Task<int> ReadPidAsync(string file)
    {
        var text = await WaitForAsync(
            () => File.Exists(file) && File.ReadAllText(file).Trim() is var t && int.TryParse(t, out _) ? t : null,
            "внук не записал свой pid");
        return int.Parse(text);
    }

    private static async Task WaitAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + Ceiling;
        while (!condition() && DateTime.UtcNow < deadline) await Task.Delay(50);
    }

    [SkippableFact]
    [UnsupportedOSPlatform("windows")]
    public async Task Unix_Долгоживущий_СвоейГруппой_ВЖурнале_УбиваетсяСВнуком()
    {
        Skip.If(OperatingSystem.IsWindows());
        Skip.If(UnixGroupProcess.FindSetsid() is null, "нет setsid");
        var journal = new TurnJournal(Path.Combine(_dir, "journal"));
        var launchers = new AgentLauncherFactory(journal);
        var pidFile = Path.Combine(_dir, "grandchild.pid");

        var process = launchers.Local.Start(new ProcessSpec
        {
            FileName = "/bin/sh",
            Args = ["-c", $"sleep 600 & echo $! > '{pidFile}'; wait"],
            WorkingDirectory = _dir,
            TurnId = "term-1",
            EnableRaisingEvents = true,
        });
        var grandchild = await ReadPidAsync(pidFile);

        UnixGroupProcess.GroupOf(process.Id).Should().Be(process.Id, "своя группа, как у хода");
        journal.ReadAll().Should().ContainSingle(e => e.TurnId == "term-1" && e.Pid == process.Id);

        launchers.Local.Kill(process, "term-1");
        await process.WaitForExitAsync().WaitAsync(Ceiling);
        await WaitAsync(() => !UnixGroupProcess.IsAlive(grandchild));

        UnixGroupProcess.IsAlive(grandchild).Should().BeFalse("внук терминала умирает с ним");
        journal.ReadAll().Should().BeEmpty();
        launchers.TrackedCount.Should().Be(0);
    }

    [SkippableFact]
    [UnsupportedOSPlatform("windows")]
    public async Task Unix_ДолгоживущийБезTurnId_ВЖурналеПоPid_ЗачисткаПослеСмертиАгентаДобивает()
    {
        Skip.If(OperatingSystem.IsWindows());
        Skip.If(UnixGroupProcess.FindSetsid() is null, "нет setsid");
        var journalDir = Path.Combine(_dir, "journal");
        var launchers = new AgentLauncherFactory(new TurnJournal(journalDir));
        var pidFile = Path.Combine(_dir, "grandchild.pid");

        var process = launchers.Local.Start(new ProcessSpec
        {
            FileName = "/bin/sh",
            Args = ["-c", $"sleep 600 & echo $! > '{pidFile}'; wait"],
            WorkingDirectory = _dir,
            EnableRaisingEvents = true,
        });
        var grandchild = await ReadPidAsync(pidFile);
        new TurnJournal(journalDir).ReadAll().Should().ContainSingle(e => e.TurnId == $"proc-{process.Id}" && e.Pid == process.Id);

        // Агент умер, не убив процесс: новая жизнь читает журнал с диска
        var killed = new TurnJournal(journalDir).SweepLeftovers(new SessionFileJanitor(Path.Combine(_dir, "profile")));
        await process.WaitForExitAsync().WaitAsync(Ceiling);
        await WaitAsync(() => !UnixGroupProcess.IsAlive(grandchild));

        killed.Should().Be(1);
        UnixGroupProcess.IsAlive(grandchild).Should().BeFalse("зачистка бьёт группу целиком");
        new TurnJournal(journalDir).ReadAll().Should().BeEmpty();
    }

    [SkippableFact]
    [UnsupportedOSPlatform("windows")]
    public async Task Unix_Короткий_БезСвоейГруппыИЖурнала()
    {
        Skip.If(OperatingSystem.IsWindows());
        var journal = new TurnJournal(Path.Combine(_dir, "journal"));
        var launchers = new AgentLauncherFactory(journal);

        using var process = launchers.Local.Start(new ProcessSpec
        {
            FileName = "/bin/sh",
            Args = ["-c", "sleep 1"],
            WorkingDirectory = _dir,
            Track = false,
        });

        UnixGroupProcess.GroupOf(process.Id).Should().NotBe(process.Id);
        journal.ReadAll().Should().BeEmpty();
        await process.WaitForExitAsync();
    }

    [SkippableFact]
    [SupportedOSPlatform("windows")]
    public async Task Windows_Долгоживущий_ВJobObject_УбиваетсяСВнуком()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "ветка Windows: Job Object");
        var journal = new TurnJournal(Path.Combine(_dir, "journal"));
        var launchers = new AgentLauncherFactory(journal);
        // PowerShell не берём: на холодном раннере CI он стартует дольше 10 с (тот же урок, что в
        // KillTreeTests). Внук — вложенный cmd, а не ping: иначе его не отличить в снимке от
        // паузного ping, после которого cmd уже сидит в Job (посадка идёт после старта).
        var cmd = Path.Combine(Environment.SystemDirectory, "cmd.exe");
        var script = "ping -n 2 127.0.0.1 >nul & cmd /d /c ping -n 600 127.0.0.1 >nul";

        var process = launchers.Local.Start(new ProcessSpec
        {
            FileName = cmd,
            Args = ["/d", "/c", script],
            WorkingDirectory = _dir,
            TurnId = "term-1",
        });
        using var grandchild = await WaitForAsync(
            () => WindowsSnapshot.ChildrenOf(process.Id, "cmd.exe").Select(OpenAlive).FirstOrDefault(p => p is not null),
            "cmd не породил внука");
        journal.ReadAll().Should().ContainSingle(e => e.TurnId == "term-1");

        launchers.Local.Kill(process, "term-1");
        await process.WaitForExitAsync().WaitAsync(Ceiling);
        await grandchild.WaitForExitAsync().WaitAsync(Ceiling);

        WindowsJobProcess.IsAlive(grandchild.Id).Should().BeFalse();
        journal.ReadAll().Should().BeEmpty();
    }

    private static Process? OpenAlive(int pid)
    {
        try
        {
            var process = Process.GetProcessById(pid);
            if (!process.HasExited) return process;
            process.Dispose();
        }
        catch (ArgumentException) { }
        catch (InvalidOperationException) { }
        return null;
    }
}
