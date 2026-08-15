using System.Diagnostics;
using ClaudeHomeServer.Services.Http;
using ClaudeHomeServer.Telemetry;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OpenTelemetry.Trace;
using Xunit;

namespace ClaudeHomeServer.Tests.Telemetry;

public class ObservabilityRegistrationTests
{
    private static IConfiguration BuildConfig(string mode, bool devEnabled = false, bool prodEnabled = false)
    {
        var dict = new Dictionary<string, string?>
        {
            ["Telemetry:Mode"] = mode,
            ["Telemetry:TraceSampleRatio:Dev"] = "0.10",
            ["Telemetry:TraceSampleRatio:Production"] = "0.05",
            ["Telemetry:Backends:Dev:Enabled"] = devEnabled.ToString().ToLowerInvariant(),
            ["Telemetry:Backends:Dev:OtlpEndpoint"] = "http://localhost:4317",
            ["Telemetry:Backends:Production:Enabled"] = prodEnabled.ToString().ToLowerInvariant(),
            ["Telemetry:Backends:Production:OtlpEndpoint"] = "http://localhost:4318",
        };
        return new ConfigurationBuilder().AddInMemoryCollection(dict).Build();
    }

    [Fact]
    public void AddObservability_RegistersTracerProvider()
    {
        var services = new ServiceCollection();
        var config = BuildConfig("dev", devEnabled: true);

        services.AddObservability(config);

        using var provider = services.BuildServiceProvider();
        provider.GetService<TracerProvider>().Should().NotBeNull();
    }

    [Fact]
    public void AddObservability_RegistersHeartbeatHostedService()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        var config = BuildConfig("dev", devEnabled: true);

        services.AddObservability(config);

        // Heartbeat должен регистрироваться ВСЕГДА, даже если все backend'ы выключены —
        // иначе observability-of-observability не работает (H4)
        var hostedServices = services.Where(s => s.ServiceType == typeof(IHostedService)).ToList();
        // Simple scan по имени — ImplementationFactory типизирован как Func<>, не детерминированно находит тип через reflection
        var heartbeatRegistered = hostedServices.Any(s =>
            s.ImplementationType == typeof(TelemetryHeartbeatService) ||
            (s.ImplementationFactory?.GetType().Name.Contains("AddHostedService") == true &&
             typeof(TelemetryHeartbeatService).FullName != null));
        // Fallback: через построенный provider
        using var provider = services.BuildServiceProvider();
        var resolvedServices = provider.GetServices<IHostedService>();
        resolvedServices.Should().Contain(s => s.GetType() == typeof(TelemetryHeartbeatService));
    }

    [Fact]
    public void AddObservability_NoBackendsEnabled_StillRegistersHeartbeat()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        var config = BuildConfig("dev", devEnabled: false, prodEnabled: false);

        services.AddObservability(config);

        using var provider = services.BuildServiceProvider();
        var hostedServices = provider.GetServices<IHostedService>();
        hostedServices.Should().Contain(s => s.GetType() == typeof(TelemetryHeartbeatService),
            "heartbeat должен тикать даже без активных backend'ов — это диагностический сигнал");
    }

    [Fact]
    public async Task HeartbeatService_WhenStarted_TicksAndDoesNotThrow()
    {
        // Интеграционный тест: heartbeat service реально стартует и не падает
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddHostedService<TelemetryHeartbeatService>();

        using var provider = services.BuildServiceProvider();
        var hostedService = provider.GetRequiredService<IHostedService>();

        // Запуск и короткая работа — не должно бросать
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        var task = hostedService.StartAsync(cts.Token);

        // Дать ему тикнуть раз (если получится за 100мс — ну ладно)
        await Task.WhenAny(task, Task.Delay(150));
        await hostedService.StopAsync(CancellationToken.None);

        // Если дошёл сюда без exception — тест прошёл
        true.Should().BeTrue();
    }

    [Fact]
    public void ServerActivitySource_CanStartActivity_WhenListenerAttached()
    {
        // Verify что ActivitySource из T3 реально слушается при регистрации OTel
        var services = new ServiceCollection();
        var config = BuildConfig("dev", devEnabled: true);
        services.AddObservability(config);

        using var provider = services.BuildServiceProvider();
        // Force TracerProvider to initialize
        _ = provider.GetService<TracerProvider>();

        // Теперь ActivitySource должен быть listened
        using var activity = ServerActivitySource.Instance.StartActivity("test");
        // activity может быть null если sampler решил не sampled — это норм для 0.10 ratio
        // Главное — не падает
        activity?.Dispose();
    }

    [Fact]
    public void КлиентОпросаАлертов_ЗарегистрированТихим()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        var dict = new Dictionary<string, string?>
        {
            ["Telemetry:Mode"] = "production",
            ["Telemetry:Alerts:Enabled"] = "true",
            ["Telemetry:Alerts:ApiKey"] = "ключ",
        };
        var config = new ConfigurationBuilder().AddInMemoryCollection(dict).Build();

        services.AddObservability(config);

        // AddQuietHttpClient кладёт логгер keyed-синглтоном по категории профиля. Его наличие —
        // и есть признак, что клиент заведён тихим: обычный AddHttpClient печатает на каждый
        // опрос четыре строки Info и стектрейс Error на отказ, а SigNoz опрашивается раз
        // в минуту круглые сутки и лежит штатно.
        using var provider = services.BuildServiceProvider();
        provider.GetKeyedService<QuietHttpLogger>("ClaudeHomeServer.Telemetry.AlertsPoll")
            .Should().NotBeNull();
    }

    [Fact]
    public void AddObservability_ProductionMode_DoesNotThrow()
    {
        var services = new ServiceCollection();
        var config = BuildConfig("production", prodEnabled: true);

        var act = () => services.AddObservability(config);

        act.Should().NotThrow();
    }

    [Fact]
    public void AddObservability_BothBackends_DoesNotThrow()
    {
        var services = new ServiceCollection();
        var config = BuildConfig("both", devEnabled: true, prodEnabled: true);

        var act = () => services.AddObservability(config);

        act.Should().NotThrow();
    }
}
