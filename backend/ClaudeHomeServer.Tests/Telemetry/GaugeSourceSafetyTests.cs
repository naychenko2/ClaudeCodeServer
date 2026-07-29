using System.Diagnostics.Metrics;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Telemetry;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ClaudeHomeServer.Tests.Telemetry;

public class GaugeSourceSafetyTests
{
    // ── Что считается «живой» сессией ────────────────────────────────────────
    // Гейдж ccs.sessions.active раньше отдавал размер реестра SessionManager, то есть все
    // чаты, поднятые из sessions.json при старте: показывал сотни, не падал после рестарта
    // и на работу не реагировал. Теперь и он, и сводка главной считают по одному предикату.

    [Theory]
    [InlineData(SessionStatus.Working, 0, true)]
    [InlineData(SessionStatus.Waiting, 0, true)]
    [InlineData(SessionStatus.Starting, 3, true)]
    [InlineData(SessionStatus.Starting, 0, false)]   // пустой новорождённый чат — не активность
    [InlineData(SessionStatus.Finished, 5, false)]
    [InlineData(SessionStatus.Orphaned, 5, false)]   // осиротел после рестарта — тем более не живой
    [InlineData(SessionStatus.Error, 5, false)]
    public void IsLive_MatchesProductDefinition(SessionStatus status, int messages, bool expected)
    {
        var session = new Session { Status = status, MessageCount = messages };

        session.IsLive().Should().Be(expected);
    }

    // ── Регистрация гейджей ──────────────────────────────────────────────────

    [Fact]
    public void CountLive_CountsOnlyLiveSessions_NotRegistrySize()
    {
        // Ровно то, что питает ccs.sessions.active. Раньше на его месте стоял
        // SessionManager.ActiveCount — размер реестра, то есть все шесть сессий ниже.
        var all = new List<Session>
        {
            new() { Status = SessionStatus.Working },
            new() { Status = SessionStatus.Waiting },
            new() { Status = SessionStatus.Starting, MessageCount = 2 },
            new() { Status = SessionStatus.Starting },   // пустой новорождённый — не активность
            new() { Status = SessionStatus.Finished, MessageCount = 9 },
            new() { Status = SessionStatus.Orphaned, MessageCount = 9 },
        };

        GaugesRegistrarService.CountLive(all).Should().Be(3);
    }

    [Fact]
    public void Register_PublishesSessionGauges()
    {
        // Регистрация идемпотентна (защита от двойного запуска hosted service) и одноразова
        // на процесс — значения тут не утверждаем: гейджи мог зарегистрировать любой тест,
        // поднимающий приложение через WebApplicationFactory. Проверяем состав инструментов:
        // «активные» и «всего» — два РАЗНЫХ гейджа, их склейка и делала метрику бессмысленной.
        var published = new List<string>();

        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, _) =>
        {
            if (instrument.Meter.Name == ServerMetrics.MeterName)
                lock (published) published.Add(instrument.Name);
        };
        listener.Start();

        var act = () =>
        {
            GaugeRegistrar.Register(() => 0, () => 0, () => 0);
            GaugeRegistrar.Register(() => 0, () => 0, () => 0);
        };
        act.Should().NotThrow();

        published.Should().Contain("ccs.sessions.active");
        published.Should().Contain("ccs.sessions.total");
        published.Should().Contain("ccs.websocket.connections");
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
