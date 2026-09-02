using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.Composition;
using ClaudeHomeServer.Services.Deploy;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace ClaudeHomeServer.Tests.Composition;

// Сторож подсистемы «Deploy» (волна 1.6): проверяет, что DeploySubsystem.Register подключает
// ВСЕ типы, которые раньше жили в Program.cs двумя отдельными блоками (блок ADR-010 рядом
// с BackupService, блок трей-раннера рядом с KnowledgeService). Если кто-то добавит регистрацию
// в Program.cs вместо подсистемы — или вынесет регистрацию из Register — тест упадёт, и ревью
// это поймает.
//
// Конфиг минимальный: секцию TrayDeploy не задаём (по умолчанию Enabled=false — на тестовой
// машине трей-раннера нет, и подсистема должна подключиться без исключений).
//
// Проверяем РЕГИСТРАЦИЮ через `services.Any(...)` / резолв, а не полный резолв графа:
// `DeployReportService` (hosted) зависит от `SessionManager`/`NotificationService`/`BuildIdProvider`,
// `DeployHost` — от `GitService` + `ILauncherFactory`. Полный резолв потребует поднимать всю
// эту цепочку, что не задача стража регистрации Deploy.
public class DeploySubsystemRegistrationTests
{
    private static IConfiguration BuildConfig() =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                // Секция TrayDeploy не задана — тест не зависит от внешнего трей-раннера.
            })
            .Build();

    [Fact]
    public void Register_SubsystemHasStableKeyAndTitle()
    {
        var subsystem = new DeploySubsystem();

        subsystem.Key.Should().Be("deploy");
        subsystem.Title.Should().Be("Выкатка");
    }

    [Fact]
    public void Register_RegistersAllDeployServices()
    {
        var services = new ServiceCollection();
        var config = BuildConfig();

        services.AddSubsystems(config, new DeploySubsystem());

        // Сервисы из блока ADR-010 (строки ~490-494 в Program.cs до выноса в подсистему):
        // BuildIdProvider читает build-id.txt на старте (X-Build в HealthController),
        // IDeployHost — шов для тестов guard'ов и журнала, DeployService — приём заявок.
        services.Should().Contain(s => s.ServiceType == typeof(BuildIdProvider));
        services.Should().Contain(s => s.ServiceType == typeof(IDeployHost));
        services.Should().Contain(s => s.ImplementationType == typeof(DeployHost));
        services.Should().Contain(s => s.ServiceType == typeof(DeployService));

        // Сервисы из блока трей-раннера (строки ~679-681): ITrayGate — шов ради тестов
        // (реализация лежит на именованных объектах ядра Windows), DeployLauncher — сигнал
        // трею и чтение deploy-status.json.
        services.Should().Contain(s => s.ServiceType == typeof(ITrayGate));
        services.Should().Contain(s => s.ImplementationType == typeof(WindowsTrayGate));
        services.Should().Contain(s => s.ServiceType == typeof(DeployLauncher));
    }

    [Fact]
    public void Register_DeployReportService_HostedRegistered()
    {
        var services = new ServiceCollection();
        var config = BuildConfig();

        services.AddLogging();
        services.AddSubsystems(config, new DeploySubsystem());

        // Подключён hosted-сервис через `AddGatedHostedService<DeployReportService>(config)`.
        // В тестах среда не Testing по умолчанию, гейт пропускает регистрацию.
        services.Should().Contain(s => s.ImplementationType == typeof(DeployReportService));
    }

    [Fact]
    public void Register_TrayDeployOptions_IsConfigured()
    {
        var services = new ServiceCollection();
        var config = BuildConfig();

        services.AddSubsystems(config, new DeploySubsystem());

        // Configure<TrayDeployOptions>(GetSection(Section)) — резолв IOptions<TrayDeployOptions>
        // должен вернуть экземпляр с Section, читаемой из конфига (тут пустое, но не ошибка).
        services.Should().Contain(s => s.ServiceType == typeof(IConfigureOptions<TrayDeployOptions>));
    }

    // Негативная проверка: если кто-то уберёт `new DeploySubsystem()` из Program.cs,
    // регистрации Deploy должны исчезнуть. Это ловит ситуацию «вынесли подсистему,
    // но забыли подключить». Параллельный позитив (`Register_RegistersAllDeployServices`)
    // проверяет, что подсистема РЕГИСТРИРУЕТ нужные типы, а этот — что без подсистемы
    // их в списке `AddSubsystems(...)` нет.
    [Fact]
    public void AddSubsystems_WithoutDeploySubsystem_LeavesDeployServicesAbsent()
    {
        var services = new ServiceCollection();
        var config = BuildConfig();

        // Намеренно НЕ передаём `new DeploySubsystem()` — список подсистем пуст.
        services.AddSubsystems(config);

        services.Should().NotContain(s => s.ServiceType == typeof(BuildIdProvider));
        services.Should().NotContain(s => s.ServiceType == typeof(IDeployHost));
        services.Should().NotContain(s => s.ServiceType == typeof(DeployService));
        services.Should().NotContain(s => s.ServiceType == typeof(ITrayGate));
        services.Should().NotContain(s => s.ServiceType == typeof(DeployLauncher));
    }

    // Сторож факта подключения в Program.cs: поднимает полный стенд через
    // `TestWebApplicationFactory<Program>` и резолвит типы Deploy из РЕАЛЬНОГО DI-графа
    // (а не из изолированного ServiceCollection, как предыдущие тесты). Если кто-то
    // уберёт `new DeploySubsystem()` из `AddSubsystems(...)` в Program.cs — резолв
    // упадёт с InvalidOperationException, и тест поймает регрессию.
    //
    // Использует уже существующий TestWebApplicationFactory: пользователь/токены/CLI-профиль
    // там уже настроены под среду Testing, и для проверки регистрации этого достаточно.
    [Fact]
    public void Program_RegistersDeploySubsystem_ServicesResolvable()
    {
        using var factory = new TestWebApplicationFactory();
        var sp = factory.Services;

        // Если DeploySubsystem отсутствует в Program.cs — любой из этих резолвов бросит
        // InvalidOperationException «Unable to resolve service for type …».
        sp.GetRequiredService<BuildIdProvider>();
        sp.GetRequiredService<IDeployHost>();
        sp.GetRequiredService<DeployService>();
        sp.GetRequiredService<ITrayGate>();
        sp.GetRequiredService<DeployLauncher>();

        // И IOptions<TrayDeployOptions> сконфигурирован (вызовет исключение, если
        // Configure<TrayDeployOptions> в Register не отработал).
        sp.GetRequiredService<IOptions<TrayDeployOptions>>().Value.Should().NotBeNull();
    }
}