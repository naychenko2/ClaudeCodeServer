using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using ClaudeHomeServer.HandsBridge.Policy;

namespace ClaudeHomeServer.HandsBridge;

/// <summary>
/// WinAPI-сторона гейта: вложенный Job моста, в который <c>app</c> кладёт запущенные программы,
/// Job хода, по которому решается «своё окно», и ответы на вопросы политики об окнах и процессах.
/// Любой сбой — «чужое».
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed partial class WindowsHandsSystem : IHandsWindowSystem, IDisposable
{
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const uint ProcessSetQuota = 0x0100;
    private const uint ProcessTerminate = 0x0001;
    private const uint JobObjectQuery = 0x0004;
    private const int JobObjectExtendedLimitInformation = 9;
    private const uint JobObjectLimitKillOnJobClose = 0x2000;

    private readonly nint _job;
    private readonly nint _turnJob;

    /// <param name="turnJobName">Имя Job хода от агента; не открылся — своих окон нет.</param>
    public WindowsHandsSystem(string? turnJobName)
    {
        // Вложенный Job внутри Job хода: программы рук гаснут вместе с мостом и вместе с ходом
        _job = CreateJobObjectW(0, null);
        if (_job == 0)
            throw new InvalidOperationException($"CreateJobObject failed: {Marshal.GetLastPInvokeError()}");

        var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
        info.BasicLimitInformation.LimitFlags = JobObjectLimitKillOnJobClose;
        if (!SetInformationJobObject(_job, JobObjectExtendedLimitInformation, ref info, (uint)Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>()))
            throw new InvalidOperationException($"SetInformationJobObject failed: {Marshal.GetLastPInvokeError()}");

        // Только имя из ожидаемого пространства: чужой Job с подобранным именем своим не станет
        if (!string.IsNullOrWhiteSpace(turnJobName)
            && turnJobName.StartsWith(ClaudeHomeServer.Protocol.HandsBridgeArgs.TurnJobPrefix, StringComparison.Ordinal))
        {
            _turnJob = OpenJobObjectW(JobObjectQuery, false, turnJobName);
            if (_turnJob == 0)
                Console.Error.WriteLine($"[hands] turn job '{turnJobName}' is not available ({Marshal.GetLastPInvokeError()}): no window is available to hands.");
        }
    }

    /// <summary>
    /// Кладёт только что запущенный процесс в Job. Потомок, успевший родиться до этого, в Job не
    /// попадёт — его окна будут чужими: гонка ошибается только в сторону запрета.
    /// </summary>
    public bool AssignToAppsJob(Process process)
    {
        var handle = OpenProcess(ProcessSetQuota | ProcessTerminate, false, (uint)process.Id);
        if (handle == 0)
            return false;
        try
        {
            return AssignProcessToJobObject(_job, handle);
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    public int? GetWindowProcessId(long hwnd)
    {
        _ = GetWindowThreadProcessId((nint)hwnd, out var pid);
        return pid == 0 ? null : (int)pid;
    }

    public bool IsProcessInTurnJob(int processId)
    {
        if (_turnJob == 0)
            return false;
        var handle = OpenProcess(ProcessQueryLimitedInformation, false, (uint)processId);
        if (handle == 0)
            return false;
        try
        {
            // Процесс вложенного Job моста состоит и в Job хода: вложенность Job наследует членство вверх
            return IsProcessInJob(handle, _turnJob, out var inJob) && inJob;
        }
        finally
        {
            CloseHandle(handle);
        }
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
        CloseHandle(_job);
        if (_turnJob != 0)
            CloseHandle(_turnJob);
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

    [LibraryImport("kernel32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint OpenJobObjectW(uint dwDesiredAccess, [MarshalAs(UnmanagedType.Bool)] bool bInheritHandle, string lpName);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetInformationJobObject(nint hJob, int infoClass, ref JOBOBJECT_EXTENDED_LIMIT_INFORMATION info, uint cbInfo);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AssignProcessToJobObject(nint hJob, nint hProcess);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsProcessInJob(nint processHandle, nint jobHandle, [MarshalAs(UnmanagedType.Bool)] out bool result);

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
