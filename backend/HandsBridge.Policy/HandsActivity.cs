namespace ClaudeHomeServer.HandsBridge.Policy;

/// <summary>
/// Сигнал агенту «ход действует руками». Боевой — именованное событие Windows хода
/// (<c>--activity-event</c>); поднять его можно сколько угодно раз, но <see cref="HandsActivity"/>
/// поднимает один. Сбой сигнала не роняет инструмент: плашки не будет, действие пройдёт.
/// </summary>
public interface IHandsActivitySignal
{
    void Raise();
}

/// <summary>
/// Первое действие рук за ход (ADR-016 §7, «Трей, статус и "Стоп"»): пока его не было, агент не
/// шлёт «ИИ управляет компьютером» — ни трею, ни серверу. Разговорный ход с подключёнными руками
/// плашку не зажигает.
/// <para>
/// Граница «трогает машину»: <c>app</c>, <c>ui_click</c>, <c>ui_type</c>, <c>screenshot_control</c>
/// (кроме <c>list_monitors</c> — это не снимок), любой <c>browser_*</c> и <c>window_management</c>,
/// кроме чисто читающих действий (<see cref="ReadOnlyWindowActions"/>). Чтение дерева окна
/// (<c>ui_snapshot</c>, <c>ui_find</c>, <c>ui_read</c>) машину не трогает.
/// </para>
/// Зовётся только после разрешения гейта; отказ сигнал не поднимает.
/// </summary>
public sealed class HandsActivity(IHandsActivitySignal signal)
{
    /// <summary>Действия <c>window_management</c>, которые только смотрят на окна.</summary>
    public static readonly IReadOnlySet<string> ReadOnlyWindowActions = new HashSet<string>(StringComparer.Ordinal)
    {
        "list", "find", "get_foreground", "wait_for", "get_state", "wait_for_state",
    };

    private int _raised;

    /// <summary>Сигнал уже поднят в этом ходе.</summary>
    public bool Raised => Volatile.Read(ref _raised) == 1;

    /// <summary>Трогает ли вызов инструмента машину.</summary>
    /// <param name="action">Действие в snake_case, как в схеме инструмента; у прочих инструментов — null.</param>
    public static bool TouchesMachine(string tool, string? action = null) => tool switch
    {
        HandsTools.App or HandsTools.UiClick or HandsTools.UiType => true,
        HandsTools.WindowManagement => action is null || !ReadOnlyWindowActions.Contains(action),
        HandsTools.ScreenshotControl => !string.Equals(action, "list_monitors", StringComparison.OrdinalIgnoreCase),
        _ => tool.StartsWith("browser_", StringComparison.Ordinal),
    };

    /// <summary>
    /// Инструмент прошёл гейт (<paramref name="allowed"/>) и сейчас подействует. Возвращает true,
    /// если именно этот вызов поднял сигнал.
    /// </summary>
    public bool Acted(string tool, bool allowed, string? action = null)
    {
        if (!allowed || !TouchesMachine(tool, action))
            return false;
        if (Interlocked.Exchange(ref _raised, 1) == 1)
            return false;

        signal.Raise();
        return true;
    }
}

/// <summary>Сигнала нет: мост запущен без <c>--activity-event</c>.</summary>
public sealed class NoHandsActivitySignal : IHandsActivitySignal
{
    public static readonly NoHandsActivitySignal Instance = new();

    public void Raise() { }
}
