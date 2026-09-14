using ClaudeHomeServer.Services.Composition;
using ClaudeHomeServer.Services.Dossiers;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace ClaudeHomeServer.Tests.Composition;

// Сторож подсистемы «Dossiers» (волна 3, шаг 2): проверяет, что `DossiersSubsystem.Register`
// подключает ВСЕ типы, которые раньше жили в Program.cs одним блоком «Паспорта изменений
// (ADR-004)» (строки ~166-183 до выноса). Если кто-то добавит регистрацию Dossiers в
// Program.cs вместо подсистемы — или вынесет регистрацию из Register — тест упадёт,
// и ревью это поймает.
//
// Конфиг минимальный: ключей Dossiers не задаём (DossierStore тихо работает на дефолтах;
// DossierCaptureService читает Dossiers:SkipCommitTypes при первом тике, тест этого не
// трогает).
public class DossiersSubsystemRegistrationTests
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
        // Изолированная коллекция: DI должен видеть IConfiguration, иначе
        // `DossierStore(IConfiguration, ILogger?)` не сможет выбрать конструктор.
        services.AddSingleton<IConfiguration>(config);
        services.AddLogging();
        services.AddSubsystems(config, new DossiersSubsystem());
        return services;
    }

    [Fact]
    public void Register_SubsystemHasStableKeyAndTitle()
    {
        var subsystem = new DossiersSubsystem();

        subsystem.Key.Should().Be("dossiers");
        subsystem.Title.Should().Be("Паспорта изменений");
    }

    [Fact]
    public void Register_RegistersAllDossierServices()
    {
        var services = BuildServices(BuildConfig(Path.Combine(Path.GetTempPath(), Path.GetRandomFileName())));

        // Синглтоны из блока Program.cs:166-183 до выноса в подсистему.
        services.Should().Contain(s => s.ServiceType == typeof(InstanceSecretsProvider));
        services.Should().Contain(s => s.ServiceType == typeof(DossierStore));
        services.Should().Contain(s => s.ServiceType == typeof(DossierCaptureState));
        services.Should().Contain(s => s.ServiceType == typeof(DossierRecallService));
        services.Should().Contain(s => s.ServiceType == typeof(DossierDiscussionStore));
        services.Should().Contain(s => s.ServiceType == typeof(DossierDiscussionService));
        services.Should().Contain(s => s.ServiceType == typeof(DossierAutoExporter));
    }

    [Fact]
    public void Register_DossierCaptureService_HostedRegistered()
    {
        var services = BuildServices(BuildConfig(Path.Combine(Path.GetTempPath(), Path.GetRandomFileName())));

        // `AddGatedHostedService<DossierCaptureService>(config)`. В тестах среда не
        // Testing по умолчанию, гейт пропускает регистрацию.
        services.Should().Contain(s => s.ImplementationType == typeof(DossierCaptureService));
    }

    [Fact]
    public void Register_DossierAutoImporter_HostedRegistered()
    {
        var services = BuildServices(BuildConfig(Path.Combine(Path.GetTempPath(), Path.GetRandomFileName())));

        // `AddGatedHostedService<DossierAutoImporter>(config)` — отдельный hosted-цикл
        // наблюдения за tip ветки ccs/dossiers/v1 (тумблер AutoImportDossiers).
        services.Should().Contain(s => s.ImplementationType == typeof(DossierAutoImporter));
    }

    // Инвариант «`DossierAutoExporter` и его hosted указывают на ОДИН инстанс»:
    // `AddSingleton` + `AddGatedHostedFrom(... sp.GetRequiredService<DossierAutoExporter>())`.
    // Если кто-то перепишет на `AddSingleton<DossierAutoExporter>()` + `AddHostedService<>`,
    // стартует второй экземпляр, и подписка `OnOwnDossierChanged` встала бы на другой
    // объект — события стора не дошли бы до автовыгрузки.
    // Тест через TestWebApplicationFactory для проверки ReferenceEquals здесь не сработает:
    // в Testing-среде гейт `AddGatedHostedFrom` не пропускает hosted-регистрацию
    // (см. `SubsystemHostingExtensions.GatedHostedShouldRegister`), и
    // `IHostedService`-дескриптора для DossierAutoExporter нет вовсе — `Single(...)` упадёт.
    // Проверяем РЕГИСТРАЦИЮ изолированно: ищем hosted-дескриптор для DossierAutoExporter
    // с ImplementationFactory (AddGatedHostedFrom). У AddHostedService<T> дескриптор был бы
    // с ImplementationType == typeof(T), и тест это поймает.
    [Fact]
    public void Register_DossierAutoExporter_HostedResolvesSameInstance()
    {
        var services = BuildServices(BuildConfig(Path.Combine(Path.GetTempPath(), Path.GetRandomFileName())));

        // Singleton-дескриптор: AddSingleton<DossierAutoExporter>() — форма стандартная,
        // ImplementationType = typeof(DossierAutoExporter), без фабрики/инстанса.
        var singletonDesc = services.SingleOrDefault(s => s.ServiceType == typeof(DossierAutoExporter));
        singletonDesc.Should().NotBeNull("AddSingleton<DossierAutoExporter> обязан присутствовать");
        singletonDesc!.ImplementationType.Should().Be(typeof(DossierAutoExporter));
        singletonDesc.ImplementationInstance.Should().BeNull();
        singletonDesc.ImplementationFactory.Should().BeNull();

        // Hosted-дескриптор для DossierAutoExporter: AddGatedHostedFrom регистрирует
        // IHostedService с ImplementationFactory (sp => ...GetRequiredService<DossierAutoExporter>()),
        // а не с ImplementationType — последний бы создал ВТОРОЙ экземпляр.
        var hostedDesc = services.SingleOrDefault(s =>
            s.ServiceType == typeof(Microsoft.Extensions.Hosting.IHostedService)
            && s.ImplementationFactory != null
            && s.ImplementationType == null);

        hostedDesc.Should().NotBeNull(
            "AddGatedHostedFrom обязан оставить дескриптор для IHostedService с фабрикой " +
            "(sp => sp.GetRequiredService<DossierAutoExporter>()), а не AddHostedService<T> — " +
            "иначе стартует второй экземпляр и подписка OnOwnDossierChanged встанет на другой объект");
    }

    // Сторож факта подключения в Program.cs: поднимает полный стенд через
    // `TestWebApplicationFactory<Program>` и резолвит типы Dossiers из РЕАЛЬНОГО
    // DI-графа (а не из изолированного ServiceCollection). Если кто-то уберёт
    // `new DossiersSubsystem()` из `AddSubsystems(...)` в Program.cs — резолв упадёт
    // с InvalidOperationException, и тест поймает регрессию.
    [Fact]
    public void Program_RegistersDossiersSubsystem_ServicesResolvable()
    {
        using var factory = new TestWebApplicationFactory();
        var sp = factory.Services;

        // Если DossiersSubsystem отсутствует в Program.cs — любой из этих резолвов
        // бросит InvalidOperationException «Unable to resolve service for type …».
        sp.GetRequiredService<DossierStore>();
        sp.GetRequiredService<DossierRecallService>();
        sp.GetRequiredService<DossierDiscussionService>();
        sp.GetRequiredService<InstanceSecretsProvider>();
    }
}