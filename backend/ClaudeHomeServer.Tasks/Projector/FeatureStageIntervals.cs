using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.Tasks.Journal;

namespace ClaudeHomeServer.Services.Tasks.Projector;

// Интервалы этапов фичи из журнала ходов (GC-B1, «Хронология»/Гант): чистая функция
// (run, journal, now) → стадия → её интервалы работы. Отличие от FeatureTimeline в том,
// что группировка — по стадиям, а не по видам: интервал этапа = склеенные ходы задач этой
// стадии (зазор ≤ GlueGap — часть работы этапа) + живой хвост идущей задачи (Live, до now).
// Режим журнала — тот же гейт, что у FeatureTimeline (FeatureTimeline.cs:212): журнала нет /
// он не начинается с created / Truncated → null. Приближённого режима здесь нет: точные
// интервалы считаются только по полному журналу.
//
// Типы результата — публичные StageInterval/StageTurn (StageIntervals.cs, контракт
// stats.timeline), сама функция — internal вертикали.
internal static class FeatureStageIntervals
{
    // Зазор склейки ходов одной стадии (GC-B1): следующий ход начался не позже GlueGap после
    // конца предыдущего — это один интервал (зазор — работа этапа, не простой).
    internal static readonly TimeSpan GlueGap = TimeSpan.FromMinutes(2);

    // Исходы, которыми интервал «заканчивается» (LastOutcome): этап оборвался на них.
    private static readonly HashSet<string> BadOutcomes = ["cancelled", "interrupted", "error"];

    // Промежуток до склейки: закрытый ход или живой хвост (Turn null у хвоста).
    private sealed record Raw(DateTime From, DateTime To, string TaskId, bool Live, StageTurn? Turn);

    // Накопление интервала при склейке (GC-B1): изменяемый рабочий буфер — ходы и id задач
    // дописываются в один список (Add), StageInterval материализуется один раз после прохода.
    // Прежняя запись через `with { Turns = [..last.Turns, r.Turn] }` копировала весь
    // накопленный список на каждой склейке — O(n²) (18k ходов → 18 с).
    private sealed class Accum
    {
        public DateTime From;
        public DateTime To;
        public bool Live;
        public readonly List<string> TaskIds = [];
        public readonly List<StageTurn> Turns = [];
    }

    /// <summary>
    /// Стадия → её интервалы (по From). Пустой словарь — в журнале нет ни одного хода стадии.
    /// null — журнал не полный (тот же гейт, что режим journal в FeatureTimeline).
    /// </summary>
    internal static Dictionary<string, List<StageInterval>>? Build(
        FeatureRun run, FeatureJournalReadResult? journal, DateTime now)
    {
        ArgumentNullException.ThrowIfNull(run);

        // Гейт режима журнала — тот же, что FeatureTimeline.Build (:212).
        if (journal is not { StartsWithCreated: true, Truncated: false }) return null;

        // taskId → стадия: по Role задачи (та же таблица RoleToStage, что подписи сегментов
        // в FeatureTimeline.BuildJournal :316). Ход задачи вне run (в т.ч. безролевой) стадии
        // не имеет — пропускается: честной attributции для него нет.
        var stageByTask = new Dictionary<string, string>();
        foreach (var t in AllTasks(run))
            if (t.Id is not null) stageByTask[t.Id] = FeatureTimeMath.RoleToStage(t.Role);

        // Промежутки по стадиям: закрытые ходы + (ниже) живые хвосты.
        var raw = new Dictionary<string, List<Raw>>();
        // Конец последнего закрытого хода задачи — старт живого хвоста той же задачи.
        var lastClosedEnd = new Dictionary<string, DateTime>();

        foreach (var e in journal.Entries)
        {
            if (e.Kind != "turn" || e.StartedAt is null || e.EndedAt is null) continue;
            if (e.TaskId is null || !stageByTask.TryGetValue(e.TaskId, out var stage)) continue;
            var from = e.StartedAt.Value;
            var to = e.EndedAt.Value;
            if (to < from) continue; // битый ход (конец раньше начала) — не интервал
            if (!raw.TryGetValue(stage, out var list)) raw[stage] = list = [];
            list.Add(new Raw(from, to, e.TaskId, false,
                new StageTurn(e.TaskId, from, to, e.Outcome, e.Model)));
            if (!lastClosedEnd.TryGetValue(e.TaskId, out var prev) || to > prev)
                lastClosedEnd[e.TaskId] = to;
        }

        // Живой хвост (GC-B1): для каждой задачи run.Now — [max(ClaudeStartedAt, конец
        // последнего закрытого хода ЭТОЙ задачи), now]. max нужен, чтобы хвост не начинался
        // с самого запуска задачи, уже покрытого её закрытыми ходами. Задача без старта и без
        // закрытых ходов, а также хвост нулевой длины — не интервал.
        foreach (var n in run.Now)
        {
            if (n.TaskId is not { } taskId || !stageByTask.TryGetValue(taskId, out var stage)) continue;
            DateTime? start = n.StartedAt;
            if (lastClosedEnd.TryGetValue(taskId, out var closedEnd) && (start is null || closedEnd > start))
                start = closedEnd;
            if (start is null || start >= now) continue;
            if (!raw.TryGetValue(stage, out var list)) raw[stage] = list = [];
            list.Add(new Raw(start.Value, now, taskId, true, null));
        }

        // Склейка по стадиям: промежутки по From; следующий начался не позже GlueGap после
        // конца текущего — один интервал (границы расширяются, Live — если любой части живой).
        var result = new Dictionary<string, List<StageInterval>>();
        foreach (var (stage, list) in raw)
        {
            var merged = new List<Accum>();
            foreach (var r in list.OrderBy(r => r.From))
            {
                var last = merged.Count > 0 ? merged[^1] : null;
                if (last is not null && r.From <= last.To + GlueGap)
                {
                    if (r.To > last.To) last.To = r.To;
                    last.Live |= r.Live;
                    if (!last.TaskIds.Contains(r.TaskId)) last.TaskIds.Add(r.TaskId);
                    if (r.Turn is not null) last.Turns.Add(r.Turn);
                }
                else
                {
                    var a = new Accum { From = r.From, To = r.To, Live = r.Live };
                    a.TaskIds.Add(r.TaskId);
                    if (r.Turn is not null) a.Turns.Add(r.Turn);
                    merged.Add(a);
                }
            }

            result[stage] = merged.Select(a =>
            {
                // LastOutcome — исход последнего хода (Turns уже в порядке From); живой
                // хвост ходом не считается — он ничего не «заканчивает».
                var lastOutcome = a.Turns.Count > 0 && BadOutcomes.Contains(a.Turns[^1].Outcome ?? "")
                    ? a.Turns[^1].Outcome
                    : null;
                return new StageInterval(a.From, a.To, FeatureTimeMath.Minutes(a.From, a.To),
                    a.Live, lastOutcome, a.TaskIds, a.Turns);
            }).ToList();
        }

        return result;
    }

    // Все задачи run (тот же обход, что AllTasks в FeatureTimeline:659): эпик + дочерние
    // по ролям. Безролевые (run.Unroled) не включаются — их ходам нет честной стадии.
    private static IEnumerable<TaskItem> AllTasks(FeatureRun run)
    {
        yield return run.Epic;
        foreach (var t in run.Steps) yield return t;
        foreach (var t in run.Reviews) yield return t;
        foreach (var t in run.Qa) yield return t;
        if (run.Final is not null) yield return run.Final;
        if (run.Merge is not null) yield return run.Merge;
        foreach (var t in run.Cut) yield return t;
        foreach (var t in run.Design) yield return t;
        foreach (var t in run.Plan) yield return t;
    }
}
