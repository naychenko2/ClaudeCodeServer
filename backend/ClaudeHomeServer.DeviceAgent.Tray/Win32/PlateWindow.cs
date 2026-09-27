using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using static ClaudeHomeServer.DeviceAgent.Tray.Win32.Native;

namespace ClaudeHomeServer.DeviceAgent.Tray.Win32;

/// <summary>
/// Плашка «ИИ управляет компьютером» (макет Ш6, раздел 2): правый нижний угол рабочей области
/// основного монитора, отступ 12 px, поверх всех окон. <c>WS_EX_NOACTIVATE</c> — фокус не
/// забирает, <c>WS_EX_TOOLWINDOW</c> — нет в панели задач и Alt+Tab. Если клик рук попадёт в
/// плашку, худший исход — нажатый «Стоп», то есть безопасный.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class PlateWindow : IDisposable
{
    private const string ClassName = "AiHomeAgentTray.Plate";
    private const int BaseWidth = 340;
    private const int BaseHeight = 112;
    private const int BaseMargin = 12;
    private const int BasePad = 14;

    private static readonly uint Background = Rgb(0x26, 0x21, 0x1E);
    private static readonly uint Border = Rgb(0xD9, 0x77, 0x57);
    private static readonly uint TitleColor = Rgb(0xFF, 0xFF, 0xFF);
    private static readonly uint BodyColor = Rgb(0xCF, 0xC6, 0xBF);
    private static readonly uint StopColor = Rgb(0xC4, 0x43, 0x2F);

    private readonly WndProc _proc;
    private readonly Action _onStop;
    private readonly nint _hwnd;
    private readonly double _scale;
    private readonly nint _titleFont;
    private readonly nint _bodyFont;
    private readonly nint _hintFont;
    private PlateView _view = PlateView.Hidden;
    private bool _hoverStop;

    public PlateWindow(Action onStop)
    {
        _onStop = onStop;
        _proc = WindowProc;
        _scale = GetDpiForSystem() / 96.0;
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
        _hwnd = CreateWindowExW(WS_EX_TOPMOST | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE, ClassName, TrayModel.PlateTitle,
            WS_POPUP, 0, 0, S(BaseWidth), S(BaseHeight), 0, 0, instance, 0);
        _titleFont = Font(15, 600);
        _bodyFont = Font(13, 400);
        _hintFont = Font(12, 400);
    }

    public void Show(PlateView view)
    {
        _view = view;
        if (!view.Visible)
        {
            ShowWindow(_hwnd, SW_HIDE);
            return;
        }
        // Рабочая область — без панели задач; перечитывается при каждом показе: монитор могли сменить
        var work = new RECT();
        SystemParametersInfoW(SPI_GETWORKAREA, 0, ref work, 0);
        int width = S(BaseWidth), height = S(BaseHeight), margin = S(BaseMargin);
        SetWindowPos(_hwnd, HWND_TOPMOST, work.Right - width - margin, work.Bottom - height - margin, width, height,
            SWP_NOACTIVATE | SWP_SHOWWINDOW);
        InvalidateRect(_hwnd, 0, true);
    }

    private int S(int value) => (int)Math.Round(value * _scale);

    private nint Font(int px, int weight) =>
        CreateFontW(-S(px), 0, 0, 0, weight, 0, 0, 0, 1 /* DEFAULT_CHARSET */, 0, 0, 5 /* CLEARTYPE_QUALITY */, 0, "Segoe UI");

    private RECT StopRect()
    {
        int pad = S(BasePad), width = S(BaseWidth), height = S(BaseHeight);
        return new RECT(width - pad - S(72), height - pad - S(30), width - pad, height - pad);
    }

    private nint WindowProc(nint hwnd, uint msg, nint wParam, nint lParam)
    {
        switch (msg)
        {
            case WM_MOUSEACTIVATE:
                return MA_NOACTIVATE;
            case WM_MOUSEMOVE:
                var hover = _view.StopVisible && StopRect().Contains(LowWord(lParam), HighWord(lParam));
                if (hover != _hoverStop)
                {
                    _hoverStop = hover;
                    SetCursor(LoadCursorW(0, hover ? IDC_HAND : IDC_ARROW));
                }
                return 0;
            case WM_SETCURSOR when LowWord(lParam) == HTCLIENT:
                SetCursor(LoadCursorW(0, _hoverStop ? IDC_HAND : IDC_ARROW));
                return 1;
            case WM_LBUTTONUP:
                if (_view.StopVisible && StopRect().Contains(LowWord(lParam), HighWord(lParam))) _onStop();
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
        DeleteObject(_titleFont);
        DeleteObject(_bodyFont);
        DeleteObject(_hintFont);
    }
}
