namespace ClaudeHomeServer.HandsBridge.Browser.Cdp;

/// <summary>
/// Замер одного вызова <c>browser_*</c>: сколько команд CDP ушло и сколько они заняли, сколько
/// ждали событий страницы (загрузка, затишье). Живёт в <see cref="AsyncLocal{T}"/> вызова:
/// команды обработчиков событий (закрытие диалога на потоке чтения трубы) в чужой замер не
/// попадают. Нужен, чтобы по <c>hands.log</c> отличить время браузера от времени модели.
/// </summary>
public sealed class CdpMeter
{
    private static readonly AsyncLocal<CdpMeter?> s_current = new();

    private int _calls;
    private long _cdpTicks;
    private long _waitTicks;

    /// <summary>Замер текущего вызова; null — вызов не замеряется.</summary>
    public static CdpMeter? Current => s_current.Value;

    /// <summary>Начать замер в текущем асинхронном контексте (действует до выхода из вызывающего async-метода).</summary>
    public static CdpMeter Start()
    {
        var meter = new CdpMeter();
        s_current.Value = meter;
        return meter;
    }

    public int Calls => Volatile.Read(ref _calls);

    /// <summary>Время команд CDP от записи до ответа.</summary>
    public TimeSpan Cdp => TimeSpan.FromTicks(Interlocked.Read(ref _cdpTicks));

    /// <summary>Время ожидания событий страницы.</summary>
    public TimeSpan Wait => TimeSpan.FromTicks(Interlocked.Read(ref _waitTicks));

    internal static void AddCall(TimeSpan elapsed)
    {
        if (s_current.Value is not { } meter)
            return;
        Interlocked.Increment(ref meter._calls);
        Interlocked.Add(ref meter._cdpTicks, elapsed.Ticks);
    }

    public static void AddWait(TimeSpan elapsed)
    {
        if (s_current.Value is { } meter)
            Interlocked.Add(ref meter._waitTicks, elapsed.Ticks);
    }
}
