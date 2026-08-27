using System.Runtime.InteropServices;

namespace AiHomeDesktop.Windows.Interop;

/// <summary>
/// Тонкий слой WinAPI грани исполнения: перечисление окон, их геометрия, снятие кадра и
/// состояние рабочего стола. Логики здесь нет намеренно — всё, что можно проверить тестом,
/// живёт в чистых функциях (WindowMatch, ScreenArgs, FrameBudget ядра).
/// </summary>
internal static class NativeMethods
{
    internal delegate bool EnumWindowsProc(nint hWnd, nint lParam);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, nint lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetWindowTextW")]
    internal static extern int GetWindowText(nint hWnd, char[] lpString, int nMaxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetWindowTextLengthW")]
    internal static extern int GetWindowTextLength(nint hWnd);

    [DllImport("user32.dll")]
    internal static extern bool IsWindowVisible(nint hWnd);

    [DllImport("user32.dll")]
    internal static extern bool IsIconic(nint hWnd);

    [DllImport("user32.dll")]
    internal static extern nint GetForegroundWindow();

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern uint GetWindowThreadProcessId(nint hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    internal static extern nint GetWindowLongPtr(nint hWnd, int nIndex);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool GetWindowRect(nint hWnd, out Rect lpRect);

    /// <summary>Снять содержимое окна в контекст рисования. Флаг 2 — PW_RENDERFULLCONTENT.</summary>
    [DllImport("user32.dll")]
    internal static extern bool PrintWindow(nint hWnd, nint hdcBlt, uint nFlags);

    [DllImport("dwmapi.dll")]
    internal static extern int DwmGetWindowAttribute(nint hWnd, int dwAttribute, out Rect pvAttribute, int cbAttribute);

    [DllImport("dwmapi.dll")]
    internal static extern int DwmGetWindowAttribute(nint hWnd, int dwAttribute, out int pvAttribute, int cbAttribute);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern nint OpenInputDesktop(uint dwFlags, bool fInherit, uint dwDesiredAccess);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool CloseDesktop(nint hDesktop);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetUserObjectInformationW", SetLastError = true)]
    internal static extern bool GetUserObjectInformation(nint hObj, int nIndex, char[] pvInfo, int nLength, out int lpnLengthNeeded);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool SetProcessDpiAwarenessContext(nint value);

    [StructLayout(LayoutKind.Sequential)]
    internal struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;

        public readonly int Width => Right - Left;
        public readonly int Height => Bottom - Top;
    }

    // --- константы, которыми пользуется грань ---

    internal const uint PwRenderFullContent = 2;

    internal const int GwlExStyle = -20;
    internal const long WsExToolWindow = 0x0000_0080;

    internal const int DwmwaExtendedFrameBounds = 9;
    internal const int DwmwaCloaked = 14;

    internal const uint DesktopSwitchDesktop = 0x0100;
    internal const int UoiName = 2;

    /// <summary>DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 — иначе координаты кадра врут.</summary>
    internal static readonly nint DpiPerMonitorAwareV2 = -4;
}
