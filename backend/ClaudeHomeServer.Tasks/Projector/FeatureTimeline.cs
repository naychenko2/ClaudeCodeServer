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
//   • notWorking — ВСЕ промежутки между work (в approx «ожидание/простой/ошибка» не различаются,
//     поэтому Waiting*/Idle*/Error* = null; порога «ночной паузы» нет — любой разрыв, в т.ч.
//     > 2 ч, — notWorking, минор 9: обещание отдельного порога убрано, решение зафиксировано);
//   • current — по тревоге фичи (waiting/stopped/merge_pending).
// Параллельные задачи склеиваются в один сегмент на (track, stage).
//
// Режим journal (Б5b): полный журнал (StartsWithCreated && !Truncated) → точные интервалы:
//   • work — по turn (startedAt..endedAt) и по периодам started…(stopped | done | now),
//     разрезанным на waiting-интервалы (work не включает минуты ожидания, major 1);
//   • waiting — waiting…(waiting_cleared | now) + device_wait…(device_wait_cleared | now)
//     (ожидание устройства — как waiting; см. комментарий в BuildJournal);
//   • error — turn.outcome=error (startedAt..endedAt) + stopped (точка);
//   • idle — остаток (не work/waiting/error). Sweep-line: перекрытия решаются приоритетом
//     error > waiting > work (work исключает waiting/error; инвариант work+waiting+error+idle
//     = elapsed, итоги из сегментов, major 2). Заполняются Waiting*/Idle*/Error* (в approx — null).
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
    // internal (не public) — не посторонняя публичная точка настройки (минор 8); тестам —
    // внутренний хук (setter): мутация снимает потолок и проверяет склейку.
    internal const int DefaultMaxSegments = 500;
    internal static int MaxSegments { get; set; } = DefaultMaxSegments;

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
            var stage = FeatureTimeMath.RoleToStage(t.Role);
            var track = stage == "review" ? "review" : "main";
            work.Add(new WorkInterval(start, end, stage, track, t.Id));
        }

        // Сегменты: work (склейка по (track, stage)) + notWorking (промежутки), по времени.
        var segments = BuildSegments(work, from, to);

        // Итоги.
        var elapsed = FeatureTimeMath.Minutes(from, to);
        var workWall = FeatureTimeMath.UnionMinutes(
            work.Select(w => (start: w.Start, end: w.End)).ToList(), from, to);
        var agentsSum = work.Sum(w => FeatureTimeMath.Minutes(w.Start, w.End));
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

    // Интервал журнала: [Start, End] (обрезается к [from, to] при склейке), вид, приоритет,
    // подпись (Stage/TaskId — по задаче хода; минор 7: в журнальном режиме сегменты несут подписи
    // этапов, а не пустые Stage=""/TaskId=null). internal — для теста эквивалентности sweep.
    internal sealed record JournalInterval(
        DateTime Start, DateTime End, string Kind, int Priority, string Stage = "", string? TaskId = null);

    // Приоритеты видов при перекрытии (sweep-line): error > waiting > work; idle — остаток.
    // waiting/error ВЫШЕ work: период started…(stopped|done|now) перекрывает ожидание, и ход с
    // outcome=error даёт и work, и error на том же диапазоне — в пересечении выигрывает более
    // «конкретный» вид, чтобы work не включал минуты ожидания/ошибки (контракт: work исключает
    // waiting и error; инвариант work + waiting + error + idle = elapsed).
    private const int PriorityError = 3;
    private const int PriorityWaiting = 2;
    private const int PriorityWork = 1;

    // Точный режим: события журнала → интервалы work/waiting/error → sweep-line → сегменты
    // (work/waiting/error/idle) + итоги. Чистая функция от (run, journal, now): run даёт
    // taskId→stage (подписи сегментов), journal — события, now — «сейчас».
    //
    // Контракт итогов (Б5b, major 1+2): work исключает waiting и error; инвариант
    // work + waiting + error + idle = elapsed. Итоги считаются ИЗ СЕГМЕНТОВ sweep-line (до
    // склейки по потолку): каждый отрезок [from, to] получает ровно один вид, поэтому сумма
    // видов = elapsed по построению, а не «work-объединение, из которого вычитать error второй
    // раз» (двойной счёт ошибки, который ломал сходимость пирога с графиком).
    private static (List<TimelineSegment> Segments, TimelineTotals Totals) BuildJournal(
        FeatureRun run, FeatureJournalReadResult journal, DateTime from, DateTime to, DateTime now)
    {
        // Индексы событий по taskId (для пар «начало → конец»): stopped, status→Done,
        // waiting_cleared, device_wait_cleared — каждый в порядке At.
        var stoppedByTask = GroupByTask(journal.Entries, "stopped");
        var doneByTask = journal.Entries
            .Where(e => e.Kind == "status" && e.To == "Done")
            .GroupBy(e => e.TaskId ?? "")
            .ToDictionary(g => g.Key, g => g.OrderBy(e => e.At).ToList());
        var waitingClearedByTask = GroupByTask(journal.Entries, "waiting_cleared");
        var deviceWaitClearedByTask = GroupByTask(journal.Entries, "device_wait_cleared");

        // taskId → stage по задаче хода (минор 7): подписи этапов на графике. run даёт задачи
        // фичи (роль → stage); taskId из журнала не в run → "" (без подписи). Track выводится
        // из stage в sweep (review → review, иначе main).
        var stageByTask = AllTasks(run)
            .Where(t => t.Id is not null)
            .GroupBy(t => t.Id!)
            .ToDictionary(g => g.Key, g => FeatureTimeMath.RoleToStage(g.First().Role));
        string LabelOf(string? taskId) =>
            taskId is not null && stageByTask.TryGetValue(taskId, out var s) ? s : "";

        // Промежуток 1: интервалы ожидания (waiting + device_wait) + счётчики. Собираем ДО work:
        // started-период режем на них, чтобы work не включал минуты ожидания (major 1).
        var waitingIntervals = new List<JournalInterval>();
        var waitingCount = 0;
        var longestWait = TimeSpan.Zero;
        foreach (var e in journal.Entries)
        {
            // waiting: ожидание человека waiting…(waiting_cleared | now).
            if (e.Kind == "waiting" && e.At > from)
            {
                var wEnd = FirstAfter(waitingClearedByTask, e.TaskId, e.At) ?? now;
                waitingCount++;
                longestWait = Max(longestWait, wEnd - e.At);
                waitingIntervals.Add(new JournalInterval(e.At, wEnd, "waiting", PriorityWaiting, LabelOf(e.TaskId), e.TaskId));
            }
            // waiting: ожидание устройства device_wait…(device_wait_cleared | now). Контракт
            // TimelineSegment (Б5a) не имеет отдельного под-вида ожидания, поэтому device_wait
            // рисуется как обычный waiting (kind="waiting"); WaitingMinutes — сумма обоих.
            else if (e.Kind == "device_wait" && e.At > from)
            {
                var dEnd = FirstAfter(deviceWaitClearedByTask, e.TaskId, e.At) ?? now;
                waitingCount++;
                longestWait = Max(longestWait, dEnd - e.At);
                waitingIntervals.Add(new JournalInterval(e.At, dEnd, "waiting", PriorityWaiting, LabelOf(e.TaskId), e.TaskId));
            }
        }

        // Промежуток 2: work (ходы + started-периоды, резанные на ожидание) и error.
        var workIntervals = new List<JournalInterval>();
        var errorIntervals = new List<JournalInterval>();
        var errorCount = 0;
        foreach (var e in journal.Entries)
        {
            // work: ход (startedAt..endedAt); если ход завершился ошибкой — и error (счёт).
            if (e.Kind == "turn" && e.StartedAt is not null && e.EndedAt is not null)
            {
                var stage = LabelOf(e.TaskId);
                workIntervals.Add(new JournalInterval(e.StartedAt.Value, e.EndedAt.Value, "work", PriorityWork, stage, e.TaskId));
                if (e.Outcome == "error")
                {
                    errorCount++;
                    errorIntervals.Add(new JournalInterval(e.StartedAt.Value, e.EndedAt.Value, "error", PriorityError, stage, e.TaskId));
                }
            }
            // work: период started…(stopped | done | now), разрезанный на waiting-интервалы.
            else if (e.Kind == "started" && e.StartedAt is not null)
            {
                var end = FirstAfter(stoppedByTask, e.TaskId, e.StartedAt.Value)
                    ?? FirstAfter(doneByTask, e.TaskId, e.StartedAt.Value)
                    ?? now;
                var stage = LabelOf(e.TaskId);
                foreach (var (s, w) in CutByWaiting(e.StartedAt.Value, end, waitingIntervals))
                    workIntervals.Add(new JournalInterval(s, w, "work", PriorityWork, stage, e.TaskId));
            }
            // error: остановка исполнителя — точка (0 мин, но в счёт).
            else if (e.Kind == "stopped")
            {
                errorCount++;
                errorIntervals.Add(new JournalInterval(e.At, e.At, "error", PriorityError, LabelOf(e.TaskId), e.TaskId));
            }
        }

        // Sweep-line: work + waiting + error → сегменты (вид по приоритету, иначе idle).
        var all = new List<JournalInterval>(workIntervals.Count + waitingIntervals.Count + errorIntervals.Count);
        all.AddRange(workIntervals);
        all.AddRange(waitingIntervals);
        all.AddRange(errorIntervals);
        var segments = Sweep(all, from, to);

        // Итоги — из сегментов sweep (до склейки по потолку): каждый отрезок [from, to] даёт ровно
        // один вид, поэтому work + waiting + error + idle = elapsed по построению (major 2).
        var elapsed = FeatureTimeMath.Minutes(from, to);
        var workWall = 0;
        var waitingMin = 0;
        var errorMin = 0;
        var idleMin = 0;
        foreach (var s in segments)
        {
            var m = FeatureTimeMath.Minutes(s.From, s.To);
            switch (s.Kind)
            {
                case "work": workWall += m; break;
                case "waiting": waitingMin += m; break;
                case "error": errorMin += m; break;
                default: idleMin += m; break; // idle
            }
        }
        var notWorking = Math.Max(0, elapsed - workWall);
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

    // Разрез [start, end] на waiting-интервалы: возвращает части [start, end], НЕ покрытые
    // ожиданием (work не включает минуты ожидания, major 1). waiting — уже обрезанные к [from, to]
    // интервалы ожидания; части, целиком внутри ожидания, отбрасываются (нулевая ширина).
    private static List<(DateTime Start, DateTime End)> CutByWaiting(
        DateTime start, DateTime end, List<JournalInterval> waiting)
    {
        // Пересекающие [start, end], обрезанные к нему (start/end — именованные).
        var cuts = new List<(DateTime start, DateTime end)>();
        foreach (var w in waiting)
        {
            if (w.End <= start || w.Start >= end) continue; // не пересекает
            var s = w.Start < start ? start : w.Start;
            var e = w.End > end ? end : w.End;
            if (e > s) cuts.Add((s, e));
        }
        if (cuts.Count == 0) return new List<(DateTime, DateTime)> { (start, end) };

        // Склеить перекрывающиеся (общий FeatureTimeMath.MergeIntervals) → зазоры = [start, end]
        // минус склеенные: один проход O(k) (вместо наивного O(W·P) «режь все части каждым waiting»,
        // который взрывал замер на 20k: 5000 started × 5000 waiting).
        var merged = FeatureTimeMath.MergeIntervals(cuts);
        var parts = new List<(DateTime, DateTime)>();
        var cursor = start;
        foreach (var (s, e) in merged)
        {
            if (s > cursor) parts.Add((cursor, s));
            if (e > cursor) cursor = e;
        }
        if (cursor < end) parts.Add((cursor, end));
        return parts;
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

    // Sweep-line (линейный, major 3): все границы (from, to + start/end интервалов, обрезанные к
    // [from, to]) → отрезки; на каждом отрезке [s, e] — вид по приоритету АКТИВНЫХ интервалов
    // (error > waiting > work), иначе idle. Активные по виду — счётчики (O(1) на границу:
    // ++/-- при старте/конце), вид отрезка — по счётчикам; подпись — от представителя вида.
    // Итого O(n log n) на сортировку границ + O(n) на проход (вместо O(n²) «скан на границу»).
    //
    // Интервал [a, b] активен на отрезке [p, next_p] ⇔ a ≤ p И b > p (b — граница, поэтому
    // b ≥ next_p ⇔ b > p). Приоритет — по виду: выигрывает самый «конкретный» активный вид.
    // Track/stage: work — из taskId (стадия задачи), иначе main/""; taskId — у одиночного work.
    // internal — для теста эквивалентности (новый линейный sweep == старый O(n²) на случайных входах).
    internal static List<TimelineSegment> Sweep(List<JournalInterval> intervals, DateTime from, DateTime to)
    {
        // Границы: from, to + обрезанные start/end всех интервалов.
        var points = new SortedSet<DateTime> { from, to };
        foreach (var iv in intervals)
        {
            points.Add(FeatureTimeMath.Clamp(iv.Start, from, to));
            points.Add(FeatureTimeMath.Clamp(iv.End, from, to));
        }
        var list = points.ToList();

        // События на границах: на точке t — интервалы, кончащиеся в t (снять) и начинающиеся (добавить).
        var endsAt = new Dictionary<DateTime, List<JournalInterval>>();
        var startsAt = new Dictionary<DateTime, List<JournalInterval>>();
        foreach (var iv in intervals)
        {
            var s = FeatureTimeMath.Clamp(iv.Start, from, to);
            var e = FeatureTimeMath.Clamp(iv.End, from, to);
            if (e <= s) continue; // нулевая/обратная ширина после обрезки — не покрывает
            if (!startsAt.TryGetValue(s, out var st)) startsAt[s] = st = new List<JournalInterval>();
            st.Add(iv);
            if (!endsAt.TryGetValue(e, out var en)) endsAt[e] = en = new List<JournalInterval>();
            en.Add(iv);
        }

        // Активные по виду: [work, waiting, error]. СЧЁТЧИКИ (O(1) на границу, major 3) +
        // стек-представитель (последний активный интервал вида — подпись, минор 7) + ended
        // (ленивое очищение вершины стека). KindIndex: work=0, waiting=1, error=2.
        var active = new int[3];
        var stack = new Stack<JournalInterval>[3];
        var ended = new HashSet<JournalInterval>[3];
        for (var k = 0; k < 3; k++) { stack[k] = new Stack<JournalInterval>(); ended[k] = new HashSet<JournalInterval>(); }
        int KindIndex(string kind) => kind switch
        {
            "work" => 0,
            "waiting" => 1,
            "error" => 2,
            _ => -1,
        };

        var segments = new List<TimelineSegment>();
        for (var i = 0; i + 1 < list.Count; i++)
        {
            var s = list[i];
            var e = list[i + 1];
            if (e <= s) continue;

            // На границе s: снимаем кончающиеся в s (b == s → b > s ложь → не активны),
            // добавляем начинающиеся (a == s → активны).
            if (endsAt.TryGetValue(s, out var ends))
                foreach (var iv in ends)
                {
                    var k = KindIndex(iv.Kind);
                    if (k < 0) continue;
                    active[k]--;
                    ended[k].Add(iv);
                }
            if (startsAt.TryGetValue(s, out var starts))
                foreach (var iv in starts)
                {
                    var k = KindIndex(iv.Kind);
                    if (k < 0) continue;
                    active[k]++;
                    stack[k].Push(iv);
                }

            // Вид отрезка: по счётчикам (error > waiting > work), иначе idle.
            var kind = active[2] > 0 ? "error" : active[1] > 0 ? "waiting" : active[0] > 0 ? "work" : "idle";

            // Подпись (Stage/TaskId/Track) — от представителя победившего вида (минор 7):
            // вершина стека после ленивого очищения завершённых (амортизировано O(1)).
            string stage = "";
            string? taskId = null;
            if (kind != "idle")
            {
                var k = KindIndex(kind);
                while (stack[k].TryPeek(out var top) && ended[k].Contains(top)) stack[k].Pop();
                if (stack[k].TryPeek(out var rep))
                {
                    stage = rep.Stage;
                    taskId = rep.TaskId;
                }
            }
            var track = stage == "review" ? "review" : "main";
            segments.Add(new TimelineSegment(s, e, kind, track, stage, taskId));
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
            foreach (var (s, e) in FeatureTimeMath.MergeIntervals(intervals.Select(w => (start: w.Start, end: w.End)).ToList()))
            {
                // Одиночный (не склеенный) интервал несёт taskId; склеенный — null.
                var single = intervals.Count == 1 ? intervals[0].TaskId : null;
                segments.Add(new TimelineSegment(s, e, "work", track, stage, single));
            }
        }

        // 2) notWorking: промежутки между объединением ВСЕХ work-интервалов (на [from, to]).
        var union = FeatureTimeMath.MergeIntervals(work.Select(w => (start: FeatureTimeMath.Clamp(w.Start, from, to), end: FeatureTimeMath.Clamp(w.End, from, to))).ToList());
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

    // Work-интервал одной задачи: [Start, End], стадия, трек, id задачи.
    // (Объединение/минуты/обрезка — в общем FeatureTimeMath, дедуп с FeatureBreakdown, минор 8.)
    private sealed record WorkInterval(DateTime Start, DateTime End, string Stage, string Track, string TaskId);
}
