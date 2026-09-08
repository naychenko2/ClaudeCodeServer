using ClaudeHomeServer.Models;

namespace ClaudeHomeServer.Services;

// Узкий шов TaskExecutionService для выноса Tasks (Этап 5, волна 1).
// TaskSchedulerService на каждом тике зовёт два метода одного сервиса:
//   • ExecuteAsync — автозапуск исполнителя в момент срока;
//   • CheckStalledExecutorAsync — страховка «ход кончился, а задачу никто не закрыл».
// Остальные публичные методы TaskExecutionService (TryDeliverCompletionAsync,
// NotifyAsync, BroadcastTaskChangedAsync, OnUserMessageAsync и пр.) остаются в Main
// и в шов не входят — Tasks ими не пользуется.
public interface ITaskExecutor
{
    Task ExecuteAsync(TaskItem task, bool auto);

    Task CheckStalledExecutorAsync(TaskItem task, DateTime nowUtc);
}