using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;
using Microsoft.AspNetCore.SignalR;

namespace ClaudeHomeServer.Hubs;

// Extension-метод рассылки `TaskChangedMessage` через Hub пользователю.
// Переехал из Controllers/TasksController.cs:486-493 (Этап 5, волна 5):
// инверсия «вертикаль → Controllers» дважды помечена ⚠ в SubsystemBoundaryTests
// (TaskSchedulerService → Controllers.TaskHubExtensions, см. IlBoundaryRegressionTests).
// В Hubs-неймспейсе префикс уже открыт в allow-list обеих вертикалей (Tasks, Notes),
// поэтому сторож принял переезд без правок Boundaries.
public static class TaskHubExtensions
{
    // Уведомление всех устройств пользователя об изменении задачи
    public static Task BroadcastTaskChangedAsync(
        this IHubContext<SessionHub> hub, string userId, string action, TaskItem task) =>
        hub.Clients.Group("user_" + userId)
            .SendAsync("message", new TaskChangedMessage(action, task));
}
