using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services.Access;
using ClaudeHomeServer.Services.Composition;

namespace ClaudeHomeServer.Services.Tasks;

// Фоновый планировщик задач: напоминания к сроку и автозапуск Claude-исполнителя.
// Один на приложение, тик каждые 30 с. Идемпотентность — отметки на самой задаче
// (ReminderSentAt, ClaudeStartedAt), переживают рестарт сервера.
public class TaskSchedulerService(
    TaskManager tasks,
    IUserStore users,
    ISessionBroadcaster broadcaster,
    ITaskExecutor executor,
    IDailyBriefingRunner briefing,
    IPersonaAutomationRunner automation,
    ITaskNotificationDispatcher notif,
    ILogger<TaskSchedulerService> log,
    // Нужен, чтобы адресат напоминания был действующим участником проекта (отозванный
    // участник уведомлений не получает). null — проверка недоступна, адресат — владелец
    // (fail-closed: без резолва доступа чужому человеку не пишем).
    IProjectAccess? access = null) : BackgroundService
{
    // Подписчик ходов фичи (Б8c) больше НЕ побочный эффект этого hosted-сервиса: он
    // регистрируется явным AddGatedHostedService в TasksSubsystem.Register (Tasks, не Main).
    private readonly object _reminderLock = new();
    private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(30);
    // Автозапуск только для сроков, наступивших недавно: защита от лавины сессий
    // по старым просроченным задачам при включении флага или долгом простое сервера
    internal static readonly TimeSpan AutoStartWindow = TimeSpan.FromHours(24);

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TickInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
            {
                try { await TickAsync(DateTime.UtcNow); }
                catch (Exception ex) { log.LogError(ex, "Ошибка тика планировщика задач"); }
            }
        }
        catch (OperationCanceledException) { /* остановка приложения */ }
    }

    // Публичный для юнит-тестов: один проход по всем пользователям
    public async Task TickAsync(DateTime nowUtc)
    {
        foreach (var user in users.GetAll())
        {
            var tz = TaskDueCalculator.ResolveTimeZone(user.TimeZone);
            foreach (var task in tasks.GetByOwner(user.Id))
            {
                if (task.Status == TaskItemStatus.Done) continue;
                await ProcessReminderAsync(task, tz, nowUtc);
                await ProcessClaudeAutoStartAsync(task, tz, nowUtc);
                await ProcessStalledExecutorAsync(task, nowUtc);
            }
            await ReconcileFeatureQueuesAsync(user.Id);
            // Утренний бриф раз в день в таймзоне юзера (быстрый выход, если не время/выключено)
            await briefing.MaybeRunScheduledAsync(user, tz, nowUtc);
            // Проактивность персон (collaborator): оценка правил автоматизаций этого пользователя
            await automation.MaybeRunAutomationsAsync(user, tz, nowUtc, CancellationToken.None);
        }
    }

    // Чистые предикаты выбора задач — извлечены из Process*-методов для юнит-тестов

    // Пора ли напоминать: напоминание настроено, ещё не отправлялось и момент наступил
    internal static bool ShouldRemind(TaskItem task, TimeZoneInfo tz, DateTime nowUtc)
    {
        if (task.ReminderSentAt is not null) return false;
        var remindAt = TaskDueCalculator.ReminderMomentUtc(task, tz);
        return remindAt is not null && remindAt <= nowUtc;
    }

    // Пора ли автозапускать исполнителя: assignee=Claude, ещё не запускалась,
    // срок наступил и не старше AutoStartWindow (защита от лавины по старым задачам)
    internal static bool ShouldAutoStart(TaskItem task, TimeZoneInfo tz, DateTime nowUtc)
    {
        if (task.Assignee != TaskItemAssignee.Claude) return false;
        if (task.Status != TaskItemStatus.Todo || task.ClaudeStartedAt is not null) return false;
        // Задача уже ждёт устройство локального проекта: запустит её DeviceOnlineDispatcher,
        // потолок ожидания считается от постановки в ожидание, а не от срока (ADR-016, план §5)
        if (task.DeviceWaitSince is not null) return false;

        var dueUtc = TaskDueCalculator.DueMomentUtc(task, tz);
        return dueUtc is not null && dueUtc <= nowUtc && nowUtc - dueUtc <= AutoStartWindow;
    }

    // Отличает отказ по правилу (пауза или гейт маршрута — W4-HOLD-G, B3) от падения автозапуска
    // (R-HOLD-G, находка 6; W4-HOLD-I): по источнику — типу, который поднимает сам гейт, — а не
    // пересчётом предиката: plain InvalidOperationException («У задачи нет владельца») рядом с
    // удержанной фичей — не отказ по правилу, и не должен попадать в тихую ветку. Два отказа по
    // правилу различаются в логе по типу (Ф-2.24): лог не должен называть отказ маршрута паузой.
    internal static bool IsHoldRefusal(Exception ex) => ex is FeatureHoldException or FeatureRouteLaunchException;

    // Автозапуск Claude-исполнителя в момент срока: assignee=Claude, ещё не запускалась.
    // Устройство локального проекта офлайн — исполнитель сам ставит задачу в «ждёт устройство»
    private async Task ProcessClaudeAutoStartAsync(TaskItem task, TimeZoneInfo tz, DateTime nowUtc)
    {
        if (!ShouldAutoStart(task, tz, nowUtc)) return;

        try
        {
            // Автозапуск без инициатора: ход наследует создателя задачи, а не «неизвестного»
            // (иначе гейт актора открыл бы исполнителю права арендатора — F-9)
            await executor.ExecuteAsync(task, auto: true, TaskLaunchActor.For(task));
        }
        catch (Exception ex)
        {
            if (IsHoldRefusal(ex))
            {
                // Оба отказа — правило, а не поломка: задачу не запускаем намеренно, лог тихий
                // и без трейса, чтобы штатные отказы не тонили реальные провалы автозапуска.
                // Но запись называет, что именно отказало: отказ по гейту маршрута — не пауза,
                // звать его паузой значит врать в расследовании (Ф-2.24).
                if (ex is FeatureRouteLaunchException)
                    log.LogInformation("Автозапуск пропущен по гейту маршрута фичи: задача {TaskId} «{Title}» — {Reason}",
                        task.Id, task.Title, ex.Message);
                else
                    log.LogInformation("Автозапуск пропущен по паузе фичи: задача {TaskId} «{Title}» — {Reason}",
                        task.Id, task.Title, ex.Message);
            }
            else
            {
                log.LogWarning(ex, "Автозапуск Claude-исполнителя не удался: задача {TaskId} «{Title}»",
                    task.Id, task.Title);
            }
        }
    }

    // Страховка «ход кончился, а задачу никто не закрыл»: решение принимает
    // TaskExecutionService (там же отметки о шагах), тик только зовёт его по каждой задаче.
    // Сбой одной задачи не должен рвать проход по остальным — как у автозапуска выше.
    private async Task ProcessStalledExecutorAsync(TaskItem task, DateTime nowUtc)
    {
        try
        {
            await executor.CheckStalledExecutorAsync(task, nowUtc);
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Проверка незакрытой задачи {TaskId} «{Title}» не удалась",
                task.Id, task.Title);
        }
    }

    // Сверка очереди фич (волна 6, И11): слот свободен, а голова очереди ждёт — события потеряны
    // или пришли до рестарта. Решение принимает сервис очереди; сбой не рвёт тик.
    private async Task ReconcileFeatureQueuesAsync(string ownerId)
    {
        try
        {
            await executor.ReconcileFeatureQueuesAsync(ownerId);
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Сверка очереди фич владельца {OwnerId} не удалась", ownerId);
        }
    }

    private async Task ProcessReminderAsync(TaskItem task, TimeZoneInfo tz, DateTime nowUtc)
    {
        // Проверка и отметка — одна критическая секция: два тика (ручной вызов и таймер)
        // не должны оба пройти ShouldRemind до того, как первый выставит ReminderSentAt.
        // Срок считаем в таймзоне АДРЕСАТА (исполнителя), а не владельца; нет данных — владельца.
        var recipient = ResolveRecipient(task);
        if (recipient != task.OwnerId
            && users.GetById(recipient)?.TimeZone is { } recipientZone)
            tz = TaskDueCalculator.ResolveTimeZone(recipientZone);

        TaskItem? updated;
        lock (_reminderLock)
        {
            if (!ShouldRemind(task, tz, nowUtc)) return;
            updated = tasks.MarkReminderSent(task.Id, nowUtc);
        }
        if (updated is null) return; // задача удалена между чтением и отметкой

        var dueText = task.DueTime is null ? task.DueDate : $"{task.DueDate} {task.DueTime}";
        await SendNotificationAsync(recipient, new NotificationMessage(
            Title: "Напоминание о задаче",
            Body: $"{updated.Title} — срок {dueText}",
            Url: TaskUrl.Of(updated),
            Kind: "reminder"));
        // Синхронизируем сторы клиентов (ReminderSentAt изменился)
        await broadcaster.ToTaskBoard(updated.OwnerId!, updated.ProjectId, new TaskChangedMessage("updated", updated));

        log.LogInformation("Напоминание отправлено: задача {TaskId} «{Title}»", updated.Id, updated.Title);
    }

    // Адресат напоминания: исполнитель-человек, пока он ДЕЙСТВУЮЩИЙ участник проекта задачи;
    // отозванный участник (как и личная/Claude-задача) — владелец. Та же политика доступа
    // (IProjectAccess.Resolve), что у MyTasksQuery: назначение отозванного не даёт ни напоминаний, ни выдачи.
    private string ResolveRecipient(TaskItem task)
    {
        var owner = task.OwnerId!;
        var assignee = TaskManager.ResolveMeUserId(task);
        if (assignee is null || assignee == owner) return owner;
        if (task.ProjectId is null) return owner;
        return access?.Resolve(assignee, task.ProjectId, ProjectRight.Read) is null ? owner : assignee;
    }

    // Тост + сторадж + web push через единый NotificationService. Адресат — человек-исполнитель
    // (общий проект: участник; личные и Claude-задачи — владелец, как раньше). Один адресат —
    // одно уведомление: владельцу параллельно не дублируем.
    private async Task SendNotificationAsync(string recipient, NotificationMessage message)
    {
        await notif.SendNotificationMessageAsync(recipient, message, sendPush: true);
    }

    // Hash-диплинк на задачу вынесен в Core (`Services/TaskUrl.Of`) — Этап 5, вынос Tasks.
}
