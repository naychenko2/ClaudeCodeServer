using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace ConPtyBridge;

/// <summary>
/// P/Invoke-обвязка ConPTY (kernel32). Ручные сигнатуры вместо net10.0-windows:
/// проект остаётся кросс-компилируемым на Linux CI, а исполнение гейтится
/// рантайм-гвардом в Program.cs.
/// </summary>
internal static partial class ConPtyNative
{
    // PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE — атрибут привязки процесса к псевдоконсоли
    public const uint ProcThreadAttributePseudoConsole = 0x00020016;
    // EXTENDED_STARTUPINFO_PRESENT — CreateProcessW получает STARTUPINFOEX
    public const uint ExtendedStartupInfoPresent = 0x00080000;
    // STARTF_USESTDHANDLES — использовать hStd* из STARTUPINFO (мы задаём NULL,
    // чтобы отрубить наследование std родителя через PEB — см. STARTUPINFO)
    public const int StartfUseStdHandles = 0x00000100;

    public const uint Infinite = 0xFFFFFFFF;
    public const uint WaitObject0 = 0;

    [StructLayout(LayoutKind.Sequential)]
    public struct COORD
    {
        public short X;
        public short Y;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct STARTUPINFO
    {
        public int cb;
        public string? lpReserved;
        public string? lpDesktop;
        public string? lpTitle;
        public int dwX;
        public int dwY;
        public int dwXSize;
        public int dwYSize;
        public int dwXCountChars;
        public int dwYCountChars;
        public int dwFillAttribute;
        public int dwFlags;
        public short wShowWindow;
        public short cbReserved2;
        public IntPtr lpReserved2;
        // std-хендлы: ЯВНО NULL + STARTF_USESTDHANDLES. Без этого CreateProcess
        // прокидывает std родителя через PEB даже при bInheritHandles=false —
        // ребёнок (powershell) видит stdin-ПАЙП моста и уходит в неинтерактивный
        // построчный режим (копит скрипт до EOF, Enter/Backspace мертвы), а его
        // stdout/stderr минуют ConPTY. С NULL-хендлами console-subsystem процесс
        // получает хендлы от своей консоли — псевдоконсоли (так чинит Windows Terminal)
        public IntPtr hStdInput;
        public IntPtr hStdOutput;
        public IntPtr hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct STARTUPINFOEX
    {
        public STARTUPINFO StartupInfo;
        public IntPtr lpAttributeList;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct PROCESS_INFORMATION
    {
        public IntPtr hProcess;
        public IntPtr hThread;
        public int dwProcessId;
        public int dwThreadId;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool CreatePipe(out SafeFileHandle hReadPipe, out SafeFileHandle hWritePipe,
        IntPtr lpPipeAttributes, int nSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern int CreatePseudoConsole(COORD size, SafeFileHandle hInput, SafeFileHandle hOutput,
        uint dwFlags, out IntPtr phPC);

    [DllImport("kernel32.dll")]
    public static extern int ResizePseudoConsole(IntPtr hPC, COORD size);

    [DllImport("kernel32.dll")]
    public static extern void ClosePseudoConsole(IntPtr hPC);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool InitializeProcThreadAttributeList(IntPtr lpAttributeList, int dwAttributeCount,
        int dwFlags, ref IntPtr lpSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool UpdateProcThreadAttribute(IntPtr lpAttributeList, uint dwFlags, IntPtr attribute,
        IntPtr lpValue, IntPtr cbSize, IntPtr lpPreviousValue, IntPtr lpReturnSize);

    [DllImport("kernel32.dll")]
    public static extern void DeleteProcThreadAttributeList(IntPtr lpAttributeList);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern bool CreateProcessW(string? lpApplicationName, string lpCommandLine,
        IntPtr lpProcessAttributes, IntPtr lpThreadAttributes, bool bInheritHandles, uint dwCreationFlags,
        IntPtr lpEnvironment, string? lpCurrentDirectory, ref STARTUPINFOEX lpStartupInfo,
        out PROCESS_INFORMATION lpProcessInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern uint WaitForSingleObject(IntPtr hHandle, uint dwMilliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool GetExitCodeProcess(IntPtr hProcess, out uint lpExitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool TerminateProcess(IntPtr hProcess, uint uExitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool CloseHandle(IntPtr hObject);
}
