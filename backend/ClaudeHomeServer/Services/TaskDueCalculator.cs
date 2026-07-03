using ClaudeHomeServer.Models;

namespace ClaudeHomeServer.Services;

// Перевод локального срока задачи (DueDate/DueTime в таймзоне пользователя) в UTC-моменты
// для планировщика. Контейнер живёт в UTC — никакой математики через DateTime.Now.
public static class TaskDueCalculator
{
    // Расчётное время дня для задач без DueTime (напоминания, автозапуск исполнителя)
    public static readonly TimeOnly DefaultDueTime = new(9, 0);

    /// <summary>UTC-момент срока задачи; null — срок не задан или не парсится.</summary>
    public static DateTime? DueMomentUtc(TaskItem task, TimeZoneInfo tz)
    {
        if (task.DueDate is null) return null;
        if (!DateOnly.TryParseExact(task.DueDate, "yyyy-MM-dd", out var date)) return null;

        var time = DefaultDueTime;
        if (task.DueTime is not null && !TimeOnly.TryParseExact(task.DueTime, "HH:mm", out time))
            return null;

        var local = date.ToDateTime(time, DateTimeKind.Unspecified);
        // GetUtcOffset вместо ConvertTimeToUtc: не бросает на несуществующем локальном
        // времени (переход на летнее время), а берёт действующий офсет
        return new DateTimeOffset(local, tz.GetUtcOffset(local)).UtcDateTime;
    }

    /// <summary>UTC-момент напоминания (срок минус офсет); null — напоминание не настроено.</summary>
    public static DateTime? ReminderMomentUtc(TaskItem task, TimeZoneInfo tz) =>
        task.ReminderMinutes is int minutes
            ? DueMomentUtc(task, tz) - TimeSpan.FromMinutes(minutes)
            : null;

    /// <summary>Таймзона по IANA-иду с фолбэком на UTC (повреждённое/неизвестное значение).</summary>
    public static TimeZoneInfo ResolveTimeZone(string? ianaId) =>
        ianaId is not null && TimeZoneInfo.TryFindSystemTimeZoneById(ianaId, out var tz)
            ? tz
            : TimeZoneInfo.Utc;
}
