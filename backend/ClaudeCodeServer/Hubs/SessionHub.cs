using ClaudeCodeServer.Services;
using Microsoft.AspNetCore.SignalR;

namespace ClaudeCodeServer.Hubs;

public class SessionHub : Hub
{
    private readonly SessionManager _sessions;

    public SessionHub(SessionManager sessions) => _sessions = sessions;

    public async Task JoinSession(string sessionId)
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, sessionId);
    }

    public async Task LeaveSession(string sessionId)
    {
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, sessionId);
    }

    public async Task SendMessage(string sessionId, string text, List<string>? attachedPaths = null)
    {
        await _sessions.SendMessageAsync(sessionId, text, attachedPaths ?? []);
    }

    public void RespondPermission(string sessionId, string requestId, string behavior)
    {
        _sessions.RespondPermission(sessionId, requestId, behavior);
    }

    public void Interrupt(string sessionId)
    {
        _sessions.Interrupt(sessionId);
    }
}
