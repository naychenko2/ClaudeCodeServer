using ClaudeHomeServer.Models;
using Microsoft.Extensions.Configuration;

namespace ClaudeHomeServer.Services.Tasks.Projector;

// Место фичи в очереди проекта (вычисляемое, без хранения): position — с единицы, length —
// сколько фич стоит в очереди этого проекта. JSON: queue { position, length }.
public sealed record FeatureQueueInfo(int Position, int Length);

// Правила очереди фич (волна 6, разрез Александра 25.10 §1–§3, §8) — чистые функции.
// Очередь — не сущность, а удержание Hold { reason: "queue" }: порядок и лимит выводятся здесь,
// и по ним же (одна функция Order) очередь покажет «вторая из трёх» и снимет следующую фичу.
public static class FeatureQueuePolicy
{
    // Единственное место чтения конфига лимита (grep по backend даёт одно совпадение).
    public const string LimitConfigKey = "Tasks:FeatureQueue:MaxActivePerProject";

    // Лимит одновременно занятых слотов на проект: нет ключа, мусор, 0 и отрицательные → 1.
    public static int Limit(IConfiguration? config) =>
        int.TryParse(config?[LimitConfigKey], out var limit) && limit >= 1 ? limit : 1;

    // Фича стоит в очереди: пауза с причиной queue (закрытая/смерженная не считается —
    // их state перекрывает удержание).
    public static bool IsQueued(FeatureRun run) =>
        run.State == FeatureStates.Paused && run.Hold?.Reason == FeatureHoldReasons.Queue;

    // Очередь одного набора фич (вызывающий отбирает проект): FIFO по Hold.Since, при равенстве —
    // по epic.Id (ordinal). Фичи не из очереди отбрасываются.
    public static IReadOnlyList<FeatureRun> Order(IEnumerable<FeatureRun> runs) => runs
        .Where(IsQueued)
        .OrderBy(r => r.Hold!.Since)
        .ThenBy(r => r.Epic.Id, StringComparer.Ordinal)
        .ToList();

    // featureId → место в очереди своего проекта. Очередь считается по проекту (Epic.ProjectId;
    // null — своя группа), порядок — тот же Order, по которому очередь снимает следующую фичу.
    public static Dictionary<string, FeatureQueueInfo> Positions(IEnumerable<FeatureRun> runs)
    {
        var result = new Dictionary<string, FeatureQueueInfo>(StringComparer.Ordinal);
        foreach (var group in runs.Where(IsQueued).GroupBy(r => r.Epic.ProjectId))
        {
            var ordered = Order(group);
            for (var i = 0; i < ordered.Count; i++)
                result[ordered[i].FeatureId] = new FeatureQueueInfo(i + 1, ordered.Count);
        }
        return result;
    }
}
