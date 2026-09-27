using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using ClaudeHomeServer.Protocol;
using static ClaudeHomeServer.DeviceAgent.Tray.Win32.Native;

namespace ClaudeHomeServer.DeviceAgent.Tray.Win32;

/// <summary>
/// Значок в области уведомлений, меню и цикл сообщений. Всё состояние — в <see cref="TrayModel"/>;
/// здесь только перевод его в Win32. Кадры pipe приходят из фонового потока и переносятся в поток
/// окна очередью плюс <c>PostMessage</c>.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class TrayApp : IDisposable
{
    private const string ClassName = "AiHomeAgentTray.Main";
    private const uint IconId = 1;
    private const uint WM_TRAY = WM_APP + 1;
    private const uint WM_DISPATCH = WM_APP + 2;
    private const nuint NoticeTimer = 1;
    private const int FirstMenuId = 1000;

    private readonly WndProc _proc;
    private readonly TrayModel _model = new();
    private readonly ConcurrentQueue<Action> _queue = new();
    private readonly Dictionary<TrayIconKind, nint> _icons = [];
    private readonly TrayPipeClient _pipe;
    private readonly nint _hwnd;
    private readonly uint _taskbarCreated;
    private readonly PlateWindow _plate;
    private int _exitCode;

    public TrayApp(string pipeName)
    {
        _proc = WindowProc;
        var instance = GetModuleHandleW(null);
        var wc = new WNDCLASSEX
        {
            cbSize = (uint)Marshal.SizeOf<WNDCLASSEX>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_proc),
            hInstance = instance,
            lpszClassName = ClassName,
        };
        RegisterClassExW(ref wc);
        // Невидимое окно-владелец значка: принимает его сообщения и держит меню на переднем плане
        _hwnd = CreateWindowExW(WS_EX_TOOLWINDOW, ClassName, TrayModel.AppTitle, WS_POPUP, 0, 0, 0, 0, 0, 0, instance, 0);
        _taskbarCreated = RegisterWindowMessageW("TaskbarCreated");

        var size = Math.Max(16, GetSystemMetrics(SM_CXSMICON));
        foreach (var kind in Enum.GetValues<TrayIconKind>()) _icons[kind] = IconFactory.Create(kind, size);

        _plate = new PlateWindow(Stop);
        _model.Changed += Refresh;
        _pipe = new TrayPipeClient(pipeName,
            () => Post(_model.OnConnected),
            () => Post(_model.OnDisconnected),
            message => Post(() =>
            {
                if (_model.Apply(message) is { } notification) Balloon(notification);
            }));
    }

    public int Run()
    {
        SetIcon(NIM_ADD, withTip: true);
        _pipe.Start();
        while (GetMessageW(out var msg, 0, 0, 0) > 0)
        {
            TranslateMessage(ref msg);
            DispatchMessageW(ref msg);
        }
        return _exitCode;
    }

    private void Post(Action action)
    {
        _queue.Enqueue(action);
        PostMessageW(_hwnd, WM_DISPATCH, 0, 0);
    }

    private nint WindowProc(nint hwnd, uint msg, nint wParam, nint lParam)
    {
        switch (msg)
        {
            case WM_DISPATCH:
                while (_queue.TryDequeue(out var action)) action();
                return 0;
            case WM_TRAY:
                var mouse = (uint)LowWord(lParam);
                if (mouse is WM_LBUTTONUP or WM_RBUTTONUP) ShowMenu();
                return 0;
            case WM_TIMER when wParam == (nint)NoticeTimer:
                KillTimer(hwnd, NoticeTimer);
                _model.StoppedNoticeExpired();
                return 0;
            case WM_QUERYENDSESSION:
                return 1;
            case WM_DESTROY:
                var data = IconData(0);
                Shell_NotifyIconW(NIM_DELETE, ref data);
                PostQuitMessage(_exitCode);
                return 0;
        }
        // Проводник перезапустился — значок надо добавить заново
        if (msg == _taskbarCreated && _taskbarCreated != 0)
        {
            SetIcon(NIM_ADD, withTip: true);
            return 0;
        }
        return DefWindowProcW(hwnd, msg, wParam, lParam);
    }

    private void Refresh()
    {
        SetIcon(NIM_MODIFY, withTip: true);
        _plate.Show(_model.Plate);
        if (_model.ShowsStoppedNotice) SetTimer(_hwnd, NoticeTimer, (uint)TrayModel.StoppedNoticeFor.TotalMilliseconds, 0);
    }

    private NOTIFYICONDATA IconData(uint flags) => new()
    {
        cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATA>(),
        hWnd = _hwnd,
        uID = IconId,
        uFlags = flags,
        uCallbackMessage = WM_TRAY,
        hIcon = _icons[_model.Icon],
        szTip = "",
        szInfo = "",
        szInfoTitle = "",
    };

    private void SetIcon(uint message, bool withTip)
    {
        var data = IconData(NIF_MESSAGE | NIF_ICON | (withTip ? NIF_TIP : 0));
        data.szTip = Clip(_model.Tooltip, 127);
        Shell_NotifyIconW(message, ref data);
    }

    private void Balloon(TrayNotification notification)
    {
        var data = IconData(NIF_INFO);
        data.szInfoTitle = Clip(notification.Title, 63);
        data.szInfo = Clip(notification.Text, 255);
        data.dwInfoFlags = NIIF_INFO;
        Shell_NotifyIconW(NIM_MODIFY, ref data);
    }

    private static string Clip(string text, int max) => text.Length <= max ? text : text[..(max - 1)] + "…";

    private void ShowMenu()
    {
        var commands = new List<TrayMenuItem>();
        var menu = Build(_model.Menu(), commands);
        try
        {
            GetCursorPos(out var at);
            // Без переднего плана меню не закрывается кликом мимо (известная особенность TrackPopupMenu)
            SetForegroundWindow(_hwnd);
            var chosen = TrackPopupMenuEx(menu, TPM_RETURNCMD | TPM_RIGHTBUTTON | TPM_NONOTIFY, at.X, at.Y, _hwnd, 0);
            PostMessageW(_hwnd, WM_NULL, 0, 0);
            if (chosen >= FirstMenuId && chosen - FirstMenuId < commands.Count) Execute(commands[chosen - FirstMenuId]);
        }
        finally
        {
            DestroyMenu(menu); // подменю уничтожаются вместе с родителем
        }
    }

    private static nint Build(IReadOnlyList<TrayMenuItem> items, List<TrayMenuItem> commands)
    {
        var menu = CreatePopupMenu();
        foreach (var item in items)
        {
            if (item.IsSeparator)
            {
                AppendMenuW(menu, MF_SEPARATOR, 0, null);
                continue;
            }
            var text = item.Text.Replace("&", "&&");
            var grayed = item.Enabled ? 0 : MF_GRAYED;
            if (item.Children is { Count: > 0 } children)
            {
                AppendMenuW(menu, MF_POPUP | grayed, (nuint)Build(children, commands), text);
                continue;
            }
            var id = (uint)(FirstMenuId + commands.Count);
            commands.Add(item);
            AppendMenuW(menu, MF_STRING | grayed, id, text);
            if (item.IsDefault) SetMenuDefaultItem(menu, id, 0);
        }
        return menu;
    }

    private void Execute(TrayMenuItem item)
    {
        switch (item.Command)
        {
            case TrayCommands.Stop:
                Stop();
                break;
            case TrayCommands.OpenFolder when item.Argument is { } folder && Directory.Exists(folder):
                ShellExecuteW(0, "open", folder, null, null, SW_SHOWNORMAL);
                break;
            case TrayCommands.OpenLog when item.Argument is { } logs:
                var file = Path.Combine(logs, "agent.log");
                ShellExecuteW(0, "open", File.Exists(file) ? file : logs, null, null, SW_SHOWNORMAL);
                break;
            case TrayCommands.Exit:
                var confirm = _model.ExitConfirmation();
                if (MessageBoxW(_hwnd, confirm.Text, confirm.Title, MB_OKCANCEL | MB_ICONWARNING | MB_TOPMOST) != IDOK) return;
                _exitCode = HandsTrayProcess.ExitAgentCode;
                DestroyWindow(_hwnd);
                break;
        }
    }

    /// <summary>«Стоп» — кадр в pipe агента; сервер не нужен.</summary>
    private void Stop()
    {
        if (_model.StopRequest() is not { } request) return;
        _ = Task.Run(async () =>
        {
            if (!await _pipe.SendAsync(request)) Post(() => Balloon(TrayModel.StopUndelivered));
        });
    }

    public void Dispose()
    {
        _pipe.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(5));
        _plate.Dispose();
        foreach (var icon in _icons.Values) DestroyIcon(icon);
    }
}
