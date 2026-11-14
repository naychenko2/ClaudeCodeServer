using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.Spend;
using ClaudeHomeServer.Services.Tasks.Journal;

namespace ClaudeHomeServer.Services.Tasks.Projector;

// Метрики этапа «исполнитель / ревью / человек» (D1b, мини-разрез D0 §1). Чистая функция
// (T, R(T), journal, spendRecords, now) → StageMetrics (И1): ни TaskManager, ни стора журнала и
// расхода у неё нет — гарантия сигнатурой, как у FeatureStageIntervals.
//
// Новой арифметики времени нет, три правила — общие функции:
//  • ожидание человека и вычет очереди — FeatureTimeMath.WaitingEpisodes/QueueIntervals (И4);
//  • минуты работы — правило интервала стадии FeatureStageIntervals.BuildGroups с ключом доли (И6);
//  • упавший ход — FeatureTimeMath.IsFailedTurn (И7).
// Приоритет перекрытий — как в FeatureTimeline: ожидание сильнее работы, очередь сильнее обоих.
internal static class FeatureStageMetrics
{
    private const string ExecutorKey = "executor";
    private const string ReviewKey = "review";

    /// <param name="stage">Задача этапа T.</param>
    /// <param name="reviews">R(T): задачи role=review с T.Id ∈ ReviewOf (qa/final сюда не входят).</param>
    /// <param name="journal">Журнал фичи; null или неполный (нет created в начале, Truncated) → mode=approx.</param>
    /// <param name="spendRecords">Записи расхода среза {T} ∪ R(T); null — подсистема Spend выключена.</param>
    internal static StageMetrics Build(
        TaskItem stage, IReadOnlyList<TaskItem> reviews, FeatureJournalReadResult? journal,
        IReadOnlyList<FeatureSpendRecord>? spendRecords, DateTime now)
    {
        ArgumentNullException.ThrowIfNull(stage);
        ArgumentNullException.ThrowIfNull(reviews);

        var all = new List<TaskItem>(reviews.Count + 1) { stage };
        all.AddRange(reviews);
        var reviewIds = reviews.Where(r => r.Id is not null).Select(r => r.Id!).ToHashSet(StringComparer.Ordinal);
        var stageIds = new HashSet<string>(StringComparer.Ordinal);
        if (stage.Id is not null) stageIds.Add(stage.Id);
        var allIds = new HashSet<string>(stageIds, StringComparer.Ordinal);
        allIds.UnionWith(reviewIds);

        // Окно этапа: [T.CreatedAt, max(CompletedAt задач этапа и ревью) ?? now]; жив, пока любая не Done.
        var live = all.Any(t => t.Status != TaskItemStatus.Done);
        var from = stage.CreatedAt;
        var to = live ? now : all.Max(t => t.CompletedAt) ?? now;
        if (to < from) to = from;
        var elapsed = FeatureTimeMath.Minutes(from, to);
        var window = new StageMetricsWindow(from, to, live, elapsed);

        var spend = SpendSlices(spendRecords, stageIds, reviewIds, allIds);
        var waitingNow = all.FirstOrDefault(t => t.AwaitingHumanSince is not null) is { } w
            ? new StageMetricsWaitingNow(w.Id, w.AwaitingHumanSince!.Value, w.AwaitingHumanReason)
            : null;
        var reviewTasks = reviews
            .OrderBy(r => r.CreatedAt)
            .Select(r => new StageMetricsReviewTask(r.Id, r.Verification?.Verdict, Math.Max(1, r.ReviewOf?.Count ?? 0)))
            .ToList();

        var complete = journal is { StartsWithCreated: true, Truncated: false };
        if (!complete)
            return BuildApprox(stage, reviews, reviewTasks, window, now, spend, waitingNow);

        var entries = journal!.Entries;

        // Минуты работы: правило интервала стадии с ключом доли (И6), затем вычет ожидания и очереди.
        var groupByTask = new Dictionary<string, string>();
        foreach (var id in stageIds) groupByTask[id] = ExecutorKey;
        foreach (var id in reviewIds) groupByTask[id] = ReviewKey;
        var intervals = FeatureStageIntervals.BuildGroups(entries, groupByTask,
            all.Where(t => t.Id is not null && FeatureProjector.IsRunning(t)).Select(t => (t.Id!, t.ClaudeStartedAt)), now);

        var queue = FeatureTimeMath.QueueIntervals(entries, from, to, now);
        var episodes = FeatureTimeMath.WaitingEpisodes(entries, "waiting", "waiting_cleared", from, to, now)
            .Where(e => e.TaskId is not null && allIds.Contains(e.TaskId))
            .ToList();

        List<(DateTime start, DateTime end)> WorkNet(string key, HashSet<string> ids) =>
            FeatureTimeMath.NetIntervals(
                intervals.TryGetValue(key, out var list) ? list.Select(i => (start: i.From, end: i.To)).ToList() : [],
                queue, from, to,
                episodes.Where(e => ids.Contains(e.TaskId!)).Select(e => (start: e.Start, end: e.End)).ToList());

        var executorNet = WorkNet(ExecutorKey, stageIds);
        var reviewNet = reviews.Count > 0 ? WorkNet(ReviewKey, reviewIds) : [];
        var humanNet = FeatureTimeMath.NetIntervals(
            episodes.Select(e => (start: e.Start, end: e.End)).ToList(), queue, from, to);

        // Доли не обязаны давать окно в сумме (ревью может идти параллельно доработке): простой —
        // окно минус объединение трёх долей, а не остаток от суммы.
        var union = FeatureTimeMath.MergeIntervals([.. executorNet, .. reviewNet, .. humanNet]);
        var idle = Math.Max(0, elapsed - FeatureTimeMath.CeilMinutes(Total(union)));

        var turns = entries.Where(e => e.Kind == "turn" && e.TaskId is not null).ToList();
        var executor = Share(Total(executorNet), FeatureProjector.IsRunning(stage),
            turns.Where(e => stageIds.Contains(e.TaskId!)).ToList(), spend.Executor, null);
        var review = reviews.Count == 0 ? null
            : Share(Total(reviewNet), reviews.Any(FeatureProjector.IsRunning),
                turns.Where(e => reviewIds.Contains(e.TaskId!)).ToList(), spend.Review, reviewTasks);
        var human = new StageMetricsHuman(
            FeatureTimeMath.CeilMinutes(Total(humanNet)), episodes.Count, spend.Replies, waitingNow);

        return new StageMetrics("journal", window, executor, review, human, idle, spend.From);
    }

    // Приближённый режим (журнал неполный, D0 §6): исполнитель — [ClaudeStartedAt, CompletedAt ?? now]
    // (только последний запуск), минуты ревью и человека, ходы и число ожиданий — null; токены и
    // replies от журнала не зависят.
    private static StageMetrics BuildApprox(
        TaskItem stage, IReadOnlyList<TaskItem> reviews, List<StageMetricsReviewTask> reviewTasks,
        StageMetricsWindow window, DateTime now, SpendParts spend, StageMetricsWaitingNow? waitingNow)
    {
        var minutes = stage.ClaudeStartedAt is { } started
            ? FeatureTimeMath.Minutes(started, stage.CompletedAt ?? now)
            : 0;
        var executor = Totals(minutes, FeatureProjector.IsRunning(stage), null, null, spend.Executor, [], null);
        var review = reviews.Count == 0 ? null
            : Totals(null, reviews.Any(FeatureProjector.IsRunning), null, null, spend.Review, [], reviewTasks);
        return new StageMetrics("approx", window, executor, review,
            new StageMetricsHuman(null, null, spend.Replies, waitingNow), null, spend.From);
    }

    private static StageMetricsShare Share(
        TimeSpan work, bool live, List<FeatureJournalEntry> turns, SpendShare? spend,
        IReadOnlyList<StageMetricsReviewTask>? tasks)
    {
        var models = turns.Select(t => t.Model).Where(m => !string.IsNullOrEmpty(m)).Select(m => m!)
            .Distinct(StringComparer.Ordinal).ToList();
        return Totals(FeatureTimeMath.CeilMinutes(work), live, turns.Count,
            turns.Count(t => FeatureTimeMath.IsFailedTurn(t.Outcome)), spend, models, tasks);
    }

    private static StageMetricsShare Totals(
        int? minutes, bool live, int? turns, int? errorTurns, SpendShare? spend,
        IReadOnlyList<string> turnModels, IReadOnlyList<StageMetricsReviewTask>? tasks) =>
        new(minutes, live, turns, errorTurns, spend?.Tokens, spend?.CostUsd, spend?.CostPartial ?? false,
            spend?.HiddenCostModels ?? [], turnModels.Count > 0 ? turnModels : spend?.Models ?? [], tasks);

    private static TimeSpan Total(IReadOnlyList<(DateTime start, DateTime end)> intervals)
    {
        var total = TimeSpan.Zero;
        foreach (var (s, e) in intervals) total += e - s;
        return total;
    }

    // ─── Расход: токены, стоимость и реплики человека по записям среза ───────────────────────────

    private sealed record SpendShare(
        StageMetricsTokens Tokens, double CostUsd, bool CostPartial,
        IReadOnlyList<string> HiddenCostModels, IReadOnlyList<string> Models);

    private sealed record SpendParts(SpendShare? Executor, SpendShare? Review, int? Replies, string? From);

    // Роль записи определяет задача, а не запись (И5): TaskId ∈ {T} — исполнитель, ∈ R(T) — ревью.
    // Spend выключен (null) или записей группы нет → null по доле; replies — null и при дневных
    // агрегатах в срезе (инициатора они не несут), иначе число записей с Initiator = human.
    private static SpendParts SpendSlices(
        IReadOnlyList<FeatureSpendRecord>? records, HashSet<string> stageIds, HashSet<string> reviewIds,
        HashSet<string> allIds)
    {
        if (records is null) return new SpendParts(null, null, null, null);
        var mine = records.Where(r => r.TaskId is not null && allIds.Contains(r.TaskId)).ToList();
        if (mine.Count == 0) return new SpendParts(null, null, null, null);

        var executor = SpendOf(mine.Where(r => stageIds.Contains(r.TaskId!)).ToList());
        var review = SpendOf(mine.Where(r => reviewIds.Contains(r.TaskId!)).ToList());
        int? replies = mine.Any(r => r.IsDaily)
            ? null
            : mine.Count(r => r.Initiator == SpendInitiators.Human);
        var from = mine.Min(r => DateOnly.FromDateTime(r.Timestamp)).ToString("yyyy-MM-dd");
        return new SpendParts(executor, review, replies, from);
    }

    private static SpendShare? SpendOf(List<FeatureSpendRecord> records)
    {
        if (records.Count == 0) return null;
        long input = 0, output = 0, cacheRead = 0, cacheCreation = 0;
        var cost = 0.0;
        var partial = false;
        var hidden = new HashSet<string>(StringComparer.Ordinal);
        foreach (var r in records)
        {
            input += Math.Max(0, r.InputTokens);
            output += Math.Max(0, r.OutputTokens);
            cacheRead += Math.Max(0, r.CacheReadTokens);
            cacheCreation += Math.Max(0, r.CacheCreationTokens);
            // Единая точка правды цены (SpendCostReliability): ненадёжная — токены идут, стоимость нет.
            if (SpendCostReliability.ReliableCostOf(r) is { } c) cost += (double)c;
            else
            {
                partial = true;
                if (r.Model is not null) hidden.Add(r.Model);
            }
        }
        var models = records.Select(r => r.Model).Where(m => !string.IsNullOrEmpty(m)).Select(m => m!)
            .Distinct(StringComparer.Ordinal).ToList();
        return new SpendShare(new StageMetricsTokens(input, output, cacheRead, cacheCreation), cost, partial,
            [.. hidden.OrderBy(m => m, StringComparer.Ordinal)], models);
    }
}
