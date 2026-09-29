using System.ComponentModel;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using ClaudeHomeServer.HandsBridge.Browser.Cdp;
using ClaudeHomeServer.HandsBridge.Browser.Launch;

namespace ClaudeHomeServer.HandsBridge.Browser;

/// <summary>
/// Chrome браузерной руки и его труба CDP (перенос пробника <c>Launcher.cs</c>/<c>Win32.cs</c>).
/// <list type="bullet">
/// <item>Старт приостановленным, посадка в СВОЙ Job с KILL_ON_JOB_CLOSE (вложен в Job хода) и только
/// потом возобновление: ни один потомок Chrome не рождается вне Job. Поэтому не
/// <see cref="WindowsHandsSystem.AssignToAppsJob"/> — тот сажает уже бегущий процесс.</item>
/// <item>Наследуются ТОЛЬКО два конца труб (<c>PROC_THREAD_ATTRIBUTE_HANDLE_LIST</c>): пробник
/// отдавал Chrome все наследуемые хэндлы моста, а с ними stdio — канал MCP с CLI.</item>
/// </list>
/// Закрытие: трубы закрываются первыми (Chrome выходит сам, по пробнику за ~1,4 с), затем Job —
/// он гасит всё, что не успело выйти.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed partial class ChromeProcess : ICdpTransport, IAsyncDisposable
{
    private static readonly TimeSpan GracefulExit = TimeSpan.FromSeconds(3);

    private const uint CreateSuspended = 0x4;
    private const uint ExtendedStartupInfoPresent = 0x80000;
    private const int StartfUseStdHandles = 0x100;
    private const nint ProcThreadAttributeHandleList = 0x20002;
    private const nint HwndMessage = -3;

    private readonly AnonymousPipeServerStream _toChrome;
    private readonly AnonymousPipeServerStream _fromChrome;
    private readonly nint _process;
    private readonly nint _job;
    private readonly TaskCompletionSource _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly ManualResetEvent _processEvent;
    private readonly RegisteredWaitHandle _exitWait;
    private int _disposed;

    private ChromeProcess(AnonymousPipeServerStream toChrome, AnonymousPipeServerStream fromChrome, nint process, nint job, int processId)
    {
        _toChrome = toChrome;
        _fromChrome = fromChrome;
        _process = process;
        _job = job;
        ProcessId = processId;

        // Хэндл процесса держим сами: PID не переиспользуется, пока он открыт
        _processEvent = new ManualResetEvent(false)
        {
            SafeWaitHandle = new Microsoft.Win32.SafeHandles.SafeWaitHandle(process, ownsHandle: false),
        };
        _exitWait = ThreadPool.RegisterWaitForSingleObject(_processEvent,
            (_, _) => _exited.TrySetResult(), null, Timeout.Infinite, executeOnlyOnce: true);
    }

    public int ProcessId { get; }

    public Stream Read => _fromChrome;

    public Stream Write => _toChrome;

    public Task Exited => _exited.Task;

    public bool HasExited => _exited.Task.IsCompleted;

    /// <summary>Скрытое окно синглтона Chrome: класс <c>Chrome_MessageWindow</c>, заголовок — путь профиля.</summary>
    public static bool MessageWindowExists(string profileDirectory) =>
        FindWindowExW(HwndMessage, 0, "Chrome_MessageWindow", profileDirectory) != 0;

    public static ChromeProcess Start(string chromePath, string profileDirectory)
    {
        // Chrome читает первую трубу и пишет во вторую
        var toChrome = new AnonymousPipeServerStream(PipeDirection.Out, HandleInheritability.Inheritable);
        var fromChrome = new AnonymousPipeServerStream(PipeDirection.In, HandleInheritability.Inheritable);
        nint job = 0;
        try
        {
            var commandLine = ChromeCommandLine.Build(chromePath, ChromeCommandLine.Arguments(
                profileDirectory, toChrome.GetClientHandleAsString(), fromChrome.GetClientHandleAsString()));

            job = WindowsHandsSystem.CreateKillOnCloseJob(out var jobError);
            if (job == 0)
                throw new Win32Exception(jobError, "CreateJobObject");

            nint[] inherit = [toChrome.ClientSafePipeHandle.DangerousGetHandle(), fromChrome.ClientSafePipeHandle.DangerousGetHandle()];
            var (processId, process) = CreateSuspendedInJob(commandLine, Path.GetDirectoryName(chromePath), inherit, job);

            // Без этого EOF на нашей стороне не наступит никогда: копия пишущего конца Chrome жила бы у нас
            toChrome.DisposeLocalCopyOfClientHandle();
            fromChrome.DisposeLocalCopyOfClientHandle();
            return new ChromeProcess(toChrome, fromChrome, process, job, processId);
        }
        catch
        {
            if (job != 0)
                CloseHandle(job);
            toChrome.Dispose();
            fromChrome.Dispose();
            throw;
        }
    }

    /// <summary>
    /// <c>CreateProcess</c> приостановленным с наследованием ровно <paramref name="inherit"/>,
    /// посадка в <paramref name="job"/>, возобновление. Стандартные хэндлы — пустые: без
    /// <c>STARTF_USESTDHANDLES</c> ребёнку ушли бы значения stdio моста.
    /// Рабочий каталог — папка Chrome: иначе он держал бы открытой папку проекта.
    /// </summary>
    private static unsafe (int ProcessId, nint Process) CreateSuspendedInJob(string commandLine, string? workingDirectory, nint[] inherit, nint job)
    {
        nint size = 0;
        _ = InitializeProcThreadAttributeList(0, 1, 0, ref size);
        var attributes = Marshal.AllocHGlobal(size);
        var initialized = false;
        try
        {
            if (!InitializeProcThreadAttributeList(attributes, 1, 0, ref size))
                throw new Win32Exception(Marshal.GetLastPInvokeError(), "InitializeProcThreadAttributeList");
            initialized = true;

            fixed (nint* handles = inherit)
            fixed (char* cwd = workingDirectory)
            {
                if (!UpdateProcThreadAttribute(attributes, 0, ProcThreadAttributeHandleList, (nint)handles,
                        (nint)(sizeof(nint) * inherit.Length), 0, 0))
                    throw new Win32Exception(Marshal.GetLastPInvokeError(), "UpdateProcThreadAttribute");

                var si = new STARTUPINFOEXW();
                si.StartupInfo.cb = sizeof(STARTUPINFOEXW);
                si.StartupInfo.dwFlags = StartfUseStdHandles;
                si.lpAttributeList = attributes;

                // CreateProcessW может править буфер командной строки — нужна своя изменяемая копия
                var cmd = (commandLine + '\0').ToCharArray();
                PROCESS_INFORMATION pi;
                fixed (char* cmdPtr = cmd)
                {
                    if (!CreateProcessW(null, cmdPtr, 0, 0, true, CreateSuspended | ExtendedStartupInfoPresent,
                            0, cwd, &si, &pi))
                        throw new Win32Exception(Marshal.GetLastPInvokeError(), "CreateProcess");
                }

                try
                {
                    if (!AssignProcessToJobObject(job, pi.hProcess))
                    {
                        var error = Marshal.GetLastPInvokeError();
                        TerminateProcess(pi.hProcess, 1);
                        CloseHandle(pi.hProcess);
                        throw new Win32Exception(error, "AssignProcessToJobObject");
                    }

                    if (ResumeThread(pi.hThread) == uint.MaxValue)
                    {
                        var error = Marshal.GetLastPInvokeError();
                        TerminateProcess(pi.hProcess, 1);
                        CloseHandle(pi.hProcess);
                        throw new Win32Exception(error, "ResumeThread");
                    }
                }
                finally
                {
                    CloseHandle(pi.hThread);
                }

                return (pi.dwProcessId, pi.hProcess);
            }
        }
        finally
        {
            if (initialized)
                DeleteProcThreadAttributeList(attributes);
            Marshal.FreeHGlobal(attributes);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        // Закрытая труба — сигнал Chrome выйти самому; Job ниже гасит опоздавших
        await _toChrome.DisposeAsync();
        await _fromChrome.DisposeAsync();
        try
        {
            await _exited.Task.WaitAsync(GracefulExit);
        }
        catch (TimeoutException)
        {
            HandsLog.Write($"браузер: Chrome {ProcessId} не вышел за {GracefulExit.TotalSeconds:0} с после закрытия трубы — гасим Job");
        }

        CloseHandle(_job);
        _exitWait.Unregister(null);
        _processEvent.Dispose();
        CloseHandle(_process);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct STARTUPINFOW
    {
        public int cb;
        public nint lpReserved;
        public nint lpDesktop;
        public nint lpTitle;
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
        public nint lpReserved2;
        public nint hStdInput;
        public nint hStdOutput;
        public nint hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct STARTUPINFOEXW
    {
        public STARTUPINFOW StartupInfo;
        public nint lpAttributeList;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION
    {
        public nint hProcess;
        public nint hThread;
        public int dwProcessId;
        public int dwThreadId;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool CreateProcessW(char* lpApplicationName, char* lpCommandLine, nint lpProcessAttributes,
        nint lpThreadAttributes, [MarshalAs(UnmanagedType.Bool)] bool bInheritHandles, uint dwCreationFlags,
        nint lpEnvironment, char* lpCurrentDirectory, STARTUPINFOEXW* lpStartupInfo, PROCESS_INFORMATION* lpProcessInformation);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool InitializeProcThreadAttributeList(nint lpAttributeList, int dwAttributeCount, int dwFlags, ref nint lpSize);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool UpdateProcThreadAttribute(nint lpAttributeList, uint dwFlags, nint attribute, nint lpValue,
        nint cbSize, nint lpPreviousValue, nint lpReturnSize);

    [LibraryImport("kernel32.dll")]
    private static partial void DeleteProcThreadAttributeList(nint lpAttributeList);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AssignProcessToJobObject(nint hJob, nint hProcess);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial uint ResumeThread(nint hThread);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool TerminateProcess(nint hProcess, uint uExitCode);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint hObject);

    [LibraryImport("user32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint FindWindowExW(nint hWndParent, nint hWndChildAfter, string lpszClass, string? lpszWindow);
}
