using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;

namespace ClaudeHomeServer.Services.Llm.Claude;

public class ClaudeSession : ILlmSessionAdapter
{
    public Session Info { get; }

    // По модели: сторонний CLI-провайдер отдаёт свои возможности (SupportsImages и т.п.)
    public LlmCapabilities Capabilities =>
        _providers?.CapabilitiesFor(Info.Model) ?? LlmCapabilitiesCatalog.Claude;

    private readonly string _rootPath;
    private readonly Func<ServerMessage, Task> _onMessage;
    // Словари ниже — Concurrent: их мутируют и памп stdout, и SignalR-вызовы
    // (RespondPermission/AnswerQuestion/RespondPlan/Interrupt) параллельно
    private readonly ConcurrentDictionary<string, TaskCompletionSource<string>> _permissionWaiters = new();
    // Инструменты, для которых пользователь выбрал «всегда разрешать» в этой сессии (значение не используется)
    private readonly ConcurrentDictionary<string, byte> _autoAllowTools = new();
    // tool_use_id → request_id вопросов AskUserQuestion (приходят как control_request can_use_tool, ждут control_response)
    private readonly ConcurrentDictionary<string, string> _pendingQuestions = new();
    // request_id → исходный input ожидающего согласования ExitPlanMode (режим «План»)
    private readonly ConcurrentDictionary<string, object> _pendingPlans = new();
    // Гарантированное исполнение одобренного плана:
    // после approve ждём реализацию; если ход завершится без правок — дошлём команду.
    private volatile bool _awaitPlanExecution;
    private volatile bool _sawToolSinceApprove;
    // Следующий ход запустить без --permission-mode plan (исполнение одобренного плана)
    private volatile bool _forceNonPlanNextTurn;
    // Глубина делегирования текущего хода: > 0 — ход инициирован агентом из другой сессии
    // (chats_send). Выставляется в начале RunTurnAsync и сбрасывается после хода;
    // при глубине >= 1 BuildTurnMcpConfig урезает инструменты делегирования (анти-рекурсия)
    private volatile int _currentTurnAgentDepth;
    // Реакционный авто-ход постановщика на доклад делегированной задачи (TaskExecutionService.
    // ReportToDelegatorAsync) — отдельный от agentDepth флаг: ход обычного пользовательского
    // чата (agentDepth=0), но tasks_run_executor всё равно должен быть недоступен, иначе A может
    // сам себе запустить только что созданную задачу → новый доклад → новая реакция →
    // бесконечный платный цикл A↔B. Выставляется/сбрасывается вместе с _currentTurnAgentDepth.
    private volatile bool _currentTurnSuppressTasksExecute;
    // Стриминг tool_use: индекс content-блока → (id инструмента, накопленный partial_json).
    // Concurrent — для видимости между потоками пампа разных ходов
    private readonly ConcurrentDictionary<int, (string Id, System.Text.StringBuilder Sb)> _toolStream = new();
    // Контекст последнего запроса к API (input + cache_read + cache_creation из usage
    // последнего assistant-сообщения ОСНОВНОГО агента) — оценка заполнения окна для клиента.
    // Обновляется на каждом шаге tool-лупа, уезжает в ResultMessage.ContextTokens.
    private volatile int _lastContextTokens;
    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _turnLock = new(1, 1);
    // Сериализует записи в stdin процесса: control_response шлются из SignalR-потоков
    // параллельно с пампом — без лока JSON-строки могут перемешаться
    private readonly SemaphoreSlim _stdinLock = new(1, 1);
    private Process? _currentProcess;

    // Ватчеры фоновых Workflow (по одному на каждый запущенный workflow в сессии)
    private readonly List<WorkflowWatcher> _workflowWatchers = [];
    // Полный поток inline-сабагентов из их транскриптов (CLI шлёт в stdout только tool_use);
    // создаётся на system/init каждого хода, диспозится по завершении процесса
    private SubagentStreamWatcher? _subagentWatcher;

    // Максимальная тишина stdout активного хода: при живой работе (генерация, инструмент,
    // субагент, компакция, ожидание пользователя) CLI шлёт события регулярно; полное молчание
    // 60 мин — крайняя защита от вечно висящего процесса (напр. провайдер оборвал стрим, а CLI
    // не завершился). Реальный обрыв, при котором CLI сам падает/выходит, ловится раньше в цикле
    // чтения (result/EOF/исключение). Не занижаем: короткий порог ложно рубил бы долгие
    // инструменты, субагентов OMO/workflow, компакцию и медленные ответы провайдера.
    private static readonly TimeSpan IdleTimeout = TimeSpan.FromMinutes(60);

    // Грейс после result: штатно CLI выходит сам, но плагинные хуки/MCP-мосты (наблюдалось
    // с oh-my-claudecode) могут держать процесс живым бесконечно — тогда завершаем ход сами,
    // не дожидаясь часового watchdog.
    private static readonly TimeSpan ResultExitGrace = TimeSpan.FromSeconds(15);

    // Расширенный грейс для прогонов с --prompt-suggestions: CLI генерит подсказку ПОСЛЕ
    // result (замер: ~9с на лёгком ходе, на тяжёлых дольше) — 15с обрывали её на середине.
    // Цена расширения — лишнее ожидание только в аварийном случае (CLI сам не вышел).
    private static readonly TimeSpan PromptSuggestionExitGrace = TimeSpan.FromSeconds(45);

    // Потолок доживания процесса с работающими фоновыми агентами после конца хода.
    // Агенты (Agent run_in_background, Workflow) живут ВНУТРИ процесса CLI: убить его
    // по грейсу — значит убить их на середине (наблюдалось на проде: task-notification
    // «status=stopped» у всех агентов длиннее 15 секунд). Значение — из Claude:BgLingerMinutes.
    // Потолок доживания процесса с фоновыми агентами после конца хода. Инстансное поле
    // (из конфига через фабрику) — раньше был public static settable, мутируемый как скрытый
    // сайд-эффект конструктора фабрики (глобальное общее состояние на весь процесс).
    private readonly TimeSpan _bgLingerTimeout;

    // Процессный прогон: один запуск claude CLI. Может пережить ход — пока в нём доживают
    // фоновые агенты, процесс не убиваем, а следующий совместимый ход отдаём ему же в stdin
    // (stream-json это штатно поддерживает). Поля мутирует поток чтения stdout (reader);
    // исключения помечены у полей.
    private sealed class CliRun
    {
        public required Process Process { get; init; }
        // Сигнатура окружения запуска (модель/режим/env/набор MCP/слой персоны) — следующий
        // ход можно отдать живому процессу только при полном совпадении (см. BuildLaunchSignature)
        public required string Signature { get; init; }
        public string? TurnMcpPath { get; init; }
        // turnId запуска — по нему pid-файл прогона в песочнице (Kill контейнерного pgid)
        public string? LaunchTurnId { get; init; }
        public Task? ReaderTask { get; set; }
        public Task<string>? StderrTask { get; set; }
        // Ход завершён (result без parent_tool_use_id получен); между ходами true.
        // Сбрасывает поток нового хода в TrySubmitTurn (под _stdinLock)
        public volatile bool TurnDone;
        // Резолвится reader'ом на result текущего хода (или финализацией — процесс умер)
        public TaskCompletionSource TurnTcs { get; set; } = NewTcs();
        // Живые фоновые задачи прогона: agentId/runId из tool_result запуска → toolUseId
        // его карточки (для события bg_agent_done при завершении);
        // читается и потоками ходов — доступ под lock (PendingBg)
        public readonly Dictionary<string, string> PendingBg = [];
        // Фоновый запуск замечен, но id не распарсился — точный учёт невозможен,
        // доживание ограничено только потолком BgLingerTimeout
        public volatile bool PendingBgUnknown;
        // toolUseId фоновых запусков без распарсенного id — их карточки закрываем
        // только при финализации прогона (доступ под lock (PendingBg))
        public readonly HashSet<string> UnknownBgToolUses = [];
        // toolUseId вызовов Agent/Task c run_in_background и Workflow — ждём их tool_result,
        // чтобы достать id фоновой задачи
        public readonly HashSet<string> BgLaunchCandidates = [];
        // Между ходами CLI ведёт собственные ходы-продолжения (ответы на task-notification) —
        // контент после TurnDone означает, что продолжение началось. Его result не должен
        // завершать пользовательский ход (см. SkipResults)
        public volatile bool ContinuationActive;
        // Сколько ближайших result'ов принадлежит продолжениям, начатым ДО отправки
        // текущего пользовательского хода (инкремент в TrySubmitTurn под _stdinLock,
        // декремент — поток reader'а)
        public int SkipResults;
        public volatile bool StdinClosed;
        // Прогон убит ради несовместимого нового хода: ExitedMessage не слать —
        // статусом сессии владеет уже новый ход
        public volatile bool SuppressExited;
        // Прогон запущен с --prompt-suggestions: после result ждём выхода CLI дольше
        // (PromptSuggestionExitGrace) — подсказка генерится и приходит после result
        public bool PromptSuggestionsActive { get; init; }

        public bool HasPendingBg
        {
            get { lock (PendingBg) return PendingBg.Count > 0 || PendingBgUnknown; }
        }

        public static TaskCompletionSource NewTcs() =>
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    // Текущий прогон; присваивает поток хода (под _turnLock), обнуляет финализация reader'а
    private CliRun? _run;

    // Хвостовой ридер главного транскрипта: завершения фоновых задач (<task-notification>)
    // CLI пишет в транскрипт, в stdout завершённого хода их может не быть (проверено live) —
    // без ридера pending прогона не опустел бы и процесс висел бы до потолка BgLingerTimeout
    private MainTranscriptTailer? _transcriptTailer;

    // Коннекторы аккаунта claude.ai (Calendar, Drive, Gamma, Miro и др.) вливаются в каждую
    // сессию автоматически помимо --mcp-config — их нельзя убрать через конфиг. Блокируем
    // через --disallowedTools; список задаётся из конфига (Claude:DisallowedTools).
    private readonly string[] _disallowedTools;

    // Встроенные Task-инструменты Claude Code (Tasks-фича, синхронизация с claude.ai) —
    // дублируют наш MCP tasks-server. Пока tasks-server подключён, блокируем их через
    // --disallowedTools (см. сборку _disallowedTools в конструкторе), чтобы модель звала
    // mcp__tasks__*, а не пустой встроенный трекер. ВНИМАНИЕ: «Task» (без суффикса) —
    // это тул ЗАПУСКА СУБАГЕНТА (делегирование), его НЕ трогаем; только трекерные
    // TaskGet/TaskList/TaskCreate/TaskUpdate.
    //
    // ВНИМАНИЕ: раньше тут стояло «несуществующие claude молча проигнорирует» и список
    // содержал TaskComplete/TaskDelete/TaskSearch. Это допущение сломалось: CLI 2.1.x
    // ВАЛИДИРУЕТ имена в deny-правилах. В интерактивном режиме он ругается в stderr на
    // каждый ход, а в `--print` (one-shot) вообще падает с кодом 1 — так у нас разом легли
    // все ИИ-фичи из-за мёртвого MultiEdit. Мёртвые имена сюда не добавлять: список сверять
    // с реальным набором инструментов CLI при его обновлении.
    private static readonly string[] BuiltInTaskTools =
        ["TaskGet", "TaskList", "TaskCreate", "TaskUpdate"];

    // Свои MCP-серверы (mcp/*-server, mcp-dify — код этого репозитория, собираются в
    // BuildTurnMcpConfig): работа с данными самого пользователя внутри системы, не внешнее
    // действие с побочным эффектом наружу (в отличие от Google Drive/Gamma/Miro/figma и
    // т.п. сторонних коннекторов). Разрешаем их автоматически, без карточки пользователю —
    // иначе персоны и автоматизации вязнут в перманентных permission-запросах на каждый
    // созданный чат/процесс claude, хотя доступ уже ограничен на уровне Persona.Tools/
    // ExtraDisallowedTools и project deny-правил (проверяются раньше, см. DecidePermissionAsync).
    // mcp__pmem_ — выделенные memory-серверы персон-консультантов (pmem_<handle>, файловые
    // сабагенты): их permission-запросы падают в фоновом контексте сабагента, где отвечать
    // некому — авторазрешаем, как и остальные свои серверы (доступ ограничен allow-list агента).
    private static readonly string[] BuiltInMcpServerPrefixes =
        ["mcp__tasks__", "mcp__notes__", "mcp__memory__", "mcp__personas__", "mcp__wsp__", "mcp__notifications__", "mcp__widgets__", "mcp__dify__", "mcp__pmem_"];

    // Отслеживание изменений файлов на время хода
    private readonly TurnFileWatcher _fileWatcher;

    private readonly string? _rawSystemPrompt;
    private readonly string? _mcpConfigPath;
    // Ключ HTTP MCP-сервера fal-ai (Fal:McpApiKey) — сервер инжектится в конфиг хода
    // из appsettings, а не хардкодится в .mcp.json (секрет вне git); пусто — без fal-ai
    private readonly string? _falMcpApiKey;
    private readonly SkillsService? _skills;
    private readonly WorkspaceKnowledgeStore? _wkStore;
    // Провайдер правил разрешений проекта — резолвим каждый запрос (правила могут меняться)
    private readonly Func<IReadOnlyList<PermissionRule>>? _permissionRules;
    private readonly TasksMcpContext? _tasksMcp;
    private readonly NotesMcpContext? _notesMcp;
    // Auto-recall заметок: по тексту хода возвращает блок для системного промпта + манифест (F3)
    private readonly Func<string, Task<RecallBlock?>>? _recallProvider;
    // Провайдер системного промпта персоны — вызывается на каждый ход
    // (свежие контракт/модель/PersonaSwitched без пересоздания адаптера)
    private readonly Func<string?>? _personaPromptProvider;
    // MCP-сервер долгой памяти персоны + auto-recall её памяти (текст промпта + манифест F3)
    private readonly MemoryMcpContext? _memoryMcp;
    private readonly Func<string, Task<RecallBlock?>>? _personaRecallProvider;
    // Блок «Привязанные знания и правила» персоны (флаг persona-bindings)
    private readonly Func<string, Task<string?>>? _bindingsProvider;
    // MCP-сервер персон: CRUD из любого чата + @упоминания/persona_ask
    private readonly PersonasMcpContext? _personasMcp;
    // MCP-сервер рабочего пространства: проекты/файлы/знания/поиск владельца
    private readonly WorkspaceMcpContext? _workspaceMcp;
    // MCP-сервер уведомлений: создание уведомлений из Claude/агентов
    private readonly NotificationsMcpContext? _notificationsMcp;
    // MCP-серверы внешних модулей из реестра (контракт §6): аддитивно к встроенным
    private readonly ModulesMcpContext? _modulesMcp;
    // MCP-сервер виджетов чата (widget_show): null — сессия без владельца
    private readonly WidgetsMcpContext? _widgetsMcp;
    // Файловые сабагенты-персоны: план хода — папки --add-dir
    // + pmem-серверы памяти консультантов; вычисляется на каждый ход
    private readonly Func<PersonaAgentsContext?>? _personaAgentsProvider;
    // Реестр CLI-провайдеров: env-оверрайды процесса (ANTHROPIC_BASE_URL и др.)
    // для сторонних моделей; null — всегда родной Claude
    private readonly LlmProviderRegistry? _providers;
    private readonly ClaudeSubscriptionPool? _subscriptionPool;
    // Драйвер среды исполнения владельца (local / docker-песочница)
    private readonly Execution.IProcessLauncher _launcher;
    // Метка текущего хода — по ней драйвер песочницы добивает процесс внутри контейнера
    private string? _currentTurnId;

    public ClaudeSession(Session info, LlmSessionContext context,
        string? mcpConfigPath = null, SkillsService? skills = null,
        WorkspaceKnowledgeStore? workspaceStore = null, string[]? disallowedTools = null,
        LlmProviderRegistry? providers = null,
        ClaudeSubscriptionPool? subscriptionPool = null,
        FileWatcherOptions? fileWatcherOptions = null,
        TimeSpan? bgLingerTimeout = null,
        string? falMcpApiKey = null)
    {
        _providers = providers;
        _subscriptionPool = subscriptionPool;
        _bgLingerTimeout = bgLingerTimeout ?? TimeSpan.FromMinutes(30);
        Info = info;
        _rootPath = context.RootPath;
        _onMessage = context.OnMessage;
        _mcpConfigPath = mcpConfigPath;
        _falMcpApiKey = falMcpApiKey;
        _rawSystemPrompt = context.RawSystemPrompt;
        _skills = skills;
        _wkStore = workspaceStore;
        _permissionRules = context.PermissionRules;
        _tasksMcp = context.TasksMcp;
        _notesMcp = context.NotesMcp;
        _recallProvider = context.RecallProvider;
        _personaPromptProvider = context.PersonaPromptProvider;
        _memoryMcp = context.MemoryMcp;
        _personaRecallProvider = context.PersonaRecallProvider;
        _bindingsProvider = context.BindingsProvider;
        _personasMcp = context.PersonasMcp;
        _workspaceMcp = context.WorkspaceMcp;
        _notificationsMcp = context.NotificationsMcp;
        _modulesMcp = context.ModulesMcp;
        _widgetsMcp = context.WidgetsMcp;
        _personaAgentsProvider = context.PersonaAgentsProvider;
        _launcher = context.Launcher ?? Execution.LocalProcessRunner.Instance;
        // Запреты конфига + ограничения возможностей персоны (ExtraDisallowedTools)
        _disallowedTools = context.ExtraDisallowedTools is { Count: > 0 } extra
            ? [.. (disallowedTools ?? []), .. extra]
            : disallowedTools ?? [];
        // Пока подключён наш MCP tasks-server, запрещаем встроенные Task-инструменты
        // Claude Code (синхронизация с claude.ai — там пусто): они дублируют mcp__tasks__*
        // и путают модель (особенно haiku зовёт TaskGet/TaskList вместо tasks_get/tasks_list,
        // получает «No tasks» и бросает задачу). Без задач в сессии — не трогаем.
        if (context.TasksMcp is not null)
            _disallowedTools = [.. _disallowedTools, .. BuiltInTaskTools];
        _fileWatcher = new TurnFileWatcher(_rootPath, _onMessage, fileWatcherOptions);
    }

    // Гейт TASKS_EXECUTE (вынесен ради юнит-тестов): доступен только на пользовательском
    // ходу (не агентном), при неисчерпанной глубине делегирования исполнителей и когда
    // ход явно не подавил его (реакция постановщика на доклад делегированной задачи —
    // TaskExecutionService.ReportToDelegatorAsync — иначе A мог бы сам себе запустить
    // только что созданную задачу и зациклить пинг-понг A↔B).
    internal static bool ResolveTasksExecuteEnabled(int currentTurnAgentDepth, int taskDelegationDepth, bool suppressTasksExecute) =>
        currentTurnAgentDepth < 1 && taskDelegationDepth < 3 && !suppressTasksExecute;

    // Объединённый MCP-конфиг хода: серверы из базового конфига (Dify с инжекцией
    // dataset id) + tasks-server с контекстом сессии; для сессий сторонних провайдеров —
    // ещё и user-scope серверы из ~/.claude.json (fal-ai и др.: изолированный
    // CLAUDE_CONFIG_DIR их не видит). null → базовый конфиг как есть.
    // Возвращает путь temp-конфига и отсортированный набор ключей серверов — ключи входят
    // в сигнатуру прогона (сам путь и содержимое меняются каждый ход: новый файл, свежий JWT)
    private (string? Path, string ServerKeys) BuildTurnMcpConfig(string? datasetId, PersonaAgentsContext? personaAgents = null, string turnText = "")
    {
        var tasksServerPath = _tasksMcp is not null ? MapMcpPath(TasksServerLocator.FindTasksServerPath()) : null;
        var hasTasks = tasksServerPath is not null;
        var notesServerPath = _notesMcp is not null ? MapMcpPath(NotesServerLocator.FindNotesServerPath()) : null;
        var hasNotes = notesServerPath is not null;
        var hasConsultants = personaAgents is { MemoryServers.Count: > 0 };
        var memoryServerPath = _memoryMcp is not null || hasConsultants
            ? MapMcpPath(MemoryServerLocator.FindMemoryServerPath()) : null;
        var hasMemory = _memoryMcp is not null && memoryServerPath is not null;
        var personasServerPath = _personasMcp is not null ? MapMcpPath(PersonasServerLocator.FindPersonasServerPath()) : null;
        var hasPersonas = personasServerPath is not null;
        var workspaceServerPath = _workspaceMcp is not null ? MapMcpPath(WorkspaceServerLocator.FindWorkspaceServerPath()) : null;
        var hasWorkspace = workspaceServerPath is not null;
        var notificationsServerPath = _notificationsMcp is not null ? MapMcpPath(NotificationsServerLocator.FindNotificationsServerPath()) : null;
        var hasNotifications = notificationsServerPath is not null;
        var widgetsServerPath = _widgetsMcp is not null ? MapMcpPath(WidgetsServerLocator.FindWidgetsServerPath()) : null;
        var hasWidgets = widgetsServerPath is not null;
        var hasDataset = !string.IsNullOrEmpty(datasetId);
        var hasModules = _modulesMcp is { Servers.Count: > 0 };
        var hasFalAi = !string.IsNullOrEmpty(_falMcpApiKey);
        var userServers = LoadUserScopeMcpServers();
        if (!hasTasks && !hasNotes && !hasMemory && !hasPersonas && !hasWorkspace && !hasNotifications
            && !hasWidgets && !hasDataset && !hasModules && !hasFalAi && userServers is null
            && !(hasConsultants && memoryServerPath is not null)) return (null, "");

        try
        {
            var servers = new System.Text.Json.Nodes.JsonObject();
            // Отпечаток СОСТАВА инструментов сервера (ключ сервера → суффикс сигнатуры).
            // Per-turn env, меняющие набор tools (PERSONAS_WRITE/MENTIONS, WORKSPACE_WRITE,
            // TASKS_EXECUTE, …), не попадают в токены/URL (те исключены из сигнатуры как
            // изменчивые). Без этого отпечатка смена, напр., PERSONAS_WRITE 0→1 не меняла
            // сигнатуру запуска — ход уходил в живой процесс доживания, personas_create там
            // так и не поднимался («No such tool available»). Копится параллельно servers,
            // вклеивается в ServerKeys ниже.
            var shapes = new Dictionary<string, string>(StringComparer.Ordinal);

            // User-scope серверы (только у сторонних провайдеров) — первыми:
            // одноимённые из базового конфига ниже их перекроют
            if (userServers is not null)
                foreach (var (key, val) in userServers)
                    if (val?.DeepClone() is { } clone && AdaptServerForRuntime(key, clone))
                        servers[key] = clone;

            // Серверы из базового конфига (+ dataset id в env Dify)
            if (!string.IsNullOrEmpty(_mcpConfigPath) && File.Exists(_mcpConfigPath))
            {
                var baseDoc = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(_mcpConfigPath));
                if (baseDoc?["mcpServers"] is System.Text.Json.Nodes.JsonObject baseServers)
                {
                    foreach (var (key, val) in baseServers)
                    {
                        var clone = val?.DeepClone();
                        if (clone is null || !AdaptServerForRuntime(key, clone)) continue;
                        if (key == "dify" && hasDataset && clone["env"] is { } env)
                        {
                            env["DIFY_DEFAULT_DATASET_ID"] = datasetId;
                            env["DIFY_SEARCH_ONLY"] = "true";
                        }
                        servers[key] = clone;
                    }
                }
            }

            // Продуктовый HTTP-сервер fal-ai (генерация изображений/видео): инжектится из
            // Fal:McpApiKey одинаково для хоста и песочницы (паритет сред). Ставим ПОСЛЕ
            // user-scope и базового конфига — одноимённый сервер оттуда перекрывается,
            // ключ не задваивается.
            if (hasFalAi)
            {
                servers["fal-ai"] = new System.Text.Json.Nodes.JsonObject
                {
                    ["type"] = "http",
                    ["url"] = "https://mcp.fal.ai/mcp",
                    ["headers"] = new System.Text.Json.Nodes.JsonObject
                    {
                        ["Authorization"] = $"Bearer {_falMcpApiKey}",
                    },
                };
            }

            if (hasTasks)
            {
                // tasks_run_executor порождает новую сессию Claude — на агентном ходу
                // (chats_send из другой сессии) не даём, та же анти-рекурсия, что у chats.
                // Плюс гард глубины делегирования: чат-исполнитель задачи глубины >= 3 не
                // запускает нового исполнителя — обрыв рекурсивного размножения (Info.TaskId →
                // TaskItem.DelegationDepth, см. Session.TaskDelegationDepth).
                var tasksExecute = ResolveTasksExecuteEnabled(_currentTurnAgentDepth, Info.TaskDelegationDepth, _currentTurnSuppressTasksExecute) ? "1" : "0";
                // Кросс-проектные ProjectTasks-привязки текущей персоны: доступ к задачам
                // ДРУГИХ проектов владельца (extraProjectIdsCsv), подмножество только для
                // чтения — extraReadOnlyCsv (create/update/delete там запрещены)
                var extraProjectIdsCsv = _tasksMcp.ExtraProjectIds is { Count: > 0 } extraIds
                    ? string.Join(",", extraIds) : "";
                var extraReadOnlyCsv = _tasksMcp.ExtraProjectIdsReadOnly is { Count: > 0 } extraRo
                    ? string.Join(",", extraRo) : "";
                servers["tasks"] = new System.Text.Json.Nodes.JsonObject
                {
                    ["command"] = "node",
                    ["args"] = new System.Text.Json.Nodes.JsonArray { tasksServerPath! },
                    ["env"] = new System.Text.Json.Nodes.JsonObject
                    {
                        ["TASKS_API_URL"] = _tasksMcp!.ApiUrl,
                        ["TASKS_API_TOKEN"] = _tasksMcp.Token,
                        ["TASKS_PROJECT_ID"] = _tasksMcp.ProjectId ?? "",
                        // Происхождение создаваемых задач: чат-источник и персона-постановщик.
                        // Берём из Info на каждый ход (как NOTES_SESSION_ID) — PersonaId сессии
                        // меняется по ходу разговора (SetPersona, смена спикера в группе)
                        ["TASKS_SESSION_ID"] = Info.Id,
                        ["TASKS_SELF_PERSONA_ID"] = Info.PersonaId ?? "",
                        ["TASKS_EXECUTE"] = tasksExecute,
                        ["TASKS_EXTRA_PROJECT_IDS"] = extraProjectIdsCsv,
                        ["TASKS_EXTRA_PROJECT_IDS_READONLY"] = extraReadOnlyCsv,
                    },
                };
                // Кросс-проектные скоупы влияют на видимость/доступность задач других проектов —
                // смена привязок должна пробить доживание живого процесса (как e{tasksExecute})
                shapes["tasks"] = $"e{tasksExecute}:{extraProjectIdsCsv}:{extraReadOnlyCsv}";
            }

            if (hasNotes)
            {
                servers["notes"] = new System.Text.Json.Nodes.JsonObject
                {
                    ["command"] = "node",
                    ["args"] = new System.Text.Json.Nodes.JsonArray { notesServerPath! },
                    ["env"] = new System.Text.Json.Nodes.JsonObject
                    {
                        ["NOTES_API_URL"] = _notesMcp!.ApiUrl,
                        ["NOTES_API_TOKEN"] = _notesMcp.Token,
                        ["NOTES_PROJECT_ID"] = _notesMcp.ProjectId ?? "",
                        ["NOTES_SESSION_ID"] = Info.Id,
                    },
                };
            }

            if (hasWidgets)
            {
                // Сервер виджетов: без env (API ему не нужен). alwaysLoad — единственный
                // крохотный инструмент; без него первый вызов в ходе падает «No such tool
                // available» (claude-code#19282), а ретраить показ виджета модели не свойственно.
                servers["widgets"] = new System.Text.Json.Nodes.JsonObject
                {
                    ["command"] = "node",
                    ["args"] = new System.Text.Json.Nodes.JsonArray { widgetsServerPath! },
                    ["alwaysLoad"] = true,
                };
            }

            if (hasMemory)
            {
                servers["memory"] = new System.Text.Json.Nodes.JsonObject
                {
                    ["command"] = "node",
                    ["args"] = new System.Text.Json.Nodes.JsonArray { memoryServerPath! },
                    // MCP подключается лениво (claude-code#19282): без alwaysLoad первый вызов
                    // инструмента в ходе падает «No such tool available». Память/персон модель
                    // зовёт первым же действием — ждём подключения до старта хода.
                    ["alwaysLoad"] = true,
                    ["env"] = new System.Text.Json.Nodes.JsonObject
                    {
                        ["MEMORY_API_URL"] = _memoryMcp!.ApiUrl,
                        ["MEMORY_API_TOKEN"] = _memoryMcp.Token,
                        ["MEMORY_PERSONA_ID"] = _memoryMcp.PersonaId,
                        // ③-3.4: проектная персона получает team_memory_* — общая память команды
                        ["MEMORY_PROJECT_ID"] = _memoryMcp.ProjectId ?? "",
                    },
                };
            }

            // Проверка _personasMcp избыточна по смыслу (hasPersonas истинен только когда контекст
            // есть — см. резолв пути выше), но без неё компилятор теряет null-состояние поля через
            // промежуточную bool и требует ! на каждом обращении внутри блока.
            if (hasPersonas && _personasMcp is not null)
            {
                // persona_ask выключен когда есть файловые сабагенты-персоны:
                // модель должна использовать Task(agentType=...) в Workflow, а не путаться.
                // Без agentDepth < 1 — на агентном ходу тоже выключаем (анти-рекурсия).
                var personaMentions = _personasMcp.MentionsHint is not null
                    && personaAgents is not { AgentHandles.Count: > 0 }
                    && _currentTurnAgentDepth < 1
                    ? "1" : "0";
                // Write-инструменты управления персонами (create/update/delete, automation_*,
                // bindings_set, generate_avatar) несут тяжёлые схемы (~28К токенов) — грузим их в
                // контекст только когда ход реально про управление командой. На агентном ходу
                // (agentDepth >= 1) управление персонами не нужно — та же анти-рекурсия, что у
                // mentions. Read/ask-инструменты остаются всегда (надёжные @упоминания без «No such tool»).
                var personaWrite = _currentTurnAgentDepth < 1 && Prompts.WriteIntentGate.PersonaManagement(turnText) ? "1" : "0";
                // Кросс-проектные ProjectPersonas-привязки: доступ к команде/точечным персонам
                // ДРУГОГО проекта — расширяют personas_list(scope=context) и резолв handle в persona_ask
                var extraProjectIdsCsv = _personasMcp.ExtraProjectIds is { Count: > 0 } extraProjects
                    ? string.Join(",", extraProjects) : "";
                var extraPersonaIdsCsv = _personasMcp.ExtraPersonaIds is { Count: > 0 } extraPersonas
                    ? string.Join(",", extraPersonas) : "";
                servers["personas"] = new System.Text.Json.Nodes.JsonObject
                {
                    ["command"] = "node",
                    ["args"] = new System.Text.Json.Nodes.JsonArray { personasServerPath! },
                    ["alwaysLoad"] = true,
                    ["env"] = new System.Text.Json.Nodes.JsonObject
                    {
                        ["PERSONAS_API_URL"] = _personasMcp.ApiUrl,
                        ["PERSONAS_API_TOKEN"] = _personasMcp.Token,
                        ["PERSONAS_PROJECT_ID"] = _personasMcp.ProjectId ?? "",
                        ["PERSONAS_SELF_ID"] = _personasMcp.SelfPersonaId ?? "",
                        ["PERSONAS_MENTIONS"] = personaMentions,
                        ["PERSONAS_BINDINGS"] = _personasMcp.BindingsEnabled ? "1" : "0",
                        ["PERSONAS_WRITE"] = personaWrite,
                        ["PERSONAS_EXTRA_PROJECT_IDS"] = extraProjectIdsCsv,
                        ["PERSONAS_EXTRA_PERSONA_IDS"] = extraPersonaIdsCsv,
                    },
                };
                // Состав/область персон зависит от write/mentions/bindings/extra-скоупов — в сигнатуру,
                // иначе поднятие write-канала (personas_create/…) не пробьёт доживание процесса
                shapes["personas"] = $"w{personaWrite}m{personaMentions}b{(_personasMcp.BindingsEnabled ? "1" : "0")}:{extraProjectIdsCsv}:{extraPersonaIdsCsv}";
            }

            if (hasWorkspace)
            {
                // Анти-рекурсия делегирования: на агентном ходу (chats_send из другой сессии)
                // секции chats и destructive не подключаются — агент не может писать в третьи
                // чаты и удалять данные (удаление — только по явной просьбе пользователя)
                var sections = _currentTurnAgentDepth >= 1
                    ? _workspaceMcp!.Sections.Where(s => s != "chats" && s != "destructive")
                    : _workspaceMcp!.Sections;
                var sectionsJoined = string.Join(",", sections);
                var workspaceWrite = Prompts.WriteIntentGate.WorkspaceWrite(turnText) ? "1" : "0";
                // Ключ сервера — "wsp", НЕ "workspace": claude CLI молча отбрасывает
                // MCP-сервер с зарезервированным именем "workspace" из --mcp-config
                // (сервер не стартует, инструменты не появляются). Отсюда же префикс
                // инструментов mcp__wsp__* в подсказках ниже и в PersonaBindingsService.
                servers["wsp"] = new System.Text.Json.Nodes.JsonObject
                {
                    ["command"] = "node",
                    ["args"] = new System.Text.Json.Nodes.JsonArray { workspaceServerPath! },
                    ["env"] = new System.Text.Json.Nodes.JsonObject
                    {
                        ["WORKSPACE_API_URL"] = _workspaceMcp!.ApiUrl,
                        ["WORKSPACE_API_TOKEN"] = _workspaceMcp.Token,
                        ["WORKSPACE_PROJECT_ID"] = _workspaceMcp.ProjectId ?? "",
                        ["WORKSPACE_SECTIONS"] = sectionsJoined,
                        ["WORKSPACE_PROJECT_IDS"] = _workspaceMcp.AllowedProjectIds is { Count: > 0 } allowed
                            ? string.Join(",", allowed) : "",
                        ["WORKSPACE_SELF_SESSION_ID"] = _workspaceMcp.SelfSessionId ?? "",
                        ["WORKSPACE_AGENT_DEPTH"] = Math.Max(_workspaceMcp.AgentDepth, _currentTurnAgentDepth).ToString(),
                        // Тяжёлые write-схемы (files_write с content, projects/chats create/update,
                        // knowledge_index) грузим в контекст только когда ход про запись в рабочее
                        // пространство. Read (list/tree/read/search/status/history) — всегда.
                        // Depth-гейта нет: делегированный ход тоже может нести интент записи; chats
                        // и destructive и так режутся секциями на агентном ходу выше.
                        ["WORKSPACE_WRITE"] = workspaceWrite,
                    },
                    // alwaysLoad как у memory/personas: аккаунт-коннекторы claude.ai переводят
                    // CLI в режим deferred-tools, где ленивые серверы прячут инструменты от модели.
                    // Персона-секретарь опирается на workspace-инструменты — держим их всегда видимыми.
                    ["alwaysLoad"] = true,
                };
                // Состав wsp-инструментов зависит от write-режима и набора секций — в сигнатуру
                shapes["wsp"] = $"w{workspaceWrite}:{sectionsJoined}";
            }

            if (hasNotifications)
            {
                servers["notifications"] = new System.Text.Json.Nodes.JsonObject
                {
                    ["command"] = "node",
                    ["args"] = new System.Text.Json.Nodes.JsonArray { notificationsServerPath! },
                    ["env"] = new System.Text.Json.Nodes.JsonObject
                    {
                        ["NOTIFICATIONS_API_URL"] = _notificationsMcp!.ApiUrl,
                        ["NOTIFICATIONS_API_TOKEN"] = _notificationsMcp.Token,
                        ["NOTIFICATIONS_SELF_PERSONA_ID"] = _notificationsMcp.SelfPersonaId ?? "",
                    },
                };
            }

            // pmem-серверы персон-консультантов (файловые сабагенты):
            // тот же memory-server под уникальным ключом pmem_<handle> с env КОНСУЛЬТАНТА —
            // файл агента ссылается на него по имени (mcpServers: [pmem_<handle>]), токен
            // живёт только в этом временном конфиге. БЕЗ alwaysLoad: ленивое подключение,
            // node-процесс не спавнится, пока консультанта не позвали (определение ~200 байт;
            // ретрай «No such tool available» вшит в тело файла агента).
            if (hasConsultants && memoryServerPath is not null)
            {
                foreach (var c in personaAgents!.MemoryServers)
                {
                    servers[c.ServerKey] = new System.Text.Json.Nodes.JsonObject
                    {
                        ["command"] = "node",
                        ["args"] = new System.Text.Json.Nodes.JsonArray { memoryServerPath },
                        ["env"] = new System.Text.Json.Nodes.JsonObject
                        {
                            ["MEMORY_API_URL"] = c.ApiUrl,
                            ["MEMORY_API_TOKEN"] = c.Token,
                            ["MEMORY_PERSONA_ID"] = c.PersonaId,
                            ["MEMORY_PROJECT_ID"] = c.ProjectId ?? "",
                        },
                    };
                }
            }

            // MCP-серверы внешних модулей (контракт §6, ТЗ R7) — строго аддитивно:
            // коллизия ключа со встроенным/пользовательским сервером → пропуск с логом
            // (модуль не может перекрыть tasks/notes/memory/…). Трафик инструментов идёт
            // через gateway ядра (MODULE_API_URL), токен chan=mcp свежий на каждый ход.
            if (hasModules)
            {
                foreach (var mod in _modulesMcp!.Servers)
                {
                    if (servers.ContainsKey(mod.Key))
                    {
                        Console.Error.WriteLine(
                            $"[ClaudeSession] MCP модуля «{mod.ModuleId}» пропущен: ключ «{mod.Key}» уже занят");
                        continue;
                    }
                    var argsArr = new System.Text.Json.Nodes.JsonArray();
                    var skip = false;
                    foreach (var arg in mod.Args)
                    {
                        // В песочнице абсолютные хост-пути args переводим в контейнерные
                        // (как AdaptServerForRuntime); непереводимый путь → сервер пропускается
                        if (_launcher.IsSandboxed && arg is { Length: > 2 } && char.IsLetter(arg[0]) && arg[1] == ':')
                        {
                            try { argsArr.Add(_launcher.Paths.ToRuntime(arg)); }
                            catch (InvalidOperationException)
                            {
                                Console.Error.WriteLine(
                                    $"[ClaudeSession] MCP модуля «{mod.ModuleId}» пропущен: путь {arg} недоступен в песочнице");
                                skip = true;
                                break;
                            }
                        }
                        else argsArr.Add(arg);
                    }
                    if (skip) continue;
                    servers[mod.Key] = new System.Text.Json.Nodes.JsonObject
                    {
                        ["command"] = mod.Command,
                        ["args"] = argsArr,
                        ["env"] = new System.Text.Json.Nodes.JsonObject
                        {
                            ["MODULE_API_URL"] = mod.ApiUrl,
                            ["MODULE_API_TOKEN"] = mod.TokenFactory(),
                            ["MODULE_ID"] = mod.ModuleId,
                        },
                    };
                }
            }

            if (servers.Count == 0) return (null, "");
            var combined = new System.Text.Json.Nodes.JsonObject { ["mcpServers"] = servers };
            // HostTempDir среды: для песочницы это bind-mount — процесс claude увидит файл
            var tmpPath = Path.Combine(_launcher.HostTempDir, $"claude-mcp-{Guid.NewGuid():N}.json");
            File.WriteAllText(tmpPath, combined.ToJsonString());
            // Ключ + отпечаток состава инструментов (если есть): смена per-turn флагов
            // (PERSONAS_WRITE/MENTIONS, WORKSPACE_WRITE/секции, TASKS_EXECUTE) меняет сигнатуру
            // запуска → живой процесс доживания не переиспользуется, инструменты поднимаются
            return (tmpPath, string.Join(",", servers
                .Select(kv => shapes.TryGetValue(kv.Key, out var shp) ? $"{kv.Key}:{shp}" : kv.Key)
                .OrderBy(k => k, StringComparer.Ordinal)));
        }
        catch (Exception ex)
        {
            // Без лога сессия молча пойдёт без MCP-серверов (tasks/dify) — обязательно сообщаем
            Console.Error.WriteLine($"[ClaudeSession] Не удалось собрать MCP-конфиг хода, используется базовый конфиг: {ex.Message}");
            return (null, "");
        }
    }

    // Путь MCP-сервера в среде исполнения: локально — как есть, в песочнице — /app/mcp/...
    // (образ несёт то же дерево). null — сервера нет в целевой среде (ход без него).
    private string? MapMcpPath(string? hostPath)
    {
        if (hostPath is null) return null;
        try { return _launcher.Paths.ToRuntime(hostPath); }
        catch (InvalidOperationException)
        {
            Console.Error.WriteLine($"[ClaudeSession] MCP-сервер недоступен в песочнице: {hostPath}");
            return null;
        }
    }

    // Адаптация стороннего описания MCP-сервера (базовый конфиг / user-scope) к среде:
    // локально — без изменений; в песочнице переписываем абсолютные Windows-пути в args
    // на контейнерные. Непереводимый путь → сервер пропускается (false). POSIX-пути
    // оставляем как есть: конфиги, писанные для контейнера (/app/...), в образе валидны.
    private bool AdaptServerForRuntime(string key, System.Text.Json.Nodes.JsonNode node)
    {
        if (!_launcher.IsSandboxed) return true;
        // localhost/127.0.0.1 в env-URL — это loopback ХОСТА (напр. DIFY_API_URL у dify):
        // из контейнера недостижим, переписываем на host.docker.internal (он в no_proxy песочницы)
        if (node["env"] is System.Text.Json.Nodes.JsonObject envObj)
        {
            foreach (var name in envObj.Select(kv => kv.Key).ToArray())
            {
                if (envObj[name] is not System.Text.Json.Nodes.JsonValue jv
                    || !jv.TryGetValue<string>(out var envVal)) continue;
                var rewritten = RewriteLoopbackUrl(envVal);
                if (!ReferenceEquals(rewritten, envVal)) envObj[name] = rewritten;
            }
        }
        if (node["args"] is not System.Text.Json.Nodes.JsonArray argsArr) return true;
        for (var i = 0; i < argsArr.Count; i++)
        {
            var val = argsArr[i]?.GetValue<string>();
            // Только абсолютные хост-пути вида X:\... / X:/...
            if (val is not { Length: > 2 } || !char.IsLetter(val[0]) || val[1] != ':') continue;
            try { argsArr[i] = _launcher.Paths.ToRuntime(val); }
            catch (InvalidOperationException)
            {
                Console.Error.WriteLine($"[ClaudeSession] MCP-сервер «{key}» пропущен: путь {val} недоступен в песочнице");
                return false;
            }
        }
        return true;
    }

    // http://localhost:…/http://127.0.0.1:… → http://host.docker.internal:… (для env песочницы).
    // Возвращает исходную строку (тот же экземпляр), если переписывать нечего.
    private static string RewriteLoopbackUrl(string value)
    {
        foreach (var prefix in (string[])["http://localhost", "http://127.0.0.1"])
        {
            if (!value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
            var rest = value[prefix.Length..];
            // Граница хоста: конец строки, порт или путь — «localhost-foo» не трогаем
            if (rest.Length == 0 || rest[0] == ':' || rest[0] == '/')
                return "http://host.docker.internal" + rest;
        }
        return value;
    }

    // User-scope MCP-серверы (~/.claude.json, mcpServers: fal-ai и др.) — прокидываем в
    // --mcp-config только когда ход пойдёт с ИЗОЛИРОВАННЫМ CLAUDE_CONFIG_DIR, где CLI не
    // прочитает ~/.claude.json сам:
    //  - сторонний провайдер (DeepSeek/GLM) — свой профиль claude-profiles/{key};
    //  - подписка пула Claude (sub-*) — свой профиль claude-profiles/sub-{key}.
    // Для основной подписки (CONFIG_DIR = ~/.claude) НЕ дублируем — CLI читает сам (задвоение).
    // null — основной Claude, файла нет или mcpServers пуст.
    private System.Text.Json.Nodes.JsonObject? LoadUserScopeMcpServers()
    {
        if (_providers is null) return null;
        var isThirdParty = _providers.ResolveByModel(Info.Model) is not null;
        // Подписка пула = провайдер сессии не "claude", не сторонний ключ, а активная доп.
        // подписка (условие 1:1 с применением BuildOAuthCliEnv при выборе env хода)
        var isPoolSubscription = _subscriptionPool?.HasExtra == true
            && Info.Provider is not null && Info.Provider != "claude"
            && _providers.GetByKey(Info.Provider) is null
            && _subscriptionPool.All.FirstOrDefault(s => s.Key == Info.Provider)?.Enabled == true;
        if (!isThirdParty && !isPoolSubscription) return null;
        var path = _providers.UserClaudeJsonPath;
        try
        {
            if (!File.Exists(path)) return null;
            var doc = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path));
            return doc?["mcpServers"] is System.Text.Json.Nodes.JsonObject o && o.Count > 0 ? o : null;
        }
        catch (Exception ex)
        {
            // Без user-scope серверов ход пойдёт, но fal-ai и др. пропадут — сообщаем
            Console.Error.WriteLine($"[ClaudeSession] Не удалось прочитать user-scope MCP из {path}: {ex.Message}");
            return null;
        }
    }

    // Ничего не делаем при старте — процесс запускается при первом сообщении
    public Task StartAsync() => Task.CompletedTask;

    public Task SendMessageAsync(string text, IReadOnlyList<string>? attachedPaths = null, int agentDepth = 0,
        bool suppressTasksExecute = false)
    {
        Info.MessageCount++;
        Info.LastMessage = text.Length > 100 ? text[..100] + "…" : text;
        Info.UpdatedAt = DateTime.UtcNow;

        // Если сообщение — вызов скилла (/skill-name [args]), разворачиваем его содержимое
        var effectiveText = _skills?.TryExpandSkill(text) ?? text;
        // Картинки отправляем как image-блоки (base64), остальные файлы — инлайним в текст
        var (imagePaths, otherPaths) = AttachmentInliner.SplitImagePaths(attachedPaths);
        var fullText = AttachmentInliner.BuildMessageText(_rootPath, effectiveText, otherPaths);

        return QueueTurnAsync(fullText, imagePaths, agentDepth, suppressTasksExecute);
    }

    // Ручное сворачивание контекста: /compact как обычный ход,
    // минуя счётчики сообщений, авто-имя чата и разворачивание скиллов
    public Task CompactAsync() => QueueTurnAsync("/compact", [], 0, false);

    // Ставит ход в очередь в фоне, чтобы не блокировать SignalR-соединение
    private Task QueueTurnAsync(string fullText, List<string> imagePaths, int agentDepth, bool suppressTasksExecute)
    {
        _ = Task.Run(async () =>
        {
            if (_cts.IsCancellationRequested) return;
            await _turnLock.WaitAsync(_cts.Token);
            try { await RunTurnAsync(fullText, imagePaths, agentDepth, suppressTasksExecute, _cts.Token); }
            catch (OperationCanceledException) { /* остановка сессии — штатно */ }
            catch (Exception ex)
            {
                // Статус Error выставит SessionManager по ErrorMessage
                await _onMessage(new ErrorMessage(ex.Message));
            }
            finally
            {
                // Ход закончился — следующий (если его инициирует человек) идёт с полным
                // набором инструментов; действует ровно на ход внутри _turnLock
                _currentTurnAgentDepth = 0;
                _currentTurnSuppressTasksExecute = false;
                _turnLock.Release();
            }
        });

        return Task.CompletedTask;
    }

    private static string MediaTypeForExt(string ext) => ext.ToLowerInvariant() switch
    {
        ".png" => "image/png",
        ".gif" => "image/gif",
        ".webp" => "image/webp",
        _ => "image/jpeg",
    };

    // Блоки изображений для content стартового сообщения. Пустые/слишком большие (>8 МБ) пропускаем.
    private List<object> BuildImageBlocks(IReadOnlyList<string> imagePaths)
    {
        var blocks = new List<object>();
        foreach (var rel in imagePaths)
        {
            try
            {
                var full = FileService.SafeJoin(_rootPath, rel);
                if (!File.Exists(full)) continue;
                var bytes = File.ReadAllBytes(full);
                if (bytes.Length == 0 || bytes.Length > 8 * 1024 * 1024) continue;
                blocks.Add(new
                {
                    type = "image",
                    source = new { type = "base64", media_type = MediaTypeForExt(Path.GetExtension(rel)), data = Convert.ToBase64String(bytes) }
                });
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[ClaudeSession] Не удалось прочитать вложение-изображение «{rel}»: {ex.Message}");
            }
        }
        return blocks;
    }

    public void RespondPermission(string requestId, string behavior)
    {
        if (_permissionWaiters.TryGetValue(requestId, out var tcs))
            tcs.TrySetResult(behavior);
    }

    // Ответ пользователя на AskUserQuestion — control_response на исходный can_use_tool запрос
    public void AnswerQuestion(string toolUseId, string updatedInputJson)
    {
        if (!_pendingQuestions.TryRemove(toolUseId, out var requestId)) return;
        object updatedInput;
        try { updatedInput = JsonSerializer.Deserialize<object>(updatedInputJson)!; }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[ClaudeSession] Ответ на вопрос не распарсился, отправляем пустой input: {ex.Message}");
            updatedInput = new { };
        }
        SendControlResponse(requestId, new { behavior = "allow", updatedInput });
    }

    // Решение пользователя по плану (ExitPlanMode): approve → allow и Claude продолжает выполнение;
    // reject → deny с комментарием, Claude остаётся в режиме планирования
    public void RespondPlan(string requestId, bool approve, string? feedback)
    {
        if (!_pendingPlans.TryRemove(requestId, out _)) return;
        if (approve)
        {
            // Ждём, что Claude реализует план в этом ходу; если завершит без правок — дошлём команду.
            // allow без updatedInput — CLI продолжит с исходным планом (см. HandleControlRequestAsync)
            _awaitPlanExecution = true;
            _sawToolSinceApprove = false;
            SendControlResponse(requestId, new { behavior = "allow" });
        }
        else
        {
            var message = string.IsNullOrWhiteSpace(feedback)
                ? "Пользователь отклонил план. Уточни план с учётом контекста и предложи заново."
                : $"Пользователь отклонил план с комментарием: {feedback}";
            SendControlResponse(requestId, new { behavior = "deny", message });
        }
    }

    // Обработка control_request(can_use_tool): AskUserQuestion → интерактивная карточка,
    // ExitPlanMode → согласование плана, прочие инструменты → permission-пайплайн.
    // Актуальные CLI шлют permission-запросы именно этим каналом (не sdk_control_request) —
    // авто-allow здесь означал бы исполнение любых команд без карточек.
    private async Task HandleControlRequestAsync(JsonElement root)
    {
        var requestId = root.TryGetProperty("request_id", out var rid) ? rid.GetString() ?? "" : "";
        if (!root.TryGetProperty("request", out var req)) return;
        var subtype = req.TryGetProperty("subtype", out var st) ? st.GetString() : null;
        if (subtype != "can_use_tool") return;

        var toolName = req.TryGetProperty("tool_name", out var tn) ? tn.GetString() ?? "" : "";
        var toolUseId = req.TryGetProperty("tool_use_id", out var tu) ? tu.GetString() ?? "" : "";
        var inputEl = req.TryGetProperty("input", out var ti) ? ti : default;
        var input = inputEl.ValueKind != JsonValueKind.Undefined
            ? JsonSerializer.Deserialize<object>(inputEl.GetRawText())! : new object();

        if (toolName == "AskUserQuestion")
        {
            // Ждём выбор пользователя — control_response отправит AnswerQuestion.
            // Статус Waiting выставит SessionManager по AskQuestionMessage
            _pendingQuestions[toolUseId] = requestId;
            await _onMessage(new AskQuestionMessage(toolUseId, input));
            return;
        }

        if (toolName == "ExitPlanMode")
        {
            // Режим «План»: Claude представил план — ждём решения пользователя (RespondPlan),
            // НЕ авто-одобряем, иначе план не выносится на согласование.
            // Статус Waiting выставит SessionManager по PlanReviewMessage
            _pendingPlans[requestId] = input;
            var plan = inputEl.ValueKind == JsonValueKind.Object && inputEl.TryGetProperty("plan", out var pl)
                ? pl.GetString() ?? "" : "";
            await _onMessage(new PlanReviewMessage(requestId, plan));
            return;
        }

        var behavior = await DecidePermissionAsync(requestId, toolName, inputEl, input);
        if (behavior == "cancelled") return; // Interrupt — процесс убит, отвечать некому
        // allow БЕЗ updatedInput: CLI продолжает с исходным вводом модели. Эхо updatedInput
        // ломало Workflow — возвращённый хэндлером ввод CLI прогоняет через доп. проверку
        // «управляющие символы, скрытые в диалоге одобрения» (исходный ввод модели ей не
        // подвергается), и резолвнутый script именованного workflow её не проходил.
        SendControlResponse(requestId, behavior == "allow"
            ? new { behavior = "allow" }
            : (object)new { behavior = "deny", message = "Пользователь отклонил действие" });
    }

    // Решение по инструменту: правила проекта → «всегда разрешать» этой сессии → карточка
    // пользователю. Возвращает "allow" | "deny" | "cancelled" (Interrupt во время ожидания).
    private async Task<string> DecidePermissionAsync(string requestId, string toolName, JsonElement inputEl, object toolInput)
    {
        // Правила проекта: deny приоритетнее; allow — авто-разрешить; null — спросить пользователя
        var ruleDecision = PermissionRuleEvaluator.Evaluate(_permissionRules?.Invoke(), toolName, inputEl);
        if (ruleDecision == "deny") return "deny";
        // Сессия-исполнитель задачи или ход правила автоматизации персоны работают автономно —
        // отвечать на карточку разрешения некому (чат никто не открывал), и без этого исполнитель
        // вязнет в первом же permission-запросе (status=Waiting до таймаута в 60 мин) и не может
        // работать. Разрешаем ВСЕ инструменты автоматически: deny-правило проекта выше учтено,
        // а права персоны уже ограничены Persona.Tools и ExtraDisallowedTools.
        if (ruleDecision == null && (Info.TaskExecution || Info.AutomationRuleId is not null)) return "allow";
        // Свои MCP-серверы — без карточки, см. комментарий у BuiltInMcpServerPrefixes.
        if (ruleDecision == null && Array.Exists(BuiltInMcpServerPrefixes, p => toolName.StartsWith(p, StringComparison.Ordinal)))
            return "allow";
        if (ruleDecision == "allow" || _autoAllowTools.ContainsKey(toolName)) return "allow";

        var tcs = new TaskCompletionSource<string>();
        _permissionWaiters[requestId] = tcs;

        // Статус Waiting выставит SessionManager по PermissionRequestMessage,
        // Working вернёт SessionManager.RespondPermission по ответу пользователя
        await _onMessage(new PermissionRequestMessage(requestId, toolName, toolInput));

        string behavior;
        try
        {
            // Ждём ответа пользователя или таймаута 60 минут
            behavior = await tcs.Task.WaitAsync(TimeSpan.FromMinutes(60));
        }
        catch (TaskCanceledException)
        {
            // Interrupt() отменил TCS через TrySetCanceled() — процесс уже убит
            _permissionWaiters.TryRemove(requestId, out _);
            return "cancelled";
        }
        catch (TimeoutException)
        {
            // Пользователь не ответил — deny и продолжаем
            _permissionWaiters.TryRemove(requestId, out _);
            return "deny";
        }
        _permissionWaiters.TryRemove(requestId, out _);

        // «Всегда разрешать»: запоминаем инструмент и отвечаем обычным allow
        if (behavior == "allow_always")
        {
            _autoAllowTools.TryAdd(toolName, 0);
            behavior = "allow";
        }
        return behavior;
    }

    private void SendControlResponse(string requestId, object responsePayload)
    {
        var msg = JsonSerializer.Serialize(new
        {
            type = "control_response",
            response = new { subtype = "success", request_id = requestId, response = responsePayload }
        });
        WriteLineToStdin(msg);
    }

    // Смена режима прав на лету: пишем control_request set_permission_mode в stdin живого
    // процесса. CLI применяет его к идущему ходу (дальнейшие tool-вызовы уже по новому режиму)
    // и отвечает control_response success (reader его игнорирует как неизвестный тип).
    // Нет процесса — false: SessionManager уже обновил Info.Mode, следующий ход пересоздастся с флагом.
    public bool TrySetPermissionModeLive(ClaudeMode mode)
    {
        var proc = _currentProcess;
        if (proc is null || proc.HasExited) return false;
        var req = JsonSerializer.Serialize(new
        {
            type = "control_request",
            request_id = "setmode_" + Guid.NewGuid().ToString("N")[..12],
            request = new { subtype = "set_permission_mode", mode = mode.ToCliFlag() }
        });
        WriteLineToStdin(req);
        return true;
    }

    // Смена модели на лету: control_request set_model в stdin живого процесса. CLI применяет
    // её к последующим round-trip'ам идущего хода и отвечает control_response success (reader
    // игнорирует). Модель нормализуем как для --model (снимаем window-алиас [1m]). Нет
    // процесса — false: SessionManager уже обновил Info.Model, следующий ход пересоздастся с ней.
    public bool TrySetModelLive(string model)
    {
        var proc = _currentProcess;
        if (proc is null || proc.HasExited) return false;
        var req = JsonSerializer.Serialize(new
        {
            type = "control_request",
            request_id = "setmodel_" + Guid.NewGuid().ToString("N")[..12],
            request = new { subtype = "set_model", model = LlmProviderRegistry.StripClaudeWindowAlias(model) ?? model }
        });
        WriteLineToStdin(req);
        return true;
    }

    // Единая точка записи в stdin процесса — под _stdinLock, чтобы параллельные
    // control_response (SignalR-потоки + памп) не перемешали JSON-строки
    private void WriteLineToStdin(string line)
    {
        var proc = _currentProcess;
        if (proc is null || proc.HasExited) return;
        _stdinLock.Wait();
        try
        {
            proc.StandardInput.WriteLine(line);
            proc.StandardInput.Flush();
        }
        catch (Exception ex)
        {
            // Процесс мог завершиться между проверкой и записью
            Console.Error.WriteLine($"[ClaudeSession] Запись в stdin не удалась: {ex.Message}");
        }
        finally { _stdinLock.Release(); }
    }

    // Закрытие stdin под тем же локом — не обрываем чужую запись на середине строки.
    // StdinClosed помечаем до Close: прогон с закрытым stdin ходы больше не принимает
    private void CloseStdin(CliRun run)
    {
        _stdinLock.Wait();
        try
        {
            run.StdinClosed = true;
            run.Process.StandardInput.Close();
        }
        catch { /* поток уже закрыт или процесс мёртв — не критично */ }
        finally { _stdinLock.Release(); }
    }

    // Закрыть stdin, только если прогон реально простаивает. Проверка условия — ПОД
    // _stdinLock: между внешней проверкой и закрытием мог проскочить TrySubmitTurn
    // (TurnDone=false), и безусловное закрытие оставило бы свежий ход с мёртвым stdin
    // (permission-ответы не записать, ход умер бы по часовому IdleTimeout)
    private void CloseStdinIfIdle(CliRun run)
    {
        _stdinLock.Wait();
        try
        {
            if (!run.TurnDone || run.HasPendingBg || run.StdinClosed) return;
            run.StdinClosed = true;
            run.Process.StandardInput.Close();
        }
        catch { /* поток уже закрыт или процесс мёртв — не критично */ }
        finally { _stdinLock.Release(); }
    }

    // Отдать ход живому процессу доживающего прогона (same-process ход). false — прогон
    // непригоден (умер/stdin закрыт/запись сорвалась): ход пойдёт новым процессом
    private bool TrySubmitTurn(CliRun run, string userMessageJson)
    {
        _stdinLock.Wait();
        try
        {
            if (run.StdinClosed || run.Process.HasExited) return false;
            // Идёт ход-продолжение CLI (ответ на task-notification) — его result придёт
            // раньше нашего и не должен завершить этот ход
            if (run.ContinuationActive)
            {
                Interlocked.Increment(ref run.SkipResults);
                run.ContinuationActive = false;
                CorrTrace("submit-turn(skip++)", Info.Id, run);
            }
            else
                CorrTrace("submit-turn(no-skip)", Info.Id, run);
            run.TurnTcs = CliRun.NewTcs();
            run.TurnDone = false;
            run.Process.StandardInput.WriteLine(userMessageJson);
            run.Process.StandardInput.Flush();
            return true;
        }
        catch (Exception ex)
        {
            run.TurnDone = true; // прогон между ходами; финализация резолвит его TurnTcs
            Console.Error.WriteLine($"[ClaudeSession] Ход в живой процесс не записался, стартуем новый: {ex.Message}");
            return false;
        }
        finally { _stdinLock.Release(); }
    }

    public void Interrupt()
    {
        if (_currentProcess is { } proc) _launcher.Kill(proc, _currentTurnId);
        // Процесс убит вместе с workflow-раннерами — агенты уже не завершатся: закрываем
        // карточки workflow финальным isDone (fire-and-forget, повторный вызов — no-op)
        List<WorkflowWatcher> workflowWatchers;
        lock (_workflowWatchers) workflowWatchers = _workflowWatchers.Where(w => !w.IsDisposed).ToList();
        foreach (var w in workflowWatchers) _ = w.AbortAsync();
        // Отменяем все ожидающие permission-диалоги: процесс убит, ответа не будет
        foreach (var tcs in _permissionWaiters.Values)
            tcs.TrySetCanceled();
        _permissionWaiters.Clear();
        _pendingQuestions.Clear();
        _pendingPlans.Clear();
        _awaitPlanExecution = false;
        _forceNonPlanNextTurn = false;
    }

    private async Task RunTurnAsync(string text, IReadOnlyList<string> imagePaths, int agentDepth,
        bool suppressTasksExecute, CancellationToken ct)
    {
        // Глубина делегирования действует ровно на этот ход (внутри _turnLock):
        // MCP-конфиг ниже собирается уже с учётом анти-рекурсии, сброс — в finally
        _currentTurnAgentDepth = agentDepth;
        _currentTurnSuppressTasksExecute = suppressTasksExecute;

        // --print обязателен: без него --output-format/--input-format/--include-partial-messages/--permission-prompt-tool не работают
        // --input-format stream-json нужен: мы посылаем JSON-объекты в stdin, а не plain text
        var args = new List<string>
        {
            "--print",
            "--verbose",
            "--output-format", "stream-json",
            "--input-format", "stream-json",
            "--include-partial-messages",
            "--permission-prompt-tool", "stdio"
        };

        // Отключаем хуки плагинов на хосте (окна консоли на каждый ход); скиллы остаются
        args.AddRange(ClaudeRuntimeSettings.HooksOffArgs(_launcher));

        if (Info.ClaudeSessionId is not null)
            args.AddRange(["--resume", Info.ClaudeSessionId]);

        // Режим прав у claude CLI задаётся флагом --permission-mode (значения: default,
        // acceptEdits, plan, auto, dontAsk, bypassPermissions), а НЕ --mode (такого флага нет).
        // После одобрения плана один ход выполняем без plan, чтобы Claude реализовал, а не планировал заново.
        if (_forceNonPlanNextTurn)
            _forceNonPlanNextTurn = false;
        else
            args.AddRange(["--permission-mode", Info.Mode.ToCliFlag()]);

        if (!string.IsNullOrWhiteSpace(Info.Model))
            args.AddRange(["--model", LlmProviderRegistry.StripClaudeWindowAlias(Info.Model)!]);

        if (!string.IsNullOrWhiteSpace(Info.Effort))
            args.AddRange(["--effort", Info.Effort]);

        // Подсказка следующего сообщения: CLI после result испускает prompt_suggestion
        // (генерация фоном с переиспользованием prompt cache хода; при холодном кэше CLI
        // сам пропускает). Только родной Claude — сторонним провайдерам фоновые запросы
        // не включаем (кэш-экономика чужая).
        var promptSuggestionsActive = _providers is null || _providers.ResolveByModel(Info.Model) is null;
        if (promptSuggestionsActive)
            args.AddRange(["--prompt-suggestions", "true"]);

        // Файловые сабагенты-персоны. На агентном ходу (agentDepth >= 1) план урезается
        // до pmem-серверов: подсказки и --add-dir не даём (анти-рекурсия, как
        // TASKS_EXECUTE/PERSONAS_MENTIONS), но .md-файлы в cwd проекта/Chats CLI видит
        // и без add-dir — если модель всё же позовёт Task(handle), память сабагента
        // должна быть достижима, иначе frontmatter (mcpServers: [pmem_…]) укажет в пустоту.
        // Ошибки провайдера — ход без консультантов.
        PersonaAgentsContext? personaAgents = null;
        if (_personaAgentsProvider is not null
            && !_disallowedTools.Contains("Task", StringComparer.Ordinal))
        {
            try { personaAgents = _personaAgentsProvider(); }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[ClaudeSession] План сабагентов-персон не собрался: {ex.Message}");
            }
            if (agentDepth >= 1 && personaAgents is not null)
                personaAgents = personaAgents with { AddDirs = [], AgentHandles = [] };
        }

        // MCP-конфиг: создаём каждый ход с актуальным dataset id (мог появиться после создания сессии)
        var currentWk = _wkStore?.GetByPath(_rootPath);
        var currentDatasetId = currentWk?.DifyDatasetId;
        var (turnMcpPath, mcpServerKeys) = BuildTurnMcpConfig(currentDatasetId, personaAgents, text);
        var effectiveMcpConfig = turnMcpPath ?? _mcpConfigPath;
        if (!string.IsNullOrWhiteSpace(effectiveMcpConfig) && File.Exists(effectiveMcpConfig))
        {
            // Аргумент — путь В СРЕДЕ исполнения (temp хода лежит на bind-mount песочницы);
            // файл базового конфига может оказаться вне неё — тогда ход идёт без MCP
            try { args.AddRange(["--mcp-config", _launcher.Paths.ToRuntime(effectiveMcpConfig)]); }
            catch (InvalidOperationException ex)
            {
                Console.Error.WriteLine($"[ClaudeSession] MCP-конфиг недоступен в песочнице, ход без него: {ex.Message}");
            }
        }

        // Папки файловых сабагентов: CLI сканирует {dir}/.claude/agents при старте процесса —
        // правки персон применяются со следующего хода
        if (personaAgents is not null)
            foreach (var dir in personaAgents.AddDirs)
            {
                // Резервные папки агентов не смонтированы в песочницу — пропускаем:
                // агенты чата и так лежат в {chatRoot}/.claude/agents, консультанты доступны через persona_ask
                try { args.AddRange(["--add-dir", _launcher.Paths.ToRuntime(dir)]); }
                catch (InvalidOperationException) { }
            }

        // pmem-серверы консультантов: сессионный allow, БЕЗ него вызов из фонового сабагента
        // упирается в permission-запрос, на который некому ответить (проверено вживую).
        // Закрыть их от ГЛАВНОЙ сессии технически нельзя — permission-правила общие на процесс,
        // а disallow имени сервера проникает и в сабагента, глуша его allow-list (проверено
        // вживую: сабагент получил только Read/Grep). Осознанный компромисс: главную сессию
        // ограничиваем инструкцией в hint (BuildMentionsHint) — «не трогай mcp__pmem_*».
        if (personaAgents is { MemoryServers.Count: > 0 })
            args.AddRange(["--allowedTools",
                string.Join(",", personaAgents.MemoryServers.Select(s => "mcp__" + s.ServerKey))]);

        // Блокируем коннекторы аккаунта claude.ai — они вливаются помимо --mcp-config.
        if (_disallowedTools.Length > 0)
            args.AddRange(["--disallowedTools", string.Join(",", _disallowedTools)]);

        // Слой персоны из системного промпта — жёсткая часть сигнатуры прогона
        // (смена собеседника посреди доживания = несовместимый ход → новый процесс)
        string? personaLayerPrompt = null;

        // Системный промпт: пересчитываем и передаём КАЖДЫЙ ход. Ход в новом процессе
        // (claude --print --resume) получает его через --append-system-prompt — тот не
        // сохраняется в транскрипте сессии: не передать → инструкции (fal-ai/запрет ASCII,
        // Dify, теги) пропадут. Same-process ход промпт живого процесса НЕ обновляет —
        // recall/подсказки на нём остаются со старта прогона (мягкая деградация).
        {
            var basePrompt = ProjectManager.BuildSystemPrompt(
                _rawSystemPrompt, currentDatasetId != null, currentWk?.DocumentTags);

            // Подсказка про систему задач — только когда tasks-server подключён
            if (_tasksMcp is not null)
            {
                var scope = _tasksMcp.ProjectId is not null
                    ? "Текущий контекст — задачи этого проекта."
                    : "Текущий контекст — личные задачи пользователя (вне проектов).";
                var columnsHint = _tasksMcp.ProjectId is not null
                    ? " У проекта может быть Kanban-доска с кастомными колонками: получи их через tasks_board_columns и клади задачу в нужную колонку, передавая columnId в tasks_create/tasks_update (статус выставится по категории колонки)."
                    : "";
                // tasks_run_executor доступен только на пользовательском ходу (см. TASKS_EXECUTE выше)
                var executeHint = ResolveTasksExecuteEnabled(_currentTurnAgentDepth, Info.TaskDelegationDepth, _currentTurnSuppressTasksExecute)
                    ? " tasks_run_executor запускает Claude-исполнителя задачи (отдельная сессия, работает в фоне)."
                    : "";
                // Поручение задачи персоне — только когда доступен и personas-server (есть personas_list)
                var personaExecHint = _personasMcp is not null
                    ? " Чтобы поручить задачу персоне-исполнителю, передай её personaId в tasks_create/tasks_update — " +
                      "задачу выполнит Claude от её лица; список персон и их id — personas_list."
                    : "";
                // Прикрепление итога выполнения — задачи с проектом имеют файлы; результат полезен всегда
                var resultHint = _tasksMcp.ProjectId is not null
                    ? " Завершая задачу через tasks_complete, прикрепляй итог: resultMarkdown — короткое описание сделанного, linkedFiles — пути затронутых файлов проекта (от корня, через /)."
                    : " Завершая задачу через tasks_complete, прикрепляй итог: resultMarkdown — короткое описание сделанного.";
                // Кросс-проектные ProjectTasks-привязки: тебе доступны задачи ещё каких-то проектов
                var crossProjectHint = _tasksMcp.ExtraProjectIds is { Count: > 0 }
                    ? " Тебе также доступны задачи ДРУГИХ проектов владельца (кросс-проектная привязка) — " +
                      "список и доступность (полный/только чтение) — tasks_list_projects; в tasks_create/tasks_list " +
                      "передай их projectId явно, чтобы адресовать задачу туда."
                    : "";
                var tasksHint =
                    "У пользователя есть встроенная система задач (вкладка «Задачи» в проекте и раздел «Календарь»). " +
                    "Управляй ею через MCP-инструменты mcp__tasks__* (tasks_list, tasks_search, tasks_get, tasks_create, " +
                    "tasks_update, tasks_complete, tasks_delete, tasks_add_subtask, tasks_toggle_subtask, tasks_board_columns). " + scope + " " +
                    "Когда пользователь просит создать/найти/изменить задачу, напоминание или список дел — используй эти инструменты, " +
                    "а не файлы или собственный список. Даты — в формате YYYY-MM-DD, время HH:MM." + columnsHint + executeHint + personaExecHint + resultHint + crossProjectHint;
                basePrompt = string.IsNullOrWhiteSpace(basePrompt)
                    ? tasksHint
                    : basePrompt + "\n\n" + tasksHint;
            }

            // Подсказка про базу заметок — только когда notes-server подключён
            if (_notesMcp is not null)
            {
                var scope = _notesMcp.ProjectId is not null
                    ? "По умолчанию создавай заметки в notes/ текущего проекта; source=\"personal\" — в личный vault."
                    : "По умолчанию создавай заметки в личный vault пользователя; source=<projectId> — в notes/ проекта.";
                var notesHint =
                    "У пользователя есть база знаний «Заметки» (Obsidian-совместимая: markdown-файлы со связями [[Заголовок]], " +
                    "обратными ссылками и графом). Веди её через MCP-инструменты mcp__notes__* (notes_list, notes_search, " +
                    "notes_read, notes_create, notes_update, notes_backlinks, notes_graph, notes_delete). " + scope + " " +
                    "Связывай заметки друг с другом через [[Заголовок другой заметки]] — по этим ссылкам строится граф знаний. " +
                    "Когда пользователь просит записать/законспектировать/связать мысль или найти по заметкам — используй эти инструменты. " +
                    "Комментарии к markdown-документам: notes_annotate (оставить комментарий к дословному фрагменту документа — " +
                    "anchorText копируй точно из файла), notes_annotations (комментарии документа с их статусами), " +
                    "notes_reply/notes_thread (ответы в треде комментария), " +
                    "notes_set_status (resolved = обработан), notes_search со status:open — найти необработанные.";
                basePrompt = string.IsNullOrWhiteSpace(basePrompt)
                    ? notesHint
                    : basePrompt + "\n\n" + notesHint;
            }

            // Подсказка про виджеты — только когда widgets-server подключён
            if (_widgetsMcp is not null)
            {
                var widgetsHint =
                    "Тебе доступен инструмент mcp__widgets__widget_show — интерактивный HTML-виджет прямо в ленте чата. " +
                    "Используй его, когда наглядность лучше текста: дашборды и сводки с метриками, графики и диаграммы " +
                    "(рисуй сам через inline SVG/canvas), таблицы с сортировкой, калькуляторы, мини-игры, интерактивные " +
                    "демонстрации. Требования к html: self-contained фрагмент БЕЗ <html>/<head>/<body>, все стили и " +
                    "скрипты — inline; внешние ресурсы (CDN-скрипты, картинки по URL, шрифты, fetch) заблокированы " +
                    "песочницей — не используй их вовсе. Лимит 64 КБ. Для попадания в тему приложения используй " +
                    "CSS-переменные var(--cc-bg), var(--cc-text), var(--cc-accent), var(--cc-border), var(--cc-muted). " +
                    "Верстай адаптивно: лента бывает узкой (320px). Виджет уже показан пользователю — не пересказывай " +
                    "его содержимое текстом, достаточно короткого комментария.";
                basePrompt = string.IsNullOrWhiteSpace(basePrompt)
                    ? widgetsHint
                    : basePrompt + "\n\n" + widgetsHint;
            }

            // Манифест recall (F3): что персона подтянула в этот ход — заметки + память.
            List<RecallItem>? manifestItems = null;

            // Auto-recall: релевантные заметки по тексту хода. Имеет смысл только когда
            // notes-server подключён (в блоке фигурирует notes_read по id). Провайдер сам
            // гейтит по флагу и failsafe-таймауту; исключения не должны ронять ход.
            if (_recallProvider is not null && _notesMcp is not null)
            {
                RecallBlock? recallBlock = null;
                try { recallBlock = await _recallProvider(text); }
                catch { /* recall не должен ронять ход */ }
                if (!string.IsNullOrWhiteSpace(recallBlock?.Text))
                    basePrompt = string.IsNullOrWhiteSpace(basePrompt)
                        ? recallBlock.Text
                        : basePrompt + "\n\n" + recallBlock.Text;
                if (recallBlock?.Items.Count > 0)
                {
                    manifestItems ??= new List<RecallItem>();
                    manifestItems.AddRange(recallBlock.Items);
                }
            }

            // Подсказка про раздел «Персоны» — только когда personas-server подключён
            if (_personasMcp is not null)
            {
                var scope = _personasMcp.ProjectId is not null
                    ? "Текущий контекст — проект: создавая проектную персону (scope \"project\"), projectId можно не указывать."
                    : "Текущий чат вне проекта: по умолчанию создаются глобальные персоны, для проектной укажи projectId.";
                // Write-интент управления командой — тот же гейт, что у PERSONAS_WRITE в BuildTurnMcpConfig:
                // на ходах без него тяжёлые схемы create/update/… не грузятся, и подсказка их не обещает.
                var personaWrite = _currentTurnAgentDepth < 1 && Prompts.WriteIntentGate.PersonaManagement(text);
                var personasHint =
                    "У пользователя есть раздел «Персоны» — AI-собеседники с именем, ролью, характером и аватаром, " +
                    "глобальные или привязанные к проекту. Смотри их через mcp__personas__* (personas_list, personas_get). " +
                    scope;
                if (personaWrite)
                    personasHint +=
                        " Управляй ими: personas_create, personas_update, personas_delete, personas_generate_avatar — " +
                        "когда пользователь просит создать/изменить/удалить персону или сгенерировать ей аватар. " +
                        "Создавая персону, заполняй ВСЕ слоты характера: character (на «ты», «Ты — …»), tone, mustDo, " +
                        "mustNot, outputFormat, speechExamples; приветствие — в greeting от её лица.";
                else
                    personasHint +=
                        " Инструменты управления (создать/изменить/настроить персону, аватар) появляются, " +
                        "когда пользователь явно об этом просит.";
                // Привязки персон (флаг persona-bindings) — кратко про инструменты работы с ними
                if (_personasMcp.BindingsEnabled)
                {
                    personasHint +=
                        " У персон есть «привязки» — источники знаний и правила с условиями применения: " +
                        "personas_bindings_list — посмотреть, personas_suggest_bindings — предложить (не сохраняет)";
                    personasHint += personaWrite
                        ? ", personas_bindings_set — заменить набор; в personas_create — параметры bindings/autoBindings. " +
                          "Свои собственные привязки персона менять не может."
                        : ". Свои собственные привязки персона менять не может.";
                }
                basePrompt = string.IsNullOrWhiteSpace(basePrompt)
                    ? personasHint
                    : basePrompt + "\n\n" + personasHint;
            }

            // Подсказка про рабочее пространство — только когда workspace-server подключён
            if (_workspaceMcp is not null)
            {
                var wsScope = _workspaceMcp.ProjectId is not null
                    ? "Текущая сессия идёт в проекте — его файлы правь встроенными Read/Edit/Write, а не через mcp__wsp__files_*."
                    : "Текущая сессия — чат вне проекта.";
                // Write-интент записи в рабочее пространство — тот же гейт, что у WORKSPACE_WRITE выше:
                // без него write-инструменты не загружены, и подсказка их не перечисляет.
                var wsWrite = Prompts.WriteIntentGate.WorkspaceWrite(text);
                // Подсказка про чаты — только когда секция chats реально подключена этим ходом.
                // Read-инструменты (list/history) — всегда; write (create/update/send) — при wsWrite.
                var chatsHint = _workspaceMcp.Sections.Contains("chats") && _currentTurnAgentDepth < 1
                    ? " Плюс чаты пользователя: chats_list, chats_history" +
                      (wsWrite
                        ? ", chats_create, chats_update (переименование) и chats_send — полноценный ход в " +
                          "другом чате от имени пользователя (результат виден ему в ленте)."
                        : ".")
                    : "";
                // Предупреждение про разрушающие операции — только когда секция destructive смонтирована
                var destructiveHint = _workspaceMcp.Sections.Contains("destructive") && _currentTurnAgentDepth < 1
                    ? " Разрушающие операции files_delete и chats_delete НЕВОССТАНОВИМЫ: применяй их ТОЛЬКО " +
                      "по явной просьбе пользователя удалить конкретный файл или чат, никогда по своей инициативе."
                    : "";
                // Git — только когда секция git смонтирована (идёт с files). Read всегда; write — при wsWrite.
                var gitHint = _workspaceMcp.Sections.Contains("git")
                    ? " Git любого проекта: git_status, git_diff, git_log, git_blame, git_file_log" +
                      (wsWrite ? ", а по явной просьбе — git_stage и git_commit." : ".")
                    : "";
                // Базы знаний Dify пользователя (личные и публичные, не проектные) — секция knowledge_bases.
                var kbHint = _workspaceMcp.Sections.Contains("knowledge_bases")
                    ? " Базы знаний пользователя: kb_list, kb_get, kb_search (семантика/полнотекст)" +
                      (wsWrite ? ", kb_add_document." : ".")
                    : "";
                var workspaceHint =
                    "Тебе доступно всё рабочее пространство пользователя через MCP-инструменты mcp__wsp__*: " +
                    "список проектов и их карточки (projects_list → projects_get), файлы любого проекта " +
                    "(files_tree, files_read, files_search), базы знаний проектов (knowledge_search, knowledge_status) " +
                    "и единый поиск по заметкам и задачам (search_unified)." +
                    (wsWrite
                        ? " Запись: projects_create/projects_update, files_write/files_mkdir/files_rename, " +
                          "knowledge_index (добавить файл в базу). files_write используй только для ДРУГИХ проектов."
                        : "") +
                    gitHint + kbHint + chatsHint + destructiveHint + " " + wsScope + " " +
                    "Когда пользователь спрашивает «где-то у меня было…» — начинай с search_unified." +
                    (wsWrite
                        ? ""
                        : " Инструменты записи (создать/изменить проект, записать файл в другой проект, " +
                          "создать/переименовать чат) появляются, когда пользователь явно об этом просит.") +
                    " Если вызов вернул «No such tool available» — сервер ещё подключается: " +
                    "подожди мгновение и повтори тот же вызов.";
                basePrompt = string.IsNullOrWhiteSpace(basePrompt)
                    ? workspaceHint
                    : basePrompt + "\n\n" + workspaceHint;
            }

            // Подсказка про долгую память. Персонная сессия — личная (memory_*) + командная (team_*);
            // обычный проектный чат без персоны — только память КОМАНДЫ проекта (team_memory_*).
            if (_memoryMcp is not null)
            {
                var hasPersonal = !string.IsNullOrEmpty(_memoryMcp.PersonaId);
                var hasTeam = !string.IsNullOrEmpty(_memoryMcp.ProjectId);
                string? memoryHint = hasPersonal
                    ? "У тебя есть долгая память между разговорами — управляй ей через MCP-инструменты mcp__memory__* " +
                      "(memory_remember, memory_search, memory_list, memory_rethink, memory_forget). Типы: semantic — устойчивые факты и " +
                      "предпочтения пользователя; episodic — что было/обсуждалось в прошлых разговорах; procedural — выученные " +
                      "приёмы и правила. Когда узнаёшь что-то важное о пользователе или договариваешься о чём-то на будущее — " +
                      "запоминай это (memory_remember). Когда нужно вспомнить контекст — ищи в памяти (memory_search). Записи можно " +
                      "не только добавлять и забывать: если факт изменился — не плоди дубль, а УТОЧНИ существующую запись по id " +
                      "через memory_rethink (перезапись текста)."
                    : null;
                if (hasTeam)
                {
                    var teamHint = hasPersonal
                        ? " Кроме личной памяти у тебя есть память КОМАНДЫ проекта — общие факты и договорённости, " +
                          "которые видят и могут править ВСЕ персоны команды (не только ты): "
                        : "У тебя есть общая память КОМАНДЫ проекта — факты, решения и договорённости, которые видят и " +
                          "используют все, кто работает в этом проекте: ";
                    teamHint +=
                        "mcp__memory__team_memory_remember (добавить общий факт/решение проекта), team_memory_list " +
                        "(посмотреть, что уже знает команда), team_memory_update (уточнить/переписать запись по id, " +
                        "когда общий факт изменился — вместо дубля), team_memory_forget (удалить устаревшее). Пиши туда то, что " +
                        "относится к проекту в целом" +
                        (hasPersonal
                            ? " и полезно другим персонам команды — а не то, что касается лично тебя (это остаётся в memory_remember)."
                            : " и полезно в дальнейшей работе над ним. Если пользователь просит «запомнить для команды/проекта» — используй team_memory_remember.");
                    memoryHint = memoryHint is null ? teamHint : memoryHint + teamHint;
                }
                if (memoryHint is not null)
                    basePrompt = string.IsNullOrWhiteSpace(basePrompt)
                        ? memoryHint
                        : basePrompt + "\n\n" + memoryHint;
            }

            // Подсказка про @упоминания (список «@handle — Роль (Имя)» + persona_ask) —
            // только при включённом флаге persona-mentions и наличии других персон
            if (_personasMcp?.MentionsHint is { } mentionsHint)
            {
                basePrompt = string.IsNullOrWhiteSpace(basePrompt)
                    ? mentionsHint
                    : basePrompt + "\n\n" + mentionsHint;
            }

            // Подсказка про субагентов-персон в Workflow: перечисляем handle'ы доступных
            // .md-агентов (из --add-dir) — модель должна знать, что их можно вызывать
            // через agentType в Task(agentType="handle", "prompt": "...") внутри workflow-скрипта.
            // ВАЖНО: добавляем ВСЕГДА. persona_ask — это одноразовый вопрос в чат, НЕ для Workflow.
            if (personaAgents is { AgentHandles.Count: > 0 })
            {
                var workflowHint =
                    "## Персоны-субагенты в Workflow\n" +
                    "У пользователя есть персоны-субагенты (файловые .md-агенты). " +
                    "Их можно вызывать в Workflow через Task(agentType=\"<handle>\", prompt=\"...\"). " +
                    "НЕ используй persona_ask (MCP-инструмент) для вызова внутри Workflow — " +
                    "persona_ask задаёт одноразовый вопрос в отдельный чат, а не запускает субагента. " +
                    "Для Workflow всегда используй Task(agentType=\"handle\").\n" +
                    "Доступные agentType: " + string.Join(", ", personaAgents.AgentHandles) + ".";
                basePrompt = string.IsNullOrWhiteSpace(basePrompt)
                    ? workflowHint
                    : basePrompt + "\n\n" + workflowHint;
            }

            // Auto-recall долгой памяти персоны: релевантные записи по тексту хода.
            // Независим от заметок; провайдер сам гейтит по MemoryEnabled/флагу, ошибки не роняют ход.
            // Заодно собираем манифест (что подтянулось) для «использовано сейчас» (F3).
            if (_personaRecallProvider is not null && _memoryMcp is not null)
            {
                RecallBlock? memRecall = null;
                try { memRecall = await _personaRecallProvider(text); }
                catch { /* recall памяти не должен ронять ход */ }
                if (!string.IsNullOrWhiteSpace(memRecall?.Text))
                    basePrompt = string.IsNullOrWhiteSpace(basePrompt)
                        ? memRecall.Text
                        : basePrompt + "\n\n" + memRecall.Text;
                if (memRecall?.Items.Count > 0)
                {
                    manifestItems ??= new List<RecallItem>();
                    manifestItems.AddRange(memRecall.Items);
                }
            }

            // Привязанные знания и правила персоны (флаг persona-bindings): индекс источников
            // «когда → откуда» + выжимки режима «всегда». Только у персонных сессий;
            // провайдер сам гейтит по флагу, ошибки не роняют ход.
            if (_bindingsProvider is not null && _personaPromptProvider is not null)
            {
                string? bindingsBlock = null;
                try { bindingsBlock = await _bindingsProvider(text); }
                catch { /* блок привязок не должен ронять ход */ }
                if (!string.IsNullOrWhiteSpace(bindingsBlock))
                    basePrompt = string.IsNullOrWhiteSpace(basePrompt)
                        ? bindingsBlock
                        : basePrompt + "\n\n" + bindingsBlock;
            }

            // Персональный слой: промпт персоны имеет приоритет
            // над .md-агентом — чат ведётся от её лица, характер задаёт именно персона.
            string? agentPrompt = _personaPromptProvider?.Invoke();
            if (agentPrompt is null && !string.IsNullOrEmpty(Info.AgentName) && _skills is not null)
                agentPrompt = _skills.GetAgentSystemPrompt(_rootPath, Info.AgentName);
            personaLayerPrompt = agentPrompt;

            var combinedPrompt = agentPrompt is not null
                ? (string.IsNullOrWhiteSpace(basePrompt)
                    ? agentPrompt
                    : basePrompt + "\n\n---\n\n" + agentPrompt)
                : basePrompt;

            if (!string.IsNullOrWhiteSpace(combinedPrompt))
                args.AddRange(["--append-system-prompt", combinedPrompt]);

            // Манифест recall (F3): что персона подтянула из памяти в этот ход — клиенту,
            // для «опирается на…» / «использовано сейчас» во вкладке контекста персоны.
            if (manifestItems is { Count: > 0 })
                _ = _onMessage(new RecallManifestMessage(
                    manifestItems.Select(i => new RecallItemDto(i.Kind, i.Ref, i.Title, i.Snippet)).ToList()));
        }

        // Env-оверрайды процесса собираем заранее (не сразу в psi): пары входят в сигнатуру прогона
        var envOverrides = new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            // claude --print по умолчанию ждёт фоновые задачи (субагентов workflow) не дольше 600с,
            // затем принудительно завершается: «Background tasks still running after 600s; terminating».
            // Из-за этого длинные workflow обрывались на 10-й минуте, не доходя до конца. 0 = ждать без
            // ограничения по времени; нас страхует watchdog (см. WatchdogFor).
            ["CLAUDE_CODE_PRINT_BG_WAIT_CEILING_MS"] = "0",

            // Даём MCP-серверам больше времени на подключение при старте хода: с дефолтным
            // таймаутом медленно стартующий node-сервер (personas и др.) не успевал
            // зарегистрировать тулы, и первый же вызов падал «No such tool available»
            // (модель ретраила, но карточка ошибки засоряла ленту).
            ["MCP_TIMEOUT"] = "30000",
        };

        // Сторонний провайдер (DeepSeek/GLM): перенаправляем CLI на его Anthropic-совместимый
        // эндпоинт. Env считаются каждый ход — модель сессии могла смениться между ходами.
        var cliEnv = _providers?.BuildCliEnv(Info.Model);
        if (cliEnv is not null)
        {
            foreach (var (k, v) in cliEnv)
                envOverrides[k] = v;
        }
        else if (_subscriptionPool?.HasExtra == true
            && _providers?.GetByKey(Info.Provider) is null)
        {
            // Подписка пула (включая "claude", если задана с токеном) — свой OAuth-профиль и
            // токен. Если ключ не найден в пуле (локальный режим — пул пуст, HasExtra=false, сюда
            // не входим) — оверрайдов нет, ход идёт по ~/.claude/.credentials.json (вход без ключа).
            var sub = _subscriptionPool.All.FirstOrDefault(s => s.Key == Info.Provider);
            if (sub?.Enabled == true)
            {
                var oauthEnv = _providers?.BuildOAuthCliEnv(sub.Key, sub.OAuthToken, sub.ApiKey, Info.Model);
                if (oauthEnv is not null)
                    foreach (var (k, v) in oauthEnv)
                        envOverrides[k] = v;
            }
        }

        // Сообщение хода: с картинками content — массив блоков (text + image base64), иначе строка
        var imageBlocks = BuildImageBlocks(imagePaths);
        object content;
        if (imageBlocks.Count == 0)
        {
            content = text;
        }
        else
        {
            var blocks = new List<object> { new { type = "text", text } };
            blocks.AddRange(imageBlocks);
            content = blocks;
        }
        var userMessageJson = JsonSerializer.Serialize(new
        {
            type = "user",
            message = new { role = "user", content }
        });

        var signature = BuildLaunchSignature(args, mcpServerKeys, envOverrides, personaLayerPrompt);

        // Same-process ход: прогон дожил с прошлого хода (фоновые агенты ещё работают),
        // окружение не изменилось — отдаём сообщение живому процессу в stdin, агенты
        // переживают смену хода. Собранный temp MCP-конфиг не пригодился — убираем.
        var existing = _run;
        if (existing is not null && existing.TurnDone && existing.Signature == signature
            && TrySubmitTurn(existing, userMessageJson))
        {
            Console.WriteLine("[ClaudeSession] Ход отдан живому процессу прогона (фоновые агенты доживают)");
            if (turnMcpPath != null)
                try { File.Delete(turnMcpPath); }
                catch (Exception ex)
                {
                    // Финализация прогона удалит только конфиг ПЕРВОГО хода — этот файл с
                    // сервисным токеном больше никто не приберёт, важно хотя бы знать
                    Console.Error.WriteLine($"[ClaudeSession] Не удалось удалить temp MCP-конфиг same-process хода {turnMcpPath}: {ex.Message}");
                }
            await existing.TurnTcs.Task.WaitAsync(ct);
            return;
        }

        // Живой, но несовместимый прогон (сменились модель/режим/персона/env — или запись
        // в stdin сорвалась): убиваем и дожидаемся финализации. Его фоновые агенты гибнут —
        // осознанная плата за смену окружения; CLI сообщит о них notification'ом на resume.
        // ExitedMessage прогона подавляем: статусом сессии владеет новый ход.
        if (existing is not null)
        {
            existing.SuppressExited = true;
            _launcher.Kill(existing.Process, existing.LaunchTurnId);
            if (existing.ReaderTask is { } prevReader)
                try { await prevReader.WaitAsync(TimeSpan.FromSeconds(15), CancellationToken.None); }
                catch (TimeoutException) { /* финализация зависла — не блокируем новый ход */ }
        }

        // claude.exe пишет/читает UTF-8. Без явной кодировки .NET берёт системную
        // OEM code page (напр. CP866 на русской Windows) → кракозябры в ответах.
        // Задаём UTF-8 без BOM (BOM сломал бы первое сообщение в stdin).
        var utf8NoBom = new System.Text.UTF8Encoding(false);

        _currentTurnId = Guid.NewGuid().ToString("N")[..12];
        // ArgumentList/Args экранирует каждый аргумент корректно (важно для многострочного
        // системного промпта); env-оверрайды собраны выше — они входят в сигнатуру прогона
        var process = _launcher.Start(new Execution.ProcessSpec
        {
            FileName = _launcher.ClaudeCliCommand,
            Args = args,
            WorkingDirectory = _rootPath,
            Env = envOverrides,
            StdioEncoding = utf8NoBom,
            TurnId = _currentTurnId,
        });
        _currentProcess = process;

        CliRun run;
        try
        {
            if (process.HasExited)
                throw new InvalidOperationException("claude мгновенно завершился при старте");

            _fileWatcher.Start();

            run = new CliRun { Process = process, Signature = signature, TurnMcpPath = turnMcpPath, LaunchTurnId = _currentTurnId, PromptSuggestionsActive = promptSuggestionsActive };
            // Читаем stderr асинхронно, иначе при переполнении буфера процесс зависнет
            run.StderrTask = process.StandardError.ReadToEndAsync(ct);

            // stdin оставляем открытым — claude пишет control_response в него при permission-запросах
            await _stdinLock.WaitAsync(ct);
            try
            {
                await process.StandardInput.WriteLineAsync(userMessageJson);
                await process.StandardInput.FlushAsync();
            }
            finally { _stdinLock.Release(); }

            _run = run;
        }
        catch
        {
            // Прогон не собрался: reader не стартовал, финализировать некому — прибираем сами
            // и завершаем ход ExitedMessage, иначе статус сессии застрянет в Working
            _fileWatcher.Stop();
            _launcher.Kill(process, _currentTurnId);
            process.Dispose();
            _currentProcess = null;
            if (turnMcpPath != null)
                try { File.Delete(turnMcpPath); }
                catch { /* temp-каталог приберёт ОС */ }
            await _onMessage(new ExitedMessage());
            throw;
        }

        // Reader живёт дольше хода — до смерти процесса (доживание фоновых агентов);
        // финализация прогона (ватчеры, temp-конфиг, ExitedMessage) — на нём
        run.ReaderTask = Task.Run(() => ReadLoopAsync(run, _cts.Token), CancellationToken.None);

        await run.TurnTcs.Task.WaitAsync(ct);

        // Без живых фоновых задач сохраняем прежнюю семантику хода: возвращаемся, когда процесс
        // умер и финализирован (ExitedMessage послан до release _turnLock). С ними — сразу:
        // прогон доживает, чат остаётся Active, ExitedMessage пошлёт финализация.
        if (!run.HasPendingBg && run.ReaderTask is { } reader)
            await reader.WaitAsync(ct);
    }

    // Цикл чтения stdout прогона. Принадлежит прогону, не ходу: переживает result и
    // продолжает транслировать события доживающих фоновых агентов и ходов-продолжений CLI
    private async Task ReadLoopAsync(CliRun run, CancellationToken ct)
    {
        // Ридер stdout переиспользуется между итерациями: нельзя запустить второе чтение
        // того же потока, пока предыдущее не завершилось. При срабатывании watchdog чтение
        // остаётся висеть — убийство процесса закроет stdout и разблокирует его.
        Task<string?>? pendingRead = null;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var armedTurnDone = run.TurnDone;
                pendingRead ??= run.Process.StandardOutput.ReadLineAsync(ct).AsTask();

                // Watchdog через гонку «строка против таймера», а НЕ через отмену ReadLineAsync:
                // на Windows-пайпе токен НЕ прерывает уже начатое чтение молчащего stdout, поэтому
                // старый watchdogCts.CancelAfter не срабатывал и зависший процесс жил часами.
                string? line;
                using (var delayCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
                {
                    var timeout = WatchdogFor(run);
                    var completed = await Task.WhenAny(pendingRead, Task.Delay(timeout, delayCts.Token));
                    if (completed != pendingRead)
                    {
                        // Тишина дольше таймаута. Пока ждали, мог начаться same-process ход —
                        // тогда переармируем (чтение держим, оно ещё валидно)
                        if (armedTurnDone && !run.TurnDone) continue;
                        if (!run.TurnDone)
                        {
                            // Активный ход молчит дольше допустимого (генерация оборвана,
                            // инструмент завис ИЛИ result хода проглочен корреляцией — тогда
                            // индикатор залипал до этого момента) — прерываем с ошибкой, спиннер снимется
                            CorrTrace($"watchdog-timeout({timeout.TotalMinutes:0}min)", Info.Id, run);
                            await _onMessage(new ErrorMessage(
                                $"Модель не отвечает более {timeout.TotalMinutes:0} мин — ход прерван"));
                        }
                        else if (run.HasPendingBg)
                            Console.Error.WriteLine(
                                $"[ClaudeSession] Фоновые агенты не завершились за {_bgLingerTimeout.TotalMinutes:0} мин тишины — завершаем процесс");
                        // Иначе: result отдан, фоновых задач нет — процесс держат плагинные
                        // хуки/мосты (наблюдалось с oh-my-claudecode), гасим молча
                        _launcher.Kill(run.Process, run.LaunchTurnId);
                        // Добираем висящее чтение: kill закрыл stdout → оно завершится (null/ошибка),
                        // без await остался бы unobserved-таск
                        try { await pendingRead; } catch { /* пайп закрыт убийством — ожидаемо */ }
                        break;
                    }
                    delayCts.Cancel(); // чтение выиграло — гасим таймер, иначе на активном стриме
                                       // копились бы тысячи висящих Task.Delay
                    line = await pendingRead;
                    pendingRead = null; // следующей итерации нужно новое чтение
                }

                if (line is null) break; // stdout закрыт — процесс завершился
                if (string.IsNullOrWhiteSpace(line)) continue;
                await ProcessLineAsync(run, line);
            }
        }
        catch (OperationCanceledException) { /* отмена сессии — штатно */ }
        catch (Exception ex)
        {
            // Полный стек — иначе не видно, ГДЕ упал разбор (напр. небезопасное чтение числа
            // из stream-json стороннего провайдера). Message в одиночку тут бесполезен.
            Console.Error.WriteLine($"[ClaudeSession] Цикл чтения прогона упал: {ex}");
            // Активный ход из-за краха цикла не дождётся result — без явной ошибки клиенту
            // UI навсегда завис бы на «Размышление…» (ExitedMessage из finally не гасит плашку
            // размышления). Шлём ошибку, чтобы ход честно завершился.
            if (!run.TurnDone)
                try { await _onMessage(new ErrorMessage("Ход прерван из-за ошибки обработки ответа модели")); }
                catch { /* сообщение клиенту best-effort */ }
        }
        finally { await FinalizeRunAsync(run); }
    }

    // Допустимая тишина stdout по состоянию прогона. Активный ход — щедрый IdleTimeout (60 мин):
    // при активном ходе молчание stdout почти всегда легитимно (CLI выполняет инструмент/субагента,
    // ждёт ответа пользователя, сжимает контекст, медленно генерирует или ретраит провайдера) —
    // короткий таймаут по любому из этих состояний ложно рубил бы ход, а надёжно отличить их от
    // обрыва по одному таймауту нельзя. Реальный обрыв провайдера отлавливается иначе: если CLI
    // сам завершится/упадёт — это увидит цикл чтения (result/EOF/исключение → ErrorMessage
    // клиенту), а IdleTimeout — лишь крайняя защита от вечно висящего процесса.
    // Доживание с фоновыми агентами — потолок BgLingerTimeout; иначе — короткий грейс выхода CLI.
    private TimeSpan WatchdogFor(CliRun run) =>
        !run.TurnDone ? IdleTimeout
        : run.HasPendingBg ? _bgLingerTimeout
        : run.PromptSuggestionsActive ? PromptSuggestionExitGrace
        : ResultExitGrace;

    // Финализация прогона: единственная точка уборки после смерти процесса
    private async Task FinalizeRunAsync(CliRun run)
    {
        CloseStdin(run);
        // Всегда убиваем процесс. На Windows дочерние node-процессы MCP-серверов
        // НЕ завершаются автоматически при выходе родителя — без явного Kill с
        // entireProcessTree они остаются сиротами и копятся сутками, съедая память.
        // На POSIX Kill уже мёртвого процесса — no-op (ловим внутри метода).
        _launcher.Kill(run.Process, run.LaunchTurnId);
        if (!run.Process.HasExited)
        {
            // Ограниченное ожидание завершения — Kill() асинхронен на некоторых ОС
            using var exitCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            try { await run.Process.WaitForExitAsync(exitCts.Token); }
            catch (OperationCanceledException) { } // 10 с истекло — идём дальше
        }
        if (run.StderrTask is { } stderrTask)
            try
            {
                var stderr = await stderrTask;
                if (!string.IsNullOrWhiteSpace(stderr))
                    Console.Error.WriteLine($"[ClaudeSession stderr] {stderr.Trim()}");
            }
            catch (OperationCanceledException) { /* сессия отменена — stderr уже не важен */ }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[ClaudeSession] Чтение stderr не удалось: {ex.Message}");
            }
        run.Process.Dispose();
        if (ReferenceEquals(_currentProcess, run.Process)) _currentProcess = null;
        // Прогон всё ещё «текущий»? Если его уже заместил новый (несовместимый ход убил
        // старый, финализация опоздала) — общие пер-сессионные ресурсы (file watcher,
        // сабагент-ватчер, tailer) принадлежат новому ходу, их не трогаем
        var wasCurrent = ReferenceEquals(Interlocked.CompareExchange(ref _run, null, run), run);

        if (run.TurnMcpPath != null)
            try { File.Delete(run.TurnMcpPath); }
            catch (Exception ex)
            {
                // В temp-конфиге сервисный токен — важно знать, если он не удалился
                Console.Error.WriteLine($"[ClaudeSession] Не удалось удалить temp MCP-конфиг {run.TurnMcpPath}: {ex.Message}");
            }

        // Процесс мёртв — живых workflow в нём нет: недобитые ватчеры ЭТОГО прогона
        // закрываем финальным isDone (Interrupt мог успеть раньше — повторный AbortAsync
        // no-op), потом чистим список. Чужие (нового прогона при опоздавшей финализации
        // замещённого) не трогаем — их workflow живы.
        List<WorkflowWatcher> lingeringWatchers;
        lock (_workflowWatchers)
            lingeringWatchers = _workflowWatchers
                .Where(w => !w.IsDisposed && (w.Owner is null || ReferenceEquals(w.Owner, run)))
                .ToList();
        foreach (var w in lingeringWatchers) await w.AbortAsync();
        lock (_workflowWatchers) _workflowWatchers.RemoveAll(w => w.IsDisposed);

        if (wasCurrent)
        {
            _fileWatcher.Stop();
            // Дочитываем хвосты транскриптов сабагентов и останавливаем ватчеры прогона
            if (_subagentWatcher is not null)
            {
                await _subagentWatcher.DrainAsync();
                _subagentWatcher.Dispose();
                _subagentWatcher = null;
            }
            _transcriptTailer?.Dispose();
            _transcriptTailer = null;
        }

        // Фоновые задачи, не успевшие завершиться, умерли вместе с процессом —
        // закрываем их карточки, иначе UI ждал бы «ответ готовится» вечно
        List<string> orphanedTools;
        lock (run.PendingBg)
        {
            orphanedTools = run.PendingBg.Values.Where(v => !string.IsNullOrEmpty(v))
                .Concat(run.UnknownBgToolUses).Distinct().ToList();
            run.PendingBg.Clear();
            run.UnknownBgToolUses.Clear();
        }
        run.PendingBgUnknown = false;
        if (orphanedTools.Count > 0)
            await _onMessage(new BgAgentDoneMessage(orphanedTools, Aborted: true));

        // Ход, ждущий result, его уже не дождётся — процесс умер: резолвим, чтобы
        // RunTurnAsync не завис (обрыв пользователю виден по ExitedMessage/статусу)
        run.TurnDone = true;
        run.TurnTcs.TrySetResult();

        // Статусом владеет SessionManager: Finished/Active он выставит по ExitedMessage
        if (!run.SuppressExited)
            await _onMessage(new ExitedMessage());
    }

    // Сигнатура окружения прогона — жёсткая часть запуска процесса. Совпала у следующего
    // хода → его можно отдать живому процессу в stdin; отличие → новый процесс.
    // Исключены изменчивые на каждый ход части: --resume (не меняется в рамках сессии),
    // путь temp MCP-конфига (вместо него набор ключей серверов + отпечаток СОСТАВА их
    // инструментов — write/mentions/секции; токены/URL из содержимого намеренно опущены как
    // изменчивые) и системный промпт целиком — из него в сигнатуре только слой персоны,
    // recall/подсказки деградируют мягко.
    private static string BuildLaunchSignature(
        IReadOnlyList<string> args, string mcpServerKeys,
        IReadOnlyDictionary<string, string> envOverrides, string? personaLayerPrompt)
    {
        var sb = new System.Text.StringBuilder();
        for (var i = 0; i < args.Count; i++)
        {
            if (args[i] is "--resume" or "--mcp-config" or "--append-system-prompt") { i++; continue; }
            sb.Append(args[i]).Append('\u0001');
        }
        sb.Append("mcp=").Append(mcpServerKeys).Append('\u0001');
        foreach (var (k, v) in envOverrides) sb.Append(k).Append('=').Append(v).Append('\u0001');
        if (!string.IsNullOrEmpty(personaLayerPrompt))
            sb.Append("persona=").Append(personaLayerPrompt);
        return sb.ToString();
    }

    // Трассировка корреляции result↔ход (диагностика залипающего индикатора «дымящийся домик»):
    // по этим строкам на реальном залипшем кейсе восстанавливается ФАКТИЧЕСКИЙ порядок событий
    // CLI (сливает ли он ход-продолжение с пользовательским ходом или всегда выдаёт отдельный
    // result) — без этого нельзя отличить отказной сценарий проглатывания от штатного пропуска.
    private static void CorrTrace(string ev, string sid, CliRun? run, JsonElement? root = null)
    {
        var nt = root is { } r && r.TryGetProperty("num_turns", out var n) && n.ValueKind == JsonValueKind.Number
            ? n.GetInt32() : -1;
        Console.WriteLine(
            $"[ClaudeSession][corr] {ev} sid={sid} turnDone={run?.TurnDone} "
            + $"skip={(run is null ? -1 : Volatile.Read(ref run.SkipResults))} "
            + $"cont={run?.ContinuationActive} bg={run?.HasPendingBg} numTurns={nt}");
    }

    // run — прогон-владелец read-loop'а, из которого пришла строка. Корреляцию ведём по нему,
    // а НЕ по полю _run: после механики доживания _run мог быть заменён новым прогоном, пока
    // старый reader дочитывает хвост — тогда поздние строки замещённого прогона (в т.ч. его
    // result) попадали бы в чужой CliRun.
    private async Task ProcessLineAsync(CliRun run, string line)
    {
        // Невалидный JSON от CLI не должен убивать весь turn
        JsonDocument doc;
        try { doc = JsonDocument.Parse(line); }
        catch (JsonException) { return; }

        using (doc)
        {
        var root = doc.RootElement;

        if (!root.TryGetProperty("type", out var typeProp)) return;

        switch (typeProp.GetString())
        {
            case "system":
                var sysSubtype = root.TryGetProperty("subtype", out var sst) ? sst.GetString() : null;
                if (sysSubtype == "init" && root.TryGetProperty("session_id", out var sid))
                {
                    var isResume = Info.ClaudeSessionId is not null;
                    Info.ClaudeSessionId = sid.GetString();
                    var model = root.TryGetProperty("model", out var m) ? m.GetString() ?? "" : "";
                    var cwd = root.TryGetProperty("cwd", out var cw) && cw.ValueKind == JsonValueKind.String ? cw.GetString() : null;
                    var toolCount = root.TryGetProperty("tools", out var tl) && tl.ValueKind == JsonValueKind.Array ? tl.GetArrayLength() : 0;
                    List<McpServerInfo>? mcp = null;
                    if (root.TryGetProperty("mcp_servers", out var ms) && ms.ValueKind == JsonValueKind.Array)
                    {
                        mcp = [];
                        foreach (var s in ms.EnumerateArray())
                        {
                            var name = s.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                            var status = s.TryGetProperty("status", out var st2) ? st2.GetString() ?? "" : "";
                            if (name.Length > 0) mcp.Add(new McpServerInfo(name, status));
                        }
                    }
                    await _onMessage(new SessionStartedMessage(
                        Info.ClaudeSessionId!, isResume, model, Info.Mode.ToWireToken(), cwd, toolCount, mcp,
                        Capabilities.Provider, Capabilities));

                    // Поток inline-сабагентов этого хода — из их транскриптов на диске.
                    // Same-process ход (init повторяется в том же процессе, контекст тот же) —
                    // ватчер переиспользуем: пересоздание помечало бы файлы «прочитанными
                    // целиком» и теряло хвост текста доживающих агентов. Иной контекст —
                    // дочитываем хвост (Drain) и только потом пересоздаём.
                    if (_subagentWatcher is null || !_subagentWatcher.Matches(cwd ?? _rootPath, Info.ClaudeSessionId!))
                    {
                        if (_subagentWatcher is not null)
                        {
                            await _subagentWatcher.DrainAsync();
                            _subagentWatcher.Dispose();
                        }
                        _subagentWatcher = new SubagentStreamWatcher(cwd ?? _rootPath, Info.ClaudeSessionId!, _onMessage);
                        _subagentWatcher.Start();
                    }

                    // Ридер notification'ов — один на прогон (init повторяется на каждом ходе
                    // нового CLI; пересоздание сбросило бы офсет и пропустило завершения)
                    if (_transcriptTailer is null)
                    {
                        _transcriptTailer = new MainTranscriptTailer(
                            cwd ?? _rootPath, Info.ClaudeSessionId!, HandleTaskNotification);
                        _transcriptTailer.Start();
                    }
                }
                else if (sysSubtype == "compact_boundary")
                {
                    // Claude свернул контекст — показываем разделитель
                    var meta = root.TryGetProperty("compact_metadata", out var cm) ? cm : default;
                    var trigger = meta.ValueKind == JsonValueKind.Object && meta.TryGetProperty("trigger", out var tr)
                        ? tr.GetString() ?? "auto" : "auto";
                    int? preTokens = meta.ValueKind == JsonValueKind.Object
                        && meta.TryGetProperty("pre_tokens", out var pt) && pt.TryGetInt32(out var ptv) ? ptv : null;
                    int? postTokens = meta.ValueKind == JsonValueKind.Object
                        && meta.TryGetProperty("post_tokens", out var pst) && pst.TryGetInt32(out var pstv) ? pstv : null;
                    await _onMessage(new CompactBoundaryMessage(trigger, preTokens, postTokens));
                }
                else if (sysSubtype == "status")
                {
                    // Ход компакции: status=="compacting" — началась; compact_result — завершилась
                    var status = root.TryGetProperty("status", out var stv) && stv.ValueKind == JsonValueKind.String
                        ? stv.GetString() : null;
                    var compactResult = root.TryGetProperty("compact_result", out var crv) && crv.ValueKind == JsonValueKind.String
                        ? crv.GetString() : null;
                    var compactError = root.TryGetProperty("compact_error", out var cev) && cev.ValueKind == JsonValueKind.String
                        ? cev.GetString() : null;
                    if (status == "compacting" || compactResult is not null)
                        await _onMessage(new CompactStatusMessage(status, compactResult, compactError));
                }
                break;

            case "stream_event":
                // Контент ОСНОВНОГО агента после конца хода — CLI начал ход-продолжение
                // (ответ на task-notification); его result не должен завершить будущий ход
                // (см. case "result"). Сообщения сабагентов (parent_tool_use_id) — это стрим
                // доживающих фоновых агентов, а не продолжение.
                if (run is { TurnDone: true, ContinuationActive: false } && !HasParentToolUseId(root))
                {
                    CorrTrace("continuation-start(stream_event)", Info.Id, run, root);
                    run.ContinuationActive = true;
                }
                await HandleStreamEventAsync(root);
                break;

            case "assistant":
                if (run is { TurnDone: true, ContinuationActive: false } && !HasParentToolUseId(root))
                {
                    CorrTrace("continuation-start(assistant)", Info.Id, run, root);
                    run.ContinuationActive = true;
                }
                TrackContextTokens(root);
                await HandleAssistantToolsAsync(run, root);
                break;

            case "result":
                // Результаты субагентов имеют parent_tool_use_id — не завершаем сессию по ним
                if (root.TryGetProperty("parent_tool_use_id", out var rPid) && rPid.ValueKind == JsonValueKind.String)
                    break;
                // Корреляция result ↔ ход: между ходами CLI ведёт собственные ходы-продолжения
                // (ответы на task-notification) со своими result'ами — их нельзя засчитывать
                // пользовательскому ходу. TurnDone=true → продолжение между ходами (ход никто
                // не ждёт); SkipResults>0 → продолжение шло в момент отправки текущего хода,
                // его result приходит первым (stdout последовательный) — пропускаем, result
                // самого хода будет следующим.
                {
                    var contRun = run;
                    if (contRun.TurnDone)
                    {
                        CorrTrace("result-skip(turnDone)", Info.Id, contRun, root);
                        contRun.ContinuationActive = false;
                        Console.WriteLine("[ClaudeSession] Result хода-продолжения CLI между ходами — пропущен");
                        CloseStdinIfIdle(contRun);
                        break;
                    }
                    if (Volatile.Read(ref contRun.SkipResults) > 0)
                    {
                        CorrTrace("result-skip(skipResults)", Info.Id, contRun, root);
                        Interlocked.Decrement(ref contRun.SkipResults);
                        Console.WriteLine("[ClaudeSession] Result хода-продолжения CLI при ожидающем ходе — пропущен");
                        break;
                    }
                    CorrTrace("result-emit", Info.Id, contRun, root);
                }
                var subtype = root.TryGetProperty("subtype", out var st) ? st.GetString() ?? "success" : "success";
                // Числа читаем через безопасные хелперы: openrouter-совместимый поток шлёт
                // эти поля как JSON null (Anthropic — всегда число), а прямой GetInt64/GetDouble
                // на null кидает и роняет весь цикл чтения прогона (ход виснет без ответа).
                var durationMs = LongProp(root, "duration_ms");
                var numTurns = IntProp(root, "num_turns");
                var totalCost = DoubleProp(root, "total_cost_usd");
                var apiErr = root.TryGetProperty("api_error_status", out var ae) && ae.ValueKind == JsonValueKind.String
                    ? ae.GetString() : null;
                List<string>? denials = null;
                if (root.TryGetProperty("permission_denials", out var pd) && pd.ValueKind == JsonValueKind.Array && pd.GetArrayLength() > 0)
                {
                    denials = [];
                    foreach (var x in pd.EnumerateArray())
                        denials.Add(x.TryGetProperty("tool_name", out var tnm) ? tnm.GetString() ?? "?" : "?");
                }
                var usage = ParseUsage(root);
                // На стороннем эндпоинте CLI считает total_cost_usd по ценам Anthropic —
                // пересчитываем по ценам конфига модели (нет цен → стоимость не показываем)
                if (_providers is not null && _providers.ResolveByModel(Info.Model) is not null)
                    totalCost = _providers.ComputeCost(Info.Model, usage);
                // API-ошибка (напр. 429 у провайдера): CLI отдаёт subtype=success, но is_error=true
                // и текст в result; синтетический assistant-текст не стримится дельтами —
                // без этого пользователь увидел бы пустой «успешный» ход
                if (root.TryGetProperty("is_error", out var isErr) && isErr.ValueKind == JsonValueKind.True
                    && root.TryGetProperty("result", out var resText) && resText.ValueKind == JsonValueKind.String
                    && !string.IsNullOrWhiteSpace(resText.GetString()))
                    await _onMessage(new ErrorMessage(resText.GetString()!));
                // Статус Error/Active выставит SessionManager по ResultMessage
                var ctxTokens = _lastContextTokens > 0 ? _lastContextTokens : (int?)null;
                await _onMessage(new ResultMessage(subtype, durationMs, numTurns, usage, totalCost, apiErr, denials, ctxTokens));
                // Ход завершён. Без живых фоновых задач закрываем stdin — CLI выйдет сам,
                // дальше ждём его не дольше ResultExitGrace. С ними stdin держим открытым:
                // прогон доживает (агенты работают внутри процесса) и готов принять
                // следующий совместимый ход. Result'ы ходов-продолжений CLI сюда не доходят —
                // отфильтрованы корреляцией выше.
                {
                    var doneRun = run;
                    doneRun.TurnDone = true;
                    if (!doneRun.HasPendingBg) CloseStdin(doneRun);
                    else
                    {
                        int pendingCount;
                        lock (doneRun.PendingBg) pendingCount = doneRun.PendingBg.Count;
                        Console.WriteLine(
                            $"[ClaudeSession] Ход завершён, прогон доживает: фоновых задач {pendingCount}"
                            + (doneRun.PendingBgUnknown ? " (+неучтённые)" : ""));
                    }
                    doneRun.TurnTcs.TrySetResult();
                }
                // Гарантия исполнения одобренного плана: если ход завершился, а Claude так и не
                // приступил к правкам — дошлём команду на реализацию (следующий ход — без plan-режима)
                if (_awaitPlanExecution)
                {
                    var needFollowUp = !_sawToolSinceApprove && subtype != "error";
                    _awaitPlanExecution = false;
                    if (needFollowUp)
                    {
                        _forceNonPlanNextTurn = true;
                        _ = SendMessageAsync("Одобренный план согласован. Реализуй его полностью сейчас — без повторного планирования.");
                    }
                }
                break;

            case "user":
                await HandleUserMessageAsync(run, root);
                break;

            case "sdk_control_request":
                await HandlePermissionAsync(root);
                break;

            case "control_request":
                await HandleControlRequestAsync(root);
                break;

            case "rate_limit_event":
                await HandleRateLimitAsync(root);
                break;

            case "prompt_suggestion":
                await HandlePromptSuggestionAsync(root);
                break;
        }
        } // using (doc)
    }

    // Подсказка следующего сообщения (--prompt-suggestions): формат поля с текстом
    // официально не документирован — парсим снисходительно (suggestion/prompt/text,
    // строка или объект с теми же полями), непонятный payload пропускаем молча.
    private async Task HandlePromptSuggestionAsync(JsonElement root)
    {
        var text = ExtractSuggestionText(root);
        if (!string.IsNullOrWhiteSpace(text))
        {
            Console.WriteLine($"[ClaudeSession] prompt_suggestion: {Truncate(text.Trim(), 120)}");
            await _onMessage(new PromptSuggestionMessage(text.Trim()));
        }
        else
            // Формат события не документирован: если CLI сменил имя поля — увидим в логе
            Console.WriteLine($"[ClaudeSession] prompt_suggestion без распознанного текста: {Truncate(root.GetRawText(), 300)}");
    }

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s[..max] + "…";

    private static string? ExtractSuggestionText(JsonElement root)
    {
        foreach (var key in (string[])["suggestion", "prompt", "text"])
        {
            if (!root.TryGetProperty(key, out var prop)) continue;
            switch (prop.ValueKind)
            {
                case JsonValueKind.String:
                    return prop.GetString();
                case JsonValueKind.Object:
                    var nested = ExtractSuggestionText(prop);
                    if (nested is not null) return nested;
                    break;
            }
        }
        return null;
    }

    // Сообщение принадлежит сабагенту (у CLI помечено parent_tool_use_id)
    private static bool HasParentToolUseId(JsonElement root) =>
        root.TryGetProperty("parent_tool_use_id", out var pid) && pid.ValueKind == JsonValueKind.String;

    // Мягкий лимит API: claude шлёт rate_limit_event и приостанавливается до сброса окна.
    // Разбор вынесен в ClaudeRateLimitParser (общий со стартовым прогревом подписок).
    private async Task HandleRateLimitAsync(JsonElement root)
    {
        if (ClaudeRateLimitParser.TryParse(root, out var msg))
            await _onMessage(msg);
    }

    private async Task HandleUserMessageAsync(CliRun run, JsonElement root)
    {
        if (!root.TryGetProperty("message", out var msg)) return;
        if (!msg.TryGetProperty("content", out var content)) return;
        // Строковый content — служебные user-сообщения CLI (summary после компакта,
        // <local-command-stdout>, task-notification): не tool_result, в ленту не транслируем.
        // task-notification — завершение фоновой задачи, вычёркиваем её из pending прогона
        if (content.ValueKind == JsonValueKind.String)
        {
            HandleTaskNotification(content.GetString());
            return;
        }
        if (content.ValueKind != JsonValueKind.Array) return;

        foreach (var block in content.EnumerateArray())
        {
            if (!block.TryGetProperty("type", out var bt)) continue;
            if (bt.GetString() != "tool_result") continue;

            var toolUseId = block.TryGetProperty("tool_use_id", out var tuid) ? tuid.GetString() ?? "" : "";
            var isError = block.TryGetProperty("is_error", out var ie) && ie.GetBoolean();

            var resultContent = "";
            if (block.TryGetProperty("content", out var c))
            {
                if (c.ValueKind == JsonValueKind.String)
                    resultContent = c.GetString() ?? "";
                else if (c.ValueKind == JsonValueKind.Array)
                {
                    var sb = new System.Text.StringBuilder();
                    foreach (var cb in c.EnumerateArray())
                        if (cb.TryGetProperty("text", out var t))
                            sb.AppendLine(t.GetString());
                    resultContent = sb.ToString().TrimEnd();
                }
            }

            // Дочитываем транскрипты сабагентов ДО трансляции результата: весь текст сабагента
            // должен лечь в ленту раньше tool_result (и продолжения текста основного агента)
            if (_subagentWatcher is not null) await _subagentWatcher.DrainAsync();

            await _onMessage(new ToolResultMessage(toolUseId, resultContent, isError));

            // Запуск фоновой задачи (async-агент, resume через SendMessage, workflow) —
            // берём её id на учёт прогона: пока pending не пуст, процесс переживает ход
            TrackBgLaunch(run, toolUseId, resultContent, isError);

            // Обратная сторона: модель сама опросила результат фоновой задачи через TaskOutput —
            // это сигнал её завершения (Kimi и др. не ждут task-notification)
            if (!isError) HandleTaskOutputCompletion(run, resultContent);

            // Если это результат Workflow с транскриптом — запускаем watcher
            if (!isError && resultContent.Contains("Transcript dir:"))
            {
                var m = System.Text.RegularExpressions.Regex.Match(resultContent, @"Transcript dir:\s*(.+)");
                if (m.Success)
                {
                    var transcriptDir = m.Groups[1].Value.Trim();
                    Console.WriteLine($"[WorkflowWatcher] старт: dir={transcriptDir} allowed={WorkflowAgentParser.IsPathAllowed(transcriptDir)}");
                    var watcher = new WorkflowWatcher(transcriptDir, toolUseId, _onMessage) { Owner = run };
                    lock (_workflowWatchers)
                    {
                        // Завершившиеся ватчеры диспозятся сами — чистим список, чтобы не рос
                        _workflowWatchers.RemoveAll(w => w.IsDisposed);
                        _workflowWatchers.Add(watcher);
                    }
                    watcher.Start();
                }
            }
        }
    }

    // Паттерны учёта фоновых задач. Id агентов у CLI бывают и hex (a4faf5af…), и base36
    // (br4ihb0jl) — берём общий алфавит [0-9a-zA-Z_-] (проверено live на CLI 2.1.212+)
    private static readonly System.Text.RegularExpressions.Regex BgAgentIdRe =
        new(@"agentId:\s*([0-9a-zA-Z_-]{6,})", System.Text.RegularExpressions.RegexOptions.Compiled);
    private static readonly System.Text.RegularExpressions.Regex BgResumedRe =
        new(@"Agent ""([0-9a-zA-Z_-]{6,})""", System.Text.RegularExpressions.RegexOptions.Compiled);
    private static readonly System.Text.RegularExpressions.Regex BgWorkflowRe =
        new(@"runId:\s*(wf_[0-9a-zA-Z_-]{4,})", System.Text.RegularExpressions.RegexOptions.Compiled);
    private static readonly System.Text.RegularExpressions.Regex TaskIdRe =
        new(@"<task-id>([^<]+)</task-id>", System.Text.RegularExpressions.RegexOptions.Compiled);
    // Опрос результата фоновой задачи инструментом TaskOutput: его tool_result несёт
    // <task_id>X</task_id> (подчёркивание, не дефис как у task-notification) и
    // <status>completed|failed…</status>. Некоторые модели (Kimi/Moonshot) не ждут
    // task-notification, а сами тянут результат через TaskOutput — это тоже сигнал завершения.
    private static readonly System.Text.RegularExpressions.Regex TaskOutputIdRe =
        new(@"<task_id>\s*([0-9a-zA-Z_-]{6,})\s*</task_id>", System.Text.RegularExpressions.RegexOptions.Compiled);
    private static readonly System.Text.RegularExpressions.Regex TaskOutputStatusRe =
        new(@"<status>\s*([a-zA-Z_]+)\s*</status>", System.Text.RegularExpressions.RegexOptions.Compiled);

    // Разбор tool_result инструмента TaskOutput → (agentId, aborted) при ТЕРМИНАЛЬНОМ статусе;
    // null — это не TaskOutput-результат либо агент ещё работает (running/pending/queued).
    // Чистая функция (вынесена ради юнит-тестов): без побочных эффектов и состояния прогона.
    internal static (string AgentId, bool Aborted)? ParseTaskOutputCompletion(string? content)
    {
        if (string.IsNullOrEmpty(content)) return null;
        var idm = TaskOutputIdRe.Match(content);
        if (!idm.Success) return null;
        var statusM = TaskOutputStatusRe.Match(content);
        if (!statusM.Success) return null;
        return statusM.Groups[1].Value.Trim().ToLowerInvariant() switch
        {
            "completed" => (idm.Groups[1].Value.Trim(), false),
            "failed" or "error" or "cancelled" or "canceled" => (idm.Groups[1].Value.Trim(), true),
            _ => null, // running / pending / queued — ещё не готов, ждём дальше
        };
    }

    // Учёт запуска фоновой задачи по tool_result: async-агент — «Async agent launched …
    // agentId: X», возобновление — «Agent "X" … resumed from transcript in the background»,
    // workflow — «runId: wf_…». Структурный кандидат (run_in_background/Workflow из
    // HandleAssistantToolsAsync) без распарсенного id → PendingBgUnknown: точный учёт
    // потерян, доживание прогона ограничится только потолком BgLingerTimeout.
    private void TrackBgLaunch(CliRun run, string toolUseId, string content, bool isError)
    {
        bool candidate;
        lock (run.PendingBg) candidate = run.BgLaunchCandidates.Remove(toolUseId);
        if (isError) return;

        // Гейт по маркерам запуска: без него агентский id пришлось бы искать в любом
        // tool_result (Bash с логами нашего же продукта дал бы ложный pending навсегда)
        var launchLike = candidate
            || content.Contains("Async agent launched", StringComparison.Ordinal)
            || content.Contains("resumed from transcript in the background", StringComparison.Ordinal)
            || content.Contains("Transcript dir:", StringComparison.Ordinal);
        if (!launchLike) return;

        var m = BgAgentIdRe.Match(content);
        if (!m.Success) m = BgWorkflowRe.Match(content);
        if (!m.Success && content.Contains("in the background", StringComparison.Ordinal))
            m = BgResumedRe.Match(content);
        if (m.Success)
            lock (run.PendingBg) run.PendingBg[m.Groups[1].Value] = toolUseId;
        else if (candidate)
        {
            run.PendingBgUnknown = true;
            lock (run.PendingBg) run.UnknownBgToolUses.Add(toolUseId);
        }
    }

    // Уведомление CLI о завершении фоновых задач: user-ход со строковым content
    // <task-notification>…<task-id>X</task-id>… Вычёркиваем задачи из pending и шлём клиентам
    // bg_agent_done (карточки агентов переключаются из «работает» в «ответ готов» только
    // по этому событию); если прогон между ходами и pending опустел — закрываем stdin:
    // CLI дообработает хвост (свой ход-продолжение с ответом на уведомление) и выйдет сам.
    private void HandleTaskNotification(string? text)
    {
        var run = _run;
        if (run is null || text is null
            || !text.Contains("<task-notification>", StringComparison.Ordinal)) return;
        List<string> doneTools = [];
        int removed, left;
        lock (run.PendingBg)
        {
            var before = run.PendingBg.Count;
            foreach (System.Text.RegularExpressions.Match m in TaskIdRe.Matches(text))
                if (run.PendingBg.Remove(m.Groups[1].Value.Trim(), out var toolUseId)
                    && !string.IsNullOrEmpty(toolUseId))
                    doneTools.Add(toolUseId);
            removed = before - run.PendingBg.Count;
            left = run.PendingBg.Count;
        }
        if (removed > 0)
            Console.WriteLine($"[ClaudeSession] Фоновая задача завершилась ({removed} шт.), осталось {left}");
        if (doneTools.Count > 0)
            _ = Task.Run(async () =>
            {
                try
                {
                    // Финальный текст агента должен лечь в ленту РАНЬШЕ события завершения
                    if (_subagentWatcher is { IsDisposed: false } watcher) await watcher.DrainAsync();
                    await _onMessage(new BgAgentDoneMessage(doneTools));
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[ClaudeSession] bg_agent_done не разослан: {ex.Message}");
                }
            });
        CloseStdinIfIdle(run);
    }

    // Завершение фоновой задачи, замеченное по tool_result инструмента TaskOutput (модели,
    // которые сами опрашивают результат, а не ждут task-notification). Вычёркиваем агента
    // из pending и шлём bg_agent_done — иначе карточка консультации крутила бы спиннер вечно.
    // Идемпотентно: повторный опрос того же агента уже не найдёт его в pending и no-op.
    private void HandleTaskOutputCompletion(CliRun run, string content)
    {
        if (ParseTaskOutputCompletion(content) is not { } completion) return;
        var (agentId, aborted) = completion;

        string? doneTool;
        int left;
        lock (run.PendingBg)
        {
            run.PendingBg.Remove(agentId, out doneTool);
            left = run.PendingBg.Count;
        }
        if (string.IsNullOrEmpty(doneTool)) return;

        Console.WriteLine($"[ClaudeSession] Фоновая задача завершилась через TaskOutput (aborted={aborted}), осталось {left}");
        var tool = doneTool;
        _ = Task.Run(async () =>
        {
            try
            {
                // Финальный текст агента должен лечь в ленту РАНЬШЕ события завершения
                if (_subagentWatcher is { IsDisposed: false } watcher) await watcher.DrainAsync();
                await _onMessage(new BgAgentDoneMessage([tool], Aborted: aborted));
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[ClaudeSession] bg_agent_done (TaskOutput) не разослан: {ex.Message}");
            }
        });
        CloseStdinIfIdle(run);
    }

    private async Task HandleStreamEventAsync(JsonElement root)
    {
        // Стрим-события сабагента (если CLI вдруг начнёт их слать) не подмешиваем в текст
        // основного агента — его контент придёт целыми блоками в HandleAssistantToolsAsync
        if (root.TryGetProperty("parent_tool_use_id", out var sePid) && sePid.ValueKind == JsonValueKind.String)
            return;

        if (!root.TryGetProperty("event", out var evt)) return;
        if (!evt.TryGetProperty("type", out var et)) return;
        var eventType = et.GetString();
        var index = evt.TryGetProperty("index", out var ix) && ix.TryGetInt32(out var ixv) ? ixv : -1;

        // Начало блока tool_use — показываем карточку сразу (до прихода полного assistant-сообщения)
        if (eventType == "content_block_start")
        {
            if (!evt.TryGetProperty("content_block", out var cb)) return;
            if (!cb.TryGetProperty("type", out var cbt) || cbt.GetString() != "tool_use") return;
            var id = cb.TryGetProperty("id", out var cid) ? cid.GetString() ?? "" : "";
            var name = cb.TryGetProperty("name", out var cn) ? cn.GetString() ?? "" : "";
            // Служебные инструменты не показываем: AskUserQuestion/ExitPlanMode идут отдельными
            // карточками (вопрос/план), ToolSearch — внутренний механизм загрузки схем инструментов
            if (id.Length == 0 || name is "AskUserQuestion" or "ExitPlanMode" or "ToolSearch") return;
            _toolStream[index] = (id, new System.Text.StringBuilder());
            await _onMessage(new ToolUseMessage(id, name, new { }));
            return;
        }

        if (eventType == "content_block_stop") { _toolStream.TryRemove(index, out _); return; }

        if (eventType != "content_block_delta") return;
        if (!evt.TryGetProperty("delta", out var delta)) return;
        if (!delta.TryGetProperty("type", out var dt)) return;

        switch (dt.GetString())
        {
            case "text_delta":
                if (delta.TryGetProperty("text", out var text))
                    await _onMessage(new TextDeltaMessage(text.GetString() ?? ""));
                break;

            case "thinking_delta":
                if (delta.TryGetProperty("thinking", out var thinking))
                    await _onMessage(new ThinkingDeltaMessage(thinking.GetString() ?? ""));
                break;

            case "input_json_delta":
                if (_toolStream.TryGetValue(index, out var ts) && delta.TryGetProperty("partial_json", out var pj))
                {
                    ts.Sb.Append(pj.GetString());
                    await _onMessage(new ToolInputDeltaMessage(ts.Id, ts.Sb.ToString()));
                }
                break;
        }
    }

    private async Task HandleAssistantToolsAsync(CliRun run, JsonElement root)
    {
        if (!root.TryGetProperty("message", out var msg)) return;
        if (!msg.TryGetProperty("content", out var content)) return;

        // Сообщения субагента (Task) несут parent_tool_use_id на уровне строки — для вложенности
        var parentId = root.TryGetProperty("parent_tool_use_id", out var pid) && pid.ValueKind == JsonValueKind.String
            ? pid.GetString() : null;

        foreach (var block in content.EnumerateArray())
        {
            if (!block.TryGetProperty("type", out var bt)) continue;
            var blockType = bt.GetString();

            // Скрытое размышление — показываем плашку-плейсхолдер (только у основного агента:
            // от сабагента плейсхолдер попал бы в основную ленту)
            if (blockType == "redacted_thinking")
            {
                if (parentId is null) await _onMessage(new RedactedThinkingMessage());
                continue;
            }

            // Текст/thinking сабагента CLI в stdout НЕ транслирует (сюда приходят только его
            // tool_use) — полный поток эмитит SubagentStreamWatcher из транскрипта на диске.
            // Текстовые блоки основного агента пропускаем: они уже пришли дельтами stream_event.
            if (blockType != "tool_use") continue;

            var toolId = block.TryGetProperty("id", out var tid) ? tid.GetString() ?? "" : "";
            var toolName = block.TryGetProperty("name", out var tn) ? tn.GetString() ?? "" : "";
            var toolInput = block.TryGetProperty("input", out var ti)
                ? JsonSerializer.Deserialize<object>(ti.GetRawText())! : new object();

            // Служебные инструменты не дублируем в ленте: AskUserQuestion/ExitPlanMode показываем
            // отдельными карточками (вопрос/план), ToolSearch — внутренняя загрузка схем инструментов
            if (toolName is "AskUserQuestion" or "ExitPlanMode" or "ToolSearch") continue;
            // После одобрения плана любой реальный инструмент означает, что Claude приступил к реализации
            if (_awaitPlanExecution) _sawToolSinceApprove = true;
            // Workflow по имени (без inline-script) → дописываем meta-блок скрипта, чтобы фронт
            // показал этапы (дотики фаз + N/M в тулбаре и карточке)
            if (toolName == "Workflow") toolInput = EnrichWorkflowInput(toolInput);

            // Кандидат в фоновые задачи прогона: Agent/Task с run_in_background или Workflow —
            // подтверждение запуска и id задачи придут в tool_result (TrackBgLaunch)
            if (parentId is null && toolId.Length > 0
                && (toolName == "Workflow"
                    || (toolName is "Agent" or "Task" && block.TryGetProperty("input", out var inputEl)
                        && inputEl.TryGetProperty("run_in_background", out var bgEl)
                        && bgEl.ValueKind == JsonValueKind.True)))
                lock (run.PendingBg) run.BgLaunchCandidates.Add(toolId);

            await _onMessage(new ToolUseMessage(toolId, toolName, toolInput, parentId));
        }

        // Ответ оборван по лимиту токенов (у сабагента — не показываем плашку в основной ленте)
        if (parentId is null && msg.TryGetProperty("stop_reason", out var stopReason)
            && stopReason.GetString() == "max_tokens")
            await _onMessage(new TruncatedMessage());
    }

    // Обогащение input вызова Workflow: сохранённый workflow запускается по имени
    // (Workflow({ name, args }) без script), а фронт достаёт meta.phases только из
    // input.script — этапы пропадали. Дописываем вырезанный блок `export const meta {…}`
    // того же скрипта, что исполнил CLI (workflows-каталог профиля этой сессии).
    private object EnrichWorkflowInput(object input)
    {
        if (input is not JsonElement el || el.ValueKind != JsonValueKind.Object) return input;
        // Inline-script уже есть (модель передала скрипт целиком) — не трогаем
        if (el.TryGetProperty("script", out _)) return input;
        if (!el.TryGetProperty("name", out var nameEl) || nameEl.ValueKind != JsonValueKind.String) return input;
        var name = nameEl.GetString();
        if (string.IsNullOrWhiteSpace(name)) return input;

        var metaBlock = WorkflowMetaResolver.TryGetMetaBlock(WorkflowScriptDirs(), name);
        if (metaBlock is null) return input;

        // Пересобираем input словарём: исходные поля (JsonElement) + script (строка meta-блока).
        // System.Text.Json сериализует смешанные значения штатно.
        var dict = new Dictionary<string, object?>();
        foreach (var p in el.EnumerateObject()) dict[p.Name] = p.Value;
        dict["script"] = metaBlock;
        return dict;
    }

    // Каталоги workflow-скриптов профиля этой сессии (тот же файл, что видит CLI):
    // сторонний провайдер — его изолированный профиль, основной Claude — ~/.claude/workflows.
    private IReadOnlyList<string> WorkflowScriptDirs() =>
        _providers?.GetByKey(Info.Provider) is not null
            ? [Path.Combine(_providers.GetProfileDir(Info.Provider), "workflows")]
            : [WorkflowMetaResolver.GlobalWorkflowsDir];

    // Permission-запрос старого канала (sdk_control_request) — общий пайплайн DecidePermissionAsync
    private async Task HandlePermissionAsync(JsonElement root)
    {
        // Используем request_id из CLI — именно его ждёт claude в control_response
        var requestId = root.TryGetProperty("request_id", out var rid)
            ? rid.GetString() ?? Guid.NewGuid().ToString()
            : Guid.NewGuid().ToString();

        var toolName = root.TryGetProperty("tool_name", out var tn) ? tn.GetString() ?? "" : "";
        var inputEl = root.TryGetProperty("tool_input", out var ti) ? ti : default;
        var toolInput = inputEl.ValueKind != JsonValueKind.Undefined
            ? JsonSerializer.Deserialize<object>(inputEl.GetRawText())! : new object();

        var behavior = await DecidePermissionAsync(requestId, toolName, inputEl, toolInput);
        if (behavior == "cancelled") return; // Interrupt — процесс убит, отвечать некому

        // Без updated_input — CLI продолжает с исходным вводом (см. HandleControlRequestAsync)
        var response = JsonSerializer.Serialize(new
        {
            type = "control_response",
            behavior
        });
        WriteLineToStdin(response);
    }

    // Размер контекста последнего запроса к API. usage у assistant-сообщения относится к ОДНОМУ
    // запросу (в отличие от result, где всё сложено за ход), поэтому сумма входных токенов здесь
    // и есть текущее заполнение окна. Сабагентов пропускаем: у них свой контекст, к окну
    // основной сессии отношения не имеющий.
    private void TrackContextTokens(JsonElement root)
    {
        if (HasParentToolUseId(root)) return;
        if (!root.TryGetProperty("message", out var msg)) return;
        if (!msg.TryGetProperty("usage", out var u)) return;

        var tokens = IntProp(u, "input_tokens")
            + IntProp(u, "cache_read_input_tokens")
            + IntProp(u, "cache_creation_input_tokens");
        if (tokens > 0) _lastContextTokens = tokens;
    }

    private static UsageInfo? ParseUsage(JsonElement root)
    {
        if (!root.TryGetProperty("usage", out var u)) return null;
        return new UsageInfo(
            IntProp(u, "input_tokens"),
            IntProp(u, "output_tokens"),
            IntProp(u, "cache_read_input_tokens"),
            IntProp(u, "cache_creation_input_tokens")
        );
    }

    // Безопасное чтение числовых полей stream-json. TryGetProperty возвращает true и для JSON
    // null, а Get*/TryGet* на НЕ-числовом элементе (Null/String) КИДАЮТ InvalidOperationException
    // (TryGetInt32 отдаёт false лишь при переполнении Number, но не при Null!) — на openrouter-
    // совместимом потоке (usage/стоимость приходят null) это роняло цикл чтения прогона.
    // Поэтому обязательна явная проверка ValueKind == Number перед чтением.
    internal static int IntProp(JsonElement o, string name, int def = 0) =>
        o.TryGetProperty(name, out var e) && e.ValueKind == JsonValueKind.Number && e.TryGetInt32(out var v) ? v : def;
    internal static long LongProp(JsonElement o, string name, long def = 0) =>
        o.TryGetProperty(name, out var e) && e.ValueKind == JsonValueKind.Number && e.TryGetInt64(out var v) ? v : def;
    internal static double? DoubleProp(JsonElement o, string name) =>
        o.TryGetProperty(name, out var e) && e.ValueKind == JsonValueKind.Number && e.TryGetDouble(out var v) ? v : (double?)null;

    public async ValueTask DisposeAsync()
    {
        _fileWatcher.Dispose();
        _subagentWatcher?.Dispose();
        _subagentWatcher = null;
        _transcriptTailer?.Dispose();
        _transcriptTailer = null;
        lock (_workflowWatchers)
        {
            foreach (var w in _workflowWatchers) w.Dispose();
            _workflowWatchers.Clear();
        }
        // Ожидающие permission-диалоги: ответа не будет — отменяем, иначе
        // DecidePermissionAsync держит граф адаптера до часового таймаута
        foreach (var tcs in _permissionWaiters.Values)
            tcs.TrySetCanceled();
        _permissionWaiters.Clear();
        _pendingQuestions.Clear();
        _pendingPlans.Clear();
        _cts.Cancel();
        if (_currentProcess != null && !_currentProcess.HasExited)
        {
            // Убиваем всё дерево: claude порождает node-процессы MCP-серверов
            _launcher.Kill(_currentProcess, _currentTurnId);
            using var exitCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            try { await _currentProcess.WaitForExitAsync(exitCts.Token); }
            catch (OperationCanceledException) { } // 10 с истекло — идём дальше
        }
        _currentProcess?.Dispose();
        _cts.Dispose();
        _turnLock.Dispose();
        _stdinLock.Dispose();
    }
}
