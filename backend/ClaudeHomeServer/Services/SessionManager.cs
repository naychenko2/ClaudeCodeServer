using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services.Llm;
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

    // Auto-recall заметок (фича notes-auto-recall): семантический индекс + гейт по флагу
    private readonly NotesKnowledgeService _notesKb;
    private readonly FeatureFlagService _flags;
    private readonly PersonaManager _personas;
    private readonly PersonaMemoryService _personaMemory;
    private readonly ILogger<SessionManager> _log;

    public SessionManager(ProjectManager projects, IHubContext<Hubs.SessionHub> hub,
        ChatHistoryService history, IConfiguration config, ILlmSessionAdapterFactory adapters,
        FalCostService falCost, UsageService usage,
        AppSettingsService appSettings, UserStore users, JwtService jwt,
        Microsoft.AspNetCore.Hosting.Server.IServer server,
        LlmProviderRegistry llmProviders,
        NotesKnowledgeService notesKb, FeatureFlagService flags, PersonaManager personas,
        PersonaMemoryService personaMemory, ILogger<SessionManager> log)
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
        _log = log;
        // Найденную стоимость fal.ai публикуем в SignalR + историю
        _falCost.OnCostResolved = PublishFalCostAsync;

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
    // записей (relevance × recency × typeWeight). Failsafe-таймаут; ошибки → null (ход без recall).
    private Func<string, Task<string?>> BuildPersonaRecallProvider(string ownerId, string personaId)
    {
        var topK = int.TryParse(_config["Persona:RecallTopK"], out var k) ? k : 5;
        var minScore = double.TryParse(_config["Persona:RecallMinScore"],
            System.Globalization.CultureInfo.InvariantCulture, out var s) ? s : 0.02;
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
            if (!_flags.IsEnabled(ownerId, FeatureFlagKeys.Notes) ||
                !_flags.IsEnabled(ownerId, FeatureFlagKeys.AiAssist)) return null;
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
        string? effort = null)
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

    // Создание чата от лица персоны. Маршрутизация по зоне:
    // проектная персона → сессия в её проекте (scope = проект); глобальная (или проект
    // недоступен) → чат вне проекта (scope = все данные владельца). Модель по умолчанию — из персоны.
    public async Task<Session> CreatePersonaChatAsync(string ownerId, string personaId,
        ClaudeMode mode, string? resumeSessionId = null, string? name = null)
    {
        var persona = _personas.Get(personaId, ownerId)
            ?? throw new KeyNotFoundException($"Персона не найдена: {personaId}");

        if (persona.Scope == PersonaScope.Project && !string.IsNullOrEmpty(persona.ProjectId)
            && _projects.GetById(persona.ProjectId) is { } project && project.OwnerId == ownerId)
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
        };
        await StartNewSessionAsync(session, rootPath, rawSystemPrompt: null, permissionRules: null);
        return session;
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

    // Персона-слой сессии (промпт характера + контекст памяти + auto-recall + возможности).
    // Строится одинаково при первом старте и при восстановлении процесса. Пусто, если персоны нет.
    private (string? Prompt, MemoryMcpContext? Memory, Func<string, Task<string?>>? Recall, List<string>? Tools)
        BuildPersonaLayer(Session session, string? ownerId)
    {
        if (session.PersonaId is null || ownerId is null) return (null, null, null, null);
        var persona = _personas.Get(session.PersonaId, ownerId);
        if (persona is null) return (null, null, null, null);
        var prompt = BuildPersonaPrompt(persona);
        // Долгая память — только если включена у персоны и владелец имеет доступ к фиче
        if (persona.MemoryEnabled && _flags.IsEnabled(ownerId, FeatureFlagKeys.Personas))
            return (prompt, BuildMemoryContext(ownerId, persona.Id), BuildPersonaRecallProvider(ownerId, persona.Id), persona.Tools);
        return (prompt, null, null, persona.Tools);
    }

    // Возможность персоны (tasks/notes/web): null-список — без ограничений (как раньше)
    private static bool PersonaToolEnabled(List<string>? tools, string key) =>
        tools is null || tools.Contains(key, StringComparer.OrdinalIgnoreCase);

    // Ограничение «web»: у персоны с выключенным веб-поиском запрещаем встроенные
    // тулы CLI (не MCP) поверх конфига Claude:DisallowedTools
    private static IReadOnlyList<string>? BuildExtraDisallowed(List<string>? tools) =>
        tools is not null && !PersonaToolEnabled(tools, "web")
            ? new[] { "WebSearch", "WebFetch" }
            : null;

    // Назначить/сменить собеседника чату ДО первого хода (единый селектор в пустом чате):
    // персону (personaId) ИЛИ стандартного .md-агента Claude (agentName) — взаимоисключающе.
    // Оба пустые = снять собеседника. Модель/усилие подтягиваются из персоны.
    // Начатую сессию не трогаем (клиент делает форк).
    public Session? SetPersona(string sessionId, string ownerId, string? personaId, string? agentName = null)
    {
        if (!_sessions.TryGetValue(sessionId, out var entry)) return null;
        if (SessionOwner(entry.Info) != ownerId) return null;
        if (entry.Info.ClaudeSessionId is not null)
            throw new InvalidOperationException("Нельзя сменить собеседника у начатой сессии — создайте новый чат");

        Persona? persona = null;
        if (!string.IsNullOrEmpty(personaId))
        {
            persona = _personas.Get(personaId, ownerId)
                ?? throw new KeyNotFoundException("Персона не найдена");
        }

        entry.Info.PersonaId = persona?.Id;
        // .md-агент и персона взаимоисключающие: назначение одного сбрасывает другого
        entry.Info.AgentName = persona is null && !string.IsNullOrWhiteSpace(agentName)
            ? agentName.Trim()
            : null;
        if (persona is not null)
        {
            entry.Info.Model = persona.Model;
            entry.Info.Effort = persona.Effort;
        }
        entry.Info.UpdatedAt = DateTime.UtcNow;

        // Ходов не было — пересоздаём адаптер с новым контекстом при следующем сообщении
        if (entry.Process is { } old)
        {
            entry.Process = null;
            FireAndForget(old.DisposeAsync().AsTask(),
                $"остановка адаптера при смене собеседника ({sessionId})");
        }
        SaveSessions();
        return entry.Info;
    }

    // Системный промпт персоны: имя + роль/описание + характер (тело systemPrompt).
    private static string BuildPersonaPrompt(Persona persona)
    {
        var sb = new System.Text.StringBuilder();
        // Роль — главная («Ты — Дизайнер по имени Светлана»); без роли — просто имя
        if (!string.IsNullOrWhiteSpace(persona.Role))
            sb.Append($"Ты — {persona.Role.Trim()} по имени {persona.Name}");
        else
            sb.Append($"Ты — {persona.Name}");
        if (!string.IsNullOrWhiteSpace(persona.Description))
            sb.Append($", {persona.Description.Trim()}");
        sb.Append(". Отвечай и действуй от своего лица, в своём характере, оставаясь собой на протяжении всего разговора.");
        if (!string.IsNullOrWhiteSpace(persona.SystemPrompt))
            sb.Append("\n\n").Append(persona.SystemPrompt.Trim());
        return sb.ToString();
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

        var adapter = _adapters.Create(session, new LlmSessionContext(rootPath,
            msg => OnMessageAsync(session.Id, accumulator, msg),
            rawSystemPrompt, permissionRules,
            PersonaToolEnabled(persona.Tools, "tasks") ? BuildTasksContext(ownerId, session.ProjectId) : null,
            PersonaToolEnabled(persona.Tools, "notes") ? BuildNotesContext(ownerId, session.ProjectId) : null,
            BuildRecallProvider(ownerId),
            persona.Prompt,
            persona.Memory,
            persona.Recall,
            BuildExtraDisallowed(persona.Tools)));
        entry.Process = adapter;

        await adapter.StartAsync();
        SaveSessions();
    }

    public async Task SendMessageAsync(string sessionId, string text, IReadOnlyList<string> attachedPaths, string? mode = null)
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

        await EnsureProcessAsync(sessionId, entry);

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

        entry.Accumulator?.OnUserMessage(text, attachedPaths);
        await entry.Process!.SendMessageAsync(text, attachedPaths);
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

        await ApplyStatusAsync(sessionId, entry, SessionStatus.Working);

        await entry.Process!.CompactAsync();
    }

    // После перезапуска сервера Process может быть null — восстанавливаем сессию
    private async Task EnsureProcessAsync(string sessionId, SessionEntry entry)
    {
        if (entry.Process is not null) return;

        var existingHistory = entry.Info.ClaudeSessionId != null
            ? await _history.LoadAsync(entry.Info.ClaudeSessionId)
            : [];
        var accumulator = new TurnAccumulator(existingHistory, entry.Info.ClaudeSessionId);
        entry.Accumulator = accumulator;

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
            context = new LlmSessionContext(rootPath,
                msg => OnMessageAsync(sessionId, accumulator, msg),
                RawSystemPrompt: null, PermissionRules: null,
                PersonaToolEnabled(persona.Tools, "tasks") ? BuildTasksContext(entry.Info.OwnerId, null) : null,
                PersonaToolEnabled(persona.Tools, "notes") ? BuildNotesContext(entry.Info.OwnerId, null) : null,
                BuildRecallProvider(entry.Info.OwnerId),
                persona.Prompt, persona.Memory, persona.Recall,
                BuildExtraDisallowed(persona.Tools));
        }
        else
        {
            var project = _projects.GetById(entry.Info.ProjectId)
                ?? throw new InvalidOperationException("Проект не найден");
            var persona = BuildPersonaLayer(entry.Info, project.OwnerId);
            context = new LlmSessionContext(project.RootPath,
                msg => OnMessageAsync(sessionId, accumulator, msg),
                project.SystemPrompt,
                () => _projects.GetById(entry.Info.ProjectId!)?.PermissionRules ?? (IReadOnlyList<PermissionRule>)Array.Empty<PermissionRule>(),
                PersonaToolEnabled(persona.Tools, "tasks") ? BuildTasksContext(project.OwnerId, project.Id) : null,
                PersonaToolEnabled(persona.Tools, "notes") ? BuildNotesContext(project.OwnerId, project.Id) : null,
                BuildRecallProvider(project.OwnerId),
                persona.Prompt, persona.Memory, persona.Recall,
                BuildExtraDisallowed(persona.Tools));
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
                case ResultMessage m:       await acc.OnResultAsync(m.Subtype, m.DurationMs, m.NumTurns, m.Usage, m.TotalCostUsd, m.ApiErrorStatus, m.PermissionDenials, _history); break;
                case RateLimitMessage m:    _usage.Record(m.LimitType, m.Utilization, m.Status, m.IsUsingOverage, m.ResetsAt, m.OverageStatus, m.OverageResetsAt); break;
                case ErrorMessage m:        await acc.OnErrorAsync(m.Text, _history); break;
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[SessionManager] Ошибка аккумулятора ({sessionId}): {ex.Message}");
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

    // Единая точка перехода статуса сессии: обновить Info → сохранить на диск → разослать клиентам
    private async Task ApplyStatusAsync(string sessionId, SessionEntry entry, SessionStatus status)
    {
        entry.Info.Status = status;
        entry.Info.UpdatedAt = DateTime.UtcNow;
        SaveSessions();
        await BroadcastStatusChangeAsync(sessionId, entry.Info,
            status, entry.Info.LastMessage, entry.Info.MessageCount);
    }

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
