using System.Runtime.InteropServices;

namespace Sbroenne.WindowsMcp.Native;

/// <summary>MONITORINFOEX: MONITORINFO плюс имя устройства монитора (<c>\\.\DISPLAY1</c>).</summary>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct MONITORINFOEX
{
    public uint CbSize;
    public RECT RcMonitor;
    public RECT RcWork;
    public uint DwFlags;
    public fixed char SzDevice[32];

    public static MONITORINFOEX Create() => new() { CbSize = (uint)sizeof(MONITORINFOEX) };

    public string DeviceName()
    {
        fixed (char* device = SzDevice)
        {
            return new string(device);
        }
    }
}

/// <summary>Монитор: границы в координатах виртуального экрана, имя устройства, основной ли.</summary>
internal readonly record struct DisplayMonitor(RECT Bounds, string DeviceName, bool IsPrimary);

/// <summary>
/// Руки: замена <c>System.Windows.Forms.Screen.AllScreens</c> прямым вызовом Win32 — мост живёт
/// в каталоге агента без WinForms (ADR-016 §7, замер размера архива агента 2026-09-27).
/// </summary>
internal static partial class DisplayMonitors
{
    [LibraryImport("user32.dll", EntryPoint = "GetMonitorInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetMonitorInfoEx(nint hMonitor, ref MONITORINFOEX lpmi);

    public static List<DisplayMonitor> All()
    {
        var monitors = new List<DisplayMonitor>();

        bool Callback(nint hMonitor, nint hdcMonitor, ref RECT lprcMonitor, nint dwData)
        {
            var info = MONITORINFOEX.Create();
            if (GetMonitorInfoEx(hMonitor, ref info))
            {
                monitors.Add(new DisplayMonitor(info.RcMonitor, info.DeviceName(),
                    (info.DwFlags & MONITORINFO.MONITORINFOF_PRIMARY) != 0));
            }
            return true;
        }

        NativeMethods.EnumDisplayMonitors(0, 0, Callback, 0);
        return monitors;
    }
}
