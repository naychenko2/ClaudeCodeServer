using System.Diagnostics.Metrics;
using ClaudeHomeServer.Services;

namespace ClaudeHomeServer.Telemetry;

/// <summary>
/// Регистрация ObservableGauges для live-метрик системы.
/// Вызывается через <see cref="GaugesRegistrarService"/> ПОСЛЕ того, как DI-контейнер
/// построен и SessionManager/ConnectionDiagnostics доступны. Это необходимо, потому что
/// ObservableGauges нужны ссылки на runtime-объекты, а <see cref="ServerMetrics"/> —
/// статический класс без DI-доступа.
/// </summary>
public static class GaugeRegistrar
{
    private static int _registered;

    /// <summary>Создаёт ObservableGauges, читающие из переданных источников. Идемпотентно.</summary>
    public static void Register(Func<int> sessionsProvider, Func<int> connectionsProvider)
    {
        if (Interlocked.Exchange(ref _registered, 1) == 1) return;

        ServerMetrics.MeterInstance.CreateObservableGauge(
            "ccs.sessions.active",
            observeValue: () => sessionsProvider(),
            unit: "sessions",
            description: "Активные сессии (зарегистрированные в SessionManager)");

        ServerMetrics.MeterInstance.CreateObservableGauge(
            "ccs.websocket.connections",
            observeValue: () => connectionsProvider(),
            unit: "connections",
            description: "Активные SignalR-соединения (из ConnectionDiagnostics)");
    }
}
