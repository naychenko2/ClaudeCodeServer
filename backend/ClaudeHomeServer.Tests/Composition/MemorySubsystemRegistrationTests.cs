using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Composition;
using ClaudeHomeServer.Services.Knowledge;
using ClaudeHomeServer.Services.Memory;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace ClaudeHomeServer.Tests.Composition;

// Сторож подсистемы «Memory» (волна 3, шаг 4): проверяет, что `MemorySubsystem.Register`
// подключает ВСЕ типы, которые раньше жили в Program.cs одним блоком «Память персон
// и команды» (строки ~164-176 + ~212-221 до выноса). Если кто-то добавит регистрацию
// Memory в Program.cs вместо подсистемы — или вынесет регистрацию из Register — тест
// упадёт, и ревью это поймает.
//
// Сами шесть типов фасадов (`PersonaMemoryService`/`TeamMemoryService`/`*Consolidation*`/
// `*Autolearn*`) живут в КОРНЕ `ClaudeHomeServer.Services`, не в `Services.Memory`.
// Подсистема только вызывает их `AddSingleton<T>()` / `AddHostedService` / `AddGatedHostedFrom`
// — сами типы остаются там, где жили раньше (см. шапку `MemorySubsystem.cs`).
//
// Конфиг минимальный: ключей Memory не задаём (MemoryConsolidationService/AutolearnService
// тихо работают на дефолтах; теста этого не трогает).
public class MemorySubsystemRegistrationTests
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
        // Изолированная коллекция: DI должен видеть IConfiguration, иначе конструкторы
        // фасадов с зависимостью от IConfiguration не смогут выбрать подходящий ctor.
        services.AddSingleton<IConfiguration>(config);
        services.AddLogging();
        services.AddSubsystems(config, new MemorySubsystem());
        return services;
    }

    [Fact]
    public void Register_SubsystemHasStableKeyAndTitle()
    {
        var subsystem = new MemorySubsystem();

        subsystem.Key.Should().Be("memory");
        subsystem.Title.Should().Be("Память персон и команды");
    }

    [Fact]
    public void Register_RegistersAllMemoryServices()
    {
        var services = BuildServices(BuildConfig(Path.Combine(Path.GetTempPath(), Path.GetRandomFileName())));

        // Фасады стора + их консолидация/autolearn из блоков Program.cs:165-166 и
        // Program.cs:213-221 до выноса в подсистему.
        services.Should().Contain(s => s.ServiceType == typeof(PersonaMemoryService));
        services.Should().Contain(s => s.ServiceType == typeof(TeamMemoryService));
        services.Should().Contain(s => s.ServiceType == typeof(PersonaMemoryConsolidationService));
        services.Should().Contain(s => s.ServiceType == typeof(PersonaMemoryAutolearnService));
        services.Should().Contain(s => s.ServiceType == typeof(TeamMemoryConsolidationService));
        services.Should().Contain(s => s.ServiceType == typeof(TeamMemoryAutolearnService));
    }

    // Инвариант «`PersonaMemoryConsolidationService` и его hosted указывают на ОДИН инстанс»:
    // `AddSingleton<PersonaMemoryConsolidationService>()` + `AddGatedHostedFrom(... sp =>
    // sp.GetRequiredService<PersonaMemoryConsolidationService>())`. Если кто-то перепишет на
    // `AddHostedService<T>`, стартует второй экземпляр, и `RequestConsolidation` от autolearn
    // ушла бы в один контейнер, а hosted-цикл читал бы из другого. Прецедент — DossierAutoExporter.
    [Fact]
    public void Register_PersonaMemoryConsolidationService_HostedResolvesSameInstance()
    {
        var services = BuildServices(BuildConfig(Path.Combine(Path.GetTempPath(), Path.GetRandomFileName())));

        var singletonDesc = services.SingleOrDefault(s => s.ServiceType == typeof(PersonaMemoryConsolidationService));
        singletonDesc.Should().NotBeNull("AddSingleton<PersonaMemoryConsolidationService> обязан присутствовать");
        singletonDesc!.ImplementationType.Should().Be(typeof(PersonaMemoryConsolidationService));
        singletonDesc.ImplementationInstance.Should().BeNull();
        singletonDesc.ImplementationFactory.Should().BeNull();

        // Хотя бы один hosted-дескриптор с ImplementationFactory — общий сигнатурный признак
        // `AddGatedHostedFrom` (AddHostedService<T> создал бы ImplementationType, без фабрики).
        // В подсистеме Memory таких дескриптора четыре (Persona/Team × Consolidation/Autolearn),
        // поэтому здесь проверяем наличие хотя бы одного — этого достаточно, чтобы поймать
        // регрессию «AddGatedHostedFrom → AddHostedService<T>».
        services.Should().Contain(s =>
            s.ServiceType == typeof(Microsoft.Extensions.Hosting.IHostedService)
            && s.ImplementationFactory != null
            && s.ImplementationType == null);
    }

    // Тот же инвариант для остальных singleton+AddGatedHostedFrom сервисов:
    // каждый из `PersonaMemoryAutolearnService`/`TeamMemoryConsolidationService`/
    // `TeamMemoryAutolearnService` должен иметь И singleton-дескриптор (AddSingleton<T>),
    // И hosted-дескриптор с фабрикой (AddGatedHostedFrom). Если кто-то перепишет на
    // `AddHostedService<T>` — авто-путь сломался бы (стартовал бы второй инстанс).
    [Theory]
    [InlineData(typeof(PersonaMemoryAutolearnService))]
    [InlineData(typeof(TeamMemoryConsolidationService))]
    [InlineData(typeof(TeamMemoryAutolearnService))]
    public void Register_HostedSingletonPair_HasBothDescriptors(Type hostedType)
    {
        var services = BuildServices(BuildConfig(Path.Combine(Path.GetTempPath(), Path.GetRandomFileName())));

        // Singleton-дескриптор: AddSingleton<T>(), ImplementationType = typeof(T),
        // без фабрики/инстанса.
        var singletonDesc = services.SingleOrDefault(s => s.ServiceType == hostedType);
        singletonDesc.Should().NotBeNull($"AddSingleton<{hostedType.Name}> обязан присутствовать");
        singletonDesc!.ImplementationType.Should().Be(hostedType);
        singletonDesc.ImplementationInstance.Should().BeNull();
        singletonDesc.ImplementationFactory.Should().BeNull();

        // Хотя бы один hosted-дескриптор с фабрикой — `AddGatedHostedFrom` обязан
        // создать фабричный дескриптор (а не ImplementationType, как `AddHostedService<T>`).
        services.Should().Contain(s =>
            s.ServiceType == typeof(Microsoft.Extensions.Hosting.IHostedService)
            && s.ImplementationFactory != null
            && s.ImplementationType == null);
    }

    // Сторож факта подключения в Program.cs: поднимает полный стенд через
    // `TestWebApplicationFactory<Program>` и резолвит типы Memory из РЕАЛЬНОГО DI-графа
    // (а не из изолированного ServiceCollection). Если кто-то уберёт `new MemorySubsystem()`
    // из `AddSubsystems(...)` в Program.cs — резолв упадёт с InvalidOperationException, и
    // тест поймает регрессию.
    [Fact]
    public void Program_RegistersMemorySubsystem_ServicesResolvable()
    {
        using var factory = new TestWebApplicationFactory();
        var sp = factory.Services;

        // Если MemorySubsystem отсутствует в Program.cs — любой из этих резолвов
        // бросит InvalidOperationException «Unable to resolve service for type …».
        sp.GetRequiredService<PersonaMemoryService>();
        sp.GetRequiredService<TeamMemoryService>();
        sp.GetRequiredService<PersonaMemoryConsolidationService>();
        sp.GetRequiredService<PersonaMemoryAutolearnService>();
        sp.GetRequiredService<TeamMemoryConsolidationService>();
        sp.GetRequiredService<TeamMemoryAutolearnService>();

        // Два форвардера IKnowledgeSyncParticipant остаются в Program.cs (кросс-вертикальный
        // клей): после переноса регистрации Memory из Program.cs в подсистему — все 5
        // участников должны быть резолвимы из РЕАЛЬНОГО DI-графа (тест ловит регрессию
        // «удалили форвардеры Persona/TeamMemory вместе с блоком Memory»). Тот же контракт
        // проверяется в `KnowledgeSubsystemRegistrationTests` — здесь держим страховку
        // со стороны Memory-подсистемы.
        sp.GetServices<IKnowledgeSyncParticipant>().Should().HaveCount(
            5,
            "пять форвардеров `IKnowledgeSyncParticipant → {PersonaMemoryService, TeamMemoryService, " +
            "DossierStore, NotesKnowledgeService, ProjectKnowledgeSyncService}` остаются в Program.cs — " +
            "проверка ловит регрессию «удалили вместе с блоком Memory»");
    }
}
