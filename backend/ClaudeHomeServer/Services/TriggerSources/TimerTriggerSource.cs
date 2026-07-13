using System.Text.Json;
using ClaudeHomeServer.Models;

namespace ClaudeHomeServer.Services.TriggerSources;

// Таймер-триггер: срабатывает по расписанию (daily/weekdays/weekly в HH:mm местное) или
// по интервалу (каждые N минут от прошлого срабатывания/создания правила). Чистая логика
// (портирована из удалённой PersonaProactiveService + TaskRecurrenceCalculator), без внешних
// зависимостей — легко юнит-тестится. FireWindow 24ч — защита от лавины после долгого простоя.
//
// Args.schedule: { type:"daily"|"weekdays"|"weekly"|"interval",
//                  time:"HH:mm", weekdays:[1..7] (ISO, 1=Пн..7=Вс), intervalMinutes:int }
public sealed class TimerTriggerSource : ITriggerSource
{
    public AutomationTriggerType Type => AutomationTriggerType.Timer;

    // Срабатываем только по «свежим» моментам расписания (как AutoStartWindow у задач):
    // защита от лавины после долгого простоя сервера или создания правила задним числом.
    internal static readonly TimeSpan FireWindow = TimeSpan.FromHours(24);

    public Task<IReadOnlyList<TriggerEvent>> EvaluateAsync(TriggerContext ctx, CancellationToken ct)
    {
        var sched = ParseSchedule(TriggerArgs.Of(ctx.Rule.Trigger));
        if (sched is null || !ShouldFire(sched, ctx.State.LastFiredAt, ctx.Rule.CreatedAt, ctx.Tz, ctx.NowUtc))
            return Task.FromResult<IReadOnlyList<TriggerEvent>>(Array.Empty<TriggerEvent>());

        var localNow = TimeZoneInfo.ConvertTimeFromUtc(ctx.NowUtc, ctx.Tz);
        var summary = sched.Type == "interval"
            ? $"Сработал таймер «{ctx.Rule.Name}» (каждые {sched.IntervalMinutes} мин)"
            : $"Сработал таймер «{ctx.Rule.Name}» по расписанию ({localNow:dd.MM.yyyy HH:mm}, местное время)";
        return Task.FromResult<IReadOnlyList<TriggerEvent>>(new[]
        {
            new TriggerEvent(ctx.Rule.Id, AutomationTriggerType.Timer, summary),
        });
    }

    // ─── Чистые предикаты (юнит-тесты) ──────────────────────────────────────────

    internal sealed record Schedule(string Type, TimeOnly? Time, List<int>? Weekdays, int? IntervalMinutes);

    internal static Schedule? ParseSchedule(IReadOnlyDictionary<string, JsonElement> args)
    {
        var s = args.GetObject("schedule") ?? args;   // допускаем расписание вложенно или плоско
        var type = (s.GetString("type") ?? "daily").Trim().ToLowerInvariant();
        TimeOnly? time = null;
        if (s.GetString("time") is { } t && TimeOnly.TryParseExact(t, "HH:mm", out var parsed)) time = parsed;
        return new Schedule(type, time, s.GetIntList("weekdays"), s.GetInt("intervalMinutes"));
    }

    internal static bool ShouldFire(Schedule sched, DateTime? lastFiredAt, DateTime createdAt, TimeZoneInfo tz, DateTime nowUtc)
    {
        // Интервал: от последнего срабатывания (или создания правила, если ни разу) прошло ≥ interval минут
        if (sched.Type == "interval")
        {
            if (sched.IntervalMinutes is not int mins || mins <= 0) return false;
            var anchor = lastFiredAt ?? createdAt;
            return nowUtc - anchor >= TimeSpan.FromMinutes(mins);
        }

        // Расписание: нужен валидный момент времени
        if (sched.Time is not TimeOnly time) return false;
        var occurrence = LastOccurrenceUtc(sched, time, tz, nowUtc);
        if (occurrence is null) return false;
        if (nowUtc - occurrence > FireWindow) return false;              // момент слишком старый (простой)
        return lastFiredAt is null || lastFiredAt < occurrence;          // по этому моменту ещё не срабатывали
    }

    // Последний момент расписания ≤ now в таймзоне юзера (UTC); null — момента нет
    // (невалидное время / у weekly не выбраны дни). Перебор ≤ 7 дней назад.
    internal static DateTime? LastOccurrenceUtc(Schedule sched, TimeOnly time, TimeZoneInfo tz, DateTime nowUtc)
    {
        var localNow = TimeZoneInfo.ConvertTimeFromUtc(nowUtc, tz);
        for (var back = 0; back <= 7; back++)
        {
            var day = localNow.Date.AddDays(-back);
            if (!DayMatches(sched, day.DayOfWeek)) continue;
            var localAt = day + time.ToTimeSpan();
            if (localAt > localNow) continue;
            return TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(localAt, DateTimeKind.Unspecified), tz);
        }
        return null;
    }

    private static bool DayMatches(Schedule sched, DayOfWeek dow) => sched.Type switch
    {
        "daily" => true,
        "weekdays" => dow is not (DayOfWeek.Saturday or DayOfWeek.Sunday),
        "weekly" => sched.Weekdays is { Count: > 0 } days && days.Contains(IsoDay(dow)),
        _ => false,
    };

    private static int IsoDay(DayOfWeek dow) => dow == DayOfWeek.Sunday ? 7 : (int)dow;
}
