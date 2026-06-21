using System.Collections.Concurrent;
using System.Text.Json;
using ClaudeCodeServer.Models;
using ClaudeCodeServer.Protocol;
using Microsoft.AspNetCore.SignalR;

namespace ClaudeCodeServer.Services;

public class SessionManager
{
    private class SessionEntry
    {
        public required Session Info;
        public ClaudeSession? Process;
        public TurnAccumulator? Accumulator;
    }

    private readonly ConcurrentDictionary<string, SessionEntry> _sessions = new();
    private readonly ProjectManager _projects;
    private readonly IHubContext<Hubs.SessionHub> _hub;
    private readonly ChatHistoryService _history;
    private readonly string _sessionsFilePath;

    public SessionManager(ProjectManager projects, IHubContext<Hubs.SessionHub> hub,
        ChatHistoryService history, IConfiguration config)
    {
        _projects = projects;
        _hub = hub;
        _history = history;

        var dataDir = Path.GetDirectoryName(
            config["DataPath"] ?? Path.Combine(AppContext.BaseDirectory, "data", "projects.json"))
            ?? Path.Combine(AppContext.BaseDirectory, "data");
        _sessionsFilePath = Path.Combine(dataDir, "sessions.json");

        LoadSessions();
    }

    // --- Персистентность сессий ---

    private void LoadSessions()
    {
        if (!File.Exists(_sessionsFilePath)) return;
        try
        {
            var json = File.ReadAllText(_sessionsFilePath);
            var list = JsonSerializer.Deserialize<List<Session>>(json);
            if (list is null) return;
            foreach (var session in list)
            {
                session.Status = SessionStatus.Finished;
                _sessions[session.Id] = new SessionEntry { Info = session };
            }
        }
        catch { }
    }

    private void SaveSessions()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_sessionsFilePath)!);
            var sessions = _sessions.Values.Select(e => e.Info).ToList();
            File.WriteAllText(_sessionsFilePath, JsonSerializer.Serialize(sessions));
        }
        catch { }
    }

    // --- Публичное API ---

    public IReadOnlyCollection<Session> GetByProject(string projectId) =>
        _sessions.Values
            .Where(e => e.Info.ProjectId == projectId)
            .Select(e => e.Info)
            .OrderBy(s => s.CreatedAt)
            .ToList();

    public Session? GetById(string id) =>
        _sessions.TryGetValue(id, out var entry) ? entry.Info : null;

    public async Task<Session> CreateAsync(string projectId, ClaudeMode mode,
        string? resumeSessionId = null, string? name = null)
    {
        var project = _projects.GetById(projectId)
            ?? throw new KeyNotFoundException($"Проект не найден: {projectId}");

        var session = new Session
        {
            ProjectId = projectId,
            Mode = mode,
            ClaudeSessionId = resumeSessionId,
            Name = name,
        };

        var existingHistory = resumeSessionId != null
            ? await _history.LoadAsync(resumeSessionId)
            : [];
        var accumulator = new TurnAccumulator(existingHistory, resumeSessionId);

        var entry = new SessionEntry { Info = session, Accumulator = accumulator };
        _sessions[session.Id] = entry;

        var claudeSession = new ClaudeSession(session, project.RootPath,
            msg => OnMessageAsync(session.Id, accumulator, msg));
        entry.Process = claudeSession;

        await claudeSession.StartAsync();
        SaveSessions();
        return session;
    }

    public async Task SendMessageAsync(string sessionId, string text, IReadOnlyList<string> attachedPaths)
    {
        if (!_sessions.TryGetValue(sessionId, out var entry))
            throw new InvalidOperationException("Сессия не найдена");

        // После перезапуска сервера Process может быть null — восстанавливаем сессию
        if (entry.Process is null)
        {
            var project = _projects.GetById(entry.Info.ProjectId)
                ?? throw new InvalidOperationException("Проект не найден");
            var existingHistory = entry.Info.ClaudeSessionId != null
                ? await _history.LoadAsync(entry.Info.ClaudeSessionId)
                : [];
            var accumulator = new TurnAccumulator(existingHistory, entry.Info.ClaudeSessionId);
            entry.Accumulator = accumulator;
            var claudeSession = new ClaudeSession(entry.Info, project.RootPath,
                msg => OnMessageAsync(sessionId, accumulator, msg));
            entry.Process = claudeSession;
            await claudeSession.StartAsync();
        }

        entry.Accumulator?.OnUserMessage(text, attachedPaths);
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
        SaveSessions();
    }

    public async Task<IReadOnlyList<StoredMessage>> GetHistoryAsync(string sessionId)
    {
        if (!_sessions.TryGetValue(sessionId, out var entry))
            return [];

        if (entry.Accumulator != null)
            return entry.Accumulator.GetAll();

        if (entry.Info.ClaudeSessionId != null)
            return await _history.LoadAsync(entry.Info.ClaudeSessionId);

        return [];
    }

    // --- Внутренняя логика ---

    private async Task OnMessageAsync(string sessionId, TurnAccumulator acc, ServerMessage msg)
    {
        try
        {
            switch (msg)
            {
                case SessionStartedMessage m:
                    acc.SetSaveKey(m.ClaudeSessionId);
                    acc.OnSessionStarted(m.Model, m.Mode);
                    SaveSessions(); // ClaudeSessionId теперь известен — сохраняем для выживания после рестарта
                    break;
                case TextDeltaMessage m:
                    acc.OnTextDelta(m.Text);
                    break;
                case ThinkingDeltaMessage m:
                    acc.OnThinkingDelta(m.Text);
                    break;
                case ToolUseMessage m:
                    acc.OnToolUse(m.Id, m.Name, m.Input);
                    break;
                case ToolResultMessage m:
                    acc.OnToolResult(m.ToolUseId, m.Content, m.IsError);
                    break;
                case FileChangedMessage m:
                    acc.OnFileChanged(m.Path, m.Added, m.Removed);
                    break;
                case ResultMessage m:
                    await acc.OnResultAsync(m.Subtype, m.DurationMs, m.NumTurns, _history);
                    break;
                case ErrorMessage m:
                    await acc.OnErrorAsync(m.Text, _history);
                    break;
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[SessionManager] Ошибка аккумулятора ({sessionId}): {ex.Message}");
        }

        await BroadcastAsync(sessionId, msg);
    }

    private Task BroadcastAsync(string sessionId, ServerMessage msg) =>
        _hub.Clients.Group(sessionId).SendAsync("message", msg with { SessionId = sessionId });
}
