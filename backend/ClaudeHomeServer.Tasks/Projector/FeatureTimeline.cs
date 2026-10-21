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
// работы/простоя, «сейчас», итоги. Чистая проекция (FeatureRun, journal, now) → FeatureTimeline.
//
// Режим approx (Б5a): по данным задач, без журнала.
//   • work — интервалы по задачам этапов [ClaudeStartedAt, CompletedAt ?? now];
//   • notWorking — промежутки между work (в approx «ожидание/простой/ошибка» не различаются,
//     поэтому Waiting*/Idle*/Error* = null); ночная пауза > 2 ч — в notWorking;
//   • current — по тревоге фичи (waiting/stopped/merge_pending).
// Параллельные задачи склеиваются в один сегмент на (track, stage).
//
// Режим journal (Б5b): полный журнал (StartsWithCreated && !Truncated) → точные интервалы:
//   • work — по turn (startedAt..endedAt) и по периодам started…(stopped | done | now);
//   • waiting — waiting…(waiting_cleared | now) + device_wait…(device_wait_cleared | now)
//     (ожидание устройства — как waiting; см. комментарий в BuildJournal);
//   • error — turn.outcome=error (startedAt..endedAt) + stopped (точка);
//   • idle — остаток (не work/waiting/error). Sweep-line: перекрытия решаются приоритетом
//     error > work > waiting. Заполняются Waiting*/Idle*/Error* (в approx — null).
// Режим «≈» — если журнала нет, он не начинается с created, либо Truncated (фичи до журнала
// целиком approx, гибрида нет).
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

        // Границы: From — создание эпика; To — now, либо момент мержа/закрытия.
        var from = run.Epic.CreatedAt;
        var to = ClosureAt(run) ?? now;
        if (to < from) to = from;

        // Стадии: из готовых FeatureStage (FirstStartedAt/LastCompletedAt уже вычислены).
        var stages = run.Stages
            .Select(s => new TimelineStage(s.Key, s.FirstStartedAt, s.LastCompletedAt))
            .ToList();

        // «Сейчас»: из тревоги фичи (waiting/stopped/merge_pending) — в обоих режимах.
        var current = CurrentOf(run);

        // Режим: journal — полный журнал (StartsWithCreated && !Truncated); иначе approx (по
        // задачам). Фичи до журнала / обрезанный журнал → целиком approx (гибрида нет).
        var isJournal = journal is { StartsWithCreated: true, Truncated: false };

        if (isJournal)
        {
            var (journalSegments, journalTotals) = BuildJournal(run, journal!, from, to, now);
            return new FeatureTimeline(from, to, stages, journalSegments, current, journalTotals);
        }

        // approx (Б5a): work — по задачам [ClaudeStartedAt, CompletedAt ?? now].
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

        return new FeatureTimeline(from, to, stages, segments, current, totals);
    }

    // ─── Точный режим по журналу (Б5b) ───────────────────────────────────────────────────────

    // Интервал журнала: [Start, End] (обрезается к [from, to] при склейке), вид, приоритет.
    private sealed record JournalInterval(DateTime Start, DateTime End, string Kind, int Priority);

    // Приоритеты видов при перекрытии (sweep-line): error > work > waiting; idle — остаток.
    private const int PriorityError = 3;
    private const int PriorityWork = 2;
    private const int PriorityWaiting = 1;

    // Точный режим: события журнала → интервалы work/waiting/error → sweep-line → сегменты
    // (work/waiting/error/idle) + итоги (waiting/idle/error, count, longest). Чистая функция от
    // (run, journal, now): run даёт taskId→stage (подпись work), journal — события, now — «сейчас».
    private static (List<TimelineSegment> Segments, TimelineTotals Totals) BuildJournal(
        FeatureRun run, FeatureJournalReadResult journal, DateTime from, DateTime to, DateTime now)
    {
        // run — для AllTasks (стадии/задачи фичи); journal — события; now — «сейчас».
        _ = run;

        // Индексы событий по taskId (для пар «начало → конец»): stopped, status→Done,
        // waiting_cleared, device_wait_cleared — каждый в порядке At.
        var stoppedByTask = GroupByTask(journal.Entries, "stopped");
        var doneByTask = journal.Entries
            .Where(e => e.Kind == "status" && e.To == "Done")
            .GroupBy(e => e.TaskId ?? "")
            .ToDictionary(g => g.Key, g => g.OrderBy(e => e.At).ToList());
        var waitingClearedByTask = GroupByTask(journal.Entries, "waiting_cleared");
        var deviceWaitClearedByTask = GroupByTask(journal.Entries, "device_wait_cleared");

        var intervals = new List<JournalInterval>();
        var waitingCount = 0;
        var longestWait = TimeSpan.Zero;
        var errorCount = 0;

        foreach (var e in journal.Entries)
        {
            switch (e.Kind)
            {
                // work: ход (startedAt..endedAt); если ход завершился ошибкой — и error (счёт).
                case "turn" when e.StartedAt is not null && e.EndedAt is not null:
                    intervals.Add(new JournalInterval(e.StartedAt.Value, e.EndedAt.Value, "work", PriorityWork));
                    if (e.Outcome == "error")
                    {
                        errorCount++;
                        intervals.Add(new JournalInterval(e.StartedAt.Value, e.EndedAt.Value, "error", PriorityError));
                    }
                    break;

                // work: период started…(stopped | done | now).
                case "started" when e.StartedAt is not null:
                    var end = FirstAfter(stoppedByTask, e.TaskId, e.StartedAt.Value)
                        ?? FirstAfter(doneByTask, e.TaskId, e.StartedAt.Value)
                        ?? now;
                    intervals.Add(new JournalInterval(e.StartedAt.Value, end, "work", PriorityWork));
                    break;

                // error: остановка исполнителя — точка (0 мин, но в счёт).
                case "stopped":
                    errorCount++;
                    intervals.Add(new JournalInterval(e.At, e.At, "error", PriorityError));
                    break;

                // waiting: ожидание человека waiting…(waiting_cleared | now).
                case "waiting" when e.At > from:
                    var wEnd = FirstAfter(waitingClearedByTask, e.TaskId, e.At) ?? now;
                    waitingCount++;
                    longestWait = Max(longestWait, wEnd - e.At);
                    intervals.Add(new JournalInterval(e.At, wEnd, "waiting", PriorityWaiting));
                    break;

                // waiting: ожидание устройства device_wait…(device_wait_cleared | now).
                // Пометка «устройство»: контракт TimelineSegment (Б5a) не имеет отдельного поля
                // для под-вида ожидания, поэтому device_wait рисуется как обычный waiting
                // (kind="waiting"); totals не дробят waiting по под-видам (WaitingMinutes — сумма
                // обоих). Отдельный kind="device_wait" не введён, чтобы не ломать фронт-контракт.
                case "device_wait" when e.At > from:
                    var dEnd = FirstAfter(deviceWaitClearedByTask, e.TaskId, e.At) ?? now;
                    waitingCount++;
                    longestWait = Max(longestWait, dEnd - e.At);
                    intervals.Add(new JournalInterval(e.At, dEnd, "waiting", PriorityWaiting));
                    break;
            }
        }

        // Sweep-line: все границы → отрезки; на каждом отрезке — вид по приоритету, иначе idle.
        var segments = Sweep(intervals, from, to);

        // Итоги: work/waiting/error — объединение интервалов; idle — остаток; NotWorking =
        // Elapsed − WorkWall (та же формула, что в approx).
        var elapsed = Minutes(from, to);
        var workWall = UnionMinutesRaw(intervals.Where(i => i.Kind == "work").Select(i => (i.Start, i.End)).ToList(), from, to);
        var waitingMin = UnionMinutesRaw(intervals.Where(i => i.Kind == "waiting").Select(i => (i.Start, i.End)).ToList(), from, to);
        var errorMin = UnionMinutesRaw(intervals.Where(i => i.Kind == "error").Select(i => (i.Start, i.End)).ToList(), from, to);
        var notWorking = Math.Max(0, elapsed - workWall);
        var idleMin = Math.Max(0, notWorking - waitingMin - errorMin);
        var totals = new TimelineTotals(
            ElapsedMinutes: elapsed,
            WorkWallMinutes: workWall,
            AgentsSumMinutes: workWall, // в journal нет «агентных» минут отдельно — = work wall
            NotWorkingMinutes: notWorking,
            WaitingMinutes: waitingMin,
            IdleMinutes: idleMin,
            WaitingCount: waitingCount,
            LongestWaitMinutes: (int)Math.Ceiling(longestWait.TotalMinutes),
            ErrorCount: errorCount,
            ErrorMinutes: errorMin);

        return (segments, totals);
    }

    // taskId → события заданного kind, в порядке At (для пар «начало → конец»).
    private static Dictionary<string, List<FeatureJournalEntry>> GroupByTask(
        IEnumerable<FeatureJournalEntry> entries, string kind) =>
        entries.Where(e => e.Kind == kind)
            .GroupBy(e => e.TaskId ?? "")
            .ToDictionary(g => g.Key, g => g.OrderBy(e => e.At).ToList());

    // Первое событие (из индексa по taskId) с At ≥ after; null — нет.
    private static DateTime? FirstAfter(
        Dictionary<string, List<FeatureJournalEntry>> byTask, string? taskId, DateTime after)
    {
        if (taskId is null || !byTask.TryGetValue(taskId, out var list)) return null;
        var hit = list.FirstOrDefault(e => e.At >= after);
        return hit?.At;
    }

    // Sweep-line: все границы (from, to + start/end интервалов, обрезанные к [from, to]) →
    // отрезки; на каждом отрезке [s, e] — вид по приоритету покрывающих интервалов, иначе idle.
    // Track/stage: work — из taskId (стадия задачи), иначе main/""; taskId — у одиночного work.
    private static List<TimelineSegment> Sweep(List<JournalInterval> intervals, DateTime from, DateTime to)
    {
        var points = new SortedSet<DateTime> { from, to };
        foreach (var iv in intervals)
        {
            points.Add(Clamp(iv.Start, from, to));
            points.Add(Clamp(iv.End, from, to));
        }
        var list = points.ToList();
        var segments = new List<TimelineSegment>();
        for (var i = 0; i + 1 < list.Count; i++)
        {
            var s = list[i];
            var e = list[i + 1];
            if (e <= s) continue;
            // Покрывающие [s, e] интервалы (start ≤ s и e ≤ end).
            var covering = intervals.Where(iv => iv.Start <= s && e <= iv.End).ToList();
            // Приоритет: error > work > waiting; без покрывающих — idle. taskId у work = null
            // (склейка по времени, как в approx: sweep не знает, какой интервал «победил» по taskId).
            var kind = covering.Count == 0
                ? "idle"
                : covering.OrderByDescending(iv => iv.Priority).First().Kind;
            segments.Add(new TimelineSegment(s, e, kind, "main", "", null));
        }

        // Порядок: по From, при равенстве — work > waiting > error > idle (детерминизм).
        segments.Sort((a, b) =>
        {
            var c = a.From.CompareTo(b.From);
            if (c != 0) return c;
            return KindRank(a.Kind) - KindRank(b.Kind);
        });

        // Потолок: при > MaxSegments — склейка по возрастающему порогу (как в approx).
        if (segments.Count > MaxSegments)
            segments = MergeByThreshold(segments, from, to);

        return segments;
    }

    // Ранг вида для детерминированного порядка: work < waiting < error < idle (work первее).
    private static int KindRank(string kind) => kind switch
    {
        "work" => 0,
        "waiting" => 1,
        "error" => 2,
        _ => 3,
    };

    // Максимум TimeSpan.
    private static TimeSpan Max(TimeSpan a, TimeSpan b) => a >= b ? a : b;

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

    // Объединение сырых интервалов (start, end) (обрезка к [from, to]) → минуты по часам стены.
    // Для точного режима (Б5b): work/waiting/error-интервалы журнала.
    private static int UnionMinutesRaw(List<(DateTime start, DateTime end)> intervals, DateTime from, DateTime to)
    {
        var union = MergeIntervals(intervals
            .Select(iv => (start: Clamp(iv.start, from, to), end: Clamp(iv.end, from, to)))
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
