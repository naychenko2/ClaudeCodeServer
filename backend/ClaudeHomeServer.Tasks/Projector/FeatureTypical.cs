using System.Collections.Concurrent;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.Tasks.Journal;

namespace ClaudeHomeServer.Services.Tasks.Projector;

// ─── ST-B1: «Типичная фича проекта» (stats.typical, волна 5 «Статистика») ──────────────────────
// Шкала сравнения героя варианта A «Пульс» (docs/mockups/feature-stats-5.md, раздел «Нужны
// данные»): медиана и p25–p75 длительности, времени ожидания человека и числа возвратов по
// СМЕРЖЕННЫМ фичам ТОГО ЖЕ проекта и ТОГО ЖЕ владельца. Текущая фича из выборки исключена;
// смерженных меньше трёх — null (шкала бессмысленна).
//
// Источник чисел — уже посчитанные: длительность и возвраты берутся из FeatureSummary /
// FeatureVerdicts — той же чистой проекции, что у индекса (FeatureProjector.Build), без I/O.
// Ожидание известно только из полного журнала (прямой скан эпизодов waiting, без таймлайна):
// поля задач прошлых эпизодов ожидания не хранят — AwaitingHumanSince снимается при
// перезапуске/закрытии задачи (TaskManager.ClearAwaitingHuman). Поэтому фича без полного
// журнала в выборку ожидания не попадает (длительность и возвраты — считаются); фич с
// журналом меньше трёх — метрика ожидания null, её размер — waitingCount.
//
// Квантили — линейная интерполяция (тип 7, как numpy): pos = q·(n−1), между соседними.
// Для n = 3: p25 = (v0+v1)/2, p75 = (v1+v2)/2 — детерминировано, проверяется на фикстуре.
//
// Стоимость (хвост вердикта Глеба по b684cff84): выборка проекта строится один раз на
// ревизию задач ЭТОГО проекта (число эпиков проекта + сумма UpdatedAt их задач; активность
// в другом проект кэш не сбрасывает). Кэш — память процесса, ключ (ownerId, projectId):
// выборка хранится полная, исключение текущей фичы — по запросу (фильтр + квантили — дёшево).
// После первого запроса на той же ревизии: ни FeatureProjector.Build, ни чтения журналов —
// только квантили сохранённой выборки. Ключи явно не чистятся при удалении проекта:
// потолок числа ключей (MaxCachedKeys) ограничивает рост памяти — сброс всего словаря
// дешевле учёта жизненного цикла каждого проекта.
public static class FeatureTypical
{
    // Квантили одной метрики: медиана и p25–p75.
    public sealed record TypicalPercentiles(double Median, double P25, double P75);

    // stats.typical: размер выборки, размер выборки ожидания и три метрики.
    public sealed record TypicalFeatureStats(
        // Число смерженных фич в выборке (текущая не считается).
        int Count,
        // Из них — фич с полным журналом: размер выборки ожидания.
        int WaitingCount,
        // Длительность (минуты).
        TypicalPercentiles Duration,
        // Ожидание человека (минуты); null — меньше трёх смерженных с полным журналом.
        TypicalPercentiles? Waiting,
        // Число возвратов (Loops).
        TypicalPercentiles Loops);

    // Выборка одной смерженной фичи: id, длительность (мин), ожидание (мин, null — неизвестно),
    // возвраты.
    private sealed record Sample(string FeatureId, double DurationMinutes, int? WaitingMinutes, int Loops);

    // Кэш: ключ "{ownerId}|{projectId или ''}" → (ревизия, выборка). Ревизия — только задачи
    // этого проекта: число эпиков проекта + сумма UpdatedAt их задач. Любая мутация стора,
    // касающаяся проекта, делает выборку неактуальной; чужие проекты — нет.
    private static readonly ConcurrentDictionary<string, (long Revision, List<Sample> Samples)> _cache = new();
    private const int MaxCachedKeys = 256;

    // owner — задачи владельца (изоляция по владельцу здесь же: фильтр по OwnerId),
    // projectId — проект текущей фичы (null — фича без проекта),
    // excludeFeatureId — id текущего эпика (в выборку не входит),
    // readJournal — null, если подсистема журнала выключена (тогда ожидание — null).
    public static TypicalFeatureStats? Build(
        IReadOnlyList<TaskItem> owner,
        string ownerId,
        string? projectId,
        string excludeFeatureId,
        Func<string, FeatureJournalReadResult?>? readJournal,
        DateTime now)
    {
        ArgumentNullException.ThrowIfNull(owner);

        // Один проход по задачам: индекс featureId → задачи (дешевле, чем O(фичи × задачи)
        // в цикле выборки) и эпики проекта. Задачи фичи берутся по FeatureId без фильтра по
        // владельцу — как раньше: вызывающий передаёт только задачи этого владельца.
        var epics = new List<TaskItem>();
        var tasksByFeature = new Dictionary<string, List<TaskItem>>();
        foreach (var t in owner)
        {
            if (t.Role == TaskRoles.Feature)
            {
                if (t.OwnerId == ownerId && t.ProjectId == projectId) epics.Add(t);
            }
            else if (t.FeatureId is not null)
            {
                if (!tasksByFeature.TryGetValue(t.FeatureId, out var list))
                    tasksByFeature[t.FeatureId] = list = new List<TaskItem>();
                list.Add(t);
            }
        }

        // Ревизия — по задачам проекта (эпики проекта и их задачи): любая мутация стора,
        // касающаяся этого проекта, её двигает; активность в другом проекте — нет.
        long revision = epics.Count;
        foreach (var e in epics)
        {
            unchecked { revision += e.UpdatedAt.Ticks + e.Id.Length; }
            if (tasksByFeature.TryGetValue(e.Id, out var sub))
                foreach (var t in sub)
                    unchecked { revision += t.UpdatedAt.Ticks + t.Id.Length; }
        }

        var key = $"{ownerId}|{projectId ?? ""}";
        List<Sample> samples;
        if (_cache.TryGetValue(key, out var hit) && hit.Revision == revision)
        {
            samples = hit.Samples;
        }
        else
        {
            samples = CollectSamples(epics, tasksByFeature, readJournal, now);
            if (_cache.Count >= MaxCachedKeys) _cache.Clear();
            _cache[key] = (revision, samples);
        }

        // Текущая фича — вне выборки (она и не «типичная»).
        var chosen = samples.Where(s => s.FeatureId != excludeFeatureId).ToList();
        if (chosen.Count < 3) return null;

        var waits = chosen.Where(s => s.WaitingMinutes is not null)
            .Select(s => (double)s.WaitingMinutes!.Value).ToList();

        return new TypicalFeatureStats(
            chosen.Count,
            waits.Count,
            Percentiles(chosen.Select(s => s.DurationMinutes).ToList()),
            waits.Count >= 3 ? Percentiles(waits) : null,
            Percentiles(chosen.Select(s => (double)s.Loops).ToList()));
    }

    // Выборка: все смерженные фичи проекта и владельца (включая текущую — исключение
    // точечное). Смерженная = State == merged: «честное» закрытие без мержа — не мерж.
    private static List<Sample> CollectSamples(
        List<TaskItem> epics, Dictionary<string, List<TaskItem>> tasksByFeature,
        Func<string, FeatureJournalReadResult?>? readJournal, DateTime now)
    {
        var result = new List<Sample>();
        foreach (var epic in epics)
        {
            var featureTasks = new List<TaskItem> { epic };
            if (tasksByFeature.TryGetValue(epic.Id, out var sub)) featureTasks.AddRange(sub);

            var run = FeatureProjector.Build(epic, featureTasks, now);
            if (run.State != FeatureStates.Merged) continue;
            var summary = run.Summary;
            if (summary is null) continue;

            int? waiting = null;
            if (readJournal is not null)
            {
                var journal = readJournal(epic.Id);
                if (journal is { StartsWithCreated: true, Truncated: false })
                    // Дешёвый путь: прямой скан эпизодов ожидания, без полного sweep-line
                    // таймлайна. Окно закрытой фичи — [создание эпика, мерж].
                    waiting = HumanWaitingMinutes(journal, epic.CreatedAt, summary.MergedAt ?? now);
            }

            result.Add(new Sample(epic.Id, summary.DurationMinutes, waiting, run.Verdicts.Loops));
        }
        return result;
    }

    // Минуты ожидания человека в окне [from, to): эпизоды waiting…(waiting_cleared).
    // Хвост вердикта Глеба: «ожидание» в таймлайне (FeatureTimeline.cs:357-360) включает и
    // device_wait — ожидание устройства моделью; здесь выбрано только человеческое ожидание
    // (метрика «сколько ждал человек»), поле назван честно. Незакрытый эпизод обрезается
    // концом окна: для смерженной фичи окно закрыто (то = mergedAt ≤ now), поэтому тот же
    // результат, что обрезка «до now» в таймлайне.
    private static int HumanWaitingMinutes(FeatureJournalReadResult journal, DateTime from, DateTime to)
    {
        var clearedByTask = new Dictionary<string, List<DateTime>>();
        foreach (var e in journal.Entries)
        {
            if (e.Kind != "waiting_cleared" || e.TaskId is null) continue;
            if (!clearedByTask.TryGetValue(e.TaskId, out var list))
                clearedByTask[e.TaskId] = list = new List<DateTime>();
            list.Add(e.At);
        }

        int total = 0;
        foreach (var e in journal.Entries)
        {
            if (e.Kind != "waiting" || e.At < from || e.At >= to) continue;
            var end = to;
            if (e.TaskId is not null && clearedByTask.TryGetValue(e.TaskId, out var cleared))
                foreach (var c in cleared)
                    if (c > e.At) { end = c; break; }
            if (end > to) end = to;
            var span = end - e.At;
            if (span > TimeSpan.Zero) total += (int)span.TotalMinutes;
        }
        return total;
    }

    // Квантили непустой выборки: медиана и p25–p75 линейной интерполяцией.
    private static TypicalPercentiles Percentiles(List<double> values)
    {
        var sorted = values.OrderBy(v => v).ToList();
        return new TypicalPercentiles(Pct(sorted, 0.5), Pct(sorted, 0.25), Pct(sorted, 0.75));
    }

    private static double Pct(List<double> sorted, double q)
    {
        double pos = q * (sorted.Count - 1);
        int lo = (int)Math.Floor(pos);
        int hi = Math.Min(lo + 1, sorted.Count - 1);
        return sorted[lo] + (sorted[hi] - sorted[lo]) * (pos - lo);
    }
}
