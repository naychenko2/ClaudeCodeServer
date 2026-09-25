using System.Runtime.Versioning;
using System.Diagnostics;
using ClaudeHomeServer.DeviceAgent.Processes;
using ClaudeHomeServer.DeviceAgent.Tests.Exec;

namespace ClaudeHomeServer.DeviceAgent.Tests.Processes;

/// <summary>
/// Убийство хода с деревом потомков — обязательно на обеих ОС: фейковый CLI порождает
/// внука, после kill не жив ни один. Linux-CI гоняет Unix-ветку, Windows-CI — Job Object.
/// </summary>
public class KillTreeTests
{
    [SkippableFact]
    [UnsupportedOSPlatform("windows")]
    public async Task Unix_группа_процессов_убивается_целиком()
    {
        Skip.If(OperatingSystem.IsWindows());
        Skip.If(UnixGroupProcess.FindSetsid() is null, "нет setsid");
        var dir = Directory.CreateTempSubdirectory("kill-unix-").FullName;
        try
        {
            var cli = FakeUnixCli.Write(dir);
            using var process = TurnProcess.Start(new TurnLaunch(cli, [], dir, UnixEnv(dir)));
            var grandchild = await ReadPidAsync(Path.Combine(dir, "grandchild.pid"));
            UnixGroupProcess.GroupOf(process.Id).Should().Be(process.Id);

            process.KillTree();
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));

            UnixGroupProcess.IsAlive(process.Id).Should().BeFalse();
            await WaitAsync(() => !UnixGroupProcess.IsAlive(grandchild));
            UnixGroupProcess.IsAlive(grandchild).Should().BeFalse("внук CLI (как MCP-клиент или Bash) умирает с ходом");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [SkippableFact]
    public async Task Windows_Job_Object_убивает_CLI_и_внука()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "ветка Windows: Job Object");
        var dir = Directory.CreateTempSubdirectory("kill-win-").FullName;
        try
        {
            var powershell = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
            var pidFile = Path.Combine(dir, "grandchild.pid");
            // Сын — powershell, внук — ping на 10 минут; сын ждёт, пока его не убьют
            var script = $"$p = Start-Process -FilePath ping.exe -ArgumentList '-n','600','127.0.0.1' -PassThru -WindowStyle Hidden; " +
                         $"Set-Content -Path '{pidFile}' -Value $p.Id; Start-Sleep -Seconds 600";
            using var process = TurnProcess.Start(new TurnLaunch(powershell, ["-NoProfile", "-Command", script], dir, WindowsEnv()));
            var grandchild = await ReadPidAsync(pidFile);
            WindowsIsAlive(grandchild).Should().BeTrue();

            process.KillTree();
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));

            await WaitAsync(() => !WindowsIsAlive(grandchild));
            WindowsIsAlive(process.Id).Should().BeFalse();
            WindowsIsAlive(grandchild).Should().BeFalse("внук в том же Job Object");
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }
    }

    [SkippableFact]
    public void Windows_запускается_только_exe_а_не_cmd()
    {
        Skip.IfNot(OperatingSystem.IsWindows());
        var act = () => TurnProcess.Start(new TurnLaunch(@"C:\tools\claude.cmd", [], Path.GetTempPath(), WindowsEnv()));
        act.Should().Throw<InvalidOperationException>().WithMessage("*cmd /c не проксирует stdin*");
    }

    private static Dictionary<string, string> UnixEnv(string dir) => new()
    {
        ["PATH"] = "/usr/bin:/bin",
        ["HOME"] = dir,
        ["CLAUDE_CONFIG_DIR"] = Path.Combine(dir, "profile"),
    };

    private static Dictionary<string, string> WindowsEnv()
    {
        var env = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in new[] { "PATH", "SystemRoot", "ComSpec", "PATHEXT", "TEMP", "TMP", "USERPROFILE", "APPDATA", "LOCALAPPDATA" })
            if (Environment.GetEnvironmentVariable(name) is { } v) env[name] = v;
        return env;
    }

    private static bool WindowsIsAlive(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return !p.HasExited;
        }
        catch (ArgumentException) { return false; }
        catch (InvalidOperationException) { return false; }
    }

    private static async Task<int> ReadPidAsync(string file)
    {
        for (var i = 0; i < 200; i++)
        {
            if (File.Exists(file) && int.TryParse((await File.ReadAllTextAsync(file)).Trim(), out var pid)) return pid;
            await Task.Delay(50);
        }
        throw new TimeoutException($"фейковый CLI не записал {file}");
    }

    private static async Task WaitAsync(Func<bool> condition)
    {
        for (var i = 0; i < 100 && !condition(); i++) await Task.Delay(50);
    }
}
