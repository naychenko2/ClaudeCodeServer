using ClaudeHomeServer.Services.Access;

namespace ClaudeHomeServer.Tests.Helpers;

// Швы общих проектов, ставшие обязательными параметрами NotesToolset (рамка хода S2a-3,
// гейт актора S2c-6): тестам вики нужна прежняя картина — широкая рамка и ни одного отказа
// по актору (владелец работает в своём проекте).
internal sealed class NotesWideTurnFrame : ITurnFrameSource
{
    public TurnFrameKind Frame(string? sessionId, string ownerId) => TurnFrameKind.Wide;
}

internal sealed class NotesNoActorDenials : IActorRightGate
{
    public string? Denied(string server, string tool, string? callerSessionId, string ownerId, string? projectId) => null;
}

internal sealed class NotesNoopHubNotifier : ClaudeHomeServer.Services.Notes.INotesHubNotifier
{
    public Task BroadcastNotesChangedAsync(string userId, string action, string noteId, string? projectId = null) => Task.CompletedTask;
    public Task BroadcastTaskChangedAsync(string userId, string action, string taskId, string? projectId = null) => Task.CompletedTask;
}
