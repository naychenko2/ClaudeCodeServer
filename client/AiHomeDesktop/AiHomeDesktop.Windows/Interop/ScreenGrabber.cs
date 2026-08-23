using System.Drawing;
using System.Drawing.Imaging;
using System.Windows.Forms;

namespace AiHomeDesktop.Windows.Interop;

/// <summary>Экран устройства: номер (с единицы), имя и границы в пикселях.</summary>
public sealed record ScreenInfo(int Number, string Name, Rectangle Bounds, bool Primary);

/// <summary>
/// Снятие кадра. Всё, что связано с GDI и DPI, живёт здесь; масштабирование и сжатие —
/// в <see cref="Imaging.FrameEncoder"/>, выбор цели — в чистой логике грани.
/// </summary>
public static class ScreenGrabber
{
    private static bool _dpiApplied;

    /// <summary>
    /// Per-monitor DPI awareness. Без него Windows врёт о координатах и размерах окон на
    /// мониторах с масштабом, и кадр приезжает обрезанным. Приложение обычно ставит режим
    /// манифестом — тогда вызов просто ничего не меняет.
    /// </summary>
    public static void EnsureDpiAwareness()
    {
        if (_dpiApplied) return;
        _dpiApplied = true;
        try
        {
            NativeMethods.SetProcessDpiAwarenessContext(NativeMethods.DpiPerMonitorAwareV2);
        }
        catch (EntryPointNotFoundException)
        {
            // Старая Windows без контекстов DPI — работаем как есть.
        }
    }

    /// <summary>Экраны устройства в порядке Windows; нумерация с единицы — её же видит модель.</summary>
    public static IReadOnlyList<ScreenInfo> Screens()
    {
        EnsureDpiAwareness();
        return Screen.AllScreens
            .Select((s, i) => new ScreenInfo(i + 1, s.DeviceName, s.Bounds, s.Primary))
            .ToList();
    }

    /// <summary>Прямоугольник всех экранов вместе — по нему обрезается запрошенная область.</summary>
    public static Rectangle VirtualBounds() => SystemInformation.VirtualScreen;

    /// <summary>
    /// Кадр окна. Сначала PrintWindow с полной отрисовкой содержимого (берёт и перекрытое
    /// окно), при отказе — копия с экрана по границам окна.
    /// </summary>
    public static Bitmap CaptureWindow(nint hwnd)
    {
        EnsureDpiAwareness();
        if (!NativeMethods.GetWindowRect(hwnd, out var rect) || rect.Width <= 0 || rect.Height <= 0)
            throw new InvalidOperationException("Не удалось получить границы окна");

        var bitmap = new Bitmap(rect.Width, rect.Height, PixelFormat.Format32bppArgb);
        var printed = false;
        using (var graphics = Graphics.FromImage(bitmap))
        {
            var hdc = graphics.GetHdc();
            try
            {
                printed = NativeMethods.PrintWindow(hwnd, hdc, NativeMethods.PwRenderFullContent);
            }
            finally
            {
                graphics.ReleaseHdc(hdc);
            }
        }

        if (printed) return bitmap;

        // Фолбэк: снимаем прямоугольник окна прямо с экрана. Границы берём у DWM — у
        // GetWindowRect в них входит невидимая рамка тени.
        bitmap.Dispose();
        var bounds = FrameBounds(hwnd, rect);
        return CaptureRegion(bounds);
    }

    /// <summary>Кадр экрана целиком.</summary>
    public static Bitmap CaptureScreen(ScreenInfo screen) => CaptureRegion(screen.Bounds);

    /// <summary>Кадр произвольной области виртуального рабочего стола.</summary>
    public static Bitmap CaptureRegion(Rectangle bounds)
    {
        EnsureDpiAwareness();
        if (bounds.Width <= 0 || bounds.Height <= 0)
            throw new InvalidOperationException("Пустая область съёмки");

        var bitmap = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppArgb);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.CopyFromScreen(bounds.Location, Point.Empty, bounds.Size, CopyPixelOperation.SourceCopy);
        return bitmap;
    }

    private static Rectangle FrameBounds(nint hwnd, NativeMethods.Rect fallback)
    {
        if (NativeMethods.DwmGetWindowAttribute(hwnd, NativeMethods.DwmwaExtendedFrameBounds,
                out NativeMethods.Rect frame, System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.Rect>()) == 0
            && frame.Width > 0 && frame.Height > 0)
        {
            return new Rectangle(frame.Left, frame.Top, frame.Width, frame.Height);
        }

        return new Rectangle(fallback.Left, fallback.Top, fallback.Width, fallback.Height);
    }
}
