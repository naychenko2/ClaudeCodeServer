using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using ClaudeHomeServer.HandsBridge.Policy;

namespace ClaudeHomeServer.HandsBridge;

/// <summary>
/// WinAPI-сторона гейта: вложенные Job моста, в которые <c>app</c> кладёт запущенные программы,
/// и ответы на вопросы политики об окнах и процессах. Любой сбой — «ввод запрещён».
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed partial class WindowsHandsSystem : IHandsWindowSystem, IDisposable
{
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const uint ProcessSetQuota = 0x0100;
    private const uint ProcessTerminate = 0x0001;
    private const int JobObjectExtendedLimitInformation = 9;
    private const uint JobObjectLimitKillOnJobClose = 0x2000;

    // Вложенные Job внутри Job хода: программы рук гаснут вместе с мостом и вместе с ходом.
    // Job хода мост не открывает: он нужен только агенту для KillTree по концу хода
    private readonly List<nint> _appJobs = [];
    private readonly Lock _sync = new();

    /// <summary>
    /// Кладёт только что запущенный процесс в СВОЙ новый Job с KILL_ON_JOB_CLOSE. Job на каждый
    /// запуск, а не общий: первый процесс вложенного Job задаёт его место в иерархии, и программа,
    /// рождённая в другой иерархии (упакованные приложения, Win11-notepad), закрывала общий Job для
    /// всех следующих — AssignProcessToJobObject отдавал ERROR_ACCESS_DENIED (замер на стенде,
    /// задача 0ab253d5). Потомок, успевший родиться до посадки, в Job не попадёт — зато он в Job
    /// хода и погаснет по концу хода вместе со всем деревом.
    /// </summary>
    /// <returns>0 — процесс в Job; иначе код ошибки Windows.</returns>
    public int AssignToAppsJob(Process process)
    {
        var job = CreateKillOnCloseJob(out var createError);
        if (job == 0)
            return createError;

        var handle = OpenProcess(ProcessSetQuota | ProcessTerminate, false, (uint)process.Id);
        if (handle == 0)
            return Fail(job);
        try
        {
            if (!AssignProcessToJobObject(job, handle))
                return Fail(job);
        }
        finally
        {
            CloseHandle(handle);
        }

        lock (_sync)
            _appJobs.Add(job);
        return 0;

        static int Fail(nint job)
        {
            var error = Marshal.GetLastPInvokeError();
            CloseHandle(job);
            return error;
        }
    }

    /// <summary>
    /// Новый Job с KILL_ON_JOB_CLOSE (вложится в Job хода при первой посадке). 0 — сбой,
    /// код ошибки Windows в <paramref name="error"/>.
    /// </summary>
    public static nint CreateKillOnCloseJob(out int error)
    {
        error = 0;
        var job = CreateJobObjectW(0, null);
        if (job == 0)
        {
            error = Marshal.GetLastPInvokeError();
            return 0;
        }

        var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
        info.BasicLimitInformation.LimitFlags = JobObjectLimitKillOnJobClose;
        if (!SetInformationJobObject(job, JobObjectExtendedLimitInformation, ref info, (uint)Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>()))
        {
            error = Marshal.GetLastPInvokeError();
            CloseHandle(job);
            return 0;
        }

        return job;
    }

    public int? GetWindowProcessId(long hwnd)
    {
        _ = GetWindowThreadProcessId((nint)hwnd, out var pid);
        return pid == 0 ? null : (int)pid;
    }

    public string? GetProcessImagePath(int processId)
    {
        var handle = OpenProcess(ProcessQueryLimitedInformation, false, (uint)processId);
        if (handle == 0)
            return null;
        try
        {
            var buffer = new char[32768];
            var size = (uint)buffer.Length;
            return QueryFullProcessImageNameW(handle, 0, buffer, ref size) ? new string(buffer, 0, (int)size) : null;
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            foreach (var job in _appJobs)
                CloseHandle(job);
            _appJobs.Clear();
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public nuint MinimumWorkingSetSize;
        public nuint MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public nuint Affinity;
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
        public nuint ProcessMemoryLimit;
        public nuint JobMemoryLimit;
        public nuint PeakProcessMemoryUsed;
        public nuint PeakJobMemoryUsed;
    }

    [LibraryImport("kernel32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint CreateJobObjectW(nint lpJobAttributes, string? lpName);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetInformationJobObject(nint hJob, int infoClass, ref JOBOBJECT_EXTENDED_LIMIT_INFORMATION info, uint cbInfo);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AssignProcessToJobObject(nint hJob, nint hProcess);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial nint OpenProcess(uint dwDesiredAccess, [MarshalAs(UnmanagedType.Bool)] bool bInheritHandle, uint dwProcessId);

    [LibraryImport("kernel32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool QueryFullProcessImageNameW(nint hProcess, uint dwFlags, [Out] char[] lpExeName, ref uint lpdwSize);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint hObject);

    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial uint GetWindowThreadProcessId(nint hWnd, out uint lpdwProcessId);
}
