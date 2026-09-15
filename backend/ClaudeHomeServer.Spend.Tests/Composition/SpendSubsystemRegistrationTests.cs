using ClaudeHomeServer.Services.Composition;
using ClaudeHomeServer.Services.Spend;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ClaudeHomeServer.Tests.Composition;

// Сторож подсистемы «Spend» (волна 2): проверяет, что `SpendSubsystem.Register` подключает
// ВСЕ типы, которые раньше жили в Program.cs одним блоком «Аналитика расхода токенов».
// Если кто-то добавит регистрацию Spend в Program.cs вместо подсистемы — или вынесет
// регистрацию из Register — тест упадёт, и ревью это поймает.
//
// Ключевой инвариант — `ISpendCollector` и `SpendStore` указывают на ОДИН инстанс
// (`sp => sp.GetRequiredService<SpendStore>()`). Тест `Register_SpendCollector_IsSameInstanceAsStore`
// ловит регрессию «поставили `AddSingleton<ISpendCollector, SpendStore>()` и завели второй стор».
public class SpendSubsystemRegistrationTests
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
        // `SpendStore(IConfiguration, ILogger?)` не сможет выбрать конструктор.
        services.AddSingleton<IConfiguration>(config);
        services.AddLogging();
        services.AddSubsystems(config, new SpendSubsystem());
        return services;
    }

    [Fact]
    public void Register_SubsystemHasStableKeyAndTitle()
    {
        var subsystem = new SpendSubsystem();

        subsystem.Key.Should().Be("spend");
        subsystem.Title.Should().Be("Аналитика расхода");
    }

    [Fact]
    public void Register_RegistersAllSpendServices()
    {
        var services = BuildServices(BuildConfig(Path.Combine(Path.GetTempPath(), Path.GetRandomFileName())));

        services.Should().Contain(s => s.ServiceType == typeof(SpendStore));
        services.Should().Contain(s => s.ServiceType == typeof(ISpendCollector));
        services.Should().Contain(s => s.ServiceType == typeof(SpendAnalyticsService));
        services.Should().Contain(s => s.ServiceType == typeof(TaskPromptMetricsStore));
    }

    [Fact]
    public void Register_SpendMaintenanceService_HostedRegistered()
    {
        var services = BuildServices(BuildConfig(Path.Combine(Path.GetTempPath(), Path.GetRandomFileName())));

        // `AddGatedHostedService<SpendMaintenanceService>(config)`. В тестах среда не
        // Testing по умолчанию, гейт пропускает регистрацию.
        services.Should().Contain(s => s.ImplementationType == typeof(SpendMaintenanceService));
    }

    // Инвариант «ISpendCollector и SpendStore — один инстанс»: `AddSingleton<ISpendCollector,
    // SpendStore>()` создал бы второй стор и разъехался с коллектором. Проверяем через
    // `BuildServiceProvider` в изолированной коллекции — резолвим оба типа и сравниваем
    // ссылки. `SpendStore` требует `DataPath` в конфиге, поэтому кладём временный путь
    // через `Path.Combine`/`Path.GetRandomFileName()` — кросс-платформенно для CI на Linux.
    [Fact]
    public void Register_SpendCollector_IsSameInstanceAsStore()
    {
        var services = BuildServices(BuildConfig(Path.Combine(Path.GetTempPath(), Path.GetRandomFileName())));

        using var sp = services.BuildServiceProvider();
        var store = sp.GetRequiredService<SpendStore>();
        var collector = sp.GetRequiredService<ISpendCollector>();

        ReferenceEquals(collector, store).Should().BeTrue(
            "ISpendCollector обязан указывать на тот же экземпляр SpendStore — иначе " +
            "коллектор пишет в одну копию стора, а читает из другой, и данные расходятся");
    }

    // Сторож факта динамической загрузки: поднимает полный стенд через
    // `TestWebApplicationFactory<Program>` и резолвит типы Spend из РЕАЛЬНОГО DI-графа
    // (а не из изолированного ServiceCollection, как предыдущие тесты). Spend —
    // динамический модуль: ModuleLoader загружает dll, находит IAppSubsystem и
    // вызывает Register. Если dll не на месте (путь из appsettings.json не резолвится)
    // или ModuleLoader не находит тип — резолв упадёт с InvalidOperationException.
    [Fact]
    public void Program_RegistersSpendSubsystem_ServicesResolvable()
    {
        using var factory = new TestWebApplicationFactory();
        var sp = factory.Services;

        // Если SpendSubsystem отсутствует в Program.cs — любой из этих резолвов бросит
        // InvalidOperationException «Unable to resolve service for type …».
        sp.GetRequiredService<SpendStore>();
        sp.GetRequiredService<SpendAnalyticsService>();
        sp.GetRequiredService<TaskPromptMetricsStore>();
        sp.GetRequiredService<ISpendCollector>();
    }
}