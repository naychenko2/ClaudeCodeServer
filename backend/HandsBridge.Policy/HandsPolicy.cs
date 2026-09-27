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
/// Границы «только свои окна» нет: граница снята решением владельца 2026-09-27 (вторая волна,
/// ADR-016 §7). Модель видит, читает, снимает и трогает любое окно рабочего стола, список окон
/// отдаёт все окна, снимок экрана, монитора и области разрешён.
/// </para>
/// Остаётся одно правило окон: в окно процесса из <see cref="HandsForbiddenApps"/> (интерпретатор,
/// терминал, Проводник) руки не вводят — ни кликом, ни текстом. Ввод туда — исполнение команд в
/// обход запрета этих программ в <c>app</c>. Читать и снимать такие окна можно. Закрыто по
/// умолчанию: не узнали процесс окна или образ — ввод запрещён.
/// </summary>
public sealed class HandsPolicy(IHandsWindowSystem windows)
{
    private const string ListWindows =
        "Call window_management(action='list') to get window handles.";

    /// <summary>
    /// Ключи Chromium, которые подменяют исполняемый файл дочерних процессов или открывают
    /// управление браузером снаружи: с ними браузер становится запускалкой команд.
    /// </summary>
    private static readonly string[] ForbiddenArgumentFragments =
        ["cmd-prefix", "gpu-launcher", "subprocess-path", "remote-debugging", "load-extension"];

    /// <summary>Действия <c>window_management</c> из схемы инструмента; прочие — отказ.</summary>
    private static readonly HashSet<string> WindowActions = new(StringComparer.Ordinal)
    {
        "list", "find", "get_foreground", "wait_for",
        "activate", "minimize", "maximize", "restore", "close", "move", "resize", "set_bounds",
        "move_to_monitor", "get_state", "wait_for_state", "move_and_activate", "ensure_visible",
    };

    /// <summary>Инструменты, которые вводят в окно: клик и текст.</summary>
    private static readonly HashSet<string> InputTools = new(StringComparer.Ordinal)
    {
        HandsTools.UiClick, HandsTools.UiType,
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

    /// <summary>
    /// Можно ли вводить в окно: процесс окна известен, его образ прочитан и не из
    /// <see cref="HandsForbiddenApps"/>. Любой сбой — нельзя.
    /// </summary>
    public bool AcceptsInput(long hwnd)
    {
        if (hwnd == 0)
            return false;
        if (windows.GetWindowProcessId(hwnd) is not { } pid)
            return false;

        var image = HandsAppPaths.TryNormalize(windows.GetProcessImagePath(pid));
        return image is not null && !HandsAppPaths.IsForbidden(image);
    }

    /// <summary>
    /// <c>ui_*</c>: окно — любое, но hwnd обязателен (фолбэка на окно переднего плана нет).
    /// Для ввода (<c>ui_click</c>, <c>ui_type</c>) окно и окно каждого переданного
    /// <c>elementId</c> обязаны принимать ввод (<see cref="AcceptsInput"/>): идентификатор
    /// элемента несёт hwnd своего корня, не разрешился — отказ, а не «поверим».
    /// </summary>
    /// <param name="resolveElementWindow">hwnd корня элемента по его id; null — id неизвестен.</param>
    public HandsDecision CheckUi(
        string tool,
        string? windowHandle,
        Func<string, long?> resolveElementWindow,
        params string?[] elementIds)
    {
        if (string.IsNullOrWhiteSpace(windowHandle))
            return HandsDecision.Deny($"{tool} requires windowHandle. {ListWindows}");

        if (!InputTools.Contains(tool))
            return HandsDecision.Allow;

        if (!TryParseHandle(windowHandle, out var hwnd) || !AcceptsInput(hwnd))
            return HandsDecision.Deny(NoInput($"Window '{windowHandle}'"));

        foreach (var elementId in elementIds)
        {
            if (string.IsNullOrWhiteSpace(elementId))
                continue;
            if (resolveElementWindow(elementId) is not { } elementWindow)
                return HandsDecision.Deny(
                    $"Element '{elementId}' is unknown. Take a fresh ui_snapshot of the window and use its ids.");
            if (!AcceptsInput(elementWindow))
                return HandsDecision.Deny(NoInput($"Element '{elementId}'"));
        }

        return HandsDecision.Allow;
    }

    /// <summary>
    /// <c>window_management</c>: любое действие из схемы над любым окном, включая <c>close</c>,
    /// поиск по заголовку (<c>find</c>, <c>wait_for</c>) и полный список окон.
    /// </summary>
    /// <param name="action">Имя действия в snake_case, как в схеме инструмента.</param>
    public static HandsDecision CheckWindowAction(string action) =>
        WindowActions.Contains(action)
            ? HandsDecision.Allow
            : HandsDecision.Deny($"Unknown window_management action '{action}'.");

    // ---------- снимок ----------

    /// <summary>
    /// <c>screenshot_control</c>: любая цель — окно (любой hwnd), экран, монитор, область, все
    /// мониторы. Снимок — только в ответ: режим <c>file</c> пишет по произвольному пути машины, а
    /// <c>outputPath</c> отвергается при любом режиме (снимок с разметкой пишет по нему файл и в
    /// режиме <c>inline</c>). Это запрет записи на диск, а не граница окон.
    /// </summary>
    public static HandsDecision CheckScreenshot(string? action, string? outputMode, string? outputPath)
    {
        if (string.Equals(action, "list_monitors", StringComparison.OrdinalIgnoreCase))
            return HandsDecision.Allow;

        if (!string.IsNullOrWhiteSpace(action) && !string.Equals(action, "capture", StringComparison.OrdinalIgnoreCase))
            return HandsDecision.Deny($"Unknown screenshot_control action '{action}'.");

        if (!string.IsNullOrWhiteSpace(outputMode) && !string.Equals(outputMode, "inline", StringComparison.OrdinalIgnoreCase))
            return HandsDecision.Deny("Only outputMode='inline' is allowed: screenshots are not written to disk.");

        if (!string.IsNullOrEmpty(outputPath))
            return HandsDecision.Deny("outputPath is not allowed: screenshots are not written to disk. Omit it.");

        return HandsDecision.Allow;
    }

    private static string NoInput(string what) =>
        $"{what} belongs to an interpreter, a terminal or Explorer (or its program is unknown): " +
        "hands never click or type there, typing into it runs commands. Reading and screenshots are allowed.";

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
    /// Alt+Esc, Ctrl+Esc, Ctrl+Shift+Esc и всё с Ctrl+Alt. Они открывают «Пуск», «Выполнить»,
    /// переключение окон, диспетчер задач и экран безопасности — ввод в оболочку в обход запрета
    /// ввода в окна Проводника. Проверка — по
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
            return HandsDecision.Deny("Alt+Tab and Alt+Esc switch windows and are not allowed; use window_management(action='activate').");
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
