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
    public const string BrowserNavigate = "browser_navigate";
    public const string BrowserSnapshot = "browser_snapshot";
    public const string BrowserClick = "browser_click";
    public const string BrowserType = "browser_type";
    public const string BrowserTabs = "browser_tabs";
    public const string BrowserWait = "browser_wait";
    public const string BrowserScreenshot = "browser_screenshot";
    public const string BrowserQuery = "browser_query";
    public const string BrowserEvaluate = "browser_evaluate";

    // Руки для окон без UIA (Citrix, Vidi, Visio): полная клавиатура, мышь, текстовый буфер, пакеты шагов.
    // Константы и перечни гейта — здесь; сами инструменты в мосту появятся в D2a–D2e.
    public const string KeyboardControl = "keyboard_control";
    public const string MouseControl = "mouse_control";
    public const string ClipboardText = "clipboard_text";
    public const string HandsBatch = "hands_batch";
    public const string ScanDocument = "scan_document";
    public const string ScanCanvas = "scan_canvas";
    // OCR (A4 «руки без UIA»): снимок → сервер → текст; чтение, не ввод.
    public const string Ocr = "ocr";

    public static readonly IReadOnlyList<string> All =
    [
        App, UiSnapshot, UiFind, UiClick, UiType, UiRead, WindowManagement, ScreenshotControl,
        BrowserNavigate, BrowserSnapshot, BrowserClick, BrowserType, BrowserTabs, BrowserWait, BrowserScreenshot,
        BrowserQuery, BrowserEvaluate,
        KeyboardControl, MouseControl, ClipboardText, HandsBatch, ScanDocument, ScanCanvas,
        Ocr,
    ];
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
/// <param name="browserProfilesRoot">
/// Корень профилей браузерной руки в данных агента (ADR-016 §7.1): <c>app</c> не открывает
/// программу с <c>user-data-dir</c> внутри него. null — браузерной руки у моста нет, беречь нечего.
/// </param>
public sealed class HandsPolicy(IHandsWindowSystem windows, string? browserProfilesRoot = null)
{
    // Кривой корень тихо выключил бы запрет, поэтому падаем при создании, а не пропускаем
    private readonly string? _browserProfilesRoot = browserProfilesRoot is null
        ? null
        : HandsAppPaths.TryNormalize(browserProfilesRoot)
          ?? throw new ArgumentException("Browser profiles root must be a full path", nameof(browserProfilesRoot));

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

    /// <summary>
    /// Действия <c>mouse_control</c> из схемы инструмента (upstream <c>MouseAction</c>); прочие —
    /// отказ. <c>drag</c> и <c>polyline</c> несут две и более точки, <c>get_position</c> — ни одной,
    /// <c>scroll</c> — колесо (потолок <see cref="MaxWheelAmount"/>), остальные — одну точку.
    /// </summary>
    private static readonly HashSet<string> PointerActions = new(StringComparer.Ordinal)
    {
        "move", "click", "double_click", "right_click", "middle_click", "drag", "polyline", "scroll", "get_position",
    };

    /// <summary>Потолок колеса <c>mouse_control(action='scroll')</c>: больше модель ход не крутит.</summary>
    public const int MaxWheelAmount = 20;

    /// <summary>
    /// Действия <c>keyboard_control</c> из схемы инструмента (upstream <c>KeyboardAction</c>);
    /// прочие — отказ.
    /// </summary>
    private static readonly HashSet<string> KeyboardActions = new(StringComparer.Ordinal)
    {
        "type", "press", "key_down", "key_up", "sequence", "release_all", "get_keyboard_layout", "wait_for_idle",
    };

    /// <summary>
    /// Действия <c>keyboard_control</c>, которые вводят в окно и поэтому требуют <c>windowHandle</c>:
    /// <c>type</c>, <c>press</c>, <c>key_down</c>, <c>key_up</c>, <c>sequence</c>. Служебные
    /// (<c>release_all</c>, <c>get_keyboard_layout</c>, <c>wait_for_idle</c>) окно не требуют.
    /// </summary>
    private static readonly HashSet<string> KeyboardInputActions = new(StringComparer.Ordinal)
    {
        "type", "press", "key_down", "key_up", "sequence",
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

            if (UserDataDirIntoProfiles(arguments) is { } userDataDirDenial)
                return HandsLaunchDecision.Deny(userDataDirDenial);
        }

        // Запускаем ровно проверенную строку, а не как путь прислала модель
        return HandsLaunchDecision.Allow(normalized);
    }

    /// <summary>
    /// Узкий запрет (решение 2026-09-29): <c>user-data-dir</c> в корень профилей браузерной руки
    /// открыл бы профиль проекта в обход руки, а сама рука дальше получала бы «профиль занят».
    /// Аргументы режутся по правилам Win32, ключ ищется во всех формах Chromium (<c>--</c>,
    /// <c>-</c>, <c>/</c>, любой регистр), путь сравнивается после нормализации, а не подстрокой.
    /// Путь, который не проверить (относительный, с переменными окружения), — отказ.
    /// Остаток: короткие имена 8.3 и точки соединения диском не разворачиваются — их ловит
    /// проверка занятости профиля до запуска браузера.
    /// </summary>
    private string? UserDataDirIntoProfiles(string arguments)
    {
        if (_browserProfilesRoot is null)
            return null;

        foreach (var argument in HandsArguments.Split(arguments))
        {
            var name = argument.TrimStart('-', '/');
            if (name.Length == argument.Length ||
                !name.StartsWith(UserDataDirSwitch, StringComparison.OrdinalIgnoreCase))
                continue;

            var rest = name[UserDataDirSwitch.Length..];
            if (rest.Length == 0 || rest[0] != '=')
                continue;

            var value = rest[1..];
            var path = value.Contains('%') ? null : HandsAppPaths.TryNormalize(value);
            if (path is null)
                return "user-data-dir must be a full path without environment variables " +
                       "(for example C:\\Users\\me\\ChromeProfile).";

            if (path.Equals(_browserProfilesRoot, StringComparison.OrdinalIgnoreCase) ||
                path.StartsWith(_browserProfilesRoot + "\\", StringComparison.OrdinalIgnoreCase))
                return "user-data-dir points into the browser profiles of hands: " +
                       "use the browser_* tools for this project's browser instead of starting it with app.";
        }

        return null;
    }

    private const string UserDataDirSwitch = "user-data-dir";

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
    /// Окно ПОД ЭКРАННОЙ ТОЧКОЙ принимает ввод: его процесс не из <see cref="HandsForbiddenApps"/>
    /// (через <see cref="AcceptsInput"/>) и, если задан <paramref name="expectedRoot"/>, корень окна
    /// под точкой — именно он. Главная ошибка мыши: клик по устаревшим координатам попадает в
    /// другое окно (тост, панель задач, «Пуск», терминал), поэтому гейт проверяет окно под точкой,
    /// а не только переданное <c>windowHandle</c>.
    /// </summary>
    public HandsDecision AcceptsInputAt(int x, int y, long? expectedRoot = null)
    {
        var root = windows.GetRootWindowAt(x, y);
        if (root == 0)
            return HandsDecision.Deny(
                $"No window at ({x}, {y}): the point is off the desktop or the window closed. " +
                "Take a fresh screenshot to see what is there now.");

        if (!AcceptsInput(root))
            return HandsDecision.Deny(NoInput($"The window under ({x}, {y})"));

        if (expectedRoot is { } expected && root != windows.GetRootWindow(expected))
            return HandsDecision.Deny(
                $"The window under ({x}, {y}) is not the window the action targets: the point landed in " +
                "another window. Take a fresh screenshot and use its coordinates.");

        return HandsDecision.Allow;
    }

    /// <summary>
    /// <c>mouse_control</c>: форма и потолки. Действие — из схемы (upstream <c>MouseAction</c>);
    /// <c>drag</c> — ровно две точки (старт и конец), <c>polyline</c> — две и более,
    /// <c>get_position</c> — ни одной, остальные — одну. Колесо <c>scroll</c>: <c>amount</c> от 1 до
    /// <see cref="MaxWheelAmount"/>. Все точки — внутри виртуального рабочего стола и над окном,
    /// принимающим ввод (<see cref="AcceptsInputAt"/>, в том числе конец перетаскивания).
    /// </summary>
    public HandsDecision CheckPointer(string action, IReadOnlyList<(int X, int Y)> points, long? windowHandle, int amount)
    {
        if (!PointerActions.Contains(action))
            return HandsDecision.Deny($"Unknown mouse_control action '{action}'.");

        if (action == "drag" && points.Count != 2)
            return HandsDecision.Deny("mouse_control(action='drag') needs exactly two points: the start and the end of the drag.");
        if (action == "polyline" && points.Count < 2)
            return HandsDecision.Deny("mouse_control(action='polyline') needs at least two points to trace a stroke.");
        if (action == "get_position" && points.Count != 0)
            return HandsDecision.Deny("mouse_control(action='get_position') takes no points: it reads the current cursor position.");
        if (action is not ("drag" or "polyline" or "get_position") && points.Count != 1)
            return HandsDecision.Deny($"mouse_control(action='{action}') needs exactly one point.");

        if (action == "scroll" && (amount < 1 || amount > MaxWheelAmount))
            return HandsDecision.Deny($"The scroll amount must be between 1 and {MaxWheelAmount}.");

        // amount осмыслен только у колеса: у остальных действий ненулевой amount — ошибка модели.
        if (action != "scroll" && amount != 0)
            return HandsDecision.Deny($"The amount is only meaningful for the wheel (scroll); for '{action}' it must be 0.");

        if (points.Count == 0)
            return HandsDecision.Allow;

        var area = windows.GetVirtualScreenBounds();
        if (area is not { } bounds)
            return HandsDecision.Deny("The virtual desktop bounds are unknown, so the point cannot be verified against the screen.");

        foreach (var (x, y) in points)
        {
            if (!bounds.Contains(x, y))
                return HandsDecision.Deny(
                    $"Point ({x}, {y}) is outside the virtual desktop " +
                    $"({bounds.Left}, {bounds.Top})-({bounds.Right}, {bounds.Bottom}). Take a fresh screenshot for current coordinates.");

            var at = AcceptsInputAt(x, y, windowHandle);
            if (!at.Allowed)
                return at;
        }

        return HandsDecision.Allow;
    }

    /// <summary>
    /// <c>keyboard_control</c>: действия ввода (<c>type</c>, <c>press</c>, <c>key_down</c>, <c>key_up</c>,
    /// <c>sequence</c>) требуют <c>windowHandle</c> — окно обязано принимать ввод (<see cref="AcceptsInput"/>)
    /// и быть окном переднего плана, иначе клавиши улетят в чужое окно. Служебные действия
    /// (<c>release_all</c>, <c>get_keyboard_layout</c>, <c>wait_for_idle</c>) окно не требуют.
    /// </summary>
    public HandsDecision CheckKeyboard(long? windowHandle, string action)
    {
        if (!KeyboardActions.Contains(action))
            return HandsDecision.Deny($"Unknown keyboard_control action '{action}'.");

        if (!KeyboardInputActions.Contains(action))
            return HandsDecision.Allow;

        if (windowHandle is not { } hwnd || hwnd == 0)
            return HandsDecision.Deny(
                $"keyboard_control(action='{action}') requires windowHandle: without it the keys go to whatever window is active. {ListWindows}");

        if (!AcceptsInput(hwnd))
            return HandsDecision.Deny(NoInput($"Window '{windowHandle}'"));

        var foreground = windows.GetForegroundWindow();
        if (foreground == 0 || windows.GetRootWindow(hwnd) != foreground)
            return HandsDecision.Deny(
                $"Window '{windowHandle}' is not the foreground window: the keys would land in another window. " +
                "Call window_management(action='activate') on it first.");

        return HandsDecision.Allow;
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

    /// <summary>
    /// <c>ocr</c> (A4 «руки без UIA»): цель — та же, что у <c>screenshot_control</c> (экран,
    /// монитор, окно, область, все мониторы). Запрет идёт через <see cref="AcceptsInput"/>: окно
    /// процесса из <see cref="HandsForbiddenApps"/> (терминал, интерпретатор, Проводник) — чтение
    /// его экрана равно чтению команд, а снимок (<c>screenshot_control</c>) такое окно разрешает.
    /// Для <c>window</c> проверяется переданное окно; для <c>region</c> и экранных целей —
    /// перечисление видимых окон (сверху вниз, с вычитанием перекрытий): любое окно, чья видимая
    /// часть пересекает область и которое не принимает ввод, отказывает ДО снимка (маленький
    /// терминал между узлами сетки не проскочит). Сбой перечисления — fail-closed; запасной путь —
    /// сетка точек (A4.1).
    /// </summary>
    public HandsDecision CheckOcr(string? target, string? windowHandle, int? regionX, int? regionY, int? regionWidth, int? regionHeight)
    {
        var t = string.IsNullOrWhiteSpace(target) ? "primary" : target.Trim().ToLowerInvariant();

        if (t is not ("primary" or "primaryscreen" or "primary_screen"
            or "secondary" or "secondaryscreen" or "secondary_screen"
            or "monitor" or "window" or "region" or "allmonitors" or "all_monitors"))
            return HandsDecision.Deny(
                $"Unknown ocr target '{target}'. Valid values: 'primary_screen', 'secondary_screen', 'monitor', 'window', 'region', 'all_monitors'.");

        if (t is "window")
        {
            if (string.IsNullOrWhiteSpace(windowHandle)
                || !TryParseHandle(windowHandle, out var hwnd)
                || !AcceptsInput(hwnd))
                return HandsDecision.Deny(NoOcr($"Window '{windowHandle}'"));
            return HandsDecision.Allow;
        }

        if (t is "region")
        {
            if (regionWidth is not > 0 || regionHeight is not > 0)
                return HandsDecision.Deny("ocr target 'region' requires positive regionWidth and regionHeight.");
            return CheckOcrArea(regionX ?? 0, regionY ?? 0, regionWidth.Value, regionHeight.Value);
        }

        // Экран, монитор, все мониторы: снимается часть или весь рабочий стол — проверяем весь
        // виртуальный экран (окно на неснятом мониторе — ложный отказ, но безопасный).
        return CheckOcrScreen();
    }

    /// <summary>
    /// Снимаемая область (region или весь виртуальный экран). Полный путь — перечисление видимых
    /// окон: идём по ним сверху вниз, вычитая перекрытия, и смотрим, какая часть области ещё видна;
    /// окно, которое не принимает ввод (<see cref="AcceptsInput"/>: запрещённое приложение или
    /// неизвестный процесс), — отказ ДО снимка. Перечисление ловит и маленькое окно, провалившееся
    /// между узлами сетки (остаточный риск A4.1). Сбой перечисления — fail-closed (отказ, а не
    /// сетка): не знаем, что под областью. Пустое перечисление (видимых окон нет) — запасная сетка,
    /// которая и так найдёт только фон.
    /// </summary>
    private HandsDecision CheckOcrArea(int left, int top, int width, int height)
    {
        var visible = SafeEnumerateVisibleWindows();
        if (visible is { Count: > 0 })
        {
            foreach (var window in VisibleIntersections(visible, left, top, left + width, top + height))
                if (!AcceptsInputByImage(window.ImagePath))
                    return HandsDecision.Deny(NoOcrArea(ProcessNameByImage(window.ImagePath)));
            return HandsDecision.Allow;
        }

        if (visible is null)
            return HandsDecision.Deny(NoOcrArea("the window list could not be read"));

        // Пустое перечисление: видимых окон нет, область — фон. Сетка — запасной путь (найдёт фон).
        foreach (var (x, y) in OcrGridPoints(left, top, width, height))
        {
            var root = windows.GetRootWindowAt(x, y);
            if (root != 0 && !AcceptsInput(root))
                return HandsDecision.Deny(NoOcrArea(ProcessName(root)));
        }
        return HandsDecision.Allow;
    }

    /// <summary>
    /// Экранная цель: какой монитор снимается, по отдельности неизвестно, поэтому проверяем весь
    /// виртуальный рабочий стол. Границы неизвестны (сбой WinAPI) — проверять нечего.
    /// </summary>
    private HandsDecision CheckOcrScreen()
    {
        var bounds = windows.GetVirtualScreenBounds();
        if (bounds is not { } b)
            return HandsDecision.Allow;
        return CheckOcrArea(b.Left, b.Top, b.Right - b.Left, b.Bottom - b.Top);
    }

    /// <summary>
    /// Перечисление видимых окон; исключение (сбой WinAPI) — null, как «не удалось».
    /// </summary>
    private IReadOnlyList<HandsVisibleWindow>? SafeEnumerateVisibleWindows()
    {
        try
        {
            return windows.EnumerateVisibleWindows();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Окно принимает ввод по его образу (та же логика, что <see cref="AcceptsInput"/>): образ
    /// прочитан и не из <see cref="HandsForbiddenApps"/>. null/неизвестный процесс — нет.
    /// </summary>
    private bool AcceptsInputByImage(string? imagePath)
    {
        var image = HandsAppPaths.TryNormalize(imagePath);
        return image is not null && !HandsAppPaths.IsForbidden(image);
    }

    /// <summary>Имя процесса по образу; «unknown process» — образ не прочитался.</summary>
    private static string ProcessNameByImage(string? imagePath)
    {
        var image = HandsAppPaths.TryNormalize(imagePath);
        return image is null ? "unknown process" : HandsAppPaths.FileName(image);
    }

    /// <summary>
    /// Окна из перечисления, чья видимая часть пересекает область [left, right)×[top, bottom).
    /// Идём сверху вниз: каждое окно вычитает из области уже закрытое окнами выше, поэтому окно,
    /// полностью перекрытое разрешённым сверху, не считается видимым.
    /// </summary>
    private static IEnumerable<HandsVisibleWindow> VisibleIntersections(
        IReadOnlyList<HandsVisibleWindow> windows, int left, int top, int right, int bottom)
    {
        var covered = new List<(int Left, int Top, int Right, int Bottom)>();
        foreach (var window in windows)
        {
            if (!window.IsVisible || !window.HasSize)
                continue;

            // Пересечение окна с областью; пусто — окно вне снимаемой области.
            var ix = Math.Max(window.Left, left);
            var iy = Math.Max(window.Top, top);
            var ix2 = Math.Min(window.Right, right);
            var iy2 = Math.Min(window.Bottom, bottom);
            if (ix >= ix2 || iy >= iy2)
                continue;

            // Часть пересечения, не закрытая окнами выше: есть — окно видно.
            if (HasVisiblePart(ix, iy, ix2, iy2, covered))
                yield return window;

            covered.Add((window.Left, window.Top, window.Right, window.Bottom));
        }
    }

    /// <summary>
    /// Есть ли у прямоугольника [x, x2)×[y, y2) часть, не покрытая ни одним прямоугольником
    /// <paramref name="covered"/>. Разбиение по пересечениям: если весь прямоугольник закрыт —
    /// нет, иначе — да (достаточно прямоугольной арифметики, без растровой сетки).
    /// </summary>
    private static bool HasVisiblePart(int x, int y, int x2, int y2,
        List<(int Left, int Top, int Right, int Bottom)> covered)
    {
        if (x >= x2 || y >= y2)
            return false;

        foreach (var (cl, ct, cr, cb) in covered)
        {
            var ix = Math.Max(x, cl);
            var iy = Math.Max(y, ct);
            var ix2 = Math.Min(x2, cr);
            var iy2 = Math.Min(y2, cb);
            if (ix >= ix2 || iy >= iy2)
                continue;

            // Окно вырезает из прямоугольника пересечение [ix,ix2)×[iy,iy2). Остаток — четыре
            // части (лево, право, верх, низ); они не пересекают это окно, поэтому в рекурсии оно
            // пропускается, а остальные окна учитываются. Если ни одна часть не видна — закрыто.
            if (HasVisiblePart(x, y, ix, y2, covered) ||
                HasVisiblePart(ix2, y, x2, y2, covered) ||
                HasVisiblePart(ix, y, ix2, iy, covered) ||
                HasVisiblePart(ix, iy2, ix2, y2, covered))
                return true;
            return false;
        }

        return true;
    }

    /// <summary>
    /// Точки сетки по области: шаг 200 px, плюс четыре угла и центр. Шаг 200 px сам по себе
    /// пропускает окно меньше 200 px, прижатое между узлами; явные углы и центр закрывают этот
    /// зазор — окно, прижатое к краю области, не проскочит.
    /// </summary>
    private static IEnumerable<(int X, int Y)> OcrGridPoints(int left, int top, int width, int height)
    {
        if (width <= 0 || height <= 0)
            yield break;

        const int step = 200;
        var right = left + width;
        var bottom = top + height;

        for (var x = left; x < right; x += step)
            for (var y = top; y < bottom; y += step)
                yield return (x, y);

        yield return (left, top);
        yield return (right - 1, top);
        yield return (left, bottom - 1);
        yield return (right - 1, bottom - 1);
        yield return (left + width / 2, top + height / 2);
    }

    /// <summary>Имя процесса окна (из образа); «unknown process» — образ не прочитался.</summary>
    private string ProcessName(long root)
    {
        var pid = windows.GetWindowProcessId(root);
        if (pid is not { } id)
            return "unknown process";
        var image = HandsAppPaths.TryNormalize(windows.GetProcessImagePath(id));
        return image is null ? "unknown process" : HandsAppPaths.FileName(image);
    }

    /// <summary>
    /// Окно для OCR: процесс из <see cref="HandsForbiddenApps"/> (или неизвестный) — чтение его
    /// экрана равно чтению команд; в отличие от снимка, OCR такое окно не читает.
    /// </summary>
    private static string NoOcr(string what) =>
        $"{what} belongs to an interpreter, a terminal or Explorer (or its program is unknown): " +
        "ocr would read its screen — the commands and their output — so hands do not ocr it.";

    /// <summary>Отказ по области: под снимаемой областью (или на экране) есть окно, которое не принимает ввод.</summary>
    private static string NoOcrArea(string process) =>
        $"ocr refused: area overlaps a window that is not allowed ({process}); choose another area.";

    // ---------- браузер ----------

    /// <summary>Потолок <c>browser_wait</c>: дольше модель ход не вешает.</summary>
    public const int MaxBrowserWaitMs = 30_000;

    private const string FreshSnapshot = "Take a fresh browser_snapshot and use its refs.";

    /// <summary>Действия <c>browser_tabs</c>; вкладки только своего браузера, чужих в профиле нет.</summary>
    private static readonly HashSet<string> TabActions = new(StringComparer.Ordinal)
    {
        "list", "new", "select", "close",
    };

    /// <summary>
    /// Адрес <c>browser_navigate</c> и новой вкладки: белый список — <c>http</c>, <c>https</c> и
    /// ровно <c>about:blank</c> (ADR-016 §7.1). Всё прочее — отказ: <c>file:</c> и
    /// <c>chrome:</c> — не дело руки, <c>javascript:</c> и <c>data:</c> — исполнение JS от модели
    /// в обход гейта <c>browser_evaluate</c> и его журнала. Переходим по нормализованной строке из решения, а не по
    /// присланной: разбор .NET и Chromium не расходятся на том, что проверено.
    /// </summary>
    public static HandsUrlDecision CheckBrowserUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return HandsUrlDecision.Deny("url is required (http, https or about:blank).");

        // Chromium молча выкидывает табуляции и переводы строк из адреса: проверили бы одно, открыли другое
        if (url.Any(char.IsControl))
            return HandsUrlDecision.Deny("url must not contain control characters.");

        var trimmed = url.Trim();
        if (trimmed.Equals(AboutBlank, StringComparison.OrdinalIgnoreCase))
            return HandsUrlDecision.Allow(AboutBlank);

        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https") ||
            string.IsNullOrEmpty(uri.Host))
            return HandsUrlDecision.Deny(
                "Only http, https and about:blank addresses can be opened: local files, browser pages, " +
                "javascript: and data: URLs are not allowed.");

        return HandsUrlDecision.Allow(uri.AbsoluteUri);
    }

    private const string AboutBlank = "about:blank";

    /// <summary>
    /// <c>browser_tabs</c>: <c>list</c>, <c>new</c> (адрес — через <see cref="CheckBrowserUrl"/>,
    /// без адреса — <c>about:blank</c>), <c>select</c> и <c>close</c> (нужен <c>tabId</c>).
    /// </summary>
    public static HandsUrlDecision CheckBrowserTabs(string? action, string? url, string? tabId)
    {
        if (action is null || !TabActions.Contains(action))
            return HandsUrlDecision.Deny($"Unknown browser_tabs action '{action}'. Use list, new, select or close.");

        switch (action)
        {
            case "new":
                return url is null ? HandsUrlDecision.Allow(AboutBlank) : CheckBrowserUrl(url);
            case "select" or "close" when string.IsNullOrWhiteSpace(tabId):
                return HandsUrlDecision.Deny($"browser_tabs(action='{action}') requires tabId. Call browser_tabs(action='list').");
            default:
                return url is null
                    ? HandsUrlDecision.Allow(null)
                    : HandsUrlDecision.Deny("url is only accepted with action='new'.");
        }
    }

    /// <summary>
    /// Ссылка на элемент из снимка (<c>e</c> и число): формат, затем знает ли её таблица ссылок
    /// моста. Неизвестная или устаревшая после перехода ссылка — отказ, а не «поверим».
    /// </summary>
    /// <param name="resolveRef">backendDOMNodeId по ссылке из последнего снимка; null — ссылки нет.</param>
    public static HandsDecision CheckBrowserRef(string tool, string? reference, Func<string, int?> resolveRef)
    {
        if (string.IsNullOrWhiteSpace(reference))
            return HandsDecision.Deny($"{tool} requires ref. {FreshSnapshot}");

        if (!IsRefFormat(reference))
            return HandsDecision.Deny($"'{reference}' is not a snapshot ref (refs look like e12). {FreshSnapshot}");

        return resolveRef(reference) is null
            ? HandsDecision.Deny($"Ref '{reference}' is unknown or stale. {FreshSnapshot}")
            : HandsDecision.Allow;
    }

    /// <summary><c>browser_snapshot</c>: без ссылки — вся страница, со ссылкой — её поддерево.</summary>
    public static HandsDecision CheckBrowserSnapshot(string? reference, Func<string, int?> resolveRef) =>
        reference is null ? HandsDecision.Allow : CheckBrowserRef(HandsTools.BrowserSnapshot, reference, resolveRef);

    /// <summary>
    /// <c>browser_wait</c>: текст на странице или пауза; время — от 1 мс до
    /// <see cref="MaxBrowserWaitMs"/>, чтобы модель не повесила ход.
    /// </summary>
    public static HandsDecision CheckBrowserWait(string? text, int? timeoutMs)
    {
        if (string.IsNullOrEmpty(text) && timeoutMs is null)
            return HandsDecision.Deny("browser_wait requires text to wait for or ms to pause.");

        if (timeoutMs is { } ms && (ms <= 0 || ms > MaxBrowserWaitMs))
            return HandsDecision.Deny($"Wait time must be between 1 and {MaxBrowserWaitMs} ms.");

        return HandsDecision.Allow;
    }

    /// <summary>Потолок длины CSS-селектора <c>browser_query</c>.</summary>
    public const int MaxBrowserSelectorChars = 1_000;

    /// <summary>Потолок числа элементов в ответе <c>browser_query</c>.</summary>
    public const int MaxBrowserQueryLimit = 100;

    /// <summary>Режимы <c>browser_query</c>: текст поддерева, атрибуты, разметка.</summary>
    public static readonly IReadOnlyList<string> BrowserQueryModes = ["text", "attributes", "html"];

    /// <summary>
    /// <c>browser_query</c> — чтение DOM без JS: CSS-селектор, ссылка снимка или оба (селектор
    /// внутри элемента). Селектор непустой, без управляющих символов и не длиннее
    /// <see cref="MaxBrowserSelectorChars"/>; режим — из <see cref="BrowserQueryModes"/> (нет —
    /// <c>text</c>); число элементов — от 1 до <see cref="MaxBrowserQueryLimit"/>; ссылка — как у
    /// <see cref="CheckBrowserRef"/>.
    /// </summary>
    public static HandsDecision CheckBrowserQuery(string? selector, string? reference, string? mode, int? limit,
        Func<string, int?> resolveRef)
    {
        if (mode is not null && !BrowserQueryModes.Contains(mode))
            return HandsDecision.Deny($"Unknown browser_query mode '{mode}'. Use text, attributes or html.");

        if (limit is { } n && (n <= 0 || n > MaxBrowserQueryLimit))
            return HandsDecision.Deny($"limit must be between 1 and {MaxBrowserQueryLimit}.");

        if (selector is not null)
        {
            if (string.IsNullOrWhiteSpace(selector))
                return HandsDecision.Deny("selector must not be empty. Pass a CSS selector like 'a.result' or omit it and pass ref.");
            if (selector.Length > MaxBrowserSelectorChars)
                return HandsDecision.Deny($"selector is longer than {MaxBrowserSelectorChars} characters.");
            if (selector.Any(char.IsControl))
                return HandsDecision.Deny("selector must not contain control characters.");
        }

        if (reference is null)
            return selector is null
                ? HandsDecision.Deny("browser_query requires selector (CSS) or ref from a snapshot.")
                : HandsDecision.Allow;

        return CheckBrowserRef(HandsTools.BrowserQuery, reference, resolveRef);
    }

    /// <summary>Потолок длины скрипта <c>browser_evaluate</c>.</summary>
    public const int MaxBrowserScriptChars = 10_000;

    /// <summary>
    /// <c>browser_evaluate</c> — JS модели в странице текущей вкладки (ADR-016 §7.1, решение
    /// владельца 2026-09-29). Гейт держит только форму: скрипт непустой и не длиннее
    /// <see cref="MaxBrowserScriptChars"/>. Что скрипт сделает в странице, гейт не проверяет и
    /// проверить не может: границы — вкладка своего профиля, потолок времени и размера результата
    /// в сессии, запрет загрузок и схем адресов остаётся в силе.
    /// </summary>
    public static HandsDecision CheckBrowserEvaluate(string? script)
    {
        if (string.IsNullOrWhiteSpace(script))
            return HandsDecision.Deny("script is required: a JavaScript expression, for example document.title.");

        return script.Length > MaxBrowserScriptChars
            ? HandsDecision.Deny($"script is longer than {MaxBrowserScriptChars} characters. Use a shorter expression or browser_query.")
            : HandsDecision.Allow;
    }

    /// <summary><c>browser_screenshot</c>: как <see cref="CheckScreenshot"/> — только в ответ, на диск не пишется.</summary>
    public static HandsDecision CheckBrowserScreenshot(string? outputPath) =>
        string.IsNullOrEmpty(outputPath)
            ? HandsDecision.Allow
            : HandsDecision.Deny("outputPath is not allowed: screenshots are not written to disk. Omit it.");

    private static bool IsRefFormat(string reference) =>
        reference.Length is >= 2 and <= 10 && reference[0] == 'e' && reference.Skip(1).All(char.IsAsciiDigit);

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

        // Клавиши, которые с Win ведут в запускалки оболочки: R — «Выполнить», X — меню,
        // S и Q — поиск, L — блокировка.
        public const int Q = 0x51;
        public const int R = 0x52;
        public const int S = 0x53;
        public const int X = 0x58;
        public const int L = 0x4C;
    }

    /// <summary>
    /// Узкий запрет системных сочетаний: только те, что ведут в оболочку. Запрещены — клавиша Win
    /// сама по себе (без другой клавиши), Win+R («Выполнить»), Win+X (меню), Win+Q (поиск), Win+L
    /// (блокировка), Win+S (поиск; Win+Shift+S — сниппинг — разрешён), Ctrl+Esc («Пуск») и
    /// Ctrl+Shift+Esc (диспетчер задач). Всё остальное разрешено: Alt+Tab, Alt+Esc, Ctrl+Alt (AltGr)
    /// в любых сочетаниях, Win+D, Win+стрелки, Win+Shift+S, Win+1..9, Alt+F4. Проверка — по
    /// виртуальному коду ПОСЛЕ сопоставления имени: синонимы вроде «meta»/«super» её не обходят.
    /// </summary>
    public static HandsDecision CheckKeys(int virtualKey, bool ctrl, bool alt, bool shift, bool win)
    {
        ctrl |= virtualKey is VirtualKeys.Control or VirtualKeys.LeftControl or VirtualKeys.RightControl;
        alt |= virtualKey is VirtualKeys.Menu or VirtualKeys.LeftMenu or VirtualKeys.RightMenu;
        win |= virtualKey is VirtualKeys.LeftWin or VirtualKeys.RightWin;

        // Win сама по себе: клавиша Win без другой клавиши — иначе «Пуск» (и Win не «залипает»).
        if (win && (virtualKey is VirtualKeys.LeftWin or VirtualKeys.RightWin) && !ctrl && !alt && !shift)
            return HandsDecision.Deny("The Windows key alone is not allowed: it opens the Start menu.");

        if (win)
        {
            // Win + R/X/Q/L — «Выполнить», меню, поиск, блокировка.
            if (virtualKey is VirtualKeys.R or VirtualKeys.X or VirtualKeys.Q or VirtualKeys.L)
                return HandsDecision.Deny("Win+R/X/Q/L open shell launchers (Run, menu, Search, Lock) and are not allowed.");
            // Win + S — поиск; Win+Shift+S — сниппинг (разрешён).
            if (virtualKey == VirtualKeys.S && !shift)
                return HandsDecision.Deny("Win+S opens Windows Search and is not allowed; Win+Shift+S (the snip tool) is allowed.");
        }

        // Ctrl+Esc — «Пуск», Ctrl+Shift+Esc — диспетчер задач.
        if (ctrl && virtualKey == VirtualKeys.Escape)
            return HandsDecision.Deny(shift
                ? "Ctrl+Shift+Esc opens Task Manager and is not allowed."
                : "Ctrl+Esc opens the Start menu and is not allowed.");

        return HandsDecision.Allow;
    }

    /// <summary>
    /// Клавиша нажимается: удержание любых клавиш разрешено, но «удержанные модификаторы +
    /// нажимаемая клавиша» проверяются тем же <see cref="CheckKeys"/>. Удержание самой Win без
    /// другой клавиши запрещено: иначе Win «залипнет» и откроет «Пуск». Без аргументов — ни одна
    /// клавиша не удержана (существующие вызовы).
    /// </summary>
    public static HandsDecision CheckKeyDown(int virtualKey, bool ctrl = false, bool alt = false, bool shift = false, bool win = false) =>
        CheckKeys(virtualKey, ctrl, alt, shift, win);

    // ---------- запись, буфер, пакет (D1c) ----------

    /// <summary>Потолок записи файла: 20 МБ.</summary>
    public const long MaxOutputBytes = 20L * 1024 * 1024;

    /// <summary>Потолок записи <c>scan_canvas</c>: 50 МБ (полотно шире обычного снимка).</summary>
    public const long MaxCanvasBytes = 50L * 1024 * 1024;

    /// <summary>Потолок новых файлов за ход: 100.</summary>
    public const int MaxFilesPerTurn = 100;

    /// <summary>
    /// Белый список расширений по инструменту записи: снимок — картинки, скан документа — текст и
    /// разметка, скан полотна — картинка. Инструмент не из таблицы — путь не проверяем (его гейт
    /// другой), но расширение всё равно должно совпасть с таблицей, иначе — отказ.
    /// </summary>
    private static readonly Dictionary<string, string[]> AllowedOutputExtensions = new(StringComparer.Ordinal)
    {
        [HandsTools.ScreenshotControl] = [".png", ".jpg"],
        [HandsTools.ScanDocument] = [".md", ".txt", ".json"],
        [HandsTools.ScanCanvas] = [".png"],
        // OCR (A4): полный текст — только текстовый файл.
        [HandsTools.Ocr] = [".txt", ".md"],
    };

    /// <summary>
    /// Путь записи (D1c): только НОВЫЙ файл под корнем проекта, не перезапись. Нормализует
    /// относительный путь (оба разделителя, пустые и «.» сегменты) и отказывает, если путь
    /// абсолютный (диск, Unix-корень), UNC, уходит выше корня через «..», несёт управляющие
    /// символы, лежит в служебном каталоге, который модель не должна трогать, или расширение не из
    /// белого списка инструмента. Коллизию имени (».v2«) гейт не решает — её выбирает пишущий код
    /// через <see cref="NextFreeName"/>.
    /// </summary>
    /// <param name="tool">Инструмент, который пишет: определяет белый список расширений и потолок размера.</param>
    /// <param name="projectRoot">Корень проекта; null — не задан (путь проверяется сам по себе).</param>
    /// <param name="relativePath">Относительный путь от корня проекта, как его прислала модель.</param>
    /// <param name="bytes">Размер файла в байтах.</param>
    /// <param name="filesWrittenThisTurn">Сколько новых файлов ход уже создал (потолок <see cref="MaxFilesPerTurn"/>).</param>
    /// <param name="appendOwn">Дописывание в файл, а не создание: разрешено только для файла, созданного тем же ходом.</param>
    /// <param name="ownFiles">
    /// Файлы, созданные этим ходом, в НОРМАЛИЗОВАННОЙ форме (оба разделителя → «/», как
    /// <paramref name="relativePath"/>); для <paramref name="appendOwn"/>. Сравнение
    /// регистронезависимое, поэтому <c>docs\x.png</c> в списке совпадает с <c>docs/x.png</c> в запросе.
    /// </param>
    public HandsOutputPathDecision CheckOutputPath(
        string tool, string? projectRoot, string? relativePath, long bytes,
        int filesWrittenThisTurn, bool appendOwn, IReadOnlyCollection<string>? ownFiles)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
            return HandsOutputPathDecision.Deny("outputPath is required: a path relative to the project root.");

        if (relativePath.Any(char.IsControl))
            return HandsOutputPathDecision.Deny("outputPath must not contain control characters.");

        var (normalized, normalizeReason) = NormalizeRelativePath(relativePath);
        if (normalized is null)
            return HandsOutputPathDecision.Deny(
                normalizeReason
                ?? "outputPath must be relative to the project root: no drive letter, no absolute or UNC path, "
                   + "and no '..' above the root.");

        // ПОРЯДОК ЗНАЧИМ: служебный каталог проверяется ДО расширения. Иначе путь в .claude/ с
        // разрешённым расширением (x.png) при снятом запрете .claude/ прошёл бы, а путь с чужим
        // расширением (x.md) отказал бы «по расширению», а не «по служебному пути» — тест
        // Service_paths_are_denied держит именно причину.
        if (IsForbiddenServicePath(normalized))
            return HandsOutputPathDecision.Deny(
                $"'{normalized}' is a service path hands must not write: use .cc-attachments/ for its own files.");

        var extensions = AllowedOutputExtensions.GetValueOrDefault(tool);
        if (extensions is null || !HasExtension(normalized, extensions))
            return HandsOutputPathDecision.Deny(
                $"{tool} writes {string.Join(", ", extensions ?? Array.Empty<string>())} files; " +
                $"'{PathExtension(normalized)}' is not one of them.");

        var ceiling = tool == HandsTools.ScanCanvas ? MaxCanvasBytes : MaxOutputBytes;
        if (bytes < 0 || bytes > ceiling)
            return HandsOutputPathDecision.Deny($"The file is {bytes} bytes; {tool} writes at most {ceiling} bytes.");

        if (filesWrittenThisTurn >= MaxFilesPerTurn)
            return HandsOutputPathDecision.Deny(
                $"This turn already created {MaxFilesPerTurn} files; {tool} does not create more.");

        if (appendOwn)
        {
            if (ownFiles is null)
                return HandsOutputPathDecision.Deny(
                    "Appending is only allowed to a file this turn created itself; this one was not.");

            // ownFiles — в той же нормализованной форме (оба разделителя → «/»); сравниваем
            // регистронезависимо, чтобы docs\x.png в списке совпал с docs/x.png в запросе.
            var own = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var file in ownFiles)
            {
                var (ownNorm, _) = NormalizeRelativePath(file);
                if (ownNorm is not null)
                    own.Add(ownNorm);
            }

            if (!own.Contains(normalized))
                return HandsOutputPathDecision.Deny(
                    "Appending is only allowed to a file this turn created itself; this one was not.");
        }

        return HandsOutputPathDecision.Allow(normalized);
    }

    /// <summary>
    /// Нормализация относительного пути: оба разделителя → «/», пустые и «.» сегменты убраны.
    /// Возвращает (Path, Reason): Path — нормализованный путь, Reason — почему путь не годится
    /// (null, если годен). Path null — путь не годится; Reason объясняет, что именно: структура
    /// (диск <c>C:/</c>, Unix-корень <c>/</c>, UNC <c>//</c>, «..» выше корня, пустой путь) или
    /// содержимое сегмента (см. <see cref="SegmentIssue"/>).
    /// </summary>
    private static (string? Path, string? Reason) NormalizeRelativePath(string path)
    {
        const string structural =
            "outputPath must be relative to the project root: no drive letter, no absolute or UNC path, " +
            "and no '..' above the root.";

        var p = path.Replace('\\', '/');

        // Диск (C:/), Unix-корень (/) и UNC (//) — абсолютные, не под корнем проекта.
        if (p.StartsWith("//", StringComparison.Ordinal))
            return (null, structural);
        if (p.Length >= 3 && char.IsAsciiLetter(p[0]) && p[1] == ':' && p[2] == '/')
            return (null, structural);
        if (p.StartsWith("/", StringComparison.Ordinal))
            return (null, structural);

        var segments = new List<string>();
        foreach (var raw in p.Split('/'))
        {
            if (raw.Length == 0 || raw == ".")
                continue;
            if (raw == "..")
                return (null, structural);

            var issue = SegmentIssue(raw, isFirst: segments.Count == 0);
            if (issue is not null)
                return (null, issue);

            segments.Add(raw);
        }

        return segments.Count == 0 ? (null, structural) : (string.Join('/', segments), null);
    }

    /// <summary>
    /// Проблема в содержимом одного сегмента пути; null — сегмент годен. <paramref name="isFirst"/>
    /// — первый ли это сегмент (для <c>~</c>, который раскрывается в home только в начале пути).
    /// </summary>
    private static string? SegmentIssue(string segment, bool isFirst)
    {
        // Разбор по code point, а не по char: валидная суррогатная пара (😀 = U+D83D U+DE00) —
        // один code point вне BMP, а не два «суррогата», и OtherSymbol (⭐, ⚓) — обычные эмодзи в
        // BMP. Отказ только на двух случаях: Cf-форматные (U+200B..U+200D, U+200E/F, U+202A..U+202E,
        // U+2060..U+2064, U+FEFF) — невидимый спуфинг имени, и ОДИНОЧНЫЙ суррогат (битый UTF-16).
        for (var i = 0; i < segment.Length; i++)
        {
            var ch = segment[i];

            // High-суррогат: валидная пара (high + следующий low) разрешена — пропускаем обе половины;
            // high без low — одиночный суррогат.
            if (char.IsHighSurrogate(ch))
            {
                if (i + 1 < segment.Length && char.IsLowSurrogate(segment[i + 1]))
                {
                    i++;
                    continue;
                }
                return $"'{segment}' carries a lone surrogate (an unpaired UTF-16 half); use a plain name.";
            }

            // Low-суррогат без high перед ним — тоже одиночный.
            if (char.IsLowSurrogate(ch))
                return $"'{segment}' carries a lone surrogate (an unpaired UTF-16 half); use a plain name.";

            // Cf-форматные (все в BMP) — невидимый спуфинг; OtherSymbol и буквы — разрешены.
            if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.Format)
                return $"'{segment}' carries an invisible Unicode format character; use a plain name.";
        }

        // ~ раскрывается в home только в первом сегменте: ~/x.png, ~user/x.png.
        if (isFirst && segment.StartsWith("~", StringComparison.Ordinal))
            return $"'{segment}' starts with '~' (a home directory); use a path relative to the project root.";

        // : — диск (C:) или альтернативный поток (x.png:stream); недопустим в любом сегменте.
        if (segment.Contains(':'))
            return $"'{segment}' contains ':' (a drive letter or an alternate data stream); use a plain name.";

        // < > " | ? * — недопустимы в именах файлов Windows.
        if (segment.Any(ch => ch is '<' or '>' or '"' or '|' or '?' or '*'))
            return $"'{segment}' contains a character Windows file names do not allow (< > \" | ? *).";

        // Зарезервированные имена устройств Windows (CON, PRN, AUX, NUL, COM1..9, LPT1..9),
        // с расширением и без, регистронезависимо: NUL.png — это NUL.
        var stem = segment[..(segment.LastIndexOf('.') > 0 ? segment.LastIndexOf('.') : segment.Length)];
        if (IsReservedDeviceName(stem))
            return $"'{segment}' is a reserved Windows device name ({stem}); use another name.";

        // Сегмент, оканчивающийся точкой или пробелом: Windows обрезает их, путь молча меняется.
        if (segment.EndsWith(".") || segment.EndsWith(" "))
            return $"'{segment}' ends with a dot or a space; Windows trims it, so the path would change.";

        return null;
    }

    /// <summary>
    /// Зарезервированные имена устройств Windows (без расширения): <c>CON</c>, <c>PRN</c>, <c>AUX</c>,
    /// <c>NUL</c> и <c>COM1..9</c> / <c>LPT1..9</c>; регистр не важен.
    /// </summary>
    private static readonly HashSet<string> ReservedDeviceNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    /// <summary>Зарезервированное имя устройства Windows (без расширения), регистронезависимо.</summary>
    private static bool IsReservedDeviceName(string stem) => ReservedDeviceNames.Contains(stem);

    /// <summary>
    /// Служебный путь, который модель не должна писать: <c>.git/</c>, <c>.claude/</c>, <c>.mcp.json</c>
    /// и любой <c>.cc-*</c>, кроме разрешённой <c>.cc-attachments/</c>. Регистр не важен.
    /// </summary>
    private static bool IsForbiddenServicePath(string normalized)
    {
        foreach (var segment in normalized.Split('/'))
        {
            if (segment.Length <= 1)
                continue;
            if (!segment.StartsWith(".", StringComparison.Ordinal))
                continue;

            var name = segment.ToLowerInvariant();
            if (name is ".git" or ".claude" or ".mcp.json")
                return true;
            if (name.StartsWith(".cc-", StringComparison.Ordinal) && name != ".cc-attachments")
                return true;
        }

        return false;
    }

    /// <summary>Расширение файла (с точкой, в нижнем регистре); без точки — пустая строка.</summary>
    private static string PathExtension(string normalized)
    {
        var name = normalized[(normalized.LastIndexOf('/') + 1)..];
        var dot = name.LastIndexOf('.');
        return dot <= 0 ? string.Empty : name[dot..].ToLowerInvariant();
    }

    private static bool HasExtension(string normalized, string[] extensions)
    {
        var extension = PathExtension(normalized);
        return extensions.Any(e => string.Equals(extension, e, StringComparison.Ordinal));
    }

    /// <summary>
    /// Свободное имя при коллизии: <c>a.png</c> занято → <c>a.v2.png</c>, потом <c>a.v3.png</c>.
    /// Чистая функция: <paramref name="exists"/> говорит, занято ли имя, и пишущий код сам выбирает
    /// результат — гейт коллизию не решает.
    /// </summary>
    public static string NextFreeName(string normalizedPath, Func<string, bool> exists)
    {
        if (!exists(normalizedPath))
            return normalizedPath;

        var slash = normalizedPath.LastIndexOf('/');
        var dir = slash >= 0 ? normalizedPath[..slash] : string.Empty;
        var name = slash >= 0 ? normalizedPath[(slash + 1)..] : normalizedPath;
        var dot = name.LastIndexOf('.');
        var stem = dot > 0 ? name[..dot] : name;
        var ext = dot > 0 ? name[dot..] : string.Empty;

        for (var i = 2; ; i++)
        {
            var candidate = $"{stem}.v{i}{ext}";
            var full = dir.Length == 0 ? candidate : $"{dir}/{candidate}";
            if (!exists(full))
                return full;
        }
    }

    /// <summary>Потолок записи в буфер: 20 000 символов.</summary>
    public const int MaxClipboardWriteChars = 20_000;

    /// <summary>Потолок ответа чтения буфера: 50 000 символов (больше — обрезка, не отказ).</summary>
    public const int MaxClipboardReadChars = 50_000;

    /// <summary>
    /// Форматы, которые говорят «это не обычный текст»: менеджеры паролей помечают буфер так,
    /// чтобы мониторы и история его не копировали.
    /// </summary>
    private static readonly HashSet<string> ClipboardSensitiveFormats = new(StringComparer.OrdinalIgnoreCase)
    {
        "ExcludeClipboardContentFromMonitorProcessing",
        "CanIncludeInClipboardHistory=0",
    };

    /// <summary>
    /// <c>clipboard_text</c> (D1c): <c>read</c> — только если буфер изменился ПОСЛЕ последнего
    /// собственного ввода моста (иначе модель читает то, что вписала сама) и без форматов
    /// «не копировать» (менеджеры паролей); <c>write</c> — до <see cref="MaxClipboardWriteChars"/>
    /// символов, только текст.
    /// </summary>
    public HandsDecision CheckClipboard(string action, int length, IHandsClipboardState state)
    {
        switch (action)
        {
            case "read":
                // «Изменился» = номер отличается от зафиксированного после собственного ввода; «>»
                // ломалось на переполнении uint (максимум → 0). Без собственного ввода читать нечего.
                if (!state.HadOwnInput || state.SequenceNumber == state.SequenceNumberAtLastOwnInput)
                    return HandsDecision.Deny(
                        "The clipboard has not changed since the last write of this turn: reading it returns the text you wrote yourself.");
                if (state.FormatNames.Any(f => ClipboardSensitiveFormats.Contains(f)))
                    return HandsDecision.Deny(
                        "The clipboard carries a do-not-copy format (a password manager): do not read it.");
                return HandsDecision.Allow;

            case "write":
                if (length < 0 || length > MaxClipboardWriteChars)
                    return HandsDecision.Deny($"The text is {length} characters; clipboard_text writes at most {MaxClipboardWriteChars}.");
                return HandsDecision.Allow;

            default:
                return HandsDecision.Deny($"Unknown clipboard_text action '{action}'. Use read or write.");
        }
    }

    /// <summary>Потолок шагов в пакете: 20.</summary>
    public const int MaxBatchSteps = 20;

    /// <summary>Потолок паузы между шагами пакета: 2000 мс.</summary>
    public const int MaxStepDelayMs = 2000;

    /// <summary>Общий потолок времени пакета: 60 000 мс.</summary>
    public const int MaxBatchTotalMs = 60_000;

    /// <summary>Потолок одного ожидания в пакете: 5000 мс.</summary>
    public const int MaxWaitMs = 5000;

    /// <summary>
    /// Инструменты, допущенные в пакет <c>hands_batch</c>: только ввод и пауза. Всё, что открывает
    /// программу, управляет окном, ходит в браузер или пишет файл, — в пакет не входит.
    /// Сравнение регистронезависимое: модель может прислать <c>APP</c>.
    /// </summary>
    private static readonly HashSet<string> BatchTools = new(StringComparer.OrdinalIgnoreCase)
    {
        HandsTools.KeyboardControl, HandsTools.MouseControl, HandsTools.UiClick, HandsTools.UiType,
        HandsTools.ClipboardText, "wait",
    };

    /// <summary>
    /// Пакет шагов <c>hands_batch</c> (D1c): до <see cref="MaxBatchSteps"/> шагов, каждый — только
    /// из <see cref="BatchTools"/>; пауза между шагами 0..<see cref="MaxStepDelayMs"/> мс, одно
    /// ожидание до <see cref="MaxWaitMs"/> мс, сумма ожиданий до <see cref="MaxBatchTotalMs"/> мс.
    /// В пакет не входит <c>app</c>, <c>window_management</c> с <c>close</c>, любой <c>browser_*</c>,
    /// снимки, вложенный пакет и неизвестный инструмент; финальный снимок — отдельное поле, не шаг.
    /// Имена инструментов и <c>action</c> сравниваются регистронезависимо (модель может прислать
    /// <c>APP</c>, <c>Close</c>).
    /// <para>
    /// <paramref name="totalWaitMs"/> — общая длительность ожиданий, которую считает МОСТ; гейт ей
    /// доверяет, но на всякий случай сам суммирует <c>wait</c>-шаги и межшаговые паузы
    /// (сумма <c>wait</c> + <c>delayMs</c> × (n−1)) и берёт БОЛЬШЕЕ из двух — оба числа обязаны
    /// укладываться в <see cref="MaxBatchTotalMs"/>.
    /// </para>
    /// </summary>
    public HandsDecision CheckBatch(IReadOnlyList<HandsBatchStep> steps, int totalWaitMs, bool hasFinalCapture)
    {
        if (steps.Count == 0)
            return HandsDecision.Deny("hands_batch needs at least one step.");

        if (steps.Count > MaxBatchSteps)
            return HandsDecision.Deny($"hands_batch has {steps.Count} steps; at most {MaxBatchSteps}.");

        var waitTotalMs = 0;
        var interStepDelays = new List<int>();
        foreach (var step in steps)
        {
            if (step is null)
                return HandsDecision.Deny("hands_batch has an empty step.");

            // Имя сравниваем регистронезависимо (модель может прислать APP, Browser_Click).
            var tool = step.Tool.Trim();

            // window_management допущен, кроме close (окно, в которое вводишь, закрывать не надо).
            if (!BatchTools.Contains(tool) && !string.Equals(tool, HandsTools.WindowManagement, StringComparison.OrdinalIgnoreCase))
            {
                if (string.Equals(tool, HandsTools.HandsBatch, StringComparison.OrdinalIgnoreCase))
                    return HandsDecision.Deny("hands_batch does not nest: a batch inside a batch is not allowed.");
                if (string.Equals(tool, HandsTools.App, StringComparison.OrdinalIgnoreCase))
                    return HandsDecision.Deny("hands_batch does not start programs: use app as a separate step.");
                if (tool.StartsWith("browser_", StringComparison.OrdinalIgnoreCase))
                    return HandsDecision.Deny($"hands_batch does not browse: use {step.Tool} as a separate step.");
                if (string.Equals(tool, HandsTools.ScreenshotControl, StringComparison.OrdinalIgnoreCase))
                    return HandsDecision.Deny("hands_batch does not take screenshots mid-batch: use the final capture field.");
                return HandsDecision.Deny($"Unknown hands_batch tool '{step.Tool}'.");
            }

            if (string.Equals(tool, HandsTools.WindowManagement, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(GetStringArg(step.Args, "action"), "close", StringComparison.OrdinalIgnoreCase))
                return HandsDecision.Deny("hands_batch does not close windows: use window_management(action='close') as a separate step.");

            if (string.Equals(tool, "wait", StringComparison.OrdinalIgnoreCase))
            {
                // У wait-шага DelayMs — длительность ожидания, не межшаговая пауза.
                if (step.DelayMs is not { } wait || wait < 0 || wait > MaxWaitMs)
                    return HandsDecision.Deny($"A wait step is {step.DelayMs} ms; at most {MaxWaitMs} ms.");
                waitTotalMs += wait;
            }
            else if (step.DelayMs is { } delay && (delay < 0 || delay > MaxStepDelayMs))
                return HandsDecision.Deny($"The step delay is {delay} ms; between steps it is at most {MaxStepDelayMs} ms.");
            else if (step.DelayMs is { } gap)
                interStepDelays.Add(gap);
        }

        // Межшаговых пауз на (n−1): после последнего шага следующего нет, последнюю паузу не считаем.
        var interStepTotalMs = interStepDelays.Count > 0
            ? interStepDelays.Sum() - interStepDelays[^1]
            : 0;
        var computedWaitMs = waitTotalMs + interStepTotalMs;

        if (totalWaitMs < 0 || totalWaitMs > MaxBatchTotalMs)
            return HandsDecision.Deny($"The waits total {totalWaitMs} ms; a batch waits at most {MaxBatchTotalMs} ms.");
        // Мост считает totalWaitMs сам; гейт доверяет, но берёт большее из двух сумм.
        if (computedWaitMs > MaxBatchTotalMs)
            return HandsDecision.Deny($"The waits total {computedWaitMs} ms; a batch waits at most {MaxBatchTotalMs} ms.");

        return HandsDecision.Allow;
    }

    /// <summary>
    /// Строковый аргумент из <c>args</c> шага (обрезанный от пробелов); null — аргумента нет.
    /// </summary>
    /// <remarks>
    /// ЧИТАЕТ <c>args</c> РЕФЛЕКСИЕЙ (<c>GetType().GetProperties()</c>): для POCO-объекта находит
    /// строковое свойство, но для <see cref="JsonElement"/> (что даёт биндинг MCP) вернёт null —
    /// свойства самого <c>JsonElement</c> не совпадают с полями JSON-объекта. Пока <c>window_management</c>
    /// в пакете мостом не поддерживается (мост отказывает раньше гейта), это безопасно: проверка
    /// «<c>window_management(action='close')</c> запрещён» молча не срабатывает, но шаг всё равно
    /// не доходит до окна. Когда <c>window_management</c> разрешат в пакете — переписать под
    /// <c>JsonElement</c> (как <c>HandsBatchStepArgs.Str</c> в мосту), иначе «close» проскочит гейт.
    /// </remarks>
    private static string? GetStringArg(object? args, string name)
    {
        if (args is null)
            return null;

        foreach (var property in args.GetType().GetProperties())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase) &&
                property.GetValue(args) is string value)
                return value.Trim();
        }

        return null;
    }

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
