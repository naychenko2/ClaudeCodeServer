using ClaudeHomeServer.Services.Composition;
using ClaudeHomeServer.Services.Http;
using ClaudeHomeServer.Services.Video;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace ClaudeHomeServer.Tests.Composition;

// Сторож подсистемы «Видео»: проверяет, что VideoSubsystem.Register подключает ВСЕ
// типы, которые раньше жили в Program.cs. Если кто-то добавит провайдера в Program.cs
// вместо подсистемы — или вынесет регистрацию из Register — тест упадёт, и ревью это поймает.
//
// Конфиг минимальный: ключей YouTube не задаём (провайдер выключится — это нормально,
// IsConfigured=false), Smotrim ключей не требует.
//
// Проверяем РЕГИСТРАЦИЮ через `services.Any(...)`, а не резолв: у YouTubeOAuthService
// есть исходящая зависимость `McpSecretStore` (сознательная граница: секреты OAuth лежат
// в общем сторе per-owner; см. CLAUDE.md и шапку VideoSubsystem.cs). Полный резолв
// требует поднимать Mcp-подсистему — это не задача стража Video.
public class VideoSubsystemRegistrationTests
{
    private static IConfiguration BuildConfig() =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Video:YouTube:ClientId"] = "",
                ["Video:YouTube:ClientSecret"] = "",
            })
            .Build();

    [Fact]
    public void Register_SubsystemHasStableKeyAndTitle()
    {
        var subsystem = new VideoSubsystem();

        subsystem.Key.Should().Be("video");
        subsystem.Title.Should().Be("Видео");
    }

    [Fact]
    public void Register_RegistersAllVideoServices()
    {
        var services = new ServiceCollection();
        var config = BuildConfig();

        services.AddSubsystems(config, new VideoSubsystem());

        // Сервисы, бывшие прямо в Program.cs до выноса.
        services.Should().Contain(s => s.ServiceType == typeof(VideoOptions));
        services.Should().Contain(s => s.ServiceType == typeof(YouTubeOAuthService));
        services.Should().Contain(s => s.ServiceType == typeof(VideoProviderRegistry));

        // Оба IVideoProvider зарегистрированы как singletons.
        var providerDescriptors = services.Where(s => s.ServiceType == typeof(IVideoProvider)).ToList();
        providerDescriptors.Should().HaveCount(2);
        providerDescriptors.Select(s => s.ImplementationType?.Name)
            .Should().Contain(["SmotrimProvider", "YouTubeProvider"]);
    }

    [Fact]
    public void Register_RegistersBothHttpClientsWithDistinctCategories()
    {
        var services = new ServiceCollection();
        var config = BuildConfig();

        services.AddSubsystems(config, new VideoSubsystem());

        // Тихого клиента отличаем по keyed-синглтону логгера (см. ObservabilityRegistrationTests):
        // на каждый профиль свой Category, и по нему же AddQuietHttpClient кладёт свой
        // QuietHttpLogger. Два разных Category = два разных профиля = две регистрации
        // клиентов. Сам факт вызова `WithoutEgressProxy()` на нужном профиле тест
        // НЕ проверяет — это остаётся за ревью и отдельной задачей на «физическую»
        // проверку инварианта (см. ADR-014).
        services.Should().Contain(s =>
            s.ServiceType == typeof(QuietHttpLogger) &&
            s.IsKeyedService &&
            s.ServiceKey as string == "ClaudeHomeServer.Video.Smotrim");
        services.Should().Contain(s =>
            s.ServiceType == typeof(QuietHttpLogger) &&
            s.IsKeyedService &&
            s.ServiceKey as string == "ClaudeHomeServer.Video.YouTube");
    }
}
