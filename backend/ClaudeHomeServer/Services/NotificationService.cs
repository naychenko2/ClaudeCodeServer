using ClaudeHomeServer.Hubs;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;
using Microsoft.AspNetCore.SignalR;

namespace ClaudeHomeServer.Services;

// Единый сервис для отправки уведомлений: сохраняет в NotificationStore + шлёт
// SignalR (in-app тост) + web push (опционально). Все существующие отправители
// (TaskSchedulerService, TaskExecutionService, DailyBriefingService,
//  SessionSummaryService, PersonaAutomationService) проходят через него.
public class NotificationService(
    NotificationStore store,
    IHubContext<SessionHub> hub,
    PushService push,
    ILogger<NotificationService> log)
{
    // Создать уведомление, сохранить, разослать. Возвращает id созданного уведомления.
    public async Task<string> SendAsync(string userId, CreateNotificationRequest req, bool sendPush = false)
    {
        var item = await store.AddAsync(userId, req);

        var msg = new NotificationMessage(
            Title: item.Title,
            Body: item.Body,
            Url: item.Url,
            Kind: item.Kind,
            NotificationId: item.Id,
            Type: item.Type,
            ProjectId: item.ProjectId,
            SessionId: item.SessionId,
            TaskId: item.TaskId,
            Source: item.Source,
            Tag: item.Tag);

        // In-app тост (SignalR)
        await hub.Clients.Group("user_" + userId).SendAsync("message", msg);

        // Web push (опционально — для важных: напоминания, завершение задачи)
        if (sendPush)
            await push.SendToUserAsync(userId, msg);

        log.LogDebug("Уведомление {Id} «{Title}» отправлено пользователю {UserId}",
            item.Id, item.Title, userId);
        return item.Id;
    }

    // Отправить уже сформированное NotificationMessage (совместимость со старыми отправителями).
    // Сохраняет в сторадж + SignalR. sendPush — отправить также web push.
    public async Task SendNotificationMessageAsync(string userId, NotificationMessage msg, bool sendPush = false)
    {
        await SendAsync(userId, new CreateNotificationRequest
        {
            Kind = msg.Kind,
            Type = msg.Type ?? "",
            Title = msg.Title,
            Body = msg.Body,
            Url = msg.Url,
            ProjectId = msg.ProjectId,
            SessionId = msg.SessionId,
            TaskId = msg.TaskId,
            Source = msg.Source,
            Tag = msg.Tag,
        }, sendPush);
    }

    // Удобный метод для напоминаний задач
    public async Task SendTaskReminderAsync(string userId, string taskId, string title,
        string dueText, string? projectId, string url)
    {
        await SendAsync(userId, new CreateNotificationRequest
        {
            Kind = "reminder",
            Type = "task_reminder",
            Title = "Напоминание о задаче",
            Body = $"{title} — срок {dueText}",
            Url = url,
            TaskId = taskId,
            ProjectId = projectId,
            Source = projectId is null ? null : $"Проект",
            Tag = "Напоминание",
        }, sendPush: true);
    }

    // Для событий Claude-исполнителя
    public async Task SendExecutionEventAsync(string userId, string sessionId,
        string title, string body, string? projectId, string? taskId,
        string type, string tag, string url)
    {
        await SendAsync(userId, new CreateNotificationRequest
        {
            Kind = "claude",
            Type = type,
            Title = title,
            Body = body,
            Url = url,
            SessionId = sessionId,
            TaskId = taskId,
            ProjectId = projectId,
            Source = projectId is null ? "Claude" : $"Проект",
            Tag = tag,
        }, sendPush: true);
    }

    // Для системных событий (дайджест, саммари, конвейер)
    public async Task SendSystemEventAsync(string userId, string title, string body,
        string kind, string type, string url, string? tag, bool sendPush = false)
    {
        await SendAsync(userId, new CreateNotificationRequest
        {
            Kind = kind,
            Type = type,
            Title = title,
            Body = body,
            Url = url,
            Tag = tag,
        }, sendPush: sendPush);
    }

    // Для ответов персон
    public async Task SendPersonaReplyAsync(string userId, string sessionId,
        string personaName, string summary, string url)
    {
        await SendAsync(userId, new CreateNotificationRequest
        {
            Kind = "claude",
            Type = "persona_reply",
            Title = $"Новое сообщение от {personaName}",
            Body = summary,
            Url = url,
            SessionId = sessionId,
            Source = $"Чат: {personaName}",
            Tag = "Персона",
        }, sendPush: true);
    }
}
