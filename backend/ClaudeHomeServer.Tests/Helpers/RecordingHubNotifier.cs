using ClaudeHomeServer.Services;

namespace ClaudeHomeServer.Tests.Helpers;

// Запись вызовов IKnowledgeHubNotifier.BroadcastKnowledgeChangedAsync для тестов
// ProjectKnowledgeSyncService (заменил прямой IHubContext<SessionHub> на шов IKnowledgeHubNotifier
// после выноса Knowledge в отдельный .csproj).
public sealed class RecordingHubNotifier : IKnowledgeHubNotifier
{
    private readonly object _gate = new();
    private readonly List<(string UserId, string Action, string? DatasetId)> _calls = [];

    public IReadOnlyList<(string UserId, string Action, string? DatasetId)> Calls
    {
        get { lock (_gate) return _calls.ToList(); }
    }

    public Task BroadcastKnowledgeChangedAsync(string userId, string action, string? datasetId)
    {
        lock (_gate) _calls.Add((userId, action, datasetId));
        return Task.CompletedTask;
    }
}