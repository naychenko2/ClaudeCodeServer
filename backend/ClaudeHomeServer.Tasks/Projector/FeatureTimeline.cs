using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.Tasks.Journal;

namespace ClaudeHomeServer.Services.Tasks.Projector;

// ─── Записи графика (JSON camelCase) ─────────────────────────────────────────────────────────

// Стадия на графике: ключ конвейера + границы (старт/конец). Границы — из FeatureStage
// (FirstStartedAt/LastCompletedAt): старт = минимальный ClaudeStartedAt задач стадии,
// конец = максимальный CompletedAt (только если все задачи стадии закрыты), иначе null.
public sealed record TimelineStage(
    // Ключ стадии: cut | plan | prod | review | qa | final | merge.
    string Key,
    // Начало стадии; null — ни одна задача стадии не стартовала.
    DateTime? StartedAt,
    // Конец стадии; null — стадия ещё не завершилась (не все задачи Done).
    DateTime? EndedAt);

// Сегмент времени: интервал [From, To] с видом, треком, стадией и (опц.) задачей.
// Kind: "work" | "waiting" | "idle" | "error" | "notWorking" (в approx только work/notWorking).
// Track: "main" (производство) | "review" (ревью). Stage — ключ стадии. TaskId — null у
// склеенных/промежутков, id задачи у одиночного work-сегмента.
public sealed record TimelineSegment(
    DateTime From,
    DateTime To,
    string Kind,
    string Track,
    string Stage,
    string? TaskId);

// «Сейчас»: текущее состояние фичи (waiting/stopped/merge_pending) с моментом и задачей.
public sealed record TimelineCurrent(
    // waiting | stopped | merge_pending.
    string Kind,
    // С какого момента: AwaitingHumanSince (waiting) / ExecutorStoppedAt (stopped) /
    // CompletedAt финала (merge_pending).
    DateTime Since,
    string? TaskId);

// Итоги по минутам. В approx Waiting*/Idle*/Error* = null (не различаем без журнала).
public sealed record TimelineTotals(
    // Общее время: To − From (минуты).
    int ElapsedMinutes,
    // Работа по часам стены: ОБЪЕДИНЕНИЕ work-интервалов (параллельные не складываются).
    int WorkWallMinutes,
    // Сумма work-интервалов (параллельные складываются) — «агентные» минуты.
    int AgentsSumMinutes,
    // Простой: Elapsed − WorkWall (в approx = все промежутки, включая ночную паузу > 2 ч).
    int NotWorkingMinutes,
    int? WaitingMinutes,
    int? IdleMinutes,
    int? WaitingCount,
    int? LongestWaitMinutes,
    int? ErrorCount,
    int? ErrorMinutes);

// Статистика фичи (GET features/{id}, поле stats): источник (approx/journal), признак
// обрезки, график, разрезы («кто и на чём работал») и «дольше всех сейчас».
public sealed record FeatureStats(
    // approx | journal.
    string Source,
    // Обрезка (в approx — false: журнал не читается).
    bool Truncated,
    FeatureTimeline Timeline,
    // Разрезы времени по персонам/моделям/ролям (Б6); null — не заполнено (индекс/список).
    FeatureBreakdown? Breakdown = null,
    // «Дольше всех сейчас» (Б6); null — ничего не идёт или не заполнено.
    FeatureLongestNow? LongestNow = null);

// ─── График времени фичи ─────────────────────────────────────────────────────────────────────

// График времени фичи (волна 3 «Обзор фич», вкладка «Статистика»): границы, интервалы
// работы/простоя, «сейчас», итоги. Чистая проекция FeatureRun → FeatureTimeline.
//
// Режим approx (Б5a): по данным задач, без журнала.
//   • work — интервалы по задачам этапов [ClaudeStartedAt, CompletedAt ?? now];
//   • notWorking — промежутки между work (в approx «ожидание/простой/ошибка» не различаются,
//     поэтому Waiting*/Idle*/Error* = null); ночная пауза > 2 ч — в notWorking;
//   • current — по тревоге фичи (waiting/stopped/merge_pending).
// Параллельные задачи склеиваются в один сегмент на (track, stage). Точный режим по
// журналу — Б5b (параметр journal оставлен, пока игнорируется).
public sealed record FeatureTimeline(
    // Момент создания эпика.
    DateTime From,
    // now, либо момент мержа/закрытия.
    DateTime To,
    // Стадии конвейера (границы).
    List<TimelineStage> Stages,
    // Сегменты (work + notWorking), в хронологическом порядке.
    List<TimelineSegment> Segments,
    // «Сейчас» (null — фича не ждёт/не встала/не ждёт мержа).
    TimelineCurrent? Current,
    // Итоги.
    TimelineTotals Totals)
{
    // Потолок сегментов: при превышении — склейка по возрастающему порогу.
    // public static (не const) — чтобы тест мог снять потолок (мутация) и проверить склейку.
    public static int MaxSegments = 500;

    public static FeatureTimeline Build(FeatureRun run, FeatureJournalReadResult? journal, DateTime now)
    {
        ArgumentNullException.ThrowIfNull(run);
        _ = journal; // Б5a: журнал игнорируется (всегда approx); параметр — для Б5b.

        // Границы: From — создание эпика; To — now, либо момент мержа/закрытия.
        var from = run.Epic.CreatedAt;
        var to = ClosureAt(run) ?? now;
        if (to < from) to = from;

        // Стадии: из готовых FeatureStage (FirstStartedAt/LastCompletedAt уже вычислены).
        var stages = run.Stages
            .Select(s => new TimelineStage(s.Key, s.FirstStartedAt, s.LastCompletedAt))
            .ToList();

        // Work-интервалы: по задачам с ClaudeStartedAt, [ClaudeStartedAt, CompletedAt ?? now].
        var work = new List<WorkInterval>();
        foreach (var t in AllTasks(run))
        {
            if (t.ClaudeStartedAt is null) continue;
            var start = t.ClaudeStartedAt.Value;
            var end = t.CompletedAt ?? now;
            if (end < start) end = start;
            var stage = RoleToStage(t.Role);
            var track = stage == "review" ? "review" : "main";
            work.Add(new WorkInterval(start, end, stage, track, t.Id));
        }

        // Сегменты: work (склейка по (track, stage)) + notWorking (промежутки), по времени.
        var segments = BuildSegments(work, from, to);

        // Итоги.
        var elapsed = Minutes(from, to);
        var workWall = UnionMinutes(work, from, to);
        var agentsSum = work.Sum(w => Minutes(w.Start, w.End));
        var notWorking = Math.Max(0, elapsed - workWall);
        var totals = new TimelineTotals(
            ElapsedMinutes: elapsed,
            WorkWallMinutes: workWall,
            AgentsSumMinutes: agentsSum,
            NotWorkingMinutes: notWorking,
            // approx: «ожидание/простой/ошибка» не различаем — null.
            WaitingMinutes: null,
            IdleMinutes: null,
            WaitingCount: null,
            LongestWaitMinutes: null,
            ErrorCount: null,
            ErrorMinutes: null);

        // «Сейчас»: из тревоги фичи (waiting/stopped/merge_pending).
        var current = CurrentOf(run);

        return new FeatureTimeline(from, to, stages, segments, current, totals);
    }

    // ─── Вспомогательные ──────────────────────────────────────────────────────────────────────

    // Момент мержа/закрытия: merge Done → CompletedAt merge; epic Done → CompletedAt epic;
    // иначе null (To = now).
    private static DateTime? ClosureAt(FeatureRun run)
    {
        if (run.Merge is { Status: TaskItemStatus.Done } && run.Merge.CompletedAt is not null)
            return run.Merge.CompletedAt.Value;
        if (run.Epic.Status == TaskItemStatus.Done && run.Epic.CompletedAt is not null)
            return run.Epic.CompletedAt.Value;
        return null;
    }

    // Все задачи фичи: эпик + дочерние (шаги/ревью/проверки/финал/мерж/cut+consult/plan).
    private static IEnumerable<TaskItem> AllTasks(FeatureRun run)
    {
        yield return run.Epic;
        foreach (var t in run.Steps) yield return t;
        foreach (var t in run.Reviews) yield return t;
        foreach (var t in run.Qa) yield return t;
        if (run.Final is not null) yield return run.Final;
        if (run.Merge is not null) yield return run.Merge;
        foreach (var t in run.Cut) yield return t;
        foreach (var t in run.Plan) yield return t;
    }

    // Роль задачи → ключ стадии конвейера (та же таблица, что FeatureProjector.RoleToStage).
    private static string RoleToStage(string? role) => role switch
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

    // «Сейчас» из тревоги фичи: kind/Сince/TaskId. attention null → null.
    // attention.kind: stopped → stopped; wait → waiting; merge → merge_pending.
    private static TimelineCurrent? CurrentOf(FeatureRun run)
    {
        var a = run.Attention;
        if (a is null || a.Since is null) return null;
        var kind = a.Kind switch
        {
            "stopped" => "stopped",
            "wait" => "waiting",
            "merge" => "merge_pending",
            _ => null,
        };
        return kind is null ? null : new TimelineCurrent(kind, a.Since.Value, a.TaskId);
    }

    // Сегменты: work (склейка по (track, stage)) + notWorking (промежутки), в хронологическом
    // порядке. work-сегменты склеиваются внутри (track, stage); notWorking — промежутки между
    // объединением всех work-интервалов (на [from, to]).
    private static List<TimelineSegment> BuildSegments(List<WorkInterval> work, DateTime from, DateTime to)
    {
        var segments = new List<TimelineSegment>();

        // 1) work: склейка по (track, stage) — объединение перекрывающихся/смежных интервалов.
        var byGroup = work
            .GroupBy(w => (w.Track, w.Stage))
            .Select(g => (Track: g.Key.Item1, Stage: g.Key.Item2, Intervals: g.ToList()))
            .ToList();
        foreach (var (track, stage, intervals) in byGroup)
        {
            foreach (var (s, e) in MergeIntervals(intervals.Select(w => (start: w.Start, end: w.End)).ToList()))
            {
                // Одиночный (не склеенный) интервал несёт taskId; склеенный — null.
                var single = intervals.Count == 1 ? intervals[0].TaskId : null;
                segments.Add(new TimelineSegment(s, e, "work", track, stage, single));
            }
        }

        // 2) notWorking: промежутки между объединением ВСЕХ work-интервалов (на [from, to]).
        var union = MergeIntervals(work.Select(w => (start: Clamp(w.Start, from, to), end: Clamp(w.End, from, to))).ToList());
        var cursor = from;
        foreach (var (s, e) in union)
        {
            if (s > cursor)
                segments.Add(new TimelineSegment(cursor, s, "notWorking", "main", "", null));
            cursor = e > cursor ? e : cursor;
        }
        if (to > cursor)
            segments.Add(new TimelineSegment(cursor, to, "notWorking", "main", "", null));

        // 3) порядок: по From, при равенстве — work перед notWorking (детерминизм).
        segments.Sort((a, b) =>
        {
            var c = a.From.CompareTo(b.From);
            if (c != 0) return c;
            return a.Kind == "work" ? -1 : 1;
        });

        // 4) потолок: при > MaxSegments — склейка по возрастающему порогу.
        if (segments.Count > MaxSegments)
            segments = MergeByThreshold(segments, from, to);

        return segments;
    }

    // Склейка по возрастающему порогу: пока сегментов > max — склеиваем СОСЕДНИЕ (по времени)
    // сегменты с разрывом ≤ threshold; threshold растёт (1, 2, 4, … мин) до сходимости.
    // Склеиваем по времени (не по стадии): work и notWorking чередуются, и только склейка
    // по времени реально снижает число. Вид/трек/стадия результата — от предыдущего сегмента,
    // taskId=null (склейка). Возвращает новый список (вход не мутируется).
    private static List<TimelineSegment> MergeByThreshold(List<TimelineSegment> segments, DateTime from, DateTime to)
    {
        var list = segments.ToList();
        var threshold = TimeSpan.FromMinutes(1);
        // Защита от бесконечного цикла: порог не превышает (to − from).
        var maxThreshold = to - from;
        while (list.Count > MaxSegments && threshold <= maxThreshold)
        {
            var merged = new List<TimelineSegment>(list.Count);
            foreach (var s in list)
            {
                var prev = merged.Count > 0 ? merged[^1] : null;
                if (prev is not null && s.From - prev.To <= threshold)
                {
                    // Склеить: расширяем конец предыдущего (вид/трек/стадия — от предыдущего).
                    merged[^1] = new TimelineSegment(prev.From, s.To, prev.Kind, prev.Track, prev.Stage, null);
                }
                else
                {
                    merged.Add(s);
                }
            }
            if (merged.Count == list.Count) break; // не сжалось — выход, иначе бесконечный цикл
            list = merged;
            threshold *= 2;
        }
        return list;
    }

    // Объединение интервалов: вход/выход — именованные (start, end). Возвращает список
    // (start, end) без пересечений, в порядке start.
    private static List<(DateTime start, DateTime end)> MergeIntervals(List<(DateTime start, DateTime end)> intervals)
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
    private static int Minutes(DateTime from, DateTime to) =>
        to <= from ? 0 : (int)Math.Ceiling((to - from).TotalMinutes);

    // Объединение work-интервалов (обрезка к [from, to]) → минуты по часам стены.
    private static int UnionMinutes(List<WorkInterval> work, DateTime from, DateTime to)
    {
        var union = MergeIntervals(work
            .Select(w => (start: Clamp(w.Start, from, to), end: Clamp(w.End, from, to)))
            .Where(iv => iv.end > iv.start)
            .ToList());
        var total = TimeSpan.Zero;
        foreach (var (s, e) in union)
            total += e - s;
        return (int)Math.Ceiling(total.TotalMinutes);
    }

    // Обрезка t к [from, to].
    private static DateTime Clamp(DateTime t, DateTime from, DateTime to) =>
        t < from ? from : (t > to ? to : t);

    // Work-интервал одной задачи: [Start, End], стадия, трек, id задачи.
    private sealed record WorkInterval(DateTime Start, DateTime End, string Stage, string Track, string TaskId);
}
