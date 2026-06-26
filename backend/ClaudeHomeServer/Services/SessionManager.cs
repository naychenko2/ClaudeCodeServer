using System.Collections.Concurrent;
using System.Text.Json;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;
using Microsoft.AspNetCore.SignalR;

namespace ClaudeHomeServer.Services;

public class SessionManager
{
    private class SessionEntry
    {
        public required Session Info;
        public ClaudeSession? Process;
        public TurnAccumulator? Accumulator;
        // Кэш последних workflow_progress для replay при подключении нового клиента
        public Dictionary<string, WorkflowProgressMessage> WorkflowProgress = new();
    }

    private readonly ConcurrentDictionary<string, SessionEntry> _sessions = new();
    private readonly ProjectManager _projects;
    private readonly IHubContext<Hubs.SessionHub> _hub;
    private readonly ChatHistoryService _history;
    private readonly string _sessionsFilePath;
    private readonly Lock _saveLock = new();

    private readonly string? _mcpConfigPath;
    private readonly SkillsService _skills;
    private readonly WorkspaceKnowledgeStore _workspaceStore;

    public SessionManager(ProjectManager projects, IHubContext<Hubs.SessionHub> hub,
        ChatHistoryService history, IConfiguration config, SkillsService skills,
        WorkspaceKnowledgeStore workspaceStore)
    {
        _projects = projects;
        _hub = hub;
        _history = history;
        _skills = skills;
        _workspaceStore = workspaceStore;

        var dataDir = Path.GetDirectoryName(
            config["DataPath"] ?? Path.Combine(AppContext.BaseDirectory, "data", "projects.json"))
            ?? Path.Combine(AppContext.BaseDirectory, "data");
        _sessionsFilePath = Path.Combine(dataDir, "sessions.json");

        _mcpConfigPath = config["McpConfigPath"];

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
        lock (_saveLock)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_sessionsFilePath)!);
                var sessions = _sessions.Values.Select(e => e.Info).ToList();
                File.WriteAllText(_sessionsFilePath, JsonSerializer.Serialize(sessions));
            }
            catch { }
        }
    }

    // --- Публичное API ---

    public IReadOnlyCollection<Session> GetByProject(string projectId) =>
        _sessions.Values
            .Where(e => e.Info.ProjectId == projectId)
            .Select(e => e.Info)
            .OrderByDescending(s => s.UpdatedAt)
            .ToList();

    // Число сессий проекта — для карточки проекта (без аллокации списка)
    public int CountByProject(string projectId) =>
        _sessions.Values.Count(e => e.Info.ProjectId == projectId);

    public Session? GetById(string id) =>
        _sessions.TryGetValue(id, out var entry) ? entry.Info : null;

    public async Task<Session> CreateAsync(string projectId, ClaudeMode mode,
        string? resumeSessionId = null, string? name = null, string? model = null, string? agentName = null)
    {
        var project = _projects.GetById(projectId)
            ?? throw new KeyNotFoundException($"Проект не найден: {projectId}");

        var session = new Session
        {
            ProjectId = projectId,
            Mode = mode,
            ClaudeSessionId = resumeSessionId,
            Name = name,
            Model = string.IsNullOrWhiteSpace(model) ? null : model.Trim(),
            AgentName = string.IsNullOrWhiteSpace(agentName) ? null : agentName.Trim(),
        };

        var existingHistory = resumeSessionId != null
            ? await _history.LoadAsync(resumeSessionId)
            : [];
        var accumulator = new TurnAccumulator(existingHistory, resumeSessionId);

        var entry = new SessionEntry { Info = session, Accumulator = accumulator };
        _sessions[session.Id] = entry;

        var wk = _workspaceStore.GetByPath(project.RootPath);
        var claudeSession = new ClaudeSession(session, project.RootPath,
            msg => OnMessageAsync(session.Id, accumulator, msg),
            wk?.DifyDatasetId, _mcpConfigPath,
            ProjectManager.BuildSystemPrompt(project.SystemPrompt, wk?.DifyDatasetId != null, wk?.DocumentTags),
            _skills);
        entry.Process = claudeSession;

        await claudeSession.StartAsync();
        SaveSessions();
        return session;
    }

    public async Task SendMessageAsync(string sessionId, string text, IReadOnlyList<string> attachedPaths, string? mode = null)
    {
        if (!_sessions.TryGetValue(sessionId, out var entry))
            throw new InvalidOperationException("Сессия не найдена");

        // Режим, выбранный в Composer, применяется со следующего хода: процесс claude
        // пересоздаётся в RunTurnAsync и читает --permission-mode из Info.Mode.
        if (mode is not null && Enum.TryParse<ClaudeMode>(mode, true, out var parsedMode)
            && entry.Info.Mode != parsedMode)
        {
            entry.Info.Mode = parsedMode;
            SaveSessions();
        }

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
            var wkRestore = _workspaceStore.GetByPath(project.RootPath);
            var claudeSession = new ClaudeSession(entry.Info, project.RootPath,
                msg => OnMessageAsync(sessionId, accumulator, msg),
                wkRestore?.DifyDatasetId, _mcpConfigPath,
                ProjectManager.BuildSystemPrompt(project.SystemPrompt, wkRestore?.DifyDatasetId != null, wkRestore?.DocumentTags),
                _skills);
            entry.Process = claudeSession;
            await claudeSession.StartAsync();
        }

        entry.Info.Status = SessionStatus.Working;
        entry.Info.UpdatedAt = DateTime.UtcNow;
        await BroadcastStatusChangeAsync(sessionId, entry.Info.ProjectId,
            SessionStatus.Working, entry.Info.LastMessage, entry.Info.MessageCount);

        entry.Accumulator?.OnUserMessage(text, attachedPaths);
        await entry.Process.SendMessageAsync(text, attachedPaths);
    }

    // Редактирование названия и модели. Модель применяется со следующего хода
    // (процесс claude пересоздаётся в RunTurnAsync), Info — общая ссылка с ClaudeSession.
    public Session? Update(string sessionId, string? name, string? model)
    {
        if (!_sessions.TryGetValue(sessionId, out var entry)) return null;
        entry.Info.Name = string.IsNullOrWhiteSpace(name) ? null : name.Trim();
        entry.Info.Model = string.IsNullOrWhiteSpace(model) ? null : model.Trim();
        entry.Info.UpdatedAt = DateTime.UtcNow;
        SaveSessions();
        return entry.Info;
    }

    public void RespondPermission(string sessionId, string requestId, string behavior)
    {
        if (!_sessions.TryGetValue(sessionId, out var entry)) return;
        entry.Process?.RespondPermission(requestId, behavior);
        entry.Info.Status = SessionStatus.Working;
        _ = BroadcastStatusChangeAsync(sessionId, entry.Info.ProjectId,
            SessionStatus.Working, entry.Info.LastMessage, entry.Info.MessageCount);
    }

    public void Interrupt(string sessionId)
    {
        if (_sessions.TryGetValue(sessionId, out var entry))
            entry.Process?.Interrupt();
    }

    public void AnswerQuestion(string sessionId, string toolUseId, string answerText)
    {
        if (!_sessions.TryGetValue(sessionId, out var entry)) return;
        entry.Process?.AnswerQuestion(toolUseId, answerText);
        // Фиксируем ответ в истории, чтобы карточка вопроса пережила перезагрузку
        if (entry.Accumulator is not null)
        {
            object? answers = null;
            try
            {
                using var doc = JsonDocument.Parse(answerText);
                if (doc.RootElement.TryGetProperty("answers", out var a))
                    answers = JsonSerializer.Deserialize<object>(a.GetRawText());
            }
            catch { }
            entry.Accumulator.OnQuestionAnswered(toolUseId, answers);
            _ = entry.Accumulator.SaveSnapshotAsync(_history);
        }
        entry.Info.Status = SessionStatus.Working;
        _ = BroadcastStatusChangeAsync(sessionId, entry.Info.ProjectId,
            SessionStatus.Working, entry.Info.LastMessage, entry.Info.MessageCount);
    }

    public void RespondPlan(string sessionId, string requestId, bool approve, string? feedback)
    {
        if (!_sessions.TryGetValue(sessionId, out var entry)) return;
        entry.Process?.RespondPlan(requestId, approve, feedback);
        // Фиксируем решение по плану в истории, чтобы карточка пережила перезагрузку
        if (entry.Accumulator is not null)
        {
            entry.Accumulator.OnPlanResolved(requestId, approve, feedback);
            _ = entry.Accumulator.SaveSnapshotAsync(_history);
        }
        entry.Info.Status = SessionStatus.Working;
        _ = BroadcastStatusChangeAsync(sessionId, entry.Info.ProjectId,
            SessionStatus.Working, entry.Info.LastMessage, entry.Info.MessageCount);
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

    public IReadOnlyList<WorkflowProgressMessage> GetWorkflowProgress(string sessionId)
    {
        if (!_sessions.TryGetValue(sessionId, out var entry)) return [];
        return entry.WorkflowProgress.Values.ToList();
    }

    public Session? GetSessionInfo(string sessionId)
    {
        _sessions.TryGetValue(sessionId, out var entry);
        return entry?.Info;
    }

    // --- Внутренняя логика ---

    private async Task OnMessageAsync(string sessionId, TurnAccumulator acc, ServerMessage msg)
    {
        _sessions.TryGetValue(sessionId, out var entry);

        // Аккумулятор — отдельный try, чтобы ошибка сохранения истории
        // не заблокировала обновление статуса и широковещание.
        try
        {
            switch (msg)
            {
                case SessionStartedMessage m:
                    acc.SetSaveKey(m.ClaudeSessionId);
                    acc.OnSessionStarted(m.Model, m.Mode);
                    SaveSessions();
                    break;
                case TextDeltaMessage m:    acc.OnTextDelta(m.Text); break;
                case ThinkingDeltaMessage m: acc.OnThinkingDelta(m.Text); break;
                case ToolUseMessage m:      acc.OnToolUse(m.Id, m.Name, m.Input, m.ParentToolUseId); break;
                case ToolResultMessage m:
                    acc.OnToolResult(m.ToolUseId, m.Content, m.IsError);
                    await acc.SaveSnapshotAsync(_history); // промежуточное сохранение после каждого tool call
                    break;
                case WorkflowProgressMessage m:
                    if (entry is not null)
                    {
                        if (m.IsDone) entry.WorkflowProgress.Remove(m.ToolUseId);
                        else entry.WorkflowProgress[m.ToolUseId] = m;
                    }
                    break;
                case FileChangedMessage m:  acc.OnFileChanged(m.Path, m.Added, m.Removed); break;
                case AskQuestionMessage m:
                    acc.OnAskQuestion(m.ToolUseId, m.Input);
                    await acc.SaveSnapshotAsync(_history);
                    break;
                case PlanReviewMessage m:
                    acc.OnPlanReview(m.RequestId, m.Plan);
                    await acc.SaveSnapshotAsync(_history);
                    break;
                case ResultMessage m:       await acc.OnResultAsync(m.Subtype, m.DurationMs, m.NumTurns, m.Usage, m.TotalCostUsd, m.ApiErrorStatus, m.PermissionDenials, _history); break;
                case ErrorMessage m:        await acc.OnErrorAsync(m.Text, _history); break;
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[SessionManager] Ошибка аккумулятора ({sessionId}): {ex.Message}");
        }

        // Обновление статуса — всегда, независимо от аккумулятора.
        // Если OnResultAsync выбросит, статус всё равно обновится.
        if (entry is not null)
        {
            SessionStatus? newStatus = null;

            if (msg is PermissionRequestMessage or AskQuestionMessage or PlanReviewMessage)
                newStatus = SessionStatus.Waiting;
            else if (msg is ResultMessage rm)
                newStatus = rm.Subtype == "error" ? SessionStatus.Error : SessionStatus.Active;
            else if (msg is ErrorMessage)
                newStatus = SessionStatus.Error;
            else if (msg is ExitedMessage &&
                     (entry.Info.Status == SessionStatus.Working || entry.Info.Status == SessionStatus.Waiting))
                newStatus = SessionStatus.Active; // прерван без result — возвращаем в рабочее состояние

            if (newStatus.HasValue)
            {
                entry.Info.Status = newStatus.Value;
                entry.Info.UpdatedAt = DateTime.UtcNow;
                if (msg is ResultMessage) SaveSessions();
                await BroadcastStatusChangeAsync(sessionId, entry.Info.ProjectId,
                    newStatus.Value, entry.Info.LastMessage, entry.Info.MessageCount);
            }
        }

        await BroadcastAsync(sessionId, msg);
    }

    private Task BroadcastAsync(string sessionId, ServerMessage msg) =>
        _hub.Clients.Group(sessionId).SendAsync("message", msg with { SessionId = sessionId });

    // Рассылаем в project-группу (все вкладки проекта) И в session-группу (сам чат),
    // чтобы клиент не пропустил обновление если не успел войти в project-группу.
    private async Task BroadcastStatusChangeAsync(string sessionId, string projectId, SessionStatus status,
        string? lastMessage = null, int messageCount = 0)
    {
        var statusMsg = new StatusChangedMessage(status.ToString().ToLower(), lastMessage, messageCount)
            with { SessionId = sessionId };
        await Task.WhenAll(
            _hub.Clients.Group("project_" + projectId).SendAsync("message", statusMsg),
            _hub.Clients.Group(sessionId).SendAsync("message", statusMsg)
        );
    }
}
