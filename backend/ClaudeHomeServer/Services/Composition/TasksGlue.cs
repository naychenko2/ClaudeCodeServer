using ClaudeHomeServer.Services.Tasks;
using ClaudeHomeServer.Services.Team;

namespace ClaudeHomeServer.Services.Composition;

// Клей Tasks-вертикали (S6-2): исполнитель задач и его швы, очередь фич, предгейт шага,
// слоты ревью, локатор деревьев проекта, раздача волн «Командной реализации» и утренний
// брифинг. Сама вертикаль задач (`TaskManager`/`TaskAiService`/`BoardService`/
// `TaskSchedulerService`) регистрируется в `TasksSubsystem`; здесь — только то, чего
// вертикаль видеть не может: Main-исполнитель (он держит чаты, сессии и очередь фич),
// Main-фасады и швы, которыми Tasks выходит в спину (ADR-014).
//
// Порядок и состав — ровно те, что были в Program.cs: `TaskExecutorAdapter` и
// `DailyBriefingRunnerAdapter` резолвят свои корни через конструктор, поэтому идут
// ПОСЛЕ них; `IFeatureHoldReleaser` остаётся рядом с исполнителем (и автоснятие
// очереди, и ручная DELETE-ручка должны работать одним телом).
public static class TasksGlue
{
    public static IServiceCollection AddTaskExecutorGlue(this IServiceCollection services, IConfiguration config)
    {
        // Планирование режима «Командная реализация» (Э2): подбор координатора/планировщика,
        // карточки кандидатов и структурный план
        services.AddSingleton<TeamPlanningService>();
        services.AddSingleton<TaskExecutionService>();
        // Шов IFeatureHoldReleaser (Core) → TaskExecutionService: и автоснятие очереди (Tasks), и ручная
        // DELETE-ручка снимают удержание одним телом. Регистрация здесь, а не в AddFeatureQueue:
        // вертикаль Tasks не видит Main; сам исполнитель регистрируется unconditional — шов с ним рядом.
        services.AddSingleton<IFeatureHoldReleaser>(sp => sp.GetRequiredService<TaskExecutionService>());
        // Швы конвейера фич (Core → Main): карточка в ключевой чат — той же доставкой, что карточки снятия
        // паузы, и состав локальных исполнителей — для счётчика в хвосте хода и пинка простоя (Tasks).
        services.AddSingleton<IKeyChatCardSender>(sp => sp.GetRequiredService<TaskExecutionService>());
        services.AddSingleton<ILocalExecutorRoster, LocalExecutorRoster>();
        // Очередь фич (волна 6): регистрируется только при включённой подсистеме задач.
        services.AddFeatureQueue(config);
        // Запуск предгейта шага (NC-P1b): шов ITaskPregate опционален — вертикаль TestRuns отключаемая
        services.AddSingleton<TaskPregateService>();
        services.AddSingleton<ReviewTreePool>();
        // ProjectTreeLocator (шаг B1b фичы «Резолвинг ссылок по worktree»): единая точка резолвинга
        // относительных путей по деревьям проекта. Git — параметр с дефолтом: при выключенной
        // подсистеме локатор остаётся рабочим (анкер + корень), без 500.
        services.AddSingleton<ProjectTreeLocator>();
        // Шов ITaskExecutor (Core) → TaskExecutorAdapter → TaskExecutionService.
        // Адаптер резолвит TaskExecutionService через конструктор, DI форвардер ниже.
        services.AddSingleton<TaskExecutorAdapter>();
        services.AddSingleton<ITaskExecutor>(sp => sp.GetRequiredService<TaskExecutorAdapter>());
        // Шов IArchitectureAgentLauncher (Core) → адаптер → TaskManager + TaskExecutionService:
        // галочка «С агентом» у сборки архитектуры ставит задачу архитектору (или без персоны).
        // У контроллера Architecture шов необязателен (нет → 503 agent_unavailable).
        services.AddSingleton<IArchitectureAgentLauncher, ArchitectureAgentLauncherAdapter>();
        // Раздача под-задач и волны режима «Командная реализация» (Э3): создание задач по плану
        // и пакетный запуск исполнителей. Конструктор вешает хук в SessionManager — сервис нужно
        // прогреть на старте (ниже), иначе «Запустить» в карточке плана осталось бы без раздачи.
        services.AddSingleton<TeamWaveService>();
        // Сторож зависших волн (Э4): без него молчаливо умерший исполнитель оставлял бы штаб
        // в стадии «волна N» навсегда
        services.AddGatedHostedService<TeamWaveWatchdog>(config);
        // DailyBriefingService — ядро композиции (Этап 5, вынос Tasks): вертикаль Tasks живёт в
        // отдельной сборке и больше не может регистрировать Main-типы. DailyBriefingService
        // остался кросс-вертикальным фасадом (Tasks+Notes+Llm) в корне Services. Шов
        // IDailyBriefingRunner (Core) → DailyBriefingRunnerAdapter → DailyBriefingService.
        // Адаптер обязан идти ПОСЛЕ DailyBriefingService — он резолвит его через конструктор.
        services.AddSingleton<DailyBriefingService>();
        services.AddSingleton<DailyBriefingRunnerAdapter>();
        services.AddSingleton<IDailyBriefingRunner>(sp => sp.GetRequiredService<DailyBriefingRunnerAdapter>());
        return services;
    }
}
