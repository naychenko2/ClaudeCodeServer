using ClaudeHomeServer.Services.Composition;
using ClaudeHomeServer.Services.Video;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Options;

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

    // Сторож прокси-инварианта (см. CLAUDE.md, раздел «Раздел „Видео“»): СМОТРИМ —
    // российский сервис, идёт БЕЗ egress-прокси; YouTube — за DPI, идёт ЧЕРЕЗ прокси.
    // Разные `Category` логгера ровно ничего о прокси не говорят, поэтому резолвим
    // `IOptionsMonitor<HttpClientFactoryOptions>`, прогоняем `HttpMessageHandlerBuilderActions`
    // на тестовом builder-е и смотрим на `PrimaryHandler.UseProxy` физически.
    [Fact]
    public void Register_SmotrimHttpClient_DisablesSystemProxy()
    {
        using var sp = BuildServiceProvider();
        var monitor = sp.GetRequiredService<IOptionsMonitor<HttpClientFactoryOptions>>();

        var builder = ApplyActions(monitor.Get(SmotrimProvider.HttpClientName), sp);
        var primary = builder.PrimaryHandler.Should().BeOfType<HttpClientHandler>().Subject;

        primary.UseProxy.Should().BeFalse(
            "СМОТРИМ — российский сервис: egress-прокси ему противопоказан, WithoutEgressProxy обязателен");
    }

    [Fact]
    public void Register_YouTubeHttpClient_KeepsSystemProxy()
    {
        using var sp = BuildServiceProvider();
        var monitor = sp.GetRequiredService<IOptionsMonitor<HttpClientFactoryOptions>>();

        var builder = ApplyActions(monitor.Get(YouTubeOAuthService.HttpClientName), sp);
        var primary = builder.PrimaryHandler.Should().BeOfType<HttpClientHandler>().Subject;

        primary.UseProxy.Should().BeTrue(
            "YouTube за DPI: через egress-прокси идут метаданные, WithoutEgressProxy тут НЕ звать");
    }

    private static ServiceProvider BuildServiceProvider()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSubsystems(BuildConfig(), new VideoSubsystem());
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

    // `HttpMessageHandlerBuilder` — абстрактный. `DefaultHttpMessageHandlerBuilder` из
    // `Microsoft.Extensions.Http` помечен internal, поэтому строим минимальный наследник:
    // подменяем Name/Services/PrimaryHandler/AdditionalHandlers, а `Build()` копируем
    // из дефолтной реализации — в самом тесте не зовём, но override обязателен.
    // `Services` пробрасываем в тест-билдер тот же, что у service provider-а, чтобы
    // `AddLogger` смог достать свой `IHttpClientLoggerFactory` и наш `QuietHttpLogger`
    // keyed-синглтон через `b.Services.GetRequiredKeyedService`.
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
