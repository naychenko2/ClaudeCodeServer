using ClaudeHomeServer.Services.Time;

namespace ClaudeHomeServer.Tests.Services;

// Тесты Core-хелпера DailyTimeWindow: чистая функция — момент (любой instant) + окна +
// таймзона. Покрывает перенос из InQuietWindow (обычный диапазон, переход через полночь,
// границы) и новое: пересчёт instant в tz, фильтр по ISO-дням, несколько окон, MinutesUntilEnd.
public class DailyTimeWindowTests
{
    private static readonly TimeZoneInfo TzMsk = TimeZoneInfo.FindSystemTimeZoneById("Europe/Moscow"); // +03:00, без DST

    private static DailyTimeWindow W(string from, string to, int[]? weekdays = null) => new(from, to, weekdays);

    // Местное время МСК (instant с offset +03:00)
    private static DateTimeOffset Ms(int day, int hour, int min = 0, int sec = 0) =>
        new(2026, 7, day, hour, min, sec, TimeSpan.FromHours(3));
    // UTC-instant
    private static DateTimeOffset Utc(int day, int hour, int min = 0) =>
        new(2026, 7, day, hour, min, 0, TimeSpan.Zero);

    // Даты тестов: 13.07.2026 — пн, 17.07 — пт, 18.07 — сб, 19.07 — вс.

    [Fact]
    public void Contains_ОбычныйДиапазон_Границы()
    {
        var windows = new[] { W("16:00", "18:00") };
        Assert.True(DailyTimeWindow.Contains(Ms(13, 17, 0), windows, TzMsk));   // 17:00 МСК внутри
        Assert.True(DailyTimeWindow.Contains(Ms(13, 16, 0), windows, TzMsk));    // левая граница входит
        Assert.False(DailyTimeWindow.Contains(Ms(13, 15, 59), windows, TzMsk));  // левее окна
        Assert.False(DailyTimeWindow.Contains(Ms(13, 18, 0), windows, TzMsk));   // правая граница не входит
    }

    [Fact]
    public void Contains_ПереходЧерезПолночь()
    {
        var windows = new[] { W("22:00", "07:00") };
        Assert.True(DailyTimeWindow.Contains(Ms(13, 3, 0), windows, TzMsk));    // 03:00 — ночь внутри
        Assert.True(DailyTimeWindow.Contains(Ms(13, 23, 30), windows, TzMsk));   // вечер после From
        Assert.True(DailyTimeWindow.Contains(Ms(13, 22, 0), windows, TzMsk));    // левая граница входит
        Assert.False(DailyTimeWindow.Contains(Ms(13, 7, 0), windows, TzMsk));    // правая граница не входит
        Assert.False(DailyTimeWindow.Contains(Ms(13, 21, 59), windows, TzMsk));  // правее To «днём»
    }

    [Fact]
    public void Contains_UtcInstantПересчитываетсяВТаймзону()
    {
        var windows = new[] { W("16:00", "18:00") };
        Assert.False(DailyTimeWindow.Contains(Utc(13, 17, 0), windows, TzMsk)); // 17:00 UTC = 20:00 МСК — вне
        Assert.True(DailyTimeWindow.Contains(Utc(13, 14, 0), windows, TzMsk));  // 14:00 UTC = 17:00 МСК — внутри
    }

    [Fact]
    public void Contains_ДниНеделиIso()
    {
        var windows = new[] { W("16:00", "18:00", new[] { 1, 2, 3, 4, 5 }) };
        Assert.True(DailyTimeWindow.Contains(Ms(13, 17, 0), windows, TzMsk));   // пн 13.07 — будень
        Assert.False(DailyTimeWindow.Contains(Ms(18, 17, 0), windows, TzMsk));  // сб 18.07 — вне
        Assert.False(DailyTimeWindow.Contains(Ms(19, 17, 0), windows, TzMsk));  // вс 19.07 (ISO 7) — вне
    }

    [Fact]
    public void Contains_ДниНеделиДляПереходаЧерезПолночь_ПоМестнойДатеМомента()
    {
        var windows = new[] { W("23:00", "07:00", new[] { 1, 2, 3, 4, 5 }) };
        Assert.True(DailyTimeWindow.Contains(Ms(17, 23, 30), windows, TzMsk));  // вечер пятницы — внутри
        Assert.False(DailyTimeWindow.Contains(Ms(18, 3, 0), windows, TzMsk));   // раннее утро субботы — местная дата сб, вне
    }

    [Fact]
    public void Contains_НесколькоОкон_ПорядокНеВажен_ПересечениеНеОшибка()
    {
        Assert.True(DailyTimeWindow.Contains(Ms(13, 23, 0), new[] { W("12:00", "14:00"), W("22:00", "07:00") }, TzMsk));
        Assert.True(DailyTimeWindow.Contains(Ms(13, 13, 0), new[] { W("22:00", "07:00"), W("12:00", "14:00") }, TzMsk));
        Assert.False(DailyTimeWindow.Contains(Ms(13, 15, 0), new[] { W("12:00", "14:00"), W("22:00", "07:00") }, TzMsk));
    }

    [Fact]
    public void Contains_ПустыеОкнаИНевалидныеЗначения_FailClosed()
    {
        Assert.False(DailyTimeWindow.Contains(Ms(13, 17, 0), null, TzMsk));
        Assert.False(DailyTimeWindow.Contains(Ms(13, 17, 0), Array.Empty<DailyTimeWindow>(), TzMsk));
        Assert.False(DailyTimeWindow.Contains(Ms(13, 17, 0), new DailyTimeWindow?[] { null }, TzMsk));
        Assert.False(DailyTimeWindow.Contains(Ms(13, 17, 0), new[] { W("25:00", "18:00") }, TzMsk)); // вне диапазона часов
        Assert.False(DailyTimeWindow.Contains(Ms(13, 17, 0), new[] { W("7:00", "18:00") }, TzMsk));  // не HH:mm
        Assert.False(DailyTimeWindow.Contains(Ms(13, 17, 0), new[] { W("abc", "18:00") }, TzMsk));
        Assert.False(DailyTimeWindow.Contains(Ms(13, 17, 0), new[] { W("16:00", "16:00") }, TzMsk)); // пустое окно
    }

    [Fact]
    public void MinutesUntilEnd_ОбычноеОкноИПереходЧерезПолночь()
    {
        Assert.Equal(60, DailyTimeWindow.MinutesUntilEnd(Ms(13, 17, 0), new[] { W("16:00", "18:00") }, TzMsk));
        Assert.Equal(120, DailyTimeWindow.MinutesUntilEnd(Ms(13, 16, 0), new[] { W("16:00", "18:00") }, TzMsk));
        Assert.Equal(450, DailyTimeWindow.MinutesUntilEnd(Ms(13, 23, 30), new[] { W("22:00", "07:00") }, TzMsk)); // до 07:00 следующих суток
        Assert.Equal(240, DailyTimeWindow.MinutesUntilEnd(Ms(13, 3, 0), new[] { W("22:00", "07:00") }, TzMsk));
    }

    [Fact]
    public void MinutesUntilEnd_ВнеОкна_ИНаПравойГранице_Null()
    {
        Assert.Null(DailyTimeWindow.MinutesUntilEnd(Ms(13, 19, 0), new[] { W("16:00", "18:00") }, TzMsk));
        Assert.Null(DailyTimeWindow.MinutesUntilEnd(Ms(13, 18, 0), new[] { W("16:00", "18:00") }, TzMsk));
        Assert.Null(DailyTimeWindow.MinutesUntilEnd(Ms(13, 17, 0), null, TzMsk));
    }

    [Fact]
    public void MinutesUntilEnd_НесколькоОкон_БлижайшийКонец()
    {
        var windows = new[] { W("16:00", "18:00"), W("17:00", "17:30") };
        Assert.Equal(30, DailyTimeWindow.MinutesUntilEnd(Ms(13, 17, 0), windows, TzMsk));
    }

    [Fact]
    public void MinutesUntilEnd_ДробныеМинуты_OкруглениеВниз()
    {
        Assert.Equal(29, DailyTimeWindow.MinutesUntilEnd(Ms(13, 17, 30, 30), new[] { W("16:00", "18:00") }, TzMsk)); // 17:30:30 → 29.5 мин, floor
    }
}
