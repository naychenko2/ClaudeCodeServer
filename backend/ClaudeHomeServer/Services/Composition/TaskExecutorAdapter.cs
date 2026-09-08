using ClaudeHomeServer.Models;

namespace ClaudeHomeServer.Services.Composition;

// Реализация ITaskExecutor (Core) поверх TaskExecutionService (Main).
// Тонкий форвардер: вертикаль Tasks через шов зовёт автозапуск исполнителя и
// страховку «задача осталась в работе», ничего не зная о SessionManager/
// NotificationService/ProjectManager/UserStore, которые держит сам исполнитель.
public sealed class TaskExecutorAdapter(TaskExecutionService executor) : ITaskExecutor
{
    public Task ExecuteAsync(TaskItem task, bool auto) =>
        executor.ExecuteAsync(task, auto);

    public Task CheckStalledExecutorAsync(TaskItem task, DateTime nowUtc) =>
        executor.CheckStalledExecutorAsync(task, nowUtc);
}