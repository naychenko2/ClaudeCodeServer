using System.Runtime.Versioning;
using ClaudeHomeServer.DeviceAgent.Composition;
using ClaudeHomeServer.DeviceAgent.Processes;
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

    private static async Task<int> ReadPidAsync(string file)
    {
        for (var i = 0; i < 100; i++)
        {
            if (File.Exists(file) && int.TryParse(File.ReadAllText(file).Trim(), out var pid)) return pid;
            await Task.Delay(100);
        }
        throw new TimeoutException("внук не записал свой pid");
    }

    private static async Task WaitAsync(Func<bool> condition)
    {
        for (var i = 0; i < 100 && !condition(); i++) await Task.Delay(100);
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
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
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
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
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
        var pidFile = Path.Combine(_dir, "grandchild.pid");
        var powershell = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
        var script = $"$p = Start-Process -FilePath ping.exe -ArgumentList '-n','600','127.0.0.1' -PassThru -WindowStyle Hidden; " +
                     $"Set-Content -Path '{pidFile}' -Value $p.Id; Start-Sleep -Seconds 600";

        var process = launchers.Local.Start(new ProcessSpec
        {
            FileName = powershell,
            Args = ["-NoProfile", "-Command", script],
            WorkingDirectory = _dir,
            TurnId = "term-1",
        });
        var grandchild = await ReadPidAsync(pidFile);
        journal.ReadAll().Should().ContainSingle(e => e.TurnId == "term-1");

        launchers.Local.Kill(process, "term-1");
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        await WaitAsync(() => !WindowsJobProcess.IsAlive(grandchild));

        WindowsJobProcess.IsAlive(grandchild).Should().BeFalse();
        journal.ReadAll().Should().BeEmpty();
    }
}
