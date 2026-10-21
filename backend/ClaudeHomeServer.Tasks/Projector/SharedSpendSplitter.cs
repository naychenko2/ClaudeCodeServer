using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.Spend;
using ClaudeHomeServer.Services.Tasks.Journal;

namespace ClaudeHomeServer.Services.Tasks.Projector;

// ─── Б9c: деление расхода ОБЩЕГО (оркестраторского) чата поровну между активными фичами ──────
//
// «Общий чат» — чат с kind key/curator/orchestrator, входящий в ДВЕ И БОЛЕЕ фичи владельца
// (решение Андрея 20.10: его расход не приписывается каждой фиче целиком — двойной счёт,
// critical ревью Глеба в Б9b; вместо этого делится поровну между фичами, активными на момент
// хода). Доля помечена как оценка: `method` = journal (по полному журналу) | approx (по
// интервалам задач).

// Интервал активности фичи: [From, To) (UTC) — период, когда фича была активна
// (state ∈ {running, waiting, merging}). Граница (фиксация правила, тест SharedSpendSplitter):
// включительно с начала, исключая конец (half-open) — ход ровно на From входит, ровно на To —
// нет. Half-open исключает двойной счёт на стыке двух смежных интервалов.
public sealed record FeatureActivityInterval(DateTime From, DateTime To);

// Активность фичи: её id + интервалы активности. Чистая структура входа SharedSpendSplitter.
public sealed record FeatureActivity(string FeatureId, IReadOnlyList<FeatureActivityInterval> Intervals);

// Чистая функция (без IO): вход — записи расхода по ходам ОБЩЕГО чата (SpendFeatureReader.Records)
// + интервалы активности ВСЕХ фич, в которые чат входит (знаменатель деления) + id текущей фичи
// + метод оценки. Выход — доля текущей фичи (FeatureSpendShared) + признак ненадёжной цены её
// доли + модели без цены в доле (для costPartial/hiddenCostModels итога, те же правила, что Б9b).
//
// Правило деления: каждый ход общего чата делится ПОРОВНУ (1/N) между N фичами, активными на
// его момент; ход без активных фич не распределяется (остаётся «нигде»). Доля фичи = сумма
// (ход / N) по ходам, на которые фича активна. Токены клампаются в ≥ 0 (как в итоге Б9b) и
// делятся в decimal (без потери); деньги — только с надёжной ценой (единая точка правды
// SpendCostReliability: читатель SpendFeatureReader выставляет FeatureSpendRecord.UnreliableCost
// при сборке записи, этот же флаг читает итог расхода — вторая точка правды не заводится).
public static class SharedSpendSplitter
{
    // Доля фичи в расходе общего чата.
    //   sharedTurns — записи расхода по ходам общего чата (время, токены, цена, источник,
    //     признак ненадёжной цены — уже выставлен читателем);
    //   activities  — интервалы активности ВСЕХ фич, в которые чат входит (знаменатель);
    //   featureId   — фича, долю которой считаем;
    //   method      — "journal" | "approx" (метод оценки активности; задаёт вызывающий).
    // Возвращает (доля, признак ненадёжной цены доли, модели без цены в доле).
    public static (FeatureSpendShared Share, bool CostPartial, List<string> HiddenModels) Split(
        IReadOnlyList<FeatureSpendRecord> sharedTurns,
        IReadOnlyList<FeatureActivity> activities,
        string featureId,
        string method)
    {
        // Индекс featureId → интервалы: быстрая проверка «активна ли текущая фича на моменте».
        var byId = new Dictionary<string, IReadOnlyList<FeatureActivityInterval>>(StringComparer.Ordinal);
        foreach (var a in activities)
            byId[a.FeatureId] = a.Intervals;

        long input = 0, output = 0, cacheRead = 0, cacheCreation = 0;
        double cost = 0;
        int turns = 0;
        var hidden = new HashSet<string>(StringComparer.Ordinal);
        bool costPartial = false;

        foreach (var rec in sharedTurns)
        {
            // Знаменатель: число фич, активных на момент хода. 0 — ход не распределяется.
            var active = CountActive(activities, rec.Timestamp);
            if (active == 0) continue;

            // Текущая фича активна на моменте? Нет — её доля 0 (ход делится остальными).
            if (!IsFeatureActive(byId, featureId, rec.Timestamp)) continue;

            // Доля = ход / active. Токены: decimal (без потери), кламп ≥ 0, округление до long.
            input += DivRound(Math.Max(0, rec.InputTokens), active);
            output += DivRound(Math.Max(0, rec.OutputTokens), active);
            cacheRead += DivRound(Math.Max(0, rec.CacheReadTokens), active);
            cacheCreation += DivRound(Math.Max(0, rec.CacheCreationTokens), active);

            // Цена: только с надёжной ценой (та же логика, что SpendFeatureReader.ComputeTotals):
            // free → надёжный 0; ненадёжная модель или запись без цены (не free) → стоимость не в
            // сумму, модель в hidden, costPartial.
            var reliable = !rec.UnreliableCost && (rec.Source == SpendSources.Free || rec.CostUsd is not null);
            if (reliable)
                cost += (rec.Source == SpendSources.Free ? 0 : (rec.CostUsd ?? 0)) / active;
            else
            {
                costPartial = true;
                if (rec.Model is not null) hidden.Add(rec.Model);
            }

            turns++;
        }

        return (
            new FeatureSpendShared(input, output, cacheRead, cacheCreation, cost, turns, method),
            costPartial,
            hidden.OrderBy(m => m, StringComparer.Ordinal).ToList());
    }

    // Число фич, активных на момент t: у которых есть интервал, покрывающий t (half-open).
    private static int CountActive(IReadOnlyList<FeatureActivity> activities, DateTime t)
    {
        var count = 0;
        foreach (var a in activities)
            if (IsFeatureActive(a.Intervals, t)) count++;
        return count;
    }

    // Фича активна на момент t: t внутри любого её интервала [From, To) (включительно с From,
    // исключая To — фиксация правила, см. FeatureActivityInterval).
    private static bool IsFeatureActive(IReadOnlyList<FeatureActivityInterval> intervals, DateTime t)
    {
        foreach (var iv in intervals)
            if (iv.From <= t && t < iv.To) return true;
        return false;
    }

    private static bool IsFeatureActive(
        Dictionary<string, IReadOnlyList<FeatureActivityInterval>> byId, string featureId, DateTime t)
        => byId.TryGetValue(featureId, out var intervals) && IsFeatureActive(intervals, t);

    // Деление long на int: decimal (без потери), округление до ближайшего (MidpointRounding.ToEven,
    // детерминированно). Правило остатка (фиксация): доля = round(ход / N); сумма долей по фичам
    // = сумма хода ДО округления (decimal точен), после округления расхождение ≤ 0.5 на фичу·ход.
    private static long DivRound(long value, int divisor)
    {
        if (divisor <= 1) return value;
        return (long)Math.Round((decimal)value / divisor, MidpointRounding.ToEven);
    }

    // ─── Интервалы активности фичи ─────────────────────────────────────────────────────────────

    // Approx-режим (без журнала): «активна» = state ∈ {running, waiting, merging} ≈ по задачам:
    //   running/merging — интервалы задач фичи [ClaudeStartedAt, CompletedAt ?? now] (merge-задача
    //     — одна из них, её интервал тот же);
    //   waiting — только ТЕКУЩЕЕ ожидание [AwaitingHumanSince, now] (прошлые ожидания в approx
    //     неизвестны — риск 5 плана: доля может уйти другим фичам, помечено method=approx).
    // run null (фича не найдена: чужая/удалённая) → интервалов нет (фича не активна).
    public static IReadOnlyList<FeatureActivityInterval> ApproxActivity(FeatureRun? run, DateTime now)
    {
        if (run is null) return [];
        var intervals = new List<FeatureActivityInterval>();
        foreach (var t in AllTasks(run))
        {
            if (t is null || t.ClaudeStartedAt is null) continue;
            var start = t.ClaudeStartedAt.Value;
            var end = t.CompletedAt ?? now;
            if (end < start) end = start;
            intervals.Add(new FeatureActivityInterval(start, end));
        }
        if (run.Epic is not null && run.Epic.AwaitingHumanSince is not null)
        {
            var start = run.Epic.AwaitingHumanSince.Value;
            var end = now;
            if (end < start) end = start;
            intervals.Add(new FeatureActivityInterval(start, end));
        }
        return intervals;
    }

    // Journal-режим (полный журнал): «активна» = state ∈ {running, waiting, merging} по событиям:
    //   running/merging — work-периоды: started → (stopped | status→Done | now) (merge-задача —
    //     та же задача, её started..end здесь);
    //   waiting — waiting → (waiting_cleared | now) + device_wait → (device_wait_cleared | now)
    //     (ожидание устройства — как waiting, как в FeatureTimeline.BuildJournal).
    public static IReadOnlyList<FeatureActivityInterval> JournalActivity(FeatureJournalReadResult journal, DateTime now)
    {
        var intervals = new List<FeatureActivityInterval>();

        // running/merging: started → (stopped | status→Done | now).
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
        // device_wait: device_wait → (device_wait_cleared | now) (ожидание устройства = waiting).
        foreach (var w in journal.Entries.Where(e => e.Kind == "device_wait").OrderBy(e => e.At))
        {
            var end = FirstAfter(journal, w.TaskId, w.At, "device_wait_cleared", now);
            if (end < w.At) end = w.At;
            intervals.Add(new FeatureActivityInterval(w.At, end));
        }
        return intervals;
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

    // Первый событие вида `endKind` (для задачи) после `from`, иначе now.
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
}
