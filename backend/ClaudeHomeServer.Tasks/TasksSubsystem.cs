using ClaudeHomeServer.Services.Composition;

namespace ClaudeHomeServer.Services.Tasks;

// Подсистема задач (вертикаль Tasks, Этап 5): домен задач + доска агентов (BoardService)
// и планировщик напоминаний/автозапуска (TaskSchedulerService).
//
// `DailyBriefingService` остался в корне Services как кросс-вертикальный фасад
// (Tasks+Notes+Llm) — класть его в Notes нельзя (потянет Task), в Task нельзя
// (потянет Notes+Llm). Регистрируется отдельной строкой в Program.cs рядом с
// `PersonaAutomationService`. Шов `Tasks → IDailyBriefingRunner` (Core) —
// единственная обратная связь: TaskSchedulerService дёргает брифинг раз в сутки в
// таймзоне владельца.
//
// `TaskExecutionService` сознательно НЕ здесь (этап 4 — расщепление SessionManager):
// внутри него четыре ребра «вертикаль → вертикаль», которые allow-list не лечит.
//
// Известные границы (сознательные):
// 1) `ClaudeHomeServer.Services.Llm` — `ICheapTextRunner` для `TaskAiService`
//    (генерация описания/подзадач/классификация/нормализация/дедуп) — префикс-шов,
//    как у `Git`/`Backgrounds`/`Deploy`/`Changelog`/`ProjectIcons`.
// 2) `ClaudeHomeServer.Hubs` — `IHubContext<SessionHub>` для `TaskSchedulerService`
//    (`BroadcastTaskChangedAsync`/напоминания). Префикс-шов.
// 3) Точечные допуски к корню `ClaudeHomeServer.Services.*` — общая инфраструктура
//    («вертикаль → спинка», по аналогии с `Spend`/`Knowledge`):
//    - `SessionManager` — реализует `ISessionDirectory` (Core) — `BoardService` берёт
//      живые сессии по id;
//    - `PersonaManager` — реализует `IPersonaLookup` (Core) — `TaskManager` берёт
//      персону для лога спавна регулярной задачи;
//    - `NotificationService` — реализует `ITaskNotificationDispatcher` (Core) —
//      `TaskSchedulerService` шлёт напоминания;
//    - `ProjectEventLogService` — реализует `IProjectEventLogService` (Core) —
//      `TaskManager.LogTask` пишет событие в проектный лог (только для задач с
//      `projectId`).
// 4) Точечный допуск к `ClaudeHomeServer.Protocol.NotificationMessage` — параметр
//    public-метода `TaskSchedulerService.SendNotificationAsync`. Префикс
//    `ClaudeHomeServer.Protocol` снят (волна 3).
// 5) Шов `Tasks → Models.Session` (статика) — снят (эксперимент-4). Раньше
//    `TaskManager.ctor` мутировал статические `Session.TaskSourceSessionResolver`/
//    `TaskDoneResolver`, из чего `Session` (Core) вычислял `ParentSessionId`/`TaskDone`.
//    Теперь вычисления живут в Main'e: `SessionTaskLinks` поверх шва `ITaskLookup`
//    (Core) и `SessionWire` на точках отдачи списков — связь вертикаль → модель
//    через явный Core-порт, а не скрытую статическую запись из конструктора.
public sealed class TasksSubsystem : IAppSubsystem
{
    public string Key => "tasks";

    public string Title => "Задачи";

    public void Register(IServiceCollection services, IConfiguration config)
    {
        // Задачи: in-memory + data/tasks.json (порядок критичен — TaskManager создаётся
        // первым, потому что TaskSchedulerService и BoardService зависят от него.
        // Microsoft DI порядок регистраций не учитывает при резолве, но singleton-резолв
        // случается лениво — на первом обращении из контроллера либо hosted-сервиса;
        // к тому моменту DI гарантирует все зависимости.
        services.AddSingleton<TaskManager>();
        // Форвардер, а не вторая регистрация: иначе второй экземпляр `TaskManager`
        // перезаписывает статические резолверы на `Session` и иерархия чатов для новых задач
        // отмирает (см. DuplicateSingletonRegistrationTests).
        services.AddSingleton<ITaskStatusReader>(sp => sp.GetRequiredService<TaskManager>());
        services.AddSingleton<TaskAiService>();
        services.AddSingleton<BoardService>();
        // Шов IPersonaAutomationRunner (Core) — регистрация фабрики вынесена в
        // Program.cs рядом с `PersonaAutomationRunnerAdapter`, потому что адаптер
        // живёт в Main (обёртка над `PersonaAutomationService`).
        services.AddGatedHostedService<TaskSchedulerService>(config);
    }
}
