using System.ComponentModel;
using System.Diagnostics;
using ClaudeHomeServer.DeviceAgent.Supervision;

namespace ClaudeHomeServer.DeviceAgent.Install;

/// <summary>Погасить работающий супервизор (переустановка, uninstall).</summary>
internal interface ISupervisorControl
{
    /// <summary>true — супервизор был и погашен; дочерний гибнет вместе с ним (Job/PDEATHSIG).</summary>
    bool Stop();
}

/// <summary>
/// По PID из <c>supervisor.pid</c>. Процесс с этим PID гасится, только если это и правда
/// агент: PID мог достаться чужому процессу после перезагрузки.
/// </summary>
internal sealed class PidFileSupervisorControl(AgentLayout layout) : ISupervisorControl
{
    public bool Stop()
    {
        if (!File.Exists(layout.SupervisorPidFile) || !int.TryParse(File.ReadAllText(layout.SupervisorPidFile).Trim(), out var pid))
            return false;
        try
        {
            using var process = Process.GetProcessById(pid);
            if (!process.ProcessName.StartsWith("ai-home-agent", StringComparison.OrdinalIgnoreCase) || pid == Environment.ProcessId)
                return false;
            process.Kill(entireProcessTree: true);
            process.WaitForExit(TimeSpan.FromSeconds(10));
            return true;
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or Win32Exception)
        {
            return false;
        }
        finally
        {
            try { File.Delete(layout.SupervisorPidFile); } catch (IOException) { }
        }
    }
}

/// <summary>Один супервизор на установку: файловая блокировка (снимается ОС вместе с процессом) плюс PID для uninstall.</summary>
internal sealed class SupervisorLock : IDisposable
{
    private readonly FileStream _lock;
    private readonly string _pidFile;

    private SupervisorLock(FileStream @lock, string pidFile)
    {
        _lock = @lock;
        _pidFile = pidFile;
    }

    public static SupervisorLock? TryAcquire(AgentLayout layout)
    {
        Directory.CreateDirectory(layout.Root);
        FileStream stream;
        try { stream = new FileStream(layout.SupervisorLockFile, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException) { return null; }
        AgentLayout.WriteAtomic(layout.SupervisorPidFile, Environment.ProcessId.ToString());
        return new SupervisorLock(stream, layout.SupervisorPidFile);
    }

    public void Dispose()
    {
        try { File.Delete(_pidFile); } catch (IOException) { }
        _lock.Dispose();
    }
}
