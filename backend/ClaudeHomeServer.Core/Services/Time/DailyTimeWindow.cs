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

    // Конец БЛИЖАЙШЕГО окна, содержащего момент, как instant в tz (статус windowEndsAt, ADR-041).
    // Для окна через полночь конец — на «следующих сутках». Вне всех окон — null
    // (на правой границе момента уже нет — полунтервал [From, To)).
    public static DateTimeOffset? CurrentEnd(DateTimeOffset now, IReadOnlyList<DailyTimeWindow?>? windows, TimeZoneInfo tz)
    {
        if (windows is null or { Count: 0 }) return null;
        var local = TimeZoneInfo.ConvertTime(now, tz);
        DateTimeOffset? best = null;
        foreach (var w in windows)
        {
            if (w is null) continue;
            if (!w.TryParseBounds(out var from, out var to)) continue;
            if (!w.InRange(local, from, to)) continue;
            var end = AtLocal(local.DateTime.Date, to, tz);
            if (end <= local) end = end.AddDays(1);   // переход через полночь: конец — следующих суток
            best = best is { } cur && cur <= end ? cur : end;
        }
        return best;
    }

    // Начало БЛИЖАЙШЕГО окна строго после момента, как instant в tz (статус nextWindowAt, ADR-041).
    // Окно с днями начинается в ближайший разрешённый день (пт 19:00 при [1..5] → пн 16:00).
    // Нет валидных окон — null. Поиск на неделю вперёд: любое валидное окно начинается в пределах
    // следующих 7 дней (дней больше — нет), глубина полная.
    public static DateTimeOffset? NextStart(DateTimeOffset now, IReadOnlyList<DailyTimeWindow?>? windows, TimeZoneInfo tz)
    {
        if (windows is null or { Count: 0 }) return null;
        var local = TimeZoneInfo.ConvertTime(now, tz);
        var wall = local.DateTime;
        DateTimeOffset? best = null;
        foreach (var w in windows)
        {
            if (w is null) continue;
            if (!w.TryParseBounds(out var from, out _)) continue;
            for (var d = 0; d <= 7; d++)
            {
                var start = AtLocal(wall.Date.AddDays(d), from, tz);
                if (start <= local) continue;   // начало уже прошло (в т.ч. текущее окно)
                if (w.Weekdays is { Count: > 0 } days && !days.Contains(IsoDay(start.DateTime.DayOfWeek))) continue;
                if (best is null || start < best) best = start;
                break;   // ближайшее начало этого окна найдено
            }
        }
        return best;
    }

    // Настенные часы (дата + время) как instant с фактическим offset таймзоны (с учётом DST).
    private static DateTimeOffset AtLocal(DateTime date, TimeOnly time, TimeZoneInfo tz)
    {
        var wall = date.Date + time.ToTimeSpan();
        return new DateTimeOffset(wall, tz.GetUtcOffset(wall));
    }

    private static int IsoDay(DayOfWeek dow) => dow == DayOfWeek.Sunday ? 7 : (int)dow;

    private bool TryParseBounds(out TimeOnly from, out TimeOnly to)
    {
        // Тот же разбор, что был в InQuietWindow (и в TimerTriggerSource/TaskDueCalculator).
        from = default; to = default;
        return TimeOnly.TryParseExact(From, "HH:mm", out from)
            && TimeOnly.TryParseExact(To, "HH:mm", out to);
    }

    private bool InRange(DateTimeOffset local, TimeOnly from, TimeOnly to)
    {
        if (Weekdays is { Count: > 0 } days && !days.Contains(IsoDay(local.DateTime.DayOfWeek))) return false;
        var now = new TimeOnly(local.TimeOfDay.Ticks);
        return from <= to ? (now >= from && now < to)   // обычный диапазон
                          : (now >= from || now < to);   // переход через полночь
    }
}
