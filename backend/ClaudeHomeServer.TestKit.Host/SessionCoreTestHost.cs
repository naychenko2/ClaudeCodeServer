using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Composition;
using ClaudeHomeServer.Services.Knowledge;
using ClaudeHomeServer.Services.Llm;
using ClaudeHomeServer.Services.Notes;
using ClaudeHomeServer.Services.Sessions;
using ClaudeHomeServer.Services.Skills;
using ClaudeHomeServer.Services.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace ClaudeHomeServer.Tests.Helpers;

// Общий помощник сборки ядра сессий для тестов (S3-8): ядро собирается тем же AddSessionCore,
// что и в Program.cs, а SessionManager конструируется ровно в одном месте — здесь. Порядок
// регистраций: умолчания-фейки → AddSessionCore() → configure теста (регистрируется последним
// и выигрывает) → фабрика SessionManager. Контейнер новый на каждый Build, состояния в
// static-полях нет; Dispose гасит провайдер вместе с его синглтонами.
public sealed class SessionCoreTestHost : IDisposable
{
    private readonly ServiceProvider _provider;

    public SessionManager Manager { get; }
    public IServiceProvider Services => _provider;

    private SessionCoreTestHost(ServiceProvider provider)
    {
        _provider = provider;
        Manager = provider.GetRequiredService<SessionManager>();
    }

    public static SessionCoreTestHost Build(string dataDir, Action<IServiceCollection>? configure = null)
    {
        var services = new ServiceCollection();
        AddDefaults(services, dataDir);
        services.AddSessionCore();
        configure?.Invoke(services);
        services.AddSingleton(sp => new SessionManager(
            sp.GetRequiredService<ProjectManager>(),
            sp.GetRequiredService<ChatHistoryService>(),
            sp.GetRequiredService<IConfiguration>(),
            sp.GetRequiredService<ILlmSessionAdapterFactory>(),
            sp.GetRequiredService<FalCostService>(),
            sp.GetRequiredService<UsageService>(),
            sp.GetRequiredService<AppSettingsService>(),
            sp.GetRequiredService<UserStore>(),
            sp.GetRequiredService<JwtService>(),
            sp.GetRequiredService<Microsoft.AspNetCore.Hosting.Server.IServer>(),
            sp.GetRequiredService<LlmProviderRegistry>(),
            sp.GetRequiredService<FeatureFlagService>(),
            sp.GetRequiredService<PersonaManager>(),
            sp.GetRequiredService<PersonaBindingsService>(),
            sp.GetRequiredService<ClaudeSubscriptionPool>(),
            sp.GetRequiredService<ILogger<SessionManager>>(),
            sp.GetRequiredService<ClaudeHomeServer.Services.Execution.ILauncherFactory>(),
            sp.GetRequiredService<ClaudeHomeServer.Services.Execution.SandboxManager>(),
            broadcaster: sp.GetRequiredService<ISessionBroadcaster>(),
            agentSync: sp.GetService<PersonaAgentFileSync>(),
            homes: sp.GetRequiredService<UserHomeResolver>(),
            cheap: sp.GetService<ICheapTextRunner>(),
            git: sp.GetService<ClaudeHomeServer.Services.Git.GitService>(),
            spend: sp.GetService<ClaudeHomeServer.Services.Spend.ISpendCollector>(),
            assignments: sp.GetRequiredService<ModelAssignmentResolver>(),
            teamPlanning: sp.GetService<ClaudeHomeServer.Services.Team.TeamPlanningService>(),
            activity: sp.GetService<SubscriptionActivityTracker>(),
            glif: sp.GetRequiredService<GlifAccountService>(),
            subagentRuns: sp.GetService<ClaudeHomeServer.Services.Llm.Claude.SubagentRunLog>(),
            turnRuns: sp.GetService<TurnRunLog>(),
            // router/ollama фасад не получает: они нужны только голосовой роли LocalVoiceTurn,
            // а её AddSessionCore собирает сам (GetService) — так помощник не упоминает
            // ILocalLlmClient за границей вертикали Llm (сторож LocalLlmClientBoundaryTests).
            turnEvents: sp.GetRequiredService<ClaudeHomeServer.Services.Turn.ITurnEventBus>(),
            tasks: sp.GetService<TaskManager>(),
            deviceGate: sp.GetService<ClaudeHomeServer.Services.Execution.IProjectDeviceGate>(),
            services: sp,
            graphInspector: sp.GetService<ClaudeHomeServer.Services.CodeGraph.ICodeGraphInspector>(),
            store: sp.GetRequiredService<SessionStore>(),
            historyRole: sp.GetRequiredService<SessionHistory>(),
            costs: sp.GetRequiredService<TurnCostLedger>(),
            pending: sp.GetRequiredService<PendingQueueService>(),
            route: sp.GetRequiredService<CliRoute>(),
            worktree: sp.GetRequiredService<ChatWorktree>(),
            providerMigration: sp.GetRequiredService<ProviderMigration>(),
            naming: sp.GetRequiredService<ChatNaming>(),
            visibility: sp.GetRequiredService<ChatVisibility>(),
            settings: sp.GetRequiredService<ChatSettings>(),
            mcpEndpoint: sp.GetRequiredService<McpEndpoint>(),
            builtinContexts: sp.GetRequiredService<BuiltinMcpContexts>(),
            personaLayer: sp.GetRequiredService<PersonaLayerBuilder>(),
            externalContexts: sp.GetRequiredService<ExternalMcpContexts>(),
            moduleContexts: sp.GetRequiredService<ModulesMcpContexts>(),
            turnPassports: sp.GetRequiredService<TurnPassports>(),
            limitRecorder: sp.GetRequiredService<SubscriptionLimitRecorder>(),
            archivedTranscripts: sp.GetRequiredService<ArchivedTranscriptStore>(),
            processHost: sp.GetRequiredService<TurnProcessHost>(),
            recovery: sp.GetRequiredService<TurnRecovery>(),
            chatCreation: sp.GetRequiredService<ChatCreation>(),
            chatDeletion: sp.GetRequiredService<ChatDeletion>(),
            completion: sp.GetRequiredService<TurnCompletion>(),
            intake: sp.GetRequiredService<TurnIntake>()));
        return new SessionCoreTestHost(services.BuildServiceProvider());
    }

    // Умолчания — те же объекты, что тесты собирали руками для конструктора SessionManager;
    // всё строится от IConfiguration из контейнера, поэтому конфиг теста (configure) подхватывают
    // и умолчания. Необязательные зависимости фасада (git, cheap, spend…) здесь не регистрируются:
    // без них ядро работает так же, как раньше с null.
    private static void AddDefaults(IServiceCollection services, string dataDir)
    {
        services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DataPath"] = Path.Combine(dataDir, "projects.json"),
                ["Session:AutoSaveSeconds"] = "0",
                ["DefaultProjectsPath"] = Path.Combine(dataDir, "homes"),
                ["ClaudeUserProfileDir"] = Path.Combine(dataDir, "claude-profile"),
            })
            .Build());

        services.AddSingleton(sp => new UserStore(sp.GetRequiredService<IConfiguration>(),
            new FakeHostEnvironment(), NullLogger<UserStore>.Instance));
        services.AddSingleton(sp => new AppSettingsService(sp.GetRequiredService<IConfiguration>()));
        services.AddSingleton(sp => new ProjectManager(sp.GetRequiredService<IConfiguration>(),
            sp.GetRequiredService<UserStore>(), sp.GetRequiredService<AppSettingsService>()));
        services.AddSingleton(sp => new ChatHistoryService(sp.GetRequiredService<IConfiguration>()));
        services.AddSingleton(sp => new PersonaManager(sp.GetRequiredService<IConfiguration>()));
        services.AddSingleton(sp => new FeatureFlagService(sp.GetRequiredService<UserStore>()));
        services.AddSingleton(sp => new LlmProviderRegistry(sp.GetRequiredService<IConfiguration>()));
        services.AddSingleton(sp => new ClaudeSubscriptionPool(sp.GetRequiredService<IConfiguration>()));
        services.AddSingleton(sp => new WorkspaceKnowledgeStore(sp.GetRequiredService<IConfiguration>()));
        services.AddSingleton<ILlmSessionAdapterFactory>(sp => new LlmSessionAdapterFactory(
            sp.GetRequiredService<IConfiguration>(), new AgentPromptSourceAdapter(new SkillsService()),
            new WorkspaceDatasetLookup(sp.GetRequiredService<WorkspaceKnowledgeStore>()),
            sp.GetRequiredService<LlmProviderRegistry>(), sp.GetRequiredService<ClaudeSubscriptionPool>()));
        services.AddSingleton(sp => new FalCostService(new Mock<IHttpClientFactory>().Object,
            sp.GetRequiredService<IConfiguration>()));
        services.AddSingleton(sp => new UsageService(sp.GetRequiredService<IConfiguration>()));
        services.AddSingleton(sp => new JwtService(sp.GetRequiredService<IConfiguration>(),
            sp.GetRequiredService<UserStore>(), NullLogger<JwtService>.Instance));
        services.AddSingleton(_ =>
        {
            var server = new Mock<Microsoft.AspNetCore.Hosting.Server.IServer>();
            server.Setup(s => s.Features).Returns(new Microsoft.AspNetCore.Http.Features.FeatureCollection());
            return server.Object;
        });
        services.AddSingleton(sp =>
        {
            var config = sp.GetRequiredService<IConfiguration>();
            var knowledge = new KnowledgeService(new Mock<IHttpClientFactory>().Object,
                Microsoft.Extensions.Options.Options.Create(new DifyOptions()),
                sp.GetRequiredService<WorkspaceKnowledgeStore>());
            var notes = new NotesService(sp.GetRequiredService<ProjectManager>(), config,
                NullLogger<NotesService>.Instance);
            var notesKb = new NotesKnowledgeService(knowledge, notes, sp.GetRequiredService<UserStore>(),
                config, NullLogger<NotesKnowledgeService>.Instance);
            return new PersonaBindingsService(sp.GetRequiredService<PersonaManager>(),
                sp.GetRequiredService<ProjectManager>(), sp.GetRequiredService<WorkspaceKnowledgeStore>(),
                knowledge, new SkillsService(), sp.GetRequiredService<UserStore>(), config,
                NullLogger<PersonaBindingsService>.Instance, notes: notes, notesKb: notesKb);
        });
        services.AddSingleton<ClaudeHomeServer.Services.Execution.ILauncherFactory>(TestLauncherFactory.Instance);
        services.AddSingleton(sp => new ClaudeHomeServer.Services.Execution.SandboxManager(
            sp.GetRequiredService<IConfiguration>(),
            NullLogger<ClaudeHomeServer.Services.Execution.SandboxManager>.Instance));
        services.AddSingleton<ISessionBroadcaster>(new TestSessionBroadcaster());

        // Зависимости ролей ядра, которых конструктор фасада раньше не просил: собраны так же,
        // как его запасная сборка без DI — поведение тестов не меняется.
        services.AddSingleton(sp => UserHomeResolver.WithoutOverrides(
            sp.GetRequiredService<AppSettingsService>(),
            sp.GetRequiredService<ClaudeHomeServer.Services.Execution.SandboxManager>()));
        services.AddSingleton(sp => new ModelAssignmentResolver(sp.GetRequiredService<AppSettingsService>()));
        // Без Glif:McpToken учёт glif выключен — то же, что прежний glif: null
        services.AddSingleton(sp => new GlifAccountService(new Mock<IHttpClientFactory>().Object,
            sp.GetRequiredService<IConfiguration>()));
        services.AddSingleton<ClaudeHomeServer.Services.Turn.ITurnEventBus>(_ => new ClaudeHomeServer.Services.Turn.TurnEventBus());
        // Пустой поиск задач без реестра — то же, что прежний null; с реестром — адаптер над ним
        services.AddSingleton<ITaskLookup>(sp => sp.GetService<TaskManager>() is { } tasks
            ? new TaskLookupAdapter(tasks)
            : new NoTaskLookup());
        services.AddSingleton<IFeatureChatLinks, NoFeatureChatLinks>();
    }

    private sealed class NoTaskLookup : ITaskLookup
    {
        public TaskItem? GetById(string id) => null;
    }

    public void Dispose() => _provider.Dispose();
}
