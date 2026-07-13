using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services.Llm;
using ClaudeHomeServer.Services.Prompts;
using Microsoft.AspNetCore.SignalR;

namespace ClaudeHomeServer.Services;

public class SessionManager
{
    private class SessionEntry
    {
        public required Session Info;
        public ILlmSessionAdapter? Process;
        public TurnAccumulator? Accumulator;
        // Кэш последних workflow_progress для replay при подключении нового клиента
        public Dictionary<string, WorkflowProgressMessage> WorkflowProgress = new();
        // Текст ответа текущего хода — для поиска маркера завершения цикла «до готово»
        public System.Text.StringBuilder LoopTurnText = new();
        // Ход завершился ошибкой (result error / error) — цикл не продолжаем
        public bool LoopTurnFailed;
        // Одиночный per-turn ожидатель хода (SendMessageAndWaitAsync): резолвится в
        // OnMessageAsync на result/error/exited и безусловно обнуляется (Interlocked)
        public TaskCompletionSource<TurnResult>? TurnWaiter;
        // Число сообщений истории до хода — чтобы взять реплику ответа именно этого хода
        public int TurnWaiterBaseline;
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

    private readonly ILlmSessionAdapterFactory _adapters;
    private readonly LlmProviderRegistry _llmProviders;
    private readonly FalCostService _falCost;
    private readonly UsageService _usage;
    private readonly AppSettingsService _appSettings;
    private readonly UserStore _users;
    private readonly JwtService _jwt;
    private readonly Microsoft.AspNetCore.Hosting.Server.IServer _server;
    private readonly IConfiguration _config;
    // Сервисные токены MCP tasks-server — по одному на владельца, с перевыпуском до истечения
    private readonly ConcurrentDictionary<string, (string Token, DateTime IssuedAt)> _tasksTokens = new();
    // Аналогично для MCP notes-server (тот же сервисный токен владельца по смыслу, свой кэш)
    private readonly ConcurrentDictionary<string, (string Token, DateTime IssuedAt)> _notesTokens = new();

    // Наблюдатель сообщений сессий (Claude-исполнитель задач слушает result/permission).
    // Вызывается после обновления статуса и broadcast; его ошибки не роняют пайплайн
    public event Func<Session, ServerMessage, Task>? OnSessionMessage;

    // Текст пользовательского сообщения (ввод в чат) — для push-источников автоматизаций
    // (детекция @упоминаний персон). Вызывается из SendMessageAsync после записи в Accumulator;
    // fire-and-forget, ошибки наблюдателя не роняют ход. (session, text, senderPersonaId)
    public event Func<Session, string, string?, Task>? OnUserMessage;

    // Удаление сессии (чат/проектная сессия) — для авто-движков: сбросить ссылки на чат правила.
    public event Action<Session>? OnSessionDeleted;

    // Auto-recall заметок (фича notes-auto-recall): семантический индекс + гейт по флагу
    private readonly NotesKnowledgeService _notesKb;
    private readonly FeatureFlagService _flags;
    private readonly PersonaManager _personas;
    private readonly PersonaMemoryService _personaMemory;
    private readonly PersonaBindingsService _bindings;
    private readonly PersonaPromptBuilder _promptBuilder;
    private readonly ILogger<SessionManager> _log;

    public SessionManager(ProjectManager projects, IHubContext<Hubs.SessionHub> hub,
        ChatHistoryService history, IConfiguration config, ILlmSessionAdapterFactory adapters,
        FalCostService falCost, UsageService usage,
        AppSettingsService appSettings, UserStore users, JwtService jwt,
        Microsoft.AspNetCore.Hosting.Server.IServer server,
        LlmProviderRegistry llmProviders,
        NotesKnowledgeService notesKb, FeatureFlagService flags, PersonaManager personas,
        PersonaMemoryService personaMemory, PersonaBindingsService bindings,
        PersonaPromptBuilder promptBuilder,
        ILogger<SessionManager> log)
    {
        _projects = projects;
        _hub = hub;
        _history = history;
        _adapters = adapters;
        _llmProviders = llmProviders;
        _falCost = falCost;
        _usage = usage;
        _appSettings = appSettings;
        _users = users;
        _jwt = jwt;
        _server = server;
        _config = config;
        _notesKb = notesKb;
        _flags = flags;
        _personas = personas;
        _personaMemory = personaMemory;
        _bindings = bindings;
        _promptBuilder = promptBuilder;
        _log = log;
        // Найденную стоимость fal.ai публикуем в SignalR + историю
        _falCost.OnCostResolved = PublishFalCostAsync;
        // Изменение персоны (профиль/возможности/привязки) — сбрасываем адаптеры её живых
        // сессий, чтобы Tool-рубильники и MCP-серверы перемонтировались со следующего хода
        _personas.OnPersonaChanged += p => InvalidatePersonaSessions(p.Id);

        var dataDir = Path.GetDirectoryName(
            config["DataPath"] ?? Path.Combine(AppContext.BaseDirectory, "data", "projects.json"))
            ?? Path.Combine(AppContext.BaseDirectory, "data");
        _sessionsFilePath = Path.Combine(dataDir, "sessions.json");

        LoadSessions();
    }

    // --- MCP tasks-server ---

    // Базовый URL API для MCP-сервера: конфиг → первый адрес Kestrel → дефолт.
    // 0.0.0.0/[::] заменяем на localhost — MCP-сервер ходит с той же машины.
    private string ResolveTasksApiUrl()
    {
        var fromConfig = _config["McpTasksApiUrl"];
        if (!string.IsNullOrWhiteSpace(fromConfig)) return fromConfig.TrimEnd('/');

        var addr = _server.Features
            .Get<Microsoft.AspNetCore.Hosting.Server.Features.IServerAddressesFeature>()?
            .Addresses.FirstOrDefault();
        if (string.IsNullOrEmpty(addr)) return "http://localhost:5000";
        return addr.Replace("0.0.0.0", "localhost").Replace("[::]", "localhost").TrimEnd('/');
    }

    // tasks-MCP доступен, когда разрешён персоной (Persona.Tools/привязки), ЛИБО сессия
    // является исполнителем задачи — тогда tasks-MCP форсируется: исполнитель обязан
    // управлять задачей через mcp__tasks__* (иначе ограниченная персона не сможет её
    // ни прочитать, ни завершить и свалится в нерабочий встроенный Task-тул).
    private bool TasksMcpEnabled(string? ownerId, Session session, Persona? persona) =>
        session.TaskExecution || _bindings.EffectiveToolEnabled(ownerId, persona, "tasks");

    // Контекст MCP-сервера задач для сессии; null — только для чата без владельца
    private TasksMcpContext? BuildTasksContext(string? ownerId, string? projectId)
    {
        if (ownerId is null) return null;
        // Перевыпуск за сутки до истечения — сервер может жить дольше срока токена
        var entry = _tasksTokens.AddOrUpdate(ownerId,
            id => (_jwt.IssueServiceToken(id), DateTime.UtcNow),
            (id, old) => DateTime.UtcNow - old.IssuedAt > JwtService.ServiceTokenLifetime - TimeSpan.FromDays(1)
                ? (_jwt.IssueServiceToken(id), DateTime.UtcNow)
                : old);
        return new TasksMcpContext(ResolveTasksApiUrl(), entry.Token, projectId);
    }

    // Контекст MCP-сервера заметок; null — только для чата без владельца
    private NotesMcpContext? BuildNotesContext(string? ownerId, string? projectId)
    {
        if (ownerId is null) return null;
        var entry = _notesTokens.AddOrUpdate(ownerId,
            id => (_jwt.IssueServiceToken(id), DateTime.UtcNow),
            (id, old) => DateTime.UtcNow - old.IssuedAt > JwtService.ServiceTokenLifetime - TimeSpan.FromDays(1)
                ? (_jwt.IssueServiceToken(id), DateTime.UtcNow)
                : old);
        return new NotesMcpContext(ResolveTasksApiUrl(), entry.Token, projectId);
    }

    // Контекст MCP-сервера памяти персоны (тот же сервисный токен владельца, что и tasks/notes)
    private MemoryMcpContext BuildMemoryContext(string ownerId, string personaId)
    {
        var entry = _notesTokens.AddOrUpdate(ownerId,
            id => (_jwt.IssueServiceToken(id), DateTime.UtcNow),
            (id, old) => DateTime.UtcNow - old.IssuedAt > JwtService.ServiceTokenLifetime - TimeSpan.FromDays(1)
                ? (_jwt.IssueServiceToken(id), DateTime.UtcNow)
                : old);
        return new MemoryMcpContext(ResolveTasksApiUrl(), entry.Token, personaId);
    }

    // Auto-recall долгой памяти персоны: по тексту хода возвращает markdown-блок релевантных
    // записей (взвешенная сумма PersonaMemoryScorer) + рабочий фокус первым блоком.
    // Failsafe-таймаут; ошибки → null (ход без recall).
    private Func<string, Task<string?>> BuildPersonaRecallProvider(string ownerId, string personaId)
    {
        var topK = int.TryParse(_config["Persona:RecallTopK"], out var k) ? k : 5;
        // Шкала скоринга — взвешенная сумма (PersonaMemoryScorer), порог ~0.30;
        // старый дефолт 0.02 относился к шкале произведения и больше не валиден
        var minScore = double.TryParse(_config["Persona:RecallMinScore"],
            System.Globalization.CultureInfo.InvariantCulture, out var s) ? s : 0.30;
        var timeoutMs = int.TryParse(_config["Persona:RecallTimeoutMs"], out var t) ? t : 2500;

        return async text =>
        {
            var query = text.Trim();
            if (query.Length == 0) return null;
            if (query.Length > 500) query = query[..500];
            try
            {
                var recallTask = _personaMemory.BuildRecallAsync(ownerId, personaId, query, topK, minScore);
                var completed = await Task.WhenAny(recallTask, Task.Delay(timeoutMs));
                if (completed != recallTask) return null;   // таймаут — ход без recall
                return await recallTask;
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Persona memory recall для {Persona}", personaId);
                return null;
            }
        };
    }

    // Провайдер auto-recall для сессии: по тексту хода ищет релевантные заметки и
    // формирует markdown-блок для системного промпта. Флаги проверяются ВНУТРИ (на
    // каждый ход — переключение действует без пересоздания процесса). null — если
    // подмешивать нечего/некому. Ошибки и таймаут Dify → null (ход идёт без recall).
    private Func<string, Task<string?>>? BuildRecallProvider(string? ownerId)
    {
        if (ownerId is null) return null;
        var topK = int.TryParse(_config["Notes:AutoRecallTopK"], out var k) ? k : 4;
        var minScore = double.TryParse(_config["Notes:AutoRecallMinScore"],
            System.Globalization.CultureInfo.InvariantCulture, out var s) ? s : 0.35;
        var timeoutMs = int.TryParse(_config["Notes:AutoRecallTimeoutMs"], out var t) ? t : 2500;

        return async text =>
        {
            if (!_notesKb.Available || !_notesKb.HasIndex(ownerId)) return null;

            var query = text.Trim();
            if (query.Length == 0) return null;
            if (query.Length > 500) query = query[..500];

            try
            {
                var searchTask = _notesKb.SearchAsync(ownerId, query, Math.Max(topK, 8));
                var completed = await Task.WhenAny(searchTask, Task.Delay(timeoutMs));
                if (completed != searchTask) return null;   // таймаут — ход без recall
                return NotesKnowledgeService.BuildRecallBlock(await searchTask, minScore, topK);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Auto-recall заметок для {Owner}", ownerId);
                return null;
            }
        };
    }

    // --- Персистентность сессий ---

    private void LoadSessions()
    {
        var list = JsonFileStore.Load<List<Session>>(_sessionsFilePath, _jsonOpts);
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

    private void SaveSessions()
    {
        lock (_saveLock)
        {
            try
            {
                var sessions = _sessions.Values.Select(e => e.Info).ToList();
                JsonFileStore.Save(_sessionsFilePath, sessions, _jsonOpts);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[SessionManager] Не удалось сохранить {_sessionsFilePath}: {ex.Message}");
            }
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

    // Включить/выключить временность чата: minutes > 0 — авто-удаление через N минут
    // после последней активности, null — обычный чат. Включение перезапускает отсчёт (UpdatedAt)
    public Session? SetExpiry(string sessionId, int? minutes)
    {
        if (!_sessions.TryGetValue(sessionId, out var entry)) return null;
        entry.Info.ExpiresAfterMinutes = minutes;
        entry.Info.UpdatedAt = DateTime.UtcNow;
        SaveSessions();
        return entry.Info;
    }

    // Все сессии (для планировщика авто-удаления временных чатов)
    public IReadOnlyCollection<Session> GetAll() =>
        _sessions.Values.Select(e => e.Info).ToList();

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

    // Запомнить заметку-итог сессии (SessionSummaryService) — для обновления при повторной генерации
    public void SetSummaryNoteId(string sessionId, string noteId)
    {
        if (!_sessions.TryGetValue(sessionId, out var entry)) return;
        entry.Info.SummaryNoteId = noteId;
        SaveSessions();
    }

    public async Task<Session> CreateAsync(string projectId, ClaudeMode mode,
        string? resumeSessionId = null, string? name = null, string? model = null, string? agentName = null,
        string? effort = null, string? personaId = null, bool taskExecution = false)
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
            Effort = string.IsNullOrWhiteSpace(effort) ? null : effort.Trim(),
            // Персона-слой подхватится общим механизмом (BuildPersonaLayer).
            // Маршрутизация остаётся по вызывающему коду (задача), не по зоне персоны.
            PersonaId = string.IsNullOrWhiteSpace(personaId) ? null : personaId,
            TaskExecution = taskExecution,
        };

        await StartNewSessionAsync(session, project.RootPath, project.SystemPrompt,
            () => _projects.GetById(projectId)?.PermissionRules ?? (IReadOnlyList<PermissionRule>)Array.Empty<PermissionRule>());
        return session;
    }

    // Создание чата вне проекта: рабочая папка — {DefaultProjectsPath}/{username}/Chats,
    // системный промпт — только встроенная часть (rawSystemPrompt=null), без проектных правил.
    public async Task<Session> CreateChatAsync(string ownerId, ClaudeMode mode,
        string? resumeSessionId = null, string? name = null, string? model = null, string? effort = null,
        string? personaId = null, bool taskExecution = false)
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
            // Персона-слой подхватится общим механизмом (BuildPersonaLayer)
            PersonaId = string.IsNullOrWhiteSpace(personaId) ? null : personaId,
            TaskExecution = taskExecution,
        };

        await StartNewSessionAsync(session, rootPath, rawSystemPrompt: null, permissionRules: null);
        return session;
    }

    // Создание чата от лица персоны. Маршрутизация по зоне:
    // проектная персона → сессия в её проекте (scope = проект); глобальная (или проект
    // недоступен) → чат вне проекта (scope = все данные владельца). Модель по умолчанию — из персоны.
    // contextProjectId — проект, ИЗ которого зовут глобальную персону («Поговорить» в проекте):
    // чат создаётся в нём, а не вне проекта (как давно позволяет смена собеседника SetPersona).
    public async Task<Session> CreatePersonaChatAsync(string ownerId, string personaId,
        ClaudeMode mode, string? resumeSessionId = null, string? name = null,
        string? contextProjectId = null, string? automationRuleId = null)
    {
        var persona = _personas.Get(personaId, ownerId)
            ?? throw new KeyNotFoundException($"Персона не найдена: {personaId}");

        // Проект сессии: у проектной персоны — её собственный; у глобальной — контекстный
        var targetProjectId = persona.Scope == PersonaScope.Project
            ? persona.ProjectId
            : contextProjectId;

        if (!string.IsNullOrEmpty(targetProjectId)
            && _projects.GetById(targetProjectId) is { } project && project.OwnerId == ownerId)
        {
            var projectSession = new Session
            {
                ProjectId = project.Id,
                OwnerId = ownerId,
                PersonaId = personaId,
                Mode = mode,
                ClaudeSessionId = resumeSessionId,
                Name = name,
                Model = persona.Model,
                Effort = persona.Effort,
                AutomationRuleId = automationRuleId,
            };
            await StartNewSessionAsync(projectSession, project.RootPath, project.SystemPrompt,
                () => _projects.GetById(project.Id)?.PermissionRules
                    ?? (IReadOnlyList<PermissionRule>)Array.Empty<PermissionRule>());
            return projectSession;
        }

        var rootPath = ResolveChatRoot(ownerId);
        var session = new Session
        {
            ProjectId = null,
            OwnerId = ownerId,
            PersonaId = personaId,
            Mode = mode,
            ClaudeSessionId = resumeSessionId,
            Name = name,
            Model = persona.Model,
            Effort = persona.Effort,
            AutomationRuleId = automationRuleId,
        };
        await StartNewSessionAsync(session, rootPath, rawSystemPrompt: null, permissionRules: null);
        return session;
    }

    // Создание группового чата (флаг persona-group-chats): 2-4 персоны владельца,
    // первая — ведущая (стартовый активный спикер). Зона — по ведущей, как в
    // CreatePersonaChatAsync: проектная персона → сессия её проекта, глобальная → чат вне проекта.
    public async Task<Session> CreateGroupChatAsync(string ownerId, IReadOnlyList<string> personaIds,
        ClaudeMode mode, string? name = null)
    {
        var participants = ValidateParticipants(ownerId, personaIds);
        var leader = participants[0];
        var participantIds = participants.Select(p => p.Id).ToList();

        if (leader.Scope == PersonaScope.Project && !string.IsNullOrEmpty(leader.ProjectId)
            && _projects.GetById(leader.ProjectId) is { } project && project.OwnerId == ownerId)
        {
            var projectSession = new Session
            {
                ProjectId = project.Id,
                OwnerId = ownerId,
                PersonaId = leader.Id,
                Participants = participantIds,
                Mode = mode,
                Name = name,
                Model = leader.Model,
                Effort = leader.Effort,
            };
            await StartNewSessionAsync(projectSession, project.RootPath, project.SystemPrompt,
                () => _projects.GetById(project.Id)?.PermissionRules
                    ?? (IReadOnlyList<PermissionRule>)Array.Empty<PermissionRule>());
            return projectSession;
        }

        var rootPath = ResolveChatRoot(ownerId);
        var session = new Session
        {
            ProjectId = null,
            OwnerId = ownerId,
            PersonaId = leader.Id,
            Participants = participantIds,
            Mode = mode,
            Name = name,
            Model = leader.Model,
            Effort = leader.Effort,
        };
        await StartNewSessionAsync(session, rootPath, rawSystemPrompt: null, permissionRules: null);
        return session;
    }

    // Обновить состав участников группового чата. Активный спикер сохраняется,
    // если остался в составе, иначе — новая ведущая. Адаптер пересоздаётся
    // (состав участников зашит в подсказку @упоминаний и групповой слой промпта).
    public Session? SetParticipants(string sessionId, string ownerId, IReadOnlyList<string> personaIds)
    {
        if (!_sessions.TryGetValue(sessionId, out var entry)) return null;
        if (SessionOwner(entry.Info) != ownerId) return null;

        var participants = ValidateParticipants(ownerId, personaIds);
        entry.Info.Participants = participants.Select(p => p.Id).ToList();
        var speaker = participants.FirstOrDefault(p => p.Id == entry.Info.PersonaId) ?? participants[0];
        SwitchSpeaker(entry, speaker);
        return entry.Info;
    }

    // Участники группового чата: 2-4 уникальные персоны, все принадлежат владельцу
    private List<Persona> ValidateParticipants(string ownerId, IReadOnlyList<string> personaIds)
    {
        var ids = (personaIds ?? Array.Empty<string>())
            .Where(id => !string.IsNullOrWhiteSpace(id)).Distinct().ToList();
        if (ids.Count is < 2 or > 4)
            throw new InvalidOperationException("В групповом чате участвуют от 2 до 4 персон");
        return ids.Select(id => _personas.Get(id, ownerId)
            ?? throw new KeyNotFoundException($"Персона не найдена: {id}")).ToList();
    }

    // Чаты владельца, ведущиеся от лица конкретной персоны (для раздела «Персоны»)
    public IReadOnlyList<Session> GetPersonaChats(string ownerId, string personaId) =>
        _sessions.Values
            .Select(e => e.Info)
            .Where(s => s.PersonaId == personaId && SessionOwner(s) == ownerId)
            .OrderByDescending(s => s.UpdatedAt)
            .ToList();

    // Владелец сессии: у чата — OwnerId, у проектной — владелец проекта
    private string? SessionOwner(Session s) =>
        s.ProjectId is not null ? _projects.GetById(s.ProjectId)?.OwnerId : s.OwnerId;

    // Персона-слой сессии (промпт характера + контекст памяти + auto-recall + сама персона
    // для гейтов возможностей). Строится одинаково при первом старте и при восстановлении процесса.
    // Промпт — замыкание: адаптер зовёт его на каждый ход, поэтому правки персоны
    // (контракт/характер), смена модели сессии и флаг PersonaSwitched применяются сразу.
    private (Func<string?>? Prompt, MemoryMcpContext? Memory, Func<string, Task<string?>>? Recall, Persona? Persona)
        BuildPersonaLayer(Session session, string? ownerId)
    {
        if (session.PersonaId is null || ownerId is null) return (null, null, null, null);
        var persona = _personas.Get(session.PersonaId, ownerId);
        if (persona is null) return (null, null, null, null);
        Func<string?> prompt = () =>
        {
            var p = session.PersonaId is { } pid ? _personas.Get(pid, ownerId) : null;
            if (p is null) return null;
            var built = _promptBuilder.Build(p, session.Model, session.PersonaSwitched,
                greeted: !string.IsNullOrWhiteSpace(p.Greeting));
            // Групповой чат: надстройка со списком участников и правилом «говори только за себя»
            if (session.Participants is { Count: > 1 } memberIds)
            {
                var members = memberIds.Select(id => _personas.Get(id, ownerId))
                    .OfType<Persona>().ToList();
                if (members.Count > 1) built += "\n\n" + BuildGroupChatHint(p, members);
            }
            return built;
        };
        // Долгая память — только если включена у персоны
        if (persona.MemoryEnabled)
            return (prompt, BuildMemoryContext(ownerId, persona.Id), BuildPersonaRecallProvider(ownerId, persona.Id), persona);
        return (prompt, null, null, persona);
    }

    // Провайдер блока «Привязанные знания и правила» персоны (флаг persona-bindings):
    // на каждый ход перечитывает персону (привязки могли измениться) и собирает
    // индекс + always-выжимки. mountedSections — секции workspace, реально смонтированные
    // этой сессии (типы без своей секции в индекс не попадают). Ошибки → null (ход без блока).
    private Func<string, Task<string?>>? BuildBindingsProvider(string? ownerId, string? personaId,
        IReadOnlyList<string>? mountedSections)
    {
        if (ownerId is null || personaId is null) return null;
        var sections = mountedSections ?? [];
        return async text =>
        {
            try { return await _bindings.BuildTurnBlockAsync(ownerId, personaId, text, sections); }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Блок привязок персоны {Persona}", personaId);
                return null;
            }
        };
    }

    // Сброс адаптеров живых сессий персоны (изменился профиль/возможности/привязки):
    // процесс пересоздаётся при следующем сообщении с актуальным контекстом,
    // транскрипт продолжается через --resume (паттерн SetPersona)
    private void InvalidatePersonaSessions(string personaId)
    {
        foreach (var entry in _sessions.Values.Where(e => e.Info.PersonaId == personaId))
        {
            if (entry.Process is { } old)
            {
                entry.Process = null;
                FireAndForget(old.DisposeAsync().AsTask(),
                    $"остановка адаптера после изменения персоны ({entry.Info.Id})");
            }
        }
    }

    // Групповая надстройка промпта: участники чата + дисциплина «отвечай только от своего
    // лица». Добавляется к персона-слою активного спикера на каждый ход.
    internal static string BuildGroupChatHint(Persona self, IReadOnlyList<Persona> participants)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("Это ГРУППОВОЙ чат: пользователь общается сразу с несколькими персонами, " +
                      "отвечает та, к кому обращаются (@handle). Участники:");
        foreach (var p in participants)
        {
            var title = string.IsNullOrWhiteSpace(p.Role) ? p.Name : $"{p.Role} ({p.Name})";
            sb.AppendLine($"- @{p.Handle} — {title}{(p.Id == self.Id ? " (это ты)" : "")}");
        }
        sb.AppendLine("Сейчас отвечаешь ты. Отвечай ТОЛЬКО от своего лица и в своём характере — " +
                      "НЕ сочиняй и не пиши реплики за других участников.");
        sb.Append("Если пользователь обращается ко всем или просит мнение другого участника — " +
                  "спроси его инструментом persona_ask и передай суть ответа своими словами, " +
                  "явно указав автора.");
        return sb.ToString();
    }

    // Контекст MCP-сервера персон: CRUD персон из любого чата (за флагом personas);
    // при включённом persona-mentions и наличии других персон в контексте — плюс
    // @упоминания: MentionsHint (блок «@handle — Роль (Имя)» для промпта) и persona_ask.
    // В групповом чате mentions-режим (persona_ask + подсказка по УЧАСТНИКАМ) включён
    // всегда, независимо от флага persona-mentions — иначе спикер не сможет спросить коллег.
    private PersonasMcpContext? BuildPersonasContext(string? ownerId, string? projectId, Session session)
    {
        if (ownerId is null) return null;

        var selfPersonaId = session.PersonaId;
        var isGroup = session.Participants is { Count: > 1 };
        string? mentionsHint = null;
        // @упоминания (persona_ask + подсказка) теперь всегда включены
        {
            var others = (isGroup
                    ? session.Participants!.Select(id => _personas.Get(id, ownerId)).OfType<Persona>()
                    : _personas.GetForContext(ownerId, projectId))
                .Where(p => p.Id != selfPersonaId)
                .ToList();
            if (others.Count > 0)
            {
                var sb = new System.Text.StringBuilder();
                sb.AppendLine("Любую персону можно спросить инструментом persona_ask (параметры: handle, " +
                    "question, context?) — она ответит от своего лица, со своим характером и памятью. " +
                    "Когда пользователь упоминает персону через @handle — обязательно обратись к ней " +
                    "через persona_ask и учти её ответ. Вопрос формулируй самодостаточно: персона не " +
                    "видит этот разговор. Если вызов вернул «No such tool available» — сервер персон ещё " +
                    "подключается: подожди мгновение и повтори тот же вызов. Доступные собеседники:");
                foreach (var p in others)
                {
                    var title = string.IsNullOrWhiteSpace(p.Role) ? p.Name : $"{p.Role} ({p.Name})";
                    sb.Append($"- @{p.Handle} — {title}");
                    if (!string.IsNullOrWhiteSpace(p.Description)) sb.Append($": {p.Description.Trim()}");
                    sb.AppendLine();
                }
                mentionsHint = sb.ToString().TrimEnd();
            }
        }

        var entry = _notesTokens.AddOrUpdate(ownerId,
            id => (_jwt.IssueServiceToken(id), DateTime.UtcNow),
            (id, old) => DateTime.UtcNow - old.IssuedAt > JwtService.ServiceTokenLifetime - TimeSpan.FromDays(1)
                ? (_jwt.IssueServiceToken(id), DateTime.UtcNow)
                : old);
        return new PersonasMcpContext(ResolveTasksApiUrl(), entry.Token, projectId, selfPersonaId,
            mentionsHint, BindingsEnabled: true);
    }

    // Контекст MCP-сервера рабочего пространства: доступ ко всем проектам владельца
    // (за флагом workspace-tools). Секции сужаются возможностями персоны (единая точка
    // истины — PersonaBindingsService.EffectiveToolEnabled: Tool-привязка приоритетнее
    // Persona.Tools); search остаётся при любом непустом наборе. Секция chats — за
    // отдельным флагом workspace-chat-send, секция destructive (безвозвратное удаление) —
    // за флагом workspace-destructive. Все возможности выключены → сервер не подключаем.
    // Project/ProjectPath-привязки персоны сужают зону (AllowedProjectIds) до привязанных
    // проектов + проекта текущей сессии; БЕЗ таких привязок поведение как у Claude —
    // все проекты владельца (null).
    private WorkspaceMcpContext? BuildWorkspaceContext(string? ownerId, string? projectId,
        string? selfSessionId, Persona? persona)
    {
        if (ownerId is null) return null;

        var sections = new List<string>();
        foreach (var key in new[] { "projects", "files", "knowledge" })
            if (_bindings.EffectiveToolEnabled(ownerId, persona, key)) sections.Add(key);
        if (_bindings.EffectiveToolEnabled(ownerId, persona, "chats"))
            sections.Add("chats");
        if (sections.Count == 0) return null;
        // Разрушающие операции (files_delete/chats_delete) — за отдельным флагом
        // workspace-destructive; персоне дополнительно нужен tool-ключ destructive
        // (Tool-привязка или Persona.Tools). Одна destructive без базовых секций не монтируется.
        // Профиль «Только чтение» строже любых привязок — секцию не монтируем вовсе.
        if (_flags.IsEnabled(ownerId, FeatureFlagKeys.WorkspaceDestructive)
            && persona?.Access != PersonaAccess.ReadOnly
            && _bindings.EffectiveToolEnabled(ownerId, persona, "destructive"))
            sections.Add("destructive");
        sections.Add("search");

        IReadOnlyList<string>? allowedIds = null;
        if (_bindings.BuildFileScopes(ownerId, persona) is { } scopes)
        {
            // Привязки есть — зона ужимается; проект самой сессии всегда доступен
            var set = new HashSet<string>(scopes);
            if (projectId is not null) set.Add(projectId);
            allowedIds = set.ToList();
        }

        var entry = _notesTokens.AddOrUpdate(ownerId,
            id => (_jwt.IssueServiceToken(id), DateTime.UtcNow),
            (id, old) => DateTime.UtcNow - old.IssuedAt > JwtService.ServiceTokenLifetime - TimeSpan.FromDays(1)
                ? (_jwt.IssueServiceToken(id), DateTime.UtcNow)
                : old);
        return new WorkspaceMcpContext(ResolveTasksApiUrl(), entry.Token, projectId,
            sections, allowedIds, selfSessionId);
    }

    // Дополнительные запреты сессии персоны: профиль доступа (PersonaAccessPolicy — «пол»
    // запретов: ReadOnly/Custom) + capability-решение «web» через привязки
    // (EffectiveToolEnabled: Tool-привязка приоритетнее Persona.Tools). Web-решение передаём
    // в policy параметром, чтобы не дублировать логику; запреты складываются — побеждает
    // более строгий (ReadOnly режет мутации, даже если binding разрешил инструмент).
    private IReadOnlyList<string>? BuildExtraDisallowed(string? ownerId, Persona? persona) =>
        PersonaAccessPolicy.BuildExtraDisallowed(persona,
            webAllowed: _bindings.EffectiveToolEnabled(ownerId, persona, "web"));


    // Назначить/сменить собеседника чату (единый селектор): персону (personaId) ИЛИ
    // стандартного .md-агента Claude (agentName) — взаимоисключающе. Оба пустые = снять.
    // Разрешено и ПО ХОДУ разговора: персона-слой строится на каждый ход, транскрипт
    // продолжается через --resume с новым системным слоем. Модель/усилие подтягиваются
    // из персоны; у начатой сессии — только при том же провайдере (guard «смена
    // провайдера у начатой сессии — 400» нерушим: транскрипт живёт у эндпоинта).
    public Session? SetPersona(string sessionId, string ownerId, string? personaId, string? agentName = null)
    {
        if (!_sessions.TryGetValue(sessionId, out var entry)) return null;
        if (SessionOwner(entry.Info) != ownerId) return null;

        Persona? persona = null;
        if (!string.IsNullOrEmpty(personaId))
        {
            persona = _personas.Get(personaId, ownerId)
                ?? throw new KeyNotFoundException("Персона не найдена");
        }

        SwitchSpeaker(entry, persona, agentName);
        return entry.Info;
    }

    // Общее ядро смены собеседника/спикера (SetPersona и роутинг группового чата):
    // PersonaId и модель/усилие персоны (у начатой сессии — только при том же провайдере),
    // флаг PersonaSwitched, сброс адаптера (новый слой подхватится при следующем ходе), Save.
    private void SwitchSpeaker(SessionEntry entry, Persona? persona, string? agentName = null)
    {
        var started = entry.Info.ClaudeSessionId is not null;
        var switching = started &&
            (entry.Info.PersonaId != persona?.Id || entry.Info.AgentName is not null || persona is not null);

        entry.Info.PersonaId = persona?.Id;
        // .md-агент и персона взаимоисключающие: назначение одного сбрасывает другого
        entry.Info.AgentName = persona is null && !string.IsNullOrWhiteSpace(agentName)
            ? agentName.Trim()
            : null;
        if (persona is not null)
        {
            if (!started)
            {
                entry.Info.Model = persona.Model;
                entry.Info.Effort = persona.Effort;
            }
            else if (_llmProviders.ProviderKey(persona.Model) == _llmProviders.ProviderKey(entry.Info.Model))
            {
                // Тот же провайдер — модель персоны применяется со следующего хода;
                // другой провайдер — оставляем модель сессии (характер всё равно её)
                entry.Info.Model = persona.Model ?? entry.Info.Model;
                entry.Info.Effort = persona.Effort ?? entry.Info.Effort;
            }
        }
        if (switching) entry.Info.PersonaSwitched = true;
        entry.Info.UpdatedAt = DateTime.UtcNow;

        // Ходов не было — пересоздаём адаптер с новым контекстом при следующем сообщении
        if (entry.Process is { } old)
        {
            entry.Process = null;
            FireAndForget(old.DisposeAsync().AsTask(),
                $"остановка адаптера при смене собеседника ({entry.Info.Id})");
        }
        SaveSessions();
    }

    // Роутинг спикера группового чата перед ходом: @упоминание участника переключает
    // активного спикера (SwitchSpeaker + speaker_changed клиентам). Во время активного
    // хода (Working/Waiting) состав не трогаем — переключение подействует со следующего.
    private async Task RouteGroupSpeakerAsync(string sessionId, SessionEntry entry, string text)
    {
        if (entry.Info.Participants is not { Count: > 1 } participantIds) return;
        if (entry.Info.Status is SessionStatus.Working or SessionStatus.Waiting) return;
        var ownerId = SessionOwner(entry.Info);
        if (ownerId is null) return;

        var participants = participantIds
            .Select(id => _personas.Get(id, ownerId))
            .OfType<Persona>()
            .ToList();
        if (participants.Count == 0) return;

        var route = GroupChatRouter.Resolve(text, participants, entry.Info.PersonaId);
        if (!route.Switched) return;

        var speaker = participants.First(p => p.Id == route.SpeakerPersonaId);
        SwitchSpeaker(entry, speaker);

        var label = string.IsNullOrWhiteSpace(speaker.Role) ? speaker.Name : $"{speaker.Role} ({speaker.Name})";
        // Только в session-группу: клиент открытого чата состоит и в user_/project_-группе,
        // рассылка в обе дублировала разделитель «Теперь отвечает» в ленте
        await BroadcastAsync(sessionId, new SpeakerChangedMessage(speaker.Id, label));
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

        // Владелец: у проектной сессии — владелец проекта, у чата — из самой сессии
        var ownerId = session.ProjectId is not null
            ? _projects.GetById(session.ProjectId)?.OwnerId
            : session.OwnerId;

        // Персона: её характер инжектится в системный промпт.
        // Scope контекста уже задан типом сессии (глобальная персона → чат без проекта →
        // доступ ко всем данным владельца; проектная → сессия проекта → только он).
        var persona = BuildPersonaLayer(session, ownerId);
        var workspace = BuildWorkspaceContext(ownerId, session.ProjectId, session.Id, persona.Persona);

        var adapter = _adapters.Create(session, new LlmSessionContext(rootPath,
            msg => OnMessageAsync(session.Id, accumulator, msg),
            rawSystemPrompt, permissionRules,
            TasksMcp: TasksMcpEnabled(ownerId, session, persona.Persona) ? BuildTasksContext(ownerId, session.ProjectId) : null,
            NotesMcp: _bindings.EffectiveToolEnabled(ownerId, persona.Persona, "notes") ? BuildNotesContext(ownerId, session.ProjectId) : null,
            RecallProvider: BuildRecallProvider(ownerId),
            PersonaPromptProvider: persona.Prompt,
            MemoryMcp: persona.Memory,
            PersonaRecallProvider: persona.Recall,
            ExtraDisallowedTools: BuildExtraDisallowed(ownerId, persona.Persona),
            PersonasMcp: BuildPersonasContext(ownerId, session.ProjectId, session),
            WorkspaceMcp: workspace,
            BindingsProvider: BuildBindingsProvider(ownerId, session.PersonaId, workspace?.Sections)));
        entry.Process = adapter;

        await adapter.StartAsync();
        SaveSessions();
    }

    public async Task SendMessageAsync(string sessionId, string text, IReadOnlyList<string> attachedPaths, string? mode = null, bool systemDirective = false, bool auto = false, string? senderPersonaId = null)
    {
        if (!_sessions.TryGetValue(sessionId, out var entry))
            throw new InvalidOperationException("Сессия не найдена");

        // Режим, выбранный в Composer, применяется со следующего хода: процесс claude
        // пересоздаётся в RunTurnAsync и читает --permission-mode из Info.Mode.
        // Режим «План» у провайдера без поддержки тихо игнорируем (защита от рассинхрона UI).
        var caps = _llmProviders.CapabilitiesFor(entry.Info.Model);
        if (mode is not null && Enum.TryParse<ClaudeMode>(mode, true, out var parsedMode)
            && entry.Info.Mode != parsedMode
            && (parsedMode != ClaudeMode.Plan || caps.SupportsPlanMode))
        {
            entry.Info.Mode = parsedMode;
            SaveSessions();
        }

        // Групповой чат: @упоминание участника в тексте переключает активного спикера
        // ДО пересоздания процесса — новый персона-слой применяется уже к этому ходу
        await RouteGroupSpeakerAsync(sessionId, entry, text);

        await EnsureProcessAsync(sessionId, entry);

        // Авторство реплик хода: text-сообщения истории получают персону на момент хода
        // (после смены собеседника старые реплики сохраняют прежний аватар)
        entry.Accumulator?.SetPersona(entry.Info.PersonaId);

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

        await ApplyStatusAsync(sessionId, entry, SessionStatus.Working);

        entry.Accumulator?.OnUserMessage(text, attachedPaths, systemDirective: systemDirective, auto: auto, senderPersonaId: senderPersonaId);

        // Push-источники автоматизаций: @упоминание персоны в тексте пользователя.
        // Fire-and-forget — обработчик не должен тормозить ход (он лишь детектит и ставит в очередь).
        if (OnUserMessage is { } userMsgObservers)
        {
            _ = Task.Run(async () =>
            {
                try { await userMsgObservers(entry.Info, text, senderPersonaId); }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[SessionManager] Ошибка OnUserMessage ({sessionId}): {ex.Message}");
                }
            });
        }
        // Обвязки хода (OmO) дописываются только к тексту для CLI —
        // история и UI хранят исходное сообщение пользователя
        await entry.Process!.SendMessageAsync(BuildCliTurnText(entry, text), attachedPaths);
        // Превью чата (LastMessage выставляет адаптер из текста для CLI) — исходным сообщением
        entry.Info.LastMessage = text.Length > 100 ? text[..100] + "…" : text;
    }

    // Текст хода для CLI: исходное сообщение + обвязки OmO.
    // Ultrawork (флаг ultrawork-keyword) — по магическому слову в сообщении;
    // протокол цикла «до готово» — пока Session.WorkLoop активен.
    private string BuildCliTurnText(SessionEntry entry, string text)
    {
        var result = text;

        if (OmoPrompts.ContainsUltraworkKeyword(text)
            && OmoPrompts.Ultrawork.Length > 0)
        {
            result += "\n\n" + OmoPrompts.Ultrawork;
        }

        if (entry.Info.WorkLoop is { } loop)
        {
            entry.LoopTurnText.Clear(); // копим текст нового хода для поиска маркера
            // Верификационный ход идёт со своей директивой — рабочий протокол не дописываем
            if (loop.Phase != "verifying")
                result += "\n\n" + OmoPrompts.WorkLoopTurn(loop.Promise);
        }

        return result;
    }

    // Владелец сессии: у проектной — владелец проекта, у чата — из самой сессии
    private string? SessionOwnerId(Session session) => session.ProjectId is not null
        ? _projects.GetById(session.ProjectId)?.OwnerId
        : session.OwnerId;

    // Отправка сообщения с ожиданием завершения хода — REST-канал агентов (chats_send).
    // Занятую или ждущую человека сессию не трогаем (Busy). Таймаут НЕ отменяет ход:
    // вызывающий получает Running и позже читает результат через историю (chats_history).
    public async Task<SendAndWaitResult> SendMessageAndWaitAsync(string sessionId, string text,
        TimeSpan timeout, int agentDepth = 0, string? senderPersonaId = null)
    {
        if (!_sessions.TryGetValue(sessionId, out var entry))
            throw new InvalidOperationException("Сессия не найдена");

        // Занята только при реально идущем ходе (Working) или ожидании человека (Waiting).
        // Starting у живой сессии означает лишь «создан, ход ещё не запускался»: после первого
        // хода статус идёт Working→Active/Finished и назад в Starting не возвращается (обратно
        // Starting ставит только рестарт → Orphaned). Поэтому свежесозданный чат (у него Process
        // уже присвоен в StartNewSessionAsync, но ходов не было) НЕ занят — принимаем сообщение
        // и стартуем первый ход. Гонку двух одновременных ходов ловит TurnWaiter ниже.
        var status = entry.Info.Status;
        if (status is SessionStatus.Working or SessionStatus.Waiting)
            return new SendAndWaitResult.Busy(status);

        await EnsureProcessAsync(sessionId, entry);
        entry.Accumulator?.SetPersona(entry.Info.PersonaId);

        // Авто-имя по первому сообщению — как при отправке человеком
        if (string.IsNullOrWhiteSpace(entry.Info.Name))
        {
            var title = MakeChatTitle(text);
            if (!string.IsNullOrEmpty(title))
            {
                entry.Info.Name = title;
                SaveSessions();
            }
        }

        // Один ожидатель на ход: параллельная отправка проиграла гонку за ход — busy
        var tcs = new TaskCompletionSource<TurnResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (Interlocked.CompareExchange(ref entry.TurnWaiter, tcs, null) is not null)
            return new SendAndWaitResult.Busy(entry.Info.Status);
        entry.TurnWaiterBaseline = entry.Accumulator?.GetAll().Count ?? 0;

        await ApplyStatusAsync(sessionId, entry, SessionStatus.Working);
        entry.Accumulator?.OnUserMessage(text, [], viaAgent: agentDepth >= 1, senderPersonaId: senderPersonaId);
        await entry.Process!.SendMessageAsync(text, null, agentDepth);

        if (timeout <= TimeSpan.Zero) return new SendAndWaitResult.Running();
        var completed = await Task.WhenAny(tcs.Task, Task.Delay(timeout));
        return completed == tcs.Task
            ? new SendAndWaitResult.Completed(await tcs.Task)
            : new SendAndWaitResult.Running(); // ход продолжается, ожидатель очистит OnMessageAsync
    }

    // Ручное сворачивание контекста: /compact в CLI, минуя счётчики и историю user-сообщений
    public async Task CompactAsync(string sessionId)
    {
        if (!_sessions.TryGetValue(sessionId, out var entry))
            throw new InvalidOperationException("Сессия не найдена");
        if (entry.Info.ClaudeSessionId is null) return; // ходов ещё не было — сворачивать нечего
        if (!_llmProviders.CapabilitiesFor(entry.Info.Model).SupportsCompact)
            return; // провайдер не умеет compact — защита от рассинхрона UI

        await EnsureProcessAsync(sessionId, entry);
        entry.Accumulator?.SetPersona(entry.Info.PersonaId);

        await ApplyStatusAsync(sessionId, entry, SessionStatus.Working);

        await entry.Process!.CompactAsync();
    }

    // После перезапуска сервера Process может быть null — восстанавливаем сессию
    private async Task EnsureProcessAsync(string sessionId, SessionEntry entry)
    {
        if (entry.Process is not null) return;

        // Переиспускаем существующий in-memory аккумулятор (процесс мог быть сброшен
        // сменой собеседника — SwitchSpeaker; его состояние, включая ещё не сохранённые
        // внеходовые карточки конвейера/совещания, нельзя терять). Новый создаём только
        // при ленивом восстановлении сессии после рестарта сервера (Accumulator == null).
        var accumulator = entry.Accumulator;
        if (accumulator is null)
        {
            var existingHistory = entry.Info.ClaudeSessionId != null
                ? await _history.LoadAsync(entry.Info.ClaudeSessionId)
                : [];
            accumulator = new TurnAccumulator(existingHistory, entry.Info.ClaudeSessionId);
            entry.Accumulator = accumulator;
        }

        // Чат вне проекта — рабочая папка Chats, без проектного промпта и правил;
        // проектная сессия — RootPath/SystemPrompt/PermissionRules из проекта.
        // Персона-слой восстанавливаем так же, как при первом старте (иначе после рестарта
        // сервера персонная сессия теряла бы характер и долгую память).
        LlmSessionContext context;
        if (entry.Info.ProjectId is null)
        {
            var rootPath = ResolveChatRoot(entry.Info.OwnerId
                ?? throw new InvalidOperationException("У чата не задан владелец"));
            var persona = BuildPersonaLayer(entry.Info, entry.Info.OwnerId);
            var workspace = BuildWorkspaceContext(entry.Info.OwnerId, null, entry.Info.Id, persona.Persona);
            context = new LlmSessionContext(rootPath,
                msg => OnMessageAsync(sessionId, accumulator, msg),
                RawSystemPrompt: null, PermissionRules: null,
                TasksMcp: TasksMcpEnabled(entry.Info.OwnerId, entry.Info, persona.Persona) ? BuildTasksContext(entry.Info.OwnerId, null) : null,
                NotesMcp: _bindings.EffectiveToolEnabled(entry.Info.OwnerId, persona.Persona, "notes") ? BuildNotesContext(entry.Info.OwnerId, null) : null,
                RecallProvider: BuildRecallProvider(entry.Info.OwnerId),
                PersonaPromptProvider: persona.Prompt,
                MemoryMcp: persona.Memory,
                PersonaRecallProvider: persona.Recall,
                ExtraDisallowedTools: BuildExtraDisallowed(entry.Info.OwnerId, persona.Persona),
                PersonasMcp: BuildPersonasContext(entry.Info.OwnerId, null, entry.Info),
                WorkspaceMcp: workspace,
                BindingsProvider: BuildBindingsProvider(entry.Info.OwnerId, entry.Info.PersonaId, workspace?.Sections));
        }
        else
        {
            var project = _projects.GetById(entry.Info.ProjectId)
                ?? throw new InvalidOperationException("Проект не найден");
            var persona = BuildPersonaLayer(entry.Info, project.OwnerId);
            var workspace = BuildWorkspaceContext(project.OwnerId, project.Id, entry.Info.Id, persona.Persona);
            context = new LlmSessionContext(project.RootPath,
                msg => OnMessageAsync(sessionId, accumulator, msg),
                project.SystemPrompt,
                () => _projects.GetById(entry.Info.ProjectId!)?.PermissionRules ?? (IReadOnlyList<PermissionRule>)Array.Empty<PermissionRule>(),
                TasksMcp: TasksMcpEnabled(project.OwnerId, entry.Info, persona.Persona) ? BuildTasksContext(project.OwnerId, project.Id) : null,
                NotesMcp: _bindings.EffectiveToolEnabled(project.OwnerId, persona.Persona, "notes") ? BuildNotesContext(project.OwnerId, project.Id) : null,
                RecallProvider: BuildRecallProvider(project.OwnerId),
                PersonaPromptProvider: persona.Prompt,
                MemoryMcp: persona.Memory,
                PersonaRecallProvider: persona.Recall,
                ExtraDisallowedTools: BuildExtraDisallowed(project.OwnerId, persona.Persona),
                PersonasMcp: BuildPersonasContext(project.OwnerId, project.Id, entry.Info),
                WorkspaceMcp: workspace,
                BindingsProvider: BuildBindingsProvider(project.OwnerId, entry.Info.PersonaId, workspace?.Sections));
        }
        var adapter = _adapters.Create(entry.Info, context);
        entry.Process = adapter;
        await adapter.StartAsync();
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
    // (процесс claude пересоздаётся в RunTurnAsync), Info — общая ссылка с адаптером.
    public Session? Update(string sessionId, string? name, string? model, string? effort)
    {
        if (!_sessions.TryGetValue(sessionId, out var entry)) return null;
        var newModel = string.IsNullOrWhiteSpace(model) ? null : model.Trim();

        // Смена провайдера: контекст сессии живёт у провайдера (транскрипт эндпоинта),
        // «переехавшая» сессия молча потеряла бы его — для начатых сессий запрещаем.
        if (_llmProviders.ProviderKey(newModel) != _llmProviders.ProviderKey(entry.Info.Model))
        {
            if (entry.Info.ClaudeSessionId is not null)
                throw new InvalidOperationException(
                    "Нельзя сменить провайдера у начатой сессии — создайте новый чат");
            // Ходов ещё не было — пересоздаём адаптер нужного типа при следующем сообщении
            if (entry.Process is { } old)
            {
                entry.Process = null;
                FireAndForget(old.DisposeAsync().AsTask(),
                    $"остановка адаптера при смене провайдера ({sessionId})");
            }
        }

        entry.Info.Name = string.IsNullOrWhiteSpace(name) ? null : name.Trim();
        entry.Info.Model = newModel;
        entry.Info.Effort = string.IsNullOrWhiteSpace(effort) ? null : effort.Trim();
        entry.Info.UpdatedAt = DateTime.UtcNow;
        SaveSessions();
        return entry.Info;
    }

    public void RespondPermission(string sessionId, string requestId, string behavior)
    {
        if (!_sessions.TryGetValue(sessionId, out var entry)) return;
        entry.Process?.RespondPermission(requestId, behavior);
        FireAndForget(ApplyStatusAsync(sessionId, entry, SessionStatus.Working),
            $"смена статуса после permission ({sessionId})");
    }

    public void Interrupt(string sessionId)
    {
        if (_sessions.TryGetValue(sessionId, out var entry))
        {
            // Стоп пользователя прерывает и цикл «до готово»: снимаем СИНХРОННО,
            // чтобы exited прерванного хода не запустил автопродолжение
            if (entry.Info.WorkLoop is not null)
            {
                entry.Info.WorkLoop = null;
                SaveSessions();
                _ = BroadcastWorkLoopAsync(sessionId, entry);
            }
            entry.Process?.Interrupt();
        }
    }

    // Включение/выключение цикла «до готово» (флаг work-loop). Включение сбрасывает
    // счётчик итераций; лимит — из конфига Loop:MaxIterations (дефолт 20).
    // userId задан (вызов из API) — сверяется с владельцем; null — внутренний вызов.
    public async Task<Session?> SetWorkLoopAsync(string sessionId, bool enabled, string? userId = null)
    {
        if (!_sessions.TryGetValue(sessionId, out var entry)) return null;
        if (userId is not null && SessionOwnerId(entry.Info) != userId) return null;

        entry.Info.WorkLoop = enabled
            ? new SessionWorkLoop
            {
                MaxIterations = int.TryParse(_config["Loop:MaxIterations"], out var m) ? m : 20,
            }
            : null;
        entry.LoopTurnText.Clear();
        SaveSessions();
        await BroadcastWorkLoopAsync(sessionId, entry);
        return entry.Info;
    }

    private Task BroadcastWorkLoopAsync(string sessionId, SessionEntry entry)
    {
        var loop = entry.Info.WorkLoop;
        return BroadcastAsync(sessionId, new WorkLoopMessage(
            loop is not null, loop?.Iteration ?? 0, loop?.MaxIterations ?? 0, loop?.Phase));
    }

    // Автопродолжение цикла «до готово»: вызывается после штатного завершения хода (exited).
    // Маркер найден → верификационный ход, затем стоп; нет → продолжение до лимита итераций.
    private async Task ContinueWorkLoopAsync(string sessionId)
    {
        if (!_sessions.TryGetValue(sessionId, out var entry)) return;
        if (entry.Info.WorkLoop is not { } loop) return;

        if (entry.LoopTurnFailed)
        {
            await SetWorkLoopAsync(sessionId, false);
            return;
        }

        var promiseFound = entry.LoopTurnText.ToString()
            .Contains($"<promise>{loop.Promise}</promise>", StringComparison.OrdinalIgnoreCase);

        if (loop.Phase == "verifying")
        {
            // Верификационный ход отработал — цикл завершён независимо от исхода
            await SetWorkLoopAsync(sessionId, false);
            return;
        }

        if (promiseFound)
        {
            loop.Phase = "verifying";
            SaveSessions();
            await BroadcastWorkLoopAsync(sessionId, entry);
            await SendMessageAsync(sessionId, OmoPrompts.WorkLoopVerification, [], systemDirective: true);
            return;
        }

        loop.Iteration++;
        if (loop.Iteration >= loop.MaxIterations)
        {
            await SetWorkLoopAsync(sessionId, false);
            return;
        }

        SaveSessions();
        await BroadcastWorkLoopAsync(sessionId, entry);
        await SendMessageAsync(sessionId,
            OmoPrompts.WorkLoopContinuation(loop.Promise, loop.Iteration, loop.MaxIterations), [], systemDirective: true);
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
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[SessionManager] Ответ на вопрос ({sessionId}) не распарсился, в историю уйдёт без answers: {ex.Message}");
            }
            entry.Accumulator.OnQuestionAnswered(toolUseId, answers);
            FireAndForget(entry.Accumulator.SaveSnapshotAsync(_history),
                $"сохранение истории после ответа на вопрос ({sessionId})");
        }
        FireAndForget(ApplyStatusAsync(sessionId, entry, SessionStatus.Working),
            $"смена статуса после ответа на вопрос ({sessionId})");
    }

    public void RespondPlan(string sessionId, string requestId, bool approve, string? feedback)
    {
        if (!_sessions.TryGetValue(sessionId, out var entry)) return;
        entry.Process?.RespondPlan(requestId, approve, feedback);
        // Фиксируем решение по плану в истории, чтобы карточка пережила перезагрузку
        if (entry.Accumulator is not null)
        {
            entry.Accumulator.OnPlanResolved(requestId, approve, feedback);
            FireAndForget(entry.Accumulator.SaveSnapshotAsync(_history),
                $"сохранение истории после решения по плану ({sessionId})");
        }
        FireAndForget(ApplyStatusAsync(sessionId, entry, SessionStatus.Working),
            $"смена статуса после решения по плану ({sessionId})");
    }

    public async Task DeleteAsync(string sessionId)
    {
        if (!_sessions.TryRemove(sessionId, out var entry)) return;
        if (entry.Process is not null)
            await entry.Process.DisposeAsync();
        // Дочищаем историю на диске — иначе data/sessions/{id} копится мусором
        if (entry.Info.ClaudeSessionId is string csid)
            _history.Delete(csid);
        SaveSessions();
        try { OnSessionDeleted?.Invoke(entry.Info); } catch { /* наблюдатель не должен ронять удаление */ }
        await BroadcastChatDeletedAsync(sessionId, entry.Info);
    }

    // Уведомить клиентов об удалении чата (в т.ч. авто-удалении временного) —
    // адресация как у BroadcastStatusChangeAsync: проект или владелец чата
    private async Task BroadcastChatDeletedAsync(string sessionId, Session info)
    {
        var msg = new ChatDeletedMessage() with { SessionId = sessionId };
        var tasks = new List<Task> { _hub.Clients.Group(sessionId).SendAsync("message", msg) };
        if (info.ProjectId is string pid)
            tasks.Add(_hub.Clients.Group("project_" + pid).SendAsync("message", msg));
        else if (info.OwnerId is string oid)
            tasks.Add(_hub.Clients.Group("user_" + oid).SendAsync("message", msg));
        await Task.WhenAll(tasks);
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
                case TextDeltaMessage m:
                    acc.OnTextDelta(m.Text);
                    // Цикл «до готово»: копим текст хода для поиска маркера завершения
                    if (entry?.Info.WorkLoop is not null) entry.LoopTurnText.Append(m.Text);
                    break;
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
                case CompactBoundaryMessage m:
                    acc.OnCompactBoundary(m.Trigger, m.PreTokens, m.PostTokens);
                    await acc.SaveSnapshotAsync(_history); // авто-компакт бывает посреди хода — фиксируем сразу
                    break;
                case AskQuestionMessage m:
                    acc.OnAskQuestion(m.ToolUseId, m.Input);
                    await acc.SaveSnapshotAsync(_history);
                    break;
                case PlanReviewMessage m:
                    acc.OnPlanReview(m.RequestId, m.Plan);
                    await acc.SaveSnapshotAsync(_history);
                    break;
                case ResultMessage m:
                    await acc.OnResultAsync(m.Subtype, m.DurationMs, m.NumTurns, m.Usage, m.TotalCostUsd, m.ApiErrorStatus, m.PermissionDenials, _history);
                    if (entry is not null) entry.LoopTurnFailed = m.Subtype == "error";
                    break;
                case RateLimitMessage m:    _usage.Record(m.LimitType, m.Utilization, m.Status, m.IsUsingOverage, m.ResetsAt, m.OverageStatus, m.OverageResetsAt); break;
                case ErrorMessage m:        await acc.OnErrorAsync(m.Text, _history); break;
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[SessionManager] Ошибка аккумулятора ({sessionId}): {ex.Message}");
        }

        // Резолв ожидателя синхронного хода (SendMessageAndWaitAsync): result — штатное
        // завершение, error/exited — обрыв (резолвим тоже, чтобы вызывающий не завис).
        // Обнуляем безусловно — ожидатель не должен утечь в следующий ход.
        if (entry is not null && msg is ResultMessage or ErrorMessage or ExitedMessage
            && Interlocked.Exchange(ref entry.TurnWaiter, null) is { } waiter)
        {
            switch (msg)
            {
                case ResultMessage rm:
                    waiter.TrySetResult(new TurnResult(
                        LastAssistantText(acc, entry.TurnWaiterBaseline), rm.DurationMs, rm.TotalCostUsd));
                    break;
                case ErrorMessage em:
                    waiter.TrySetResult(new TurnResult(em.Text, 0, null));
                    break;
                case ExitedMessage:
                    // Прерван без result — отдаём то, что ассистент успел написать
                    waiter.TrySetResult(new TurnResult(
                        LastAssistantText(acc, entry.TurnWaiterBaseline), 0, null));
                    break;
            }
        }

        // Обновление статуса — всегда, независимо от аккумулятора; SessionManager —
        // ЕДИНСТВЕННЫЙ владелец переходов Session.Status (ClaudeSession статус не пишет).
        // Если OnResultAsync выбросит, статус всё равно обновится.
        if (entry is not null)
        {
            SessionStatus? newStatus = null;

            if (msg is PermissionRequestMessage or AskQuestionMessage or PlanReviewMessage)
                newStatus = SessionStatus.Waiting;
            else if (msg is ResultMessage rm)
                // Active (не Finished): клиент по active перезагружает историю хода;
                // финальный Finished выставится по ExitedMessage ниже
                newStatus = rm.Subtype == "error" ? SessionStatus.Error : SessionStatus.Active;
            else if (msg is ErrorMessage)
                newStatus = SessionStatus.Error;
            else if (msg is ExitedMessage)
                newStatus = entry.Info.Status switch
                {
                    // прерван без result — возвращаем в рабочее состояние
                    SessionStatus.Working or SessionStatus.Waiting => SessionStatus.Active,
                    // ход завершился штатно (result уже перевёл в Active) — фиксируем Finished
                    SessionStatus.Active => SessionStatus.Finished,
                    _ => null,
                };

            if (newStatus.HasValue)
                await ApplyStatusAsync(sessionId, entry, newStatus.Value);

            // Цикл «до готово»: ход штатно завершился (exited) — решаем, продолжать ли.
            // В фоне, чтобы не блокировать read-loop адаптера пересозданием процесса.
            if (msg is ExitedMessage && entry.Info.WorkLoop is not null
                && entry.Info.Status is SessionStatus.Finished or SessionStatus.Active)
            {
                _ = Task.Run(async () =>
                {
                    try { await ContinueWorkLoopAsync(sessionId); }
                    catch (Exception ex)
                    {
                        Console.Error.WriteLine($"[SessionManager] Цикл «до готово» ({sessionId}): {ex.Message}");
                    }
                });
            }
        }

        await BroadcastAsync(sessionId, msg);

        if (entry is not null && OnSessionMessage is { } observers)
        {
            try { await observers(entry.Info, msg); }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[SessionManager] Ошибка наблюдателя сессии ({sessionId}): {ex.Message}");
            }
        }
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

    // Запись StoredMessage в историю сессии ВНЕ хода + broadcast (обобщение паттерна
    // PublishFalCostAsync): активная сессия → через Accumulator + SaveSnapshot;
    // неактивная → LoadAsync + append + SaveAsync под локом. Используется совещаниями.
    public async Task AppendStoredAsync(string sessionId, StoredMessage stored, ServerMessage broadcast)
    {
        if (!_sessions.TryGetValue(sessionId, out var entry)) return;

        if (entry.Accumulator is { } acc)
        {
            acc.Append(stored);
            try { await acc.SaveSnapshotAsync(_history); }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[SessionManager] Сохранение истории ({sessionId}) после внеходовой записи: {ex.Message}");
            }
        }
        else if (entry.Info.ClaudeSessionId is string key)
        {
            await _falPersistLock.WaitAsync();
            try
            {
                var stored0 = await _history.LoadAsync(key);
                stored0.Add(stored);
                await _history.SaveAsync(key, stored0);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[SessionManager] Прямая внеходовая запись истории ({sessionId}): {ex.Message}");
            }
            finally { _falPersistLock.Release(); }
        }

        await BroadcastSessionMessageAsync(sessionId, broadcast);
    }

    // Единая точка перехода статуса сессии: обновить Info → сохранить на диск → разослать клиентам
    private async Task ApplyStatusAsync(string sessionId, SessionEntry entry, SessionStatus status)
    {
        entry.Info.Status = status;
        entry.Info.UpdatedAt = DateTime.UtcNow;
        SaveSessions();
        await BroadcastStatusChangeAsync(sessionId, entry.Info,
            status, entry.Info.LastMessage, entry.Info.MessageCount);
    }

    // Текст последней реплики ассистента текущего хода (сообщения после baseline) —
    // ответ для синхронного ожидателя SendMessageAndWaitAsync
    private static string LastAssistantText(TurnAccumulator acc, int baseline) =>
        acc.GetAll().Skip(Math.Max(0, baseline)).OfType<StoredTextMessage>().LastOrDefault()?.Text ?? "";

    // Для fire-and-forget задач: ошибку логируем, а не теряем молча
    private static void FireAndForget(Task task, string context) =>
        task.ContinueWith(
            t => Console.Error.WriteLine($"[SessionManager] {context}: {t.Exception?.GetBaseException().Message}"),
            TaskContinuationOptions.OnlyOnFaulted);

    // Остановка всех живых адаптеров — вызывается при graceful shutdown приложения,
    // иначе после остановки сервера остаются зомби-процессы (claude + node MCP-серверов)
    public void KillAllProcesses()
    {
        var tasks = _sessions.Values
            .Select(e => e.Process)
            .OfType<ILlmSessionAdapter>()
            .Select(p => p.DisposeAsync().AsTask())
            .ToArray();
        if (tasks.Length == 0) return;
        try { Task.WaitAll(tasks, TimeSpan.FromSeconds(15)); }
        catch (AggregateException ex)
        {
            Console.Error.WriteLine($"[SessionManager] Остановка процессов при завершении: {ex.GetBaseException().Message}");
        }
    }

    private Task BroadcastAsync(string sessionId, ServerMessage msg) =>
        _hub.Clients.Group(sessionId).SendAsync("message", msg with { SessionId = sessionId });

    // Публичный broadcast внеходового сообщения сессии: session-группа + project_/user_-группа
    // (по образцу BroadcastStatusChangeAsync). Используется роутингом группового чата и совещаниями.
    public async Task BroadcastSessionMessageAsync(string sessionId, ServerMessage msg)
    {
        if (!_sessions.TryGetValue(sessionId, out var entry)) return;
        var wired = msg with { SessionId = sessionId };
        var tasks = new List<Task> { _hub.Clients.Group(sessionId).SendAsync("message", wired) };
        if (entry.Info.ProjectId is string pid)
            tasks.Add(_hub.Clients.Group("project_" + pid).SendAsync("message", wired));
        else if (entry.Info.OwnerId is string oid)
            tasks.Add(_hub.Clients.Group("user_" + oid).SendAsync("message", wired));
        await Task.WhenAll(tasks);
    }

    // Сессия, принадлежащая пользователю (и проектная, и чат вне проекта) — для
    // эндпоинтов, работающих с любым типом сессии (участники группы, совещания).
    public Session? GetOwned(string sessionId, string ownerId)
    {
        var s = GetById(sessionId);
        return s is not null && SessionOwner(s) == ownerId ? s : null;
    }

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

// Итог завершённого хода для синхронного ожидания (SendMessageAndWaitAsync):
// текст последней реплики ассистента, длительность и стоимость (если провайдер её отдал)
public record TurnResult(string Reply, long DurationMs, double? CostUsd);

// Результат отправки с ожиданием: Busy — сессия занята или ждёт человека (ход НЕ отправлен);
// Completed — ход завершился в срок; Running — ход продолжается (wait=none или истёк таймаут)
public abstract record SendAndWaitResult
{
    public sealed record Busy(SessionStatus CurrentStatus) : SendAndWaitResult;
    public sealed record Completed(TurnResult Result) : SendAndWaitResult;
    public sealed record Running : SendAndWaitResult;
}
