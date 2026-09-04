using ClaudeHomeServer.Services.Composition;

namespace ClaudeHomeServer.Services.Tasks;

// Подсистема задач (вертикаль Tasks, волна 4C, шаг 1): домен задач + доска агентов
// (BoardService) и утренний брифинг (DailyBriefingService). `TaskExecutionService`
// сознательно НЕ здесь (этап 4 — расщепление SessionManager): внутри него четыре
// ребра «вертикаль → вертикаль», которые allow-list не лечит.
//
// Известные границы (сознательные):
// 1) `ClaudeHomeServer.Services.Llm.ICheapTextRunner` — префикс-шов (как у
//    `Backgrounds`/`Git`/`Deploy`/`Changelog`/`ProjectIcons`): TaskAiService генерирует
//    описание/подзадачи/классификацию/нормализацию заголовка/дедуп через дешёвую
//    модель общего назначения; LLM-инфраструктура общего назначения, используемая и
//    другими разделами (теги заметок, сводки, память).
// 2) `ClaudeHomeServer.Services.Hubs` — префикс-шов: `TaskSchedulerService` шлёт
//    `BroadcastTaskChangedAsync` и напоминания, `DailyBriefingService` тоже пишет в
//    ленту через `IHubContext<SessionHub>` (событие `task_reminder`).
// 3) Точечные допуски к корню `ClaudeHomeServer.Services.*` — общая инфраструктура
//    («вертикаль → спинка», аналогично `Dossiers`/`Git`/`Spend`/`Knowledge`):
//    - `SessionManager` — `TaskSchedulerService` цикл «до готово» вызывает
//      `TaskExecutionService` (вне этой вертикали) для автозапуска исполнителя;
//      `BoardService` берёт живые сессии; `TaskAiService`/`DailyBriefingService` —
//      косвенно через ownerId.
//    - `ProjectManager` — `TaskAiService` берёт контекст проекта (CLAUDE.md);
//      `DailyBriefingService` перебирает проекты владельца для git-активности.
//    - `UserStore` — `TaskSchedulerService` и `DailyBriefingService` перебирают
//      пользователей; `TaskAiService` берёт таймзону владельца.
//    - `UserHomeResolver` — `TaskSchedulerService` берёт путь владельца (резолв
//      корня для git-активности по проектам).
//    - `NotificationService` — `TaskSchedulerService` шлёт напоминания
//      (`SendNotificationMessageAsync`), `DailyBriefingService` шлёт «доброе утро».
//    - `AppSettingsService` — `DailyBriefingService` читает гейт
//      `DailyBriefingEnabled` инстанса.
//    - `ProjectEventLogService` — `TaskManager.LogTask` пишет событие в проектный лог
//      (только для задач с projectId); `DailyBriefingService` читает события за
//      сутки для команды.
//    - `PersonaManager` — `TaskManager` пишет `personas` (опциональная зависимость
//      для лога), `BoardService` берёт персоны для подписи колонки,
//      `DailyBriefingService` ищет персона-секретаря для голоса брифа.
//    - `PushService` — `DailyBriefingService` шлёт web-push по расписанию.
// 4) Точечный допуск к `NotesService` (Services/ корень — следующий шаг волны 4C,
//    пока остаётся в корне): `DailyBriefingService` пишет в дневниковую заметку
//    (`GetOrCreateDaily`/`Update`). После переезда Notes — обновить FullName.
// 5) Точечные допуски к `ClaudeHomeServer.Protocol.*` — типы WS-сообщений для
//    расписания (NotificationMessage в сигнатуре `TaskSchedulerService.SendNotificationAsync`).
//    Префикс `ClaudeHomeServer.Protocol` снят (волна 3), но материал-аргумент
//    `NotificationMessage` остаётся в публичной сигнатуре — оставляю один тип.
// 6) Шов `Tasks → Models.Session`: `TaskManager.ctor` строки 32-36 мутируют три
//    статических резолвера на `Session` (`TaskSourceSessionResolver`,
//    `TaskDelegationDepthResolver`, `TaskDoneResolver`) — зависят
//    `Session.ParentSessionId`, `Session.TaskDelegationDepth` (гейт `TASKS_EXECUTE`)
//    и `Session.TaskDone` (фильтр чатов «Готово»). Связь из тела конструктора,
//    рефлексией не контролируется; зафиксирована в allow-list комментарием ниже.
//    Контракт: `TaskManager` создаётся раньше первой сериализации `Session` (он
//    singleton, инстанс живёт весь процесс, никаких поздних Lazy).
// 7) Шов Tasks → SessionMessagingService/SessionContextResolver/UnifiedSearchService
//    (Services/ корень) — допуск через `using TaskManager = ClaudeHomeServer.Services.Tasks.TaskManager`
//    или прямое полное имя в ctor-параметре, рефлексия сторожей видит только
//    сигнатуры public-методов: каждый из этих трёх core-синглтонов получает
//    `TaskManager` параметром. После переезда обновляем полное имя.
public sealed class TasksSubsystem : IAppSubsystem
{
    public string Key => "tasks";

    public string Title => "Задачи и брифинг";

    public void Register(IServiceCollection services, IConfiguration config)
    {
        // Задачи: in-memory + data/tasks.json (порядок критичен — TaskManager создаётся
        // первым, потому что TaskSchedulerService, BoardService и DailyBriefingService
        // зависят от него. Microsoft DI порядок регистраций не учитывает при резолве,
        // но singleton-резолв случается лениво — на первом обращении из контроллера
        // либо hosted-сервиса; к тому моменту DI гарантирует все зависимости).
        services.AddSingleton<TaskManager>();
        services.AddSingleton<TaskAiService>();
        services.AddSingleton<BoardService>();
        services.AddSingleton<DailyBriefingService>();
        services.AddGatedHostedService<TaskSchedulerService>(config);
    }
}
