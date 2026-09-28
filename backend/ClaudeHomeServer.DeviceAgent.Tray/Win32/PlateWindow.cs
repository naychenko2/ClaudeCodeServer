using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using static ClaudeHomeServer.DeviceAgent.Tray.Win32.Native;

namespace ClaudeHomeServer.DeviceAgent.Tray.Win32;

/// <summary>
/// Плашка «ИИ управляет компьютером» (макет Ш6, раздел 2): по умолчанию правый нижний угол
/// рабочей области основного монитора, поверх всех окон. <c>WS_EX_NOACTIVATE</c> — фокус не
/// забирает, <c>WS_EX_TOOLWINDOW</c> — нет в панели задач и Alt+Tab. Если клик рук попадёт в
/// плашку, худший исход — нажатый «Стоп», то есть безопасный.
/// <para>
/// Полупрозрачна (<c>WS_EX_LAYERED</c>), под мышью непрозрачна. Тащится за любое место, кроме
/// «Стоп», и запоминает место (<see cref="PlateLayout"/>). Перетаскивание ручное —
/// <c>SetCapture</c> плюс <c>SetWindowPos</c> с <c>SWP_NOACTIVATE</c>, а не <c>HTCAPTION</c>:
/// модальный цикл перемещения <c>DefWindowProc</c> живёт по своим правилам активации, а ввод рук,
/// ушедший в плашку, — ровно то, чего нельзя. Ручной путь проверен вживую: фокус не уходит.
/// <c>WS_EX_TRANSPARENT</c> не ставить: сквозь плашку не нажать «Стоп».
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class PlateWindow : IDisposable
{
    private const string ClassName = "AiHomeAgentTray.Plate";
    private const int BaseWidth = 340;
    private const int BaseHeight = 112;
    private const int BasePad = 14;

    private static readonly uint Background = Rgb(0x26, 0x21, 0x1E);
    private static readonly uint Border = Rgb(0xD9, 0x77, 0x57);
    private static readonly uint TitleColor = Rgb(0xFF, 0xFF, 0xFF);
    private static readonly uint BodyColor = Rgb(0xCF, 0xC6, 0xBF);
    private static readonly uint StopColor = Rgb(0xC4, 0x43, 0x2F);

    private readonly WndProc _proc;
    private readonly Action _onStop;
    private readonly TrayStateStore _state;
    private readonly nint _hwnd;
    private double _scale;
    private nint _titleFont;
    private nint _bodyFont;
    private nint _hintFont;
    private PlateView _view = PlateView.Hidden;
    private bool _hoverStop;
    private bool _hover;
    private bool _tracking;
    private byte _alpha;
    // Перетаскивание: где схватили плашку (от её левого верхнего угла) и откуда она поехала
    private POINT? _grab;
    private RECT _dragFrom;

    public PlateWindow(Action onStop, TrayStateStore state)
    {
        _onStop = onStop;
        _state = state;
        _proc = WindowProc;
        var instance = GetModuleHandleW(null);
        var wc = new WNDCLASSEX
        {
            cbSize = (uint)Marshal.SizeOf<WNDCLASSEX>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_proc),
            hInstance = instance,
            hCursor = LoadCursorW(0, IDC_ARROW),
            lpszClassName = ClassName,
        };
        RegisterClassExW(ref wc);
        ApplyScale(GetDpiForSystem() / 96.0);
        _hwnd = CreateWindowExW(WS_EX_TOPMOST | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_LAYERED, ClassName,
            TrayModel.PlateTitle, WS_POPUP, 0, 0, S(BaseWidth), S(BaseHeight), 0, 0, instance, 0);
        // Слоистое окно без альфы не видно вовсе — задать до первого показа
        UpdateAlpha();
    }

    public void Show(PlateView view)
    {
        var wasVisible = _view.Visible;
        _view = view;
        if (!view.Visible)
        {
            EndDrag();
            ShowWindow(_hwnd, SW_HIDE);
            _hover = _tracking = _hoverStop = false;
            UpdateAlpha();
            return;
        }
        // Модель меняется и без смены видимости (опрос агента раз в несколько секунд): место
        // выбирается только при появлении, иначе плашка прыгала бы из рук человека
        if (!wasVisible) Place(_state.LoadPlate(), SWP_SHOWWINDOW);
        else SetWindowPos(_hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_SHOWWINDOW);
        InvalidateRect(_hwnd, 0, true);
    }

    private void Place(PlatePosition? position, uint flags)
    {
        var placement = PlateLayout.Place(position, Monitors(), BaseWidth, BaseHeight);
        ApplyScale(placement.Monitor.Scale);
        SetWindowPos(_hwnd, HWND_TOPMOST, placement.Left, placement.Top, placement.Width, placement.Height,
            SWP_NOACTIVATE | flags);
    }

    /// <summary>Мониторы перечитываются при каждом показе: их могли отключить или сменить разрешение.</summary>
    private static List<PlateMonitor> Monitors()
    {
        var monitors = new List<PlateMonitor>();
        EnumDisplayMonitors(0, 0, (nint monitor, nint _, ref RECT _, nint _) =>
        {
            var info = new MONITORINFOEX { cbSize = (uint)Marshal.SizeOf<MONITORINFOEX>(), szDevice = "" };
            if (!GetMonitorInfoW(monitor, ref info)) return true;
            var dpi = GetDpiForMonitor(monitor, MDT_EFFECTIVE_DPI, out var dpiX, out _) == 0 && dpiX > 0 ? dpiX : GetDpiForSystem();
            monitors.Add(new PlateMonitor(info.szDevice, ToScreen(info.rcMonitor), ToScreen(info.rcWork), dpi / 96.0,
                (info.dwFlags & MONITORINFOF_PRIMARY) != 0));
            return true;
        }, 0);
        if (monitors.Count == 0)
        {
            // Не должно случаться; без мониторов — хотя бы основной экран по системным метрикам
            var screen = new ScreenRect(0, 0, GetSystemMetrics(0 /* SM_CXSCREEN */), GetSystemMetrics(1 /* SM_CYSCREEN */));
            monitors.Add(new PlateMonitor("", screen, screen, GetDpiForSystem() / 96.0, true));
        }
        return monitors;
    }

    private static ScreenRect ToScreen(RECT rect) => new(rect.Left, rect.Top, rect.Right, rect.Bottom);

    private void ApplyScale(double scale)
    {
        if (scale == _scale && _titleFont != 0) return;
        _scale = scale;
        DeleteFonts();
        _titleFont = Font(15, 600);
        _bodyFont = Font(13, 400);
        _hintFont = Font(12, 400);
    }

    private void DeleteFonts()
    {
        foreach (var font in new[] { _titleFont, _bodyFont, _hintFont })
            if (font != 0) DeleteObject(font);
    }

    private int S(int value) => PlateLayout.Scaled(value, _scale);

    private nint Font(int px, int weight) =>
        CreateFontW(-S(px), 0, 0, 0, weight, 0, 0, 0, 1 /* DEFAULT_CHARSET */, 0, 0, 5 /* CLEARTYPE_QUALITY */, 0, "Segoe UI");

    private RECT StopRect()
    {
        int pad = S(BasePad), width = S(BaseWidth), height = S(BaseHeight);
        return new RECT(width - pad - S(72), height - pad - S(30), width - pad, height - pad);
    }

    private void UpdateAlpha()
    {
        var alpha = PlateLayout.Alpha(_hover, _grab is not null);
        if (alpha == _alpha) return;
        _alpha = alpha;
        SetLayeredWindowAttributes(_hwnd, 0, alpha, LWA_ALPHA);
    }

    /// <summary>Попросить <c>WM_MOUSELEAVE</c>; мышь уже снаружи — он придёт сразу.</summary>
    private void TrackLeave()
    {
        var track = new TRACKMOUSEEVENT
        {
            cbSize = (uint)Marshal.SizeOf<TRACKMOUSEEVENT>(),
            dwFlags = TME_LEAVE,
            hwndTrack = _hwnd,
        };
        _tracking = TrackMouseEvent(ref track);
    }

    private void BeginDrag()
    {
        GetCursorPos(out var at);
        GetWindowRect(_hwnd, out _dragFrom);
        _grab = new POINT { X = at.X - _dragFrom.Left, Y = at.Y - _dragFrom.Top };
        SetCapture(_hwnd);
        UpdateAlpha();
    }

    private void Drag()
    {
        if (_grab is not { } grab) return;
        GetCursorPos(out var at);
        SetWindowPos(_hwnd, 0, at.X - grab.X, at.Y - grab.Y, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
    }

    /// <summary>
    /// Отпустили: место прижимается к рабочей области (или уходит в угол, если плашку утащили за
    /// экран) и запоминается. Размер пересчитывается под DPI монитора, куда её принесли.
    /// </summary>
    private void EndDrag()
    {
        if (_grab is null) return;
        _grab = null; // до ReleaseCapture: он пришлёт WM_CAPTURECHANGED
        ReleaseCapture();
        GetWindowRect(_hwnd, out var rect);
        if (rect.Left != _dragFrom.Left || rect.Top != _dragFrom.Top)
        {
            var monitors = Monitors();
            var here = Nearest(monitors, rect);
            var placement = PlateLayout.Place(PlateLayout.ToSaved(here, rect.Left, rect.Top), monitors, BaseWidth, BaseHeight);
            ApplyScale(placement.Monitor.Scale);
            SetWindowPos(_hwnd, HWND_TOPMOST, placement.Left, placement.Top, placement.Width, placement.Height, SWP_NOACTIVATE);
            InvalidateRect(_hwnd, 0, true);
            _state.SavePlate(PlateLayout.ToSaved(placement.Monitor, placement.Left, placement.Top));
        }
        TrackLeave();
        UpdateAlpha();
    }

    /// <summary>Монитор, на который пришлась большая часть плашки; ни на один — основной.</summary>
    private static PlateMonitor Nearest(List<PlateMonitor> monitors, RECT rect)
    {
        var plate = ToScreen(rect);
        var best = monitors.MaxBy(m => { var i = m.Bounds.Intersect(plate); return (long)i.Width * i.Height; })!;
        var overlap = best.Bounds.Intersect(plate);
        return overlap.Width > 0 && overlap.Height > 0 ? best : monitors.FirstOrDefault(m => m.Primary) ?? monitors[0];
    }

    private nint WindowProc(nint hwnd, uint msg, nint wParam, nint lParam)
    {
        switch (msg)
        {
            case WM_MOUSEACTIVATE:
                return MA_NOACTIVATE;
            case WM_MOUSEMOVE:
                if (!_tracking) TrackLeave();
                _hover = true;
                UpdateAlpha();
                if (_grab is not null)
                {
                    Drag();
                    return 0;
                }
                var hover = _view.StopVisible && StopRect().Contains(LowWord(lParam), HighWord(lParam));
                if (hover != _hoverStop)
                {
                    _hoverStop = hover;
                    SetCursor(LoadCursorW(0, hover ? IDC_HAND : IDC_ARROW));
                }
                return 0;
            case WM_MOUSELEAVE:
                _tracking = false;
                _hover = _hoverStop = false;
                UpdateAlpha();
                return 0;
            case WM_SETCURSOR when LowWord(lParam) == HTCLIENT:
                SetCursor(LoadCursorW(0, _hoverStop ? IDC_HAND : IDC_ARROW));
                return 1;
            case WM_LBUTTONDOWN:
                // «Стоп» срабатывает на отпускании; нажатие на нём перетаскивание не начинает
                if (!(_view.StopVisible && StopRect().Contains(LowWord(lParam), HighWord(lParam)))) BeginDrag();
                return 0;
            case WM_LBUTTONUP:
                if (_grab is not null)
                {
                    EndDrag();
                    return 0;
                }
                if (_view.StopVisible && StopRect().Contains(LowWord(lParam), HighWord(lParam))) _onStop();
                return 0;
            case WM_CAPTURECHANGED:
                // Захват отняли (Esc системы, другое окно) — считаем, что отпустили
                EndDrag();
                return 0;
            case WM_DISPLAYCHANGE:
                // Сменились мониторы или разрешение: запомненное место могло уйти с экрана
                if (_view.Visible && _grab is null) Place(_state.LoadPlate(), 0);
                return 0;
            case WM_PAINT:
                Paint(hwnd);
                return 0;
        }
        return DefWindowProcW(hwnd, msg, wParam, lParam);
    }

    private void Paint(nint hwnd)
    {
        var hdc = BeginPaint(hwnd, out var ps);
        try
        {
            int width = S(BaseWidth), height = S(BaseHeight), pad = S(BasePad);
            var all = new RECT(0, 0, width, height);
            Fill(hdc, all, Border);
            Fill(hdc, new RECT(1, 1, width - 1, height - 1), Background);
            SetBkMode(hdc, TRANSPARENT);

            // Точка «идёт ход» — только пока руки действуют
            var textLeft = pad;
            if (_view.StopVisible)
            {
                Shape(hdc, Border, () => Ellipse(hdc, pad, S(17), pad + S(9), S(26)));
                textLeft = pad + S(16);
            }
            Text(hdc, _view.Title, _titleFont, TitleColor, new RECT(textLeft, S(10), width - pad, S(32)), DT_SINGLELINE | DT_END_ELLIPSIS);
            if (_view.Project is { } project)
                Text(hdc, project, _bodyFont, BodyColor, new RECT(textLeft, S(34), width - pad, S(52)), DT_SINGLELINE | DT_END_ELLIPSIS);
            if (_view.Hint is { } hint)
                Text(hdc, hint, _hintFont, BodyColor, new RECT(textLeft, S(56), width - pad - S(84), height - pad), DT_WORDBREAK);

            if (_view.StopVisible)
            {
                var stop = StopRect();
                Shape(hdc, StopColor, () => RoundRect(hdc, stop.Left, stop.Top, stop.Right, stop.Bottom, S(8), S(8)));
                Text(hdc, TrayModel.StopText, _bodyFont, TitleColor, stop, DT_SINGLELINE | DT_CENTER | DT_VCENTER);
            }
        }
        finally
        {
            EndPaint(hwnd, ref ps);
        }
    }

    private static void Fill(nint hdc, RECT rect, uint color)
    {
        var brush = CreateSolidBrush(color);
        FillRect(hdc, ref rect, brush);
        DeleteObject(brush);
    }

    private static void Shape(nint hdc, uint color, Action draw)
    {
        var brush = CreateSolidBrush(color);
        var oldBrush = SelectObject(hdc, brush);
        var oldPen = SelectObject(hdc, GetStockObject(NULL_PEN));
        draw();
        SelectObject(hdc, oldPen);
        SelectObject(hdc, oldBrush);
        DeleteObject(brush);
    }

    private static void Text(nint hdc, string text, nint font, uint color, RECT rect, int format)
    {
        var old = SelectObject(hdc, font);
        SetTextColor(hdc, color);
        DrawTextW(hdc, text, text.Length, ref rect, format | DT_NOPREFIX);
        SelectObject(hdc, old);
    }

    public void Dispose()
    {
        DestroyWindow(_hwnd);
        DeleteFonts();
    }
}
