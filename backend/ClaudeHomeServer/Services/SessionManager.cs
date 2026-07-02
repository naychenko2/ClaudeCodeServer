using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
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
    // Сериализует прямую запись стоимости fal.ai в историю неактивных сессий (load-modify-save)
    private readonly SemaphoreSlim _falPersistLock = new(1, 1);

    // Enum (в т.ч. ClaudeMode) сериализуем строками — устойчиво к изменению порядка значений.
    // При чтении конвертер принимает и старый числовой формат.
    private static readonly JsonSerializerOptions _jsonOpts = new()
    {
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string? _mcpConfigPath;
    private readonly SkillsService _skills;
    private readonly RoleManager _roles;
    private readonly RoleMemoryService _roleMemory;
    private readonly WorkspaceKnowledgeStore _workspaceStore;
    private readonly FalCostService _falCost;
    private readonly UsageService _usage;
    private readonly AppSettingsService _appSettings;
    private readonly UserStore _users;

    public SessionManager(ProjectManager projects, IHubContext<Hubs.SessionHub> hub,
        ChatHistoryService history, IConfiguration config, SkillsService skills,
        RoleManager roles, RoleMemoryService roleMemory,
        WorkspaceKnowledgeStore workspaceStore, FalCostService falCost, UsageService usage,
        AppSettingsService appSettings, UserStore users)
    {
        _projects = projects;
        _hub = hub;
        _history = history;
        _skills = skills;
        _roles = roles;
        _roleMemory = roleMemory;
        _workspaceStore = workspaceStore;
        _falCost = falCost;
        _usage = usage;
        _appSettings = appSettings;
        _users = users;
        // Найденную стоимость fal.ai публикуем в SignalR + историю
        _falCost.OnCostResolved = PublishFalCostAsync;

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
            var list = JsonSerializer.Deserialize<List<Session>>(json, _jsonOpts);
            if (list is null) return;
            foreach (var session in list)
            {
                // Процесс умер при рестарте — "живые" статусы переводим в orphaned
                session.Status = session.Status switch
                {
                    SessionStatus.Working or SessionStatus.Starting or SessionStatus.Waiting
                        => SessionStatus.Orphaned,
                    SessionStatus.Active => SessionStatus.Finished,
                    _ => session.Status,
                };
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
                File.WriteAllText(_sessionsFilePath, JsonSerializer.Serialize(sessions, _jsonOpts));
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

    // Чаты вне проекта, принадлежащие пользователю (для вкладки «Чаты»)
    public IReadOnlyCollection<Session> GetProjectlessChats(string ownerId) =>
        _sessions.Values
            .Where(e => e.Info.ProjectId == null && e.Info.OwnerId == ownerId)
            .Select(e => e.Info)
            .OrderByDescending(s => s.UpdatedAt)
            .ToList();

    // Закрепить/открепить чат
    public bool SetPinned(string sessionId, bool pinned)
    {
        if (!_sessions.TryGetValue(sessionId, out var entry)) return false;
        entry.Info.IsPinned = pinned;
        entry.Info.UpdatedAt = DateTime.UtcNow;
        SaveSessions();
        return true;
    }

    // Рабочая папка чата, принадлежащего пользователю (для загрузки вложений).
    // null — если это не project-less чат данного владельца.
    public string? GetChatRoot(string sessionId, string ownerId)
    {
        var s = GetById(sessionId);
        if (s is null || s.ProjectId is not null || s.OwnerId != ownerId) return null;
        return ResolveChatRoot(ownerId);
    }

    // Рабочая папка чата вне проекта: {DefaultProjectsPath}/{username}/Chats (создаётся при отсутствии)
    private string ResolveChatRoot(string ownerId)
    {
        var basePath = _appSettings.Get().DefaultProjectsPath;
        if (string.IsNullOrWhiteSpace(basePath))
            throw new InvalidOperationException("Не задана папка проектов по умолчанию");
        var username = _users.GetById(ownerId)?.Username
            ?? throw new KeyNotFoundException($"Пользователь не найден: {ownerId}");
        var path = Path.Combine(basePath, username, "Chats");
        Directory.CreateDirectory(path);
        return path;
    }

    public Session? GetById(string id) =>
        _sessions.TryGetValue(id, out var entry) ? entry.Info : null;

    public async Task<Session> CreateAsync(string projectId, ClaudeMode mode,
        string? resumeSessionId = null, string? name = null, string? model = null, string? agentName = null,
        string? effort = null, string? roleId = null)
    {
        var project = _projects.GetById(projectId)
            ?? throw new KeyNotFoundException($"Проект не найден: {projectId}");

        // Роль (если выбрана) задаёт дефолтную модель/effort, когда они не указаны явно
        var role = string.IsNullOrWhiteSpace(roleId) ? null : _roles.GetById(roleId);
        var effectiveModel = !string.IsNullOrWhiteSpace(model) ? model : role?.Model;
        var effectiveEffort = !string.IsNullOrWhiteSpace(effort) ? effort : role?.Effort;

        var session = new Session
        {
            ProjectId = projectId,
            Mode = mode,
            ClaudeSessionId = resumeSessionId,
            Name = name,
            Model = string.IsNullOrWhiteSpace(effectiveModel) ? null : effectiveModel.Trim(),
            AgentName = string.IsNullOrWhiteSpace(agentName) ? null : agentName.Trim(),
            Effort = string.IsNullOrWhiteSpace(effectiveEffort) ? null : effectiveEffort.Trim(),
            RoleId = role?.Id,
        };

        await StartNewSessionAsync(session, project.RootPath, project.SystemPrompt,
            () => _projects.GetById(projectId)?.PermissionRules ?? (IReadOnlyList<PermissionRule>)Array.Empty<PermissionRule>());
        return session;
    }

    // Создание чата вне проекта: рабочая папка — {DefaultProjectsPath}/{username}/Chats,
    // системный промпт — только встроенная часть (rawSystemPrompt=null), без проектных правил.
    public async Task<Session> CreateChatAsync(string ownerId, ClaudeMode mode,
        string? resumeSessionId = null, string? name = null, string? model = null, string? effort = null)
    {
        var rootPath = ResolveChatRoot(ownerId);

        var session = new Session
        {
            ProjectId = null,
            OwnerId = ownerId,
            Mode = mode,
            ClaudeSessionId = resumeSessionId,
            Name = name,
            Model = string.IsNullOrWhiteSpace(model) ? null : model.Trim(),
            Effort = string.IsNullOrWhiteSpace(effort) ? null : effort.Trim(),
        };

        await StartNewSessionAsync(session, rootPath, rawSystemPrompt: null, permissionRules: null);
        return session;
    }

    // Общий запуск новой сессии: аккумулятор истории, регистрация в реестре, старт процесса claude.
    private async Task StartNewSessionAsync(Session session, string rootPath, string? rawSystemPrompt,
        Func<IReadOnlyList<PermissionRule>>? permissionRules)
    {
        var existingHistory = session.ClaudeSessionId != null
            ? await _history.LoadAsync(session.ClaudeSessionId)
            : [];
        var accumulator = new TurnAccumulator(existingHistory, session.ClaudeSessionId);

        var entry = new SessionEntry { Info = session, Accumulator = accumulator };
        _sessions[session.Id] = entry;

        var claudeSession = new ClaudeSession(session, rootPath,
            msg => OnMessageAsync(session.Id, accumulator, msg),
            _mcpConfigPath, rawSystemPrompt,
            _skills, _workspaceStore,
            permissionRules,
            _roles, _roleMemory);
        entry.Process = claudeSession;

        await claudeSession.StartAsync();
        SaveSessions();
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
            var existingHistory = entry.Info.ClaudeSessionId != null
                ? await _history.LoadAsync(entry.Info.ClaudeSessionId)
                : [];
            var accumulator = new TurnAccumulator(existingHistory, entry.Info.ClaudeSessionId);
            entry.Accumulator = accumulator;

            // Чат вне проекта — рабочая папка Chats, без проектного промпта и правил;
            // проектная сессия — RootPath/SystemPrompt/PermissionRules из проекта.
            ClaudeSession claudeSession;
            if (entry.Info.ProjectId is null)
            {
                var rootPath = ResolveChatRoot(entry.Info.OwnerId
                    ?? throw new InvalidOperationException("У чата не задан владелец"));
                claudeSession = new ClaudeSession(entry.Info, rootPath,
                    msg => OnMessageAsync(sessionId, accumulator, msg),
                    _mcpConfigPath, rawSystemPrompt: null,
                    _skills, _workspaceStore, permissionRules: null,
                    roles: _roles, roleMemory: _roleMemory);
            }
            else
            {
                var project = _projects.GetById(entry.Info.ProjectId)
                    ?? throw new InvalidOperationException("Проект не найден");
                claudeSession = new ClaudeSession(entry.Info, project.RootPath,
                    msg => OnMessageAsync(sessionId, accumulator, msg),
                    _mcpConfigPath, project.SystemPrompt,
                    _skills, _workspaceStore,
                    () => _projects.GetById(entry.Info.ProjectId!)?.PermissionRules ?? (IReadOnlyList<PermissionRule>)Array.Empty<PermissionRule>(),
                    _roles, _roleMemory);
            }
            entry.Process = claudeSession;
            await claudeSession.StartAsync();
        }

        // Авто-имя сессии по первому сообщению (Claude в --print не отдаёт title/summary).
        // Ставим только если имя ещё не задано — последующие сообщения название не меняют.
        // Работает и для чатов вне проекта, и для проектных сессий.
        if (string.IsNullOrWhiteSpace(entry.Info.Name))
        {
            var title = MakeChatTitle(text);
            if (!string.IsNullOrEmpty(title))
            {
                entry.Info.Name = title;
                SaveSessions();
            }
        }

        entry.Info.Status = SessionStatus.Working;
        entry.Info.UpdatedAt = DateTime.UtcNow;
        await BroadcastStatusChangeAsync(sessionId, entry.Info,
            SessionStatus.Working, entry.Info.LastMessage, entry.Info.MessageCount);

        entry.Accumulator?.OnUserMessage(text, attachedPaths);
        await entry.Process.SendMessageAsync(text, attachedPaths);
    }

    // Заголовок чата из первого сообщения: первая строка, обрезанная до разумной длины.
    private static string MakeChatTitle(string text)
    {
        var t = text.Trim();
        var nl = t.IndexOfAny(['\n', '\r']);
        if (nl >= 0) t = t[..nl].Trim();
        const int max = 48;
        if (t.Length > max) t = string.Concat(t.AsSpan(0, max).TrimEnd(), "…");
        return t;
    }

    // Редактирование названия и модели. Модель применяется со следующего хода
    // (процесс claude пересоздаётся в RunTurnAsync), Info — общая ссылка с ClaudeSession.
    public Session? Update(string sessionId, string? name, string? model, string? effort)
    {
        if (!_sessions.TryGetValue(sessionId, out var entry)) return null;
        entry.Info.Name = string.IsNullOrWhiteSpace(name) ? null : name.Trim();
        entry.Info.Model = string.IsNullOrWhiteSpace(model) ? null : model.Trim();
        entry.Info.Effort = string.IsNullOrWhiteSpace(effort) ? null : effort.Trim();
        entry.Info.UpdatedAt = DateTime.UtcNow;
        SaveSessions();
        return entry.Info;
    }

    public void RespondPermission(string sessionId, string requestId, string behavior)
    {
        if (!_sessions.TryGetValue(sessionId, out var entry)) return;
        entry.Process?.RespondPermission(requestId, behavior);
        entry.Info.Status = SessionStatus.Working;
        _ = BroadcastStatusChangeAsync(sessionId, entry.Info,
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
        _ = BroadcastStatusChangeAsync(sessionId, entry.Info,
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
        _ = BroadcastStatusChangeAsync(sessionId, entry.Info,
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

        IReadOnlyList<StoredMessage> list;
        if (entry.Accumulator != null)
            list = entry.Accumulator.GetAll();
        else if (entry.Info.ClaudeSessionId != null)
            list = await _history.LoadAsync(entry.Info.ClaudeSessionId);
        else
            list = [];

        // Догоняем стоимость старых fal-генераций, у которых её ещё нет (фоном, дедуп внутри)
        BackfillFalCosts(sessionId, list);
        return list;
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
                    TryTrackFalCost(sessionId, m.Content); // fire-and-forget: стоимость придёт позже
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
                case RateLimitMessage m:    _usage.Record(m.LimitType, m.Utilization, m.Status, m.IsUsingOverage, m.ResetsAt, m.OverageStatus, m.OverageResetsAt); break;
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
                await BroadcastStatusChangeAsync(sessionId, entry.Info,
                    newStatus.Value, entry.Info.LastMessage, entry.Info.MessageCount);
            }
        }

        await BroadcastAsync(sessionId, msg);
    }

    // Извлекает request_id из результата вызова, если это генерация fal.ai. Признак fal —
    // наличие request_id И fal-домена где-либо в ответе. Покрывает обе формы результата:
    //  • run_model/submit_job: fal.run в *_url (status_url/response_url/cancel_url);
    //  • get_job_result (видео/аудио): *_url нет, но fal.media в URL медиа.
    private static string? TryExtractFalRequestId(string content)
    {
        if (string.IsNullOrEmpty(content)) return null;
        if (!content.Contains("fal.run") && !content.Contains("fal.ai") && !content.Contains("fal.media")) return null;
        try
        {
            using var doc = JsonDocument.Parse(content);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            if (root.TryGetProperty("request_id", out var rid) && rid.ValueKind == JsonValueKind.String)
                return rid.GetString();
            return null;
        }
        catch { return null; } // не JSON / не наш формат — это не fal-результат
    }

    // Ставит результат генерации fal.ai на отслеживание стоимости (опрос billing-events — в фоне).
    private void TryTrackFalCost(string sessionId, string content)
    {
        if (!_falCost.Enabled) return;
        var requestId = TryExtractFalRequestId(content);
        if (!string.IsNullOrEmpty(requestId))
            _falCost.Track(sessionId, requestId);
    }

    // Догоняет стоимость для СТАРЫХ генераций fal.ai в истории, у которых ещё нет fal_cost
    // (сгенерированы до появления фичи/ключа). Вызывается при загрузке истории сессии.
    private void BackfillFalCosts(string sessionId, IReadOnlyList<StoredMessage> history)
    {
        if (!_falCost.Enabled) return;
        var have = new HashSet<string>();
        foreach (var m in history)
            if (m is StoredFalCostMessage f) have.Add(f.RequestId);
        foreach (var m in history)
        {
            if (m is not StoredToolUseMessage t || t.IsError || string.IsNullOrEmpty(t.Result)) continue;
            var rid = TryExtractFalRequestId(t.Result);
            if (rid != null && !have.Contains(rid))
                _falCost.Track(sessionId, rid);
        }
    }

    // Публикация найденной стоимости fal.ai: запись в историю (дедуп) + broadcast клиентам.
    // Активная сессия → через аккумулятор; неактивная (нет аккумулятора) → прямо в файл истории,
    // иначе стоимость не переживёт переоткрытие и «считается…» зависнет.
    public async Task PublishFalCostAsync(string sessionId, FalCostMessage msg)
    {
        if (!_sessions.TryGetValue(sessionId, out var entry)) return;

        if (entry.Accumulator is not null)
        {
            if (!entry.Accumulator.OnFalCost(msg.RequestId, msg.EndpointId, msg.CostUsd, msg.OutputUnits, msg.UnitPrice))
                return; // дубликат — уже опубликован
            try { await entry.Accumulator.SaveSnapshotAsync(_history); }
            catch (Exception ex) { Console.Error.WriteLine($"[FalCost] Сохранение истории ({sessionId}) не удалось: {ex.Message}"); }
        }
        else if (entry.Info.ClaudeSessionId is string key)
        {
            // Сессия не активна — пишем стоимость напрямую в историю на диске (под локом против гонок)
            await _falPersistLock.WaitAsync();
            try
            {
                var stored = await _history.LoadAsync(key);
                if (stored.Any(m => m is StoredFalCostMessage f && f.RequestId == msg.RequestId))
                    return; // дубликат — уже в истории
                stored.Add(new StoredFalCostMessage(msg.RequestId, msg.EndpointId, msg.CostUsd, msg.OutputUnits, msg.UnitPrice));
                await _history.SaveAsync(key, stored);
            }
            catch (Exception ex) { Console.Error.WriteLine($"[FalCost] Прямая запись истории ({sessionId}) не удалась: {ex.Message}"); }
            finally { _falPersistLock.Release(); }
        }

        await BroadcastAsync(sessionId, msg);
    }

    private Task BroadcastAsync(string sessionId, ServerMessage msg) =>
        _hub.Clients.Group(sessionId).SendAsync("message", msg with { SessionId = sessionId });

    // Рассылаем в session-группу (сам чат) всегда, плюс в project-группу (все вкладки проекта)
    // для проектной сессии ЛИБО в user-группу (список чатов) для чата вне проекта —
    // чтобы клиент не пропустил обновление, если не успел войти в session-группу.
    private async Task BroadcastStatusChangeAsync(string sessionId, Session info, SessionStatus status,
        string? lastMessage = null, int messageCount = 0)
    {
        var statusMsg = new StatusChangedMessage(status.ToString().ToLower(), lastMessage, messageCount)
            with { SessionId = sessionId };
        var tasks = new List<Task> { _hub.Clients.Group(sessionId).SendAsync("message", statusMsg) };
        if (info.ProjectId is string pid)
            tasks.Add(_hub.Clients.Group("project_" + pid).SendAsync("message", statusMsg));
        else if (info.OwnerId is string oid)
            tasks.Add(_hub.Clients.Group("user_" + oid).SendAsync("message", statusMsg));
        await Task.WhenAll(tasks);
    }
}
