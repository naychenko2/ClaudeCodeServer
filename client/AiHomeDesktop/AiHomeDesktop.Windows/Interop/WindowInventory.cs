using System.Diagnostics;
using AiHomeDesktop.Windows.Execution;

namespace AiHomeDesktop.Windows.Interop;

/// <summary>
/// Снимок окон верхнего уровня. Только сбор фактов: какое окно выбрать — решает чистая
/// логика <see cref="WindowMatch"/>.
/// </summary>
public static class WindowInventory
{
    /// <summary>Окна с заголовками в Z-order: верхнее первым (порядок EnumWindows).</summary>
    public static IReadOnlyList<WindowInfo> List()
    {
        var self = Environment.ProcessId;
        var windows = new List<WindowInfo>();

        NativeMethods.EnumWindows((hwnd, _) =>
        {
            if (!NativeMethods.IsWindowVisible(hwnd)) return true;

            // Окна-инструменты (панельки, всплывашки) целью не бывают.
            var exStyle = (long)NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GwlExStyle);
            if ((exStyle & NativeMethods.WsExToolWindow) != 0) return true;

            // Cloaked — призрачные окна UWP на другом рабочем столе: видимые по флагу,
            // но не нарисованные нигде.
            if (IsCloaked(hwnd)) return true;

            var title = TitleOf(hwnd);
            if (title.Length == 0) return true;

            NativeMethods.GetWindowThreadProcessId(hwnd, out var pid);
            windows.Add(new WindowInfo(
                hwnd,
                title,
                ProcessNameOf((int)pid),
                (int)pid,
                NativeMethods.IsIconic(hwnd),
                (int)pid == self));
            return true;
        }, 0);

        return windows;
    }

    /// <summary>Активное окно, если оно вообще есть и у него есть заголовок.</summary>
    public static WindowInfo? Foreground()
    {
        var hwnd = NativeMethods.GetForegroundWindow();
        if (hwnd == 0) return null;

        var title = TitleOf(hwnd);
        NativeMethods.GetWindowThreadProcessId(hwnd, out var pid);
        return new WindowInfo(
            hwnd,
            title.Length == 0 ? "(без заголовка)" : title,
            ProcessNameOf((int)pid),
            (int)pid,
            NativeMethods.IsIconic(hwnd),
            (int)pid == Environment.ProcessId);
    }

    private static string TitleOf(nint hwnd)
    {
        var length = NativeMethods.GetWindowTextLength(hwnd);
        if (length <= 0) return "";

        var buffer = new char[length + 1];
        var written = NativeMethods.GetWindowText(hwnd, buffer, buffer.Length);
        return written <= 0 ? "" : new string(buffer, 0, written);
    }

    private static string ProcessNameOf(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return process.ProcessName;
        }
        catch (Exception)
        {
            // Процесс успел закрыться или к нему нет доступа — заголовка окна достаточно.
            return "";
        }
    }

    private static bool IsCloaked(nint hwnd) =>
        NativeMethods.DwmGetWindowAttribute(hwnd, NativeMethods.DwmwaCloaked, out int cloaked, sizeof(int)) == 0
        && cloaked != 0;
}
