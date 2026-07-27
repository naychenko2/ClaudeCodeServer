using Microsoft.Extensions.Hosting;

namespace ClaudeHomeServer.Telemetry;

/// <summary>
/// Heartbeat для observability-of-observability (H4). Раз в 30с инкрементит
/// <c>ccs.telemetry.heartbeat</c> counter. Если в SigNoz/Aspire счётчик остановился —
/// проблема в телеметрии (OTLP exporter упал, network issues, и т.д.), а не
/// в самом приложении.
/// </summary>
public sealed class TelemetryHeartbeatService : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(30);
    private readonly ILogger<TelemetryHeartbeatService>? _log;

    public TelemetryHeartbeatService(ILogger<TelemetryHeartbeatService>? log = null) => _log = log;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _log?.LogDebug("Telemetry heartbeat started — interval {Interval}s", Interval.TotalSeconds);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                ServerMetrics.RecordHeartbeat();
            }
            catch (Exception ex)
            {
                // Heartbeat не должен ронять приложение — это диагностический сигнал,
                // а не критичный функционал
                _log?.LogDebug(ex, "Heartbeat tick failed (non-fatal)");
            }

            try
            {
                await Task.Delay(Interval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }
}
