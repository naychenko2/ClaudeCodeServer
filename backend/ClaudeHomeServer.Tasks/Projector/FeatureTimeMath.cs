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

    // Целые минуты от суммарного TimeSpan (округление вверх; 0 при ≤ 0). Для итогов:
    // один ceil на суммарное время вида, а не сумма ceil по сегментам (major 2: сумма
    // округлений сегментов ≠ elapsed, т.к. elapsed округляется один раз от общего интервала).
    public static int CeilMinutes(TimeSpan total) =>
        total <= TimeSpan.Zero ? 0 : (int)Math.Ceiling(total.TotalMinutes);

    // Метод наибольшего остатка (largest remainder): раздать `elapsed` целых минут по видам,
    // чтобы сумма была РОВНО `elapsed` при любом раскладе. Базис — floor(span) каждого вида;
    // дефицит `elapsed − сумма floor` (0..4: сырые span-ы плиткой покрывают [from, to],
    // поэтому их сумма = to − from, а elapsed = ceil(этого); дефицит = ceil(суммы дробных
    // частей), а сумма дробных частей < 4 (четыре вида) → 0..4) раздаётся видам с наибольшей
    // дробной частью (tie — по индексу, детерминизм). Каждый вид ≈ своему span, но сумма не
    // «уплывает» выше elapsed, как при независимых ceil (проба: ход 30 с + ожидание 20 с +
    // ошибка 10 с в окне 2 мин → независимые ceil дали 1+1+1+0 = 3 ≠ 2; здесь work=1,
    // waiting=0, error=0, idle=1 → сумма 2 = elapsed).
    // Возвращает (work, waiting, error, idle) в порядке аргументов.
    public static (int Work, int Waiting, int Error, int Idle) LargestRemainderMinutes(
        int elapsed, TimeSpan work, TimeSpan waiting, TimeSpan error, TimeSpan idle)
    {
        var spans = new[] { work, waiting, error, idle };
        var floors = new int[4];
        var fracs = new double[4];
        for (var i = 0; i < 4; i++)
        {
            floors[i] = (int)Math.Floor(spans[i].TotalMinutes);
            fracs[i] = spans[i].TotalMinutes - floors[i];
        }
        var sumFloors = floors[0] + floors[1] + floors[2] + floors[3];
        var deficit = elapsed - sumFloors; // 0..4 (см. комментарий выше)
        // Индексы по убыванию дробной части; при равенстве — по индексу (детерминизм).
        var order = Enumerable.Range(0, 4)
            .OrderByDescending(i => fracs[i])
            .ThenBy(i => i)
            .ToList();
        var result = floors;
        for (var k = 0; k < deficit && k < order.Count; k++)
            result[order[k]]++;
        return (result[0], result[1], result[2], result[3]);
    }

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
