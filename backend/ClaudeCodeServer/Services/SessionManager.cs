using System.Collections.Concurrent;
using ClaudeCodeServer.Models;
using ClaudeCodeServer.Protocol;
using Microsoft.AspNetCore.SignalR;

namespace ClaudeCodeServer.Services;

public class SessionManager
{
    private readonly ConcurrentDictionary<string, (Session Info, ClaudeSession? Process)> _sessions = new();
    private readonly ProjectManager _projects;
    private readonly IHubContext<Hubs.SessionHub> _hub;

    public SessionManager(ProjectManager projects, IHubContext<Hubs.SessionHub> hub)
    {
        _projects = projects;
        _hub = hub;
    }

    public IReadOnlyCollection<Session> GetByProject(string projectId) =>
        _sessions.Values
            .Where(s => s.Info.ProjectId == projectId)
            .Select(s => s.Info)
            .ToList();

    public Session? GetById(string id) =>
        _sessions.TryGetValue(id, out var entry) ? entry.Info : null;

    public async Task<Session> CreateAsync(string projectId, ClaudeMode mode, string? resumeSessionId = null)
    {
        var project = _projects.GetById(projectId)
            ?? throw new KeyNotFoundException($"Проект не найден: {projectId}");

        var session = new Session
        {
            ProjectId = projectId,
            Mode = mode,
            ClaudeSessionId = resumeSessionId
        };
        _sessions[session.Id] = (session, null);

        var claudeSession = new ClaudeSession(session, project.RootPath,
            msg => BroadcastAsync(session.Id, msg));

        _sessions[session.Id] = (session, claudeSession);
        await claudeSession.StartAsync();
        return session;
    }

    public async Task SendMessageAsync(string sessionId, string text, IReadOnlyList<string> attachedPaths)
    {
        if (!_sessions.TryGetValue(sessionId, out var entry) || entry.Process is null)
            throw new InvalidOperationException("Сессия не найдена или не запущена");
        await entry.Process.SendMessageAsync(text, attachedPaths);
    }

    public void RespondPermission(string sessionId, string requestId, string behavior)
    {
        if (_sessions.TryGetValue(sessionId, out var entry))
            entry.Process?.RespondPermission(requestId, behavior);
    }

    public void Interrupt(string sessionId)
    {
        if (_sessions.TryGetValue(sessionId, out var entry))
            entry.Process?.Interrupt();
    }

    public async Task DeleteAsync(string sessionId)
    {
        if (_sessions.TryRemove(sessionId, out var entry) && entry.Process is not null)
            await entry.Process.DisposeAsync();
    }

    private Task BroadcastAsync(string sessionId, ServerMessage msg) =>
        _hub.Clients.Group(sessionId).SendAsync("message", msg with { SessionId = sessionId });
}
