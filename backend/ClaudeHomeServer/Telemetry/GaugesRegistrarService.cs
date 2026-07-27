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
                sessionsProvider: () => sessions.ActiveCount,
                connectionsProvider: () => connections.ActiveCount);
        }
        catch
        {
            // Не роняем запуск приложения из-за observability
        }
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
