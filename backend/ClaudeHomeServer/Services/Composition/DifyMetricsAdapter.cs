using ClaudeHomeServer.Core.Telemetry;

namespace ClaudeHomeServer.Services.Composition;

// Реализация IDifyMetrics (Core) поверх ServerMetrics (Main).
// Тонкая обёртка: RecordSyncError → ServerMetrics.RecordDifySyncError.
// Создана ради выноса ProjectKnowledgeSyncService из Main в отдельный .csproj:
// Knowledge-вертикаль не имеет прямого доступа к ServerMetrics.
public sealed class DifyMetricsAdapter : IDifyMetrics
{
    public void RecordSyncError(string reason) =>
        ServerMetrics.RecordDifySyncError(reason);
}