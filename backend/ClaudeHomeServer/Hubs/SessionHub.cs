using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http.Connections.Features;
using Microsoft.AspNetCore.SignalR;

namespace ClaudeHomeServer.Hubs;

[Authorize]
public class SessionHub : Hub
{
    private readonly SessionManager _sessions;
    private readonly FileWatcherService _watcher;
    private readonly ConnectionDiagnostics _diag;

    public SessionHub(SessionManager sessions, FileWatcherService watcher, ConnectionDiagnostics diag)
    {
        _sessions = sessions;
        _watcher = watcher;
        _diag = diag;
    }

    public override Task OnConnectedAsync()
    {
        var transport = Context.Features.Get<IHttpTransportFeature>()?.TransportType.ToString() ?? "unknown";
        _diag.RecordConnect(Context.ConnectionId, transport, Context.UserIdentifier ?? Context.User?.Identity?.Name);
        return base.OnConnectedAsync();
    }

    public async Task JoinSession(string sessionId)
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, sessionId);

        // Новый клиент сразу получает текущий статус (чтобы не пропустить working при workflow)
        var info = _sessions.GetSessionInfo(sessionId);
        if (info is { Status: SessionStatus.Working or SessionStatus.Waiting or SessionStatus.Starting })
        {
            var statusMsg = new StatusChangedMessage(info.Status.ToString().ToLower(), info.LastMessage, info.MessageCount)
                with { SessionId = sessionId };
            await Clients.Caller.SendAsync("message", statusMsg);
        }

        // Кэшированные workflow_progress
        foreach (var msg in _sessions.GetWorkflowProgress(sessionId))
            await Clients.Caller.SendAsync("message", msg with { SessionId = sessionId });
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

    // Группа для realtime-обновления списка чатов вне проекта (без файлового watcher)
    public Task JoinUser(string userId) =>
        Groups.AddToGroupAsync(Context.ConnectionId, "user_" + userId);

    public Task LeaveUser(string userId) =>
        Groups.RemoveFromGroupAsync(Context.ConnectionId, "user_" + userId);

    public override Task OnDisconnectedAsync(Exception? exception)
    {
        _watcher.RemoveConnection(Context.ConnectionId);
        _diag.RecordDisconnect(Context.ConnectionId, exception);
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

    public void RespondPlan(string sessionId, string requestId, bool approve, string? feedback = null)
    {
        _sessions.RespondPlan(sessionId, requestId, approve, feedback);
    }
}
