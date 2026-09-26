using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;
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
    [SupportedOSPlatform("windows")]
    public async Task Windows_Job_Object_убивает_CLI_и_внука()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "ветка Windows: Job Object");
        var dir = Directory.CreateTempSubdirectory("kill-win-").FullName;
        try
        {
            // Сын — cmd, внуки — ping на 10 минут: фоновый (start /b) и передний, на котором cmd
            // ждёт, пока его не убьют. Первый короткий ping — пауза, чтобы внуки родились уже
            // после посадки cmd в job. PowerShell не берём: на раннере CI он молчал дольше 30 с.
            var cmd = Path.Combine(Environment.SystemDirectory, "cmd.exe");
            var script = "ping -n 2 127.0.0.1 >nul & start /b ping -n 600 127.0.0.1 >nul & ping -n 600 127.0.0.1 >nul";
            using var process = TurnProcess.Start(new TurnLaunch(cmd, ["/d", "/c", script], dir, WindowsEnv()));
            process.Process.StandardInput.Close();
            var log = new System.Text.StringBuilder();
            process.Process.OutputDataReceived += (_, e) => { if (e.Data is not null) lock (log) log.AppendLine(e.Data); };
            process.Process.ErrorDataReceived += (_, e) => { if (e.Data is not null) lock (log) log.AppendLine("err: " + e.Data); };
            process.Process.BeginOutputReadLine();
            process.Process.BeginErrorReadLine();

            var grandchildren = await WaitForChildrenAsync(process.Id, "ping.exe", count: 2, () =>
            {
                var state = process.Process.HasExited ? $"вышел с кодом {process.ExitCode}" : "жив";
                lock (log) return $"cmd {state}; вывод:\n{log}";
            });
            grandchildren.Should().OnlyContain(pid => WindowsIsAlive(pid));

            process.KillTree();
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));

            await WaitAsync(() => grandchildren.All(pid => !WindowsIsAlive(pid)));
            WindowsIsAlive(process.Id).Should().BeFalse();
            grandchildren.Should().OnlyContain(pid => !WindowsIsAlive(pid), "внуки в том же Job Object");
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
        for (var i = 0; i < 600; i++)
        {
            if (File.Exists(file) && int.TryParse((await File.ReadAllTextAsync(file)).Trim(), out var pid)) return pid;
            await Task.Delay(50);
        }
        throw new TimeoutException($"фейковый CLI не записал {file}");
    }

    // Внуков ищем снимком процессов по pid родителя, а не файлом от самого фейкового CLI
    [SupportedOSPlatform("windows")]
    private static async Task<int[]> WaitForChildrenAsync(int parentPid, string exeName, int count, Func<string> diagnostics)
    {
        int[] found = [];
        for (var i = 0; i < 600; i++)
        {
            // Только живые: паузный ping — тоже потомок cmd и может ещё висеть в снимке
            found = WindowsSnapshot.ChildrenOf(parentPid, exeName).Where(WindowsIsAlive).ToArray();
            if (found.Length >= count) return found;
            await Task.Delay(50);
        }
        throw new TimeoutException($"у фейкового CLI {found.Length} из {count} потомков {exeName}; {diagnostics()}");
    }

    private static async Task WaitAsync(Func<bool> condition)
    {
        for (var i = 0; i < 100 && !condition(); i++) await Task.Delay(50);
    }

    [SupportedOSPlatform("windows")]
    private static class WindowsSnapshot
    {
        private const uint TH32CS_SNAPPROCESS = 0x2;

        public static int[] ChildrenOf(int parentPid, string exeName)
        {
            using var snapshot = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
            if (snapshot.IsInvalid) throw new Win32Exception(Marshal.GetLastPInvokeError(), "снимок процессов не снят");
            var result = new List<int>();
            var entry = new PROCESSENTRY32W { dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32W>() };
            for (var ok = Process32FirstW(snapshot, ref entry); ok; ok = Process32NextW(snapshot, ref entry))
                if (entry.th32ParentProcessID == parentPid && string.Equals(entry.szExeFile, exeName, StringComparison.OrdinalIgnoreCase))
                    result.Add((int)entry.th32ProcessID);
            return [.. result];
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct PROCESSENTRY32W
        {
            public uint dwSize;
            public uint cntUsage;
            public uint th32ProcessID;
            public UIntPtr th32DefaultHeapID;
            public uint th32ModuleID;
            public uint cntThreads;
            public uint th32ParentProcessID;
            public int pcPriClassBase;
            public uint dwFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
            public string szExeFile;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern SafeFileHandle CreateToolhelp32Snapshot(uint dwFlags, uint th32ProcessID);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool Process32FirstW(SafeFileHandle hSnapshot, ref PROCESSENTRY32W lppe);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool Process32NextW(SafeFileHandle hSnapshot, ref PROCESSENTRY32W lppe);
    }
}
