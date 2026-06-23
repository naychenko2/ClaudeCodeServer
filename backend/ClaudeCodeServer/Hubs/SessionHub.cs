using ClaudeCodeServer.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace ClaudeCodeServer.Hubs;

[Authorize]
public class SessionHub : Hub
{
    private readonly SessionManager _sessions;
    private readonly FileWatcherService _watcher;

    public SessionHub(SessionManager sessions, FileWatcherService watcher)
    {
        _sessions = sessions;
        _watcher = watcher;
    }

    public async Task JoinSession(string sessionId)
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, sessionId);
    }

    public async Task LeaveSession(string sessionId)
    {
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, sessionId);
    }

    public async Task JoinProject(string projectId)
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, "project_" + projectId);
        _watcher.Watch(projectId, Context.ConnectionId);
    }

    public async Task LeaveProject(string projectId)
    {
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, "project_" + projectId);
        _watcher.Unwatch(projectId, Context.ConnectionId);
    }

    public override Task OnDisconnectedAsync(Exception? exception)
    {
        _watcher.RemoveConnection(Context.ConnectionId);
        return base.OnDisconnectedAsync(exception);
    }

    public async Task SendMessage(string sessionId, string text, List<string>? attachedPaths = null, string? mode = null)
    {
        await _sessions.SendMessageAsync(sessionId, text, attachedPaths ?? [], mode);
    }

    public void RespondPermission(string sessionId, string requestId, string behavior)
    {
        _sessions.RespondPermission(sessionId, requestId, behavior);
    }

    public void Interrupt(string sessionId)
    {
        _sessions.Interrupt(sessionId);
    }

    public void AnswerQuestion(string sessionId, string toolUseId, string answerText)
    {
        _sessions.AnswerQuestion(sessionId, toolUseId, answerText);
    }
}
