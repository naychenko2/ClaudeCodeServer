namespace ClaudeHomeServer.Services.Time;

// Дневное окно «HH:mm» в местном времени переданной таймзоны, с опциональной фильтр-
// по-дням (ISO 1..7: пн..вс). Чистый хелпер: без состояния; таймзону выбирает вызывающий —
// хелпер только пересчитывает instant в неё (TimeZoneInfo.ConvertTime) и сравнивает.
// Окно полуоткрытое [From, To): левая граница входит, правая — нет. From > To — переход
// через полночь (например 23:00–07:00). Невалидные From/To («25:00», мусор) — fail-closed:
// окно никогда не включает (так же, как до выноса в PersonaAutomationService.InQuietWindow).
// Дни недели относятся к МЕСТНОЙ дате проверяемого момента: для окна через полночь раннее
// утро субботы вне окна с буднями [1..5], поздний вечер пятницы — внутри.
// Несколько окон — пересечение не ошибка: входит, если попал в любое; порядок не важен.
public sealed record DailyTimeWindow(string From, string To, IReadOnlyList<int>? Weekdays = null)
{
    // Входит ли момент (любой instant) в хотя бы одно окно после пересчёта в tz.
    // windows null/пусто — false (окон нет — входить некуда).
    public static bool Contains(DateTimeOffset now, IReadOnlyList<DailyTimeWindow?>? windows, TimeZoneInfo tz)
    {
        if (windows is null or { Count: 0 }) return false;
        var local = TimeZoneInfo.ConvertTime(now, tz);
        foreach (var w in windows)
        {
            if (w is null) continue;
            if (!w.TryParseBounds(out var from, out var to)) continue;
            if (w.InRange(local, from, to)) return true;
        }
        return false;
    }

    // Целых минут до конца БЛИЖАЙШЕГО окна, содержащего момент (floor; на правой границе
    // момента уже нет — null). Вне всех окон — null. Нужен статусу «конец окна» (KM-B5c).
    public static int? MinutesUntilEnd(DateTimeOffset now, IReadOnlyList<DailyTimeWindow?>? windows, TimeZoneInfo tz)
    {
        if (windows is null or { Count: 0 }) return null;
        var local = TimeZoneInfo.ConvertTime(now, tz);
        int? best = null;
        foreach (var w in windows)
        {
            if (w is null) continue;
            if (!w.TryParseBounds(out var from, out var to)) continue;
            if (!w.InRange(local, from, to)) continue;
            var delta = to - new TimeOnly(local.TimeOfDay.Ticks);
            if (delta < TimeSpan.Zero) delta += TimeSpan.FromDays(1);   // конец — на «следующих сутках» окна
            best = best is { } cur ? Math.Min(cur, (int)delta.TotalMinutes) : (int)delta.TotalMinutes;
        }
        return best;
    }

    private bool TryParseBounds(out TimeOnly from, out TimeOnly to)
    {
        // Тот же разбор, что был в InQuietWindow (и в TimerTriggerSource/TaskDueCalculator).
        from = default; to = default;
        return TimeOnly.TryParseExact(From, "HH:mm", out from)
            && TimeOnly.TryParseExact(To, "HH:mm", out to);
    }

    private bool InRange(DateTimeOffset local, TimeOnly from, TimeOnly to)
    {
        if (Weekdays is { Count: > 0 } days)
        {
            var iso = local.DateTime.DayOfWeek == DayOfWeek.Sunday ? 7 : (int)local.DateTime.DayOfWeek;
            if (!days.Contains(iso)) return false;
        }
        var now = new TimeOnly(local.TimeOfDay.Ticks);
        return from <= to ? (now >= from && now < to)   // обычный диапазон
                          : (now >= from || now < to);   // переход через полночь
    }
}
