using ClaudeHomeServer.Services.Turn;

namespace ClaudeHomeServer.Services.Tasks.Journal;

// Снятие ожидания человека ответом в чате задачи (FS-E3b, «Фичи: упрощение»).
// Подписчик вертикали Tasks на HumanMessageAccepted (издатель — приём хода TurnIntake,
// FS-E3a): человек ответил в чате — задача, к которой привязана эта сессия, больше не
// «ждёт вас». Задачи нет или ожидания нет — ничего не делать.
//
// Правила:
// - Задача находится по sessionId через TaskManager.GetBySession (LinkedSessionId),
//   без SessionManager. Чат без привязки (общий оркестраторский/ключевой/куратор) —
//   ничего не делаем.
// - Ключ владельца — из события: задачу ЧУЖОГО владельца не трогаем даже при совпадении
//   SessionId (per-owner изоляция; TurnContext.OwnerId — единственный достоверный
//   источник вне HTTP-запроса).
// - Момент ответа — время приёма события (отметки времени в самом событии нет). Он идёт
//   в ClearAwaitingHuman(id, answeredAt): ожидание, поставленное позже ответа, не
//   снимается — решение по Since держит стор под _awaitLock, а не этот подписчик.
// - События задачи (task_changed) и журнала (waiting_cleared) здесь нет: их пишет
//   существующий клир в TaskManager.
// - Сбой не роняет подписчика/хоста: Notification-шина гасит исключения подписчиков сама
//   (PublishAsync не бросает никогда), страховка try/catch держит ещё и наш собственный
//   код (GetBySession, ClearAwaitingHuman) и логирует его.
//
// Создание: IHostedService (Tasks, не Main — граница подсистем держится), регистрация
// явная в TasksSubsystem.Register (AddGatedHostedService) — по образцу
// FeatureTurnJournalSubscriber. Очереди и фоновой записи нет: снятие — in-memory правка
// стора задач с его же дебаунс-сейвом.
public sealed class AwaitingHumanAnswerSubscriber : IHostedService
{
    private readonly TaskManager _tasks;
    private readonly ILogger<AwaitingHumanAnswerSubscriber>? _log;

    public AwaitingHumanAnswerSubscriber(TaskManager tasks, ITurnEventBus bus,
        ILogger<AwaitingHumanAnswerSubscriber>? log = null)
    {
        _tasks = tasks;
        _log = log;
        // Подписка в конструкторе: экземпляр создаётся при старте хоста (IHostedService),
        // и к первому человеческому ходу подписчик уже на шине.
        bus.OnNotification<HumanMessageAccepted>(HandleHumanMessage, "Tasks.AwaitingHumanAnswer");
    }

    // IHostedService: создание при старте хоста (подписка развёрнута в конструкторе),
    // остановки ждать нечего: в фоне ничего не пишется.
    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    // Один человеческий ход → снять ожидание привязанной задачи (если оно не новее ответа).
    private Task HandleHumanMessage(HumanMessageAccepted e)
    {
        try
        {
            var sessionId = e.Turn.SessionId;
            var ownerId = e.Turn.OwnerId;
            if (string.IsNullOrEmpty(ownerId)) return Task.CompletedTask;

            // Задача, к которой привязана сессия. Чат без привязки — ничего не делаем.
            var task = _tasks.GetBySession(sessionId);
            if (task is null) return Task.CompletedTask;

            // Per-owner: задача чужого владельца не наша даже при совпадении SessionId.
            if (task.OwnerId != ownerId) return Task.CompletedTask;

            // Момент ответа — время приёма события. Ожидание, поставленное позже этого
            // момента, ответ не снимает (решение — под _awaitLock стора, см. перегрузку).
            _tasks.ClearAwaitingHuman(task.Id, DateTime.UtcNow);
        }
        catch (Exception ex)
        {
            // Сбой не роняет подписчика/хоста: шина гасит, но держим и свой код.
            _log?.LogWarning(ex, "Ожидание человека: не удалось снять ответом в чате {Session}", e.Turn.SessionId);
        }
        return Task.CompletedTask;
    }
}
