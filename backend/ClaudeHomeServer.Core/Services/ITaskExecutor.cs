using ClaudeHomeServer.Models;

namespace ClaudeHomeServer.Services;

// Узкий шов TaskExecutionService для выноса Tasks (Этап 5, волна 1).
// Реализует его TaskExecutorAdapter (Composition): ExecuteAsync и CheckStalledExecutorAsync
// он форвардит в TaskExecutionService, а ReconcileFeatureQueuesAsync делает через
// FeatureQueueService — у самого исполнителя этого метода нет.
// TaskSchedulerService на каждом тике зовёт три метода:
//   • ExecuteAsync — автозапуск исполнителя в момент срока (актор хода — создатель задачи,
//     см. TaskLaunchActor: автозапуск не повышает ход до арендатора);
//   • CheckStalledExecutorAsync — страховка «ход кончился, а задачу никто не закрыл»;
//   • ReconcileFeatureQueuesAsync — сверка очереди фич (волна 6): страховка потерянных событий,
//     слот свободен, а голова очереди ждёт.
// Остальные публичные методы TaskExecutionService (TryDeliverCompletionAsync,
// NotifyAsync, BroadcastTaskChangedAsync, OnUserMessageAsync и пр.) остаются в Main
// и в шов не входят — Tasks ими не пользуется.
public interface ITaskExecutor
{
    // actorUserId — актор порождённого хода: у автозапуска это TaskLaunchActor.For(task)
    // (создатель задачи); null — актор неизвестен/владелец, ход идёт как раньше.
    Task ExecuteAsync(TaskItem task, bool auto, string? actorUserId);

    Task CheckStalledExecutorAsync(TaskItem task, DateTime nowUtc);

    Task ReconcileFeatureQueuesAsync(string ownerId);
}