using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.Tasks.Journal;

namespace ClaudeHomeServer.Services.Tasks.Projector;

// Единая точка «математики времени» фичи (волна 3 «Обзор фич», Б5b): функции, которые раньше
// копировались по FeatureTimeline/FeatureBreakdown/FeatureProjector (RoleToStage, MergeIntervals,
// Minutes, UnionMinutes, Clamp). Свод в одно место — чтобы два/три места не разойдлись (минор 8
// ревью Глеба). Чистые статические функции без состояния — не тянут швов между вертикалями.
internal static class FeatureTimeMath
{
    // Роль задачи → ключ стадии конвейера (таблица одна на весь «Обзор фич»).
    // cut/consult → cut (Разрез); design → design (необязательный «Дизайн», W4-DES-B);
    // step/rework → prod; прочее → prod.
    public static string RoleToStage(string? role) => role switch
    {
        TaskRoles.Cut or TaskRoles.Consult => "cut",
        TaskRoles.Design => "design",
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
    // дефицит `elapsed − сумма floor` (0..n, n = число span-ов: они плиткой покрывают
    // [from, to], поэтому их сумма = to − from, а elapsed = ceil(этого); дефицит = ceil(суммы
    // дробных частей), а сумма дробных частей < n → 0..n) раздаётся видам с наибольшей
    // дробной частью (tie — по индексу, детерминизм). Каждый вид ≈ своему span, но сумма не
    // «уплывает» выше elapsed, как при независимых ceil (проба: ход 30 с + ожидание 20 с +
    // ошибка 10 с в окне 2 мин → независимые ceil дали 1+1+1+0 = 3 ≠ 2; здесь work=1,
    // waiting=0, error=0, idle=1 → сумма 2 = elapsed).
    // Возвращает минуты в порядке аргументов. Математика не знает, что это за виды:
    // состав span-ов (work/waiting/…/queued) решает вызывающий.
    public static int[] LargestRemainderMinutes(int elapsed, params TimeSpan[] spans)
    {
        var n = spans.Length;
        var floors = new int[n];
        var fracs = new double[n];
        for (var i = 0; i < n; i++)
        {
            floors[i] = (int)Math.Floor(spans[i].TotalMinutes);
            fracs[i] = spans[i].TotalMinutes - floors[i];
        }
        var sumFloors = floors.Sum();
        var deficit = elapsed - sumFloors; // 0..n (см. комментарий выше)
        // Индексы по убыванию дробной части; при равенстве — по индексу (детерминизм).
        var order = Enumerable.Range(0, n)
            .OrderByDescending(i => fracs[i])
            .ThenBy(i => i)
            .ToList();
        var result = floors;
        for (var k = 0; k < deficit && k < order.Count; k++)
            result[order[k]]++;
        return result;
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

    // Эпизод ожидания: задача, начало и конец (конец уже обрезан по правой границе окна).
    internal readonly record struct WaitEpisode(string? TaskId, DateTime Start, DateTime End);

    // Эпизоды ожидания (И4, мини-разрез D0): единая точка правила «начало → конец» для всех
    // потребителей минут ожидания (FeatureTimeline, FeatureTypical, FeatureStageMetrics).
    // Начало — событие startKind с At в [from, to); конец — первое событие clearKind той же задачи
    // с At ≥ Start, иначе now; затем обрезка по `to`. Нулевой эпизод (clear в ту же миллисекунду)
    // возвращается — он считается как факт ожидания, минут не даёт. Порядок — как в журнале.
    internal static List<WaitEpisode> WaitingEpisodes(
        IReadOnlyList<FeatureJournalEntry> entries, string startKind, string clearKind,
        DateTime from, DateTime to, DateTime now) =>
        WaitingEpisodes(entries, [(startKind, clearKind)], from, to, now);

    // Несколько пар видов за один проход по журналу (таймлайн: waiting + device_wait): порядок
    // эпизодов — порядок их начал в журнале, от него зависит «первый при равенстве» самого долгого.
    internal static List<WaitEpisode> WaitingEpisodes(
        IReadOnlyList<FeatureJournalEntry> entries, IReadOnlyList<(string Start, string Clear)> kinds,
        DateTime from, DateTime to, DateTime now)
    {
        var clearedByKind = new Dictionary<string, Dictionary<string, List<DateTime>>>();
        foreach (var (_, clear) in kinds)
        {
            if (clearedByKind.ContainsKey(clear)) continue;
            var byTask = new Dictionary<string, List<DateTime>>();
            foreach (var e in entries)
            {
                if (e.Kind != clear || e.TaskId is null) continue;
                if (!byTask.TryGetValue(e.TaskId, out var list)) byTask[e.TaskId] = list = [];
                list.Add(e.At);
            }
            foreach (var list in byTask.Values) list.Sort();
            clearedByKind[clear] = byTask;
        }

        var result = new List<WaitEpisode>();
        foreach (var e in entries)
        {
            if (e.At < from || e.At >= to) continue;
            foreach (var (start, clear) in kinds)
            {
                if (e.Kind != start) continue;
                DateTime? cleared = null;
                if (e.TaskId is not null && clearedByKind[clear].TryGetValue(e.TaskId, out var list))
                    foreach (var at in list)
                        if (at >= e.At) { cleared = at; break; }
                var end = cleared ?? now;
                result.Add(new WaitEpisode(e.TaskId, e.At, end <= to ? end : to));
                break;
            }
        }
        return result;
    }

    // Интервалы очереди фич (R-Q3B-2): held(reason=queue) открывает интервал, его закрывает
    // ближайший hold_cleared или другой held (смена причины), иначе — now. Обрезанные к [from, to],
    // half-open [start, end), пустые отброшены; между собой не пересекаются (каждый held
    // закрывается следующим hold-событием).
    internal static List<(DateTime Start, DateTime End)> QueueIntervals(
        IReadOnlyList<FeatureJournalEntry> entries, DateTime from, DateTime to, DateTime now)
    {
        var holds = entries.Where(e => e.Kind is "held" or "hold_cleared").OrderBy(e => e.At).ToList();
        var result = new List<(DateTime Start, DateTime End)>();
        for (var i = 0; i < holds.Count; i++)
        {
            if (holds[i].Kind != "held" || holds[i].Reason != FeatureHoldReasons.Queue) continue;
            var start = holds[i].At;
            var end = i + 1 < holds.Count ? holds[i + 1].At : now;
            var qs = Clamp(start, from, to);
            var qe = Clamp(end, from, to);
            if (qe > qs) result.Add((qs, qe));
        }
        return result;
    }

    // Net-длительность интервала [start, end): сырой интервал минус пересечения с интервалами
    // очереди — пока фича стояла в очереди, она ждала место, а не человека (R-Q3B-4, waiting) и
    // не работала (R-Q3B-5, AgentsSum). start/end уже обрезаны к [from, to]; интервалы очереди
    // не пересекаются между собой (построение QueueIntervals), поэтому пересечения не
    // складываются дважды и net ≥ 0.
    internal static TimeSpan NetSpan(
        DateTime start, DateTime end, List<(DateTime Start, DateTime End)> queueIntervals)
    {
        var net = end - start;
        foreach (var (qs, qe) in queueIntervals)
        {
            var os = start > qs ? start : qs;
            var oe = end < qe ? end : qe;
            if (oe > os) net -= oe - os;
        }
        return net;
    }

    // Объединение интервалов (обрезка к [from, to]) за вычетом очереди и, если задано, интервалов
    // `cut` (у долей этапа — ожидание человека): остаток — набор непересекающихся интервалов.
    // Пересечения исходных интервалов считаются один раз.
    internal static List<(DateTime start, DateTime end)> NetIntervals(
        IReadOnlyList<(DateTime start, DateTime end)> intervals,
        List<(DateTime Start, DateTime End)> queueIntervals,
        DateTime from, DateTime to,
        IReadOnlyList<(DateTime start, DateTime end)>? cut = null)
    {
        var clamped = intervals
            .Select(iv => (start: Clamp(iv.start, from, to), end: Clamp(iv.end, from, to)))
            .Where(iv => iv.end > iv.start)
            .ToList();
        var blocked = queueIntervals.Select(q => (start: q.Start, end: q.End)).ToList();
        if (cut is not null) blocked.AddRange(cut);
        var mergedBlocked = MergeIntervals(blocked);
        var result = new List<(DateTime start, DateTime end)>();
        foreach (var (s, e) in MergeIntervals(clamped))
        {
            var cursor = s;
            foreach (var (bs, be) in mergedBlocked)
            {
                if (be <= cursor || bs >= e) continue;
                if (bs > cursor) result.Add((cursor, bs));
                if (be > cursor) cursor = be;
            }
            if (e > cursor) result.Add((cursor, e));
        }
        return result;
    }

    // Суммарное время NetIntervals; округление — на вызывающем (CeilMinutes от суммы).
    internal static TimeSpan NetUnionSpan(
        IReadOnlyList<(DateTime start, DateTime end)> intervals,
        List<(DateTime Start, DateTime End)> queueIntervals,
        DateTime from, DateTime to,
        IReadOnlyList<(DateTime start, DateTime end)>? cut = null)
    {
        var total = TimeSpan.Zero;
        foreach (var (s, e) in NetIntervals(intervals, queueIntervals, from, to, cut))
            total += e - s;
        return total;
    }

    // Упавший ход (И7): outcome есть и не success. Словарь исходов растёт (паспорт хода:
    // failed/egress_down/interrupted/cancelled/crashed, фолбэк локального движка: local_*,
    // window_1m_unavailable), поэтому отрицание, а не список: новый отказ попадает сам.
    // outcome = null (нет ни паспорта, ни исхода) — не ошибка.
    internal static bool IsFailedTurn(string? outcome) =>
        outcome is not null && outcome != "success";
}
