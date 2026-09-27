using System.Globalization;
using ClaudeHomeServer.Protocol;

namespace ClaudeHomeServer.HandsBridge.Policy;

/// <summary>Инструменты моста. Любой другой в сборке — дефект (сторож — HandsToolSurfaceGuardTests).</summary>
public static class HandsTools
{
    public const string App = "app";
    public const string UiSnapshot = "ui_snapshot";
    public const string UiFind = "ui_find";
    public const string UiClick = "ui_click";
    public const string UiType = "ui_type";
    public const string UiRead = "ui_read";
    public const string WindowManagement = "window_management";
    public const string ScreenshotControl = "screenshot_control";

    public static readonly IReadOnlyList<string> All =
        [App, UiSnapshot, UiFind, UiClick, UiType, UiRead, WindowManagement, ScreenshotControl];
}

/// <summary>
/// Единственный гейт рук: его зовёт каждый инструмент моста ДО любого действия.
/// <para>
/// Своё окно (решение владельца 2б, 2026-09-27) — окно процесса из Job хода: программа,
/// запущенная <c>app</c> (вложенный Job моста живёт внутри Job хода), командой этого хода или
/// их потомок. Белого списка программ нет. Окно процесса из <see cref="HandsForbiddenApps"/>
/// своим не бывает никогда: ввод в терминал — это исполнение команд. Владеемые диалоги своего
/// окна свои, потому что живут в том же процессе; диалог, который владеемым сделал ЧУЖОЙ
/// процесс, — чужой.
/// </para>
/// Граница — защита от ошибок модели (не лезть в открытые окна человека), а не от злоумышленника.
/// Закрыто по умолчанию: сбой WinAPI или мост вне Job хода — всё читается как «чужое».
/// </summary>
public sealed class HandsPolicy(IHandsWindowSystem windows)
{
    private const string UseOwnWindow =
        "Only windows of programs started in this turn are available. " +
        "Start a program with app(programPath=...) and use the handle it returns, " +
        "or call window_management(action='list') to see your windows.";

    /// <summary>
    /// Ключи Chromium, которые подменяют исполняемый файл дочерних процессов или открывают
    /// управление браузером снаружи: с ними браузер становится запускалкой команд.
    /// </summary>
    private static readonly string[] ForbiddenArgumentFragments =
        ["cmd-prefix", "gpu-launcher", "subprocess-path", "remote-debugging", "load-extension"];

    /// <summary>
    /// Действия <c>window_management</c>, которым нужен свой hwnd. <c>list</c>/<c>find</c>
    /// разрешены, но отдают только свои окна; <c>get_foreground</c> сверяется по результату.
    /// </summary>
    private static readonly HashSet<string> HandleActions = new(StringComparer.Ordinal)
    {
        "activate", "minimize", "maximize", "restore", "close", "move", "resize", "set_bounds",
        "move_to_monitor", "get_state", "wait_for_state", "move_and_activate", "ensure_visible",
    };

    // ---------- app ----------

    /// <summary>
    /// Запуск: полный путь <c>.exe</c> после нормализации, без поиска по <c>PATH</c>;
    /// интерпретаторы и терминалы (<see cref="HandsForbiddenApps"/>) — никогда.
    /// </summary>
    public HandsLaunchDecision CheckLaunch(string? programPath, string? arguments)
    {
        var normalized = HandsAppPaths.TryNormalize(programPath);
        if (normalized is null)
            return HandsLaunchDecision.Deny(
                "programPath must be a full path to an .exe (for example C:\\Program Files\\App\\app.exe); " +
                "names without a folder are not searched in PATH.");

        if (HandsAppPaths.IsForbidden(normalized))
            return HandsLaunchDecision.Deny(
                $"'{HandsAppPaths.FileName(normalized)}' is an interpreter or a terminal: it runs arbitrary commands " +
                "and is never started by hands. Start the program you need directly by its .exe.");

        if (!HandsAppPaths.IsExecutable(normalized))
            return HandsLaunchDecision.Deny("Only .exe programs can be started.");

        if (arguments is not null)
        {
            foreach (var fragment in ForbiddenArgumentFragments)
            {
                if (arguments.Contains(fragment, StringComparison.OrdinalIgnoreCase))
                    return HandsLaunchDecision.Deny(
                        $"Argument '{fragment}' is not allowed: it turns the program into a command launcher. " +
                        "Pass a URL or a file path instead.");
            }
        }

        // Запускаем ровно проверенную строку, а не как путь прислала модель
        return HandsLaunchDecision.Allow(normalized);
    }

    // ---------- окна ----------

    /// <summary>Своё окно по правилу из шапки класса.</summary>
    public bool IsOwnWindow(long hwnd)
    {
        if (hwnd == 0)
            return false;
        var processId = windows.GetWindowProcessId(hwnd);
        if (processId is not { } pid || !windows.IsProcessInTurnJob(pid))
            return false;

        var image = HandsAppPaths.TryNormalize(windows.GetProcessImagePath(pid));
        return image is not null && !HandsAppPaths.IsForbidden(image);
    }

    /// <summary>То же для hwnd строкой, как его передаёт модель.</summary>
    public bool IsOwnWindow(string? handle) => TryParseHandle(handle, out var hwnd) && IsOwnWindow(hwnd);

    /// <summary>Только свои окна — для <c>list</c>, <c>find</c> и поиска окна после запуска.</summary>
    public IReadOnlyList<T> FilterOwn<T>(IEnumerable<T> items, Func<T, string?> handleOf) =>
        items.Where(item => TryParseHandle(handleOf(item), out var hwnd) && IsOwnWindow(hwnd)).ToList();

    /// <summary>
    /// <c>ui_*</c>: окно обязано быть своим, и каждый переданный <c>elementId</c> — из своего окна
    /// (идентификатор элемента несёт hwnd своего корня; не разрешился — отказ, а не «поверим»).
    /// </summary>
    /// <param name="resolveElementWindow">hwnd корня элемента по его id; null — id неизвестен.</param>
    public HandsDecision CheckUi(
        string tool,
        string? windowHandle,
        Func<string, long?> resolveElementWindow,
        params string?[] elementIds)
    {
        if (string.IsNullOrWhiteSpace(windowHandle))
            return HandsDecision.Deny($"{tool} requires windowHandle of your own window. {UseOwnWindow}");

        if (!IsOwnWindow(windowHandle))
            return HandsDecision.Deny($"Window '{windowHandle}' is not yours. {UseOwnWindow}");

        foreach (var elementId in elementIds)
        {
            if (string.IsNullOrWhiteSpace(elementId))
                continue;
            var elementWindow = resolveElementWindow(elementId);
            if (elementWindow is not { } hwnd)
                return HandsDecision.Deny(
                    $"Element '{elementId}' is unknown. Take a fresh ui_snapshot of your window and use its ids.");
            if (!IsOwnWindow(hwnd))
                return HandsDecision.Deny($"Element '{elementId}' belongs to a window that is not yours. {UseOwnWindow}");
        }

        return HandsDecision.Allow;
    }

    /// <summary>
    /// <c>window_management</c>. Поиска и ожидания окна по заголовку по всему столу нет:
    /// <c>wait_for</c> получает отказ, <c>find</c> ищет только среди своих.
    /// </summary>
    /// <param name="action">Имя действия в snake_case, как в схеме инструмента.</param>
    public HandsDecision CheckWindowAction(string action, string? handle)
    {
        switch (action)
        {
            case "list":
            case "find":
            case "get_foreground":
                return HandsDecision.Allow;
            case "wait_for":
                return HandsDecision.Deny(
                    "wait_for searches every window on the desktop by title and is disabled. " +
                    "Use window_management(action='list') to see your windows, or wait_for_state with your handle.");
        }

        if (!HandleActions.Contains(action))
            return HandsDecision.Deny($"Unknown window_management action '{action}'.");

        if (string.IsNullOrWhiteSpace(handle))
            return HandsDecision.Deny($"{action} requires the handle of your own window. {UseOwnWindow}");

        return IsOwnWindow(handle)
            ? HandsDecision.Allow
            : HandsDecision.Deny($"Window '{handle}' is not yours. {UseOwnWindow}");
    }

    /// <summary><c>get_foreground</c> отдаёт окно, только если на переднем плане своё.</summary>
    public HandsDecision CheckForegroundResult(string? handle) =>
        IsOwnWindow(handle)
            ? HandsDecision.Allow
            : HandsDecision.Deny($"The foreground window is not yours, its details are hidden. {UseOwnWindow}");

    // ---------- снимок ----------

    /// <summary>
    /// <c>screenshot_control</c>: снимок — только <c>target='window'</c> своего окна и только в
    /// ответ (режим <c>file</c> пишет по произвольному пути машины). <c>outputPath</c> отвергается
    /// при любом режиме: снимок с разметкой пишет по нему файл и в режиме <c>inline</c>. Экран,
    /// монитор, область и все мониторы — отказ. Список мониторов пикселей не несёт и разрешён.
    /// </summary>
    public HandsDecision CheckScreenshot(string? action, string? target, string? windowHandle, string? outputMode, string? outputPath)
    {
        if (string.Equals(action, "list_monitors", StringComparison.OrdinalIgnoreCase))
            return HandsDecision.Allow;

        if (!string.IsNullOrWhiteSpace(action) && !string.Equals(action, "capture", StringComparison.OrdinalIgnoreCase))
            return HandsDecision.Deny($"Unknown screenshot_control action '{action}'.");

        if (!string.Equals(target, "window", StringComparison.OrdinalIgnoreCase))
            return HandsDecision.Deny(
                $"Screenshots of '{(string.IsNullOrWhiteSpace(target) ? "primary_screen" : target)}' are not allowed: " +
                "only target='window' with the handle of your own window. " + UseOwnWindow);

        if (!string.IsNullOrWhiteSpace(outputMode) && !string.Equals(outputMode, "inline", StringComparison.OrdinalIgnoreCase))
            return HandsDecision.Deny("Only outputMode='inline' is allowed: screenshots are not written to disk.");

        if (!string.IsNullOrEmpty(outputPath))
            return HandsDecision.Deny("outputPath is not allowed: screenshots are not written to disk. Omit it.");

        if (string.IsNullOrWhiteSpace(windowHandle))
            return HandsDecision.Deny($"target='window' requires windowHandle of your own window. {UseOwnWindow}");

        return IsOwnWindow(windowHandle)
            ? HandsDecision.Allow
            : HandsDecision.Deny($"Window '{windowHandle}' is not yours. {UseOwnWindow}");
    }

    // ---------- клавиатура ----------

    /// <summary>Виртуальные коды клавиш Win32, которые участвуют в системных сочетаниях.</summary>
    public static class VirtualKeys
    {
        public const int Tab = 0x09;
        public const int Control = 0x11;
        public const int Menu = 0x12;
        public const int Escape = 0x1B;
        public const int LeftWin = 0x5B;
        public const int RightWin = 0x5C;
        public const int LeftControl = 0xA2;
        public const int RightControl = 0xA3;
        public const int LeftMenu = 0xA4;
        public const int RightMenu = 0xA5;
    }

    /// <summary>
    /// Системные сочетания не уходят никогда: любое с Win, клавиша Win сама по себе, Alt+Tab,
    /// Alt+Esc, Ctrl+Esc, Ctrl+Shift+Esc и всё с Ctrl+Alt. Они уводят ввод из своего окна
    /// («Пуск», переключение окон, диспетчер задач, экран безопасности). Проверка — по
    /// виртуальному коду ПОСЛЕ сопоставления имени: синонимы вроде «meta»/«super» её не обходят.
    /// </summary>
    public static HandsDecision CheckKeys(int virtualKey, bool ctrl, bool alt, bool shift, bool win)
    {
        ctrl |= virtualKey is VirtualKeys.Control or VirtualKeys.LeftControl or VirtualKeys.RightControl;
        alt |= virtualKey is VirtualKeys.Menu or VirtualKeys.LeftMenu or VirtualKeys.RightMenu;
        win |= virtualKey is VirtualKeys.LeftWin or VirtualKeys.RightWin;

        if (win)
            return HandsDecision.Deny("Key combinations with the Windows key are not allowed.");
        if (ctrl && alt)
            return HandsDecision.Deny("Ctrl+Alt combinations are not allowed.");
        if (alt && virtualKey is VirtualKeys.Tab or VirtualKeys.Escape)
            return HandsDecision.Deny("Alt+Tab and Alt+Esc switch away from your window and are not allowed.");
        if (ctrl && virtualKey == VirtualKeys.Escape)
            return HandsDecision.Deny(shift
                ? "Ctrl+Shift+Esc opens Task Manager and is not allowed."
                : "Ctrl+Esc opens the Start menu and is not allowed.");

        return HandsDecision.Allow;
    }

    /// <summary>
    /// Зажать можно только обычную клавишу: зажатые Win, Ctrl или Alt собрали бы системное
    /// сочетание из следующих нажатий в обход <see cref="CheckKeys"/>.
    /// </summary>
    public static HandsDecision CheckKeyDown(int virtualKey) =>
        virtualKey is VirtualKeys.LeftWin or VirtualKeys.RightWin
            or VirtualKeys.Control or VirtualKeys.LeftControl or VirtualKeys.RightControl
            or VirtualKeys.Menu or VirtualKeys.LeftMenu or VirtualKeys.RightMenu
            ? HandsDecision.Deny("Holding Windows, Ctrl or Alt keys is not allowed.")
            : HandsDecision.Allow;

    // ---------- разбор ----------

    /// <summary>
    /// hwnd строго десятичной строкой без знака и пробелов — строже любого разборщика upstream,
    /// поэтому строка, прошедшая гейт, везде дальше читается как то же число.
    /// </summary>
    public static bool TryParseHandle(string? handle, out long hwnd)
    {
        hwnd = 0;
        if (string.IsNullOrEmpty(handle) || handle.Any(ch => ch is < '0' or > '9'))
            return false;
        return long.TryParse(handle, NumberStyles.None, CultureInfo.InvariantCulture, out hwnd) && hwnd != 0;
    }
}
