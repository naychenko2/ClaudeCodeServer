using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace ClaudeHomeServer.DeviceAgent.Install;

internal enum DetachedStartStatus { Started, BreakawayDenied, Failed }

internal sealed record DetachedStart(DetachedStartStatus Status, int ProcessId = 0, string? Error = null);

/// <summary>Запуск процесса, который переживёт окно и сессию, откуда его запустили.</summary>
internal interface IDetachedStarter
{
    DetachedStart Start(string executable, IReadOnlyList<string> args, string workingDirectory);
}

internal static class WindowsCommandLine
{
    /// <summary>Командная строка Windows: путь и аргументы с пробелами — в кавычках.</summary>
    public static string Build(string executable, IReadOnlyList<string> args) =>
        string.Join(' ', new[] { executable }.Concat(args).Select(a => a.Length > 0 && !a.Any(c => c is ' ' or '\t' or '"') ? a : $"\"{a}\""));
}

/// <summary>
/// Windows: <c>CreateProcess</c> с <c>CREATE_BREAKAWAY_FROM_JOB</c>. Спайк AD-0b:
/// <c>UseShellExecute=true</c> не отсоединяет — из ssh процесс попадал в Job сессии и умирал
/// вместе с ней. Процесс вне Job запускается без флага breakaway (ему не из чего выходить);
/// Job, запрещающий breakaway, — честный отказ <see cref="DetachedStartStatus.BreakawayDenied"/>,
/// а не запуск, который тихо умрёт с окном.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class WindowsDetachedStarter : IDetachedStarter
{
    private const uint CREATE_NEW_PROCESS_GROUP = 0x00000200;
    private const uint CREATE_BREAKAWAY_FROM_JOB = 0x01000000;
    private const uint CREATE_NO_WINDOW = 0x08000000;
    private const int ERROR_ACCESS_DENIED = 5;

    public DetachedStart Start(string executable, IReadOnlyList<string> args, string workingDirectory)
    {
        var inJob = IsProcessInJob(GetCurrentProcess(), IntPtr.Zero, out var result) && result;
        var flags = CREATE_NO_WINDOW | CREATE_NEW_PROCESS_GROUP | (inJob ? CREATE_BREAKAWAY_FROM_JOB : 0);

        // CreateProcessW пишет в буфер командной строки — нужен изменяемый массив
        var commandLine = (WindowsCommandLine.Build(executable, args) + "\0").ToCharArray();
        var startup = new STARTUPINFOW { cb = Marshal.SizeOf<STARTUPINFOW>() };
        if (CreateProcessW(executable, commandLine, IntPtr.Zero, IntPtr.Zero, false, flags, IntPtr.Zero,
                workingDirectory, ref startup, out var info))
        {
            CloseHandle(info.hThread);
            CloseHandle(info.hProcess);
            return new DetachedStart(DetachedStartStatus.Started, info.dwProcessId);
        }

        var error = Marshal.GetLastPInvokeError();
        return inJob && error == ERROR_ACCESS_DENIED
            ? new DetachedStart(DetachedStartStatus.BreakawayDenied, Error: new Win32Exception(error).Message)
            : new DetachedStart(DetachedStartStatus.Failed, Error: new Win32Exception(error).Message);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct STARTUPINFOW
    {
        public int cb;
        public IntPtr lpReserved;
        public IntPtr lpDesktop;
        public IntPtr lpTitle;
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
        public IntPtr hStdInput;
        public IntPtr hStdOutput;
        public IntPtr hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION
    {
        public IntPtr hProcess;
        public IntPtr hThread;
        public int dwProcessId;
        public int dwThreadId;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateProcessW(string lpApplicationName, [In, Out] char[] lpCommandLine,
        IntPtr lpProcessAttributes, IntPtr lpThreadAttributes, [MarshalAs(UnmanagedType.Bool)] bool bInheritHandles,
        uint dwCreationFlags, IntPtr lpEnvironment, string lpCurrentDirectory, ref STARTUPINFOW lpStartupInfo,
        out PROCESS_INFORMATION lpProcessInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsProcessInJob(IntPtr processHandle, IntPtr jobHandle, [MarshalAs(UnmanagedType.Bool)] out bool result);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}
