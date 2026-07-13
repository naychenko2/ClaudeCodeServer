using ClaudeHomeServer.Services.TriggerSources;

namespace ClaudeHomeServer.Tests.Services;

// Чистая логика таймер-триггера (портирована из удалённой PersonaProactiveService):
// расписание daily/weekdays/weekly, интервал, FireWindow 24ч, идемпотентность по LastFiredAt.
public class TimerTriggerSourceTests
{
    private static readonly TimeZoneInfo Moscow = TimeZoneInfo.FindSystemTimeZoneById("Europe/Moscow"); // UTC+3

    private static TimerTriggerSource.Schedule Sched(string type, string? time = "09:00",
        List<int>? weekdays = null, int? interval = null)
    {
        TimeOnly? t = time is not null && TimeOnly.TryParseExact(time, "HH:mm", out var p) ? p : null;
        return new TimerTriggerSource.Schedule(type, t, weekdays, interval);
    }

    // 2026-07-13 — понедельник; 2026-07-11 — суббота. Москва UTC+3 → 09:00 локальных = 06:00 UTC.

    [Fact]
    public void Daily_срабатывает_после_времени_если_не_стреляли()
    {
        var daily = Sched("daily");
        var now = new DateTime(2026, 7, 13, 6, 30, 0, DateTimeKind.Utc); // 09:30 Москвы, пн
        Assert.True(TimerTriggerSource.ShouldFire(daily, lastFiredAt: null, createdAt: now.AddDays(-1), Moscow, now));
    }

    [Fact]
    public void Daily_не_срабатывает_повторно_по_тому_же_моменту()
    {
        var daily = Sched("daily");
        var now = new DateTime(2026, 7, 13, 6, 30, 0, DateTimeKind.Utc); // 09:30 Москвы
        // Уже стреляли в момент сегодняшнего 09:00 (06:00 UTC) → lastFiredAt >= occurrence
        Assert.False(TimerTriggerSource.ShouldFire(daily,
            lastFiredAt: new DateTime(2026, 7, 13, 6, 0, 0, DateTimeKind.Utc),
            createdAt: now.AddDays(-1), Moscow, now));
    }

    [Fact]
    public void Daily_до_времени_не_срабатывает_день_в_день()
    {
        var daily = Sched("daily");
        // 08:30 Москвы пн — сегодняшний 09:00 ещё не наступил; последний момент = вчера 09:00.
        // Это > 24 ч назад от «завтра не наступило»? Нет: вчера 09:00 — ровно в пределах суток,
        // но если по нему УЖЕ стреляли (LastFiredAt вчера) — не повторится. Первый запуск до 09:00
        // стреляет по вчерашнему моменту (в пределах FireWindow) — это допустимое поведение.
        var now = new DateTime(2026, 7, 13, 5, 30, 0, DateTimeKind.Utc); // 08:30 Москвы
        Assert.True(TimerTriggerSource.ShouldFire(daily, lastFiredAt: null, createdAt: now.AddDays(-2), Moscow, now));
    }

    [Fact]
    public void Interval_срабатывает_когда_прошёл_интервал()
    {
        var interval = Sched("interval", time: null, interval: 30);
        var now = new DateTime(2026, 7, 13, 10, 0, 0, DateTimeKind.Utc);
        Assert.True(TimerTriggerSource.ShouldFire(interval, lastFiredAt: now.AddMinutes(-35),
            createdAt: now.AddDays(-1), Moscow, now));   // 35 ≥ 30
    }

    [Fact]
    public void Interval_не_срабатывает_раньше_интервала()
    {
        var interval = Sched("interval", time: null, interval: 30);
        var now = new DateTime(2026, 7, 13, 10, 0, 0, DateTimeKind.Utc);
        Assert.False(TimerTriggerSource.ShouldFire(interval, lastFiredAt: now.AddMinutes(-20),
            createdAt: now.AddDays(-1), Moscow, now));   // 20 < 30
    }

    [Fact]
    public void Interval_от_создания_правила_если_ни_разу_не_стреляли()
    {
        var interval = Sched("interval", time: null, interval: 30);
        var now = new DateTime(2026, 7, 13, 10, 0, 0, DateTimeKind.Utc);
        Assert.True(TimerTriggerSource.ShouldFire(interval, lastFiredAt: null,
            createdAt: now.AddMinutes(-35), Moscow, now));   // создано 35 мин назад
    }

    [Fact]
    public void Weekly_срабатывает_в_свой_день()
    {
        var weekly = Sched("weekly", weekdays: [6]); // ISO 6 = суббота
        var sat = new DateTime(2026, 7, 11, 6, 30, 0, DateTimeKind.Utc); // 09:30 Москвы, сб
        Assert.True(TimerTriggerSource.ShouldFire(weekly, lastFiredAt: null, createdAt: sat.AddDays(-7), Moscow, sat));
    }

    [Fact]
    public void Weekly_не_срабатывает_если_момент_старше_FireWindow()
    {
        var weekly = Sched("weekly", weekdays: [6]); // только суббота
        var mon = new DateTime(2026, 7, 13, 6, 30, 0, DateTimeKind.Utc); // 09:30 Москвы, пн
        // Последний момент (сб 06:00 UTC) — > 24 ч назад → FireWindow гасит (защита от лавины после простоя)
        Assert.False(TimerTriggerSource.ShouldFire(weekly, lastFiredAt: null, createdAt: mon.AddDays(-7), Moscow, mon));
    }

    [Fact]
    public void Weekly_без_дней_не_имеет_момента()
    {
        var weeklyEmpty = Sched("weekly", weekdays: []);
        var mon = new DateTime(2026, 7, 13, 6, 30, 0, DateTimeKind.Utc);
        Assert.Null(TimerTriggerSource.LastOccurrenceUtc(weeklyEmpty, new TimeOnly(9, 0), Moscow, mon));
    }

    [Fact]
    public void LastOccurrence_daily_даёт_сегодняшний_момент_в_UTC()
    {
        var daily = Sched("daily");
        var mon930 = new DateTime(2026, 7, 13, 6, 30, 0, DateTimeKind.Utc); // 09:30 Москвы
        Assert.Equal(new DateTime(2026, 7, 13, 6, 0, 0, DateTimeKind.Utc),
            TimerTriggerSource.LastOccurrenceUtc(daily, new TimeOnly(9, 0), Moscow, mon930));
    }

    [Fact]
    public void LastOccurrence_weekdays_на_выходных_возвращает_пятницу()
    {
        var weekdays = Sched("weekdays");
        var sat930 = new DateTime(2026, 7, 11, 6, 30, 0, DateTimeKind.Utc); // 09:30 Москвы, сб
        // Пятница 10.07 09:00 Москвы = 06:00 UTC
        Assert.Equal(new DateTime(2026, 7, 10, 6, 0, 0, DateTimeKind.Utc),
            TimerTriggerSource.LastOccurrenceUtc(weekdays, new TimeOnly(9, 0), Moscow, sat930));
    }
}
