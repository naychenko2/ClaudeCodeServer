using ClaudeHomeServer.Models;

namespace ClaudeHomeServer.Services;

// Расчёт даты следующего экземпляра регулярной задачи. Отсчёт всегда от срока
// текущего экземпляра (не от даты завершения) — расписание не «плывёт».
public static class TaskRecurrenceCalculator
{
    /// <summary>
    /// Дата следующего экземпляра (YYYY-MM-DD) или null — серия закончена (Until)
    /// либо правило/срок некорректны.
    /// </summary>
    public static string? NextDueDate(string currentDueDate, TaskRecurrence rule)
    {
        if (!DateOnly.TryParseExact(currentDueDate, "yyyy-MM-dd", out var current)) return null;
        var interval = Math.Max(1, rule.Interval);

        DateOnly? next = rule.Type switch
        {
            TaskRecurrenceType.Daily => current.AddDays(interval),
            TaskRecurrenceType.Weekly => NextWeekly(current, interval, rule.Weekdays),
            TaskRecurrenceType.Monthly => current.AddMonths(interval),  // AddMonths клипует 31-е на конец месяца
            TaskRecurrenceType.Yearly => current.AddYears(interval),    // AddYears клипует 29 февраля
            _ => null,
        };
        if (next is null) return null;

        if (rule.Until is not null &&
            DateOnly.TryParseExact(rule.Until, "yyyy-MM-dd", out var until) &&
            next > until)
            return null;

        return next.Value.ToString("yyyy-MM-dd");
    }

    // Еженедельно по дням недели (ISO 1=Пн … 7=Вс), каждые N недель.
    // Недели считаются от понедельника недели текущего срока.
    private static DateOnly NextWeekly(DateOnly current, int interval, List<int>? weekdays)
    {
        var days = weekdays is { Count: > 0 }
            ? weekdays.Where(d => d is >= 1 and <= 7).Distinct().Order().ToList()
            : [IsoWeekday(current)];
        if (days.Count == 0) days = [IsoWeekday(current)];

        var currentWeekMonday = current.AddDays(1 - IsoWeekday(current));
        // Достаточно просмотреть два интервала недель вперёд
        for (var offset = 1; offset <= interval * 14; offset++)
        {
            var candidate = current.AddDays(offset);
            if (!days.Contains(IsoWeekday(candidate))) continue;
            var candidateWeekMonday = candidate.AddDays(1 - IsoWeekday(candidate));
            var weeksBetween = (candidateWeekMonday.DayNumber - currentWeekMonday.DayNumber) / 7;
            if (weeksBetween % interval == 0) return candidate;
        }
        // Недостижимо при валидных данных, но компилятору нужен исход
        return current.AddDays(7 * interval);
    }

    private static int IsoWeekday(DateOnly d) => d.DayOfWeek == DayOfWeek.Sunday ? 7 : (int)d.DayOfWeek;
}
