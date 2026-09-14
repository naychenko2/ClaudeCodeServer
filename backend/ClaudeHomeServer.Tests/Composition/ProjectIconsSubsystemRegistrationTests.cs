using ClaudeHomeServer.Services.Composition;
using ClaudeHomeServer.Services.ProjectIcons;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace ClaudeHomeServer.Tests.Composition;

// Сторож подсистемы «ProjectIcons» (волна 2): проверяет, что ProjectIconsSubsystem.Register
// подключает ВСЕ типы, которые раньше жили в Program.cs отдельным блоком
// (ProjectIconGlyphService, ProjectIconMigration и его hosted-сервис). Если кто-то
// добавит регистрацию в Program.cs вместо подсистемы — или вынесет регистрацию из
// Register — тест упадёт, и ревью это поймает.
public class ProjectIconsSubsystemRegistrationTests
{
    private static IConfiguration BuildConfig() =>
        new ConfigurationBuilder().Build();

    [Fact]
    public void Register_SubsystemHasStableKeyAndTitle()
    {
        var subsystem = new ProjectIconsSubsystem();

        subsystem.Key.Should().Be("project-icons");
        subsystem.Title.Should().Be("Значки проектов");
    }

    [Fact]
    public void Register_RegistersAllProjectIconServices()
    {
        var services = new ServiceCollection();
        var config = BuildConfig();

        services.AddSubsystems(config, new ProjectIconsSubsystem());

        services.Should().Contain(s => s.ServiceType == typeof(ProjectIconGlyphService));
        services.Should().Contain(s => s.ServiceType == typeof(ProjectIconMigration));
    }

    [Fact]
    public void Register_ProjectIconMigrationService_HostedRegistered()
    {
        var services = new ServiceCollection();
        var config = BuildConfig();

        services.AddLogging();
        services.AddSubsystems(config, new ProjectIconsSubsystem());

        // Подключён hosted-сервис через `AddGatedHostedService<ProjectIconMigrationService>(config)`.
        // В тестах среда не Testing по умолчанию, гейт пропускает регистрацию.
        services.Should().Contain(s => s.ImplementationType == typeof(ProjectIconMigrationService));
    }

    // Сторож факта подключения в Program.cs: поднимает полный стенд через
    // `TestWebApplicationFactory<Program>` и резолвит типы ProjectIcons из РЕАЛЬНОГО
    // DI-графа (а не из изолированного ServiceCollection, как предыдущие тесты).
    // Если кто-то уберёт `new ProjectIconsSubsystem()` из `AddSubsystems(...)` в
    // Program.cs — резолв упадёт с InvalidOperationException, и тест поймает регрессию.
    // Оба синглтона подсистемы, как у `Git` — иначе изолированные тесты пройдут мимо
    // выноса одного типа обратно в Program.cs.
    [Fact]
    public void Program_RegistersProjectIconsSubsystem_ServicesResolvable()
    {
        using var factory = new TestWebApplicationFactory();
        var sp = factory.Services;

        sp.GetRequiredService<ProjectIconGlyphService>();
        sp.GetRequiredService<ProjectIconMigration>();
    }
}
