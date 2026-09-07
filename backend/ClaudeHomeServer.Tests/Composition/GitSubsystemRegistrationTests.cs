using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Composition;
using ClaudeHomeServer.Services.Git;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Options;

namespace ClaudeHomeServer.Tests.Composition;

// Сторож подсистемы «Git» (волна 1.5): проверяет, что GitSubsystem.Register подключает
// ВСЕ типы, которые раньше жили в Program.cs тремя отдельными блоками (блок Git-сервисов
// рядом с DocumentAiService, отдельная строка CommitAttributionService рядом с SessionManager,
// и AddHttpClient("forgejo") в секции HTTP-клиентов). Если кто-то добавит регистрацию в
// Program.cs вместо подсистемы — или вынесет регистрацию из Register — тест упадёт, и ревью
// это поймает.
//
// Конфиг минимальный: ключей Forgejo не задаём (GitServerService тихо выключен —
// см. шапку GitSubsystem.cs и `GitServerService.IsConfigured`).
//
// Проверяем РЕГИСТРАЦИЮ через `services.Any(...)` / резолв типа, а не полный резолв
// графа: `GitAutoCommitService` (hosted) подписан на SessionManager, `CommitAttributionService`
// тоже зависит от SessionManager — полный резолв потребует поднимать весь SessionManager
// и его зависимости, что не задача стража Git.
public class GitSubsystemRegistrationTests
{
    private static IConfiguration BuildConfig() =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Forgejo:BaseUrl"] = "",
                ["Forgejo:AdminToken"] = "",
            })
            .Build();

    [Fact]
    public void Register_SubsystemHasStableKeyAndTitle()
    {
        var subsystem = new GitSubsystem();

        subsystem.Key.Should().Be("git");
        subsystem.Title.Should().Be("Git");
    }

    [Fact]
    public void Register_RegistersAllGitServices()
    {
        var services = new ServiceCollection();
        var config = BuildConfig();

        services.AddSubsystems(config, new GitSubsystem());

        // Сервисы из блока ~266-270 в Program.cs (до выноса в подсистему).
        services.Should().Contain(s => s.ServiceType == typeof(GitService));
        services.Should().Contain(s => s.ServiceType == typeof(GitServerService));
        services.Should().Contain(s => s.ServiceType == typeof(GitAiService));

        // Сервис из блока ~456 в Program.cs (отдельная регистрация рядом с SessionManager).
        services.Should().Contain(s => s.ServiceType == typeof(CommitAttributionService));
    }

    [Fact]
    public void Register_GitAutoCommitService_HostedRegistered()
    {
        var services = new ServiceCollection();
        var config = BuildConfig();

        services.AddLogging();
        services.AddSubsystems(config, new GitSubsystem());

        // Подключён hosted-сервис через `AddGatedHostedService<GitAutoCommitService>(config)`.
        // В тестах среда не Testing по умолчанию, гейт пропускает регистрацию.
        services.Should().Contain(s => s.ImplementationType == typeof(GitAutoCommitService));
    }

    // Сторож факта подключения в Program.cs: поднимает полный стенд через
    // `TestWebApplicationFactory<Program>` и резолвит типы Git из РЕАЛЬНОГО DI-графа
    // (а не из изолированного ServiceCollection, как предыдущие тесты). Если кто-то
    // уберёт `new GitSubsystem()` из `AddSubsystems(...)` в Program.cs — резолв
    // упадёт с InvalidOperationException, и тест поймает регрессию. Без него класс
    // дефекта «вынесли подсистему, но забыли подключить» проходит молча.
    [Fact]
    public void Program_RegistersGitSubsystem_ServicesResolvable()
    {
        using var factory = new TestWebApplicationFactory();
        var sp = factory.Services;

        // Если GitSubsystem отсутствует в Program.cs — любой из этих резолвов бросит
        // InvalidOperationException «Unable to resolve service for type …».
        sp.GetRequiredService<GitService>();
        sp.GetRequiredService<GitServerService>();
        sp.GetRequiredService<GitAiService>();
        sp.GetRequiredService<CommitAttributionService>();
    }

    // Сторож прокси-инварианта (см. шапку GitSubsystem.cs): Forgejo — локальный сервис,
    // идёт БЕЗ egress-прокси. `WithoutEgressProxy()` обязателен, иначе Forgejo молча
    // проксируется (на контейнерных владельцев — на sandbox-прокси) и не отвечает.
    // Техника проверки скопирована из VideoSubsystemRegistrationTests.
    [Fact]
    public void Register_ForgejoHttpClient_DisablesSystemProxy()
    {
        using var sp = BuildServiceProvider();
        var monitor = sp.GetRequiredService<IOptionsMonitor<HttpClientFactoryOptions>>();

        var builder = ApplyActions(monitor.Get("forgejo"), sp);
        var primary = builder.PrimaryHandler.Should().BeOfType<HttpClientHandler>().Subject;

        primary.UseProxy.Should().BeFalse(
            "Forgejo — локальный сервис: egress-прокси ему противопоказан, WithoutEgressProxy обязателен");
    }

    private static ServiceProvider BuildServiceProvider()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSubsystems(BuildConfig(), new GitSubsystem());
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
