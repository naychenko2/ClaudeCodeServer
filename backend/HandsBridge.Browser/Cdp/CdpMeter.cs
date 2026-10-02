namespace ClaudeHomeServer.HandsBridge.Browser.Cdp;

/// <summary>Этап вызова в <c>hands.log</c>: переход, готовность DOM, затишье, снимок.</summary>
/// <param name="Detail">Подробность в скобках (чем кончилось затишье, число узлов); null — без неё.</param>
public sealed record CdpStage(string Name, TimeSpan Elapsed, string? Detail = null);

/// <summary>
/// Замер одного вызова <c>browser_*</c>: сколько команд CDP ушло и сколько они заняли, сколько
/// ждали событий страницы (загрузка, затишье). Живёт в <see cref="AsyncLocal{T}"/> вызова:
/// команды обработчиков событий (закрытие диалога на потоке чтения трубы) в чужой замер не
/// попадают. Нужен, чтобы по <c>hands.log</c> отличить время браузера от времени модели, а
/// этапы (<see cref="Stages"/>) — чтобы внутри вызова отличить переход от снимка.
/// </summary>
public sealed class CdpMeter
{
    private static readonly AsyncLocal<CdpMeter?> s_current = new();

    private int _calls;
    private long _cdpTicks;
    private long _waitTicks;
    private readonly List<CdpStage> _stages = [];

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

    /// <summary>Этапы вызова в порядке завершения.</summary>
    public IReadOnlyList<CdpStage> Stages
    {
        get
        {
            lock (_stages)
                return [.. _stages];
        }
    }

    /// <summary>Отметить завершённый этап текущего вызова; вне замера — ничего.</summary>
    public static void AddStage(string name, TimeSpan elapsed, string? detail = null)
    {
        if (s_current.Value is not { } meter)
            return;
        lock (meter._stages)
            meter._stages.Add(new CdpStage(name, elapsed, detail));
    }

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
