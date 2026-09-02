using ClaudeHomeServer.Services.Composition;
using ClaudeHomeServer.Services.Tts;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Options;

namespace ClaudeHomeServer.Tests.Composition;

// Сторож подсистемы «Озвучка (Яндекс SpeechKit)»: проверяет, что TtsSubsystem.Register
// подключает ВСЕ типы, которые раньше жили в Program.cs (волна 1.4). Если кто-то добавит
// регистрацию в Program.cs вместо подсистемы — или вынесет регистрацию из Register —
// тест упадёт, и ревью это поймает.
//
// Конфиг минимальный: ключей SpeechKit не задаём (YandexTtsService.IsConfigured=false),
// это штатный режим без ключа (контракт фолбэка на голос браузера живёт в контроллере).
//
// Проверяем РЕГИСТРАЦИЮ через `services.Any(...)`, а не резолв: `VoiceResolver` зависит
// от `PersonaManager` (Services/ корень) — полный резолв потребовал бы поднимать
// граф персон, это не задача стража.
public class TtsSubsystemRegistrationTests
{
    private static IConfiguration BuildConfig() =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Yandex:SpeechKit:ApiKey"] = "",
                ["Yandex:SpeechKit:FolderId"] = "",
            })
            .Build();

    [Fact]
    public void Register_SubsystemHasStableKeyAndTitle()
    {
        var subsystem = new TtsSubsystem();

        subsystem.Key.Should().Be("tts");
        subsystem.Title.Should().Be("Озвучка (Яндекс SpeechKit)");
    }

    [Fact]
    public void Register_RegistersAllTtsServices()
    {
        var services = new ServiceCollection();
        var config = BuildConfig();

        services.AddSubsystems(config, new TtsSubsystem());

        // Сервисы, бывшие прямо в Program.cs до выноса (волна 1.4).
        services.Should().Contain(s => s.ServiceType == typeof(YandexTtsService));
        services.Should().Contain(s => s.ServiceType == typeof(VoiceResolver));
    }

    // Сторож прокси-инварианта (см. CLAUDE.md, раздел «Голосовой режим чата» и шапку
    // TtsSubsystem.cs): SpeechKit — внешний сервис за DPI, идёт ЧЕРЕЗ egress-прокси.
    // `WithoutEgressProxy()` тут НЕ звать — без прокси API просто не отвечает
    // (как у `fal-ai`, `glif`, биллинга Яндекса). Техника проверки скопирована из
    // VideoSubsystemRegistrationTests / YandexSubsystemRegistrationTests.
    [Fact]
    public void Register_YandexTtsHttpClient_KeepsSystemProxy()
    {
        using var sp = BuildServiceProvider();
        var monitor = sp.GetRequiredService<IOptionsMonitor<HttpClientFactoryOptions>>();

        var builder = ApplyActions(monitor.Get(YandexTtsService.HttpClientName), sp);
        var primary = builder.PrimaryHandler.Should().BeOfType<HttpClientHandler>().Subject;

        primary.UseProxy.Should().BeTrue(
            "SpeechKit — внешний сервис за DPI: egress-прокси обязателен, WithoutEgressProxy тут НЕ звать");
    }

    private static ServiceProvider BuildServiceProvider()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSubsystems(BuildConfig(), new TtsSubsystem());
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
