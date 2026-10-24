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
// Ожидание известно только из полного журнала (итоги FeatureTimeline): поля задач прошлых
// эпизодов ожидания не хранят — AwaitingHumanSince снимается при перезапуске/закрытии задачи
// (TaskManager.ClearAwaitingHuman). Поэтому фича без полного журнала в выборку ожидания
// не попадает (длительность и возвраты — считаются); журналов меньше трёх — метрика
// ожидания null.
//
// Квантили — линейная интерполяция (тип 7, как numpy): pos = q·(n−1), между соседними.
// Для n = 3: p25 = (v0+v1)/2, p75 = (v1+v2)/2 — детерминировано, проверяется на фикстуре.
//
// Стоимость: выборка проекта строится один раз на ревизию данных (число задач + сумма
// UpdatedAt: любая мутация стора её двигает; эпизоды ожидания пишутся только на переходах
// задач, которые UpdatedAt двигают). Кэш — память процесса, ключ (ownerId, projectId):
// выборка хранится полная, исключение текущей фичы — по запросу (фильтр + квантили — дёшево).
// После первого запроса на той же ревизии: ни построения run, ни чтения журналов — только
// квантили сохранённой выборки.
public static class FeatureTypical
{
    // Квантили одной метрики: медиана и p25–p75.
    public sealed record TypicalPercentiles(double Median, double P25, double P75);

    // stats.typical: размер выборки и три метрики.
    public sealed record TypicalFeatureStats(
        // Число смерженных фич в выборке (текущая не считается).
        int Count,
        // Длительность (минуты).
        TypicalPercentiles Duration,
        // Ожидание человека (минуты); null — меньше трёх смерженных с полным журналом.
        TypicalPercentiles? Waiting,
        // Число возвратов (Loops).
        TypicalPercentiles Loops);

    // Выборка одной смерженной фичи: id, длительность (мин), ожидание (мин, null — неизвестно),
    // возвраты.
    private sealed record Sample(string FeatureId, double DurationMinutes, int? WaitingMinutes, int Loops);

    // Кэш: ключ "{ownerId}|{projectId или ''}" → (ревизия, выборка). Ревизия — число задач +
    // сумма UpdatedAt: любое изменение стора делает выборку неактуальной.
    private static readonly ConcurrentDictionary<string, (long Revision, List<Sample> Samples)> _cache = new();

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

        long revision = owner.Count;
        foreach (var t in owner)
            unchecked { revision += t.UpdatedAt.Ticks + t.Id.Length; }

        var key = $"{ownerId}|{projectId ?? ""}";
        List<Sample> samples;
        if (_cache.TryGetValue(key, out var hit) && hit.Revision == revision)
        {
            samples = hit.Samples;
        }
        else
        {
            samples = CollectSamples(owner, ownerId, projectId, readJournal, now);
            _cache[key] = (revision, samples);
        }

        // Текущая фича — вне выборки (она и не «типичная»).
        var chosen = samples.Where(s => s.FeatureId != excludeFeatureId).ToList();
        if (chosen.Count < 3) return null;

        var waits = chosen.Where(s => s.WaitingMinutes is not null)
            .Select(s => (double)s.WaitingMinutes!.Value).ToList();

        return new TypicalFeatureStats(
            chosen.Count,
            Percentiles(chosen.Select(s => s.DurationMinutes).ToList()),
            waits.Count >= 3 ? Percentiles(waits) : null,
            Percentiles(chosen.Select(s => (double)s.Loops).ToList()));
    }

    // Выборка: все смерженные фичи проекта и владельца (включая текущую — исключение
    // точечное). Смерженная = State == merged: «честное» закрытие без мержа — не мерж.
    private static List<Sample> CollectSamples(
        IReadOnlyList<TaskItem> owner, string ownerId, string? projectId,
        Func<string, FeatureJournalReadResult?>? readJournal, DateTime now)
    {
        var result = new List<Sample>();
        foreach (var epic in owner)
        {
            if (epic.Role != TaskRoles.Feature) continue;
            if (epic.OwnerId != ownerId) continue;
            if (epic.ProjectId != projectId) continue;

            var featureTasks = new List<TaskItem> { epic };
            foreach (var t in owner)
                if (t.FeatureId == epic.Id) featureTasks.Add(t);

            var run = FeatureProjector.Build(epic, featureTasks, now);
            if (run.State != FeatureStates.Merged) continue;
            var summary = run.Summary;
            if (summary is null) continue;

            int? waiting = null;
            if (readJournal is not null)
            {
                var journal = readJournal(epic.Id);
                if (journal is { StartsWithCreated: true, Truncated: false })
                    waiting = FeatureTimeline.Build(run, journal, now).Totals.WaitingMinutes;
            }

            result.Add(new Sample(epic.Id, summary.DurationMinutes, waiting, run.Verdicts.Loops));
        }
        return result;
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
