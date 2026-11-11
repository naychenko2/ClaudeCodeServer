using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services.Composition;
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
// - Журнал (waiting_cleared) пишет существующий клир в TaskManager. Событие задачи
//   (task_changed) — здесь, после успешного снятия: ни доска, ни страница фичи не живут
//   по одному журналу — фронт обновляет их только по task_changed (lib/tasks.ts,
//   lib/featurePage.ts). Тот же путь, что FS-E2 (SessionMessagingService) и TasksToolset:
//   TaskChangedMessage("updated") через ToTaskBoard после успешного клира. Ожидание не
//   снято (клир вернул null) или задачи нет — ничего не шли.
// - Сбой не роняет подписчика/хоста: Notification-шина гасит исключения подписчиков сама
//   (PublishAsync не бросает никогда), страховка try/catch держит ещё и наш собственный
//   код (GetBySession, ClearAwaitingHuman, вещание) и логирует его.
//
// Создание: IHostedService (Tasks, не Main — граница подсистем держится), регистрация
// явная в TasksSubsystem.Register (AddGatedHostedService) — по образцу
// FeatureTurnJournalSubscriber. Очереди и фоновой записи нет: снятие — in-memory правка
// стора задач с его же дебаунс-сейвом.
public sealed class AwaitingHumanAnswerSubscriber : IHostedService
{
    private readonly TaskManager _tasks;
    private readonly ISessionBroadcaster? _broadcaster;
    private readonly ILogger<AwaitingHumanAnswerSubscriber>? _log;

    // broadcaster — шов рассылки task_changed (тот же факультативный шов, что у
    // SessionMessagingService в FS-E2): в проде зарегистрирован (CoreSeamsGlue),
    // null — только юнит-тесты без рассылки.
    public AwaitingHumanAnswerSubscriber(TaskManager tasks, ITurnEventBus bus,
        ILogger<AwaitingHumanAnswerSubscriber>? log = null, ISessionBroadcaster? broadcaster = null)
    {
        _tasks = tasks;
        _log = log;
        _broadcaster = broadcaster;
        // Подписка в конструкторе: экземпляр создаётся при старте хоста (IHostedService),
        // и к первому человеческому ходу подписчик уже на шине.
        bus.OnNotification<HumanMessageAccepted>(HandleHumanMessage, "Tasks.AwaitingHumanAnswer");
    }

    // IHostedService: создание при старте хоста (подписка развёрнута в конструкторе),
    // остановки ждать нечего: в фоне ничего не пишется.
    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    // Один человеческий ход → снять ожидание привязанной задачи (если оно не новее ответа).
    private async Task HandleHumanMessage(HumanMessageAccepted e)
    {
        try
        {
            var sessionId = e.Turn.SessionId;
            var ownerId = e.Turn.OwnerId;
            if (string.IsNullOrEmpty(ownerId)) return;

            // Задача, к которой привязана сессия. Чат без привязки — ничего не делаем.
            var task = _tasks.GetBySession(sessionId);
            if (task is null) return;

            // Per-owner: задача чужого владельца не наша даже при совпадении SessionId.
            if (task.OwnerId != ownerId) return;

            // Момент ответа — время приёма события. Ожидание, поставленное позже этого
            // момента, ответ не снимает (решение — под _awaitLock стора, см. перегрузку).
            var cleared = _tasks.ClearAwaitingHuman(task.Id, DateTime.UtcNow);

            // Снятие удалось — вещаем task_changed: payload — обновлённая задача (пара уже
            // снята), а не состояние до ответа; без события доска и страница фичи остались
            // бы на «ждёт вас» при снятом ожидании. Клир вернул null (ожидания нет / оно
            // новее ответа) — события нет: «updated» без изменения не шлём.
            if (cleared is not null && _broadcaster is not null)
                await _broadcaster.ToTaskBoard(cleared.OwnerId!, cleared.ProjectId,
                    new TaskChangedMessage("updated", cleared));
        }
        catch (Exception ex)
        {
            // Сбой не роняет подписчика/хоста: шина гасит, но держим и свой код
            // (и вещание — внутри того же try/catch).
            _log?.LogWarning(ex, "Ожидание человека: не удалось снять ответом в чате {Session}", e.Turn.SessionId);
        }
    }
}
