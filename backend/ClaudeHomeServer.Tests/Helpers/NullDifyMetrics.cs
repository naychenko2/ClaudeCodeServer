using ClaudeHomeServer.Core.Telemetry;

namespace ClaudeHomeServer.Tests.Helpers;

// Пустая реализация IDifyMetrics для тестов, которым не нужно считать метрики
// (используется в ProjectKnowledgeSyncServiceTests после выноса Knowledge из Main).
public sealed class NullDifyMetrics : IDifyMetrics
{
    public int SyncErrorCount { get; private set; }
    public string? LastReason { get; private set; }

    public void RecordSyncError(string reason)
    {
        SyncErrorCount++;
        LastReason = reason;
    }
}