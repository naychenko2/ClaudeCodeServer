using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using static ClaudeHomeServer.DeviceAgent.Tray.Win32.Native;

namespace ClaudeHomeServer.DeviceAgent.Tray.Win32;

/// <summary>HICON из пикселей <see cref="TrayIconArt"/>: 32-битный цвет с альтой плюс пустая маска.</summary>
[SupportedOSPlatform("windows")]
internal static class IconFactory
{
    public static nint Create(TrayIconKind kind, int size)
    {
        var pixels = TrayIconArt.Render(kind, size);
        var header = new BITMAPINFOHEADER
        {
            biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(),
            biWidth = size,
            biHeight = -size, // строки сверху вниз
            biPlanes = 1,
            biBitCount = 32,
        };
        var screen = GetDC(0);
        var color = CreateDIBSection(screen, ref header, 0, out var bits, 0, 0);
        ReleaseDC(0, screen);
        if (color == 0) return 0;
        Marshal.Copy(Array.ConvertAll(pixels, p => unchecked((int)p)), 0, bits, pixels.Length);

        // Монохромная маска: строки выровнены по 16 бит, нули — прозрачность решает альфа цвета
        var mask = CreateBitmap(size, size, 1, 1, new byte[(size + 15) / 16 * 2 * size]);
        var info = new ICONINFO { fIcon = 1, hbmMask = mask, hbmColor = color };
        var icon = CreateIconIndirect(ref info);
        DeleteObject(color);
        DeleteObject(mask);
        return icon;
    }
}
