using ClaudeHomeServer.Services.Tasks.Journal;

namespace ClaudeHomeServer.Services.Tasks.Projector;

// Отклонение оценок фичи (FS-M478, величины 4, 7, 8 §6 записки feature-page-simplify-v4):
// чистая функция над уже прочитанным снимком журнала (тот же journalResult, что читается для
// FeatureTimeline.Build — второго чтения журнала нет, B1b). Строки estimate_set пишет TaskManager
// (FS-O2a/FS-O2b): числа оценки в EtaMinutes/Total.
// - InitialEtaMinutes — базовый срок эпика (TaskItem.InitialEtaMinutes, фиксируется один раз);
// - FirstEta/CurrentEta — etaMinutes первой и последней строки estimate_set с оценкой;
// - ShiftMinutes — Current − First (null, если строк с оценкой меньше двух);
// - DenominatorChanges — сколько раз total менялся между соседними строками estimate_set,
//   где total не null (строки без total пропускаются, сменой не считаются);
// - Accuracy — только у закрытой фичи (run.Closed — эпик Done, FeatureProjector.cs:559–560):
//   Summary.DurationMinutes / InitialEtaMinutes. Summary есть и у merged с открытым эпиком
//   (FeatureProjector.cs:573–601), но такая фича не закрыта — accuracy у неё null.
public sealed record FeatureEstimateDrift(
    // Базовая оценка срока эпика; null — не зафиксирована.
    int? InitialEtaMinutes,
    // etaMinutes первой / последней строки estimate_set с непустым etaMinutes; null — таких нет.
    int? FirstEtaMinutes,
    int? CurrentEtaMinutes,
    // Current − First; null — строк с оценкой меньше двух (при одной first == current, но сдвига нет).
    int? ShiftMinutes,
    // Число смен total между соседними строками estimate_set с total (0 — знаменатель не менялся).
    int DenominatorChanges,
    // Точность срока закрытой фичи: факт / базовая оценка; null — фича открыта или базовой оценки нет.
    double? Accuracy)
{
    public static FeatureEstimateDrift Build(FeatureRun run, FeatureJournalReadResult? journal)
    {
        ArgumentNullException.ThrowIfNull(run);
        var initial = run.Epic.InitialEtaMinutes;

        // Строки журнала — в порядке At (тот же порядок, что GroupByTask в FeatureTimeline.cs:603).
        var rows = (journal?.Entries ?? [])
            .Where(e => e.Kind == "estimate_set")
            .OrderBy(e => e.At)
            .ToList();

        var withEta = rows.Where(e => e.EtaMinutes is not null).ToList();
        int? first = withEta.Count > 0 ? withEta[0].EtaMinutes : null;
        int? current = withEta.Count > 0 ? withEta[^1].EtaMinutes : null;
        int? shift = withEta.Count >= 2 ? current - first : null;

        // Смены знаменателя — только между соседними строками с total: строка без total
        // выпадает из последовательности, разрыв через неё сменой не считается.
        var totals = rows.Where(e => e.Total is not null).Select(e => e.Total!.Value).ToList();
        int changes = 0;
        for (var i = 1; i < totals.Count; i++)
        {
            if (totals[i] != totals[i - 1]) changes++;
        }

        // Точность — только у закрытой фичи (эпик Done) с ненулевой базовой оценкой: Summary есть
        // и у смерженной с открытым эпиком, но она не закрыта; без базовой оценки делить не на что.
        double? accuracy = run.Closed && run.Summary is { } summary && initial is > 0
            ? summary.DurationMinutes / initial.Value
            : null;

        return new FeatureEstimateDrift(initial, first, current, shift, changes, accuracy);
    }
}
