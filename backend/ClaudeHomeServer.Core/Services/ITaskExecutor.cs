using ClaudeHomeServer.Models;

namespace ClaudeHomeServer.Services;

// Узкий шов TaskExecutionService для выноса Tasks (Этап 5, волна 1).
// TaskSchedulerService на каждом тике зовёт три метода:
//   • ExecuteAsync — автозапуск исполнителя в момент срока;
//   • CheckStalledExecutorAsync — страховка «ход кончился, а задачу никто не закрыл»;
//   • ReconcileFeatureQueuesAsync — сверка очереди фич (волна 6): страховка потерянных событий,
//     слот свободен, а голова очереди ждёт.
// Остальные публичные методы TaskExecutionService (TryDeliverCompletionAsync,
// NotifyAsync, BroadcastTaskChangedAsync, OnUserMessageAsync и пр.) остаются в Main
// и в шов не входят — Tasks ими не пользуется.
public interface ITaskExecutor
{
    Task ExecuteAsync(TaskItem task, bool auto);

    Task CheckStalledExecutorAsync(TaskItem task, DateTime nowUtc);

    Task ReconcileFeatureQueuesAsync(string ownerId);
}