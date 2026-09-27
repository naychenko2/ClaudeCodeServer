using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text.Json;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Sbroenne.WindowsMcp.Tools;

/// <summary>
/// MCP tool for launching applications on Windows.
/// </summary>
[McpServerToolType]
[SupportedOSPlatform("windows")]
public static partial class AppTool
{
    /// <summary>
    /// Launch Windows applications by semantic app name or executable path. Prefer this tool over powershell, shell,
    /// terminal, or command-line process launchers whenever the user asks to open, start, or launch an app.
    /// Use this to start programs like notepad.exe, calc.exe, msedge.exe, chrome.exe, winword.exe, excel.exe, etc.
    /// Returns structured launch status plus a window handle for use with window_management, keyboard_control, and other tools.
    /// Keywords: launch, open, start, run, app, application, program, executable, exe, open app,
    /// start program, launch application, run program, notepad, calculator, browser, edge, chrome.
    /// </summary>
    /// <remarks>
    /// This tool is safer and more reliable than shell commands for app launch because it focuses the window,
    /// waits for the first usable window, handles UWP/Store app stubs such as Calculator, and returns normalized
    /// process/window metadata. Do not use powershell or shell commands to launch apps unless this tool fails or
    /// the task explicitly requires shell execution.
    ///
    /// Examples: app(programPath='notepad.exe'), app(programPath='calc.exe'), app(programPath='msedge.exe', arguments='https://example.com').
    /// After launch, the window is focused and ready for input. Use the returned handle for subsequent operations.
    /// Launch a browser with a URL, then use ui_find/ui_click/ui_type with the returned handle to automate page content.
    /// Edge (msedge.exe) and Chrome (chrome.exe) page content is fully automatable: links, buttons, and form fields
    /// surface as ARIA/visible-text UIA names. Browser chrome (address bar, tabs) is best-effort — use keyboard shortcuts.
    ///
    /// HANDS: any program can be started by the full path to its .exe, except interpreters and terminals (cmd, PowerShell,
    /// Python, Node, bash, Windows Terminal, Explorer...). Launcher stubs that hand off to another process (calc.exe,
    /// Store apps, a browser that is already running) return no handle: find their window with window_management.
    /// </remarks>
    /// <param name="programPath">Full path to the program's .exe (e.g., 'C:\\Program Files\\App\\app.exe'). Names without a folder are not searched in PATH.</param>
    /// <param name="arguments">Command-line arguments for the program (optional). Example: '--new-window' for browsers.</param>
    /// <param name="workingDirectory">Working directory for the launched program (optional).</param>
    /// <param name="waitForWindow">Wait for the application window to appear before returning (default: true). Set to false for background processes.</param>
    /// <param name="timeoutMs">Timeout in milliseconds to wait for the window to appear (default: 5000).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A call result containing a text content block with the JSON payload of the launch operation, including the window handle for subsequent operations. <c>IsError</c> reflects operation success.</returns>
    [McpServerTool(Name = "app", Title = "Launch Application", Destructive = true, OpenWorld = false)]
    public static async partial Task<CallToolResult> ExecuteAsync(
        string programPath,
        [DefaultValue(null)] string? arguments,
        [DefaultValue(null)] string? workingDirectory,
        [DefaultValue(true)] bool waitForWindow,
        [DefaultValue(null)] int? timeoutMs,
        CancellationToken cancellationToken)
    {
        const string actionName = "launch";

        var gate = HandsGate.Policy.CheckLaunch(programPath, arguments);
        if (!gate.Allowed)
        {
            return HandsGate.Deny(HandsTools.App, gate.Reason!);
        }

        try
        {
            var result = await HandleLaunchAsync(gate.ProgramPath, arguments, workingDirectory, waitForWindow, timeoutMs, cancellationToken);
            return ToCallToolResult(result);
        }
        catch (OperationCanceledException)
        {
            var errorResult = WindowManagementResult.CreateFailure(
                WindowManagementErrorCode.Timeout,
                "Operation was cancelled");
            return ToCallToolResult(errorResult);
        }
        catch (Exception ex)
        {
            return ErrorResult(WindowsToolsBase.SerializeToolError(actionName, ex));
        }
    }

    /// <summary>
    /// Converts a window management result into an MCP call result. <see cref="CallToolResult.IsError"/>
    /// mirrors <see cref="WindowManagementResult.Success"/>.
    /// </summary>
    private static CallToolResult ToCallToolResult(WindowManagementResult result) =>
        new()
        {
            Content = [new TextContentBlock { Text = JsonSerializer.Serialize(result, WindowsToolsBase.JsonOptions) }],
            IsError = !result.Success
        };

    /// <summary>
    /// Wraps a pre-serialized JSON error payload in a failed call result.
    /// </summary>
    private static CallToolResult ErrorResult(string json) =>
        new()
        {
            Content = [new TextContentBlock { Text = json }],
            IsError = true
        };

    private static async Task<WindowManagementResult> HandleLaunchAsync(
        string? programPath,
        string? arguments,
        string? workingDirectory,
        bool waitForWindow,
        int? timeoutMs,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(programPath))
        {
            return WindowManagementResult.CreateFailure(
                WindowManagementErrorCode.MissingRequiredParameter,
                "programPath is required. Specify the full path of the program's .exe.");
        }

        var hands = HandsGate.System;
        if (hands is null)
        {
            return WindowManagementResult.CreateFailure(
                WindowManagementErrorCode.SystemError,
                "Hands are not configured on this machine.");
        }

        try
        {
            // Chromium browsers only expose a complete accessibility tree when an assistive-technology
            // client requests it. Force renderer accessibility on launch so ui_find/ui_read/ui_click see
            // full page content (links, buttons, form fields) instead of a reduced/empty tree.
            arguments = AugmentChromiumArguments(programPath, arguments);

            // Руки: без ShellExecute — ни ассоциаций файлов, ни поиска по PATH, ни «открыть через»
            var startInfo = new ProcessStartInfo
            {
                FileName = programPath,
                UseShellExecute = false
            };

            if (!string.IsNullOrWhiteSpace(arguments))
            {
                startInfo.Arguments = arguments;
            }

            if (!string.IsNullOrWhiteSpace(workingDirectory))
            {
                if (!Directory.Exists(workingDirectory))
                {
                    return WindowManagementResult.CreateFailure(
                        WindowManagementErrorCode.InvalidParameter,
                        $"workingDirectory does not exist: '{workingDirectory}'");
                }

                startInfo.WorkingDirectory = workingDirectory;
            }

            HandsLog.Write($"app: запуск '{programPath}' args='{arguments}'");
            var process = Process.Start(startInfo);
            if (process is null)
            {
                HandsLog.Write($"app: Process.Start вернул null для '{programPath}'");
                return WindowManagementResult.CreateFailure(
                    WindowManagementErrorCode.SystemError,
                    $"Failed to start process: '{programPath}'");
            }

            // Программа вне Job моста — неуправляемая: гасим, а не оставляем работать мимо гейта
            var assignError = hands.AssignToAppsJob(process);
            if (assignError != 0)
            {
                process.Refresh();
                var exited = process.HasExited;
                HandsLog.Write($"app: '{programPath}' pid={process.Id} AssignProcessToJobObject {HandsLog.Win32(assignError)}, " +
                    (exited ? $"процесс уже вышел с кодом {process.ExitCode}" : "процесс остановлен"));
                if (exited)
                {
                    return HandOffFailure(programPath, process.ExitCode);
                }

                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                    // Процесс вышел между проверкой и остановкой
                }

                return WindowManagementResult.CreateFailure(
                    WindowManagementErrorCode.SystemError,
                    $"Windows refused to put '{programPath}' under hands control (Windows error {HandsLog.Win32(assignError)}), " +
                    "so it was stopped. This happens when Windows starts a program inside its own job hierarchy " +
                    "(Store-packaged apps and their aliases). Try a regular desktop application.");
            }

            HandsLog.Write($"app: '{programPath}' pid={process.Id} под контролем рук");

            if (waitForWindow)
            {
                var windowService = WindowsToolsBase.WindowService;
                var timeout = timeoutMs ?? WindowsToolsBase.TimeoutMs;
                var deadline = DateTime.UtcNow.AddMilliseconds(timeout);

                // Руки: окно ищется только среди окон этого процесса. Ветки upstream «заглушка
                // вышла — ищем окно по заголовку» и «любое окно процесса с тем же именем» не
                // возвращаем: они выдавали за запущенное окно другой программы. Найти окно по
                // заголовку модель может сама — window_management find/wait_for.
                while (!process.HasExited && DateTime.UtcNow < deadline)
                {
                    await Task.Delay(100, cancellationToken);
                    process.Refresh();

                    var processWindow = await FindProcessWindowAsync(windowService, process, cancellationToken);
                    if (processWindow != null)
                    {
                        return WindowManagementResult.CreateWindowSuccess(
                            processWindow,
                            $"Launched '{programPath}'. Window is focused and ready. Use this handle for all subsequent operations.");
                    }
                }

                if (process.HasExited)
                {
                    HandsLog.Write($"app: '{programPath}' pid={process.Id} вышел с кодом {process.ExitCode}, не показав окна");
                    return HandOffFailure(programPath, process.ExitCode);
                }

                var finalWindow = await FindProcessWindowAsync(windowService, process, cancellationToken);
                if (finalWindow != null)
                {
                    return WindowManagementResult.CreateWindowSuccess(
                        finalWindow,
                        $"Launched '{programPath}'. Window is focused and ready. Use this handle for all subsequent operations.");
                }

                return WindowManagementResult.CreateSuccess(
                    $"Launched '{programPath}' (PID: {process.Id}), but window did not appear within timeout. Use window_management(action='list') to find it.");
            }

            // Not waiting for window - just return success
            return WindowManagementResult.CreateSuccess(
                $"Launched '{programPath}' successfully (PID: {process.Id})");
        }
        catch (System.ComponentModel.Win32Exception ex) when (Logged(programPath, ex) && ex.NativeErrorCode == 2) // ERROR_FILE_NOT_FOUND
        {
            return WindowManagementResult.CreateFailure(
                WindowManagementErrorCode.WindowNotFound,
                $"Program not found: '{programPath}'. The path does not exist on this machine.");
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 5) // ERROR_ACCESS_DENIED
        {
            return WindowManagementResult.CreateFailure(
                WindowManagementErrorCode.AccessDenied,
                $"Access denied when trying to launch '{programPath}'. Check permissions.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (ex is not System.ComponentModel.Win32Exception)
            {
                HandsLog.Write($"app: сбой запуска '{programPath}': {ex.GetType().Name}: {ex.Message}");
            }

            return WindowManagementResult.CreateFailure(
                WindowManagementErrorCode.SystemError,
                $"Failed to launch '{programPath}': {ex.Message}");
        }
    }

    /// <summary>
    /// Заглушка: процесс сразу передал запуск другому процессу (Win11-notepad, calc, приложения
    /// Store, уже запущенный браузер) и вышел. Список заглушек не ведём — ловим по факту выхода.
    /// </summary>
    private static WindowManagementResult HandOffFailure(string programPath, int exitCode) =>
        WindowManagementResult.CreateFailure(
            WindowManagementErrorCode.SystemError,
            $"'{programPath}' handed the launch off to another process and exited right away (exit code {exitCode}), " +
            "so hands have no handle for it. Find the window it opened with window_management(action='find'), " +
            "or start a regular desktop application instead.");

    /// <summary>Код ошибки CreateProcess — в лог; фильтр catch, всегда true.</summary>
    private static bool Logged(string programPath, System.ComponentModel.Win32Exception ex)
    {
        HandsLog.Write($"app: CreateProcess '{programPath}' {HandsLog.Win32(ex.NativeErrorCode)}");
        return true;
    }

    /// <summary>
    /// Окно запущенного процесса: сначала главное, затем любое видимое окно того же PID.
    /// </summary>
    private static async Task<WindowInfoCompact?> FindProcessWindowAsync(
        Window.WindowService windowService,
        Process process,
        CancellationToken cancellationToken)
    {
        if (process.MainWindowHandle != IntPtr.Zero)
        {
            var windowInfo = await windowService.GetWindowInfoAsync(process.MainWindowHandle, cancellationToken);
            if (windowInfo != null)
            {
                return WindowInfoCompact.FromFull(windowInfo);
            }
        }

        var listResult = await windowService.ListWindowsAsync(includeAllDesktops: true, cancellationToken: cancellationToken);
        if (!listResult.Success || listResult.Windows == null)
        {
            return null;
        }

        return listResult.Windows.FirstOrDefault(w => w.ProcessId == process.Id);
    }

    /// <summary>
    /// Known Chromium-based browser executables (without extension) that expose their page
    /// accessibility tree lazily and benefit from --force-renderer-accessibility on launch.
    /// </summary>
    private static readonly string[] ChromiumExecutables =
        ["msedge", "chrome", "brave", "vivaldi", "opera", "chromium"];

    /// <summary>
    /// Appends --force-renderer-accessibility when launching a Chromium browser so its page
    /// accessibility tree is fully populated for UIA-based automation. No-op for other programs
    /// or when the flag is already present.
    /// </summary>
    internal static string? AugmentChromiumArguments(string programPath, string? arguments)
    {
        var name = Path.GetFileNameWithoutExtension(programPath);
        if (string.IsNullOrEmpty(name) ||
            !ChromiumExecutables.Contains(name, StringComparer.OrdinalIgnoreCase))
        {
            return arguments;
        }

        if (arguments != null &&
            arguments.Contains("force-renderer-accessibility", StringComparison.OrdinalIgnoreCase))
        {
            return arguments;
        }

        const string a11yFlag = "--force-renderer-accessibility";
        return string.IsNullOrWhiteSpace(arguments) ? a11yFlag : $"{a11yFlag} {arguments}";
    }
}
