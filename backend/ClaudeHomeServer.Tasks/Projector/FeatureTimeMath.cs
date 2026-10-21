using ClaudeHomeServer.Models;

namespace ClaudeHomeServer.Services.Tasks.Projector;

// Единая точка «математики времени» фичи (волна 3 «Обзор фич», Б5b): функции, которые раньше
// копировались по FeatureTimeline/FeatureBreakdown/FeatureProjector (RoleToStage, MergeIntervals,
// Minutes, UnionMinutes, Clamp). Свод в одно место — чтобы два/три места не разойдлись (минор 8
// ревью Глеба). Чистые статические функции без состояния — не тянут швов между вертикалями.
internal static class FeatureTimeMath
{
    // Роль задачи → ключ стадии конвейера (таблица одна на весь «Обзор фич»).
    // cut/consult → cut (Разрез); step/rework → prod; прочее → prod.
    public static string RoleToStage(string? role) => role switch
    {
        TaskRoles.Cut or TaskRoles.Consult => "cut",
        TaskRoles.Plan => "plan",
        TaskRoles.Step or TaskRoles.Rework => "prod",
        TaskRoles.Review => "review",
        TaskRoles.Qa => "qa",
        TaskRoles.Final => "final",
        TaskRoles.Merge => "merge",
        _ => "prod",
    };

    // Объединение перекрывающихся/смежных интервалов: возвращает список (start, end) без
    // пересечений, в порядке start. Вход/выход — именованные (start, end).
    public static List<(DateTime start, DateTime end)> MergeIntervals(
        IReadOnlyList<(DateTime start, DateTime end)> intervals)
    {
        var sorted = intervals.OrderBy(iv => iv.start).ToList();
        var result = new List<(DateTime start, DateTime end)>();
        foreach (var (start, end) in sorted)
        {
            if (result.Count > 0 && start <= result[^1].end)
            {
                var cs = result[^1].start;
                var ce = result[^1].end;
                result[^1] = (start: cs, end: end > ce ? end : ce);
            }
            else
            {
                result.Add((start: start, end: end));
            }
        }
        return result;
    }

    // Длительность [from, to] в минутах (округление вверх до минуты; 0 при to ≤ from).
    public static int Minutes(DateTime from, DateTime to) =>
        to <= from ? 0 : (int)Math.Ceiling((to - from).TotalMinutes);

    // Объединение интервалов (обрезка к [from, to], если задано) → минуты по часам стены
    // (округление вверх; 0 — нет интервалов). Параллельные интервалы не складываются.
    public static int UnionMinutes(
        IReadOnlyList<(DateTime start, DateTime end)> intervals, DateTime? from = null, DateTime? to = null)
    {
        var clamped = intervals
            .Select(iv => (start: Clamp(iv.start, from, to), end: Clamp(iv.end, from, to)))
            .Where(iv => iv.end > iv.start)
            .ToList();
        var merged = MergeIntervals(clamped);
        var total = TimeSpan.Zero;
        foreach (var (s, e) in merged)
            total += e - s;
        return (int)Math.Ceiling(total.TotalMinutes);
    }

    // Обрезка t к [from, to]; null-границы не режут.
    public static DateTime Clamp(DateTime t, DateTime? from, DateTime? to)
    {
        if (from is not null && t < from) t = from.Value;
        if (to is not null && t > to) t = to.Value;
        return t;
    }
}
