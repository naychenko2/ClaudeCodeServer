namespace ClaudeHomeServer.Core.Telemetry;

public interface IDifyMetrics
{
    void RecordSyncError(string reason);
}
