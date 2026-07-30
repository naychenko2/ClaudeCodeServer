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

    /// <summary>
    /// Создаёт ObservableGauges, читающие из переданных источников. Идемпотентно.
    ///
    /// <paramref name="liveSessionsProvider"/> и <paramref name="totalSessionsProvider"/> —
    /// разные величины, и раньше они были склеены: под именем «активные сессии» гейдж отдавал
    /// размер реестра, то есть все чаты, когда-либо созданные и не удалённые. График показывал
    /// сотни, не падал после рестарта и не реагировал на работу — мерил не то, что обещал.
    /// </summary>
    public static void Register(
        Func<int> liveSessionsProvider, Func<int> totalSessionsProvider, Func<int> connectionsProvider)
    {
        if (Interlocked.Exchange(ref _registered, 1) == 1) return;

        ServerMetrics.MeterInstance.CreateObservableGauge(
            "ccs.sessions.active",
            observeValue: () => liveSessionsProvider(),
            unit: "sessions",
            description: "Сессии, которые сейчас работают или ждут человека (SessionLiveness.IsLive)");

        ServerMetrics.MeterInstance.CreateObservableGauge(
            "ccs.sessions.total",
            observeValue: () => totalSessionsProvider(),
            unit: "sessions",
            description: "Всего чатов в реестре SessionManager — размер стора, а не активность");

        ServerMetrics.MeterInstance.CreateObservableGauge(
            "ccs.websocket.connections",
            observeValue: () => connectionsProvider(),
            unit: "connections",
            description: "Активные SignalR-соединения (из ConnectionDiagnostics)");
    }
}
