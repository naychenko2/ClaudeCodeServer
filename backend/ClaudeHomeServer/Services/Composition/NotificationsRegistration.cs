using ClaudeHomeServer.Services.Composition.Notifications;

namespace ClaudeHomeServer.Services.Composition;

// Регистрация уведомлений и push-доставки (S6-2): стор сообщений, сам сервис, шов
// ITaskNotificationDispatcher и подписки push. Уведомления — не вертикаль: их пишут
// задачи, знания, бэкап и выкатка (каждая — через свой шов), а доставка одна на инстанс.
// Порядок и состав — ровно те, что были в Program.cs; адаптер задач идёт ПОСЛЕ
// NotificationService (резолвит его через конструктор).
public static class NotificationsRegistration
{
    public static IServiceCollection AddNotifications(this IServiceCollection services)
    {
        services.AddSingleton<NotificationStore>();
        services.AddSingleton<NotificationService>();
        // Шов ITaskNotificationDispatcher (Core) → TaskNotificationDispatcherAdapter → NotificationService.
        // Обоснование отдельного шва (а не расширения IKnowledgeNotificationDispatcher) —
        // в ITaskNotificationDispatcher.cs: Tasks работает с готовым NotificationMessage и
        // всегда с web push; Knowledge шлёт плоские параметры и без push.
        services.AddSingleton<TaskNotificationDispatcherAdapter>();
        services.AddSingleton<ITaskNotificationDispatcher>(sp => sp.GetRequiredService<TaskNotificationDispatcherAdapter>());
        services.AddSingleton<PushSubscriptionStore>();
        services.AddSingleton<PushService>();
        return services;
    }
}
