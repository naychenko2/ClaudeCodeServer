using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace BrowserHandsProbe;

[SupportedOSPlatform("windows")]
static class Win32
{
    const uint CREATE_SUSPENDED = 0x4;
    const uint TH32CS_SNAPPROCESS = 0x2;
    const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    const uint PROCESS_VM_READ = 0x10;
    const int JobObjectBasicAccountingInformation = 1;
    const int JobObjectExtendedLimitInformation = 9;
    const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x2000;
    const int ProcessCommandLineInformation = 60;
    const uint SYNCHRONIZE = 0x00100000;
    const uint WAIT_TIMEOUT = 0x102;
    const uint STILL_ACTIVE = 259;

    /// <summary>
    /// Запуск с наследованием хэндлов. Процесс стартует приостановленным: если нужен Job,
    /// он назначается до первой инструкции, иначе ранние потомки Chrome ускользнут из Job.
    /// </summary>
    public static (int Pid, IntPtr ProcessHandle) CreateProcessInherit(string commandLine, IntPtr job)
    {
        var si = new STARTUPINFO { cb = Marshal.SizeOf<STARTUPINFO>() };
        var cmd = new StringBuilder(commandLine);
        if (!CreateProcessW(null, cmd, IntPtr.Zero, IntPtr.Zero, true, CREATE_SUSPENDED,
                IntPtr.Zero, null, ref si, out var pi))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateProcess");
        try
        {
            if (job != IntPtr.Zero && !AssignProcessToJobObject(job, pi.hProcess))
            {
                int err = Marshal.GetLastWin32Error();
                TerminateProcess(pi.hProcess, 1);
                throw new Win32Exception(err, "AssignProcessToJobObject");
            }
            ResumeThread(pi.hThread);
        }
        finally
        {
            CloseHandle(pi.hThread);
        }
        return (pi.dwProcessId, pi.hProcess);
    }

    public static IntPtr CreateKillOnCloseJob()
    {
        var job = CreateJobObjectW(IntPtr.Zero, null);
        if (job == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateJobObject");
        var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
        info.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
        int size = Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>();
        var ptr = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(info, ptr, false);
            if (!SetInformationJobObject(job, JobObjectExtendedLimitInformation, ptr, (uint)size))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "SetInformationJobObject");
        }
        finally
        {
            Marshal.FreeHGlobal(ptr);
        }
        return job;
    }

    public static (uint Total, uint Active, uint Terminated) JobCounters(IntPtr job)
    {
        if (!QueryInformationJobObject(job, JobObjectBasicAccountingInformation, out var a,
                (uint)Marshal.SizeOf<JOBOBJECT_BASIC_ACCOUNTING_INFORMATION>(), IntPtr.Zero))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "QueryInformationJobObject");
        return (a.TotalProcesses, a.ActiveProcesses, a.TotalTerminatedProcesses);
    }

    public static void TerminateJob(IntPtr job)
    {
        if (!TerminateJobObject(job, 1))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "TerminateJobObject");
    }

    public static void CloseHandleSafe(IntPtr h)
    {
        if (h != IntPtr.Zero) CloseHandle(h);
    }

    public static List<(int Pid, int Ppid, string Exe)> SnapshotProcesses()
    {
        var list = new List<(int, int, string)>();
        var snap = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
        if (snap == new IntPtr(-1)) throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateToolhelp32Snapshot");
        try
        {
            var e = new PROCESSENTRY32W { dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32W>() };
            if (!Process32FirstW(snap, ref e)) return list;
            do list.Add(((int)e.th32ProcessID, (int)e.th32ParentProcessID, e.szExeFile));
            while (Process32NextW(snap, ref e));
        }
        finally
        {
            CloseHandle(snap);
        }
        return list;
    }

    /// <summary>Командная строка чужого процесса; null, если прав не хватило.</summary>
    public static string? CommandLine(int pid)
    {
        var h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION | PROCESS_VM_READ, false, pid);
        if (h == IntPtr.Zero) h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h == IntPtr.Zero) return null;
        try
        {
            NtQueryInformationProcess(h, ProcessCommandLineInformation, IntPtr.Zero, 0, out int need);
            if (need <= 0) return null;
            var buf = Marshal.AllocHGlobal(need);
            try
            {
                if (NtQueryInformationProcess(h, ProcessCommandLineInformation, buf, need, out _) != 0) return null;
                // UNICODE_STRING { USHORT Length; USHORT MaximumLength; PWSTR Buffer; }
                int len = (ushort)Marshal.ReadInt16(buf);
                var str = Marshal.ReadIntPtr(buf, IntPtr.Size);
                return Marshal.PtrToStringUni(str, len / 2);
            }
            finally
            {
                Marshal.FreeHGlobal(buf);
            }
        }
        finally
        {
            CloseHandle(h);
        }
    }

    /// <summary>Хэндл для ожидания выхода; 0, если открыть не дали.</summary>
    public static IntPtr OpenForWait(int pid) => OpenProcess(SYNCHRONIZE | PROCESS_QUERY_LIMITED_INFORMATION, false, pid);

    /// <summary>true — процесс вышел (или ждать не дали).</summary>
    public static bool WaitExit(IntPtr h, int ms) => WaitForSingleObject(h, (uint)ms) != WAIT_TIMEOUT;

    public static int? ExitCode(IntPtr h) => GetExitCodeProcess(h, out uint code) && code != STILL_ACTIVE ? unchecked((int)code) : null;

    /// <summary>Скрытое окно синглтона Chrome: класс Chrome_MessageWindow, заголовок — путь профиля.</summary>
    public static bool ChromeMessageWindowExists(string userDataDir) =>
        FindWindowExW(new IntPtr(-3), IntPtr.Zero, "Chrome_MessageWindow", userDataDir) != IntPtr.Zero;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct STARTUPINFO
    {
        public int cb;
        public string? lpReserved, lpDesktop, lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2;
        public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct PROCESS_INFORMATION
    {
        public IntPtr hProcess, hThread;
        public int dwProcessId, dwThreadId;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct PROCESSENTRY32W
    {
        public uint dwSize, cntUsage, th32ProcessID;
        public IntPtr th32DefaultHeapID;
        public uint th32ModuleID, cntThreads, th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szExeFile;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit, PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize, MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass, SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct IO_COUNTERS
    {
        public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount,
            ReadTransferCount, WriteTransferCount, OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct JOBOBJECT_BASIC_ACCOUNTING_INFORMATION
    {
        public long TotalUserTime, TotalKernelTime, ThisPeriodTotalUserTime, ThisPeriodTotalKernelTime;
        public uint TotalPageFaultCount, TotalProcesses, ActiveProcesses, TotalTerminatedProcesses;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool CreateProcessW(string? app, StringBuilder cmd, IntPtr pa, IntPtr ta, bool inherit,
        uint flags, IntPtr env, string? cwd, ref STARTUPINFO si, out PROCESS_INFORMATION pi);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern uint ResumeThread(IntPtr hThread);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool TerminateProcess(IntPtr hProcess, uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool CloseHandle(IntPtr h);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern IntPtr CreateJobObjectW(IntPtr attrs, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool SetInformationJobObject(IntPtr job, int cls, IntPtr info, uint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool QueryInformationJobObject(IntPtr job, int cls, out JOBOBJECT_BASIC_ACCOUNTING_INFORMATION info,
        uint size, IntPtr retLen);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool TerminateJobObject(IntPtr job, uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint pid);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool Process32FirstW(IntPtr snap, ref PROCESSENTRY32W e);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool Process32NextW(IntPtr snap, ref PROCESSENTRY32W e);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern IntPtr OpenProcess(uint access, bool inherit, int pid);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern uint WaitForSingleObject(IntPtr h, uint ms);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool GetExitCodeProcess(IntPtr h, out uint code);

    [DllImport("ntdll.dll")]
    static extern int NtQueryInformationProcess(IntPtr h, int cls, IntPtr buf, int len, out int retLen);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern IntPtr FindWindowExW(IntPtr parent, IntPtr after, string cls, string? window);
}
