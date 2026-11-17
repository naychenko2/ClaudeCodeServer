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

    // ─── CurrentEnd / NextStart (статус windowEndsAt / nextWindowAt, ADR-041) ───
    // Ожидаемое время — настенные часы МСК; Ms() даёт тот же instant, что вернул бы хелпер.

    [Fact]
    public void CurrentEnd_ОбычноеОкно_КонецТогоЖеДня()
    {
        var windows = new[] { W("16:00", "18:00") };
        Assert.Equal(Ms(13, 18, 0), DailyTimeWindow.CurrentEnd(Ms(13, 17, 0), windows, TzMsk)); // 17:00 → 18:00 того же дня
        Assert.Equal(Ms(13, 18, 0), DailyTimeWindow.CurrentEnd(Ms(13, 16, 0), windows, TzMsk)); // с левой границы
    }

    [Fact]
    public void CurrentEnd_ПереходЧерезПолночь_КонецСледующихСуток()
    {
        var windows = new[] { W("22:00", "07:00") };
        Assert.Equal(Ms(14, 7, 0), DailyTimeWindow.CurrentEnd(Ms(13, 23, 30), windows, TzMsk));  // 23:30 → 07:00 следующих суток
        Assert.Equal(Ms(13, 7, 0), DailyTimeWindow.CurrentEnd(Ms(13, 3, 0), windows, TzMsk));    // 03:00 → 07:00 этих суток
    }

    [Fact]
    public void CurrentEnd_ВнеОкон_ИНаПравойГранице_Null()
    {
        Assert.Null(DailyTimeWindow.CurrentEnd(Ms(13, 19, 0), new[] { W("16:00", "18:00") }, TzMsk));
        Assert.Null(DailyTimeWindow.CurrentEnd(Ms(13, 18, 0), new[] { W("16:00", "18:00") }, TzMsk));
        Assert.Null(DailyTimeWindow.CurrentEnd(Ms(13, 17, 0), null, TzMsk));
    }

    [Fact]
    public void CurrentEnd_НесколькоОкон_БлижайшийКонец()
    {
        var windows = new[] { W("16:00", "18:00"), W("17:00", "17:30") };
        Assert.Equal(Ms(13, 17, 30), DailyTimeWindow.CurrentEnd(Ms(13, 17, 0), windows, TzMsk));
    }

    [Fact]
    public void NextStart_ВнеОкна_БлижайшееНачалоСтрогоПослеМомента()
    {
        var windows = new[] { W("16:00", "18:00") };
        Assert.Equal(Ms(14, 16, 0), DailyTimeWindow.NextStart(Ms(13, 19, 0), windows, TzMsk));  // 19:00 → 16:00 следующего дня
        Assert.Equal(Ms(13, 16, 0), DailyTimeWindow.NextStart(Ms(13, 10, 0), windows, TzMsk));  // утро → 16:00 этих же суток
    }

    [Fact]
    public void NextStart_ВнутриОкна_НачалоТекущегоОкнаНеВозвращается()
    {
        var windows = new[] { W("16:00", "18:00") };
        Assert.Equal(Ms(14, 16, 0), DailyTimeWindow.NextStart(Ms(13, 17, 0), windows, TzMsk)); // внутри — начало следующих суток
        Assert.Equal(Ms(14, 16, 0), DailyTimeWindow.NextStart(Ms(13, 16, 0), windows, TzMsk)); // ровно на From: окно уже открыто → следующих суток
    }

    [Fact]
    public void NextStart_ДниНедели_ПятницаВечер_ВозвращаетПонедельник()
    {
        var windows = new[] { W("16:00", "18:00", new[] { 1, 2, 3, 4, 5 }) };
        Assert.Equal(Ms(20, 16, 0), DailyTimeWindow.NextStart(Ms(17, 19, 0), windows, TzMsk)); // пт 17.07 19:00 → пн 20.07 16:00
    }

    [Fact]
    public void NextStart_ПереходЧерезПолночьСДнями_УтроСубботы_ВозвращаетПонедельник()
    {
        var windows = new[] { W("22:00", "07:00", new[] { 1, 2, 3, 4, 5 }) };
        Assert.Equal(Ms(20, 22, 0), DailyTimeWindow.NextStart(Ms(18, 3, 0), windows, TzMsk)); // сб 18.07 03:00 → пн 20.07 22:00
    }

    [Fact]
    public void NextStart_НетВалидныхОкон_Null()
    {
        Assert.Null(DailyTimeWindow.NextStart(Ms(13, 10, 0), null, TzMsk));
        Assert.Null(DailyTimeWindow.NextStart(Ms(13, 10, 0), new[] { W("abc", "18:00") }, TzMsk));
        Assert.Null(DailyTimeWindow.NextStart(Ms(13, 10, 0), new[] { W("16:00", "18:00", new[] { 8 }) }, TzMsk)); // ISO 8 — не день
    }
}
