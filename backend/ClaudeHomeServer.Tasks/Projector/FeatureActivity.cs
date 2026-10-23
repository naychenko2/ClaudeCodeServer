using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.Tasks.Journal;

namespace ClaudeHomeServer.Services.Tasks.Projector;

// ─── Б9c, шаг 3: единый модуль активности фичи ────────────────────────────────────────────────
//
// «Активна на момент t» — одна функция для графика времени (FeatureTimeline) и деления
// (SharedSpendSplitter). Правило (оба режима, одно и то же):
//   • работа: [старт, min(сдача, остановка, now)). Approx: ClaudeStartedAt →
//     min(CompletedAt, ExecutorStoppedAt, now); остановка учитывается, только если ≥ старта.
//     Journal: started.StartedAt → первое (stopped | status→Done) после, минимум.
//     Мерж — частный случай: интервал merge-задачи.
//   • ожидание: AwaitingHumanSince ЛЮБОЙ задачи фичи (не только эпика) и DeviceWaitSince —
//     как текущее ожидание до now. Journal: waiting…waiting_cleared, device_wait…device_wait_cleared.
//   • активность фичи — объединение этих интервалов, обрезанное по epic.CreatedAt.
//
// Оговорка (осознанно): приоритет «stopped главнее running» из ComputeState в историю НЕ
// переносим. Активность = «над фичей кто-то работает или кто-то её ждёт» — мера деления,
// а не статус в интерфейсе. Остановленная задача перестаёт быть активной в момент остановки.
//
// Побочный эффект: на графике времени (approx) остановленная задача больше не рисуется
// работой до now — это исправление, а не регрессия.

// Интервал активности фичи: [From, To) (UTC) — half-open: включительно с начала,
// исключая конец. Ход ровно на From входит, ровно на To — нет.
public sealed record FeatureActivityInterval(DateTime From, DateTime To);

// Активность фичи: её id + интервалы активности. Чистая структура входа SharedSpendSplitter.
public sealed record FeatureActivity(string FeatureId, IReadOnlyList<FeatureActivityInterval> Intervals);

// Чистые функции: вход — FeatureRun (approx) или FeatureJournalReadResult (journal) + now.
// Выход — объединённые интервалы активности фичи (half-open), обрезанные по epic.CreatedAt.
public static class FeatureActivityBuilder
{
    // Конец work-интервала задачи: min(CompletedAt, ExecutorStoppedAt, now).
    // Остановка учитывается, только если ≥ старта (старт в TaskManager её и так сбрасывает).
    // end < start → start (дегенерированный интервал нулевой).
    public static DateTime WorkEnd(TaskItem t, DateTime now)
    {
        var start = t.ClaudeStartedAt!.Value;
        var end = now;
        if (t.CompletedAt is not null && t.CompletedAt.Value < end) end = t.CompletedAt.Value;
        if (t.ExecutorStoppedAt is not null && t.ExecutorStoppedAt.Value >= start && t.ExecutorStoppedAt.Value < end)
            end = t.ExecutorStoppedAt.Value;
        return end < start ? start : end;
    }

    // Approx-режим (без журнала): работа + ожидание + device_wait по полям задач.
    // run null → нет интервалов (фича не найдена).
    public static IReadOnlyList<FeatureActivityInterval> Approx(FeatureRun? run, DateTime now)
    {
        if (run is null) return [];
        var clipFrom = run.Epic.CreatedAt;
        var intervals = new List<FeatureActivityInterval>();
        foreach (var t in AllTasks(run))
        {
            // работа: [ClaudeStartedAt, min(CompletedAt, ExecutorStoppedAt, now))
            if (t.ClaudeStartedAt is not null)
            {
                var start = t.ClaudeStartedAt.Value;
                var end = WorkEnd(t, now);
                intervals.Add(new FeatureActivityInterval(start, end));
            }
            // ожидание человека: [AwaitingHumanSince, now) — ЛЮБОЙ задачи фичи.
            if (t.AwaitingHumanSince is not null)
            {
                var start = t.AwaitingHumanSince.Value;
                var end = now;
                if (end < start) end = start;
                intervals.Add(new FeatureActivityInterval(start, end));
            }
            // ожидание устройства: [DeviceWaitSince, now).
            if (t.DeviceWaitSince is not null)
            {
                var start = t.DeviceWaitSince.Value;
                var end = now;
                if (end < start) end = start;
                intervals.Add(new FeatureActivityInterval(start, end));
            }
        }
        return Clip(intervals, clipFrom);
    }

    // Journal-режим (полный журнал): started→(stopped|Done), waiting→(waiting_cleared|now),
    // device_wait→(device_wait_cleared|now). clipFrom — обрезка по epic.CreatedAt.
    public static IReadOnlyList<FeatureActivityInterval> Journal(
        FeatureJournalReadResult journal, DateTime now, DateTime? clipFrom = null)
    {
        var intervals = new List<FeatureActivityInterval>();

        // running/merging: started → (stopped | status→Done | now).
        // Старт — время события (At), не StartedAt (nullable, может не быть в старых записях).
        foreach (var started in journal.Entries.Where(e => e.Kind == "started").OrderBy(e => e.At))
        {
            var end = EndOf(journal, started.TaskId, started.At, now);
            if (end < started.At) end = started.At;
            intervals.Add(new FeatureActivityInterval(started.At, end));
        }
        // waiting: waiting → (waiting_cleared | now).
        foreach (var w in journal.Entries.Where(e => e.Kind == "waiting").OrderBy(e => e.At))
        {
            var end = FirstAfter(journal, w.TaskId, w.At, "waiting_cleared", now);
            if (end < w.At) end = w.At;
            intervals.Add(new FeatureActivityInterval(w.At, end));
        }
        // device_wait: device_wait → (device_wait_cleared | now).
        foreach (var w in journal.Entries.Where(e => e.Kind == "device_wait").OrderBy(e => e.At))
        {
            var end = FirstAfter(journal, w.TaskId, w.At, "device_wait_cleared", now);
            if (end < w.At) end = w.At;
            intervals.Add(new FeatureActivityInterval(w.At, end));
        }
        return Clip(intervals, clipFrom);
    }

    // Обрезка по clipFrom: интервалы, начинающиеся раньше, сдвигаются; пустые (From == To) —
    // отбрасываются. clipFrom null → без обрезки.
    private static IReadOnlyList<FeatureActivityInterval> Clip(
        List<FeatureActivityInterval> intervals, DateTime? clipFrom)
    {
        if (clipFrom is null) return intervals;
        var result = new List<FeatureActivityInterval>(intervals.Count);
        foreach (var iv in intervals)
        {
            var from = iv.From < clipFrom.Value ? clipFrom.Value : iv.From;
            if (from < iv.To)
                result.Add(new FeatureActivityInterval(from, iv.To));
        }
        return result;
    }

    // Конец work-периода задачи: первый (stopped | status→Done) после `from`, иначе now.
    private static DateTime EndOf(FeatureJournalReadResult journal, string? taskId, DateTime from, DateTime now)
    {
        var end = now;
        foreach (var e in journal.Entries)
        {
            if (e.TaskId != taskId || e.At < from) continue;
            if (e.Kind == "stopped" || (e.Kind == "status" && e.To == "Done"))
            {
                if (e.At < end) end = e.At;
            }
        }
        return end;
    }

    // Первое событие вида `endKind` (для задачи) после `from`, иначе now.
    private static DateTime FirstAfter(FeatureJournalReadResult journal, string? taskId, DateTime from,
        string endKind, DateTime now)
    {
        var end = now;
        foreach (var e in journal.Entries)
        {
            if (e.TaskId != taskId || e.Kind != endKind || e.At < from) continue;
            if (e.At < end) end = e.At;
        }
        return end;
    }

    // Все задачи фичи: эпик + дочерние (та же выборка, что FeatureTimeline/FeatureBreakdown).
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
}
