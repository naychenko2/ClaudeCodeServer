using ClaudeHomeServer.Services.Composition;
using ClaudeHomeServer.Services.Yandex;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Options;

namespace ClaudeHomeServer.Tests.Composition;

// Сторож подсистемы «Яндекс (биллинг)»: проверяет, что YandexSubsystem.Register
// подключает ВСЕ типы, которые раньше жили в Program.cs. Если кто-то добавит
// регистрацию в Program.cs вместо подсистемы — или вынесет регистрацию из Register —
// тест упадёт, и ревью это поймает.
//
// Конфиг минимальный: ключей биллинга не задаём, фича выключится — это нормально,
// YandexAccountService.Enabled/IsConfigured=false.
//
// Проверяем РЕГИСТРАЦИЮ через `services.Any(...)`, а не резолв: резолв потребовал бы
// поднимать HTTP-инфраструктуру, это не задача стража.
public class YandexSubsystemRegistrationTests
{
    private static IConfiguration BuildConfig() =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Yandex:Billing:ServiceAccountId"] = "",
                ["Yandex:Billing:KeyId"] = "",
                ["Yandex:Billing:PrivateKey"] = "",
            })
            .Build();

    [Fact]
    public void Register_SubsystemHasStableKeyAndTitle()
    {
        var subsystem = new YandexSubsystem();

        subsystem.Key.Should().Be("yandex");
        subsystem.Title.Should().Be("Яндекс (биллинг)");
    }

    [Fact]
    public void Register_RegistersAllYandexBillingServices()
    {
        var services = new ServiceCollection();
        var config = BuildConfig();

        services.AddSubsystems(config, new YandexSubsystem());

        // Сервисы, бывшие прямо в Program.cs до выноса.
        services.Should().Contain(s => s.ServiceType == typeof(YandexIamTokenProvider));
        services.Should().Contain(s => s.ServiceType == typeof(YandexAccountService));
    }

    // Сторож прокси-инварианта (см. CLAUDE.md, раздел «Раздел „Видео“» и шапку
    // YandexSubsystem.cs): Яндекс-биллинг — внешний сервис за DPI, идёт ЧЕРЕЗ
    // egress-прокси. `WithoutEgressProxy()` тут НЕ звать. Техника проверки скопирована
    // из VideoSubsystemRegistrationTests после фикса прокси-инварианта.
    [Fact]
    public void Register_YandexBillingHttpClient_KeepsSystemProxy()
    {
        using var sp = BuildServiceProvider();
        var monitor = sp.GetRequiredService<IOptionsMonitor<HttpClientFactoryOptions>>();

        var builder = ApplyActions(monitor.Get(YandexIamTokenProvider.HttpClientName), sp);
        var primary = builder.PrimaryHandler.Should().BeOfType<HttpClientHandler>().Subject;

        primary.UseProxy.Should().BeTrue(
            "Яндекс-биллинг — внешний сервис за DPI: egress-прокси обязателен, WithoutEgressProxy тут НЕ звать");
    }

    private static ServiceProvider BuildServiceProvider()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSubsystems(BuildConfig(), new YandexSubsystem());
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