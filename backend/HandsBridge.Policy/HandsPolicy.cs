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

    public static readonly IReadOnlyList<string> All =
    [
        App, UiSnapshot, UiFind, UiClick, UiType, UiRead, WindowManagement, ScreenshotControl,
        BrowserNavigate, BrowserSnapshot, BrowserClick, BrowserType, BrowserTabs, BrowserWait, BrowserScreenshot,
        BrowserQuery, BrowserEvaluate,
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
