using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace ClaudeHomeServer.DeviceAgent.Processes;

/// <summary>
/// Windows: CLI (<c>claude.exe</c> напрямую — <c>cmd /c</c> не проксирует stdin) сажается в
/// собственный Job Object с <c>JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE</c>. Потомки попадают в
/// job автоматически, kill = <c>TerminateJobObject</c>. Хэндл job держит только агент:
/// умер агент — ОС закрыла хэндл — job убит вместе со всем деревом.
///
/// Остаточное окно: .NET не умеет создавать процесс приостановленным, поэтому процесс
/// попадает в job сразу ПОСЛЕ старта. Потомок, порождённый в эти миллисекунды, в job не
/// попадёт — его добивает запасной <c>Process.Kill(entireProcessTree: true)</c>.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class WindowsJobProcess : TurnProcess
{
    private readonly SafeJobHandle _job;
    private int _killed;

    private WindowsJobProcess(Process process, SafeJobHandle job) : base(process)
    {
        _job = job;
        StartTimeUtc = SafeStartTime(process);
    }

    public static new WindowsJobProcess Start(TurnLaunch launch)
    {
        var name = Path.GetFileName(launch.ExecutablePath);
        if (!name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"на Windows запускается только .exe напрямую, а не «{name}»: cmd /c не проксирует stdin");

        var job = CreateKillOnCloseJob();
        var psi = BaseStartInfo(launch.ExecutablePath, launch);
        foreach (var arg in launch.Args) psi.ArgumentList.Add(arg);

        Process? process = null;
        try
        {
            process = Process.Start(psi) ?? throw new InvalidOperationException("процесс CLI не запустился");
            if (!AssignProcessToJobObject(job, process.Handle))
                throw new Win32Exception(Marshal.GetLastPInvokeError(), "процесс CLI не посажен в Job Object");
            return new WindowsJobProcess(process, job);
        }
        catch
        {
            if (process is not null)
            {
                try { process.Kill(entireProcessTree: true); } catch (Exception) { }
                process.Dispose();
            }
            job.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Посадить уже запущенный процесс в свой Job Object (терминал, дев-сервер — задача 4.3):
    /// то же, что у хода, только процесс стартовал чужим кодом. Возвращает хэндл job: его
    /// закрытие добивает всё дерево, как и смерть агента.
    /// </summary>
    internal static Job AttachToNewJob(Process process)
    {
        var job = CreateKillOnCloseJob();
        if (!AssignProcessToJobObject(job, process.Handle))
        {
            var error = Marshal.GetLastPInvokeError();
            job.Dispose();
            throw new Win32Exception(error, "процесс не посажен в Job Object");
        }
        return new Job(job);
    }

    /// <summary>Job Object процесса: убить дерево целиком или отпустить хэндл.</summary>
    internal sealed class Job : IDisposable
    {
        private readonly SafeJobHandle _handle;

        internal Job(SafeJobHandle handle) => _handle = handle;

        public bool Terminate() => !_handle.IsClosed && TerminateJobObject(_handle, 1);

        public void Dispose() => _handle.Dispose();
    }

    public override void KillTree()
    {
        if (Interlocked.Exchange(ref _killed, 1) == 1) return;
        var terminated = !_job.IsClosed && TerminateJobObject(_job, 1);
        // Запасной путь: job не сработал либо часть дерева успела родиться до посадки в job
        if (!terminated || !Process.HasExited) KillTreeFallback();
    }

    public override void Dispose()
    {
        // Закрытие хэндла само добивает всё, что осталось в job (KILL_ON_JOB_CLOSE)
        _job.Dispose();
        base.Dispose();
    }

    private static SafeJobHandle CreateKillOnCloseJob()
    {
        var job = CreateJobObjectW(IntPtr.Zero, null);
        if (job.IsInvalid) throw new Win32Exception(Marshal.GetLastPInvokeError(), "Job Object не создан");

        var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        {
            BasicLimitInformation = new JOBOBJECT_BASIC_LIMIT_INFORMATION { LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE },
        };
        var size = Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>();
        var ptr = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(info, ptr, fDeleteOld: false);
            if (!SetInformationJobObject(job, JobObjectExtendedLimitInformation, ptr, (uint)size))
            {
                var error = Marshal.GetLastPInvokeError();
                job.Dispose();
                throw new Win32Exception(error, "Job Object не настроен на KILL_ON_JOB_CLOSE");
            }
        }
        finally
        {
            Marshal.FreeHGlobal(ptr);
        }
        return job;
    }

    /// <summary>Жив ли процесс (для тестов убийства дерева).</summary>
    public static bool IsAlive(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return !p.HasExited;
        }
        catch (ArgumentException) { return false; }
        catch (InvalidOperationException) { return false; }
    }

    private const int JobObjectExtendedLimitInformation = 9;
    private const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x2000;

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IO_COUNTERS
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    internal sealed class SafeJobHandle() : SafeHandleZeroOrMinusOneIsInvalid(ownsHandle: true)
    {
        protected override bool ReleaseHandle() => CloseHandle(handle);
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeJobHandle CreateJobObjectW(IntPtr lpJobAttributes, string? lpName);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(SafeJobHandle hJob, int infoClass, IntPtr lpInfo, uint cbInfoLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(SafeJobHandle hJob, IntPtr hProcess);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TerminateJobObject(SafeJobHandle hJob, uint uExitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr hObject);
}
