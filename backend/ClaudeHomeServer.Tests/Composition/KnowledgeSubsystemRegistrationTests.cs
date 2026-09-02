using ClaudeHomeServer.Services.Composition;
using ClaudeHomeServer.Services.Knowledge;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Options;

namespace ClaudeHomeServer.Tests.Composition;

// Сторож подсистемы «Knowledge» (волна 3, шаг 3): проверяет, что `KnowledgeSubsystem.Register`
// подключает ВСЕ типы, которые раньше жили в Program.cs четырьмя отдельными блоками
// (`WorkspaceKnowledgeStore` рядом с FalCostService, `KnowledgeBaseCatalogService` рядом с
// McpHttp-тулсетами, HTTP-клиент `dify` в секции HTTP-клиентов и общий блок Knowledge
// ~668-693 без форвардеров). Если кто-то добавит регистрацию Knowledge в Program.cs вместо
// подсистемы — или вынесет регистрацию из Register — тест упадёт, и ревью это поймает.
//
// `KnowledgeService`/`WorkspaceKnowledgeStore`/`KnowledgeBaseCatalogService`/
// `ProjectKnowledgeSyncService`/`UserKnowledgeCascade` живут в КОРНЕ `ClaudeHomeServer.Services`,
// не в `Services.Knowledge`. Подсистема только вызывает их `AddSingleton<T>()`/`AddHostedService`
// и т.п. — сами типы остаются там, где жили раньше (см. шапку KnowledgeSubsystem.cs).
//
// Конфиг минимальный: ключей Dify не задаём — KnowledgeService тихо работает на дефолтах
// (IsConfigured=false, реконсайлер сразу выходит).
public class KnowledgeSubsystemRegistrationTests
{
    private static IConfiguration BuildConfig(string dataPath) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DataPath"] = dataPath,
            })
            .Build();

    private static ServiceCollection BuildServices(IConfiguration config)
    {
        var services = new ServiceCollection();
        // Изолированная коллекция: DI должен видеть IConfiguration, иначе `WorkspaceKnowledgeStore(IConfiguration)`
        // не сможет выбрать конструктор.
        services.AddSingleton<IConfiguration>(config);
        services.AddLogging();
        services.AddSubsystems(config, new KnowledgeSubsystem());
        return services;
    }

    [Fact]
    public void Register_SubsystemHasStableKeyAndTitle()
    {
        var subsystem = new KnowledgeSubsystem();

        subsystem.Key.Should().Be("knowledge");
        subsystem.Title.Should().Be("Знания");
    }

    [Fact]
    public void Register_RegistersAllKnowledgeServices()
    {
        var services = BuildServices(BuildConfig(Path.Combine(Path.GetTempPath(), Path.GetRandomFileName())));

        // Синглтоны из блока Program.cs:323 и Program.cs:388 до выноса в подсистему.
        services.Should().Contain(s => s.ServiceType == typeof(ClaudeHomeServer.Services.WorkspaceKnowledgeStore));
        services.Should().Contain(s => s.ServiceType == typeof(ClaudeHomeServer.Services.KnowledgeBaseCatalogService));

        // Секция DifyOptions: services.Configure<DifyOptions>(...) регистрирует дескриптор
        // IConfigureOptions<DifyOptions> (через OptionsConfigurationServiceCollectionExtensions),
        // который сам подтянет `AddOptions()` (open generic). В изолированном ServiceCollection
        // проверяем closed-generic-дескриптор — он и есть «регистрация перенесена»;
// IOptions<DifyOptions> резолвится уже через TestWebApplicationFactory (полный стенд).
        services.Should().Contain(s => s.ServiceType == typeof(Microsoft.Extensions.Options.IConfigureOptions<ClaudeHomeServer.Models.DifyOptions>));

        // Сервисы из блока Program.cs:668-693 до выноса в подсистему (без 5 форвардеров
        // `IKnowledgeSyncParticipant → {DossierStore, ...}`, которые остаются в Program.cs).
        services.Should().Contain(s => s.ServiceType == typeof(ClaudeHomeServer.Services.KnowledgeService));
        services.Should().Contain(s => s.ServiceType == typeof(ClaudeHomeServer.Services.ProjectKnowledgeSyncService));
        services.Should().Contain(s => s.ServiceType == typeof(ClaudeHomeServer.Services.UserKnowledgeCascade));

        // IKnowledgeAlertNotifier (шов нотификатора) + конкретный KnowledgeAlertNotifier.
        services.Should().Contain(s => s.ServiceType == typeof(IKnowledgeAlertNotifier));
    }

    [Fact]
    public void Register_ProjectKnowledgeTurnSync_HostedRegistered()
    {
        var services = BuildServices(BuildConfig(Path.Combine(Path.GetTempPath(), Path.GetRandomFileName())));

        // `AddGatedHostedService<ProjectKnowledgeTurnSync>(config)` — hosted-мост событий хода
        // Claude на FileService.OnMutated. В тестах среда не Testing по умолчанию, гейт пропускает
        // регистрацию; ищем по ImplementationType (AddHostedService<T>).
        services.Should().Contain(s => s.ImplementationType == typeof(ClaudeHomeServer.Services.ProjectKnowledgeTurnSync));
    }

    // Инвариант «`KnowledgeIndexReconciler` и его hosted указывают на ОДИН инстанс»:
    // `AddSingleton<KnowledgeIndexReconciler>()` + `AddGatedHostedFrom(... sp.GetRequiredService<KnowledgeIndexReconciler>())`.
    // Если кто-то перепишет на `AddSingleton<KnowledgeIndexReconciler>()` + `AddHostedService<T>`,
    // стартует второй экземпляр, и счётчики реконсайлера (он единственный, кто владеет
    // `_targets`/`_attempts`/`_quarantine`/`_lastCounts`/`_recovered`) были бы раздвоены.
    // Ищем hosted-дескриптор с ImplementationFactory (AddGatedHostedFrom).
    [Fact]
    public void Register_KnowledgeIndexReconciler_HostedResolvesSameInstance()
    {
        var services = BuildServices(BuildConfig(Path.Combine(Path.GetTempPath(), Path.GetRandomFileName())));

        // Singleton-дескриптор: AddSingleton<KnowledgeIndexReconciler>() — форма стандартная.
        var singletonDesc = services.SingleOrDefault(s => s.ServiceType == typeof(KnowledgeIndexReconciler));
        singletonDesc.Should().NotBeNull("AddSingleton<KnowledgeIndexReconciler> обязан присутствовать");
        singletonDesc!.ImplementationType.Should().Be(typeof(KnowledgeIndexReconciler));
        singletonDesc.ImplementationInstance.Should().BeNull();
        singletonDesc.ImplementationFactory.Should().BeNull();

        // Hosted-дескриптор для KnowledgeIndexReconciler: AddGatedHostedFrom регистрирует
        // IHostedService с ImplementationFactory, а не ImplementationType.
        var hostedDesc = services.SingleOrDefault(s =>
            s.ServiceType == typeof(Microsoft.Extensions.Hosting.IHostedService)
            && s.ImplementationFactory != null
            && s.ImplementationType == null);

        hostedDesc.Should().NotBeNull(
            "AddGatedHostedFrom обязан оставить дескриптор для IHostedService с фабрикой " +
            "(sp => sp.GetRequiredService<KnowledgeIndexReconciler>()), а не AddHostedService<T> — " +
            "иначе стартует второй экземпляр реконсайлера и состояние раздвоится");
    }

    // Сторож прокси-инварианта (см. шапку KnowledgeSubsystem.cs): Dify — локальный сервис,
    // идёт БЕЗ egress-прокси. `WithoutEgressProxy()` обязателен, иначе Dify молча проксируется
    // (на контейнерных владельцев — на sandbox-прокси) и не отвечает. Техника проверки
    // скопирована из GitSubsystemRegistrationTests.Register_ForgejoHttpClient_DisablesSystemProxy.
    [Fact]
    public void Register_DifyHttpClient_DisablesSystemProxy()
    {
        using var sp = BuildServiceProvider();
        var monitor = sp.GetRequiredService<IOptionsMonitor<HttpClientFactoryOptions>>();

        var builder = ApplyActions(monitor.Get("dify"), sp);
        var primary = builder.PrimaryHandler.Should().BeOfType<HttpClientHandler>().Subject;

        primary.UseProxy.Should().BeFalse(
            "Dify — локальный сервис: egress-прокси ему противопоказан, WithoutEgressProxy обязателен");
    }

    private static ServiceProvider BuildServiceProvider()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSubsystems(BuildConfig(Path.Combine(Path.GetTempPath(), Path.GetRandomFileName())),
            new KnowledgeSubsystem());
        return services.BuildServiceProvider();
    }

    private static TestHandlerBuilder ApplyActions(
        HttpClientFactoryOptions options, IServiceProvider sp)
    {
        var builder = new TestHandlerBuilder(sp);
        foreach (var action in options.HttpMessageHandlerBuilderActions)
            action(builder);
        return builder;
    }

    // Сторож факта подключения в Program.cs: поднимает полный стенд через
    // `TestWebApplicationFactory<Program>` и резолвит типы Knowledge из РЕАЛЬНОГО DI-графа
    // (а не из изолированного ServiceCollection). Если кто-то уберёт `new KnowledgeSubsystem()`
    // из `AddSubsystems(...)` в Program.cs — резолв упадёт с InvalidOperationException, и
    // тест поймает регрессию.
    [Fact]
    public void Program_RegistersKnowledgeSubsystem_ServicesResolvable()
    {
        using var factory = new TestWebApplicationFactory();
        var sp = factory.Services;

        // Если KnowledgeSubsystem отсутствует в Program.cs — любой из этих резолвов
        // бросит InvalidOperationException «Unable to resolve service for type …».
        sp.GetRequiredService<ClaudeHomeServer.Services.KnowledgeService>();
        sp.GetRequiredService<ClaudeHomeServer.Services.WorkspaceKnowledgeStore>();
        sp.GetRequiredService<ClaudeHomeServer.Services.ProjectKnowledgeSyncService>();
        sp.GetRequiredService<ClaudeHomeServer.Services.KnowledgeBaseCatalogService>();
        sp.GetRequiredService<KnowledgeIndexReconciler>();

        // Пять форвардеров IKnowledgeSyncParticipant остаются в Program.cs (кросс-вертикальный
        // клей): после переноса регистрации Knowledge из Program.cs в подсистему — все 5
        // участников должны быть резолвимы из РЕАЛЬНОГО DI-графа (тест ловит регрессию
        // «удалили форвардер вместе с блоком Knowledge»).
        sp.GetServices<IKnowledgeSyncParticipant>().Should().HaveCount(
            5,
            "пять форвардеров `IKnowledgeSyncParticipant → {PersonaMemoryService, TeamMemoryService, " +
            "DossierStore, NotesKnowledgeService, ProjectKnowledgeSyncService}` остаются в Program.cs — " +
            "проверка ловит регрессию «удалили вместе с блоком Knowledge»");
    }

    // Копия тестовой обвязки для проверки прокси — см. VideoSubsystemRegistrationTests.
    private sealed class TestHandlerBuilder : HttpMessageHandlerBuilder
    {
        public TestHandlerBuilder(IServiceProvider services)
        {
            Services = services;
        }

        public override string Name { get; set; } = string.Empty;

        public override IServiceProvider Services { get; }

        public override HttpMessageHandler PrimaryHandler { get; set; } = new HttpClientHandler();

        public override IList<DelegatingHandler> AdditionalHandlers { get; } =
            new List<DelegatingHandler>();

        public override HttpMessageHandler Build()
        {
            var handler = PrimaryHandler;
            foreach (var next in AdditionalHandlers)
            {
                if (next is null) continue;
                next.InnerHandler = handler;
                handler = next;
            }
            return handler;
        }
    }
}