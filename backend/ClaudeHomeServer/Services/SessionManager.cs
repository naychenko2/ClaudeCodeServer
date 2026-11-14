using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using ClaudeHomeServer.Core.Telemetry;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services.Access;
using ClaudeHomeServer.Services.ChatSubjects;
using ClaudeHomeServer.Services.Composition;
using ClaudeHomeServer.Services.Knowledge;
using ClaudeHomeServer.Services.Llm;
using ClaudeHomeServer.Services.Prompts;
using ClaudeHomeServer.Services.Sessions;
using ClaudeHomeServer.Services.Spheres;
using ClaudeHomeServer.Services.Tasks;
using ClaudeHomeServer.Services.Team;
using ClaudeHomeServer.Services.Turn;

namespace ClaudeHomeServer.Services;

// Фасад сессий (S3-7, аддендум Александра §1): ход в сессии запускает только ядро приёма
// TurnIntake — здесь остаются переадресации прежних публичных сигнатур, цикл «до готово»
// (его тело до шага 2 выноса TurnContinuation) и штабные обвязки. Шов ITurnStarter фасад
// уже не реализует: он резолвит ядро. ITeamTurnIntake реализует ядро (карта §3); фасад
// держит его однострочной переадресацией только потому, что конструктор TeamWaveService
// принимает конкретный SessionManager и upcast'ит его сам (TeamWaveService.cs:104), а код
// Services/Team этот шаг не меняет.
public class SessionManager : IDisposable, ITeamNotifier, ISessionDirectory,
    ITeamSessionDirectory, ITeamHistoryStore, ITeamRunState, ITeamTurnIntake,
    Access.ITurnActorRegistry, Access.ITurnFrameSource,
    Sessions.IWorkLoopContinuation
{

    // Дальше какой глубины цепочка автоотчётов не идёт. 3 — как у делегирования задач:
    // исполнитель → постановщик → его постановщик, дальше эскалация теряет смысл.
    private const int MaxReportChainDepth = 3;

    // Стор метаданных сессий (S3-1): реестр, сохранение sessions.json, владелец и видимость,
    // актор хода, наблюдатели, рассылка статуса. Синглтон DI (AddSessionCore); фасад получает
    // его параметром — запасной сборки нет с S3-8c. SessionManager — фасад над ним.
    private readonly SessionStore _store;
    // Тот же реестр, что держит стор (одна точка правды): второго держателя нет — свойство
    // читает словарь стора напрямую (хвост 52a8981c п.4, S3-8c). На него завязаны тела ещё
    // не вынесенных ролей и white-box тесты (рефлексия «_sessions» — теперь по свойству).
    private ConcurrentDictionary<string, SessionEntry> _sessions => _store.Sessions;
    // Координатор режима «Командная реализация» (этап 4, шаг 2г-3а): owning — создаётся
    // в конструкторе SessionManager, DI-регистрация придёт в шаге 2г-4. Owning разрывает
    // цикл «ядро ↔ вертикаль штаба» без Lazy<T> и без нового Func-канала.
    private readonly TeamCoordinator _teamCoordinator;
    // Хранитель состояния режима (этап 4, шаг 2г-3в, волна А): owning по тому же шаблону,
    // что _teamCoordinator. Шесть блоков тела штаба (WithTeamState,
    // BroadcastTeamImplementAsync, NewTeamImplementBudget, TeamImplementSetupError,
    // ResolveTeamPlanRoot, GetTeamPlanAsync) переехали сюда; SessionManager держит тонкие
    // обёртки-делегаты, чтобы не переписывать тесты. Седьмой блок,
    // SaveTeamImplementStateAsync, снят в шаге 2г-4 волны 3 — его тело живёт
    // в ITeamSessionDirectory.PersistAndBroadcastAsync.
    private readonly TeamStateService _teamState;
    // Планирование штаба (этап 4, шаг 2г-3д, волна В): owning по тому же шаблону, что
    // _teamCoordinator/_teamState. Пять блоков тела штаба (RunTeamPlanningAsync,
    // CreateTeamPlanAsync, PublishTeamPlanAsync, SupersedeCurrentPlanCardAsync,
    // ResolveStalePlanCardAsync) переехали сюда; SessionManager держит тонкие
    // обёртки-делегаты, чтобы не переписывать тесты (18 мест вызывают _sut.CreateTeamPlanAsync).
    private readonly TeamPlanService _teamPlan;
    private readonly ChatHistoryService _history;
    // Паспорта прогонов сабагентов (диагностика обрывов + сигнал для автодобивания); null — без
    // вертикали Llm в контейнере. Подписки на шине для паспортов ставит TurnPassports (S3-4).
    private readonly Llm.Claude.SubagentRunLog? _subagentRuns;
    // Шина событий хода (ADR-013): синглон DI, тот же, что читают роли. Телом фасада поле не
    // читается (теневой подписчик ставится из параметра) — держится как тест-шов: базовые
    // тесты штаба публикуют TurnCompleted через ТОТ ЖЕ экземпляр (SessionManagerTestBase).
    private readonly Turn.ITurnEventBus _turnEvents;
    // Лок сохранения sessions.json — один на процесс, живёт в сторе; пометки «зафиксировано в git»
    // (MarkFilesCommitted/UnmarkFileCommitted) делают read-modify-write под тем же замком.
    private Lock _saveLock => _store.SaveLock;
    // Признак штатной остановки — в ядре приёма TurnIntake.Stopping (S3-7, аддендум §2):
    // писатель один — BeginShutdown, его зовёт KillAllProcesses ДО гашения адаптеров;
    // разбор хода читает его через IsStopping шва (TurnCompletion), тела цикла и добивания
    // (остаются здесь до шага 2) — как раннюю отсечку через _intake.Stopping.
    // Тик ожидания по маркеру `<waiting>` (фаза waiting И WaitingReason != null). Тикает
    // ТОЛЬКО ожидание модели — ожидание по живой делегированной задаче НЕ трогаем
    // (доклад придёт сам, смерть исполнителя ловит алерт молчания). Отдельный таймер
    // вместо встраивания в автосейв стора — у них разные требования к гейтам и к
    // зависимости от живого хода: автосейв просто сбрасывает стор, тик шлёт директивы.
    private Timer? _waitingTickTimer;
    // Интервал тика ожидания по маркеру. Дефолт 5 минут (Loop:WaitingTickSeconds). Тестам
    // и особым окружениям позволяют уменьшить (для скорости прогонов). <=0 — таймер не
    // заводится, как у автосейва.
    private readonly TimeSpan _waitingTickInterval;
    // Потолок тиков ожидания по маркеру. По достижении цикл встаёт с reason="waiting_timeout"
    // и причиной из маркера. Дефолт 20 (Loop:MaxWaitingTicks). При 5-минутном интервале это
    // ~1.5 часа чистого ожидания — дольше редко нужно; раньше цикл честно признаёт, что
    // событие не пришло.
    private readonly int _maxWaitingTicks;
    // Роли ядра сессий (S3-2, docs/research/session-manager-split-2026-11.md): фасад
    // делегирует им тонкими обёртками — сигнатуры наружу не меняются, тела ещё не
    // вынесенных ролей вызывают прежние имена. Экземпляры — синглтоны DI (AddSessionCore),
    // приходят параметрами конструктора (S3-8c); один экземпляр на фасад.
    private readonly SessionHistory _sessionHistory;
    private readonly TurnCostLedger _costs;
    private readonly PendingQueueService _pending;
    private readonly CliRoute _route;
    // Рабочая папка чата (S3-3): отдельное дерево, пометка коммита, уборка графа/watcher'ов.
    private readonly ChatWorktree _worktree;
    // Смена провайдера у чата (S3-3): guard и перенос транскрипта — единственная точка.
    private readonly ProviderMigration _providerMigration;
    // Именование чата (S3-3): авто-заголовок, явное переименование, значки тем.
    private readonly ChatNaming _naming;
    // Видимость чата (S3-3): тумблер приватности с переносом вложений, их учёт и paths в ленте.
    private readonly ChatVisibility _visibility;
    // Настройки и жизненный цикл чата (S3-3): архив, срок, темы, собеседник, правка имени/модели.
    private readonly ChatSettings _settings;
    // Контекст запуска (S3-4): адрес и токен MCP, встроенные продуктовые серверы, слой персоны,
    // внешние серверы и модули, паспорта хода. Тот же порядок, что в S3-2/S3-3: DI-экземпляр
    // параметром конструктора (S3-8c); один экземпляр на фасад.
    private readonly McpEndpoint _endpoint;
    private readonly BuiltinMcpContexts _builtin;
    private readonly PersonaLayerBuilder _persona;
    private readonly ExternalMcpContexts _external;
    private readonly ModulesMcpContexts _moduleContexts;
    private readonly TurnPassports _passports;
    // Процесс CLI и жизненный цикл чата (S3-5): хост процесса, возобновление оборванного
    // хода, создание и удаление чата. Тот же порядок, что в S3-2/S3-3/S3-4: DI-экземпляр
    // параметром конструктора (S3-8c); один экземпляр на фасад. Фабрика контекста
    // запуска — internal-тип внутри хоста (полем фасада она быть не может: CS0051), шов
    // приёма сообщений — ленивый на этот же экземляр: с S3-6 разбор сообщений делает
    // роль конца хода, не фасад.
    private readonly TurnProcessHost _process;
    private readonly TurnRecovery _recovery;
    private readonly ChatCreation _creation;
    private readonly ChatDeletion _deletion;
    // Ядро приёма хода (S3-7): ход в сессии запускает только оно. Фасад переадресует прежние
    // публичные сигнатуры; цикл «до готово» просит у него ход через TrySubmitContinuationAsync
    // (решающая проверка остановки — в ядре); штабной ITeamTurnIntake реализует ядро, фасад
    // держит его однострочной переадресацией (upcast TeamWaveService, п.5 признаков).
    // Пустая очередь при активном цикле — переходный хук OnLoopContinueRequested: его
    // ставит этот конструктор (тело цикла здесь до шага 2).
    private readonly Sessions.TurnIntake _intake;

    // Общий лок внеходовых записей истории (_falPersistLock) переехал в SessionHistory вместе
    // с механикой истории; здесь только делегирующая обёртка: правка карточки плана в
    // сохранённой истории (тело, остающееся в фасаде) идёт через ТОТ ЖЕ лок, что и публикации
    // цен в TurnCostLedger. Отдельный лок здесь вернул бы гонку lost update.
    private async Task<T> WithFalPersistLockAsync<T>(Func<Task<T>> work) =>
        await _sessionHistory.WithFalPersistLockAsync(work);

    private async Task WithFalPersistLockAsync(Func<Task> work) =>
        await _sessionHistory.WithFalPersistLockAsync(work);

    // Ключ history.json чата: транскрипт CLI, а до первого ответа агента — id самого чата.
    // Тот же ключ берут EnsureAccumulatorAsync и локальный голосовой ход, поэтому записи,
    // сделанные до первого ответа (карточки нитей картинок, ADR-019), аккумулятор первого хода
    // поднимает с диска и дальше сохраняет уже под ClaudeSessionId — ничего не теряется.
    private static string HistoryKeyOf(Session info) => SessionHistory.HistoryKeyOf(info);

    // Enum (в т.ч. ClaudeMode) сериализуем строками — устойчиво к изменению порядка значений.
    // При чтении конвертер принимает и старый числовой формат.
    private static readonly JsonSerializerOptions _jsonOpts = new()
    {
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly LlmProviderRegistry _llmProviders;
    // FalCostService, UsageService, ModelAssignmentResolver, UserHomeResolver и
    // ArchivedTranscriptStore здесь больше не поля и не параметры: их читают роли
    // (TurnCostLedger, CliRoute, ChatSettings, ChatCreation), собранные в AddSessionCore (S3-8c).
    private readonly ITierModelResolver _appSettings;
    private readonly IConfiguration _config;

    // Зрители сессии и живой прогон CLI — в сторе (S3-1)
    public void AddViewer(string sessionId, string connectionId) => _store.AddViewer(sessionId, connectionId);

    public void RemoveViewer(string sessionId, string connectionId) => _store.RemoveViewer(sessionId, connectionId);

    public void RemoveConnectionViewers(string connectionId) => _store.RemoveConnectionViewers(connectionId);

    public bool HasViewers(string sessionId) => _store.HasViewers(sessionId);

    public bool HasLiveTurnProcess(string sessionId) => _store.HasLiveTurnProcess(sessionId);

    // Наблюдатель сообщений сессий (Claude-исполнитель задач слушает result/permission).
    // Вызывается после обновления статуса и broadcast; его ошибки не роняют пайплайн.
    // С S3-6 событие живёт в TurnCompletion (его единственный потребитель —
    // ProcessPipelineItemAsync); здесь остаются аксессоры подписки — внешняя сигнатура прежняя.
    public event Func<Session, ServerMessage, Task>? OnSessionMessage
    {
        add => _completion.OnSessionMessage += value;
        remove => _completion.OnSessionMessage -= value;
    }

    // Текст пользовательского сообщения (ввод в чат) — для push-источников автоматизаций
    // (детекция @упоминаний персон). С S3-7 событие живёт в ядре приёма (его единственный
    // вызывающий — SendDirectAsync там же); здесь аксессоры подписки — внешняя сигнатура
    // прежняя, подписчик PersonaAutomationService не менялся. (session, text, senderPersonaId)
    public event Func<Session, string, string?, Task>? OnUserMessage
    {
        add => _intake.OnUserMessage += value;
        remove => _intake.OnUserMessage -= value;
    }

    // Удаление сессии (чат/проектная сессия) — для авто-движков: сбросить ссылки на чат правила.
    public event Action<Session>? OnSessionDeleted;

    // Корень контейнера: ленивые резолвы фасада (IProjectAccess для GetWithRight/GetAccessible,
    // internal-роли вроде LocalVoiceTurn — CS0051, параметром быть не могут). Прямой зависимости
    // ролей через него нет — это решение 05.11 (1dda7103), см. комментарий ChatDeletion.cs:17.
    private readonly IServiceProvider _services;
    private readonly PersonaManager _personas;
    private readonly ILogger<SessionManager> _log;
    private readonly Execution.SandboxManager _sandbox;
    // Шов «ядро → штаб» (этап 4, шаг 2б плана выноса штаба): четыре крючка уведомления
    // и запрос IsSessionBusy, реализованные этим же классом как обёртка над прежними
    // приватными методами (см. явные реализации ITeamNotifier ниже). Поле снято в S3-8c:
    // реализация остаётся на этом же классе, отдельный держатель шва не нужен.
    // Три шва данных «штаб → ядро» (этап 4, шаг 2г-3б; объявления — Services/Team/
    // TeamCoreSeams.cs). Направление обратное ITeamNotifier: здесь вертикаль спрашивает ядро.
    // С S3-8c ITeamRunState держит SessionStore, ITeamTurnIntake — ядро приёма (п.2):
    // вертикаль получает эти роли прямо в свои конструкторы. ITeamHistoryStore остаётся на
    // фасаде (this): половина его тел — фасадная развилка Accumulator/диск и эскалационные
    // методы, чьи тела живут в вертикали (TeamDecisionService.cs:441); перенос тел в роль —
    // правка формы ядра, отдельный шаг (находка отчёта S3-8c). Каталога сессий среди полей
    // нет намеренно: его спрашивает только вертикаль (у тела штаба, пока оно здесь, entry
    // уже на руках).
    private readonly ITeamHistoryStore _teamHistory;
    private readonly ITeamRunState _teamRunState;
    private readonly ITeamTurnIntake _teamIntake;
    // Координатор решений по карточке плана (волна Г): запуск работы, закрытие интервью
    // и реакция на карточку плана. Owning-паттерн (создаётся в конструкторе, в DI
    // переедет на шаге 2г-4).
    private readonly TeamDecisionService _teamDecision;
    // Квоты и бюджет практики (волна Е): гейт запуска исполнителей и пробуждения штаба
    // агентом, подъём от чата исполнения к штабу, карточка «бюджет исрасходован».
    // Owning-паттерн (создаётся в конструкторе, в DI переедет на шаге 2г-4).
    private readonly TeamBudgetService _teamBudget;
    // Шапка разбора хода штаба и доклад о блокере (волна Ж): HandleTeamTurnCompletedShim
    // (подписчик turn/completed), HandleTeamTurnEndAsync (тело разбора маркеров),
    // RestoreWaveWatchdogIfPaused, TryAutoResolveTeamBlockerAsync, ReportBlockerAsync.
    // Owning-паттерн по тому же рецепту, что у _teamDecision/_teamBudget.
    private readonly TeamTurnCompletionService _teamTurnCompletion;
    // Включение и переключение режима «Командная реализация» (волна Ж): SetTeamImplementAsync
    // и SetTeamImplementAutoAsync. Owning-паттерн по тому же рецепту.
    private readonly TeamEnableService _teamEnable;
    // Конец хода (S3-6): разбор сообщений адаптера — аккумулятор, статусы, гейты прогона,
    // очередь рассылки и наблюдатели. Фасад держет делегирующие обёртки прежних internal-входов
    // (тесты и тела приёма хода зовут их по-прежнему) и аксессоры события наблюдателей.
    private readonly Sessions.TurnCompletion _completion;
    // Локальный голосовой ход (S3-6): прямой вызов Ollama мимо CLI. Единственный потребитель —
    // приём хода (ShouldRunLocalVoice/RunLocalVoiceTurnAsync), поэтому полем конструктора не
    // становится: internal-тип (CS0051), общий синглтон с ядром приёма — резолв из контейнера
    // в конструкторе без запасной сборки (S3-8c).
    private readonly Sessions.LocalVoiceTurn _voice;
    // Резолв задач (SessionTaskLinks.ParentSessionId / IsTaskDone) живёт в сторе (S3-1):
    // Core-шов ITaskLookup, null — задача не резолвится (ParentSessionId=null, TaskDone=false).
    private ITaskLookup? _taskLookup => _store.TaskLookup;
    // Тест-шов: подменить ITaskLookup. TaskManager в конструктор SessionManager не пробрасывают
    // (DI-цикл), поэтому юниты, которым нужен резолв задачи, подменяют lookup явно.
    // Подмена идёт по стору, а не по фасаду: в DI-пути стор — синглтон (SessionCoreRegistration.cs:22),
    // поэтому в Host-тесте подмена отравила бы общий стор на всё время жизни фабрики. Сегодня шов зовут
    // только юниты и каждый SessionCoreTestHost.Build носит свой контейнер и свой стор
    // (SessionCoreTestHost.cs:38) — риска нет; при появлении Host-тестов нужен сброс или изоляция стора.
    internal void SetTaskLookupForTests(ITaskLookup? lookup) => _store.TaskLookup = lookup;

    // Сжатие конструктора (S3-8c, решение (А) 08.11): роли ядра и живые зависимости фасада и
    // штаба — типизированными параметрами; запасные сборки S3-1…S3-7 сняты. internal-типы
    // ролей (LocalVoiceTurn) параметрами публичного конструктора не становятся (CS0051) —
    // резолвятся из контейнера в теле, без запасной сборки. Nullable-параметры ровно там,
    // где сервиса может не быть в контейнере (выключенная подсистема, тест без него):
    // null означает «подсистемы нет», запасной экземпляр не собирается.
    public SessionManager(
        // Роли ядра (Services/Sessions, AddSessionCore): все — синглтоны, один экземпляр на фасад.
        SessionStore store, SessionHistory historyRole, TurnCostLedger costs, PendingQueueService pending,
        CliRoute route, ChatWorktree worktree, ProviderMigration providerMigration, ChatNaming naming,
        ChatVisibility visibility, ChatSettings settings, McpEndpoint mcpEndpoint,
        BuiltinMcpContexts builtinContexts, PersonaLayerBuilder personaLayer,
        ExternalMcpContexts externalContexts, ModulesMcpContexts moduleContexts, TurnPassports turnPassports,
        TurnProcessHost processHost, TurnRecovery recovery, ChatCreation chatCreation, ChatDeletion chatDeletion,
        Sessions.TurnCompletion completion, Sessions.TurnIntake intake,
        // Живые зависимости фасада и штаба: конфигурация и резолверы, читаемые телом фасада,
        // и зависимости, нужные создаваемым здесь сервисам штаба.
        ProjectManager projects, ChatHistoryService history, IConfiguration config,
        LlmProviderRegistry llmProviders, PersonaManager personas, ILogger<SessionManager> log,
        // Шов Ф4 (Этап 5): ISessionBroadcaster нужен TeamCoordinator и стору; сам фасад на нём
        // не сидит (рассылки — через роли).
        Composition.ISessionBroadcaster broadcaster,
        // Фабрика логгеров — типизированные логгеры вертикали (волна В); корень контейнера —
        // ленивые резолвы фасада (IProjectAccess, internal-роли); шина хода (ADR-013) — тот же
        // синглтон, что читают роли; учёт лимитов тира — резолвер моделей агентных мест.
        ILoggerFactory loggerFactory, IServiceProvider services, Turn.ITurnEventBus turnEvents,
        AppSettingsService appSettings, Execution.SandboxManager sandbox,
        // Опциональны ровно там, где сервиса может не быть в контейнере: без план-механики штаба
        // режим включается, но план не строится; без реестра задач связи сессии не резолвятся;
        // без журнала прогонов нudge сабагентов молчит. Дефолт = null обязателен:
        // nullable-аннотация для контейнера ничего не значит — параметр опционален только с
        // значением по умолчанию (CLAUDE.md:291, прецедент NotesSubsystemGateTests.cs:90);
        // без него активация фасада падает вместо деградации в null.
        TeamPlanningService? teamPlanning = null, TaskManager? tasks = null,
        Llm.Claude.SubagentRunLog? subagentRuns = null)
    {
        // Стор — первым: на него опираются реестр, швы штаба и всё, что ниже читает сессии.
        _store = store;
        _services = services;
        _subagentRuns = subagentRuns;
        _sandbox = sandbox;
        _personas = personas;
        _history = history;
        _llmProviders = llmProviders;
        _appSettings = appSettings;
        _config = config;
        _log = log;

        // Роли ядра (S3-2…S3-7): приходят готовыми из AddSessionCore, параметром конструктора
        // (S3-8c): фасад только присваивает поля. Порядок сборки задаёт регистрация: журнал
        // цен ставит OnCostResolved на свою публикацию, очередь и маршрут читают стор —
        // он зарегистрирован первым.
        _sessionHistory = historyRole;
        _costs = costs;
        _pending = pending;
        _route = route;
        _worktree = worktree;
        _providerMigration = providerMigration;
        _naming = naming;
        _visibility = visibility;
        _settings = settings;
        _endpoint = mcpEndpoint;
        _builtin = builtinContexts;
        _persona = personaLayer;
        _external = externalContexts;
        _moduleContexts = moduleContexts;
        _passports = turnPassports;
        _process = processHost;
        _recovery = recovery;
        _creation = chatCreation;
        _deletion = chatDeletion;
        _completion = completion;
        _intake = intake;
        // Локальный голосовой ход (S3-6/S3-7): общий синглтон с ядром приёма — internal-тип
        // (CS0051), параметром не становится; резолв из контейнера, без запасной сборки (S3-8c).
        _voice = services.GetRequiredService<Sessions.LocalVoiceTurn>();
        // Тест-шов: та же шина, что держит фасад. Телом фасада поле не читается (подписку
        // ставит параметр), но SessionManagerTestBase.PublishTurnCompletedAsync публикует
        // TurnCompleted через ТОТ ЖЕ экземпляр — иначе тест публиковал бы мимо подписчика.
        _turnEvents = turnEvents;

        // Швы данных «штаб → ядро» (ITeamHistoryStore/ITeamRunState/ITeamTurnIntake): поля держат
        // экземпляры ролей (п.2, S3-8c): ITeamRunState — SessionStore, ITeamTurnIntake — ядро
        // приёма. ITeamHistoryStore остаётся на фасаде: тела его эскалационных методов живут
        // в вертикали (PublishTeamEscalationAsync — TeamDecisionService.cs:441) и в фасадной
        // развилке Accumulator/диск; перенос тел — отдельный шаг (находка отчёта S3-8c).
        // Присваивание швов — до создания сервисов вертикали: они получают их в конструкторы.
        _teamHistory = this;
        _teamRunState = _store;
        _teamIntake = _intake;

        // Штаб: создание остаётся в конструкторе (п.2). Первый параметр семи сервисов типизирован
        // SessionManager — это по-прежнему this (вертикаль знает фасад); интерфейсные швы получают
        // экземпляры ролей. Owning-паттерн без изменений: он разрывает цикл «ядро ↔ вертикаль
        // штаба» без Lazy<T> и без нового Func-канала.
        _teamCoordinator = new TeamCoordinator(broadcaster);
        // Хранитель состояния режима (волна А): создаётся ДО остального штаба и до LoadSessions,
        // чтобы восстановление состояния режима после рестарта (через публичные обёртки
        // WithTeamState) могло идти через TeamStateService сразу. Owning по тому же шаблону,
        // что _teamCoordinator. Волна Б: вертикаль сама проверяет
        // CapabilitiesFor(model).SupportsPlanMode при входе в план-фазу.
        _teamState = new TeamStateService(this, teamPlanning, projects, config, llmProviders);
        // Волна В: создаётся ПОСЛЕ _teamState, потому что TeamPlanService зовёт публичные
        // обёртки ядра (RestoreUserMode, SaveSessions, BroadcastTeamImplementAsync,
        // ResolveTeamPlanRoot) и видит швы данных через сам SessionManager. Жизненный цикл
        // симметричен _teamCoordinator/TeamStateService — owning в конструкторе, регистрация
        // через DI придёт в шаге 2г-4.
        _teamPlan = new TeamPlanService(this, _teamHistory, _teamRunState, teamPlanning, _teamCoordinator,
            _personas, projects, _teamState, loggerFactory.CreateLogger<TeamPlanService>());
        // Волна Г: координатор решений по карточке плана (запуск работы, закрытие
        // интервью, реакция на карточку). Owning-паттерн (создаётся в конструкторе
        // SessionManager — здесь тот же приём, что у _teamCoordinator/_teamState/_teamPlan).
        // В DI переедет на шаге 2г-4, owning-обёртки StartTeamWorkAsync/CloseTeamTalkAsync/
        // RespondTeamPlanAsync в SessionManager будут сняты.
        _teamDecision = new TeamDecisionService(this, this, _teamHistory, _teamRunState, _teamIntake,
            _personas, _teamState, _teamPlan, loggerFactory.CreateLogger<TeamDecisionService>());
        // Квоты и бюджет практики (волна Е). Owning-паттерн (создаётся в конструкторе,
        // в DI переедет на шаге 2г-4) разрывает цикл «SessionManager хочет TeamBudgetService,
        // TeamBudgetService хочет SessionManager» — вертикаль видит ядро по прямой ссылке,
        // а ядро знает о вертикали через поле _teamBudget.
        _teamBudget = new TeamBudgetService(this, this, _teamRunState, _teamState,
            loggerFactory.CreateLogger<TeamBudgetService>());
        // Шапка разбора хода штаба и доклад о блокере (волна Ж): owning-паттерн по тому же
        // рецепту, что у _teamDecision/_teamBudget. Подписчик turn/completed переехал в
        // вертикаль — шинная подписка ниже регистрирует делегат на метод TeamTurnCompletionService.
        _teamTurnCompletion = new TeamTurnCompletionService(this, this, _teamHistory, _teamRunState,
            _teamDecision, loggerFactory.CreateLogger<TeamTurnCompletionService>(), tasks);
        // Включение и переключение режима «Командная реализация» (волна Ж): SetTeamImplementAsync
        // и SetTeamImplementAutoAsync. Owning-паттерн по тому же рецепту, что и _teamBudget.
        _teamEnable = new TeamEnableService(this, this, _teamHistory, _teamRunState, _teamState,
            loggerFactory.CreateLogger<TeamEnableService>());

        // Переходный хук «пустая очередь при активном цикле — цикл продолжит директивой»:
        // ядро регистрирует факт, тело цикла живёт здесь до шага 2 (вынос TurnContinuation),
        // там хук перейдёт новому владельцу.
        _intake.OnLoopContinueRequested = ContinueWorkLoopAsync;

        // Найденную стоимость fal.ai публикует TurnCostLedger: он ставит
        // FalCostService.OnCostResolved на свою PublishFalCostAsync в своём конструкторе
        // (S3-2). Здесь колбэк не ставим — иначе последний присвоенный затёр бы запись роли.
        // Изменение персоны (профиль/возможности/привязки) — сбрасываем адаптеры её живых
        // сессий, чтобы Tool-рубильники и MCP-серверы перемонтировались со следующего хода
        _personas.OnPersonaChanged += p => InvalidatePersonaSessions(p.Id);

        // Подписка на шину событий хода (ADR-013): здесь остаётся только тень разбора хода
        // штаба (HandleTeamTurnCompletedShim) — она относится к вертикали штаба, а не к
        // паспортам. Подписчики паспортов (PromptAssembled/SubagentRunCompleted/TurnCompleted)
        // и реестр контрибьюторов секций промпта ставит TurnPassports в своём конструкторе
        // (S3-4) на ТОМ ЖЕ экземпляре шины: синглтон DI — тот же, что получает этот конструктор
        // параметром (S3-8c).
        // Этап 4 / шаг 1б: теневой подписчик turn/completed для штаба. ИНЕРТЕН — боевую
        // логику HandleTeamTurnEndAsync НЕ зовёт, только сверяет факт вызова старого пути с
        // тем, что диктует Outcome. Старый путь работает как единственный. Переключение
        // (под-шаг 3) снимет прямой вызов из OnMessageAsync и дедуп SkipNextTeamTurnEnd.
        turnEvents.OnNotification<TurnCompleted>(HandleTeamTurnCompletedShim,
            "SessionManager.TeamShadowSubscriber");

        LoadSessions();

        // Автосохранение: периодический сброс in-memory данных на диск (таймер и дебаунс — в сторе)
        _store.StartAutoSave();

        // Тик ожидания по маркеру `<waiting>`. Тот же гейт `autoSave > Zero` — чтобы тесты
        // с `Session:AutoSaveSeconds = 0` не оставляли тик работающим: фоновые таймеры
        // выполняют произвольный код в произвольный момент и между ассертами теста гонят
        // директиву и сбрасывают счётчики (та же причина, что у _autoSaveTimer). Дефолты
        // подхватываются из Loop:* если ключ не задан или мусор — см. ParsePositiveSeconds/
        // ParsePositiveInt (Loop:MaxIterations ходит через LoopLimitOrDefault со своей
        // семантикой «<=0 = дефолт 20», не трогаем).
        _waitingTickInterval = ParsePositiveSeconds(config["Loop:WaitingTickSeconds"], 300);
        _maxWaitingTicks = ParsePositiveInt(config["Loop:MaxWaitingTicks"], 20);
        if (_store.AutoSaveEnabled && _waitingTickInterval > TimeSpan.Zero)
            _waitingTickTimer = new Timer(_ => FireAndForget(TickWaitingLoopsAsync(), "тик ожидания work-loop"),
                null, _waitingTickInterval, _waitingTickInterval);
    }

    // Положительный интервал (секунды) из конфига с дефолтом: <=0 и мусор → defaultValue.
    private static TimeSpan ParsePositiveSeconds(string? raw, int defaultSeconds) =>
        int.TryParse(raw, out var v) && v > 0 ? TimeSpan.FromSeconds(v) : TimeSpan.FromSeconds(defaultSeconds);

    // Положительное целое из конфига с дефолтом: <=0 и мусор → defaultValue.
    private static int ParsePositiveInt(string? raw, int defaultValue) =>
        int.TryParse(raw, out var v) && v > 0 ? v : defaultValue;

    // --- Тонкие обёртки ролей контекста запуска (S3-4) ---
    // Тела ушли в Services/Sessions (McpEndpoint, BuiltinMcpContexts); сигнатуры фасада
    // прежние: на них сидят точки сборки контекста здесь и внешние потребители
    // (McpBackendAccess, TasksToolset, PersonasToolset, WorkspaceToolset).
    internal string ResolveTasksApiUrl(string? ownerId = null) => _endpoint.ResolveTasksApiUrl(ownerId);
    internal static string LoopbackApiUrl(string address) => Sessions.McpEndpoint.LoopbackApiUrl(address);
    internal string GetServiceToken(string ownerId) => _endpoint.GetServiceToken(ownerId);
    private bool HttpEndpointUsable(string apiUrl) => _endpoint.HttpEndpointUsable(apiUrl);

    internal bool TasksMcpEnabled(string? ownerId, Session session, Persona? persona) =>
        _builtin.TasksMcpEnabled(ownerId, session, persona);
    private TasksMcpContext? BuildTasksContext(string? ownerId, string? projectId, Persona? persona = null) =>
        _builtin.BuildTasksContext(ownerId, projectId, persona);
    private NotesMcpContext? BuildNotesContext(string? ownerId, string? projectId, Persona? persona) =>
        _builtin.BuildNotesContext(ownerId, projectId, persona);
    private WidgetsMcpContext? BuildWidgetsContext(string? ownerId, Persona? persona) =>
        _builtin.BuildWidgetsContext(ownerId, persona);
    private WatchMcpContext? BuildWatchContext(string? ownerId) => _builtin.BuildWatchContext(ownerId);
    private WebSearchMcpContext? BuildWebSearchContext(string? ownerId, Persona? persona) =>
        _builtin.BuildWebSearchContext(ownerId, persona);
    private ArchitectureMcpContext? BuildArchitectureContext(string? ownerId, string? projectId, Persona? persona) =>
        _builtin.BuildArchitectureContext(ownerId, projectId, persona);
    internal HiggsfieldMcpContext? BuildHiggsfieldContext(string? ownerId, Persona? persona) =>
        _builtin.BuildHiggsfieldContext(ownerId, persona);
    internal ImageEditorMcpContext? BuildImageEditorContext(string? ownerId, Session session) =>
        _builtin.BuildImageEditorContext(ownerId, session);
    internal AudioEditorMcpContext? BuildAudioEditorContext(string? ownerId, Session session) =>
        _builtin.BuildAudioEditorContext(ownerId, session);
    internal VideoEditorMcpContext? BuildVideoEditorContext(string? ownerId, Session session) =>
        _builtin.BuildVideoEditorContext(ownerId, session);
    internal PluginsMcpContext? BuildPluginsContext(string? ownerId, Session session) =>
        _builtin.BuildPluginsContext(ownerId, session);
    internal TurnContextMcpContext? BuildTurnContextContext(string? ownerId, Session session) =>
        _builtin.BuildTurnContextContext(ownerId, session);
    internal LocalMediaMcpContext? BuildLocalMediaContext(string? ownerId, string? projectId, Persona? persona) =>
        _builtin.BuildLocalMediaContext(ownerId, projectId, persona);
    internal TestsMcpContext? BuildTestsContext(string? ownerId, string? projectId, Persona? persona) =>
        _builtin.BuildTestsContext(ownerId, projectId, persona);
    private DifyMcpContext? BuildDifyContext(string? ownerId) => _builtin.BuildDifyContext(ownerId);
    private CodeGraphMcpContext? BuildCodeGraphContext(string? ownerId, string? projectId, string sessionId,
        string? rootPath, Persona? persona) =>
        _builtin.BuildCodeGraphContext(ownerId, projectId, sessionId, rootPath, persona);
    private MemoryMcpContext BuildMemoryContext(string ownerId, string personaId, string? projectId) =>
        _builtin.BuildMemoryContext(ownerId, personaId, projectId);
    private MemoryMcpContext? BuildTeamMemoryContext(string? ownerId, string? projectId) =>
        _builtin.BuildTeamMemoryContext(ownerId, projectId);

    // HttpMcpEnabled/HttpMcpActive здесь больше не нужны: их единственными callers были три
    // места сборки LlmSessionContext, а они с S3-5 живут в TurnLaunchContextFactory (там же и
    // вызов BuiltinMcpContexts.HttpMcpActive).

    private bool BrowserEnabled(string? ownerId, Persona? persona) => _builtin.BrowserEnabled(ownerId, persona);
    private bool HandsFor(string? projectId) => _builtin.HandsFor(projectId);
    private bool ServerContentFor(string? projectId) => _builtin.ServerContentFor(projectId);
    private string? LlmGatewayApiUrlFor(Session info, string? ownerId) => _builtin.LlmGatewayApiUrlFor(info, ownerId);

    // Единственное место чтения Llm:SystemPromptViaFile (нет ключа = файл включён)
    private bool SystemPromptViaFile =>
        !bool.TryParse(_config["Llm:SystemPromptViaFile"], out var viaFile) || viaFile;


    // Лежит ли транскрипт CLI чата на диске сервера (ADR-016). Тело переехало в CliRoute
    // (S3-2): оно нужно поиску, копированию и удалению транскрипта — это маршрут.
    private bool TranscriptOnServer(Session info) => _route.TranscriptOnServer(info);

    // --- Персистентность сессий ---

    // Кандидаты на auto-resume и маркеры обрыва ходов (S3-5): их держит TurnRecovery —
    // здесь остаётся точка, на которую смотрит hosted service авто-возобновления.
    public Task AbortMarkersWritten => _recovery.AbortMarkersWritten;

    // Загрузка sessions.json — делегируется стору; сборка поставляет колбэк восстановления очереди
    // (RestorePending); что делать с результатом загрузки (кандидаты auto-resume, маркеры
    // обрыва в историю) — решает TurnRecovery.
    private void LoadSessions()
    {
        // Колбэк загрузки — роль очереди: восстановление pending.json при старте сервера
        var loaded = _store.Load(_pending.Restore);
        _recovery.OnSessionsLoaded(loaded.RestartResumeCandidates, loaded.AbortedMidTurn);
    }

    internal static int MigratePersonaChatMode(IEnumerable<Session> sessions) =>
        SessionStore.MigratePersonaChatMode(sessions);

    // --- Персистентность очереди отложенных сообщений (pending.json) ---
    // Тела переехали в PendingQueueService (S3-2): срок годности восстановления, запись файла и
    // загрузка при старте — там; здесь только делегирующие обёртки для мест, которые ещё
    // живут в фасаде (создание чата, «Стоп», автодренаж после рестарта). Колбэк
    // SessionStore.Load(onLoaded) теперь указывает на PendingQueueService.Restore напрямую.

    private void AttachPendingPersistence(SessionEntry entry) => _pending.AttachPendingPersistence(entry);

    private void PersistPending(SessionEntry entry) => _pending.PersistPending(entry);

    // После того как авто-возобновление отработало: чаты без живого хода с восстановленной
    // незамороженной очередью разбираются штатным DrainNextPendingAsync; просроченные
    // сообщения получают плашку «не доставлено». Тело — в TurnRecovery (S3-5).
    public async Task DrainRestoredPendingAsync() => await _recovery.DrainRestoredPendingAsync();

    private async Task AppendPendingExpiredCardAsync(string sessionId, SessionEntry entry, List<QueuedMessage> expired) =>
        await _pending.AppendPendingExpiredCardAsync(sessionId, entry, expired);

    // Выдача кандидатов на auto-resume: список держит TurnRecovery (его заполняет
    // OnSessionsLoaded); возвращает и очищает — чтобы второй вызов не получил их повторно.
    public IReadOnlyList<string> TakeRestartResumeCandidates() => _recovery.TakeRestartResumeCandidates();

    // Счётчик вызовов SaveSessions (живёт в сторе) — для тестов «пачка пишет файл один раз» (ApplySubjects)
    internal int SaveSessionsCalls => _store.SaveSessionsCalls;

    private void FlushPendingSave() => _store.FlushPendingSave();

    // Тестовый хук: наполнить стор сессиями без полного CreateAsync (бенчмарк MarkRead).
    internal void AddSessionForTest(Session info)
    {
        var entry = new SessionEntry { Info = info };
        AttachPendingPersistence(entry);
        _sessions[info.Id] = entry;
    }

    // Публичный (волна В): TeamPlanService сохраняет каталог сессий после правки PlanCardId/
    // PlanVersion/Replanning — через тот же канал, что и TeamWaveService.
    public void SaveSessions() => _store.SaveSessions();




    internal string? EffectiveParentSessionId(Session s) => _store.EffectiveParentSessionId(s);

    // Только для тестов: запустить sweep-terminus (P12/P15) вне обычных триггеров SaveSessions,
    // чтобы детерминированно проверить переход Active→Finished по истечению grace. Прод-код
    // триггерит sweep каждым SaveSessions (включая автосохранение) — этого API тестам не нужно.
    internal void RunStuckActiveSweepForTests() => SaveSessions();

    // --- Публичное API ---

    public IReadOnlyCollection<Session> GetByProject(string projectId) => _store.GetByProject(projectId);

    public IReadOnlyList<Session> GetVisibleByProject(string projectId, string userId) =>
        _store.GetVisibleByProject(projectId, userId);

    public IReadOnlyList<string> GetSessionsWithLiveAgents(string ownerId) =>
        _store.GetSessionsWithLiveAgents(ownerId);

    public IReadOnlyList<string> GetSessionsWithLiveBgCommands(string ownerId) =>
        _store.GetSessionsWithLiveBgCommands(ownerId);

    // --- Настройки и жизненный цикл чата (S3-3) ---
    // Тела переехали в ChatSettings (docs/research/session-manager-split-2026-11.md §1):
    // архив и его автоправило, срок, темы проекта, закрепление и группировка, уведомления,
    // озвучка, пометки, контекст, собеседник и правка имени/модели. Здесь — тонкие
    // обёртки-делегаты: точки их вызова (контроллеры, вертикали, MCP) живут в фасаде до
    // следующих шагов S3; публичные сигнатуры не меняются. Инварианты роли (UpdatedAt не
    // двигают настройки; архивация и возврат — не активность) описаны в ChatSettings.

    // Чаты под автоправилом архивации — ЕДИНАЯ точка отбора для превью и для тика правила.
    public IReadOnlyList<Session> GetArchiveRuleCandidates(string ownerId, string? projectId, int days, DateTime nowUtc) =>
        _settings.GetArchiveRuleCandidates(ownerId, projectId, days, nowUtc);

    // Чистая часть предиката правила. Делегат держится: его зовут юнит-тесты через
    // SessionManager.MatchesArchiveRule.
    internal static bool MatchesArchiveRule(Session s, ITaskLookup? tasks, DateTime cutoff) =>
        ChatSettings.MatchesArchiveRule(s, tasks, cutoff);

    public int CountByProject(string projectId) => _store.CountByProject(projectId);

    /// <summary>Всего зарегистрированных сессий (для OTel gauge). ConcurrentDictionary.Count — thread-safe, sub-ms.</summary>
    public int ActiveCount => _sessions.Count;

    public IReadOnlyCollection<Session> GetProjectlessChats(string ownerId) =>
        _store.GetProjectlessChats(ownerId);

    // Закрепить/открепить чат. UpdatedAt не двигается: закрепление — настройка, а не активность.
    public bool SetPinned(string sessionId, bool pinned) => _settings.SetPinned(sessionId, pinned);

    // Ручная группировка чатов: назначить родителя либо вынести в корень (parentId == null).
    // Единственная точка записи ParentOverrideId/ParentDetached; чат при этом не «переезжает».
    public Session? SetParent(string sessionId, string? parentId, string ownerId) =>
        _settings.SetParent(sessionId, parentId, ownerId);

    // --- Темы чатов проекта ---
    // Тела — в ChatSettings (S3-3): единственная точка записи Session.Subject* и отбор
    // кандидатов разбора. Замок записи тем (_subjectsLock) переехал туда же: он держит
    // CAS «снимок прохода → запись» только над этими двумя методами.

    public IReadOnlyList<string> ApplySubjects(string projectId, IReadOnlyList<SubjectWrite> writes, DateTime nowUtc) =>
        _settings.ApplySubjects(projectId, writes, nowUtc);

    public IReadOnlyList<string> ReleaseOrphanedHumanSubjects(string projectId) =>
        _settings.ReleaseOrphanedHumanSubjects(projectId);

    public IReadOnlyList<Session> GetSubjectCandidates(string projectId, DateTime nowUtc, bool manual,
        DateTime? since = null, TimeSpan? idle = null, int minMessages = 0) =>
        _settings.GetSubjectCandidates(projectId, nowUtc, manual, since, idle, minMessages);

    // Убрать чат в архив (archived = true) / вернуть (false). Единственная точка записи
    // полей архива; UpdatedAt не трогает. SaveSessions здесь НЕ зовёт — сохраняющий вызов
    // (ручная архивация, пачка автоправила) делает одну запись на весь проход.
    public Session? SetArchived(string sessionId, bool archived, string by, string? batchId = null) =>
        _settings.SetArchived(sessionId, archived, by, batchId);

    // Разовая миграция чатов картинки v2 на v3 (ADR-019): архив + маркер MigratedAt.
    // Идемпотентна; возвращает число мигрированных чатов.
    public int ArchiveLegacyImageChats(DateTime nowUtc) => _settings.ArchiveLegacyImageChats(nowUtc);

    // Кто архивировал: не "user" и не "rule" — откат прохода автоправила такие чаты не вернёт.
    // Значение живёт в ChatSettings (S3-3), здесь остаётся публичное имя: его читает тест
    // миграции картинки v3 (ImageChatV3MigrationTests).
    public const string LegacyImageChatArchivedBy = Sessions.ChatSettings.LegacyImageChatArchivedBy;

    // Ручная архивация/возврат из архива (PUT /api/chats/{id}/archived, шаг 2 плана
    // «Архив чатов»): обёртка над SetArchived для точки входа из UI — гейт живости,
    // снятие срока временного чата, персист и событие ленты chat_archived. Автоправило
    // (ChatArchiveService) зовёт SetArchived напрямую: у него свой гейт отбора и одна
    // SaveSessions() на всю пачку.
    //
    // Гейт живости — только на архивацию: возврат ничего не рвёт, а «вернуть из архива,
    // пока идёт ход» — ровно то, что происходит при снятии архива активностью. 409 на
    // живом ходе отсюда уезжает InvalidOperationException (конвенция RestartWave и др.).
    public Task<Session?> SetArchivedAsync(string sessionId, string ownerId, bool archived) =>
        _settings.SetArchivedAsync(sessionId, ownerId, archived);

    // Проход автоправила архивации (ChatArchiveService, шаг 6 плана v4): заархивировать
    // пачку одним batchId — ОДНОЙ SaveSessions на весь проход (до 200 чатов; иначе каждая
    // архивация переписывала бы sessions.json целиком и дёргала sweep) и событием
    // chat_archived каждому чату. Гейт живости не повторяем: отбор кандидатов
    // (GetArchiveRuleCandidates) уже его отработал; кому-то стать живым между отбором и
    // записью — допустимая гонка, чат вернёт из архива собственная активность.
    public Task ArchiveBatchAsync(IReadOnlyCollection<string> sessionIds, string batchId) =>
        _settings.ArchiveBatchAsync(sessionIds, batchId);

    // Откат пачки автоправила из уведомления/раздела «Архив»: вернуть РОВНО чаты прохода
    // batchId (ArchivedBy="rule" и чат ещё в архиве), а не всю историю правила. Одна
    // SaveSessions на пачку, событие возврата каждому. Владелец — по GetOwned: батч-id
    // приходит из URL, чужой не должен возвращать чужие чаты (даже угаданный).
    public Task<int> RestoreArchiveBatchAsync(string ownerId, string batchId) =>
        _settings.RestoreArchiveBatchAsync(ownerId, batchId);

    // Включить/выключить временность чата: minutes > 0 — авто-удаление через N минут
    // после последней активности, null — обычный чат.
    //
    // UpdatedAt намеренно НЕ обновляется (как в SetParent): по нему сортируется список и
    // считается непрочитанность, а смена настройки хранения — не активность чата. Отсчёт
    // срока при этом не должен стартовать в прошлом, поэтому включение ставит ExpiryAnchor —
    // дедлайн считается от него, если он позже последней активности.
    public Session? SetExpiry(string sessionId, int? minutes) => _settings.SetExpiry(sessionId, minutes);

    // Сужение прав чата-исполнителя слепком этапа (И5): запреты инструментов и подмножество
    // ключей MCP; вызывается один раз при создании сессии, до первого хода.
    public Session? SetStageLimits(string sessionId, IReadOnlyList<string>? disallowedTools, IReadOnlyList<string>? mcpAllow) =>
        _settings.SetStageLimits(sessionId, disallowedTools, mcpAllow);

    // Заморозить срок чата паузой фичи (W4-HOLD-E/J, хвост N): снимаемое значение сохраняется
    // в HoldFrozenExpiresAfterMinutes — возврат после снятия паузы восстанавливает срок самого
    // чата, а не задачи (R-HOLD-J п.3). Идемпотентна: повторная пауза той же записи не трогает
    // уже снятый срок. UpdatedAt, как в SetExpiry, не двигается: настройка хранения — не активность.
    public Session? FreezeExpiryForHold(string sessionId) => _settings.FreezeExpiryForHold(sessionId);

    // Вернуть замороженный срок: якорь — «сейчас» (как SetExpiry), сохранённое значение гасится.
    // Нечего возвращать (чат был вечным или не замораживался) — состояние не меняется.
    public Session? RestoreFrozenExpiry(string sessionId) => _settings.RestoreFrozenExpiry(sessionId);

    // Запись модуля в ленту вне хода (ADR-019 §2, шов IChatFeed): история + живая пара
    // module_record. Активность, как тихие строки v2: UpdatedAt двигается, чат выходит из
    // архива. Timestamp ставит сервер, если модуль его не задал. false — чата нет.
    public Task<bool> AppendModuleRecordAsync(string sessionId, StoredModuleRecord record) =>
        _settings.AppendModuleRecordAsync(sessionId, record);

    // Первая отметка сообщения чата: тело в сторе (S3-1).
    private void ClaimFirstMessage(SessionEntry entry) => _store.ClaimFirstMessage(entry);

    // --- Видимость чата (S3-3) ---
    // Тела — в ChatVisibility: тумблер приватности с переносом вложений между раскладками,
    // сохранение вложения в нужную раскладку, вышибание чужих соединений, opt-out паспортов.
    // Замок VisibilityLock — составляющая записи SessionEntry (по сессии), ключ не менялся.

    public ChatVisibilityResult SetChatVisibility(string sessionId, string userId, bool makePrivate) =>
        _visibility.SetChatVisibility(sessionId, userId, makePrivate);

    public string? SaveChatAttachment(string sessionId, string callerId, string? fileName, Action<string> write) =>
        _visibility.SaveChatAttachment(sessionId, callerId, fileName, write);

    public Task EvictForeignViewersAsync(string sessionId, string authorId) =>
        _visibility.EvictForeignViewersAsync(sessionId, authorId);

    public bool TrySetExcludeFromDossiers(string sessionId, bool value) =>
        _visibility.TrySetExcludeFromDossiers(sessionId, value);

    // Заглушить/включить уведомления по чату (браузерные «нужно решение» / «ход завершён»);
    // UpdatedAt — настройка, а не активность.
    public Session? SetNotificationsMuted(string sessionId, bool muted) => _settings.SetNotificationsMuted(sessionId, muted);

    // Включить/выключить голосовой режим чата и/или сменить стиль озвучки.
    // Один сеттер на оба поля намеренно: каждый дёргает SaveSessions(), то есть перезапись
    // всего списка — два раздельных дали бы две записи файла на один PUT.
    // null-аргумент = поле не трогаем: стиль приезжает и БЕЗ флага (устройство выправляет
    // чужой стиль у чата с уже включённой озвучкой), а такой запрос не должен её гасить.
    // UpdatedAt не трогаем по той же причине, что в SetExpiry: это настройка, а не активность.
    public Session? SetVoiceMode(string sessionId, bool? on, string? style = null) =>
        _settings.SetVoiceMode(sessionId, on, style);

    // Отметить чат прочитанным (синк непрочитанности между устройствами). UpdatedAt не трогаем:
    // прочтение — не активность чата. false — сессии нет или она не принадлежит owner'у (404).
    public bool MarkRead(string sessionId, string ownerId) => _settings.MarkRead(sessionId, ownerId);

    public bool IsPrivateChat(string sessionId) => _store.IsPrivateChat(sessionId);

    // Пометка человека для отбора чатов в вики (ADR-035 §12а): Important / Skip / null — снять.
    // Шов ISessionDirectory (зовёт вертикаль Notes, ADR-014).
    public bool SetWikiMark(string sessionId, string? mark) => _settings.SetWikiMark(sessionId, mark);

    // Сменить состав контекста чата (фича chat-context): идемпотентная замена списка целиком;
    // валидацию записей делает контроллер ДО этого вызова.
    public Task<Session?> SetContextAsync(string sessionId, IReadOnlyList<SessionContextEntry> entries) =>
        _settings.SetContextAsync(sessionId, entries);

    // Контекст чата, принадлежащего пользователю: для GET-эндпоинта и (вслед за ним)
    // тула context_list. null — чужая/несуществующая сессия.
    public IReadOnlyList<SessionContextEntry>? GetContext(string sessionId, string ownerId) =>
        _settings.GetContext(sessionId, ownerId);

    public IReadOnlyCollection<Session> GetAll() => _store.GetAll();

    // Разовая переадресация закреплённых моделей (миграция каталога провайдера): id из карты
    // заменяется, всё остальное — включая незнакомые модели и «preset:{id}» — остаётся как есть.
    // Возвращает число изменённых чатов; 0 — стор на диск не переписывается.
    // Идёт через живой реестр, а не файл: иначе первый же SaveSessions вернул бы старые id.
    public int RemapModels(IReadOnlyDictionary<string, string> map) => _settings.RemapModels(map);

    // То же с произвольным сведением: map(id) → новое значение или null (не менять).
    public int RemapModels(Func<string, string?> map) => _settings.RemapModels(map);

    // Рабочая папка чата, принадлежащего пользователю (для загрузки вложений): у чата вне
    // проекта — {дом}/Chats, у проектного — рабочая папка сессии (worktree, иначе корень
    // проекта), чтобы Claude нашёл вложение по относительному пути из своего cwd.
    // null — чужая/несуществующая сессия либо папку определить не удалось.
    // Маршрут чата (рабочая папка, провайдер/модель, профиль CLI, база деревьев) переехал в
    // CliRoute (S3-2, docs/research/session-manager-split-2026-11.md §1). Здесь — тонкие
    // обёртки-делегаты: точки их вызова (создание чата, worktree, вложения, контекст хода)
    // живут в фасаде до следующих шагов S3; публичные сигнатуры не меняются.

    public string? GetChatRoot(string sessionId, string ownerId) => _route.GetChatRoot(sessionId, ownerId);

    // Выбрать подписку Claude для новой сессии: если модель Claude (не сторонний провайдер),
    // выбираем из пула подписок (least-loaded из способных обслужить модель); для сторонних — по модели.
    private string ResolveSubscriptionProvider(string? model) => _route.ResolveSubscriptionProvider(model);

    // Модель места по назначению (слоты + таблица назначений + per-user слоты). Подставляется,
    // только когда модель НЕ задана явно и это НЕ resume.
    private string? ResolveDefaultModel(string usageKey, string? model, string? resumeSessionId, string? ownerId) =>
        _route.ResolveDefaultModel(usageKey, model, resumeSessionId, ownerId);

    // Место применения по признакам сессии — тот же порядок, что у ClaudeSession.UsageKey.
    private static string UsageKeyFor(bool taskExecution, string? taskId, string? personaId) =>
        CliRoute.UsageKeyFor(taskExecution, taskId, personaId);

    // Рабочая папка чата вне проекта: {домашняя папка владельца}/Chats (создаётся при отсутствии)
    private string ResolveChatRoot(string ownerId) => _route.ResolveChatRoot(ownerId);

    // Корень профиля CLI С УЧЁТОМ СРЕДЫ владельца (хостовый/песочный). Хостовая раскладка —
    // в CliRoute.ConfigRootForProvider; наружу всегда идёт только через ConfigRootFor.
    private string ConfigRootFor(string? ownerId, string? providerKey) => _route.ConfigRootFor(ownerId, providerKey);

    // База деревьев проекта: {home}/.worktrees/<slug проекта>. Одна формула для деревьев чатов
    // и слотов ревью — в CliRoute.WorktreesBase.
    internal string WorktreesBase(string? ownerId, Project project) => _route.WorktreesBase(ownerId, project);

    // Рабочая папка ГЛАЗАМИ CLI (для container-пользователя — контейнерный путь).
    internal string CwdForOwner(string? ownerId, string hostCwd) => _route.CwdForOwner(ownerId, hostCwd);

// Этап 4 / шаг 1в (боевой с 1в, идейно с 1б): подписчик turn/completed для штаба.
// Замещает прямой вызов HandleTeamTurnEndAsync из OnMessageAsync: изымает план
// (text/failed/asked) из LastTeamTurnEnds и асинхронно зовёт HandleTeamTurnEndAsync.
//
// Контракт фильтра по Outcome (сверка исходов живёт в коммите f049a593 и его переносе
// в docs/research/session-core-split-2026-09.md, §4, шаг 2в):
// - success | failed | egress_down | local_down — изымаем план и зовём штаб;
// - crashed — решает НЕ исход, а факт «терминал хода дошёл downstream», материализованный
//   планом в слоте (см. развилку ниже);
// - interrupted — штаб НЕ зовём, но восстанавливаем отсечки сторожа волн
//   (RestoreWaveWatchdogIfPaused) — здесь. На штатном ходе это no-op: HandleTeamTurnEndAsync
//   вернёт отсечки сам (см. 7593), вызов здесь идемпотентен.
// - cancelled — downstream ничего не получает (return до SettleAsync), сторож не трогаем.
// Если бы на interrupted сработал штаб, воспроизвёлся бы продовый дефект «фантомная
// эскалация»: обе точки прерывания чистят буфер маркеров (PreemptTurnForQueue, «Стоп»), но
// НЕ слот — пришедший следом терминал положил бы план с пустым текстом, и разбор поднял бы
// карточку молчаливого тупика поверх прерванного хода.
//
// Почему crashed разведён на два случая. Под одним исходом живут два разных пути финала
// в FallbackLlmSessionAdapter, и различает их не Outcome, а то, увидела ли лента конец хода:
// - сбой оркестрации ПОСЛЕ провальной попытки доставки → FailClosedAsync отдаёт downstream
//   ErrorMessage(ExpectResultFollows=true) + ResultMessage("error"). OnMessageAsync видит
//   терминал, осушает буфер маркеров и кладёт план — ход для человека состоялся, маркеры
//   обязаны быть разобраны (до перевода на подписку это делал прямой вызов из OnMessageAsync);
// - сбой ДО первой попытки (lastEnd=null, в hold'е пусто) → SettleAsync, downstream не
//   получает ничего, плана нет — разбирать нечего.
// Отсюда правило: на crashed пробуем ЗАБРАТЬ план и решаем по нему, а не по исходу. Отсутствие
// плана здесь — штатный случай (не WARN), в отличие от success/failed/egress_down/local_down,
// где терминал downstream гарантирован и его пропажа означает сбой проводки.
//
// Двойной терминал одного хода (ErrorMessage{ExpectResultFollows=true} + ResultMessage) в
// OnMessageAsync обе попытки кладут план по тому же TurnSeq; первая запись выигрывает
// (RecordTeamTurnEnd внутри ContainsKey → return), вторая — no-op. Шина публикует
// turn/completed строго ОДИН раз на ход, и подписчик забирает план ровно один раз —
// поэтому дедуп SkipNextTeamTurnEnd в OnMessageAsync ушёл вместе с переключением.
private Task HandleTeamTurnCompletedShim(TurnCompleted e) =>
        _teamTurnCompletion.HandleTeamTurnCompletedAsync(e);

    // Рабочая папка сессии: отдельное worktree чата приоритетнее корня проекта.
    // Единая точка подмены cwd — через неё идут обе funnel-точки LlmSessionContext.
    // internal — той же формулой DifyToolset резолвит дефолтный датасет проекта чата
    // (волна 4): расхождение формул означало бы разный состав tools/list и shape.
    // Тело переехало в CliRoute (S3-2); обёртка сохранена — на неё сидят внешние вызовы.
    internal static string EffectiveRoot(Session session, string fallbackRoot) =>
        CliRoute.EffectiveRoot(session, fallbackRoot);

    // Доступ к истории для вертикали Services.Team через шов: TeamStateService читает
    // историю неактивного чата, но идёт через этот метод, а не через прямую ссылку на
    // ChatHistoryService — иначе сторож границ краснеет. Тело — в SessionHistory (S3-2).
    internal async Task<TeamImplementPlan?> ReadStoredTeamPlanAsync(string claudeSessionId, string planId,
        bool onlyUnresolved = false) =>
        await _sessionHistory.ReadStoredTeamPlanAsync(claudeSessionId, planId, onlyUnresolved);

    // Корень, куда «Командная реализация» пишет файл полного плана (Э8-доп., 2026-08-02):
    // worktree штаба, если он в нём работает, иначе корень проекта. null — чат вне проекта,
    // писать план некуда (глобальный чат — раздел «Состав команды» продуктового плана).
    // Тело переехало в TeamStateService (волна А); обёртка снята (волна 3, шаг 2г-4):
    // TeamPlanService зовёт TeamStateService.ResolveTeamPlanRoot напрямую.
    internal string? ResolveTeamPlanRoot(Session session) => _teamState.ResolveTeamPlanRoot(session);

    // Рабочая папка сессии (для поиска транскрипта по уплощённому cwd);
    // null — папку определить не удалось (миграцию в этом случае не делаем). Тело — в CliRoute (S3-2).
    private string? TryResolveCwd(Session s) => _route.TryResolveCwd(s);

    // Корни профилей CLI для поиска транскрипта чата (тело — в CliRoute, S3-2): тот же список
    // нужен и при ветвлении (поиск самой длинной копии), и при архивации/удалении.
    private IEnumerable<string> TranscriptSearchRoots(Session info) => _route.TranscriptSearchRoots(info);

    // Карточка «Продолжить на …»: чат родного Claude упёрся в лимит, внутри пула
    // автофейловер (TryPoolFailover) не переключил. Предлагаем варианты: здоровые
    // аккаунты ТОГО ЖЕ пула (пользователь выбирает сам — TryPoolFailover либо уже
    // проверил их все на исчерпание, либо переключаться было некуда) и настроенные
    // сторонние провайдеры. Эфемерно (в history не пишется): после сброса окна
    // предложение неактуально. internal — тестируется без розыгрыша целого хода
    // (см. OfferProviderFallbackAsync_*Tests).
    // Смена провайдера у чата (S3-3): единственная точка — в ProviderMigration; здесь
    // обёртки с прежними сигнатурами. Карточка «Продолжить на …» (OfferProviderFallbackAsync)
    // вызывается из петли сообщений хода, которая ещё живёт здесь.
    internal Task OfferProviderFallbackAsync(string sessionId, string? resetsAt)
        => _providerMigration.OfferProviderFallbackAsync(sessionId, resetsAt);

    public Task<Session> MigrateProviderAsync(string sessionId, string ownerId, string? model,
        string? subscriptionKey = null)
        => _providerMigration.MigrateProviderAsync(sessionId, ownerId, model, subscriptionKey);

    // --- Создание и порождение чата (S3-5) ---
    // Тела ушли в Services/Sessions/ChatCreation.cs; сигнатуры фасада прежние: на них сидят
    // контроллеры и тулсеты. Типы ChatBranchInclude / ChatBranchResult живут в ядре
    // (Services/Sessions/SessionCoreContracts.cs).
    public async Task<ChatBranchResult> BranchAsync(string sessionId, string ownerId,
        int userMessageIndex, string anchorText, string include, string? name = null)
        => await _creation.BranchAsync(sessionId, ownerId, userMessageIndex, anchorText, include, name);

    public async Task<Session> CreateAsync(string projectId, ClaudeMode mode,
        string? resumeSessionId = null, string? name = null, string? model = null, string? agentName = null,
        string? effort = null, string? personaId = null, bool taskExecution = false, string? taskId = null,
        string? onboardingKind = null, string? createdByUserId = null, string? privateToUserId = null)
        => await _creation.CreateAsync(projectId, mode, resumeSessionId, name, model, agentName, effort,
            personaId, taskExecution, taskId, onboardingKind, createdByUserId, privateToUserId);

    public async Task<Session> CreateChatAsync(string ownerId, ClaudeMode mode,
        string? resumeSessionId = null, string? name = null, string? model = null, string? effort = null,
        string? personaId = null, bool taskExecution = false, string? taskId = null,
        string? onboardingKind = null)
        => await _creation.CreateChatAsync(ownerId, mode, resumeSessionId, name, model, effort,
            personaId, taskExecution, taskId, onboardingKind);

    public async Task<Session> CreatePersonaChatAsync(string ownerId, string personaId,
        ClaudeMode mode, string? resumeSessionId = null, string? name = null,
        string? contextProjectId = null, string? automationRuleId = null,
        string? createdByUserId = null, string? privateToUserId = null)
        => await _creation.CreatePersonaChatAsync(ownerId, personaId, mode, resumeSessionId, name,
            contextProjectId, automationRuleId, createdByUserId, privateToUserId);

    public async Task<Session> CreateGroupChatAsync(string ownerId, IReadOnlyList<string> personaIds,
        ClaudeMode mode, string? name = null, string? createdByUserId = null)
        => await _creation.CreateGroupChatAsync(ownerId, personaIds, mode, name, createdByUserId);

    public IReadOnlyList<Session> GetPersonaChats(string ownerId, string personaId, string viewer)
        => _creation.GetPersonaChats(ownerId, personaId, viewer);

    public Session? GetById(string id) => _store.GetById(id);

    public TurnDelegationState GetActiveTurnDelegation(string sessionId, string ownerId) =>
        _store.GetActiveTurnDelegation(sessionId, ownerId);


    private static void NoteRunDelegation(SessionEntry entry, int agentDepth, bool suppressTasksExecute,
        bool fromHuman) => SessionStore.NoteRunDelegation(entry, agentDepth, suppressTasksExecute, fromHuman);

    // Актор хода (S2a-1). Пишется ТОЛЬКО в двух точках подачи (SendDirectAsync, SendMessageAndWaitAsync),
    // вызывающие передают его параметром senderUserId (по образцу senderPersonaId): запись в самих
    // вызывающих оставила бы устаревшего актора на каждом пропуске. Запись безусловная: каждая подача
    // перезаписывает оба поля, устаревший актор её не переживает; под PendingLock (читают потоки MCP-вызовов). Подача без инициатора
    // (null) у внутренних ходов (дренаж, WorkLoop, nudge, resume) наследует «липкого» актора прогона,
    // пока живы его фоновые агенты; у остальных пишется UnknownActor (fail-closed).
    internal const string UnknownActor = SessionStore.UnknownActor;



    private static string? HumanAuthorOf(string? senderUserId, int agentDepth, string? senderPersonaId) =>
        SessionStore.HumanAuthorOf(senderUserId, agentDepth, senderPersonaId);

    private void NoteTurnActor(SessionEntry entry, string? actor, bool fromHuman) =>
        _store.NoteTurnActor(entry, actor, fromHuman);

    public string? GetTurnActor(string sessionId, string ownerId) => _store.GetTurnActor(sessionId, ownerId);

    public Access.TurnFrameKind GetTurnFrame(string? sessionId, string ownerId) =>
        _store.GetTurnFrame(sessionId, ownerId);

    // Живой ход чата начал человек (защёлка траты) — тело в SessionStore.
    public bool IsTurnFromHuman(string sessionId, string ownerId) => _store.IsTurnFromHuman(sessionId, ownerId);

    public bool IsOwnerOrTurnAuthor(string sessionId, string userId) =>
        _store.IsOwnerOrTurnAuthor(sessionId, userId);

    public string? PendingSentBy(string sessionId, string messageId) =>
        _store.PendingSentBy(sessionId, messageId);

    string? Access.ITurnActorRegistry.Actor(string sessionId, string ownerId) => GetTurnActor(sessionId, ownerId);
    Access.TurnFrameKind Access.ITurnFrameSource.Frame(string? sessionId, string ownerId) => GetTurnFrame(sessionId, ownerId);

    public bool HasActiveWorkLoop(string sessionId, string ownerId) =>
        _store.HasActiveWorkLoop(sessionId, ownerId);

    // Запомнить заметку-итог сессии (SessionSummaryService) — для обновления при повторной генерации
    public void SetSummaryNoteId(string sessionId, string noteId) => _settings.SetSummaryNoteId(sessionId, noteId);

    // Кэш сводки карточки архива (ChatDigestService, место chat-digest): текст и момент
    // сборки — при UpdatedAt > ArchiveSummaryAt сводка не актуальна (см. Session).
    // UpdatedAt намеренно не двигается: сборка сводки — не активность чата, иначе она
    // сама снимала бы архив и поднимала чат в списке. null — сбросить кэш.
    public void SetArchiveSummary(string sessionId, string? summary) => _settings.SetArchiveSummary(sessionId, summary);

    // Пометить пути чатов «правки зафиксированы в git» (детект сдвига HEAD —
    // CommitAttributionService). Bulk: ОДИН SaveSessions на весь коммит — SaveSessions
    // перезаписывает весь sessions.json, звать его в цикле по чатам недопустимо.
    // Paths — пути коммита, пересечённые с сырым множеством чата (считает вызывающий);
    // RawPaths — само сырое множество: пометки, выпавшие из него (история переписана),
    // вычищаются здесь же — на чтении индекс их только игнорирует, стор не переписывая.
    // Инвариант: UpdatedAt не двигаем (по нему сортировка и непрочитанность,
    // а фиксация правок — не активность чата). Пути — нормализованные (lowercase,
    // прямые слэши); список подменяется целиком, а не мутируется на месте, чтобы
    // конкурентная сериализация SaveSessions не увидела список в полразборе.
    // Рабочая папка чата (S3-3): пометка «зафиксировано в git» и её снятие — тело в ChatWorktree.
    public void MarkFilesCommitted(
        IReadOnlyList<(string SessionId, IReadOnlyCollection<string> Paths, IReadOnlyCollection<string> RawPaths)> batch)
        => _worktree.MarkFilesCommitted(batch);

    // Вернуть путь в учёт атрибуции: чат снова правит файл после фиксации. Сидит на
    // ГОРЯЧЕМ пути (каждое write-сообщение хода) — ранний выход без записи, когда
    // пометки нет. path — уже нормализованный (SessionChangedPaths.Normalize).
    // UpdatedAt не двигаем (см. MarkFilesCommitted).
    public void UnmarkFileCommitted(string sessionId, string path) => _worktree.UnmarkFileCommitted(sessionId, path);

    /// <summary>Видна ли персоне работа в проекте (зона Global/Project/Sphere) — единая проверка чатов.</summary>
    public bool PersonaVisibleIn(Persona persona, string? projectId) => _settings.PersonaVisibleIn(persona, projectId);

    /// <summary>Отказ хода, если проект вышел из сферы персоны; null — ход разрешён (см. <see cref="PersonaZone.OutOfZoneRefusal"/>).</summary>
    public string? OutOfZoneRefusal(Persona persona, string? projectId) => _settings.OutOfZoneRefusal(persona, projectId);

    // Обновить состав участников группового чата. Активный спикер сохраняется,
    // если остался в составе, иначе — новая ведущая. Адаптер пересоздаётся
    // (состав участников зашит в подсказку @упоминаний и групповой слой промпта).
    public Session? SetParticipants(string sessionId, string ownerId, IReadOnlyList<string> personaIds,
        string? callerUserId = null) =>
        _settings.SetParticipants(sessionId, ownerId, personaIds, callerUserId);

    public string? ResolveOwnerId(Session s) => _store.ResolveOwnerId(s);

    private bool VisibleTo(Session s, string userId) => _store.VisibleTo(s, userId);

    public bool IsVisibleTo(string sessionId, string? userId) => _store.IsVisibleTo(sessionId, userId);

    public bool IsTranscriptReservedForOthers(string csid, string userId) =>
        _store.IsTranscriptReservedForOthers(csid, userId);

    public bool HasSessionsOwnedBy(string ownerId) => _store.HasSessionsOwnedBy(ownerId);

    public IReadOnlyCollection<Session> GetAllOwnedBy(string ownerId) => _store.GetAllOwnedBy(ownerId);

    public IReadOnlyCollection<Session> GetAllOwnedVisibleBy(string userId) =>
        _store.GetAllOwnedVisibleBy(userId);

    // Живая персона чата для цепочки фолбэка (ClaudeSession.EffectiveTurnChain →
    // ResolveChain с матрицами персоны): перечитывается на каждый ход — правка матриц
    // персоны/специальности применяется со следующего хода без пересоздания адаптера.
    // --- Тонкие обёртки слоя персоны (S3-4) ---
    // Тела ушли в Services/Sessions/PersonaLayerBuilder.cs; сигнатуры фасада прежние:
    // на них сидят точки сборки контекста здесь и внешние потребители (PersonasToolset,
    // ProjectsController). BuildPersonaLayer остаётся здесь: это точка сборки слоя
    // в контексте хода, а не правило персоны.
    private Func<Persona?> BuildPersonaProvider(Session session, string? ownerId) =>
        _persona.BuildPersonaProvider(session, ownerId);
    private Func<string, Task<string?>>? BuildBindingsProvider(string? ownerId, string? personaId,
        IReadOnlyList<string>? mountedSections) =>
        _persona.BuildBindingsProvider(ownerId, personaId, mountedSections);
    public void InvalidateHandsSessions(string ownerId, string? projectId) =>
        _persona.InvalidateHandsSessions(ownerId, projectId);
    private void InvalidatePersonaSessions(string personaId) => _persona.InvalidatePersonaSessions(personaId);
    // ResolveOtherPersonas/SplitConsultants сняты: их единственный вызывающий — Omc-блок
    // BuildCliTurnText — переехал в PersonaLayerBuilder целиком (подготовка к S3-7).
    internal bool ConsultantsEnabled(string? ownerId, Session session, Persona? persona) =>
        _persona.ConsultantsEnabled(ownerId, session, persona);
    internal bool PersonasEnabled(string? ownerId, Session session, Persona? persona) =>
        _persona.PersonasEnabled(ownerId, session, persona);
    internal bool MentionsToolsEnabled(string? ownerId, Session session, Persona? persona) =>
        _persona.MentionsToolsEnabled(ownerId, session, persona);
    private Func<PersonaAgentsContext?>? BuildPersonaAgentsProvider(string? ownerId, Session session, Persona? persona) =>
        _persona.BuildPersonaAgentsProvider(ownerId, session, persona);
    internal string? BuildMentionsHint(string ownerId, Session session, List<Persona> others) =>
        _persona.BuildMentionsHint(ownerId, session, others);
    private void AppendPersonaLines(System.Text.StringBuilder sb, List<Persona> personas, string? currentProjectId) =>
        _persona.AppendPersonaLines(sb, personas, currentProjectId);
    private PersonasMcpContext? BuildPersonasContext(string? ownerId, string? projectId, Session session, Persona? persona = null) =>
        _persona.BuildPersonasContext(ownerId, projectId, session, persona);
    private IReadOnlyList<string>? BuildExtraDisallowed(string? ownerId, Persona? persona, Session session) =>
        _persona.BuildExtraDisallowed(ownerId, persona, session);

    // Персона-слой сессии: контекст памяти (для долгой памяти персоны/команды) + сама персона
    // для гейтов возможностей. Промпт характера + групповая надстройка + онбординг-оверлей —
    // у IPromptSectionContributor (PersonaLayerContributor).
    private (MemoryMcpContext? Memory, Persona? Persona)
        BuildPersonaLayer(Session session, string? ownerId)
    {
        if (session.PersonaId is null || ownerId is null) return (null, null);
        var persona = _personas.Get(session.PersonaId, ownerId);
        if (persona is null) return (null, null);
        // Долгая память — только если включена у персоны
        if (!persona.MemoryEnabled) return (null, persona);
        // team_memory_* (③-3.4, диета памяти команды ч.3) — по проекту ТЕКУЩЕГО чата, не по
        // scope персоны: состав MCP-инструментов один и тот же у проектных и глобальных персон
        // (инвариант «tools/list не зависит от хода» — тем более не от того, какая персона),
        // а пишет ли персона в команду — решает бэкенд (ProjectsController.TeamMemoryWriteAllowed:
        // Persona.Scope==Project && Persona.ProjectId==id проекта памяти). Глобальная персона в
        // проектном чате получает team_memory_list/search (read-only), персона другого проекта —
        // так же; вне проектного чата (session.ProjectId пуст) команды памяти нет вообще.
        return (BuildMemoryContext(ownerId, persona.Id, session.ProjectId), persona);
    }

    // Id-заглушка пустой зоны: ни один проект владельца под ним не живёт. Определение —
    // в роли (единственная точка, где зона строится), здесь — тот же идентификатор контракта.
    internal const string NoProjectsZone = Sessions.ExternalMcpContexts.NoProjectsZone;

    // --- Тонкие обёртки внешних серверов и модулей (S3-4) ---
    // Тела ушли в Services/Sessions/ExternalMcpContexts.cs и ModulesMcpContexts.cs;
    // сигнатуры фасада прежние: на них сидят точки сборки контекста хода.
    private WorkspaceMcpContext? BuildWorkspaceContext(string? ownerId, string? projectId,
        string? selfSessionId, Persona? persona) =>
        _external.BuildWorkspaceContext(ownerId, projectId, selfSessionId, persona);
    internal WorkspaceMcpPlan? BuildWorkspacePlan(string ownerId, string? projectId, Persona? persona) =>
        _external.BuildWorkspacePlan(ownerId, projectId, persona);
    private ModulesMcpContext? BuildModulesContext(string? ownerId) => _moduleContexts.BuildModulesContext(ownerId);
    public Func<IReadOnlyList<SessionContextEntry>> BuildChatContextProvider(string sessionId) =>
        _external.BuildChatContextProvider(sessionId);
    private Func<ExternalMcpContext?>? BuildExternalMcpProvider(string? ownerId, string? projectId, Persona? persona, string? sessionId) =>
        _external.BuildExternalMcpProvider(ownerId, projectId, persona, sessionId);
    private Dictionary<string, string> ResolveSecretValues(string ownerId, Dictionary<string, string>? map) =>
        _external.ResolveSecretValues(ownerId, map);
    private bool TryApplyAuthHeaders(string ownerId, McpServerRecord record, Dictionary<string, string> headers) =>
        _external.TryApplyAuthHeaders(ownerId, record, headers);
    private NotificationsMcpContext? BuildNotificationsContext(string? ownerId, string? personaId, Persona? persona) =>
        _external.BuildNotificationsContext(ownerId, personaId, persona);


    /// <summary>
    /// Персона, назначаемая чату, обязана быть видна в рамке этого чата (доработка R-S2a-8):
    /// каждая точка назначения обязана собрать набор персон и позвать этот метод до записи.
    /// Полное описание рамки и точек входа — в ChatSettings (S3-3).
    /// </summary>
    public void EnsurePersonasVisibleInFrame(string? projectId, IEnumerable<Persona?> personas) =>
        _settings.EnsurePersonasVisibleInFrame(projectId, personas);

    // Назначить/сменить собеседника чату (единый селектор): персону (personaId) ИЛИ
    // стандартного .md-агента Claude (agentName) — взаимоисключающе. Оба пустые = снять.
    // Разрешено и ПО ХОДУ разговора: персона-слой строится на каждый ход, транскрипт
    // продолжается через --resume с новым системным слоем. Модель/усилие подтягиваются
    // из персоны; у начатой сессии — только при том же провайдере: смена собеседника
    // транскрипт не перевозит (в отличие от явной смены модели), а уводить ход в чужой
    // профиль CLI молча нельзя — модель персоны просто не применяется.
    public Session? SetPersona(string sessionId, string ownerId, string? personaId, string? agentName = null) =>
        _settings.SetPersona(sessionId, ownerId, personaId, agentName);

    // Запомнить персону, созданную в ходе онбординг-сессии (через personas_create из чата
    // мастера/наставника). Финализация (FinalizeOnboardingAsync) досевает профиль дефолта
    // только ей: выбранная существующая персона прав не получает.
    public void SetOnboardingCreatedPersona(string sessionId, string ownerId, string personaId) =>
        _settings.SetOnboardingCreatedPersona(sessionId, ownerId, personaId);

    // Пометить онбординг-сессию финализированной (PersonasController.FinalizeOnboardingAsync):
    // повторный make-default из живой сессии после этого — no-op без второго события в ленте.
    public void SetOnboardingFinalized(string sessionId, string ownerId) =>
        _settings.SetOnboardingFinalized(sessionId, ownerId);

    // Общее ядро смены собеседника/спикера и роутинг спикера группового чата — тело в
    // ChatSettings (S3-3; роутинг опущен туда же в S3-7 — политика спикера принадлежит
    // настройкам, приём хода остаётся только точкой вызова).

    // Приём сообщения (S3-7, аддендум §1): ход в сессии запускает только ядро TurnIntake —
    // очередь, слот «занят» и пин там же одной атомарной операцией. Фасад держит прежнюю
    // публичную сигнатуру и переадресует; поведение (очередь/прерывание/снимок статуса) —
    // в комментарии ядра приёма.
    public Task<SendUserOutcome> SendMessageAsync(string sessionId, string text, IReadOnlyList<string> attachedPaths, string? mode = null, bool systemDirective = false, bool auto = false, string? senderPersonaId = null, bool suppressTasksExecute = false, string? senderOrigin = null, string? senderConnectionId = null, string? staffNote = null, DeliveryCause cause = DeliveryCause.Unknown, int agentDepth = 0, string? senderUserId = null)
        => _intake.SendMessageAsync(sessionId, text, attachedPaths, mode, systemDirective, auto,
            senderPersonaId, suppressTasksExecute, senderOrigin, senderConnectionId, staffNote, cause, agentDepth, senderUserId);

    // Превью чата и сигнал после «Стоп» (S3-7): владелец — ядро приёма; фасад держит имена
    // для потребителей (карточка списка и тесты зовут их по типу фасада).
    internal static string ChatPreview(string text) => Sessions.TurnIntake.ChatPreview(text);

    internal const string UserStopNoteText = Sessions.TurnIntake.UserStopNoteText;

    // Текст хода для CLI (S3-7): тело — в ядре приёма (обвязки — часть состава хода).
    // Обёртка держит имя и сигнатуру: VoiceLocalTurnTests зовёт BuildCliTurnText
    // reflection-ом по типу фасада.
    private string BuildCliTurnText(SessionEntry entry, string text, bool auto, bool systemDirective, string? senderPersonaId)
        => _intake.BuildCliTurnText(entry, text, auto, systemDirective, senderPersonaId);

    // Локальный голосовой ход (S3-6): тела BuildVoiceContextBlock, ShouldRunLocalVoice,
    // BuildVoiceMessages, TrimVoiceMessage, RunLocalVoiceTurnAsync переехали в LocalVoiceTurn.
    // Здесь остаются делегирующие обёртки: приём хода (SendDirectAsync) решает локальность и
    // поднимает ход теми же вызовами; VoiceLocalTurnTests зовут ShouldRunLocalVoice reflection-ом
    // по типу фасада — обёртка держит имя и сигнатуру.
    private static string? BuildVoiceContextBlock(TurnAccumulator acc) =>
        Sessions.LocalVoiceTurn.BuildVoiceContextBlock(acc);

    private bool ShouldRunLocalVoice(SessionEntry entry, bool auto, bool systemDirective,
        IReadOnlyList<string> attachedPaths) =>
        _voice.ShouldRunLocalVoice(entry, auto, systemDirective, attachedPaths);

    private async Task RunLocalVoiceTurnAsync(string sessionId, SessionEntry entry) =>
        await _voice.RunLocalVoiceTurnAsync(sessionId, entry);


    // Отправка сообщения с ожиданием завершения хода — REST-канал агентов (chats_send).
    // Занятая или ждущая человека сессия НЕ отвергает сообщение: оно встаёт в очередь
    // (Queued). По умолчанию (preempt=true) обычный ход при этом прерывается — доставка
    // сразу по его концу; preempt=false рабочего хода НЕ прерывает (сообщение ждёт
    // штатного result — Kill процесса убил бы фоновых сабагентов внутри хода), но ход
    // на карточке разрешения (Waiting) прерывается всегда. Цикл
    // «до готово» и штаб «Командной реализации» агентское НЕ рушит: в цикле сообщение ждёт
    // конца ВСЕГО цикла (гейт разбора очереди по WorkLoop), в штабе — штатного конца хода
    // координатора. Таймаут НЕ отменяет ход: вызывающий получает Running и позже читает
    // результат через историю (chats_history).
    public Task<SendAndWaitResult> SendMessageAndWaitAsync(string sessionId, string text,
        TimeSpan timeout, int agentDepth = 0, string? senderPersonaId = null,
        string? senderOrigin = null, string? senderChatName = null, bool preempt = true,
        bool systemDirective = false, string? senderUserId = null)
        => _intake.SendMessageAndWaitAsync(sessionId, text, timeout, agentDepth, senderPersonaId,
            senderOrigin, senderChatName, preempt, systemDirective, senderUserId);



    // === Очередь сообщений занятой сессии ===
    // Тела переехали в PendingQueueService (S3-2): постановка с дедупом, снимки, отмена,
    // заморозка-проверки. Здесь обёртки с прежними сигнатурами: вызывающие места
    // (приём хода, «Стоп», байпас фолбэка, DeviceWait) живут в фасаде до S3-4/S3-7.
    // Рекурсия «постановка → разбор» (dispatchNow → DrainNextPendingAsync) разорвана
    // швом Lazy<ITurnStarter>: с S3-7 его реализация — ядро приёма TurnIntake
    // (SessionCoreRegistration.cs:33), не фасад.

    private async Task<SendAndWaitResult> EnqueuePendingAsync(string sessionId, SessionEntry entry,
        string text, string? senderPersonaId, string? senderOrigin, int agentDepth,
        bool silent = false, bool suppressTasksExecute = false, string? senderChatName = null,
        PendingKind kind = PendingKind.Agent, IReadOnlyList<string>? attachedPaths = null,
        string? mode = null, string? staffNote = null, string? senderUserId = null) =>
        await _pending.EnqueuePendingAsync(sessionId, entry, text, senderPersonaId, senderOrigin, agentDepth,
            silent, suppressTasksExecute, senderChatName, kind, attachedPaths, mode, staffNote, senderUserId);

    // Отправить сообщение сразу либо, если чат занят, поставить в очередь — единая точка
    // для серверных отправок (доклад исполнителя). Раньше такие ходы полагались на неявную
    // очередь семафора в адаптере: она невидима, безразмерна и молча теряет ходы при
    // Interrupt. Возвращает true, если сообщение отложено.
    //
    // kind — вид сообщения для гейта разбора очереди при активном цикле «до готово». Дефолт
    // Agent: подавляющее большинство серверных отправок — посторонние агенты, и они по-
    // прежнему ждут конца ВСЕГО цикла. Report — доклад исполнителя (TaskExecutionService.
    // ReportToDelegatorAsync): должен будить ждущий цикл, иначе координатор и исполнитель
    // зависают во взаимном ожидании. User отсюда не шлётся.
    // Отправить либо поставить в очередь (S3-7): тело — в ядре приёма; фасад — точка
    // переадресации серверных отправок (доклады, будильники) с прежней сигнатурой.
    public Task<bool> SendOrEnqueueAsync(string sessionId, string text,
        string? senderPersonaId = null, string? senderOrigin = null,
        bool silent = false, bool suppressTasksExecute = false, string? staffNote = null,
        PendingKind kind = PendingKind.Agent, string? senderUserId = null)
        => _intake.SendOrEnqueueAsync(sessionId, text, senderPersonaId, senderOrigin, silent,
            suppressTasksExecute, staffNote, kind, senderUserId);

    // === Отчёт «наверх»: в родительский чат ===

    // Положить отчёт в родительский чат. Карточка ложится всегда (0 токенов); ход родителя
    // запускается только для withTurn — финального доклада по задаче. Промежуточный отчёт
    // («застрял на блокере») ходом не платим: родитель увидит его на своём следующем ходу.
    //
    // Глубину цепочки считаем на сервере, а не гейтим инструмент через env: значение,
    // меняющееся между ходами, пересобирает MCP-конфиг и рвёт незавершённые вызовы
    // («Stream closed» — на этих граблях уже стояли, см. ClaudeSession.ResolveTasksExecuteEnabled).
    public async Task<ReportUpResult> ReportUpAsync(string sessionId, string text, string ownerId,
        bool withTurn, string? reactionPrompt = null)
    {
        // S2d-3f.2 (R-S2d-3f §2): источник отчёта — чат, видимый вызывающему: Name и PersonaId чужого
        // приватного не уносятся в карточку родителя. Отказ тот же, что «чат не найден» (404 контроллера).
        // GetOwned не заменён ничем иным: та же проверка арендатора плюс CanSee (S2d-3a). Машинные пути
        // (чат-исполнитель, своя сессия вызывателя) не закрываются: их чаты не приватные, а автор своего
        // приватного — не арендатор, он здесь не проходил и до правки.
        if (GetOwnedVisible(sessionId, ownerId) is not { } chat) return ReportUpResult.NotFound;
        if (!_sessions.TryGetValue(sessionId, out var from)) return ReportUpResult.NotFound;
        if (SessionTaskLinks.ParentSessionId(chat, _taskLookup) is not { } parentId) return ReportUpResult.NoParent;
        if (GetOwned(parentId, ownerId) is null) return ReportUpResult.NoParent;
        if (!_sessions.TryGetValue(parentId, out var to)) return ReportUpResult.NoParent;

        var depth = from.ReportChainDepth + 1;
        if (depth > MaxReportChainDepth) return ReportUpResult.TooDeep;
        to.ReportChainDepth = depth;

        // Лицо отчёта: персона чата-отправителя, иначе — нейтральная карточка с его именем
        var persona = chat.PersonaId;
        // Время доклада ставим один раз и кладём в оба слоя (история + живая лента), чтобы
        // подпись поста не разъезжалась между перезагрузкой и пришедшим в моменте событием
        var reportTs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        await AppendStoredAsync(parentId,
            persona is not null
                ? new StoredTextMessage(text, personaId: persona, timestamp: reportTs)
                : new Protocol.StoredUserMessage(text, viaAgent: true, senderChatName: chat.Name, timestamp: reportTs),
            persona is not null
                ? new GuestTextMessage(text, persona, reportTs)
                : new UserMessageMessage(text, null, null, true, null, chat.Name, Timestamp: reportTs));

        if (!withTurn) return ReportUpResult.Delivered;

        var queued = await SendOrEnqueueAsync(parentId, reactionPrompt ?? text,
            senderPersonaId: null, silent: true, suppressTasksExecute: true);
        return queued ? ReportUpResult.Queued : ReportUpResult.Delivered;
    }

    // Доклад о блокере (Э4): в отличие от промежуточного отчёта БУДИТ постановщика — ход
    // запускается сразу. Тело переехало в TeamTurnCompletionService (волна Ж): пробуждение
    // штаба через `TryConsumeTeamWakeup` с правильным шаблоном карточки при исчерпанной
    // квоте (Stopped vs BudgetExhausted), компенсация квоты через `RefundTeamWakeup`,
    // подъём от чата исполнителя к штабу — собственное дело штабного цикла «блокер»,
    // и вертикаль владеет единым тестом, чтобы не размазывать развилку «квота выбрана
    // vs практика остановлена» между ядром и обёрткой. Обёртка сохранена ради
    // публичной сигнатуры (контроллеры и TaskExecutionService зовут по этому контракту).
    public Task<ReportUpResult> ReportBlockerAsync(string sessionId, string text, string ownerId)
        => _teamTurnCompletion.ReportBlockerAsync(sessionId, text, ownerId);

    // Снимок очереди для клиента и REST (тело — в PendingQueueService, S3-2)
    public IReadOnlyList<QueuedMessage> GetPending(string sessionId) => _pending.GetPending(sessionId);

    // «Прервать ход и доставить ждущее сейчас» (S3-7): тело — в ядре приёма (прерывание
    // принадлежит той же границе, что и очередь). Фасад — переадресация кнопки карточки.
    public bool PreemptForPending(string sessionId) => _intake.PreemptForPending(sessionId);

    // Отменить ожидающее сообщение (S3-7): тело — в ядре приёма (очередь — его владение).
    public Task<bool> CancelPendingAsync(string sessionId, string messageId) => _intake.CancelPendingAsync(sessionId, messageId);

    // Убрать поставленное (queued) сообщение по тексту — для служебных отправок (записка-передача),
    // которые не должны отработать отдельным ходом, если чат занялся между проверкой занятости и
    // отправкой. true, если что-то убрано. Тело — в PendingQueueService (S3-2).
    public bool RemovePendingByText(string sessionId, string text) => _pending.RemovePendingByText(sessionId, text);

    // Переходный шов цикла «до готово» (S3-7): продолжение и добивание не стартуют ход сами —
    // они просят фасад, чьё тело цикла остаётся здесь до шага 2 (TurnContinuation).
    // Шов ITurnStarter с фасада снят: с S3-7 его реализует ядро приёма (SessionCoreRegistration),
    // ход в сессии запускает только оно.
    Task Sessions.IWorkLoopContinuation.ContinueWorkLoopAsync(string sessionId) => ContinueWorkLoopAsync(sessionId);
    Task Sessions.IWorkLoopContinuation.NudgeTruncatedSubagentAsync(string sessionId,
        Llm.SubagentRunPassport run, int attempt) => NudgeTruncatedSubagentAsync(sessionId, run, attempt);

    // Достать следующее сообщение и отправить его обычным ходом. Вызывается по концу хода
    // (OnMessageAsync). Замороженная «Стоп» очередь не разбирается автоматически — только
    // возобновление (новое пользовательское сообщение при свободном чате) снимает заморозку.
    // Ошибка отправки не должна ронять разбор — сообщение уже снято с очереди, иначе оно
    // застряло бы навсегда и блокировало остальные.
    // Достать следующее сообщение и отправить его обычным ходом (S3-7): dequeue, пин и
    // захват слота — одна атомарная операция в ядре приёма. Обёртка держит имя и сигнатуру:
    // тесты порядка/иерархии/выключения зовут её reflection-ом по типу фасада, свои точки
    // цикла (SetWorkLoop, рестарт) идут через неё же.
    private Task DrainNextPendingAsync(string sessionId) => _intake.DrainNextPendingAsync(sessionId);


    internal static bool Busy(ILlmSessionAdapter? p) => SessionStore.Busy(p);

    // Доставка конкретного сообщения из очереди обычным ходом (без повторных гейтов очереди).
    // Пользовательское идёт со своими вложениями и режимом; агентское — как серверная отправка.
    // Возвращает true, если доставка выполнена (ход стартовал — можно ждать result); false —
    // бросилась внутри, ход не поднялся и его повторная обработка по result не придёт (Minor 3).
    // Доставка конкретного сообщения из очереди обычным ходом (S3-7): тело — в ядре приёма
    // (ожидание смерти предыдущего прогона, перепроверка автора приватного чата, старт —
    // всё на границе приёма). Обёртка держит имя и сигнатуру: тесты зовут её reflection-ом
    // по типу фасада.
    private Task<bool> DeliverPendingAsync(string sessionId, SessionEntry entry, QueuedMessage next)
        => _intake.DeliverPendingAsync(sessionId, entry, next);

    // Клиенту уходят только видимые элементы (silent — не призрак). Тело — в PendingQueueService (S3-2).
    private Task BroadcastPendingAsync(string sessionId, SessionEntry entry) =>
        _pending.BroadcastPendingAsync(sessionId, entry);

    private static IReadOnlyList<PendingMessageDto> VisiblePending(SessionEntry entry) =>
        PendingQueueService.VisiblePending(entry);

    // === Ожидание устройства локального проекта (ADR-016, вариант А плана §5) ===

    // Вердикт «ждать устройство» для чата: только проектный чат локального проекта, чьё
    // устройство не готово. Устройства нет вовсе (отозвано) — не ждём: ход откажет честно.
    private ProjectBackgroundGate? DeviceWaitGate(SessionEntry entry) => _process.DeviceWaitGate(entry);

    // Парковка и выпуск ждущих сообщения — в TurnProcessHost (S3-5); обёртки держат
    // публичные ручки и место вызова для приёма хода (он в фасаде до S3-7).
    private async Task<SendAndWaitResult> ParkForDeviceAsync(string sessionId, SessionEntry entry,
        ProjectBackgroundGate gate, DeviceWaitMessage message)
        => await _process.ParkForDeviceAsync(sessionId, entry, gate, message);

    /// <summary>Чаты, у которых есть сообщения, ждущие устройство.</summary>
    public IReadOnlyList<Session> GetDeviceWaitingSessions() => _process.GetDeviceWaitingSessions();

    /// <summary>
    /// Устройство готово: ждавшие сообщения встают в голову обычной очереди (порядок прихода
    /// сохраняется) и разбираются штатным drain — первое сразу, если чат свободен, остальные
    /// по концу хода. Возвращает число выпущенных сообщений.
    /// </summary>
    public async Task<int> ReleaseDeviceWaitAsync(string sessionId)
        => await _process.ReleaseDeviceWaitAsync(sessionId);

    /// <summary>
    /// Снять сообщения, ждущие устройство дольше потолка (поставлены раньше <paramref name="olderThanUtc"/>).
    /// Возвращает снятые — уведомление владельцу шлёт вызывающий.
    /// </summary>
    public async Task<IReadOnlyList<DeviceWaitMessage>> ExpireDeviceWaitAsync(string sessionId, DateTime olderThanUtc)
        => await _process.ExpireDeviceWaitAsync(sessionId, olderThanUtc);

    // Есть ли в очереди сообщение, продолжающее цикл (User/Report). Тело — в PendingQueueService (S3-2).
    private static bool HasContinuingPending(SessionEntry entry) => PendingQueueService.HasContinuingPending(entry);

    private static bool HasPending(SessionEntry entry) => PendingQueueService.HasPending(entry);

    // Снимок для replay при JoinSession (без служебных)
    public IReadOnlyList<PendingMessageDto> GetVisiblePending(string sessionId) => _pending.GetVisiblePending(sessionId);

    // Ручное сворачивание контекста: /compact в CLI, минуя счётчики и историю user-сообщений.
    // Тело — в TurnProcessHost (S3-5). Гейт «провайдер умеет compact» остаётся здесь:
    // LlmProviderRegistry в хост процесса не входит (ни одному другому телу этой роли он не
    // нужен), а защита от рассинхрона UI держится на публичной сигнатуре, как держалась.
    public async Task CompactAsync(string sessionId)
    {
        if (!_sessions.TryGetValue(sessionId, out var entry))
            throw new InvalidOperationException("Сессия не найдена");
        if (entry.Info.ClaudeSessionId is null) return; // ходов ещё не было — сворачивать нечего
        if (!_llmProviders.CapabilitiesFor(entry.Info.Model).SupportsCompact)
            return; // провайдер не умеет compact — защита от рассинхрона UI

        await _process.CompactAsync(sessionId, entry);
    }


    // Создание/переиспользование аккумулятора истории — в SessionHistory (S3-5): ключ
    // истории и общий лок внеходовых записей — её дело. Тел здесь с S3-7 не осталось
    // (приём хода — в ядре, оно зовёт хост процесса), но обёртка держится как место,
    // на которое смотрит белый тест VoiceLocalTurnTests (рефлексия по типу фасада) —
    // тот же приём, что у MakeChatTitle и ShouldRunLocalVoice выше.
    private async Task EnsureAccumulatorAsync(SessionEntry entry) => await _sessionHistory.EnsureAccumulatorAsync(entry);

    // Именование чата (S3-3): авто-заголовок и его уточнение — тело в ChatNaming. Обёртки
    // сохранены и как точка вызова (создание/приём хода ещё живут здесь), и как место,
    // на которое смотрит белый тест MakeChatTitle (SessionManagerTestBase, рефлексия).
    private static string MakeChatTitle(string text) => ChatNaming.MakeChatTitle(text);


    // Именование чата (S3-3): явное переименование и значки тем — тела в ChatNaming;
    // здесь тонкие обёртки с прежними сигнатурами (их зовут контроллеры и тесты).
    public Task<Session?> RetitleAsync(string userId, string sessionId, CancellationToken ct)
        => _naming.RetitleAsync(userId, sessionId, ct);

    public Task<Session?> SetChatIconAsync(string userId, string sessionId, CancellationToken ct)
        => _naming.SetChatIconAsync(userId, sessionId, ct);

    public Task<IconBatchResult> SetChatIconsAsync(string userId, CancellationToken ct, string? projectId = null)
        => _naming.SetChatIconsAsync(userId, ct, projectId);

    private static bool HasTurnInFlight(SessionEntry entry) => SessionStore.HasTurnInFlight(entry);

    public bool HasTurnInFlight(string sessionId) => _store.HasTurnInFlight(sessionId);

    // Редактирование названия и модели. Модель применяется со следующего хода
    // (процесс claude пересоздаётся в RunTurnAsync), Info — общая ссылка с адаптером.
    //
    // PATCH-семантика: null = «поле не передано, не трогать». Иначе частичные апдейты
    // (MCP chats_update только с name; PUT {pinned} из togglePin) затирали бы модель/имя.
    //
    // ownerId — владелец, от чьего имени идёт правка (UserId контроллера). Единственная
    // мутирующая точка, где его раньше не было: смена провайдера уходит в
    // MigrateProviderAsync, а тот проверяет владение — брать владельца из самой сессии
    // означало бы сверять её саму с собой.
    public Task<Session?> UpdateAsync(string sessionId, string ownerId, string? name, string? model,
        string? effort, List<string>? tags = null) =>
        _settings.UpdateAsync(sessionId, ownerId, name, model, effort, tags);

    /// <summary>Явно перевести чат с окна 1M на базовое (200K): срезать суффикс [1m] у модели.</summary>
    /// Единственный путь, которым суффикс окна снимается по воле человека — кнопка «продолжить в
    /// стандартном окне» под карточкой отказа Window1MUnavailable. Автоматического среза больше
    /// нет нигде в ходе чата (он был тихой миной для длинных разговоров), поэтому решение
    /// «мне хватит 200K» принимает пользователь, а сервер только исполняет.
    ///
    /// Модель берём ЭФФЕКТИВНУЮ (Info.Model может быть пуста — тогда модель приходит от слота
    /// назначения места), а закрепляем в чате базовый алиас явно: иначе назначение места на
    /// следующем ходу вернуло бы окно 1M и человек снова упёрся бы в ту же карточку.
    /// Не тир-алиас с окном — снимать нечего, отказ (InvalidOperationException → 400).
    public Task<Session?> DropWindow1MAsync(string sessionId, string ownerId) =>
        _settings.DropWindow1MAsync(sessionId, ownerId);

    // --- Процесс CLI: «Стоп», карточки взаимодействия, зависший чат (S3-5) ---
    // Тела ушли в Services/Sessions/TurnProcessHost.cs; сигнатуры фасада прежние: на них сидят
    // хаб (Stop), контроллеры (permission/answer/plan, auto-allow), отзыв участника и
    // TaskExecutionService (гашение процесса закрытого исполнителя). Приватные хелперы
    // (IsStaleInteractionAnswer, ResumeAfterInteraction, RecordUserInterrupt, ReviveStuckSession,
    // FreezePending) переехали вместе с телами — здесь их больше нет.
    public void Interrupt(string sessionId) => _process.Interrupt(sessionId);

    internal void InterruptCore(string sessionId, bool byUser) => _process.InterruptCore(sessionId, byUser);

    public void StopChatsOfRevokedMember(string projectId, string memberUserId)
        => _process.StopChatsOfRevokedMember(projectId, memberUserId);

    internal bool StopClosedExecutorProcess(string sessionId, string taskId)
        => _process.StopClosedExecutorProcess(sessionId, taskId);

    public void RespondPermission(string sessionId, string requestId, string behavior)
        => _process.RespondPermission(sessionId, requestId, behavior);

    public Session? RemoveAutoAllowTool(string sessionId, string tool) => _process.RemoveAutoAllowTool(sessionId, tool);

    public void AnswerQuestion(string sessionId, string toolUseId, string answerText)
        => _process.AnswerQuestion(sessionId, toolUseId, answerText);

    public void RespondPlan(string sessionId, string requestId, bool approve, string? feedback)
        => _process.RespondPlan(sessionId, requestId, approve, feedback);

    // === КР-наблюдаемость, этап 3: перезапуск хода штаба без потери работы ===

    // Ожидание смерти процесса после kill: само ожидание — в TurnRecovery (S3-5), здесь
    // остаётся точка, на которой тесты сужают его до сотен миллисекунд.
    internal TimeSpan KillWaitTimeout { get => _recovery.KillWaitTimeout; set => _recovery.KillWaitTimeout = value; }

    // Перезапуск зависшего хода (этап 3): тело — в TurnRecovery (проверка живости → kill →
    // ожидание смерти → валидация транскрипта → revive → разбор очереди). Гейт повторного
    // вызова (_turnRestarts) и порог свежести хода переехали туда же.
    public async Task<StuckTurnRestartResult> RestartStuckTurnAsync(string sessionId, bool startFresh)
        => await _recovery.RestartStuckTurnAsync(sessionId, startFresh);

    // === Авто-возобновление ходов, оборванных рестартом сервера (фича turn-auto-resume) ===

    // Шов для тестов: целостность хвоста транскрипта (true — можно возобновлять).
    // По умолчанию — та же проверка, что RestartStuckTurnAsync: файл не найден (локальный
    // проект) — проверку пропускаем, иначе проверяем IsTailIntact.
    protected virtual bool IsTailIntactForResume(string sessionId)
    {
        if (!_sessions.TryGetValue(sessionId, out var entry)) return true;
        var transcript = FindResumeTranscript(entry);
        return transcript is null || Llm.Claude.TranscriptProbe.IsTailIntact(transcript);
    }

    // Шов для тестов: фактическая отправка resume-хода. По умолчанию — обычный путь
    // SendMessageAsync: auto — для прохода через DeviceWaitGate, cause — атрибуция доставки.
    protected virtual Task<SendUserOutcome> SendRestartResumeTurnAsync(string sessionId, string text)
        => SendMessageAsync(sessionId, text, [], auto: true, cause: DeliveryCause.ServerRestartResume);

    // Возобновление хода, оборванного рестартом сервера (B3). Кандидат — сессия, чей ход
    // шёл (Working) в момент падения: LoadSessions перевёл её в Orphaned. Шаги строго по
    // порядку, отказ любого — не шлём, в ленте остаётся карточка-баннер:
    //   1. статус Orphaned (кандидат, а не idle/упавший);
    //   2. хвост транскрипта цел (битый — через --resume не продолжится);
    //   3. гидратация аккумулятора (ДО карточки — карточка пишется в историю);
    //   4. карточка «Сервер был перезапущен — продолжаю ход с того же места»;
    //   5. RestartResumeAttempts += 1 + SaveSessions (UpdatedAt не двигаем);
    //   6. resume-ход обычным путём (auto, cause=ServerRestartResume).
    // Возобновление хода, оборванного рестартом сервера — тело в TurnRecovery (S3-5), шаги
    // 1-6 сохранены там же в том же порядке. Тестовые швы остаются здесь (на них стоит
    // SpySessionManager) и приходят в роль делегатами вызова.
    public async Task<RestartResumeResult> ResumeAfterRestartAsync(string sessionId)
        => await _recovery.ResumeAfterRestartAsync(sessionId, IsTailIntactForResume, SendRestartResumeTurnAsync);

    // Транскрипт resume-якоря чата (тело — в CliRoute, S3-2): null — якоря нет или файл не найден.
    // Приватное имя сохранено: на него смотрит тест через рефлексию (SessionManagerLocalTranscriptTests).
    private string? FindResumeTranscript(SessionEntry entry) => _route.FindResumeTranscript(entry);

    // Дефолт лимитов цикла «до готово». Невалидное значение конфига (не число или ≤ 0)
    // сваливается в дефолт, а не молча отрубает цикл: MaxIterations=0 иначе даёт
    // немедленную остановку по лимиту. internal — тестируется напрямую (SessionManagerTests).
    internal static int LoopLimitOrDefault(string? raw, int defaultValue = 20) =>
        int.TryParse(raw, out var v) && v > 0 ? v : defaultValue;

    // Включение/выключение цикла «до готово» (флаг work-loop). Включение сбрасывает
    // счётчик итераций; лимит — из конфига Loop:MaxIterations (дефолт 20, при ≤0 — тоже
    // дефолт, см. LoopLimitOrDefault).
    // userId задан (вызов из API) — сверяется с владельцем; null — внутренний вызов.
    // Режим прав, выбранный в Composer. Раньше он доезжал до сессии только вместе с
    // сообщением (см. SendMessageAsync), и выбор, сделанный до первого хода, терялся при
    // уходе со страницы: UI перечитывал Session.Mode и показывал прежний режим.
    // На сам ход это не влияет — процесс claude всё равно пересоздаётся в RunTurnAsync
    // и читает --permission-mode из Info.Mode.
    // Смена режима (S3-7): тело — в ядре приёма (режим — часть состава хода, та же точка,
    // что и сообщение с mode). Фасад — точка переадресации API и внутренних вызовов.
    public Session? SetMode(string sessionId, string mode, string? userId = null) => _intake.SetMode(sessionId, mode, userId);

    // manual=true — отключение по кнопке «Остановить цикл» в UI (в отличие от внутренних
    // отключений из ContinueWorkLoopAsync по лимиту/ошибке — те шлют СВОЁ сообщение сами,
    // до вызова этого метода, и manual=false, чтобы не задваивать ленту).
    public async Task<Session?> SetWorkLoopAsync(string sessionId, bool enabled, string? userId = null,
        bool manual = false)
    {
        if (!_sessions.TryGetValue(sessionId, out var entry)) return null;
        if (userId is not null && ResolveOwnerId(entry.Info) != userId) return null;

        // Гард B4: автопилот и «Командная реализация» одновременно ломают ход — см.
        // SessionModeConflictException.
        if (enabled && entry.Info.TeamImplement is not null)
            throw new SessionModeConflictException(
                "Автопилот недоступен в чате «Командной реализации» — здесь работа идёт через задачи исполнителям.");

        var wasEnabled = entry.Info.WorkLoop is not null;
        // Присвоение WorkLoop и очистку буфера хода держим под одним локом: иначе обнуление
        // поля состязается с чтением в ContinueWorkLoopAsync (там же инкремент Iteration
        // под локом), и потребитель мог бы увидеть уже выключенный объект и уйти в мусор.
        var newLoop = enabled
            ? new SessionWorkLoop
            {
                MaxIterations = LoopLimitOrDefault(_config["Loop:MaxIterations"]),
            }
            : null;
        lock (entry.LoopTurnLock)
        {
            entry.Info.WorkLoop = newLoop;
            entry.LoopTurnText.Clear();
        }
        // При выключении гасим маркер итерации: висящий LoopTurnInFlight заблокировал бы
        // разбор очереди по концу следующего хода (гейт !LoopTurnInFlight).
        // (S3-7d) Под PendingLock — тем же замком, что гейт разбора очереди: раньше снятие было
        // под LoopTurnLock, т.е. под другим замком относительно читателя маркера, и состояние
        // маркера не было атомарно относительно drain'а. Присвоение WorkLoop и очистка буфера
        // остаются под LoopTurnLock (см. комментарий выше). Промежуточное состояние «цикл снят,
        // маркер ещё взведён» безвредно: маркер читают только там, где активен цикл.
        if (newLoop is null)
            lock (entry.PendingLock) entry.LoopTurnInFlight = false;
        SaveSessions();
        await BroadcastWorkLoopAsync(sessionId, entry);

        // Явное сообщение в ленту (B5): иначе гаснет только бейдж, и непонятно, доделана
        // работа или брошена — вторая фраза важна, текущий ход после снятия цикла продолжается.
        if (manual && wasEnabled && !enabled)
            await AddWorkLoopStoppedNoticeAsync(sessionId, entry, "manual",
                "Цикл остановлен вами. Текущий ход продолжает работу.");

        // Агентские сообщения, скопившиеся за время цикла (они ждали конца ВСЕГО цикла),
        // при выключении не должны дожидаться следующего пользовательского хода — запускаем
        // разбор очереди. В фоне: SetWorkLoopAsync вызывается в т.ч. из ContinueWorkLoopAsync
        // по result. НО только когда текущий ход цикла уже завершился (статус не Working/Waiting):
        // ручной стоп приходит ВО ВРЕМЯ хода, и drain миновал бы гейт занятости SendMessageAsync(auto)
        // → второй ход в живой процесс. Ничего не теряется — по result текущего хода отработает
        // штатный drain (там WorkLoop уже null).
        if (wasEnabled && !enabled && entry.Info.Status is not (SessionStatus.Working or SessionStatus.Waiting))
            _ = Task.Run(async () =>
            {
                try { await DrainNextPendingAsync(sessionId); }
                catch (Exception ex) { Console.Error.WriteLine($"[SessionManager] Разбор очереди после стопа цикла ({sessionId}): {ex.Message}"); }
            });

        return entry.Info;
    }

    // Персистит и рассылает явную остановку цикла «до готово» (B5) — лимит итераций, ошибка
    // хода или ручной стоп. Reason ∈ limit|error|manual — контракт для фронта (Кира, п.5).
    private async Task AddWorkLoopStoppedNoticeAsync(string sessionId, SessionEntry entry, string reason, string text)
        => await _sessionHistory.AddWorkLoopStoppedNoticeAsync(sessionId, reason, text);

    private Task BroadcastWorkLoopAsync(string sessionId, SessionEntry entry)
    {
        var loop = entry.Info.WorkLoop;
        return BroadcastAsync(sessionId, new WorkLoopMessage(
            loop is not null, loop?.Iteration ?? 0, loop?.MaxIterations ?? 0, loop?.Phase,
            loop?.WaitingReason, loop?.WaitingTicks ?? 0));
    }

    // Режим «Командная реализация»: вкл/выкл режима чата-штаба. Тело переехало в
    // TeamEnableService (волна Ж): гарды B2/B4, снимок «оборванной волны», возврат режима
    // человека при выключении, правка настроек поверх активного режима (M4), тройная
    // синхронизация Mode/CLI/AdapterStale через шов — собственное дело вертикали,
    // единое тело держит все развилки «выкл посреди интервью/волны/проверки». Обёртка
    // сохранена ради публичной сигнатуры: контроллеры и тесты зовут по этому контракту.
    public Task<Session?> SetTeamImplementAsync(string sessionId, bool enabled,
        bool autoWaves = true, string? coordinatorPersonaId = null, string? plannerPersonaId = null,
        IReadOnlyCollection<string>? executorPersonaIds = null, string? userId = null,
        bool coordinatorNoCode = true)
        => _teamEnable.SetTeamImplementAsync(sessionId, enabled, autoWaves, coordinatorPersonaId,
            plannerPersonaId, executorPersonaIds, userId, coordinatorNoCode);

    // Причина, по которой режим включать нельзя — до единого хода интервью (B2 приёмки).
    // Тело переехало в TeamStateService (волна А): тонкая обёртка сохраняет публичную
    // сигнатуру для тестов и внешних вызывающих. Публичный (волна Ж): TeamEnableService
    // зовёт при включении режима — гард B2 приёмки (нет координатора / пустой состав)
    // должен срабатывать ДО того, как сессия становится режимной.
    public (string Code, string Message)? TeamImplementSetupError(Session session,
        string? coordinatorPersonaId, IReadOnlyCollection<string>? executorPersonaIds) =>
        _teamState.TeamImplementSetupError(session, coordinatorPersonaId, executorPersonaIds);

    // Переключение авто-волн на ходу (из бейджа режима): не включает/выключает режим,
    // только флаг внутри. Режим не активен → поля не трогает, возвращает сессию как есть.
    // Тело в TeamEnableService (волна Ж); обёртка сохранена ради публичной сигнатуры.
    public Task<Session?> SetTeamImplementAutoAsync(string sessionId, bool autoWaves, string? userId = null)
        => _teamEnable.SetTeamImplementAutoAsync(sessionId, autoWaves, userId);

    // Режим прав, совместимый с гардом «координатор не пишет код», переехал в Core
    // (`ClaudeHomeServer.Services.PermissionModeGuard`) — там же, где тип `ClaudeMode`. Ядро
    // SessionManager и штаб TeamWaveService зовут его из Core по новому пути.

    // Вход в план-режим стадий интервью и планирования (Э8). Тело переехало в
    // TeamStateService (волна Б): управление режимом хода — собственное дело вертикали,
    // а ядро держит только рантайм-поля и доступ к Process. Обёртка сохранена ради
    // сигнатуры (вызовы идут из нескольких точек этого класса).
    // Публичный (волна Г): TeamDecisionService.StartTeamWorkAsync зовёт при входе
    // в план-фазу из Idle, не дёргая приватный шов.
    internal void EnterPlanPhaseMode(string sessionId)
        => _teamState.EnterPlanPhaseMode(sessionId);

    // Возврат режима человека после согласования плана (Confirming → Wave), при
    // выключении режима и в добавочном плане авто-волн (B1). Тело переехало в
    // TeamStateService (волна Б); обёртка сохранена по тем же причинам, что
    // EnterPlanPhaseMode. Публичный (волна В): TeamPlanService зовёт из публикации
    // добавочного плана, не дёргая приватный шов.
    internal void RestoreUserMode(string sessionId)
        => _teamState.RestoreUserMode(sessionId);

    // Бюджет итерации из дефолтов плана с optional override из конфига TeamImplement:Max*
    // Публичный (волна Г): TeamDecisionService.StartTeamWorkAsync зовёт при открытии
    // свежей итерации в Idle.
    internal TeamImplementBudget NewTeamImplementBudget() => _teamState.NewTeamImplementBudget();

    // Рассылка TeamImplementMessage по группе чата. Тело в TeamStateService. Публичный
    // (волна В): TeamPlanService публикует состояние режима после правки PlanCardId/
    // PlanVersion/Replanning — через тот же канал, что и TeamWaveService.
    internal Task BroadcastTeamImplementAsync(string sessionId, Session session) =>
        _teamState.BroadcastTeamImplementAsync(sessionId, session);

    private Task BroadcastTeamImplementAsync(string sessionId, SessionEntry entry) =>
        _teamState.BroadcastTeamImplementAsync(sessionId, entry.Info);

    // --- Э2: планирование по компетенциям и карточка плана ---
    //
    // Тела RunTeamPlanningAsync/CreateTeamPlanAsync/PublishTeamPlanAsync/
    // SupersedeCurrentPlanCardAsync/ResolveStalePlanCardAsync переехали
    // в TeamPlanService (волна В). Обёртки сохранены, чтобы не переписывать тесты:
    // 18 мест зовут _sut.CreateTeamPlanAsync через тот же контракт.

    // Построить план по вводной и опубликовать карточкой в ленту штаба.
    // Тело — в TeamPlanService. Обёртка сохранена ради публичной сигнатуры.
    public Task<(TeamImplementPlan? Plan, string? Reason)> CreateTeamPlanAsync(
        string sessionId, string request, string? userId = null, CancellationToken ct = default,
        bool fromHuman = true, string? feedback = null) =>
        _teamPlan.CreateTeamPlanAsync(sessionId, request, userId, ct, fromHuman, feedback);

    // Ответ человека по карточке плана (SessionHub.RespondTeamPlan).
    // Тело переехало в TeamDecisionService (волна Г): решение по карточке — собственное
    // дело вертикали (включая развилку Accumulator/диск, которую спрятал
    // публичный ApplyPlanDecisionAsync в ядре). Обёртка сохранена ради публичной
    // сигнатуры — SessionHub зовёт её по этому контракту.
    public Task<TeamImplementPlan?> RespondTeamPlanAsync(string sessionId, string planId,
        TeamPlanDecision decision, string? subtaskId = null, string? executorPersonaId = null,
        string? userId = null, string? feedback = null) =>
        _teamDecision.RespondTeamPlanAsync(sessionId, planId, decision, subtaskId,
            executorPersonaId, userId, feedback);

    // Гасим устаревшую карточку и объясняем человеку, почему решение по ней не сработало.
    // Тело — в TeamPlanService (волна В). Обёртка сохранена ради сигнатуры и единого
    // канала: RespondTeamPlanAsync вызывает её в той же ветке, что и раньше.
    // Публичный (волна Г): TeamDecisionService.RespondTeamPlanAsync вызывает её
    // по тому же контракту.
    internal Task ResolveStalePlanCardAsync(string sessionId, Session session,
        SessionTeamImplement team, string planId, TeamImplementPlan plan) =>
        _teamPlan.ResolveStalePlanCardAsync(sessionId, session, team, planId, plan);

    // Погасить ТЕКУЩУЮ карточку плана как заменённую версией nextVersion. Тело — в
    // TeamPlanService (волна В). Обёртка сохранена ради сигнатуры: из PublishTeamPlanAsync
    // и EnterInterviewAsync вызывается по тому же контракту.
    // Публичный (волна Г): TeamDecisionService.StartTeamWorkAsync/RespondTeamPlanAsync
    // (ветка Edit) вызывают её по тому же контракту.
    internal Task SupersedeCurrentPlanCardAsync(string sessionId, Session session, int nextVersion) =>
        _teamPlan.SupersedeCurrentPlanCardAsync(sessionId, session, nextVersion);

    // Обработчики волны переехали в вертикаль (шаг 2г-4, волна 1): их держит
    // TeamCoordinator, а ядро отдаёт его одной ссылкой вместо четырёх публичных
    // Func-свойств. Ставит обработчики TeamWaveService, читают четыре сервиса вертикали и
    // четыре ветки ядра ниже. Экземпляр координатора один — тот, что создан в конструкторе,
    // поэтому в тестах связь работает ровно как раньше (сборка объектов не менялась).
    // Прежние комментарии обещали здесь «разрыв цикла TaskExecutionService → SessionManager»;
    // фактически цикла не было — вертикаль клала обработчик в ядро и сама же его оттуда
    // забирала. Разбор — docs/research/team-di-migration-2026-09.md §3.
    // null-семантика прежняя: обработчик не назначен (тесты ядра без штаба) — ветка молчит.
    internal TeamCoordinator TeamHandlers => _teamCoordinator;

    // --- Э4: автономный цикл, бюджет и эскалации ---

    // Сессии с включённым режимом — сторожу зависших волн (TeamWaveWatchdog) и сводкам.
    public IReadOnlyList<Session> GetTeamImplementSessions() =>
        [.. _sessions.Values.Select(e => e.Info).Where(s => s.TeamImplement is not null)];

    // Нерешённая карточка плана по id — путь для чата без аккумулятора (сервер перезапустился,
    // ход ещё не начинался). Фильтр по Resolved — тот же, что у Accumulator.FindTeamPlan у
    // активного чата: повторный клик по уже разрешённой карточке не должен пройти
    // (идемпотентность решения по карточке). Выделено из прежнего LoadPendingStoredPlanAsync
    // волной Г, чтобы вертикаль TeamDecisionService получила одну точку вместо размазанной
    // логики «Accumulator.FindTeamPlan + LoadPendingStoredPlanAsync».
    public async Task<TeamImplementPlan?> FindActivePlanAsync(string sessionId, string planId)
    {
        if (!_sessions.TryGetValue(sessionId, out var entry)) return null;
        if (entry.Accumulator is { } acc) return acc.FindTeamPlan(planId);
        if (entry.Info.ClaudeSessionId is not string key) return null;
        return await ReadStoredTeamPlanAsync(key, planId, onlyUnresolved: true);
    }

    // Применить решение человека по карточке плана (Run/Reassign/Cancel/Edit-отмена).
    // Развилка «активный аккумулятор против диска» была размазана по двум веткам
    // RespondTeamPlanAsync в SessionManager; волна Г собирает её в один публичный метод
    // ядра. Активный чат → Accumulator.OnTeamPlanUpdated + FireAndForget SaveSnapshotAsync;
    // неактивный → LoadAsync + правка под _falPersistLock + SaveAsync. true — карточка
    // обновлена; false — карточки нет либо чата/транскрипта нет (причина уходит в лог).
    public async Task<bool> ApplyPlanDecisionAsync(string sessionId, string planId,
        TeamImplementPlan plan, bool resolved)
    {
        if (!_sessions.TryGetValue(sessionId, out var entry)) return false;
        if (entry.Accumulator is { } acc)
        {
            acc.OnTeamPlanUpdated(planId, plan, resolved ? plan.Approved : null);
            FireAndForget(acc.SaveSnapshotAsync(_history),
                $"сохранение истории после решения по плану команды ({sessionId})");
            return true;
        }
        if (entry.Info.ClaudeSessionId is not string key) return false;
        try
        {
            return await WithFalPersistLockAsync(async () =>
            {
                var stored = await _history.LoadAsync(key);
                var card = stored.OfType<StoredTeamPlanMessage>()
                    .LastOrDefault(m => m.PlanId == planId && !m.Resolved);
                if (card is null) return false;
                card.Plan = plan;
                card.Resolved = resolved;
                if (resolved) card.Approved = plan.Approved;
                await _history.SaveAsync(key, stored);
                return true;
            });
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Правка карточки в истории на диске ({SessionId}) не удалась", sessionId);
            return false;
        }
    }

    // Правка карточки истории у НЕактивного чата (read-modify-write под общим локом).
    // Тело — в SessionHistory (S3-2).
    private Task<bool> MutateStoredAsync<T>(SessionEntry entry, string sessionId,
        Func<T, bool> match, Action<T> mutate) where T : StoredMessage =>
        _sessionHistory.MutateStoredAsync(entry, sessionId, match, mutate);

    // Транзакция над состоянием режима: ЕДИНСТВЕННЫЙ способ править счётчики бюджета и
    // попытки под-задач. Точки записи разнесены по потокам (раздача волны из колбэка
    // завершения задачи, перевыдача из колбэка провала хода, квота из HTTP-фильтра), а
    // `int++` не атомарен — частичный лок означал бы потерянные инкременты и нечестный счёт
    // ровно там, ради чего Э4 и делался. Внутри — только синхронная работа с моделью.
    internal T? WithTeamState<T>(string sessionId, Func<SessionTeamImplement, T> mutate) =>
        _teamState.WithTeamState(sessionId, mutate);

    // Публичный (волна Д): TeamDecisionService зовёт его вместо прямой работы с
    // entry.Accumulator. Активный чат — живые объекты аккумулятора; неактивный (после
    // рестарта) — копии с диска. У активного чата возвращённые объекты — те же, что в
    // истории: их правки фиксируются снимком.
    public async Task<IReadOnlyList<TeamEscalation>> ListOpenEscalationsAsync(string sessionId)
    {
        if (!_sessions.TryGetValue(sessionId, out var entry)) return [];
        if (entry.Accumulator is { } acc)
            return acc.GetAll().OfType<StoredTeamEscalationMessage>()
                .Where(m => !m.Escalation.Resolved)
                .Select(m => m.Escalation).ToList();
        if (entry.Info.ClaudeSessionId is not string key) return [];
        try
        {
            var stored = await _history.LoadAsync(key);
            return stored.OfType<StoredTeamEscalationMessage>()
                .Where(m => !m.Escalation.Resolved)
                .Select(m => m.Escalation).ToList();
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Чтение карточек остановки с диска ({SessionId}) не удалось", sessionId);
            return [];
        }
    }

    // Синхронный собрат ListOpenEscalationsAsync (S6, фикс-волна): шов для TeamBudgetService/
    // TeamDecisionService. Тело — в SessionHistory (S3-2); здесь остаётся реализация шва.
    IReadOnlyList<TeamEscalation> ITeamHistoryStore.GetOpenTeamEscalationsSync(string sessionId) =>
        _sessionHistory.GetOpenTeamEscalationsSync(sessionId);

    // Этапы и итоговые счётчики долгого инструмента (run_tests) — в историю его вызова: живой
    // tool_progress идёт мимо пампа CLI, и без этой записи строка этапов и итог «174 из 177»
    // пропадали бы после F5. persist — записать снимок на диск сразу (конец прогона: tool_result
    // может уже не прийти, если ход оборван); промежуточные снимки ляжут с ближайшим сохранением.
    // Аккумулятора уже нет (ход оборван раньше конца прогона) — финальный снимок дописывается
    // в сохранённую историю по toolUseId, иначе «прервано · сборка» не пережило бы F5;
    // промежуточные без аккумулятора не пишем — их перекроет финальный
    public void RecordToolStages(string sessionId, string toolUseId, IReadOnlyList<ToolStage> stages,
        ToolRunTotals? totals, bool persist) =>
        FireAndForget(RecordToolStagesAsync(sessionId, toolUseId, stages, totals, persist),
            $"этапы инструмента ({sessionId})");

    // Снимок в аккумулятор ложится синхронно, до первого await: порядок событий сохраняется
    internal async Task RecordToolStagesAsync(string sessionId, string toolUseId, IReadOnlyList<ToolStage> stages,
        ToolRunTotals? totals, bool persist)
    {
        if (!_sessions.TryGetValue(sessionId, out var entry)) return;
        if (entry.Accumulator is { } acc)
        {
            if (acc.OnToolStages(toolUseId, stages, totals) && persist)
                await acc.SaveSnapshotAsync(_history);
            return;
        }
        if (persist)
            await MutateStoredAsync<StoredToolUseMessage>(entry, sessionId, m => m.Id == toolUseId,
                m => TurnAccumulator.ApplyToolStages(m, stages, totals));
    }

    // Фактический старт MCP-инструмента с прогрессом (run_tests, build, start_stand) — из его
    // tools/call, мимо пампа CLI. CLI выполняет такие вызовы по очереди, и без этой метки все
    // выписанные в одном ответе карточки сразу показывали бы «идёт» с отсчётом от tool_use.
    // Тот же tool_started, что у Bash/агентов: метка ложится в историю вызова, событие уходит
    // в ленту сразу либо следом за tool_use, если вызов обогнал его рассылку (OnExternalToolStarted)
    public void RecordToolStarted(string sessionId, string toolUseId)
    {
        if (!_sessions.TryGetValue(sessionId, out var entry) || entry.Accumulator is not { } acc) return;
        var startedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        if (acc.OnExternalToolStarted(toolUseId, startedAt))
            FireAndForget(BroadcastAsync(sessionId, new ToolStartedMessage(toolUseId, startedAt)),
                $"старт инструмента ({sessionId})");
    }

    // Публичный (волна Д): TeamDecisionService зовёт его вместо прямой работы с
    // entry.Accumulator. Счётчик и момент последнего оклика пишутся на карточку в истории —
    // переживают рестарт сервера, чтобы после перезапуска не начать оклик заново.
    // false — карточка уже закрыта либо её нет.
    public async Task<bool> MarkEscalationRemindedAsync(string sessionId, string escalationId)
    {
        if (!_sessions.TryGetValue(sessionId, out var entry)) return false;
        if (entry.Accumulator is { } acc)
        {
            if (!acc.OnTeamEscalationReminded(escalationId)) return false;
            try { await acc.SaveSnapshotAsync(_history); }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Сохранение истории после напоминания ({SessionId}) не удалось", sessionId);
            }
            return true;
        }
        // Чат неактивен (после рестарта аккумулятор ещё не оживлён) — правим историю на диске
        return await _teamHistory.MutateCardAsync<StoredTeamEscalationMessage>(sessionId,
            m => m.EscalationId == escalationId && !m.Escalation.Resolved,
            m =>
            {
                m.Escalation.RemindersSent++;
                m.Escalation.LastReminderAt = DateTime.UtcNow;
            });
    }

    // Публичный (волна Д): TeamDecisionService.RespondTeamEscalationAsync зовёт его
    // вместо прямой работы с entry.Accumulator. Помечает карточку resolved на активном
    // чате (и пишет снимок), либо правит на диске у неактивного. Возвращает объект
    // карточки — координатору нужны её поля (Kind/Actions/TaskId/Wave/PersonaId).
    // null — карточки нет / не резолвилась.
    // resolutionNote (M3, фикс-волна): подпись снятия штабом пишется в карточку ОДНИМ
    // вызовом вместе с Resolved/ChosenActionId — раньше правка шла отдельным
    // MutateCardAsync, а у активного чата MutateCardAsync ходил через диск и затирался
    // ближайшим снимком Accumulator-а; теперь текст снятия живёт до перезагрузки.
    public async Task<TeamEscalation?> ResolveEscalationAsync(string sessionId, string escalationId,
        string? actionId, string? resolutionNote = null)
    {
        if (!_sessions.TryGetValue(sessionId, out var entry)) return null;
        if (entry.Accumulator is { } acc)
        {
            var escalation = acc.FindTeamEscalation(escalationId);
            var resolved = acc.OnTeamEscalationResolved(escalationId, actionId);
            if (!resolved) return null;
            if (escalation is null) return null;
            // Подпись снятия вписывается в тот же объект до снимка: снимок зальёт её на диск
            // одним коммитом, отдельного MutateCardAsync с гонкой уже нет
            if (resolutionNote is not null) escalation.ResolutionNote = resolutionNote;
            FireAndForget(acc.SaveSnapshotAsync(_history),
                $"сохранение истории после решения по карточке остановки ({sessionId})");
            return escalation;
        }
        // Чат неактивен — карточка лежит только на диске
        StoredTeamEscalationMessage? card = null;
        var ok = await _teamHistory.MutateCardAsync<StoredTeamEscalationMessage>(sessionId,
            m => m.EscalationId == escalationId && !m.Escalation.Resolved,
            m =>
            {
                m.Escalation.Resolved = true;
                m.Escalation.ChosenActionId = actionId;
                if (resolutionNote is not null) m.Escalation.ResolutionNote = resolutionNote;
                card = m;
            });
        if (!ok) return null;
        return card?.Escalation;
    }

    // Вердикт квоты запуска исполнителя со штабного хода-реакции (Э4). Enum в Models
    // (TeamRunQuota) живёт под тем же именем, что и nested SessionManager.TeamRunQuota:
    // тесты и фильтр DenyOnDelegatedTurn пользуются nested (контракт публичной сигнатуры
    // ядра), TeamBudgetService — Models (вертикаль не должна ссылаться на ядро через
    // nested-тип). Каст через (int) безопасен: оба enum'а имеют одинаковые значения.
    public enum TeamRunQuota { NotTeamMode, Allowed, Exhausted }

    // Гейт лавины запусков: на реакционном ходу координатора (ответ на доклад исполнителя)
    // запуск задачи разрешён, ПОКА цел бюджет итерации — запрет заменён квотой, а не снят.
    // Тело переехало в TeamBudgetService (волна Е). Обёртка сохранена ради публичной
    // сигнатуры: DenyOnDelegatedTurn.Decide и фильтр OnActionExecuted зовут по этому контракту.
    public (TeamRunQuota Verdict, string? Reason) TryConsumeTeamImplementRun(string sessionId, string ownerId)
    {
        var (v, r) = _teamBudget.TryConsumeTeamImplementRun(sessionId, ownerId);
        return ((TeamRunQuota)(int)v, r);
    }

    // Компенсация квоты запуска (m3, второй проход Глеба). Тело в TeamBudgetService.
    // Обёртка сохранена ради сигнатуры DenyOnDelegatedTurn.OnActionExecuted.
    public void RefundTeamImplementRun(string sessionId, string ownerId) =>
        _teamBudget.RefundTeamImplementRun(sessionId, ownerId);

    // Квота пробуждения штаба агентом (Э4). Тело переехало в TeamBudgetService (волна Е):
    // квота — собственное дело вертикали. Обёртки сохранены ради публичных сигнатур:
    // ReportBlockerAsync (метод уезжает в волну Ж) и SessionMessagingService.SendAsync
    // ходят по этому контракту.
    // TeamMode=false — чат не штаб: ограничение не наше дело, пропускаем как раньше.
    public (bool TeamMode, bool Allowed, string? Reason) TryConsumeTeamWakeup(string sessionId) =>
        _teamBudget.TryConsumeTeamWakeup(sessionId);

    // Компенсация квоты пробуждения (m3, второй проход Глеба). Тело в TeamBudgetService.
    public void RefundTeamWakeup(string sessionId) =>
        _teamBudget.RefundTeamWakeup(sessionId);

    // Решение человека по карточке остановки (SessionHub.RespondTeamEscalation).
    // Тело переехало в TeamDecisionService (волна Д): addBudget / runNext / resume / retryPlan
    // и остальные ветки — собственное дело вертикали (включая развилку Accumulator/диск,
    // которую спрятал публичный ResolveEscalationAsync в ядре). Обёртка сохранена ради
    // публичной сигнатуры — SessionHub зовёт её по этому контракту.
    public Task<bool> RespondTeamEscalationAsync(string sessionId, string escalationId,
        string? actionId, string? comment = null, string? userId = null, string? actorUserId = null) =>
        _teamDecision.RespondTeamEscalationAsync(sessionId, escalationId, actionId, comment, userId, actorUserId);

    // «Остановить» (кнопка человека): текущие исполнители дорабатывают, новые волны не
    // стартуют. Единая точка остановки для кнопки режима (ChatsController) и кнопки
    // «Остановить» информационной карточки волны (RespondTeamEscalationAsync): состояние И
    // карточка возврата живут здесь — без карточки продолжать остановленную практику
    // было бы нечем (сбой 28.08.2026). Тело переехало в TeamDecisionService (волна Д):
    // вертикаль публикует карточку возврата и поднимает состояние. Обёртка сохранена ради
    // публичной сигнатуры — ChatsController и RespondTeamEscalationAsync зовут её
    // по этому контракту.
    public Task<Session?> StopTeamImplementAsync(string sessionId, string? userId = null) =>
        _teamDecision.StopTeamImplementAsync(sessionId, userId);

    // Признак «у чата sessionId есть живая делегированная задача, по которой ждём доклада
    // исполнителя». Вешает сторона задач при регистрации (TaskManager.GetById и проверка
    // полей SourceSessionId/Status/CompletionDelivered/ClaudeStartedAt/ExecutorStoppedAt).
    // Здесь Func остаётся осознанно, в отличие от четырёх штабных, и по ДВУМ причинам, ни
    // одна из которых не про «SessionManager не знает TaskManager» (тот про SessionManager и
    // правда не знает, циклом это не делает). Первая: ставит хук TaskExecutionService
    // (TaskExecutionService.cs:136) — сторона ЧУЖАЯ, а не сама вертикаль, и вот у неё
    // зависимость на SessionManager есть, то есть цикл настоящий. Вторая: TaskManager живёт
    // в вертикали Services.Tasks, и прямая ссылка на него из спины уронила бы сторож границ.
    // null — признак не задан (тесты, либо стора задач нет): ждать нечего.
    public Func<string, bool>? HasLiveDelegatedTasks { get; set; }

    // Старт нового хода в сессии (любой источник: человек, очередь, авто-ход). Вешает
    // TaskExecutionService — по тем же причинам, что HasLiveDelegatedTasks: продолжение в чате
    // исполнителя после «Стоп» снимает пометку остановки, иначе задача числилась бы мёртвой.
    // Сбой хука ход не роняет. null — хук не задан (тесты без стора задач).
    // С S3-7 хук читается в ядре приёма (там старт хода); фасад — точка его установки
    // (та же, что была) и переадресует в ядро.
    public Func<string, Task>? TurnStarting
    {
        get => _intake.TurnStarting;
        set => _intake.TurnStarting = value;
    }

    // Резолв названия задачи по id (волна 1 team-blocker-honest, дефект 1430b732): штаб
    // публикует карточку остановки с TaskId, и подпись диалога снятия должна показать
    // название задачи, а не заголовок карточки. Тот же узкий шов, что у HasLiveDelegatedTasks:
    // SessionManager не знает TaskManager (цикл), а зовущая сторона (TeamDecisionService)
    // зависимость на ядро имеет — поэтому Func, а не прямая ссылка. null — задача
    // не найдена (удалена) или стора задач нет (тесты): подставляется null, фронт падает
    // обратно на заголовок карточки.
    public Func<string, string?>? GetTaskTitle { get; set; }

    // Тонкие обёртки на Core-хелпер TeamProtocolMarkers. Реализации уехали в спину
    // (`ClaudeHomeServer.Core.Services.TeamProtocolMarkers`): их зовёт и ядро SessionManager,
    // и штаб TeamWaveService, и живая трансляция любого чата в OnMessageAsync, и
    // TaskExecutionService. Обёртки оставлены ровно для обратной совместимости тестов
    // SessionManagerTests/TaskExecutionServiceTests — тесты обращаются к этим методам
    // напрямую (`SessionManager.ParseEscalationMarker(text)`), и под-шаг 2а явно требует
    // «зелёные без правок тестов». Семантика и поведение не меняются.
    internal static (TeamEscalationKind Kind, string Text)? ParseEscalationMarker(string text)
        => TeamProtocolMarkers.ParseEscalationMarker(text);

    internal static string? ParseWorkMarker(string text)
        => TeamProtocolMarkers.ParseWorkMarker(text);

    internal static bool HasTalkMarker(string text)
        => TeamProtocolMarkers.HasTalkMarker(text);

    internal const string NoReplyMarker = TeamProtocolMarkers.NoReplyMarker;

    internal static bool HasNoReplyMarker(string text)
        => TeamProtocolMarkers.HasNoReplyMarker(text);

    internal static string StripTeamProtocolMarkers(string text)
        => TeamProtocolMarkers.StripTeamProtocolMarkers(text);

    internal static bool IsAmbiguousMarkerTail(string tail)
        => TeamProtocolMarkers.IsAmbiguousMarkerTail(tail);

    internal static string TrimAmbiguousMarkerTail(string text)
        => TeamProtocolMarkers.TrimAmbiguousMarkerTail(text);

    internal static string TrimUnresolvedMarkerOpen(string strippedText)
        => TeamProtocolMarkers.TrimUnresolvedMarkerOpen(strippedText);

    // Отсечки сторожа волн, погашенные вопросом ASK (OnAskQuestionStabAsync), возвращаются
    // по завершении хода — ответ получен, либо ход прерван (прерывание без result приходит
    // сюда же, на исходах interrupted | crashed, см. HandleTeamTurnCompletedShim). Без
    // возврата волна осталась бы без надзора: настоящий stall никто бы не поймал, а
    // «молчаливых пауз не бывает». Волна должна быть живой: закрытая
    // (ClosedWave == WaveNumber) или нулевая — не в счёт.
    // Owning-обёртка для вертикали TeamTurnCompletionService: вертикаль зовёт с одним
    // sessionId и не видит SessionEntry (40-польная персистентная модель с пятью
    // примитивами синхронизации — общая память двух подсистем не нужна).
    internal void RestoreWaveWatchdogIfPaused(string sessionId)
    {
        if (!_sessions.TryGetValue(sessionId, out var entry)) return;
        RestoreWaveWatchdogIfPaused(sessionId, entry);
    }

    private void RestoreWaveWatchdogIfPaused(string sessionId, SessionEntry entry)
    {
        if (entry.Info.TeamImplement is not { } team) return;
        if (team.Stage != TeamImplementStage.Wave || team.WaveStartedAt is not null) return;
        if (team.WaveNumber == 0 || team.ClosedWave >= team.WaveNumber) return;

        WithTeamState(sessionId, t =>
        {
            t.WaveStartedAt = DateTime.UtcNow;
            t.WaveActivityAt = DateTime.UtcNow;
            return true;
        });
        entry.Info.UpdatedAt = DateTime.UtcNow;
        SaveSessions();
    }

    // Конец хода штаба (Э4 + Э5): тело переехало в TeamTurnCompletionService (волна Ж) —
    // разбор маркеров координатора, гард молчаливого тупика, обработка проверки — всё это
    // собственное дело штабного цикла, и вертикаль владеет единым тестом, чтобы не плодить
    // отдельные развилки «Accumulator vs диск» и обратные рёбра в ядро. Обёртка сохранена
    // ради публичной сигнатуры: HandleTeamTurnCompletedShim и тесты SessionManagerTests
    // зовут HandleTeamTurnEndAsync напрямую.
    public Task HandleTeamTurnEndAsync(string sessionId, string turnText, bool failed, bool asked = false)
        => _teamTurnCompletion.HandleTeamTurnEndAsync(sessionId, turnText, failed, asked);

    // Волна 3 задачи b63fd8ea: хук на завершение фонового async-агента (BgAgentDoneMessage).
    // Внутри вертикали читается session, стадия, планирование — то, что вертикаль уже умеет.
    // asked и hasAsync пробрасываем из ядра, потому что оба значения нужны В ОДНОМ решении
    // ядра (case BgAgentDoneMessage: сброс метки AsyncAgentStallSince под TeamTurnLock на
    // проверке !hasAsync, и тут же публикация карточки по тому же hasAsync). Если бы
    // hasAsync читался через ITeamRunState уже в вертикали (вызовом HasAsyncAgent или иным
    // способом поверх AsyncAgentInFlight), между её вызовом и вызовом этой обёртки пришёл
    // бы другой BgAgentDoneMessage, переписал state и ядро приняло решение по устаревшему
    // снимку. Единый параметр из ядра — общий снимок на оба решения (метка + карточка).
    // В самой вертикали (TeamTurnCompletionService.HandleTeamTurnEndAsync, путь гарда
    // молчаливого тупика) проверка живого async-агента идёт через
    // _run.ShouldSuppressAsyncAgentStallGuard — он под капотом берёт AsyncAgentInFlight(entry)
    // МИМО шва ITeamRunState.HasAsyncAgent (последний на момент ревью оказался невостребован:
    // Глеб проверил удалением объявления и реализации, `dotnet build` прошёл с 0 ошибок).
    // Решения по hasAsync (Major + Minor 2) описаны в шапке HandleBgAgentDoneAsync.
    public Task HandleBgAgentDoneAsync(string sessionId, bool aborted, bool hasAsync, bool asked)
        => _teamTurnCompletion.HandleBgAgentDoneAsync(sessionId, aborted, hasAsync, asked);

    // P23: авто-гашение карточки блокера, когда координатор сам снял её предмет. Тело остаётся
    // в ядре (работает с приватным состоянием entry.Accumulator и приватным _history —
    // 40-польная персистентная модель, пять примитивов синхронизации), вертикаль получает
    // только сессию/текст. Публичный owning-обёртка (волна Ж) для вертикали
    // TeamTurnCompletionService.HandleTeamTurnEndAsync, которая зовёт после разбора
    // маркера эскалации (P23).
    public Task<bool> TryAutoResolveTeamBlockerAsync(string sessionId, string turnText)
    {
        if (!_sessions.TryGetValue(sessionId, out var entry)) return Task.FromResult(false);
        return TryAutoResolveTeamBlockerInternalAsync(sessionId, entry, turnText);
    }

    // Owning-обёртка для вертикали и тулсетов: единая точка снятия блокера по факту
    // (волна 1 team-blocker-honest). Тело в TeamDecisionService — там же, где и сам
    // механизм гашения. Сигналы (tasks_update/tasks_complete/tasks_run_executor/chats_send/
    // SubtaskDropHandler/<team:resolved>) идут через этот метод.
    public Task<bool> TryResolveBlockerByFactAsync(string sessionId, string taskId, string reason) =>
        _teamDecision.TryResolveBlockerByFactAsync(sessionId, taskId, reason);

    // Тело в ядре — здесь лезем в Accumulator/History, а вертикаль видит только результат.
    private async Task<bool> TryAutoResolveTeamBlockerInternalAsync(string sessionId, SessionEntry entry, string turnText)
    {
        if (entry.Info.TeamImplement is not { } team) return false;
        if (team.Stage != TeamImplementStage.AwaitingDecision) return false;

        // Последняя открытая карточка блокера в истории чата (вехи остановки живут дольше хода).
        var openBlocker = entry.Accumulator?.GetAll()
            .OfType<StoredTeamEscalationMessage>()
            .Where(m => !m.Escalation.Resolved && m.Escalation.Kind == TeamEscalationKind.Blocker)
            .LastOrDefault();
        if (openBlocker is null) return false;

        // Координатор снял блокер действием, а не молчанием: либо продолжает работу маркером
        // team:work, или итерация финиширована (все плановые волны закрыты). Иначе карточка
        // уместна — координатор реально ждёт решения человека, оставляем как есть.
        var hasWork = TeamProtocolMarkers.ParseWorkMarker(turnText) is not null;
        var allWavesClosed = AllPlannedWavesClosed(team);
        if (!hasWork && !allWavesClosed) return false;

        // Сведение к общей точке (волна 1 team-blocker-honest): taskId берём из самой карточки
        // (к этому моменту карточка в аккумуляторе заведомо есть — иначе мы бы вышли на :6410).
        // reason — какой именно путь снятия сработал, чтобы подпись «Снят штабом: …» была честной.
        // Возвращаем РЕАЛЬНЫЙ результат гашения: вызывающий в TeamTurnCompletionService при
        // false пропускает перечитывание team-состояния, а при true — перечитывает. Раньше
        // тут стоял безусловный `return Task.FromResult(true)`, который заставлял перечитывать
        // состояние зря, когда гасить было нечего (старый код успевал вернуть true до того,
        // как _teamDecision успевал сказать false).
        var reason = hasWork ? "координатор снял блокер маркером работы"
            : "координатор подвёл итог — все волны плана закрыты";
        return await _teamDecision.TryResolveBlockerByFactAsync(sessionId, openBlocker.Escalation.TaskId ?? "", reason);
    }

    // Новая вводная разложена планировщиком и уходит в волну (Э5). Тело переехало в
    // TeamDecisionService (волна Г): подготовка состояния перед планированием —
    // собственное дело вертикали (гард по стадии, переключение Interview→Planning,
    // открыление свежей итерации в Idle, погашение устаревшей карточки на Confirming).
    // Owning-обёртка для вертикали TeamTurnCompletionService (волна Ж): HandleTeamTurnEndAsync
    // вызывает из разбора маркера team:work по тому же контракту — internal, чтобы
    // вертикаль звала, а снаружи API ядра не открывало.
    internal Task StartTeamWorkAsync(string sessionId, string request, string? feedback = null) =>
        _teamDecision.StartTeamWorkAsync(sessionId, request, feedback);

    // Собственно планирование: вводная (и правка к плану) сохраняются для повтора, зовётся
    // планировщик, а при отказе публикуется карточка с причиной и кнопкой повтора. Тело —
    // в TeamPlanService (волна В). Обёртка сохранена ради сигнатуры: StartTeamWorkAsync,
    // retryPlan и RespondTeamPlanAsync (ветка Edit) зовут её по тому же контракту.
    // Публичный (волна Г): TeamDecisionService.StartTeamWorkAsync/RespondTeamPlanAsync
    // (ветка Edit) зовут её по тому же контракту.
    internal Task RunTeamPlanningAsync(string sessionId, string request, string? feedback,
        bool fromHuman) =>
        _teamPlan.RunTeamPlanningAsync(sessionId, request, feedback, fromHuman);

    // Выход из интервью без работы (M6, маркер `<team:talk/>`): координатор честно разобрал
    // сообщение — это разговор, практику на пустом месте не разворачиваем. Тело переехало
    // в TeamDecisionService (волна Г): выход из интервью и третья дверь в мёртвую зону
    // конвейера — собственное дело вертикали. Owning-обёртка для вертикали
    // TeamTurnCompletionService (волна Ж): HandleTeamTurnEndAsync вызывает из разбора
    // маркера team:talk по тому же контракту — internal, чтобы вертикаль звала.
    internal Task CloseTeamTalkAsync(string sessionId) =>
        _teamDecision.CloseTeamTalkAsync(sessionId);

    // Новая вводная человека (Э5): итерация начинается заново — бюджет обнуляется, счёт волн
    // и остановка сбрасываются. Сбросить может ТОЛЬКО человек: путь сюда один — сообщение
    // через хаб, у агента его нет (chats_send, доклады и авто-ходы идут другими методами).
    // M6: на приёме открываем лишь СВЕЖУЮ итерацию (режим включён, плана ещё не было) — она
    // по определению вводная и по Э8 всегда проходит интервью. В ожидании (Idle) сообщение
    // может оказаться разговором: сброс там делает классификация координатора (маркер работы
    // в StartTeamWorkAsync), а не приём — иначе вопрос «что вы сделали?» обнулял потолки,
    // навязывал план-режим и ловил ложный stall-гард (подтверждено живьём, аудит 2026-08-01).
    // Перепланирование (план уже есть) и ответы на вопросы интервью сюда тоже не попадают.
    private void ResetTeamIterationOnUserInput(string sessionId, SessionEntry entry)
    {
        if (entry.Info.TeamImplement is not { } team) return;
        // Stage по-прежнему исключает волну/ожидание/проверку (как раньше — тест «вводная
        // посреди волны бюджет не сбрасывает» ставит их напрямую, без PlanCardId). Но одного
        // Stage мало: с волны 3 дефолтная стадия свежего режима — тоже Interview (спека Э8),
        // и сам по себе Stage больше не отличает «ни одного сообщения ещё не было» от «уже
        // второй раунд интервью» — оба со Stage=Interview, PlanCardId=null. Различает
        // FirstIterationOpened — взводится только этим методом, один раз за итерацию.
        if (team.FirstIterationOpened || team.PlanCardId is not null
            || team.Stage is not (TeamImplementStage.Planning or TeamImplementStage.Interview)) return;

        WithTeamState(sessionId, t =>
        {
            t.Budget = NewTeamImplementBudget();
            t.WaveNumber = 0;
            t.ClosedWave = 0;
            t.PlannedWaves = 0;
            t.WaveStartedAt = null;
            t.WaveActivityAt = null;
            // «Остановить» относилось к прошлой итерации — новая вводная человека её снимает
            t.Stopped = false;
            // Э8: вход в итерацию — это интервью. Первая вводная проходит его ВСЕГДА: даже
            // кристальная постановка даёт «вопросов нет» и допущения в карточке плана.
            // Счётчик раундов и признак перепланирования — с нуля: они живут на вводную.
            t.Stage = TeamImplementStage.Interview;
            t.InterviewRounds = 0;
            t.Replanning = false;
            t.FirstIterationOpened = true;
            // Э8-фикс (прод 2026-08-03): счёт вводных — под файловые пути плана, растёт
            // на каждой новой (см. IterationNumber).
            t.IterationNumber++;
            return true;
        });
        // План-режим ставим ПОСЛЕ смены стадии: ход по этой самой вводной уже уйдёт в CLI
        // с --permission-mode plan, а не со следующего сообщения (ResetTeamIterationOnUserInput
        // зовётся на приёме сообщения, до очереди и до запуска процесса).
        EnterPlanPhaseMode(sessionId);
        entry.Info.UpdatedAt = DateTime.UtcNow;
        SaveSessions();
        FireAndForget(BroadcastTeamImplementAsync(sessionId, entry),
            $"рассылка состояния режима после новой вводной ({sessionId})");
    }

    // Ответ человека из стадии «ждёт решения» обычным сообщением (M3): практика возвращается
    // в работу так же, как по кнопке карточки (RespondTeamEscalationAsync, ветка по умолчанию) —
    // до первой волны в стадию, из которой ушла в ожидание, иначе в волну со свежими отсечками
    // сторожа. Бюджет и счёт волн НЕ трогаем: итерация та же самая, обнуляет их только новая
    // вводная (ResetTeamIterationOnUserInput) — иначе достаточно было бы отвечать на карточки,
    // чтобы бесконечно продлевать потолок идущей практике.
    // P23: если все плановые волны уже закрыты — возвращать в Wave некуда (вечная «волна N из
    // N»), идём в Idle: итерация завершена, режим ждёт новой вводной.
    // Волна 1 team-blocker-honest: открытые решающие карточки гасятся тем же путём, что кнопка
    // человека — иначе полоса «Практика ждёт вашего решения» продолжает висеть (прод 2026-09).
    // Проходим по всем открытым карточкам с этим TaskId (включая блокеры) и гасим через
    // ResolveEscalationAsync; у блокеров — резолюшн-нота «Ответ сообщением».
    // Карточки, чьё решение по сути и есть текст (S5, фикс-волна). Их гасим текстовым
    // ответом из «ждёт решения». Прочие решающие карточки (BudgetExhausted, WaveGate,
    // Stopped) оставляем человеку: их кнопка делает серверное действие (поднять потолки,
    // раздать волну, снять «Стоп»), которое текст заменить не может. У BudgetExhausted
    // кнопка «Добавить бюджет» — единственный способ поднять потолки, и при погашенной
    // карточке человек остался бы без неё (прод 2026-09 — зафиксировано Глебом).
    private static bool ExtinguishedByMessage(TeamEscalationKind kind) =>
        kind is TeamEscalationKind.Blocker
                or TeamEscalationKind.TaskFailed
                or TeamEscalationKind.PlanDeviation
                or TeamEscalationKind.CheckFailed
                or TeamEscalationKind.ProductDecision;

    private async Task ResumeTeamFromDecisionOnUserInput(string sessionId, SessionEntry entry)
    {
        if (entry.Info.TeamImplement is not { Stage: TeamImplementStage.AwaitingDecision }) return;

        // S5 (фикс-волна): гасим сообщением ТОЛЬКО карточки, чьё решение и есть текст.
        // Не-гасимые (BudgetExhausted, WaveGate, Stopped) оставляем открытыми — иначе
        // человек теряет серверные кнопки.
        var openCards = await ListOpenEscalationsAsync(sessionId);
        foreach (var card in openCards.Where(c => !c.Kind.IsInformational()
                && ExtinguishedByMessage(c.Kind)))
        {
            // M3 (фикс-волна): ResolutionNote пишется в карточку ОДНИМ вызовом вместе с
            // Resolved/ChosenActionId — раньше отдельный MutateCardAsync у активного чата
            // ходил через диск и затирался ближайшим снимком аккумулятора; теперь подпись
            // «Ответ сообщением» переживает перечитывание истории
            var resolved = await ResolveEscalationAsync(sessionId, card.Id, "message", "Ответ сообщением");
            if (resolved is null) continue;
            await BroadcastAsync(sessionId, new TeamEscalationMessage(card.Id,
                card.Kind.ToWireToken(), card.Title, card.Details, card.Actions,
                card.TaskId, card.Wave, Resolved: true, ChosenActionId: "message",
                card.PersonaId, ResolutionNote: "Ответ сообщением",
                TaskTitle: card.TaskTitle));
        }

        WithTeamState(sessionId, t =>
        {
            t.Stage = t.WaveNumber == 0
                ? t.StageBeforeDecision ?? TeamImplementStage.Planning
                : AllPlannedWavesClosed(t)
                    ? TeamImplementStage.Idle
                    : TeamImplementStage.Wave;
            t.StageBeforeDecision = null;
            // Вернулись в волну — заводим страховку таймаута заново (как решение по карточке):
            // без отсечки сторож молчал бы, и повторное зависание осталось бы незамеченным
            if (t.Stage == TeamImplementStage.Wave && t.WaveNumber > 0 && t.ClosedWave < t.WaveNumber)
            {
                t.WaveStartedAt = DateTime.UtcNow;
                t.WaveActivityAt = DateTime.UtcNow;
            }
            return true;
        });
        entry.Info.UpdatedAt = DateTime.UtcNow;
        SaveSessions();
        await BroadcastTeamImplementAsync(sessionId, entry);

        // Мёртвая зона конвейера (прод 2026-08-17): у текстового ответа нет actionId, белым
        // списком кнопок он не покрывался вовсе — после закрытой волны практика возвращалась
        // в Wave без раздачи следующей и стояла часами. Признак тот же, что у кнопок
        // (WaveStartPendingAfterDecision); саму раздачу и её гейты по-прежнему решает
        // TeamWaveService. Повод StateCatchUp (D1): текстовый ответ — не кнопка «Запустить»,
        // при снятых авто-волнах человек получает гейт-карточку, а не молчаливую раздачу.
        if (entry.Info.TeamImplement is { } teamNow
            && teamNow.PlanCardId is { } planId
            && _teamCoordinator.WaveStarter is { } starter)
        {
            var plan = await ((ITeamHistoryStore)this).GetTeamPlanAsync(sessionId, planId);
            if (plan is not null && WaveStartPendingAfterDecision(teamNow, plan))
            {
                try { await starter(entry.Info, plan, TeamWaveTrigger.StateCatchUp); }
                catch (Exception ex)
                {
                    _log.LogError(ex, "Раздача волны после ответа человека (чат {SessionId}) не удалась", sessionId);
                }
            }
        }
    }

    // Все плановые волны итерации закрыты — работа по плану закончена. P23: это сигнал того,
    // что карточка блокера стала неактуальна финалом итерации (координатор подвёл итог, а
    // стадия зависла в AwaitingDecision), и критерий терминального состояния при возврате из
    // «ждёт решения» (RespondTeamEscalation / ResumeTeamFromDecisionOnUserInput) — иначе
    // практика формально «в волне N из N» после полностью закрытых волн. PlannedWaves == 0
    // (план не запускался) никогда не считаем закрытым набором.
    // Дубликат TeamDecisionService.AllPlannedWavesClosed: используется и ядром (в этой же
    // функции для возврата стадии), и вертикалью (волна Г). Стадия по StageBeforeDecision
    // зависит от вертикального решения о возврате в планирование, но критерий закрытия
    // волн — чистая арифметика, и тащить ради неё шов нерационально.
    private static bool AllPlannedWavesClosed(SessionTeamImplement team) =>
        team.PlannedWaves > 0 && team.ClosedWave >= team.PlannedWaves;

    // Мёртвая зона конвейера (прод 2026-08-17): карточка остановки висела ПОСЛЕ закрытия
    // волны — авто-раздача следующей уже была подавлена («практика ждёт человека»), а белый
    // список actionId ответа её не покрывал: конвейер замолкал до ручного «Остановить →
    // Продолжить». Признак «раздачу нужно позвать» после решения человека: практика вернулась
    // в Wave, все РОЗДАННЫЕ волны закрыты (ClosedWave == WaveNumber), а в плане есть
    // нерозданные под-задачи. Решение «раздать или поднять карточку» остаётся за
    // TeamWaveService (бюджет, версии плана, «Остановить») — здесь только «пора ли звать».
    // Дубликат TeamDecisionService.WaveStartPendingAfterDecision: та же логика «пора ли
    // звать» для текстового ответа (D1) и кнопок — вызывается и из ядра (здесь), и из
    // вертикали (волна Г). Дубликат намеренный: это предикат без сайд-эффектов, и тащить
    // ради него шов нерационально.
    private static bool WaveStartPendingAfterDecision(SessionTeamImplement team, TeamImplementPlan plan) =>
        team.Stage == TeamImplementStage.Wave
        && team.WaveNumber > 0
        && team.ClosedWave == team.WaveNumber
        && plan.Subtasks.Any(s => s.TaskId is null);

    // Возврат в интервью (Э8). Два входа: координатор сказал маркером `clarify`, что дальше
    // действовать не может (тупик в волне), либо просто задал человеку вопрос ASK-карточкой —
    // и то и другое означает, что требования неясны. Волны встают на паузу, чат уходит в
    // план-режим, а человек получает карточку «Нужны уточнения» с уведомлением и push:
    // молчаливых пауз в режиме не бывает. После ответов будет план vN на подтверждение.
    // withTurn — поднять координатору ход с просьбой задать вопросы: нужен только маркеру
    // (его ставят в КОНЦЕ ответа, спросить в том же ходу координатор уже не может).
    // При ASK-вопросе ход не нужен — вопросы человек уже видит.
    internal async Task EnterInterviewAsync(string sessionId, string reason, bool withTurn)
    {
        if (!_sessions.TryGetValue(sessionId, out var entry)) return;
        if (entry.Info.TeamImplement is not { } team) return;
        var wave = team.WaveNumber;
        // Уже в интервью — второй карточки и второго хода не надо, но план-режим подтвердим:
        // сюда можно попасть и после рестарта сервера, и повторным вопросом того же раунда.
        var alreadyInterview = team.Stage == TeamImplementStage.Interview;

        var hadPlan = team.PlanCardId is not null;
        var nextVersion = team.PlanVersion + 1;
        WithTeamState(sessionId, t =>
        {
            t.Stage = TeamImplementStage.Interview;
            // Перепланирование: план в итерации уже был, значит следующий — новая версия,
            // и подтверждение карточкой обязательно даже при включённых авто-волнах.
            if (t.PlanCardId is not null) t.Replanning = true;
            // Волна больше не идёт: сторож зависших волн в интервью не тикает — ожидание
            // ответа человека это не зависание.
            t.WaveStartedAt = null;
            t.WaveActivityAt = null;
            return true;
        });
        // План в итерации уже был — его карточка гаснет как заменённая: пока готовится версия
        // vN+1, по старой нельзя ни запустить волну, ни решить что-либо (кнопок у неё нет).
        if (hadPlan) await SupersedeCurrentPlanCardAsync(sessionId, entry.Info, nextVersion);
        EnterPlanPhaseMode(sessionId);
        entry.Info.UpdatedAt = DateTime.UtcNow;
        SaveSessions();
        await BroadcastTeamImplementAsync(sessionId, entry);
        if (alreadyInterview) return;

        _log.LogInformation("Чат-штаб {SessionId} вернулся в интервью: {Reason}", sessionId, reason);

        var card = new TeamEscalation
        {
            Kind = TeamEscalationKind.NeedsClarification,
            Title = TeamImplementPrompts.EscalationTitle(TeamEscalationKind.NeedsClarification, reason),
            Details = TeamImplementPrompts.NeedsClarificationDetails(reason, wave),
            Wave = wave,
            Actions = TeamEscalationActions.For(TeamEscalationKind.NeedsClarification),
        };
        if (_teamCoordinator.EscalationRaiser is { } raise) await raise(entry.Info, card);
        else await ((ITeamHistoryStore)this).PublishTeamEscalationAsync(sessionId, card);

        if (withTurn)
            await _teamIntake.SendOrEnqueueAsync(sessionId, TeamImplementPrompts.ClarifyInterviewTurn(reason, team),
                senderPersonaId: null, silent: true, suppressTasksExecute: true,
                staffNote: TeamStaffNotes.InterviewReturn);
    }

    // === Шов «ядро → штаб» (этап 4, шаг 2б плана выноса штаба) ===
    // Явные реализации ITeamNotifier: наружу доступны только как явная реализация на этом
    // классе — их зовут роли через ленивый шов Lazy<ITeamNotifier> (AddSessionCore). Отдельный
    // держатель шва полем снят в S3-8c: он нигде не читался. Тела повторяют прежние приватные
    // методы один в один — поведение не меняется. В шаге 2г реализация переедет в вертикаль
    // штаба целиком, эти методы уйдут вместе с телом.

    void ITeamNotifier.OnHumanInput(string sessionId)
    {
        if (!_sessions.TryGetValue(sessionId, out var entry)) return;
        ResetTeamIterationOnUserInput(sessionId, entry);
    }

    async Task ITeamNotifier.OnHumanInputAsync(string sessionId)
    {
        if (!_sessions.TryGetValue(sessionId, out var entry)) return;
        await ResumeTeamFromDecisionOnUserInput(sessionId, entry);
    }

    Task ITeamNotifier.OnAskQuestionStabAsync(string sessionId)
        => OnAskQuestionStabAsync(sessionId);

    // S3-6: хук возврата отсечек сторожа волн по ExitedMessage — тело по-прежнему здесь
    // (волны живут в штабе), шов с S3-6 читает его и из TurnCompletion.
    void ITeamNotifier.RestoreWaveWatchdogIfPaused(string sessionId)
        => RestoreWaveWatchdogIfPaused(sessionId);

    void ITeamNotifier.RestoreUserMode(string sessionId)
    {
        // Волна Б: реализация переехала в TeamStateService, шов развернулся в
        // правильную сторону — ядро делегирует в вертикаль, а не наоборот.
        _teamState.RestoreUserMode(sessionId);
    }

    void ITeamRunState.TrySetPermissionModeLive(string sessionId, ClaudeMode mode) =>
        _teamRunState.TrySetPermissionModeLive(sessionId, mode);

    void ITeamRunState.TrySetEntryModeLiveAndStaleAdapter(string sessionId, ClaudeMode mode) =>
        _teamRunState.TrySetEntryModeLiveAndStaleAdapter(sessionId, mode);

    T? ITeamRunState.WithTeamState<T>(string sessionId, Func<SessionTeamImplement, T> mutate) where T : default =>
        _teamRunState.WithTeamState(sessionId, mutate);

    void ITeamRunState.RecordTeamTurnEnd(string sessionId, int turnSeq,
        string? text, bool failed, bool asked) =>
        _teamRunState.RecordTeamTurnEnd(sessionId, turnSeq, text, failed, asked);

    bool ITeamRunState.TryTakeTeamTurnEnd(string sessionId, int turnSeq,
        out string? text, out bool failed, out bool asked) =>
        _teamRunState.TryTakeTeamTurnEnd(sessionId, turnSeq, out text, out failed, out asked);

    bool ITeamRunState.TryClaimSilentStall(string sessionId, out SilentStallClaim claim) =>
        _teamRunState.TryClaimSilentStall(sessionId, out claim);

    bool ITeamRunState.RollbackSilentStallClaim(string sessionId, SilentStallClaim claim) =>
        _teamRunState.RollbackSilentStallClaim(sessionId, claim);

    bool ITeamNotifier.IsSessionBusy(string sessionId)
    {
        if (!_sessions.TryGetValue(sessionId, out var entry)) return false;
        return entry.TeamPlanningInFlight;
    }

    // === Четыре шва данных «штаб → ядро» (этап 4, шаг 2г-3б) ===
    // Явные реализации контрактов из Services/Team/TeamCoreSeams.cs: наружу недоступны, штаб
    // ходит через поля-интерфейсы. Тела — обёртки над прежними методами ядра, поведение один
    // в один; в шаге 2г тело штаба уедет в вертикаль, и эти реализации останутся ЕДИНСТВЕННЫМ
    // местом, где вертикаль касается SessionEntry и словаря сессий.
    // ITeamRunState.HasViewers отдельной обёртки не имеет: публичный HasViewers ядра совпадает
    // с контрактом по сигнатуре и реализует его неявно.

    TeamSessionInfo? ITeamSessionDirectory.Get(string sessionId) => _store.DirectoryGet(sessionId);

    // Сброс каталога на диск. Зовёт вертикаль штаба после правки полей режима.
    // Идемпотентен — внутренний лок SaveSessions сериализует записи и под concurrent.
    // Связки «правка + бродкаст» идут не сюда, а в PersistAndBroadcastAsync того же шва.
    void ITeamSessionDirectory.Persist() => SaveSessions();

    void ITeamSessionDirectory.WriteUnlessPrivate(string sessionId, Action write) =>
        _store.WriteUnlessPrivate(sessionId, write);

    // Точка «правка состояния режима + бродкаст»: тело прежнего
    // TeamStateService.SaveTeamImplementStateAsync, перенесено в шов. public-обёртка
    // SessionManager.SaveTeamImplementStateAsync снята вместе с методом TeamStateService —
    // вертикаль (включая публичный TeamWaveService) ходит через _dir напрямую.
    async Task ITeamSessionDirectory.PersistAndBroadcastAsync(string sessionId)
    {
        var session = GetById(sessionId);
        if (session is null) return;
        session.UpdatedAt = DateTime.UtcNow;
        SaveSessions();
        await BroadcastTeamImplementAsync(sessionId, session);
    }

    IReadOnlyList<TeamSessionInfo> ITeamSessionDirectory.ListChildren(string parentSessionId) =>
        _store.ListChildren(parentSessionId);

    ILookup<string, TeamSessionInfo> ITeamSessionDirectory.ChildrenByParent() => _store.ChildrenByParent();


    Task<bool> ITeamHistoryStore.MutateCardAsync<T>(string sessionId, Func<T, bool> match, Action<T> mutate)
        => _sessions.TryGetValue(sessionId, out var entry)
            ? MutateStoredAsync(entry, sessionId, match, mutate)
            : Task.FromResult(false);

    Task ITeamHistoryStore.AppendAsync(string sessionId, StoredMessage stored, ServerMessage broadcast)
        => AppendStoredAsync(sessionId, stored, broadcast);

    // Единая точка правки/добавления карточки плана (волна В). Тело — в SessionHistory (S3-2);
    // семантика request описана в xml-комментарии ITeamHistoryStore.SavePlanCardAsync.
    Task<bool> ITeamHistoryStore.SavePlanCardAsync(string sessionId, PlanCardWriteRequest req) =>
        _sessionHistory.SavePlanCardAsync(sessionId, req);

    bool ITeamRunState.HasLiveTurn(string sessionId) => _teamRunState.HasLiveTurn(sessionId);

    bool ITeamRunState.HasAsyncAgent(string sessionId) => _teamRunState.HasAsyncAgent(sessionId);

    bool ITeamRunState.TurnStartedByHuman(string sessionId) => _teamRunState.TurnStartedByHuman(sessionId);

    bool ITeamRunState.IsPlanningInFlight(string sessionId) => _teamRunState.IsPlanningInFlight(sessionId);

    void ITeamRunState.SetPlanningInFlight(string sessionId, bool inFlight) =>
        _teamRunState.SetPlanningInFlight(sessionId, inFlight);

    bool ITeamRunState.ShouldSuppressAsyncAgentStallGuard(string sessionId) =>
        _teamRunState.ShouldSuppressAsyncAgentStallGuard(sessionId);

    // Шов штаба (S3-7, аддендум §1): реализует ядро приёма; здесь — однострочная
    // переадресация на него (нужна upcast'у TeamWaveService, код Services/Team не меняется).
    Task<bool> ITeamTurnIntake.SendOrEnqueueAsync(string sessionId, string text,
        string? senderPersonaId, bool silent, bool suppressTasksExecute, string? staffNote, string? senderUserId)
        => _intake.SendOrEnqueueAsync(sessionId, text, senderPersonaId,
            silent: silent, suppressTasksExecute: suppressTasksExecute, staffNote: staffNote,
            senderUserId: senderUserId);

    void ITeamTurnIntake.InterruptTurn(string sessionId) => _intake.InterruptTurn(sessionId);

    // Публикация карточки остановки: запись в ленту + WS + стадия «ждёт решения».
    // Тело в TeamDecisionService (волна Д).
    Task ITeamHistoryStore.PublishTeamEscalationAsync(string sessionId, TeamEscalation escalation) =>
        _teamDecision.PublishTeamEscalationAsync(sessionId, escalation);

    // Открытые (не resolved) карточки остановки чата.
    Task<IReadOnlyList<TeamEscalation>> ITeamHistoryStore.GetOpenTeamEscalationsAsync(string sessionId) =>
        _teamDecision.GetOpenTeamEscalationsAsync(sessionId);

    // Пометка отправленного напоминания по карточке остановки.
    Task<bool> ITeamHistoryStore.MarkTeamEscalationRemindedAsync(string sessionId, string escalationId) =>
        _teamDecision.MarkTeamEscalationRemindedAsync(sessionId, escalationId);

    // План итерации по id: Accumulator.FindTeamPlanAny для активного чата,
    // TeamStateService.GetTeamPlanFromHistoryAsync для неактивного.
    async Task<TeamImplementPlan?> ITeamHistoryStore.GetTeamPlanAsync(string sessionId, string planId)
    {
        if (!_sessions.TryGetValue(sessionId, out var entry)) return null;
        if (entry.Accumulator is { } acc) return acc.FindTeamPlanAny(planId);
        if (entry.Info.ClaudeSessionId is not string key) return null;
        return await _teamState.GetTeamPlanFromHistoryAsync(key, planId);
    }

    // Координатор задал вопрос ASK-карточкой (Э8). В интервью это очередной раунд (их не
    // больше двух на вводную — счёт ведёт бэкенд, модель своих раундов не помнит).
    // Вне интервью вопрос живёт внутри хода и практику НЕ останавливает (решение по запросу
    // владельца 2026-08-04 — единый канал вопросов): продуктовая развилка или уточнение
    // задаётся ASK, ответ приходит в тот же ход и работа продолжается с того же места.
    // Возврат в интервью с паузой волн и перепланированием остался только за явным маркером
    // <escalate:clarify> («требования неясны и действовать нельзя») — прежний вход сюда из
    // ASK делал из любого вопроса пересборку плана.
    internal async Task OnAskQuestionStabAsync(string sessionId)
    {
        if (!_sessions.TryGetValue(sessionId, out var entry)) return;
        if (entry.Info.TeamImplement is not { } team) return;

        if (team.Stage == TeamImplementStage.Interview)
        {
            WithTeamState(sessionId, t => { t.InterviewRounds++; return true; });
            entry.Info.UpdatedAt = DateTime.UtcNow;
            SaveSessions();
            await BroadcastTeamImplementAsync(sessionId, entry);
        }
        else if (team.Stage == TeamImplementStage.Wave)
        {
            // Пока человек отвечает на ASK, гасим отсечки сторожа волн — ожидание человека
            // не зависание (тот же приём, что у стадии «ждёт решения» в
            // PublishTeamEscalationAsync). Иначе при долгом ответе карточка WaveStalled легла
            // бы поверх живого вопроса. Отсечки вернёт конец хода (HandleTeamTurnEndAsync).
            WithTeamState(sessionId, t =>
            {
                t.WaveStartedAt = null;
                t.WaveActivityAt = null;
                return true;
            });
            entry.Info.UpdatedAt = DateTime.UtcNow;
            SaveSessions();
        }

        // Вопрос ждёт человека: уведомление и push, если его нет в чате — звать человека
        // надо в любой стадии, иначе ход молча ждёт клика («молчаливых пауз не бывает»).
        if (_teamCoordinator.QuestionNotifier is { } notify)
        {
            try { await notify(entry.Info); }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Уведомление о вопросе интервью ({SessionId}) не отправлено", sessionId);
            }
        }
    }

    // Эскалация, поднятая самим координатором маркером в ходе (расхождение с планом, красная
    // проверка; продуктовый вопрос ушёл в ASK — маркер decision здесь только фолбэк).
    // Заголовки — из таблицы «Эскалация и остановки».
    // Публичный (волна Ж): TeamTurnCompletionService.HandleTeamTurnEndAsync вызывает при
    // разборе маркера эскалации из turnText и при инфраструктурном обрыве хода проверки —
    // тонкая публикация карточки по EscalationRaiser координатора (иначе без push/уведомления).
    internal async Task RaiseCoordinatorEscalationAsync(string sessionId, TeamEscalationKind kind, string details)
    {
        if (!_sessions.TryGetValue(sessionId, out var entry)) return;
        if (entry.Info.TeamImplement is not { } team) return;

        var escalation = new TeamEscalation
        {
            Kind = kind,
            Title = TeamImplementPrompts.EscalationTitle(kind, details),
            Details = details,
            Wave = team.WaveNumber,
            Actions = TeamEscalationActions.For(kind),
        };
        if (_teamCoordinator.EscalationRaiser is { } raise) await raise(entry.Info, escalation);
        else await ((ITeamHistoryStore)this).PublishTeamEscalationAsync(sessionId, escalation);
    }

    // Отдельное git worktree чата: вкл — создать дерево на новой ветке от HEAD проекта и
    // перевести туда рабочую папку сессии; выкл — вернуть чат в корень проекта и снять дерево.
    // Рабочая папка чата (S3-3): включение/снятие отдельного дерева и привязка к
    // существующему — тела в ChatWorktree; здесь тонкие обёртки с прежними сигнатурами.
    public Task<Session?> SetWorktreeAsync(string sessionId, bool enabled,
        string? branch = null, bool force = false, string? userId = null)
        => _worktree.SetWorktreeAsync(sessionId, enabled, branch, force, userId);

    /// Привязать чат к УЖЕ СУЩЕСТВУЮЩЕМУ дереву (тело — в ChatWorktree, S3-3).
    public Task<bool> AttachWorktreeAsync(string sessionId, string worktreePath, string? branch = null)
        => _worktree.AttachWorktreeAsync(sessionId, worktreePath, branch);

    /// Существует ли путь на диске и числится ли он в `git worktree list` проекта (тело — в
    /// ChatWorktree). Нужен исполнителю задачи, когда AttachWorktreeAsync отказал: отличить
    /// «дерево есть, но чат в него не встал» от «дерева нет вовсе». Сбой git — false (fail-closed).
    public Task<bool> IsProjectWorktreeAsync(string projectId, string? ownerId, string worktreePath)
        => _worktree.IsProjectWorktreeAsync(projectId, ownerId, worktreePath);

    // --- Политика добивания и паспорта (S3-4) ---
    // Чистые функции ушли в Services/Sessions/SubagentNudgePolicy.cs, подписчики шины —
    // в Services/Sessions/TurnPassports.cs. Обёртки сохранены: на них сидят TaskExecutionService,
    // цикл «до готово» здесь и существующие тесты (SubagentNudgeTests, SessionManagerTestBase).
    internal const int MaxSubagentNudges = Sessions.SubagentNudgePolicy.MaxSubagentNudges;
    internal static bool ShouldNudgeSubagent(int nudgesSent, bool workLoopActive, bool teamActive,
        bool hasPending, bool loopTurnInFlight, bool isInterruptedRun = false) =>
        Sessions.SubagentNudgePolicy.ShouldNudgeSubagent(nudgesSent, workLoopActive, teamActive,
            hasPending, loopTurnInFlight, isInterruptedRun);
    internal static bool TurnInFlight(SessionStatus status) => Sessions.SubagentNudgePolicy.TurnInFlight(status);
    internal static bool ResetsNudgeSeries(string? nudgeAgentId, string reportedAgentId) =>
        Sessions.SubagentNudgePolicy.ResetsNudgeSeries(nudgeAgentId, reportedAgentId);
    internal static bool StartsNudgeSeries(string? nudgeAgentId, string truncatedAgentId) =>
        Sessions.SubagentNudgePolicy.StartsNudgeSeries(nudgeAgentId, truncatedAgentId);
    internal static bool RefutesTruncation(string? markedAgentId, string reportedAgentId) =>
        Sessions.SubagentNudgePolicy.RefutesTruncation(markedAgentId, reportedAgentId);

    // Синхронный мост тестов на шину (его берут через рефлексию): реализует TurnPassports,
    // здесь — та же сигнатура.
    internal Action<Llm.SubagentRunPassport>? SubagentRunSinkFor(string sessionId) =>
        _passports.SubagentRunSinkFor(sessionId);

    // Добивание: директива координатору дослать оборванному сабагенту продолжение. Тем же
    // способом, что и цикл «до готово» (systemDirective-ход после result), и с тем же
    // потолком попыток — см. SubagentPrompts.ResumeTruncated. Для класса «убит прерыванием»
    // (FinishedBy == "interrupted") политика сюда не пустит (ShouldNudgeSubagent с
    // isInterruptedRun: true), но если когда-то дойдёт — текст берётся по-другому
    // (ResumeInterrupted: не «дослать продолжить», а «ход прерван, транскрипт цел»).
    private async Task NudgeTruncatedSubagentAsync(string sessionId,
        Llm.SubagentRunPassport run, int attempt)
    {
        // Окно штатной остановки: директива ушла бы в умирающий адаптер (флаг — в ядре,
        // S3-7; здесь чтение как ранняя отсечка, решающая проверка — внутри ядра)
        if (_intake.Stopping) return;
        if (!_sessions.TryGetValue(sessionId, out var entry) || entry.Process is null) return;
        // Обрыв опровергнут, пока добивание планировалось (финал агента доехал до транскрипта
        // после перепроверки): последний паспорт агента уже штатный — директиву не шлём.
        // Источник истины — стор паспортов, а не пометки на сессии: их разобрали до планирования
        if (_subagentRuns?.Latest(run.AgentId) is { Truncated: false }) return;
        // Директива добивания уже несёт все факты обрыва — дублировать их пометкой
        // в том же ходе незачем (пометка чужого агента остаётся ждать своего хода)
        if (entry.TruncatedBgNote?.AgentId == run.AgentId) entry.TruncatedBgNote = null;
        _subagentRuns?.NoteNudge(run.AgentId);
        _log.LogWarning("Сабагент {AgentId} ({AgentType}) оборвался на {Tool} после {Tools} вызовов " +
            "и {Seconds} с (контекст {Context} токенов) — добивание {Attempt}/{Max}, класс {Class}, чат {SessionId}",
            run.AgentId, run.AgentType, run.LastTool, run.ToolUses, run.DurationSeconds,
            run.ContextTokens, attempt, MaxSubagentNudges, run.FinishedBy, sessionId);
        var directive = run.FinishedBy == "interrupted"
            ? Prompts.SubagentPrompts.ResumeInterrupted(run, attempt, MaxSubagentNudges)
            : Prompts.SubagentPrompts.ResumeTruncated(run, attempt, MaxSubagentNudges);
        // (S3-7) Директиву в ход просит ядро приёма: отказ ядра (остановка, нет сессии) —
        // хода нет, нудж молча не состоялся; повторной постановки здесь не было и раньше.
        await _intake.TrySubmitContinuationAsync(sessionId, directive, DeliveryCause.SubagentNudge);
    }

    // Автопродолжение цикла «до готово»: вызывается по result хода, нёсшего протокол цикла.
    // Маркер найден → верификационный ход, затем стоп; нет → продолжение до лимита итераций
    // либо уход в фазу waiting, если у чата есть живые делегированные задачи.
    private async Task ContinueWorkLoopAsync(string sessionId)
    {
        // Окно штатной остановки: новый ход цикла поднял бы процесс, который тут же убьют.
        // Флаг — в ядре приёма (S3-7); здесь чтение — ранняя отсечка, решающая проверка —
        // внутри TrySubmitContinuation (Stop между ними не поднимает хода в остановленную сессию)
        if (_intake.Stopping) return;
        if (!_sessions.TryGetValue(sessionId, out var entry)) return;
        if (entry.Info.WorkLoop is not { } loop) return;

        // Возврат из waiting: предыдущая итерация ушла в ожидание исполнителя, и теперь
        // (доставлен доклад, человек вмешался, либо пришёл алерт молчания) цикл реально
        // продолжает работу. Снимаем фазу ДО гейтов, чтобы фаза не «застряла» при уступке.
        // Заодно обнуляем счётчик тиков и причину: новая фаза waiting (если будет) стартует
        // с чистого счётчика, а старая причина в логе/бейдже «ожидание» уже неактуальна.
        //
        // ИНВАРИАНТ: ++ за result хода тратится РОВНО ОДИН РАЗ — здесь (если был возврат
        // waiting→working) ИЛИ в конце метода (если блок не сработал). Две точки кода,
        // взаимоисключающие по флагу wasReturnFromWaiting: иначе result после возврата
        // засчитывался бы дважды. Тики ожидания НЕ считаются итерациями — сама фаза
        // waiting бесплатна по счёту. Проверка лимита — общий хелпер
        // StopIfWorkLoopLimitReachedAsync: один текст остановки и один путь reason="limit",
        // без двух копий.
        var wasReturnFromWaiting = false;
        if (loop.Phase == "waiting")
        {
            loop.Phase = "working";
            loop.WaitingTicks = 0;
            loop.WaitingReason = null;
            loop.WaitingSince = null;
            wasReturnFromWaiting = true;
            loop.Iteration++;
            SaveSessions();
            await BroadcastWorkLoopAsync(sessionId, entry);
            if (await StopIfWorkLoopLimitReachedAsync(sessionId, entry, loop)) return;
        }

        // Без двойной отправки (гонка «result → директива продолжения» vs «очередь доставляет
        // сообщение пользователя»): если пользователь успел прислать сообщение в этом ходе или
        // между итерациями, оно само продолжит цикл как следующая итерация (доставку выполнит
        // drain). Доклад исполнителя (Report) — аналогично: drain его вытащит, и ход-реакция
        // вернёт цикл к работе. Системную директиву продолжения/верификации в этом случае не
        // шлём — иначе два хода подряд ушли бы в один процесс. Проверка атомарна с извлечением
        // drain'а: LoopTurnInFlight=true означает, что drain уже вытащил сообщение и запускает
        // итерацию. Маркер взводим ПОД ТЕМ ЖЕ PendingLock, что и гейт (Major 3): иначе в окне до
        // BuildCliTurnText (SaveSessions/Broadcast/EnsureProcess, сотни мс) параллельный drain
        // успевал вытащить пользовательское сообщение и пустить второй ход в тот же процесс.
        // До всей логики цикла (phase/iteration) — чтобы не оставлять изменённое состояние при уступке.
        lock (entry.PendingLock)
        {
            if (entry.Pending.Any(p => p.Kind is PendingKind.User or PendingKind.Report)) return;
            if (entry.LoopTurnInFlight) return;
            entry.LoopTurnInFlight = true;
        }

        try
        {
            // Буфер потребляем и чистим здесь (а не при постановке хода): очистка на постановке
            // стирала бы текст стримящегося хода при параллельной отправке пользователя
            string turnText;
            lock (entry.LoopTurnLock)
            {
                turnText = entry.LoopTurnText.ToString();
                entry.LoopTurnText.Clear();
            }

            if (entry.LoopTurnFailed)
            {
                await AddWorkLoopStoppedNoticeAsync(sessionId, entry, "error", "Цикл остановлен: ход завершился ошибкой.");
                await SetWorkLoopAsync(sessionId, false);
                return;
            }

            var promiseFound = ContainsPromiseMarker(turnText, loop.Promise);

            if (loop.Phase == "verifying")
            {
                // Верификационный ход отработал — цикл завершён независимо от исхода (штатное
                // окончание, свидетельства уже в самом верификационном посте — отдельное
                // сообщение-остановка тут не нужно, в отличие от лимита/ошибки/ручного стопа).
                // Блокер тут НЕ проверяем намеренно: верификация уже отвечает «да/нет», и
                // второй слой семантики поверх неё не нужен.
                await SetWorkLoopAsync(sessionId, false);
                return;
            }

            // ПОРЯДОК ВАЖЕН: блокер проверяем раньше промиса. Если модель вывела оба, это
            // противоречие («готово, но встал») — дешевле встать сразу по блокеру, чем
            // гонять верификационный ход по сомнительному «готово» и плодить ещё одну
            // итерацию до лимита. Не переставляй местами «как более логичный» порядок.
            if (TryExtractBlockedMarker(turnText, out var blockedReason))
            {
                var notice = blockedReason is null
                    ? "Цикл остановлен: работа встала на блокере."
                    : $"Цикл остановлен: работа встала на блокере — {blockedReason}";
                await AddWorkLoopStoppedNoticeAsync(sessionId, entry, "blocked", notice);
                // SetWorkLoopAsync(false) сама сбрасывает LoopTurnInFlight и размораживает
                // очередь — своей уборки рядом не добавляем, иначе отстаём от инварианта.
                await SetWorkLoopAsync(sessionId, false);
                return;
            }

            if (promiseFound)
            {
                loop.Phase = "verifying";
                SaveSessions();
                await BroadcastWorkLoopAsync(sessionId, entry);
                if (entry.Info.WorkLoop is null) return; // Стоп успел снять цикл — ход-сироту не шлём
                // (S3-7) Директиву в ход просит ядро приёма (решающая проверка остановки — там).
                // Отказ ядра — хода нет; снятый здесь маркер не оставит цикл на мёртвой итерации.
                if (!await _intake.TrySubmitContinuationAsync(sessionId, OmoPrompts.WorkLoopVerification))
                    lock (entry.PendingLock) entry.LoopTurnInFlight = false;
                return;
            }

            // ПОРЯДОК ВАЖЕН: маркер `<waiting>` проверяем ПОСЛЕ `<promise>`. Если модель вывела
            // оба, это противоречие («готово» и одновременно «жду»), и верификационный ход
            // разрешит его дешевле, чем лишний круг ожидания. Симметрично блокеру выше.
            //
            // ФАЗА ОЖИДАНИЯ: у чата есть живые делегированные задачи (координатор запустил
            // исполнителя и не получил доклада) ИЛИ координатор сам вывел `<waiting>`
            // (ждёт внешнего события, о котором система знать не может — ответ на chats_send,
            // чужой процесс, человек вне чата). Пока ждём — итерации не тратим, директиву
            // продолжения НЕ шлём. ВАЖНО: LoopTurnInFlight снимаем под PendingLock — иначе
            // drain (DrainNextPendingAsync) вечно уступает на гейте, и доклад не доедет
            // НИКОГДА. Цикл повиснет намертво. Возврат — на приходе Report/user-сообщения
            // (DrainNextPendingAsync) и в начале самого ContinueWorkLoopAsync: фаза
            // «waiting» переключается обратно в «working» ДО гейтов выше.
            var waitingReason = TryExtractWaitingMarker(turnText, out var waitingFromMarker);
            var liveDelegated = HasLiveDelegatedTasks?.Invoke(sessionId) == true;
            if (waitingFromMarker != null || liveDelegated)
            {
                var reason = waitingFromMarker; // null → ожидание по живой задаче (доклад придёт сам)
                // Переустанавливаем счётчик ТОЛЬКО при смене причины ожидания: маркер
                // пришёл впервые (или сменился текст) — стартуем с чистого счётчика;
                // тот же маркер в повторной итерации (модель тика не вывела, вернулась
                // с тем же текстом) — продолжаем счёт. Иначе координатор, который
                // каждые 5 минут выводит ОДНО И ТО ЖЕ, тикал бы вечно без шанса дойти
                // до лимита.
                var newWaiting = waitingFromMarker != null && waitingFromMarker != loop.WaitingReason;
                if (newWaiting)
                {
                    loop.WaitingReason = waitingFromMarker;
                    loop.WaitingSince = DateTime.UtcNow;
                    loop.WaitingTicks = 0;
                }
                lock (entry.PendingLock) entry.LoopTurnInFlight = false;
                loop.Phase = "waiting";
                SaveSessions();
                await BroadcastWorkLoopAsync(sessionId, entry);
                if (entry.Info.WorkLoop is null) return; // Стоп успел снять цикл — без хода
                _log.LogInformation("Цикл {Session} ушёл в фазу ожидания (причина: {Reason}, итераций {Iter}/{Max})",
                    sessionId,
                    liveDelegated ? "живая делегированная задача" : (waitingFromMarker ?? "не указана"),
                    loop.Iteration, loop.MaxIterations);
                return;
            }

            // Чистый working-ход (без возврата из ожидания): Iteration тратится здесь — счётчик
            // считает все ходы цикла. Возврат из ожидания уже инкрементировал выше (флаг
            // wasReturnFromWaiting=true), так что ++ здесь срабатывает ТОЛЬКО для обычных
            // ходов. Тики ожидания бесплатны — они не доходят до этой ветки. Проверка лимита
            // и текст остановки — общий хелпер.
            if (!wasReturnFromWaiting) loop.Iteration++;
            SaveSessions();
            await BroadcastWorkLoopAsync(sessionId, entry);
            if (await StopIfWorkLoopLimitReachedAsync(sessionId, entry, loop)) return;
            if (entry.Info.WorkLoop is null) return; // Стоп успел снять цикл — ход-сироту не шлём
            // (S3-7) Директиву продолжения в ход просит ядро приёма; отказ ядра (Stop между
            // ранней отсечкой и этим местом) — хода нет, маркер итерации снимаем здесь же.
            if (!await _intake.TrySubmitContinuationAsync(sessionId,
                OmoPrompts.WorkLoopContinuation(loop.Promise, loop.Iteration, loop.MaxIterations)))
                lock (entry.PendingLock) entry.LoopTurnInFlight = false;
        }
        catch
        {
            // Директива так и не ушла (сбой до/в отправке) — ход result не пришлёт, и взведённый
            // выше маркер повис бы, заблокировав разбор очереди (drain уступает, пока он взведён).
            // Пути остановки снимают цикл через SetWorkLoop(false) — та сбрасывает маркер сама;
            // здесь ловим только исключение до отправки. Перебрасываем — вызывающий (Task.Run по
            // result) залогирует.
            // (S3-7c) Снятие — под PendingLock, как подъём маркера в начале метода и в drain'e:
            // гейт разбора очереди читает маркер под этим же замком, состояние маркера меняется
            // атомарно относительно операций над очередью. Сам этот catch очередь не трогает.
            if (entry.Info.WorkLoop is not null)
                lock (entry.PendingLock) entry.LoopTurnInFlight = false;
            throw;
        }
    }

    // Общая проверка лимита итераций для цикла «до готово». Вызывается ПОСЛЕ каждого
    // инкремента Iteration (в блоке возврата waiting→working и в конце обычного working-хода) —
    // одна точка правды для reason="limit" и текста остановки. Возвращает true, если лимит
    // достигнут и цикл остановлен (вызывающий обязан сделать return). Хелпер общий, а не
    // две копии, чтобы текст и путь стопа не разъехались при будущих правках.
    private async Task<bool> StopIfWorkLoopLimitReachedAsync(string sessionId, SessionEntry entry, SessionWorkLoop loop)
    {
        if (loop.Iteration < loop.MaxIterations) return false;
        await AddWorkLoopStoppedNoticeAsync(sessionId, entry, "limit",
            $"Цикл остановлен: исчерпан лимит в {loop.MaxIterations} ходов. " +
            "Работа могла остаться незавершённой — проверьте результат.");
        await SetWorkLoopAsync(sessionId, false);
        return true;
    }

    // Маркер завершения ищем вне код-блоков и с точным регистром: модель часто цитирует
    // протокол в начале хода («когда закончу — выведу `<promise>…</promise>`») — бэктики
    // и ``` не считаются исполнением обещания
    internal static bool ContainsPromiseMarker(string text, string promise)
    {
        var stripped = StripCodeBlocks(text);
        return stripped.Contains($"<promise>{promise}</promise>", StringComparison.Ordinal);
    }

    // Маркер блокера ищем по тем же правилам, что и промис (вне код-блоков и инлайн-кода,
    // регистр тега точный): модель цитирует протокол в начале хода — бэктики/``` не считаются
    // реальной остановкой. Причину возвращаем в первом вхождении (после схлопывания \r\n и
    // обрезки до 300 символов — она едет в ленту текстом уведомления). Пустой тег
    // `<blocked></blocked>` — валидная остановка без причины: TryExtract вернёт true и
    // reason == null.
    internal static bool TryExtractBlockedMarker(string text, out string? reason)
    {
        reason = null;
        var stripped = StripCodeBlocks(text);
        var match = System.Text.RegularExpressions.Regex.Match(
            stripped, "<blocked>([\\s\\S]*?)</blocked>");
        if (!match.Success) return false;

        var raw = match.Groups[1].Value ?? string.Empty;
        // Схлопываем переводы строк (в т.ч. \r\n) в пробел и подрезаем края.
        var collapsed = System.Text.RegularExpressions.Regex.Replace(raw, "\\s+", " ").Trim();
        if (collapsed.Length == 0) return true;
        if (collapsed.Length > 300) collapsed = collapsed[..300];
        reason = collapsed;
        return true;
    }

    // Маркер ожидания — симметричен блокеру: вне код-блоков, точный регистр, пустой тег
    // валиден. Это второй источник ухода в фазу waiting (первый — живая делегированная задача,
    // HasLiveDelegatedTasks): модель ждёт внешнего события, о котором система знать не может
    // (ответ на chats_send, чужой процесс, человек вне чата). Без этого координатор либо
    // жжёт итерации, либо выводит `<blocked>` и валит цикл — оба варианта врут про состояние.
    internal static bool TryExtractWaitingMarker(string text, out string? reason)
    {
        reason = null;
        var stripped = StripCodeBlocks(text);
        var match = System.Text.RegularExpressions.Regex.Match(
            stripped, "<waiting>([\\s\\S]*?)</waiting>");
        if (!match.Success) return false;

        var raw = match.Groups[1].Value ?? string.Empty;
        var collapsed = System.Text.RegularExpressions.Regex.Replace(raw, "\\s+", " ").Trim();
        if (collapsed.Length == 0) return true;
        if (collapsed.Length > 300) collapsed = collapsed[..300];
        reason = collapsed;
        return true;
    }

    // Тик ожидания по маркеру `<waiting>`. Обходит все активные сессии раз в
    // Loop:WaitingTickSeconds и шлёт координатору системную директиву-тик, если:
    //   - цикл включён И фаза == "waiting" И WaitingReason != null (по маркеру, не по задаче);
    //   - цикл включён И фаза == "waiting" И WaitingReason != null (по маркеру, не по задаче);
    //   - с момента входа в фазу прошло ≥ интервала (первый тик — через полный интервал);
    //   - чат СВОБОДЕН: нет живого прогона (HasLiveTurn=false), нет маркера хода-итерации
    //     (LoopTurnInFlight=false), статус не Working/Waiting (нет текущего хода).
    // Тикаем ТОЛЬКО ожидание по маркеру: ожидание по живой делегированной задаче
    // (HasLiveDelegatedTasks=true, WaitingReason=null) тикать НЕ надо — доклад придёт сам,
    // а смерть исполнителя ловит алерт молчания. Разводим явно: ветка `WaitingReason != null`.
    // На потолке (WaitingTicks >= _maxWaitingTicks) — стоп с reason="waiting_timeout" и
    // причиной ожидания в тексте уведомления.
    // internal: тесты SessionManagerTests зовут напрямую (через рефлексию для подмены
    // _waitingTickInterval). В боевом коде вызывается только фоновым таймером _waitingTickTimer.
    internal async Task TickWaitingLoopsAsync()
    {
        // Копия id под перебор: тик может уводить цикл в стоп и чистить entry —
        // _sessions меняется «под нами», итерировать оригинал нельзя.
        var sessionIds = _sessions.Keys.ToArray();
        foreach (var sessionId in sessionIds)
        {
            if (!_sessions.TryGetValue(sessionId, out var entry)) continue;
            if (entry.Info.WorkLoop is not { } loop) continue;
            if (loop.Phase != "waiting") continue;
            if (loop.WaitingReason is null) continue; // ожидание по задаче — не тикаем

            // Чат занят — пропускаем. Следующий тик через интервал догонит.
            if (entry.LoopTurnInFlight) continue;
            if (entry.Info.Status is SessionStatus.Working or SessionStatus.Waiting) continue;
            if (HasLiveTurnProcess(sessionId)) continue;

            // С момента входа в фазу прошло ≥ интервала? WaitingSince ставится на входе
            // и не сбрасывается на тиках — отсюда и первый тик через полный интервал, и
            // все последующие через интервал (таймер сам по себе периодичен).
            var sinceUtc = (loop.WaitingSince ?? DateTime.UtcNow).ToUniversalTime();
            if (DateTime.UtcNow - sinceUtc < _waitingTickInterval) continue;

            loop.WaitingTicks++;
            if (loop.WaitingTicks >= _maxWaitingTicks)
            {
                var notice = $"Цикл остановлен: ожидание по маркеру «{loop.WaitingReason}» " +
                             $"не завершилось за {loop.WaitingTicks * (int)_waitingTickInterval.TotalSeconds / 60} минут " +
                             $"({loop.WaitingTicks} из {_maxWaitingTicks} тиков).";
                _log.LogWarning("Цикл {Session}: исчерпан потолок тиков ожидания ({Ticks}/{Max})",
                    sessionId, loop.WaitingTicks, _maxWaitingTicks);
                // AddWorkLoopStoppedNoticeAsync сам персистит и рассылает; стоп идёт
                // через SetWorkLoopAsync, которая сбрасывает LoopTurnInFlight и фазу.
                await AddWorkLoopStoppedNoticeAsync(sessionId, entry, "waiting_timeout", notice);
                await SetWorkLoopAsync(sessionId, false);
                continue;
            }

            // Тик НЕ тратит Iteration — только WaitingTicks. Директива уходит системной:
            // координатор увидит подсказку и сам решит, выводить ли `<waiting>` снова.
            // BuildCliTurnText взводит LoopTurnInFlight (под PendingLock — тем же замком, что
            // и гейт разбора очереди), а ContinueWorkLoopAsync
            // по result хода-тика проверит, остался ли маркер, и вернёт фазу.
            _log.LogInformation("Цикл {Session}: тик ожидания {Ticks}/{Max} (причина: {Reason})",
                sessionId, loop.WaitingTicks, _maxWaitingTicks, loop.WaitingReason);
            SaveSessions();
            await BroadcastWorkLoopAsync(sessionId, entry);
            if (entry.Info.WorkLoop is null) continue; // стоп успел снять цикл
            // (S3-7) Тик просит ход у ядра приёма (директива — тем же путём, что и продолжение).
            // Сам тик маркера не взводил (его поднимает состав хода в ядре) — отказ ядра нечего снимать.
            await _intake.TrySubmitContinuationAsync(sessionId,
                OmoPrompts.WorkLoopWaitingTick(loop.WaitingReason,
                    loop.WaitingTicks, _maxWaitingTicks,
                    (int)_waitingTickInterval.TotalSeconds));
        }
    }

    // Общая чистка код-блоков и инлайн-кода — используется детекторами маркеров протокола
    // цикла «до готово». Бэктики и ``` не считаются исполнением обещания/блокера: модель
    // часто цитирует протокол в начале хода («выведу `<promise>…</promise>` когда закончу»).
    private static string StripCodeBlocks(string text)
    {
        var stripped = System.Text.RegularExpressions.Regex.Replace(text, "```[\\s\\S]*?(```|$)", "");
        stripped = System.Text.RegularExpressions.Regex.Replace(stripped, "`[^`\n]*`", "");
        return stripped;
    }

    // Решение (Minor, волна 3, осознанно оставлено как есть) — полное описание каскада и
    // причины не чинить сейчас — у тела удаления в ChatDeletion: живые волны и дочерние чаты
    // исполнения при удалении чата-штаба не отменяются и не переносятся.
    // Удаление чата (S3-5): тело в ChatDeletion. Наблюдатель фасада OnSessionDeleted приходит
    // в роль колбэком вызова — порядок «SaveSessions → наблюдатель → события подсистем →
    // рассылка» сохранён; инвариант «уносим ровно {csid}.jsonl, не по маске и не папку»
    // покрыт SessionManagerCrudTests (DeleteAsync_УдаляетТранскриптCliИНеТрогаетЧужой).
    public async Task DeleteAsync(string sessionId)
        => await _deletion.DeleteAsync(sessionId, s => OnSessionDeleted?.Invoke(s));

    // Транскрипт claude CLI удаленного чата ({профиль}/projects/{уплощенный cwd}/{csid}.jsonl).
    // Тело — в CliRoute (S3-2): там же TranscriptSearchRoots и TryResolveCwd. Инвариант
    // «уносим ровно {csid}.jsonl, не по маске и не папку» — в теле роли, покрыт
    // SessionManagerCrudTests (DeleteAsync_УдаляетТранскриптCliИНеТрогаетЧужой).
    // DeleteTranscript (обёртка маршрута) и CleanupOrphanBranchTranscript переехали в
    // TurnCompletion (S3-6): их единственный вызов — case SessionStartedMessage разбора хода.


    // Фасад: чтение ленты (SessionHistory) + досчёт цен (TurnCostLedger). Склейка здесь, а не
    // внутри истории, намеренно: журнал цен пишет в историю, и склейка внутри истории была бы
    // циклом роль→журнал→роль (S3-0 §1 «цены fal/glif»).
    public async Task<IReadOnlyList<StoredMessage>> GetHistoryAsync(string sessionId)
    {
        var list = await _sessionHistory.GetHistoryAsync(sessionId);
        // Догоняем стоимость старых fal-генераций, у которых её ещё нет (фоном, дедуп внутри)
        _costs.BackfillFalCosts(sessionId, list);
        // Догоняем учёт старых glif-генераций, у которых ещё нет glif_cost
        _costs.BackfillGlifCosts(sessionId, list);
        return list;
    }

    public IReadOnlyList<WorkflowProgressMessage> GetWorkflowProgress(string sessionId) =>
        _sessionHistory.GetWorkflowProgress(sessionId);

    // Ожидающая карточка взаимодействия (разрешение/вопрос/план) — replay при JoinSession
    public ServerMessage? GetPendingInteraction(string sessionId) => _sessionHistory.GetPendingInteraction(sessionId);

    // Последний манифест recall (F3) сессии — replay при подключении клиента (JoinSession)
    public RecallManifestMessage? GetLastRecallManifest(string sessionId) => _sessionHistory.GetLastRecallManifest(sessionId);

    public Session? GetSessionInfo(string sessionId)
    {
        _sessions.TryGetValue(sessionId, out var entry);
        return entry?.Info;
    }

    // --- Внутренняя логика ---

    // runId — прогон адаптера, чей read-loop прислал сообщение (0 у внутренних вызовов без
    // прогона): по нему отличаем exited ЭТОГО прогона от позднего exited доживающего
    // internal (InternalsVisibleTo): тесты гоняют сообщения (в т.ч. ResultMessage) через
    // обработчик, чтобы проверить побочные эффекты (сброс RestartResumeAttempts, статус).
    // Конец хода (S3-6): тело OnMessageAsync/OnMessageQueuedAsync, очередь рассылки
    // (GetPipeline/EnqueueWorkAsync/ProcessPipelineItemAsync), ApplyStatusQueuedAsync и
    // CleanupOrphanBranchTranscript переехали в TurnCompletion — дословно, с комментариями.
    // Здесь остаются делегирующие обёртки прежних internal-входов: тесты и тела приёма хода
    // (RefuseTurnAsync, локальный голос) зовут их по-прежнему; явная реализация
    // шва приёма сообщений снята — его владелец теперь роль (SessionCoreRegistration).
    internal async Task OnMessageAsync(string sessionId, TurnAccumulator acc, ServerMessage msg, long runId = 0)
    {
        await _completion.OnMessageAsync(sessionId, acc, msg, runId);
    }

    internal SessionMessagePipeline? PipelineForTest(string sessionId) =>
        _completion.PipelineForTest(sessionId);

    // Дожидается разбора всего, что легло в очередь сессии (тесты и точки, которым нужны
    // отработавшие наблюдатели)
    internal Task DrainPipelineAsync(string sessionId) => _completion.DrainPipelineAsync(sessionId);

    internal Task OnMessageQueuedAsync(string sessionId, TurnAccumulator acc, ServerMessage msg, long runId = 0) =>
        _completion.OnMessageQueuedAsync(sessionId, acc, msg, runId);

    // Цены хода (трекинг/догоняющий учёт/публикация fal и glif) — в TurnCostLedger (S3-2).
    // Вызывающие места: приём хода — в роли (TurnCompletion: TryTrack* на tool_result хода),
    // загрузка истории — здесь, в GetHistoryAsync выше (Backfill* по загруженной ленте), и
    // в роли ChatNaming (та же склейка ленты). Отдельных обёрток трекинга/бэкфилла в ядре
    // нет: бэкфилл вызывается прямо из тела загрузки истории — это боевой вход догоняющего
    // учёта, защищён FalCostProductionPathTests (БэкфиллИсторииСПакетнымToolResult…).

    // Публикация учёта glif-генерации: запись в историю (дедуп) + broadcast клиентам.
    public Task PublishGlifCostAsync(string sessionId, GlifCostMessage msg) => _costs.PublishGlifCostAsync(sessionId, msg);

    // Публикация найденной стоимости fal.ai: запись в историю (дедуп) + broadcast клиентам.
    // Публично: на него ставит колбэк FalCostService.OnCostResolved сам TurnCostLedger.
    public Task PublishFalCostAsync(string sessionId, FalCostMessage msg) => _costs.PublishFalCostAsync(sessionId, msg);

    // Запись расхода штатного хода в аналитику — вынесена в SpendMapping.RecordTurnSpend
    // (этап 4, волна 1 «приём хода», 2026-09-07): код спины, использующий обе стороны
    // (ISpendCollector подсистемы Spend и LlmProviderRegistry слоя Llm), без состояния,
    // держать его в ядре SessionManager было лишним весом.

    // Запись StoredMessage в историю сессии ВНЕ хода + broadcast. Тело — в SessionHistory (S3-2).
    public Task AppendStoredAsync(string sessionId, StoredMessage stored, ServerMessage broadcast, bool inTurn = false) =>
        _sessionHistory.AppendStoredAsync(sessionId, stored, broadcast, inTurn);

    private Task ApplyStatusAsync(string sessionId, SessionEntry entry, SessionStatus status,
        bool touchUpdatedAt = true) => _store.ApplyStatusAsync(sessionId, entry, status, touchUpdatedAt);

    // Для fire-and-forget задач: ошибку логируем, а не теряем молча. Тело — в DeferredRun
    // (Services.Sessions, S3-0 §3): тот же хелпер нужен и ролям ядра, звать фасад сверху они
    // не могут. Здесь остаётся делегирующая обёртка: места вызова в ядре не переписываются.
    private static void FireAndForget(Task task, string context) => DeferredRun.FireAndForget(task, context);

    // Остановка всех живых адаптеров — вызывается при graceful shutdown приложения,
    // иначе после остановки сервера остаются зомби-процессы (claude + node MCP-серверов).
    // С3-5: само гашение — в TurnProcessHost.KillProcesses. Флаг «штатная остановка» с S3-7
    // живёт в ядре приёма (TurnIntake.Stopping): здесь он поднимается единственным
    // писателем (BeginShutdown) ДО гашения адаптеров и читается телами разбора и цикла
    // как ранняя отсечка; таймер тика ожидания (цикл «до готово») остаётся здесь,
    // порядок тот же, что был.
    public void KillAllProcesses()
    {
        // Первая строка — ДО гашения адаптеров: умирающий процесс присылает ErrorMessage/
        // ExitedMessage, и OnMessageAsync при поднятом признаке гасит их (newStatus = null),
        // не перетирая Working в Error/Active (ADR-033). Новый SaveSessions() здесь НЕ
        // добавляем: его убрали (5c6606282) как источник зависаний остановки (ожидание _saveLock).
        _intake.BeginShutdown();
        // Таймер не должен пытаться писать одновременно с убийством процессов:
        // сценарий — shutdown, SaveSessions() ждёт _saveLock, а в это время адаптеры
        // claude не диспозятся → процессы зависают в памяти (боролись ранее). Тот же
        // сценарий — для тика ожидания: в окне shutdown он бы мог послать директиву
        // в умирающий адаптер, и SendMessageAsync не нашёл бы Process. Дебаунс-таймер
        // MarkRead сбрасываем синхронно (FlushPendingSave) ДО убийства: отложенная запись
        // не теряется, а SaveSessions() завершается на этом же потоке, без гонки с диспозом.
        _store.StopAutoSave();
        _waitingTickTimer?.Dispose();
        FlushPendingSave();
        _process.KillProcesses();
    }

    // IDisposable — для фоновых таймеров. Адаптеры (процессы claude) убивает
    // KillAllProcesses() из ApplicationStopping. Не дублируем — иначе два cleanup-пути.
    // FlushPendingSave — idempotent: если KillAllProcesses уже сбросил запись, здесь no-op.
    public void Dispose()
    {
        _store.StopAutoSave();
        _waitingTickTimer?.Dispose();
        FlushPendingSave();
        GC.SuppressFinalize(this);
    }

    internal Task BroadcastAsync(string sessionId, ServerMessage msg) => _store.BroadcastAsync(sessionId, msg);

    internal Task BroadcastExceptAsync(string sessionId, string exceptConnectionId, ServerMessage msg) =>
        _store.BroadcastExceptAsync(sessionId, exceptConnectionId, msg);

    public Task BroadcastSessionMessageAsync(string sessionId, ServerMessage msg) =>
        _store.BroadcastSessionMessageAsync(sessionId, msg);

    public Session? GetOwned(string sessionId, string ownerId) => _store.GetOwned(sessionId, ownerId);

    public Session? GetOwnedVisible(string sessionId, string userId) => _store.GetOwnedVisible(sessionId, userId);

    // Сессия, на которую у человека есть право right (S1-7): REST-входы хода (RunTurn) проверяют
    // его здесь, а не владением, чтобы открытие права редактору (S2a-10) было строкой матрицы
    // ProjectAccess, а не правкой каждого эндпоинта. Нет доступа — null (404, существование не раскрываем).
    // Композиция в фасаде (S3-1): владение и видимость — SessionStore, право — IProjectAccess;
    // стор IProjectAccess не получает (цикл ProjectAccessService → SessionDirectoryAdapter → SessionManager).
    public Session? GetWithRight(string sessionId, string userId, Access.ProjectRight right)
    {
        var s = _store.GetById(sessionId);
        if (s is null) return null;
        return _services?.GetService<Access.IProjectAccess>() is { } access
            ? access.ResolveSession(userId, sessionId, right) is null ? null : s
            : _store.ResolveOwnerId(s) == userId ? s : null;
    }

    // Чат, который человеку ВИДЕН (владелец проекта, участник общего проекта при флаге и
    // рубильнике, владелец чата вне проекта), вместе с областью запроса. Только для чтения:
    // ключ хранилищ дальше — scope.TenantOwnerId, а не id вызывающего. Запись и ходы остаются
    // на GetOwned (в S1 участник чаты не пишет). Точка доступа берётся из корня DI лениво:
    // прямая зависимость замкнула бы цикл ProjectAccessService → ISessionDirectory → SessionManager.
    public (Session Session, Access.RequestScope Scope)? GetAccessible(string sessionId, string userId)
    {
        var s = _store.GetById(sessionId);
        if (s is null) return null;
        var scope = _services?.GetService<Access.IProjectAccess>() is { } access
            ? access.ResolveSession(userId, sessionId)
            : _store.ResolveOwnerId(s) == userId ? new Access.RequestScope(userId, userId, Access.ProjectRole.Owner) : null;
        return scope is null ? null : (s, scope);
    }

    private Task BroadcastStatusChangeAsync(string sessionId, Session info, SessionStatus status,
        string? lastMessage = null, int messageCount = 0) =>
        _store.BroadcastStatusChangeAsync(sessionId, info, status, lastMessage, messageCount);
}
