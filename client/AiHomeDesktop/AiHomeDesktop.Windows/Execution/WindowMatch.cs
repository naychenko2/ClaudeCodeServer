using AiHomeDesktop.Core.Protocol;

namespace AiHomeDesktop.Windows.Execution;

/// <summary>Окно верхнего уровня так, как его видит грань исполнения.</summary>
/// <param name="Handle">HWND. В чистой логике — просто число, чтобы её можно было гонять тестом.</param>
/// <param name="Title">Заголовок окна.</param>
/// <param name="ProcessName">Имя процесса-владельца (без пути и расширения).</param>
/// <param name="ProcessId">PID владельца.</param>
/// <param name="IsMinimized">Свёрнуто: содержимое снять нельзя — это отдельный честный исход.</param>
/// <param name="IsSelf">Окно самого клиента AI Home.</param>
public sealed record WindowInfo(
    long Handle,
    string Title,
    string ProcessName,
    int ProcessId,
    bool IsMinimized,
    bool IsSelf);

/// <summary>
/// Выбор окна: либо цель, либо честный исход с текстом. Тихого no-op здесь нет —
/// «не нашли» и «свёрнуто» это разные ответы, и модель обязана их различать.
/// </summary>
public sealed record WindowPick(WindowInfo? Window, string? Outcome, string? Message)
{
    public static WindowPick Found(WindowInfo window) => new(window, null, null);

    public static WindowPick Refuse(string outcome, string message) => new(null, outcome, message);
}

/// <summary>
/// Поиск целевого окна по части заголовка. Чистая функция без WinAPI: список окон снимает
/// <see cref="Interop.WindowInventory"/>, а решение принимается здесь — его можно проверить
/// тестом на любой платформе.
/// </summary>
public static class WindowMatch
{
    /// <summary>Сколько заголовков показываем модели, когда окно не нашлось.</summary>
    private const int HintTitles = 8;

    /// <summary>
    /// Выбрать окно. Пустой запрос — активное окно (правило desktop_screen: scope=window
    /// по умолчанию снимает то, на что человек смотрит).
    /// </summary>
    public static WindowPick Select(IReadOnlyList<WindowInfo> windows, string? query, WindowInfo? foreground)
    {
        var wanted = (query ?? "").Trim();
        if (wanted.Length == 0) return SelectForeground(foreground);

        // Ранг совпадения: точное совпадение важнее начала строки, начало — важнее вхождения.
        // Порядок внутри ранга — порядок перечисления, то есть Z-order: верхнее окно первым.
        var matches = windows
            .Select(w => (Window: w, Rank: RankOf(w.Title, wanted)))
            .Where(m => m.Rank >= 0)
            .OrderBy(m => m.Rank)
            .Select(m => m.Window)
            .ToList();

        if (matches.Count == 0)
            return WindowPick.Refuse(DesktopOutcomes.WindowNotAvailable,
                $"Окна с заголовком «{wanted}» на устройстве нет. {OpenWindows(windows)}");

        // Своё окно целью не бывает: агент не действует в клиенте AI Home и не снимает его.
        var usable = matches.Where(w => !w.IsSelf).ToList();
        if (usable.Count == 0)
            return WindowPick.Refuse(DesktopOutcomes.SelfTargetDenied,
                "Под запрос подошло только окно самого клиента AI Home — его снимать нельзя.");

        var visible = usable.FirstOrDefault(w => !w.IsMinimized);
        if (visible is null)
            return WindowPick.Refuse(DesktopOutcomes.WindowMinimized,
                $"Окно «{usable[0].Title}» свёрнуто — снять его содержимое нельзя. "
                + "Попроси человека развернуть окно.");

        return WindowPick.Found(visible);
    }

    private static WindowPick SelectForeground(WindowInfo? foreground)
    {
        if (foreground is null)
            return WindowPick.Refuse(DesktopOutcomes.WindowNotAvailable,
                "Активного окна на устройстве нет: рабочий стол пуст либо окно принадлежит "
                + "процессу без видимого содержимого. Назови окно по заголовку или сними экран целиком.");

        if (foreground.IsSelf)
            return WindowPick.Refuse(DesktopOutcomes.SelfTargetDenied,
                "Активное окно — сам клиент AI Home; его снимать нельзя. "
                + "Назови нужное окно по заголовку или сними экран целиком.");

        if (foreground.IsMinimized)
            return WindowPick.Refuse(DesktopOutcomes.WindowMinimized,
                "Активное окно свёрнуто — снять его содержимое нельзя. Попроси человека развернуть окно.");

        return WindowPick.Found(foreground);
    }

    /// <summary>-1 — не подошло; 0 — точное совпадение, 1 — начало заголовка, 2 — вхождение.</summary>
    private static int RankOf(string title, string wanted)
    {
        if (string.Equals(title, wanted, StringComparison.OrdinalIgnoreCase)) return 0;
        if (title.StartsWith(wanted, StringComparison.OrdinalIgnoreCase)) return 1;
        return title.Contains(wanted, StringComparison.OrdinalIgnoreCase) ? 2 : -1;
    }

    /// <summary>
    /// Подсказка со списком открытых окон: без неё модель гадает заголовок вслепую, а
    /// перебор — это лишние вызовы к машине человека.
    /// </summary>
    private static string OpenWindows(IReadOnlyList<WindowInfo> windows)
    {
        var titles = windows.Where(w => !w.IsSelf).Select(w => w.Title).Take(HintTitles).ToList();
        if (titles.Count == 0) return "Открытых окон с заголовками сейчас нет.";

        var tail = windows.Count(w => !w.IsSelf) > titles.Count ? " и другие" : "";
        return "Сейчас открыты: " + string.Join(", ", titles.Select(t => $"«{t}»")) + tail + ".";
    }
}
