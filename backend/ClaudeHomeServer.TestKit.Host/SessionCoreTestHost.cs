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
        // Сжатый конструктор фасада (S3-8c): роли ядра и живые зависимости — типизированными
        // аргументами из контейнера. Опциональные — ровно там, где подсистемы может не быть
        // (GetService): без них фасад работает как раньше, запасных экземпляров не собирает.
        services.AddSingleton(sp => new SessionManager(
            sp.GetRequiredService<SessionStore>(),
            sp.GetRequiredService<SessionHistory>(),
            sp.GetRequiredService<TurnCostLedger>(),
            sp.GetRequiredService<PendingQueueService>(),
            sp.GetRequiredService<CliRoute>(),
            sp.GetRequiredService<ChatWorktree>(),
            sp.GetRequiredService<ProviderMigration>(),
            sp.GetRequiredService<ChatNaming>(),
            sp.GetRequiredService<ChatVisibility>(),
            sp.GetRequiredService<ChatSettings>(),
            sp.GetRequiredService<McpEndpoint>(),
            sp.GetRequiredService<BuiltinMcpContexts>(),
            sp.GetRequiredService<PersonaLayerBuilder>(),
            sp.GetRequiredService<ExternalMcpContexts>(),
            sp.GetRequiredService<ModulesMcpContexts>(),
            sp.GetRequiredService<TurnPassports>(),
            sp.GetRequiredService<TurnProcessHost>(),
            sp.GetRequiredService<TurnRecovery>(),
            sp.GetRequiredService<ChatCreation>(),
            sp.GetRequiredService<ChatDeletion>(),
            sp.GetRequiredService<TurnCompletion>(),
            sp.GetRequiredService<TurnIntake>(),
            sp.GetRequiredService<ProjectManager>(),
            sp.GetRequiredService<ChatHistoryService>(),
            sp.GetRequiredService<IConfiguration>(),
            sp.GetRequiredService<LlmProviderRegistry>(),
            sp.GetRequiredService<PersonaManager>(),
            sp.GetRequiredService<ILogger<SessionManager>>(),
            sp.GetRequiredService<ISessionBroadcaster>(),
            sp.GetRequiredService<ILoggerFactory>(),
            sp,
            sp.GetRequiredService<ClaudeHomeServer.Services.Turn.ITurnEventBus>(),
            sp.GetRequiredService<AppSettingsService>(),
            sp.GetRequiredService<ClaudeHomeServer.Services.Execution.SandboxManager>(),
            sp.GetService<ClaudeHomeServer.Services.Team.TeamPlanningService>(),
            sp.GetService<TaskManager>(),
            sp.GetService<ClaudeHomeServer.Services.Llm.Claude.SubagentRunLog>()));
        return new SessionCoreTestHost(services.BuildServiceProvider());
    }

    // Умолчания — те же объекты, что тесты собирали руками для конструктора SessionManager;
    // всё строится от IConfiguration из контейнера, поэтому конфиг теста (configure) подхватывают
    // и умолчания. Необязательные зависимости фасада (git, spend…) здесь не регистрируются:
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
        // Рекордер лимитов подписок: единственная продовая регистрация — в слое Llm
        // (LlmSubsystem.cs:447), а тестовый контейнер эту вертикаль не поднимает — рекордер
        // собирается здесь из тех же умолчаний (UsageService, пул). Трекер активности —
        // GetService: тесты, регистрирующие свой трекер в configure, им и обеспечиваются
        // (резолв ленивый, последний выигрывает); без него — null, как опциональный
        // параметр конструктора.
        services.AddSingleton(sp => new SubscriptionLimitRecorder(
            sp.GetRequiredService<UsageService>(),
            sp.GetRequiredService<ClaudeSubscriptionPool>(),
            sp.GetService<SubscriptionActivityTracker>()));
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
