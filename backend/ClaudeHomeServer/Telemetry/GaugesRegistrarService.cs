using System.Diagnostics.Metrics;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using Microsoft.Extensions.Hosting;

namespace ClaudeHomeServer.Telemetry;

/// <summary>
/// Регистрирует ObservableGauges для sessions/websockets ПОСЛЕ построения DI-контейнера.
/// Запускается как HostedService — к моменту StartAsync все singletons (SessionManager,
/// ConnectionDiagnostics) уже созданы.
/// </summary>
public sealed class GaugesRegistrarService : IHostedService
{
    private readonly IServiceProvider _sp;
    public GaugesRegistrarService(IServiceProvider sp) => _sp = sp;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            var sessions = _sp.GetRequiredService<SessionManager>();
            var connections = _sp.GetRequiredService<ConnectionDiagnostics>();
            GaugeRegistrar.Register(
                liveSessionsProvider: () => CountLive(sessions.GetAll()),
                totalSessionsProvider: () => sessions.ActiveCount,
                connectionsProvider: () => connections.ActiveCount);

            var reconciler = _sp.GetRequiredService<Services.Knowledge.KnowledgeIndexReconciler>();
            GaugeRegistrar.RegisterKnowledgeReconcile(
                errorDocumentsProvider: () => ErrorDocuments(reconciler),
                recoveredProvider: () => reconciler.RecoveredTotal);
        }
        catch
        {
            // Не роняем запуск приложения из-за observability
        }
        return Task.CompletedTask;
    }

    /// <summary>
    /// Сколько сессий сейчас реально работают или ждут человека.
    ///
    /// Отдельная функция, а не <c>SessionManager.ActiveCount</c>: тот отдаёт размер реестра —
    /// ВСЕ чаты, поднятые из sessions.json при старте. Гейдж <c>ccs.sessions.active</c> раньше
    /// читал именно его и потому показывал сотни, не падал после рестарта и не реагировал на
    /// работу. Предикат общий с сводкой главной (<c>SessionLiveness.IsLive</c>), чтобы «активные»
    /// в UI и в метрике означали одно и то же.
    /// </summary>
    internal static int CountLive(IReadOnlyCollection<Session> all) => all.Count(s => s.IsLive());

    /// <summary>
    /// Срез error-документов Dify для многотегового гейджа. Агрегация по типу датасета
    /// живёт в реконсайлере (<c>AggregateByType</c>): Label цели содержит id персоны и путь
    /// проекта, в теги это отдавать нельзя.
    /// </summary>
    internal static IEnumerable<Measurement<long>> ErrorDocuments(
        Services.Knowledge.KnowledgeIndexReconciler reconciler) =>
        Services.Knowledge.KnowledgeIndexReconciler.AggregateByType(reconciler.LastCounts)
            .Select(x => new Measurement<long>(x.Count,
                new KeyValuePair<string, object?>("dataset_type", x.DatasetType),
                new KeyValuePair<string, object?>("healability", x.Healable ? "healable" : "unhealable")));

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
