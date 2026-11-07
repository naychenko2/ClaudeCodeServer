using ClaudeHomeServer.Services.Llm;
using ClaudeHomeServer.Services.Sessions;
using ClaudeHomeServer.Services.Team;

// Namespace намеренно Composition, а не Sessions: Program.cs уже импортирует Composition (его
// строка остаётся ровно одной), а сторож формы ядра запрещает ссылки на типы Services.Sessions
// из чего-либо, кроме SessionManager и самих Services.Sessions.
namespace ClaudeHomeServer.Services.Composition;

// Регистрация ядра сессий: роли, вынесенные из SessionManager (docs/research/session-manager-split-2026-11.md).
// Все классы — синглтоны (у SessionManager тоже синглтон-время жизни, а состояние общее).
// Каждый следующий шаг S3 дописывает свои классы сюда же; в Program.cs — одна строка AddSessionCore().
public static class SessionCoreRegistration
{
    public static IServiceCollection AddSessionCore(this IServiceCollection services)
    {
        // Стор метаданных сессий (S3-1): единственный реестр сессий процесса. ITaskLookup (адаптер
        // TaskLookupAdapter над TaskManager, раньше его строил сам SessionManager) уже в DI —
        // регистрация в Program.cs для Spend; второй регистрации того же адаптера не заводим:
        // её ловит DuplicateSingletonRegistrationTests.
        services.AddSingleton(sp => new SessionStore(
            sp.GetRequiredService<IConfiguration>(),
            sp.GetRequiredService<ProjectManager>(),
            sp.GetRequiredService<ISessionBroadcaster>(),
            sp.GetRequiredService<ILogger<SessionStore>>(),
            sp.GetRequiredService<ITaskLookup>()));

        // --- Роли ядра (S3-2) ---
        // Шов «очередь → приём хода»: ленивый на синглтон SessionManager — прямая зависимость
        // дала бы цикл PendingQueueService → SessionManager → PendingQueueService; на резолве ленивого
        // шва SessionManager ещё не строится, цикл разрывается отложенным вызовом (S3-0 §3).
        services.AddSingleton(sp => new Lazy<ITurnStarter>(() => sp.GetRequiredService<SessionManager>()));

        // Стор копий транскриптов архивных чатов и учёт лимитов подписок — общие экземпляры
        // с ролями: без них TurnCostLedger и CliRoute собрали бы СВОИ, и счётчик лимитов
        // (он же у шлюза LLM) стал бы вторым.
        services.AddSingleton(sp => new ArchivedTranscriptStore(sp.GetRequiredService<IConfiguration>()));
        services.AddSingleton(sp => new SubscriptionLimitRecorder(
            sp.GetRequiredService<UsageService>(),
            sp.GetRequiredService<ClaudeSubscriptionPool>(),
            sp.GetService<SubscriptionActivityTracker>()));

        services.AddSingleton(sp => new SessionHistory(
            sp.GetRequiredService<ChatHistoryService>(),
            sp.GetRequiredService<SessionStore>(),
            sp.GetRequiredService<ILogger<SessionHistory>>()));
        // ISpendCollector опционален (Spend-подсистема может не быть): та же семантика, что у
        // параметра SessionManager — без него аналитика расхода просто не пишется.
        services.AddSingleton(sp => new TurnCostLedger(
            sp.GetRequiredService<FalCostService>(),
            sp.GetRequiredService<GlifAccountService>(),
            sp.GetService<Spend.ISpendCollector>(),
            sp.GetRequiredService<SubscriptionLimitRecorder>(),
            sp.GetRequiredService<SessionHistory>(),
            sp.GetRequiredService<ChatHistoryService>(),
            sp.GetRequiredService<SessionStore>(),
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
            sp.GetRequiredService<ISessionBroadcaster>(),
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
        // синглтон, что читает фасад (регистрация в Program.cs); шов ITurnStarter — ленивый,
        // иначе цикл «паспорта → приём хода → паспорта».
        services.AddSingleton(sp => new TurnPassports(
            sp.GetRequiredService<ClaudeHomeServer.Services.Turn.ITurnEventBus>(),
            sp.GetService<PromptSnapshotStore>(),
            sp.GetService<ClaudeHomeServer.Services.Mcp.McpStatusStore>(),
            sp.GetService<ClaudeHomeServer.Services.Llm.Claude.SubagentRunLog>(),
            sp.GetService<ClaudeHomeServer.Services.Llm.TurnRunLog>(),
            sp.GetRequiredService<SessionStore>(),
            sp.GetRequiredService<Lazy<ITurnStarter>>(),
            sp.GetServices<ClaudeHomeServer.Services.Turn.IPromptSectionContributor>()));
        return services;
    }
}
