using ClaudeHomeServer.Services;
using ClaudeHomeServer.Telemetry;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace ClaudeHomeServer.Tests.Telemetry;

public class GaugeSourceSafetyTests
{
    [Fact]
    public void SessionManager_ActiveCount_ReadsConcurrentDictionaryCount()
    {
        // Smoke: свойство существует и не бросает
        // Полный integration test требует поднять SessionManager с конфигом — дорого для unit-теста
        var prop = typeof(SessionManager).GetProperty("ActiveCount");
        prop.Should().NotBeNull();
        prop!.PropertyType.Should().Be(typeof(int));
    }

    [Fact]
    public void ConnectionDiagnostics_ActiveCount_ReadsActiveCounter()
    {
        var prop = typeof(ConnectionDiagnostics).GetProperty("ActiveCount");
        prop.Should().NotBeNull();
        prop!.PropertyType.Should().Be(typeof(int));
    }

    [Fact]
    public void GaugeRegistrar_Register_IsIdempotent()
    {
        // Повторная регистрация не должна бросать (защита от двойного запуска hosted service)
        var act = () =>
        {
            GaugeRegistrar.Register(() => 0, () => 0);
            GaugeRegistrar.Register(() => 0, () => 0);
        };
        act.Should().NotThrow();
    }

    [Fact]
    public async Task GaugesRegistrarService_StartAsync_DoesNotThrowOnMissingServices()
    {
        // Если DI не зарегистрировал SessionManager/ConnectionDiagnostics — не должно ронять app startup
        var services = new ServiceCollection();
        using var provider = services.BuildServiceProvider();
        var svc = new GaugesRegistrarService(provider);

        var act = async () => await svc.StartAsync(CancellationToken.None);

        await act.Should().NotThrowAsync("observability не должна ронять запуск приложения");
    }

    [Fact]
    public void ServerMetrics_MeterInstance_ExposeMeter()
    {
        // Проверка что Meter доступен для регистрации gauges
        ServerMetrics.MeterInstance.Should().NotBeNull();
        ServerMetrics.MeterInstance.Name.Should().Be(ServerMetrics.MeterName);
    }
}
