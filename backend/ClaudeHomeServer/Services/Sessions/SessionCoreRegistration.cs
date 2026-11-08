using ClaudeHomeServer.Services.Llm;
using ClaudeHomeServer.Services.Sessions;
using ClaudeHomeServer.Services.Team;

// Namespace намеренно Composition, а не Sessions: Program.cs уже импортирует Composition (его
// строка остаётся ровно одной). Сторож формы ядра допускает ссылки на типы Services.Sessions
// только из SessionManager, самих Services.Sessions, этой точки сборки (CoreCompositionRoots,
// полный доступ) и поимённого списка вызывающих за ядром (CoreDataReferrerAllowList, только
// типы-данные). Именно из-за пространства имён Composition файл правилами 1–3 и 5 не охвачен:
// они берут типы по namespace Services.Sessions.
namespace ClaudeHomeServer.Services.Composition;

// Регистрация ядра сессий: роли, вынесенные из SessionManager (docs/research/session-manager-split-2026-11.md).
// Все классы — синглтоны (у SessionManager тоже синглтон-время жизни, а состояние общее).
// Каждый следующий шаг S3 дописывает свои классы сюда же; в Program.cs — одна строка AddSessionCore().
public static class SessionCoreRegistration
{
    public static IServiceCollection AddSessionCore(this IServiceCollection services)
    {
        // Стор метаданных сессий (S3-1): единственный реестр сессий процесса. С S3-8c — активация
        // типа: ни один конструктор (фасада или регистрации) не строит стор запасным экземпляром,
        // единственная его сборка — здесь, из контейнера. ITaskLookup (адаптер TaskLookupAdapter
        // над TaskManager) уже в DI — регистрация в Program.cs для Spend; второй регистрации
        // того же адаптера не заводим: её ловит DuplicateSingletonRegistrationTests.
        services.AddSingleton<SessionStore>();

        // --- Роли ядра (S3-2) ---
        // Шов «очередь → приём хода»: ленивый — прямая зависимость дала бы цикл
        // PendingQueueService → приём хода → очередь. С S3-7 (аддендум §1) шов резолвит ядро
        // приёма TurnIntake, а не фасад: ход в сессии запускает только оно; на резолве ленивого
        // шва ядро ещё не строится, цикл разрывается отложенным вызовом (S3-0 §3).
        services.AddSingleton(sp => new Lazy<ITurnStarter>(() => sp.GetRequiredService<TurnIntake>()));
        // Переходный шов цикла «до готово» (S3-7): продолжение и добивание не стартуют ход сами
        // и не видят шов приёма; реализует его SessionManager — тело цикла на фасаде до шага 2,
        // там шов снимается (продолжение зовёт ядро через TrySubmitContinuation).
        services.AddSingleton(sp => new Lazy<IWorkLoopContinuation>(() => sp.GetRequiredService<SessionManager>()));

        // Стор копий транскриптов архивных чатов — общий экземпляр с ролями: без него
        // CliRoute собрал бы свой. Рекордер лимитов подписок (SubscriptionLimitRecorder)
        // здесь НЕ регистрируем: единственная его регистрация — в слое Llm (LlmSubsystem),
        // её же резолвят шлюз LLM и фасад. ТРЕБОВАНИЕ: TurnCostLedger ниже берёт его через
        // GetRequiredService, поэтому контейнер, собранный одним AddSessionCore() БЕЗ слоя
        // Llm, не поднимется — потребитель обязан подключить и LlmSubsystem (на этом упало
        // слияние ветки тестов, 73b4a4184, починено 30017de49). Прежняя фабричная регистрация
        // здесь была ПЕРВОЙ по порядку (Program.cs), type-based из LlmSubsystem — последней,
        // и побеждал он («последний дескриптор побеждает»); пара дескрипторов и составляла
        // дубль. Его ловит SingleRegistrationServices_ОдинДескрипторТипомБезФабрики
        // (общий сторож дублей фабрик не видит — слепота названа в его файле).
        services.AddSingleton(sp => new ArchivedTranscriptStore(sp.GetRequiredService<IConfiguration>()));

        services.AddSingleton(sp => new SessionHistory(
            sp.GetRequiredService<ChatHistoryService>(),
            sp.GetRequiredService<SessionStore>(),
            sp.GetRequiredService<ILogger<SessionHistory>>()));
        // ISpendCollector опционален (Spend-подсистема может не быть): та же семантика, что у
        // параметра SessionManager — без него аналитика расхода просто не пишется.
        // LlmProviderRegistry — с S3-6: запись расхода хода (RecordTurnSpend) переехала в
        // журнал цен вместе с телом конца хода TurnCompletion.
        services.AddSingleton(sp => new TurnCostLedger(
            sp.GetRequiredService<FalCostService>(),
            sp.GetRequiredService<GlifAccountService>(),
            sp.GetService<Spend.ISpendCollector>(),
            sp.GetRequiredService<SubscriptionLimitRecorder>(),
            sp.GetRequiredService<SessionHistory>(),
            sp.GetRequiredService<ChatHistoryService>(),
            sp.GetRequiredService<SessionStore>(),
            sp.GetRequiredService<LlmProviderRegistry>(),
            sp.GetRequiredService<ILogger<TurnCostLedger>>()));
        services.AddSingleton(sp => new PendingQueueService(
            sp.GetRequiredService<SessionStore>(),
            sp.GetRequiredService<ChatHistoryService>(),
            sp.GetRequiredService<SessionHistory>(),
            sp.GetRequiredService<Lazy<ITurnStarter>>(),
            sp.GetRequiredService<ILogger<PendingQueueService>>()));
        services.AddSingleton(sp => new CliRoute(
            sp.GetRequiredService<LlmProviderRegistry>(),
            sp.GetRequiredService<ClaudeSubscriptionPool>(),
            sp.GetRequiredService<ModelAssignmentResolver>(),
            sp.GetRequiredService<UserStore>(),
            sp.GetRequiredService<UserHomeResolver>(),
            sp.GetRequiredService<ClaudeHomeServer.Services.Execution.ILauncherFactory>(),
            sp.GetRequiredService<ClaudeHomeServer.Services.Execution.SandboxManager>(),
            sp.GetRequiredService<ProjectManager>(),
            sp.GetRequiredService<SessionStore>(),
            sp.GetRequiredService<ArchivedTranscriptStore>(),
            sp.GetRequiredService<ILogger<CliRoute>>()));

        // --- Рабочая папка чата (S3-3) ---
        // Прогрев сборки свежего worktree: общий синглтон с ChatWorktree (раньше создавался
        // в конструкторе SessionManager; в конструкторе остаётся только его запасная сборка).
        services.AddSingleton(sp => new WorktreeBuildWarmup(
            sp.GetRequiredService<ClaudeHomeServer.Services.Execution.ILauncherFactory>(),
            sp.GetRequiredService<IConfiguration>(),
            sp.GetRequiredService<ILogger<WorktreeBuildWarmup>>()));
        // git/граф/watcher'ы — GetService: та же опциональность, что у параметров
        // SessionManager (без них worktree-фича и уборка выключены).
        services.AddSingleton(sp => new ChatWorktree(
            sp.GetService<Git.GitService>(),
            sp.GetService<CodeGraph.CodeGraphService>(),
            sp.GetService<CodeGraph.ICodeGraphInspector>(),
            sp.GetService<FileWatcherService>(),
            sp.GetRequiredService<WorktreeBuildWarmup>(),
            sp.GetRequiredService<ProjectManager>(),
            sp.GetRequiredService<SessionStore>(),
            sp.GetRequiredService<CliRoute>(),
            sp.GetRequiredService<ILogger<ChatWorktree>>()));

        // --- Жизненный цикл чата: смена провайдера (S3-3) ---
        // Шов «роль → штаб»: ленивый на синглтон SessionManager — в переходный режим его
        // ITeamNotifier реализует фасад (S3-0 §3); прямая зависимость дала бы цикл
        // «ядро → штаб → ядро» (RestoreUserMode → SetMode → ...).
        services.AddSingleton(sp => new Lazy<ITeamNotifier>(() => sp.GetRequiredService<SessionManager>()));
        services.AddSingleton(sp => new ProviderMigration(
            sp.GetRequiredService<SessionStore>(),
            sp.GetRequiredService<CliRoute>(),
            sp.GetRequiredService<LlmProviderRegistry>(),
            sp.GetRequiredService<ClaudeSubscriptionPool>(),
            sp.GetRequiredService<Lazy<ITeamNotifier>>(),
            sp.GetRequiredService<ILogger<ProviderMigration>>()));
        services.AddSingleton(sp => new ChatNaming(
            sp.GetRequiredService<SessionStore>(),
            sp.GetRequiredService<SessionHistory>(),
            sp.GetRequiredService<TurnCostLedger>(),
            sp.GetService<ICheapTextRunner>(),
            sp.GetRequiredService<IConfiguration>(),
            sp.GetRequiredService<ProjectManager>(),
            sp.GetRequiredService<ILogger<ChatNaming>>()));

        // --- Видимость чата (S3-3) ---
        // Шов «видимость → привязка чатов фиче»: ленивый на зарегистрированный FeatureChatLinks
        // (его конструктор зависит от ISessionDirectory — адаптера над SessionManager, поэтому
        // прямая зависимость дала бы цикл «ядро → Tasks → ядро»). Резолв — на первом обращении,
        // когда контейнер уже собран.
        services.AddSingleton(sp => new Lazy<IFeatureChatLinks>(() => sp.GetRequiredService<IFeatureChatLinks>()));
        services.AddSingleton(sp => new ChatVisibility(
            sp.GetRequiredService<SessionStore>(),
            sp.GetRequiredService<ChatHistoryService>(),
            sp.GetRequiredService<ProjectManager>(),
            sp.GetRequiredService<ISessionBroadcaster>(),
            sp.GetRequiredService<Lazy<IFeatureChatLinks>>(),
            sp.GetRequiredService<ILogger<ChatVisibility>>()));

        // --- Настройки и жизненный цикл чата (S3-3) ---
        // Зависимости ровно те, что в таблице S3-0 §1 (десять). ISphereDirectory — GetService:
        // та же опциональность, что у параметра SessionManager (без подсистемы сфер зона
        // персоны работает через NoSphereDirectory, как и раньше).
        services.AddSingleton(sp => new ChatSettings(
            sp.GetRequiredService<SessionStore>(),
            sp.GetRequiredService<SessionHistory>(),
            sp.GetRequiredService<CliRoute>(),
            sp.GetRequiredService<ProviderMigration>(),
            sp.GetRequiredService<ProjectManager>(),
            sp.GetRequiredService<LlmProviderRegistry>(),
            sp.GetRequiredService<ModelAssignmentResolver>(),
            sp.GetRequiredService<ClaudeSubscriptionPool>(),
            sp.GetRequiredService<PersonaManager>(),
            sp.GetService<Spheres.ISphereDirectory>(),
            sp.GetRequiredService<ILogger<ChatSettings>>()));
        // --- Контекст запуска (S3-4) ---
        // Адрес и токен MCP: их читают все сборщики контекстов, поэтому они первыми.
        services.AddSingleton(sp => new McpEndpoint(
            sp.GetRequiredService<IConfiguration>(),
            sp.GetRequiredService<Microsoft.AspNetCore.Hosting.Server.IServer>(),
            sp.GetRequiredService<JwtService>(),
            sp.GetRequiredService<ClaudeHomeServer.Services.Execution.ILauncherFactory>(),
            sp.GetRequiredService<ILogger<McpEndpoint>>()));
        // Встроенные продуктовые MCP-серверы хода. IServiceProvider — единственный в ядре
        // (allow-list правила 5 сторожа формы): ленивый резолв McpToolsetRegistry и
        // IDeviceExecChannel, прямая зависимость замкнула бы граф синглтонов. Опциональные
        // — GetService: та же семантика, что у параметров SessionManager (подсистема или
        // OAuth-сервис могут не быть в DI).
        services.AddSingleton(sp => new BuiltinMcpContexts(
            sp.GetRequiredService<McpEndpoint>(),
            sp.GetRequiredService<FeatureFlagService>(),
            sp.GetRequiredService<PersonaBindingsService>(),
            sp,
            sp.GetService<ClaudeHomeServer.Services.Composition.SubsystemStateStore>(),
            sp.GetRequiredService<ProjectManager>(),
            sp.GetService<ClaudeHomeServer.Services.Mcp.HiggsfieldOAuthService>(),
            sp.GetRequiredService<IConfiguration>()));
        // Слой персоны. ISphereDirectory — GetService: без подсистемы сфер зона персоны
        // работает через NoSphereDirectory, как и раньше.
        services.AddSingleton(sp => new PersonaLayerBuilder(
            sp.GetRequiredService<SessionStore>(),
            sp.GetRequiredService<McpEndpoint>(),
            sp.GetRequiredService<PersonaManager>(),
            sp.GetRequiredService<PersonaBindingsService>(),
            sp.GetService<PersonaAgentFileSync>(),
            sp.GetRequiredService<ProjectManager>(),
            sp.GetService<Spheres.ISphereDirectory>(),
            sp.GetRequiredService<ILogger<PersonaLayerBuilder>>()));
        // Внешние серверы владельца и рабочий пространство. Реестр/секреты/OAuth — GetService:
        // без них реестровых серверов в ход нет (та же опциональность, что у параметров фасада).
        services.AddSingleton(sp => new ExternalMcpContexts(
            sp.GetRequiredService<SessionStore>(),
            sp.GetRequiredService<McpEndpoint>(),
            sp.GetRequiredService<PersonaBindingsService>(),
            sp.GetRequiredService<ProjectManager>(),
            sp.GetService<ClaudeHomeServer.Services.Mcp.McpRegistry>(),
            sp.GetService<ClaudeHomeServer.Services.Mcp.McpSecretStore>(),
            sp.GetService<ClaudeHomeServer.Services.Mcp.McpOAuthService>(),
            sp.GetRequiredService<FeatureFlagService>(),
            sp.GetRequiredService<UserStore>(),
            sp.GetRequiredService<IConfiguration>(),
            sp.GetRequiredService<ILogger<ExternalMcpContexts>>()));
        // Модули — отдельная роль: с ними у ExternalMcpContexts было бы двенадцать
        // зависимостей (правило 2 сторожа — резать класс, не поднимать лимит).
        services.AddSingleton(sp => new ModulesMcpContexts(
            sp.GetRequiredService<McpEndpoint>(),
            sp.GetRequiredService<UserStore>(),
            sp.GetRequiredService<FeatureFlagService>(),
            sp.GetService<ClaudeHomeServer.Services.Modules.ModuleRegistry>(),
            sp.GetService<ClaudeHomeServer.Services.Modules.ModuleTokenService>()));
        // Паспорта хода и сабагентов: подписки на шину ставит сам TurnPassports. Шина — тот же
        // синглтон, что читает фасад (регистрация в Program.cs); шов IWorkLoopContinuation —
        // ленивый, иначе цикл «паспорта → приём хода → паспорта»; S3-7: добивание просит
        // продолжение цикла, а не старт хода — с общего шва приёма оно снято.
        services.AddSingleton(sp => new TurnPassports(
            sp.GetRequiredService<ClaudeHomeServer.Services.Turn.ITurnEventBus>(),
            sp.GetService<PromptSnapshotStore>(),
            sp.GetService<ClaudeHomeServer.Services.Mcp.McpStatusStore>(),
            sp.GetService<ClaudeHomeServer.Services.Llm.Claude.SubagentRunLog>(),
            sp.GetService<ClaudeHomeServer.Services.Llm.TurnRunLog>(),
            sp.GetRequiredService<SessionStore>(),
            sp.GetRequiredService<Lazy<IWorkLoopContinuation>>(),
            sp.GetServices<ClaudeHomeServer.Services.Turn.IPromptSectionContributor>()));

        // --- Конец хода (S3-6) ---
        // Разбор сообщений адаптера: аккумулятор, статусы, гейты прогона, запуск очереди и
        // цикла, фолбэк хода (ErrorMessage без последующего result → Status=Error). Очередь
        // рассылки переезжает с тем же ключом сериализации — по id сессии (entry.Pipeline),
        // а не по экземпляру класса.
        services.AddSingleton(sp => new TurnCompletion(
            sp.GetRequiredService<SessionStore>(),
            sp.GetRequiredService<ChatHistoryService>(),
            sp.GetRequiredService<CliRoute>(),
            sp.GetRequiredService<ChatWorktree>(),
            sp.GetRequiredService<ProviderMigration>(),
            sp.GetRequiredService<TurnCostLedger>(),
            sp.GetRequiredService<Lazy<ITurnStarter>>(),
            sp.GetRequiredService<Lazy<IWorkLoopContinuation>>(),
            sp.GetRequiredService<Lazy<ITeamNotifier>>(),
            sp.GetRequiredService<ILogger<TurnCompletion>>()));
        // Шов «сообщения процесса → разбор ленты»: ленивый на TurnCompletion — с S3-6 его
        // владелец не фасад. Меняется ровно эта строка и одна строка запасной сборки в
        // конструкторе SessionManager (S3-5 → S3-6).
        services.AddSingleton(sp => new Lazy<ITurnMessageSink>(() => sp.GetRequiredService<TurnCompletion>()));
        // Хост процесса CLI: старт/переподключение, «Стоп», ответы на карточки, ожидание
        // устройства. Фабрика контекста — internal-тип (её сигнатура держит internal-шов
        // ITurnMessageSink с internal TurnAccumulator), отдельным синглтоном она не становится:
        // единственный её потребитель — этот хост, и она строится здесь же. IPluginDirSource —
        // GetService: без подсистемы Plugins ход идёт без --plugin-dir (та же опциональность,
        // что у параметра фасада). IProjectDeviceGate — GetService: без неё ожидания устройства
        // нет (тесты).
        services.AddSingleton(sp => new TurnProcessHost(
            sp.GetRequiredService<ILlmSessionAdapterFactory>(),
            new TurnLaunchContextFactory(
                sp.GetRequiredService<BuiltinMcpContexts>(),
                sp.GetRequiredService<PersonaLayerBuilder>(),
                sp.GetRequiredService<ExternalMcpContexts>(),
                sp.GetRequiredService<ModulesMcpContexts>(),
                sp.GetRequiredService<ClaudeHomeServer.Services.Execution.ILauncherFactory>(),
                sp.GetService<ClaudeHomeServer.Services.Plugins.IPluginDirSource>(),
                sp.GetRequiredService<IConfiguration>(),
                sp.GetRequiredService<SessionStore>(),
                sp.GetRequiredService<Lazy<ITurnMessageSink>>(),
                sp.GetRequiredService<Lazy<ITurnStarter>>()),
            sp.GetRequiredService<SessionStore>(),
            sp.GetRequiredService<SessionHistory>(),
            sp.GetRequiredService<CliRoute>(),
            sp.GetRequiredService<PendingQueueService>(),
            sp.GetService<ClaudeHomeServer.Services.Execution.IProjectDeviceGate>(),
            sp.GetRequiredService<ProjectManager>(),
            sp.GetRequiredService<Lazy<ITurnStarter>>(),
            sp.GetRequiredService<ClaudeHomeServer.Services.Turn.ITurnEventBus>(),
            sp.GetRequiredService<ILogger<TurnProcessHost>>()));
        // Локальный голосовой ход (S3-7): общий синглтон фасада и ядра приёма — два экземпляра
        // дали бы два состояния голосовой ветки. Router/ollama — GetService: та же
        // опциональность, что у параметров фасада (без них локальной ветки нет, разговор
        // идёт через CLI, как раньше).
        services.AddSingleton(sp => new LocalVoiceTurn(
            sp.GetService<ClaudeHomeServer.Services.Llm.LocalActionRouter>(),
            sp.GetService<ClaudeHomeServer.Services.Llm.ILocalLlmClient>(),
            sp.GetRequiredService<PersonaManager>(),
            sp.GetRequiredService<SessionStore>(),
            sp.GetRequiredService<TurnCompletion>()));
        // Ядро приёма хода (S3-7, аддендум Александра §1): ход в сессии запускает только оно;
        // реализует шов ITurnStarter и штабной Team.ITeamTurnIntake. Зависимости ровно десять
        // (правило 2 сторожа): стор, слой персоны, очередь, хост процесса, конец хода, настройки,
        // именование, голос, ленивый шов штаба, конфиг. Вызов цикла (пустая очередь при активном
        // цикле) — переходный хук OnLoopContinueRequested, его ставит фасад (тело цикла на фасаде
        // до шага 2); конструктор internal — сам класс public только как параметр публичного
// конструктора фасада (CS0051); конструктор зовёт его только отсюда и из запасной сборки.
        services.AddSingleton(sp => new TurnIntake(
            sp.GetRequiredService<SessionStore>(),
            sp.GetRequiredService<PersonaLayerBuilder>(),
            sp.GetRequiredService<PendingQueueService>(),
            sp.GetRequiredService<TurnProcessHost>(),
            sp.GetRequiredService<TurnCompletion>(),
            sp.GetRequiredService<ChatSettings>(),
            sp.GetRequiredService<ChatNaming>(),
            sp.GetRequiredService<LocalVoiceTurn>(),
            sp.GetRequiredService<Lazy<ITeamNotifier>>(),
            sp.GetRequiredService<IConfiguration>(),
            sp.GetRequiredService<ILogger<TurnIntake>>()));
        // Возобновление оборванного хода: кандидаты auto-resume, маркеры обрыва, ручной
        // перезапуск зависшего хода. Порог свежести хода читается из конфига в самой роли.
        services.AddSingleton(sp => new TurnRecovery(
            sp.GetRequiredService<SessionStore>(),
            sp.GetRequiredService<PendingQueueService>(),
            sp.GetRequiredService<CliRoute>(),
            sp.GetRequiredService<ChatHistoryService>(),
            sp.GetRequiredService<SessionHistory>(),
            sp.GetRequiredService<Lazy<ITurnStarter>>(),
            sp.GetRequiredService<IConfiguration>()));
        // Создание чата: обычный, вне проекта, от лица персоны, групповой, ветка, перенос,
        // продолжение.
        services.AddSingleton(sp => new ChatCreation(
            sp.GetRequiredService<SessionStore>(),
            sp.GetRequiredService<ChatHistoryService>(),
            sp.GetRequiredService<CliRoute>(),
            sp.GetRequiredService<TurnProcessHost>(),
            sp.GetRequiredService<ProjectManager>(),
            sp.GetRequiredService<PersonaLayerBuilder>(),
            sp.GetRequiredService<ModelAssignmentResolver>(),
            sp.GetRequiredService<ChatSettings>(),
            sp.GetRequiredService<ClaudeHomeServer.Services.Turn.ITurnEventBus>()));
        // Удаление чата. PromptSnapshotStore — GetService: без него снимков промпта нет.
        // Lazy<ReviewTreePool?> вместо IServiceProvider (решение 05.11, 1dda7103): настоящий
        // цикл ReviewTreePool → SessionManager; при отсутствии пула — null, как был null у
        // прежнего GetService.
        services.AddSingleton(sp => new Lazy<ReviewTreePool?>(() => sp.GetService<ReviewTreePool>()));
        services.AddSingleton(sp => new ChatDeletion(
            sp.GetRequiredService<SessionStore>(),
            sp.GetRequiredService<ChatHistoryService>(),
            sp.GetRequiredService<CliRoute>(),
            sp.GetRequiredService<ChatWorktree>(),
            sp.GetService<PromptSnapshotStore>(),
            sp.GetRequiredService<Lazy<ReviewTreePool?>>(),
            sp.GetRequiredService<ClaudeHomeServer.Services.Turn.ITurnEventBus>(),
            sp.GetRequiredService<ILogger<ChatDeletion>>()));
        return services;
    }
}
