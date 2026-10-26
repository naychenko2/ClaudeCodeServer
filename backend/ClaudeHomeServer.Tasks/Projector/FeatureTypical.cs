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
// B2a (волна 6 «Обзор фич», разрез п.3): та же выборка и тот же кэш расширяются двумя
// величинами для оценки времени (FeatureBrief.estimate): stepMinutes — медиана по фичам от
// средней длины принятых цепочек шагов одной фичи; tailMinutes — медиана хвоста тех же фич:
// от принятия последнего шага до мержа. Длительности берутся из run.StepChains, которые
// FeatureProjector уже посчитал для этой же фичи (FeatureRun.cs:54), — своего прохода по
// журналам и нового хранилища нет. Считаются только принятые цепочки (AcceptedAt ненулевой
// только у них — FeatureStepAccounting.cs:232): снятый шаг вне знаменателя (FeatureStepProgress),
// у непринятого шага нет честной длительности «до принятия». Фича без принятых шагов в шаговую
// выборку не входит (её длительность и возвраты — считаются); таких фич меньше трёх — обе
// метрики null (то же правило, что у ожидания).
//
// Стоимость (хвосты вердиктов Глеба b684cff84 и a4d19b98f): кэш — по фиче, а не по проекту:
// ключ (ownerId, featureId, наличие журнала) → (ревизия этой фичи, её выборка). Ревизия
// фичи — UpdatedAt её эпика и его задач: у смерженной фичи эти таймстемпы после мержа не
// двигаются, её выборка живёт в кэше долго; активность живой фичи в том же проекте
// пересчитывает только её собственную выборку (один FeatureProjector.Build), без чтений
// чужих журналов. Агрегат (фильтр + квантили) собирается из кэша фич на каждый запрос — дёшево.
// Ключ содержит наличие журнала: выборка, посчитанная без журнала (waiting = null), не должна
// обслуживать запрос с журналом, и наоборот.
// Выборки, у которых длительность или окно ожидания зависит от `now` (MergedAt == null),
// в кэш не кладутся: они застыли бы до следующей мутации задач (п.4 хвостов).
// Потолок числа ключей (MaxCachedKeys) ограничивает рост памяти: при переполнении
// выселяется наименее недавно использованный ключ, а не весь словарь — один шумный
// владелец не обнуляет кэш всем.
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
        TypicalPercentiles Loops,
        // B2a: размер шаговой выборки — смерженных фич с принятыми шагами (текущая не считается).
        int StepCount,
        // Типичные минуты на шаг (B2a): медиана по фичам от средней длины их принятых цепочек;
        // null — фич с шагами меньше трёх.
        TypicalPercentiles? StepMinutes,
        // Типичные минуты хвоста (B2a): от принятия последнего шага до мержа (qa + final + merge);
        // null — фич с принятыми шагами и мержем меньше трёх.
        TypicalPercentiles? TailMinutes);

    // Выборка одной смерженной фичи: id, длительность (мин), ожидание (мин, null — неизвестно),
    // возвраты, средняя минуты на принятый шаг и минуты хвоста (B2a; null — принятых шагов нет
    // или мерж без даты — пара вычисляется только вместе, см. BuildSample).
    private sealed record Sample(
        string FeatureId, double DurationMinutes, int? WaitingMinutes, int Loops,
        double? StepMinutes, double? TailMinutes);

    // Запись кэша: ревизия фичи, её выборка (null — фича не смержена), тик последнего
    // обращения (для выселения при переполнении).
    private record Entry(long Revision, Sample? Sample, long LastUsedTicks);

    // Кэш: ключ "{ownerId}|{featureId}|{"j" если журнал доступен, иначе "-"}" → запись.
    private static readonly ConcurrentDictionary<string, Entry> _cache = new();
    private const int MaxCachedKeys = 256;

    // Для тестов: обнулить кэш — каждый тест начинается с чистого, числа не текутся между
    // тестами через статический словарь процесса.
    public static void ClearCache() => _cache.Clear();

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

        // Наличие журнала — часть ключа: выборки с ожиданием и без него — разные числа.
        var journalFlag = readJournal is null ? "-" : "j";
        var samples = new List<Sample>(epics.Count);
        foreach (var epic in epics)
        {
            var featureTasks = new List<TaskItem> { epic };
            if (tasksByFeature.TryGetValue(epic.Id, out var sub)) featureTasks.AddRange(sub);

            // Ревизия фичи: UpdatedAt её эпика и его задач. Любая мутация, касающаяся
            // фичи, её двигает; активность других фич — нет.
            long revision = epic.Id.Length;
            unchecked { revision += epic.UpdatedAt.Ticks; }
            if (sub is not null)
                foreach (var t in sub)
                    unchecked { revision += t.UpdatedAt.Ticks + t.Id.Length; }

            var key = $"{ownerId}|{epic.Id}|{journalFlag}";
            if (_cache.TryGetValue(key, out var hit) && hit.Revision == revision)
            {
                _cache[key] = hit with { LastUsedTicks = now.Ticks };
                if (hit.Sample is not null) samples.Add(hit.Sample);
                continue;
            }

            var (sample, cacheable) = BuildSample(epic, featureTasks, readJournal, now);
            if (sample is not null) samples.Add(sample);
            if (cacheable)
            {
                if (_cache.Count >= MaxCachedKeys) EvictOldest();
                _cache[key] = new Entry(revision, sample, now.Ticks);
            }
        }

        // Текущая фича — вне выборки (она и не «типичная»).
        var chosen = samples.Where(s => s.FeatureId != excludeFeatureId).ToList();
        if (chosen.Count < 3) return null;

        var waits = chosen.Where(s => s.WaitingMinutes is not null)
            .Select(s => (double)s.WaitingMinutes!.Value).ToList();
        // B2a: шаговая и хвостовая выборки — фичи, у которых эти величины посчитаны
        // (BuildSample считает их только вместе); порог тот же, что у ожидания: <3 → null.
        var steps = chosen.Where(s => s.StepMinutes is not null)
            .Select(s => s.StepMinutes!.Value).ToList();
        var tails = chosen.Where(s => s.TailMinutes is not null)
            .Select(s => s.TailMinutes!.Value).ToList();

        return new TypicalFeatureStats(
            chosen.Count,
            waits.Count,
            Percentiles(chosen.Select(s => s.DurationMinutes).ToList()),
            waits.Count >= 3 ? Percentiles(waits) : null,
            Percentiles(chosen.Select(s => (double)s.Loops).ToList()),
            steps.Count,
            steps.Count >= 3 ? Percentiles(steps) : null,
            tails.Count >= 3 ? Percentiles(tails) : null);
    }

    // Выселение при переполнении: наименее недавно использованный ключ — не весь словарь.
    private static void EvictOldest()
    {
        string? victim = null;
        long oldest = long.MaxValue;
        foreach (var (key, entry) in _cache)
            if (entry.LastUsedTicks < oldest)
            {
                oldest = entry.LastUsedTicks;
                victim = key;
            }
        if (victim is not null) _cache.TryRemove(victim, out _);
    }

    // Выборка одной фичи: null — не смерженная (в выборку входят только смерженные:
    // «честное» закрытие без мержа — не мерж). cacheable = false, если длительность или
    // окно ожидания зависит от `now` (MergedAt == null — мерж-задача без CompletedAt):
    // такая выборка считается на каждый запрос, а не застывает в кэше (п.4 хвостов).
    private static (Sample? Sample, bool Cacheable) BuildSample(
        TaskItem epic, List<TaskItem> featureTasks,
        Func<string, FeatureJournalReadResult?>? readJournal, DateTime now)
    {
        var run = FeatureProjector.Build(epic, featureTasks, now);
        if (run.State != FeatureStates.Merged) return (null, true);
        var summary = run.Summary;
        if (summary is null) return (null, true);

        int? waiting = null;
        if (readJournal is not null)
        {
            var journal = readJournal(epic.Id);
            if (journal is { StartsWithCreated: true, Truncated: false })
                // Дешёвый путь: прямой скан эпизодов ожидания, без полного sweep-line
                // таймлайна. Окно закрытой фичи — [создание эпика, мерж].
                waiting = HumanWaitingMinutes(journal, epic.CreatedAt, summary.MergedAt ?? now);
        }

        // B2a (разрез п.3): шаг и хвост — с уже посчитанного этого же run, без нового прохода:
        // цепочки и их длительности (FeatureStepAccounting.cs:254) посчитаны FeatureProjector'ом
        // выше. Считаются только принятые цепочки: AcceptedAt ненулевой ровно у них
        // (FeatureStepAccounting.cs:232) — снятый шаг вне знаменателя (FeatureStepProgress),
        // у непринятого нет честной длительности «до принятия». Пара step/tail вычисляется только
        // вместе и только при известном MergedAt: без мержа нет хвоста, и такая выборка в кэш
        // не кладётся в любом случае (п.4). stepMinutes — среднее по цепочкам фичи (без
        // округления: среднее целых длительностей), tailMinutes — от последней AcceptedAt до
        // мержа, округление одно по TimeSpan (как DurationMinutes в FeatureStepAccounting).
        double? stepMinutes = null, tailMinutes = null;
        var acceptedChains = run.StepChains.Where(c => c.AcceptedAt is not null).ToList();
        if (acceptedChains.Count > 0 && summary.MergedAt is not null)
        {
            stepMinutes = acceptedChains.Sum(c => c.DurationMinutes) / acceptedChains.Count;
            var lastAccepted = acceptedChains.Max(c => c.AcceptedAt!.Value);
            tailMinutes = Math.Max(0, Math.Round((summary.MergedAt.Value - lastAccepted).TotalMinutes));
        }

        return (new Sample(epic.Id, summary.DurationMinutes, waiting, run.Verdicts.Loops,
                stepMinutes, tailMinutes),
            summary.MergedAt is not null);
    }

    // Минуты ожидания человека в окне [from, to): эпизоды waiting…(waiting_cleared).
    // Семантика — правило таймлайна (п.2 хвостов вердикта ST-R-B1): пересечения эпизодов
    // объединяются (sweep), округление — одно по суммарному TimeSpan (FeatureTimeMath.
    // UnionMinutes), а не сумма floor по эпизодам: параллельное ожидание двух задач одной
    // фичи считается один раз, подминутные эпизоды не обнуляются. Единственная намеренная
    // разница с stats.timeline.waitingMinutes — device_wait: здесь только человеческое
    // ожидание (метрика «сколько ждал человек»), согласовано с прошлым вердиктом.
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

        var intervals = new List<(DateTime Start, DateTime End)>();
        foreach (var e in journal.Entries)
        {
            // Считаются только эпизоды, начавшиеся внутри окна [from, to) — то же правило,
            // что в FeatureTimeline.cs:338.
            if (e.Kind != "waiting" || e.At < from || e.At >= to) continue;
            var end = to;
            if (e.TaskId is not null && clearedByTask.TryGetValue(e.TaskId, out var cleared))
                foreach (var c in cleared)
                    if (c > e.At) { end = c; break; }
            if (end > to) end = to;
            if (end > e.At) intervals.Add((e.At, end));
        }
        return FeatureTimeMath.UnionMinutes(intervals, from, to);
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
