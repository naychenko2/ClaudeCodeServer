using ClaudeHomeServer.Services.Backgrounds;
using ClaudeHomeServer.Services.Composition;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace ClaudeHomeServer.Tests.Composition;

// Сторож подсистемы «Backgrounds» (волна 2): проверяет, что BackgroundsSubsystem.Register
// подключает ВСЕ типы, которые раньше жили в Program.cs тремя отдельными строками
// (ProjectBackgroundService рядом с DocumentAiService, ProjectBackgroundBackfill и его
// hosted-сервис блоком ниже). Если кто-то добавит регистрацию в Program.cs вместо
// подсистемы — или вынесет регистрацию из Register — тест упадёт, и ревью это поймает.
public class BackgroundsSubsystemRegistrationTests
{
    private static IConfiguration BuildConfig() =>
        new ConfigurationBuilder().Build();

    [Fact]
    public void Register_SubsystemHasStableKeyAndTitle()
    {
        var subsystem = new BackgroundsSubsystem();

        subsystem.Key.Should().Be("backgrounds");
        subsystem.Title.Should().Be("Фоны проектов");
    }

    [Fact]
    public void Register_RegistersAllBackgroundServices()
    {
        var services = new ServiceCollection();
        var config = BuildConfig();

        services.AddSubsystems(config, new BackgroundsSubsystem());

        services.Should().Contain(s => s.ServiceType == typeof(ProjectBackgroundService));
        services.Should().Contain(s => s.ServiceType == typeof(ProjectBackgroundBackfill));
    }

    [Fact]
    public void Register_ProjectBackgroundBackfillService_HostedRegistered()
    {
        var services = new ServiceCollection();
        var config = BuildConfig();

        services.AddLogging();
        services.AddSubsystems(config, new BackgroundsSubsystem());

        // Подключён hosted-сервис через `AddGatedHostedService<ProjectBackgroundBackfillService>(config)`.
        // В тестах среда не Testing по умолчанию, гейт пропускает регистрацию.
        services.Should().Contain(s => s.ImplementationType == typeof(ProjectBackgroundBackfillService));
    }

    // Сторож факта подключения в Program.cs: поднимает полный стенд через
    // `TestWebApplicationFactory<Program>` и резолвит типы Backgrounds из РЕАЛЬНОГО DI-графа
    // (а не из изолированного ServiceCollection, как предыдущие тесты). Если кто-то
    // уберёт `new BackgroundsSubsystem()` из `AddSubsystems(...)` в Program.cs — резолв
    // упадёт с InvalidOperationException, и тест поймает регрессию.
    [Fact]
    public void Program_RegistersBackgroundsSubsystem_ServicesResolvable()
    {
        using var factory = new TestWebApplicationFactory();
        var sp = factory.Services;

        sp.GetRequiredService<ProjectBackgroundService>();
        sp.GetRequiredService<ProjectBackgroundBackfill>();
    }
}
