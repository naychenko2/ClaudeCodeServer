using System.Reflection;
using ClaudeHomeServer.Services.Composition;
using ClaudeHomeServer.Services.Reader;
using ClaudeHomeServer.Tests.Helpers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace ClaudeHomeServer.Tests.Services.Reader;

// Сторож регистрации `ReaderSubsystem`: гарантирует, что подсистема подключает и
// `ReaderService`/`ReaderQuotaService`, и именованный клиент `link-reader` с тем
// самым `SocketsHttpHandler` от `ReaderHttpHandlerFactory` — TOCTOU-safe SSRF-линией
// (ADR-005 §2: `UseProxy=false`, `ConnectCallback` сам резолвит хост и режет
// приватные адреса). Если кто-то тихо «унифицирует» клиент под `AddQuietHttpClient`
// или забудет `ConfigurePrimaryHttpMessageHandler`, тест хендлера упадёт с понятным
// сообщением, а не молча пустит трафик через системный egress-прокси (а тот
// релеит на приватные адреса без фильтрации).
public class ReaderSubsystemRegistrationTests
{
    private static (IServiceCollection services, IConfiguration config) NewHost()
    {
        var services = new ServiceCollection();
        // `ReaderService` зависит от `ILogger<T>` и `IConfiguration` — без этого
        // `GetService<ReaderService>()` кинет NRE на этапе конструирования,
        // и сторож бы провалился по ложной причине.
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Reader:AcceptLanguage"] = "ru-RU,ru;q=0.9",
            })
            .Build();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(config);
        return (services, config);
    }

    [Fact]
    public void Register_РезолвитReaderServiceИКвоту()
    {
        var (services, config) = NewHost();
        services.AddSubsystems(config, new ReaderSubsystem());

        using var provider = services.BuildServiceProvider();

        var reader = provider.GetService<ReaderService>();
        var quota = provider.GetService<ReaderQuotaService>();

        Assert.NotNull(reader);
        Assert.NotNull(quota);
    }

    [Fact]
    public void Register_ИменованныйКлиентСФабрикойХендлера_SocketsHttpHandlerБезПрокси()
    {
        var (services, config) = NewHost();
        services.AddSubsystems(config, new ReaderSubsystem());

        using var provider = services.BuildServiceProvider();

        var factory = provider.GetRequiredService<IHttpClientFactory>();
        using var client = factory.CreateClient(ReaderService.HttpClientName);

        // Без `ConfigurePrimaryHttpMessageHandler(ReaderHttpHandlerFactory.Create)`
        // ASP.NET кладёт дефолтный `HttpClientHandler` с `UseProxy=true` — и клиент
        // уезжает через системный egress, а тот релеит на приватные адреса без
        // фильтрации (ADR-005 §2). Тест должен падать в этом случае.
        var primary = UnwrapPrimary(client);
        Assert.IsType<SocketsHttpHandler>(primary);
        var sockets = (SocketsHttpHandler)primary;
        Assert.False(sockets.UseProxy);
    }

    // Сторож факта подключения в Program.cs: поднимает полный стенд через
    // `TestWebApplicationFactory<Program>` и резолвит типы Reader из РЕАЛЬНОГО DI-графа.
    // Если `new ReaderSubsystem()` убрали из `AddSubsystems(...)` — резолв падает
    // с InvalidOperationException, регрессия ловится.
    [Fact]
    public void Program_RegistersReaderSubsystem_ServicesResolvable()
    {
        using var factory = new TestWebApplicationFactory();
        var sp = factory.Services;

        sp.GetRequiredService<ReaderService>();
        sp.GetRequiredService<ReaderQuotaService>();
    }

    // HttpClient оборачивает primary-билдер декораторами (Logging/Activities), поэтому
    // реальный handler сидит в `InnerHandler` цепочки. Идём до non-delegating.
    //
    // Где достать сам primary-handler:
    // - .NET 8/9: HttpClient композировал HttpMessageInvoker, у него было internal
    //   свойство `PrimaryHandler` на самом HttpClient.
    // - .NET 10+: HttpClient НАСЛЕДУЕТ HttpMessageInvoker, у того есть internal поле
    //   `_handler`. Старое свойство `PrimaryHandler` вернуло null через рефлексию —
    //   на этой версии его просто нет (диагностика 2026-09-02).
    // Поддерживаем обе схемы: сначала поле `_handler` на `HttpMessageInvoker`, фолбэк —
    // свойство `PrimaryHandler` на `HttpClient`.
    private static HttpMessageHandler UnwrapPrimary(HttpClient client)
    {
        var primary = GetPrimaryHandler(client);
        Assert.NotNull(primary);

        while (primary is DelegatingHandler decorating)
        {
            Assert.NotNull(decorating.InnerHandler);
            primary = decorating.InnerHandler;
        }

        return primary!;
    }

    private static HttpMessageHandler? GetPrimaryHandler(HttpClient client)
    {
        var invokerField = typeof(HttpMessageInvoker).GetField(
            "_handler", BindingFlags.Instance | BindingFlags.NonPublic);
        if (invokerField != null)
            return (HttpMessageHandler?)invokerField.GetValue(client);

        var prop = typeof(HttpClient).GetProperty(
            "PrimaryHandler", BindingFlags.Instance | BindingFlags.NonPublic);
        if (prop != null)
            return (HttpMessageHandler?)prop.GetValue(client);

        return null;
    }
}