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

public class SessionManager : IDisposable, ITeamNotifier, ISessionDirectory,
    ITeamSessionDirectory, ITeamHistoryStore, ITeamRunState, ITeamTurnIntake,
    Access.ITurnActorRegistry, Access.ITurnFrameSource, Sessions.ITurnStarter
{

    // Дальше какой глубины цепочка автоотчётов не идёт. 3 — как у делегирования задач:
    // исполнитель → постановщик → его постановщик, дальше эскалация теряет смысл.
    private const int MaxReportChainDepth = 3;

    // Ожидающее доставки сообщение. Kind: User — сообщение человека из «честной очереди»
    // (доставляется со своими вложениями и режимом, как при обычной отправке); Agent —
    // chats_send/серверные отправки; Report — доклад о завершении делегированной задачи
    // (TaskExecutionService.ReportToDelegatorAsync): ход-реакция постановщика. Report ждёт
    // конца хода, как Agent, НО при активном цикле «до готово» гейт разбора очереди пропускает
    // его СРАЗУ (наравне с User) — иначе цикл жжёт итерации, не видя доклада. SenderOrigin
    // заполняется, только если отправитель из ДРУГОГО места (иной проект / вне проектов) —
    // получателю показываем чип-источник, чтобы было видно, откуда прилетело.
    //
    // Silent — ход-реакция, чей текст уже виден в ленте отдельной репликой (доклад
    // исполнителя): призрак дублировал бы её служебным промптом.
    // SuppressTasksExecute — обязателен для доклада: без него постановщик мог бы
    // самозапустить новую задачу и закольцевать A↔B.
    // SenderChatName — имя чата-отправителя: подпись карточки, когда персоны у него нет
    // («Входящее сообщение» ни о чём не говорит, а имя чата отвечает на вопрос «кто пишет»).
    // StaffNote — служебный ход механики штаба: в ленте рисуется плашкой-разделителем
    // с этой подписью, а не пузырём «Автоматически» (см. StoredUserMessage.StaffNote).
    public record QueuedMessage(
        string Id, string Text, string? SenderPersonaId, string? SenderOrigin,
        int AgentDepth, DateTime EnqueuedAt, bool Silent = false, bool SuppressTasksExecute = false,
        string? SenderChatName = null, PendingKind Kind = PendingKind.Agent,
        IReadOnlyList<string>? AttachedPaths = null, string? Mode = null, string? StaffNote = null,
        string? SenderUserId = null);

    // Вид ожидающего сообщения. Report отделён от Agent: при активном цикле «до готово» Report
    // будит ждущий цикл (как User), а обычные Agent-сообщения посторонних агентов продолжают
    // ждать конца ВСЕГО цикла — иначе координатор сбивался бы посреди итерации. Решение
    // владельца 2026-09-01: посторонний агент не должен сбивать координатора.
    public enum PendingKind { Agent, User, Report }

    // Атрибуция доставленного хода для лога «Доставка хода» (инцидент 2026-08-10 П3): кто
    // инициировал авто-доставку, когда src=auto/origin пустой. Различает точки, прежде бывшие
    // неразличимыми (drain очереди / work-loop / доклад исполнителя / обход байпаса), и pinpoint'ит
    // источник остаточных повторных доставок после закрытия байпаса (П3 п.3).
    public enum DeliveryCause
    {
        User,           // hub — человек через SignalR
        QueueUser,      // fromQueue — пользовательское сообщение из Pending
        QueueAgent,     // fromQueue — агентское сообщение из Pending (в т.ч. обход байпаса)
        Direct,         // auto — прямая отправка (SendOrEnqueueAsync при свободном чате: доклад исполнителя и пр.)
        WorkLoop,       // auto — цикл «до готово» (верификация/продолжение)
        SubagentNudge,  // auto — добивание сабагента, оборвавшегося на середине
        ToolCallLeakNudge, // auto — повтор вызова инструмента, ушедшего в текст (локальная модель)
        ServerRestartResume, // auto — возобновление хода, оборванного рестартом сервера (фича turn-auto-resume)
        HoldResume,     // auto — продолжение хода, прерванного паузой фичи (W4-HOLD-D)
        HoldCard,      // auto — карточка оркестратору в ключевой чат при снятии паузы: сама
                      // нотификация, а не продолжение прерванного хода (W4-HOLD-N, хвост R-N)
        Unknown,        // auto — точка не помечена (диагностика: проставить cause)
    }

    // Снимок пользовательского сообщения, ушедшего в работу, — для возврата в композер
    // по «Стоп», если пользовательских в очереди не оказалось
    public record UserTurnSnapshot(string Text, IReadOnlyList<string> AttachedPaths, string? Mode);

    // Исход постановки пользовательского сообщения (Hub SendMessage возвращает клиенту):
    // Started — ход запущен сразу; Queued — чат занят/очередь непуста, сообщение встало в
    // серверную очередь и уйдёт по FIFO (оптимистичный баллон рисовать не надо — придёт
    // снимок pending_messages, а доставленное вернётся событием user_message);
    // QueuedPreempted — то же, но идущий ход при этом пришлось прервать (ждал человека либо
    // шёл цикл «до готово»). Клиенту это нужно знать: убитый ход пришлёт голый exited, и без
    // отметки о прерывании лента нарисует ложную аварию «AI завершился неожиданно».
    public enum SendUserOutcome { Started, Queued, QueuedPreempted }

    // Стор метаданных сессий (S3-1): реестр, сохранение sessions.json, владелец и видимость,
    // актор хода, наблюдатели, рассылка статуса. Синглтон из DI (AddSessionCore); без DI
    // (юниты) собирается в конструкторе. SessionManager — фасад над ним.
    private readonly SessionStore _store;
    // Тот же реестр, что держит стор (одна точка правды): поле оставлено на фасаде, потому что
    // на него завязаны тела ещё не вынесенных ролей и white-box тесты (рефлексия по «_sessions»).
    private readonly ConcurrentDictionary<string, SessionEntry> _sessions;
    // Сквозной счётчик прогонов адаптера (SessionEntry.RunId): один на процесс сервера —
    // сравниваются только прогоны одной сессии, глобальная уникальность лишь упрощает отладку
    private static long _runSeq;
    private readonly ProjectManager _projects;
    // Готовность устройства локального проекта (ADR-016, вариант А плана §5): фоновые и
    // отложенные доставки в чат офлайн-устройства паркуются в Session.DeviceWaitQueue вместо
    // хода, обречённого на отказ. null — проверки нет (тесты без неё): ход идёт как раньше.
    private readonly Execution.IProjectDeviceGate? _deviceGate;
    // Шов Ф4 (Этап 5): заменяет _hub.Clients.Group(...).SendAsync — префиксы
    // собираются внутри SessionHubBroadcaster, а не в вызывающем коде.
    private readonly Composition.ISessionBroadcaster _broadcaster;
    private readonly Llm.ICheapTextRunner? _cheap;
    // Маршруты мест каталога (локаль/слот/модель) и параметры профилей — для ветки
    // локального голосового хода (chat-voice). null — в тестах без локали.
    private readonly Llm.LocalActionRouter? _router;
    // Прямой HTTP-клиент локальной LLM (Ollama или llama-server) для локальных голосовых
    // ходов; null — в тестах.
    private readonly Llm.ILocalLlmClient? _ollama;
    // Планировщик режима «Командная реализация» (Э2); null — режим без планирования
    private readonly TeamPlanningService? _teamPlanning;
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
    // Платформа внешних модулей: реестр манифестов + выпуск модульных токенов (R7)
    private readonly Modules.ModuleRegistry? _modules;
    private readonly Modules.ModuleTokenService? _moduleTokens;
    private readonly ChatHistoryService _history;
    // Снимки промпта ходов (кнопка «какой промпт ушёл»); null — в тестах
    private readonly PromptSnapshotStore? _promptSnapshots;
    // Паспорта прогонов сабагентов (диагностика обрывов + сигнал для автодобивания); null — в тестах
    private readonly Llm.Claude.SubagentRunLog? _subagentRuns;
    // Паспорта ходов (исход/попытки/подмены), единственный источник записи — шина
    // turn/completed (CLAUDE.md, раздел LLM-провайдеры). null — в тестах.
    private readonly Llm.TurnRunLog? _turnRuns;
    // Шина событий хода (ADR-013): уезжает в LlmSessionContext.Events каждой сессии.
    // null — в тестах без DI: лениво создаётся в TurnEvents, чтобы подписчики могли
    // звать шину и без явной передачи (тесты SessionManagerTests опираются на это).
    private Turn.ITurnEventBus? _turnEvents;
    private Turn.ITurnEventBus TurnEvents => _turnEvents ??= new Turn.TurnEventBus();
    // Корень data/ из конфигурации (тот же резолв, что у sessions.json) — прокидывается
    // в LlmSessionContext.DataDir для встроенного deny {data}/wiki/** (ADR-035 §2).
    private readonly string _dataDir;
    // Лок сохранения sessions.json — один на процесс, живёт в сторе; пометки «зафиксировано в git»
    // (MarkFilesCommitted/UnmarkFileCommitted) делают read-modify-write под тем же замком.
    private Lock _saveLock => _store.SaveLock;
    /// <summary>
    /// Признак штатной остановки: одноразовый, назад не снимается (процесс после остановки
    /// не живёт). KillAllProcesses поднимает его ПЕРВОЙ строкой (до гашения адаптеров).
    /// Умирающий процесс присылает ErrorMessage/ExitedMessage, и OnMessageAsync при поднятом
    /// признаке не переводит по ним статус — sessions.json сохранит Working, и LoadSessions
    /// отберёт сессию в кандидаты на auto-resume (ADR-033). ResultMessage пишется как обычно.
    /// Цикл «до готово», добивание сабагента, разбор очереди и штаб при поднятом признаке
    /// новых ходов не запускают.
    /// </summary>
    private volatile bool _stopping;
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
    // вынесенных ролей вызывают прежние имена. Экземпляры — синглтоны DI (AddSessionCore)
    // или запасная сборка в конструкторе (юниты); один экземпляр на фасад.
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

    // Общий лок внеходовых записей истории (_falPersistLock) переехал в SessionHistory вместе
    // с механикой истории; здесь только делегирующая обёртка: оживление аккумулятора и
    // правка плана (тела, остающиеся в фасаде до S3-5) идут через ТОТ ЖЕ лок, что и
    // публикации цен в TurnCostLedger. Отдельный лок здесь вернул бы гонку lost update.
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

    private readonly ILlmSessionAdapterFactory _adapters;
    private readonly LlmProviderRegistry _llmProviders;
    // FalCostService здесь больше не поле: трекинг и публикация стоимости — в TurnCostLedger
    // (S3-2), он получает falCost в свой конструктор (и ставит OnCostResolved там же).
    private readonly UsageService _usage;
    private readonly ITierModelResolver _appSettings;
    // Резолвер моделей агентных мест: пустая модель → назначение места → слот тира
    private readonly Llm.ModelAssignmentResolver _assignments;
    private readonly UserStore _users;
    private readonly JwtService _jwt;
    private readonly Microsoft.AspNetCore.Hosting.Server.IServer _server;
    private readonly IConfiguration _config;
    // Копии транскриптов заархивированных чатов (data/archived-transcripts) — шаг 0 плана
    // «Архив чатов»: создаётся в конструкторе напрямую, см. там же
    private readonly ArchivedTranscriptStore _archivedTranscripts;
    // Сервисный токен MCP-серверов — ОДИН на владельца (задачи/заметки/память/персоны/…
    // используют один и тот же owner-scoped JWT), с перевыпуском до истечения. См. GetServiceToken.
    private readonly ConcurrentDictionary<string, (string Token, DateTime IssuedAt)> _serviceTokens = new();

    // Зрители сессии и живой прогон CLI — в сторе (S3-1)
    public void AddViewer(string sessionId, string connectionId) => _store.AddViewer(sessionId, connectionId);

    public void RemoveViewer(string sessionId, string connectionId) => _store.RemoveViewer(sessionId, connectionId);

    public void RemoveConnectionViewers(string connectionId) => _store.RemoveConnectionViewers(connectionId);

    public bool HasViewers(string sessionId) => _store.HasViewers(sessionId);

    public bool HasLiveTurnProcess(string sessionId) => _store.HasLiveTurnProcess(sessionId);

    // Наблюдатель сообщений сессий (Claude-исполнитель задач слушает result/permission).
    // Вызывается после обновления статуса и broadcast; его ошибки не роняют пайплайн
    public event Func<Session, ServerMessage, Task>? OnSessionMessage;

    // Текст пользовательского сообщения (ввод в чат) — для push-источников автоматизаций
    // (детекция @упоминаний персон). Вызывается из SendMessageAsync после записи в Accumulator;
    // fire-and-forget, ошибки наблюдателя не роняют ход. (session, text, senderPersonaId)
    public event Func<Session, string, string?, Task>? OnUserMessage;

    // Удаление сессии (чат/проектная сессия) — для авто-движков: сбросить ссылки на чат правила.
    public event Action<Session>? OnSessionDeleted;

    private readonly FeatureFlagService _flags;
    private readonly IServiceProvider? _services;
    private Services.Mcp.Http.McpToolsetRegistry? _mcpToolsets;
    private readonly PersonaManager _personas;
    private readonly PersonaBindingsService _bindings;
    private readonly ClaudeSubscriptionPool _subscriptionPool;
    // Время последней фактической активности аккаунта пула (живой ход/пинг) для идл-пинга
    // подписок (SubscriptionUsageWarmupService); null — в тестах, тогда просто не трогаем.
    private readonly SubscriptionActivityTracker? _activity;
    // Учёт лимитов подписок по rate_limit_event хода: та же точка, что у шлюза LLM. Состояние
    // рекордер не держит (оно в _usage и _subscriptionPool), поэтому свой экземпляр тут не
    // второй счётчик.
    private readonly SubscriptionLimitRecorder _limitRecorder;
    private readonly ILogger<SessionManager> _log;
    // Фабрика логгеров для вертикали (волна В): TeamPlanService получает собственный
    // типизированный ILogger. null — в тестах без DI, TeamPlanService работает на NullLogger.
    private readonly ILoggerFactory? _loggerFactory;
    // Снимок загруженных подсистем: гейт объявления arch_* по факту загрузки модуля
    private readonly Composition.SubsystemStateStore? _subsystemStates;
    // Драйверы среды исполнения владельцев (local / docker-песочница)
    private readonly Execution.ILauncherFactory _launchers;
    private readonly Execution.SandboxManager _sandbox;
    // Домашняя папка владельца ({база по среде}/{username} либо override из конфига)
    private readonly UserHomeResolver _homes;

    private readonly PersonaAgentFileSync? _agentSync;
    // Git-операции worktree чата (null — в тестах: worktree-фича выключена)
    private readonly Git.GitService? _git;
    // Учёт glif-генераций — не поле фасада: он в TurnCostLedger (S3-2), glif идёт в его конструктор.
    // Аналитика расхода токенов (null — в тестах: сбор выключен)
    private readonly Spend.ISpendCollector? _spend;
    // Граф кода, watcher'ы файлов и прогрев сборки worktree (S3-3): поля переехали в
    // ChatWorktree — параметры конструктора остались и передаются в его запасную сборку.
    // Шов «ядро → штаб» (этап 4, шаг 2б плана выноса штаба): четыре крючка уведомления
    // и запрос IsSessionBusy, реализованные этим же классом как обёртка над прежними
    // приватными методами (см. явные реализации ITeamNotifier ниже). В шаге 2г реализация
    // переедет в вертикаль штаба целиком, а сюда будет приходить через DI.
    private readonly ITeamNotifier _teamNotifier;
    // Четыре шва данных «штаб → ядро» (этап 4, шаг 2г-3б; объявления — Services/Team/
    // TeamCoreSeams.cs). Направление обратное ITeamNotifier: здесь вертикаль спрашивает ядро.
    // Реализация — этот же класс явными реализациями ниже (обёртки над прежними приватными
    // методами, поведение один в один). Поля нужны, чтобы штабной блок ходил в ядро уже через
    // контракт: в шаге 2г тело уедет в вертикаль, и вызовы менять не придётся — только
    // источник интерфейсов (DI вместо this). Каталога сессий среди полей нет намеренно: его
    // спрашивает только вертикаль (у тела штаба, пока оно здесь, entry уже на руках).
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
    // Личный реестр MCP-серверов владельца + значения их секретов (null — в тестах:
    // ход идёт только со встроенными серверами и наследством .mcp.json)
    private readonly Mcp.McpRegistry? _mcpRegistry;
    private readonly Mcp.McpSecretStore? _mcpSecrets;
    // Последний известный статус серверов: пишется из system/init каждого хода; null — в тестах
    private readonly Mcp.McpStatusStore? _mcpStatus;
    // OAuth внешних серверов: обновление протухшего токена перед сборкой конфига хода; null — в тестах
    private readonly Mcp.McpOAuthService? _mcpOAuth;
    // OAuth-сервис Higgsfield (инстансное подключение): null — в тестах
    private readonly Mcp.HiggsfieldOAuthService? _higgsfieldOAuth;
    // Секция Dify (ApiUrl/ApiKey/неймспейс) — для BuildDifyContext (волна 4): единственное
    // потребление тут — проверка настроенности и строки stdio-ветки отката; вся работа с
    // Dify — в KnowledgeService со своей копией IOptions
    private readonly Models.DifyOptions _dify = new();
    // Резолв задач (SessionTaskLinks.ParentSessionId / IsTaskDone) живёт в сторе (S3-1):
    // Core-шов ITaskLookup, null — задача не резолвится (ParentSessionId=null, TaskDone=false).
    private ITaskLookup? _taskLookup => _store.TaskLookup;
    // Тест-шов: подменить ITaskLookup. TaskManager в конструктор SessionManager не пробрасывают
    // (DI-цикл), поэтому юниты, которым нужен резолв задачи, подменяют lookup явно.
    internal void SetTaskLookupForTests(ITaskLookup? lookup) => _store.TaskLookup = lookup;

    public SessionManager(ProjectManager projects,
        ChatHistoryService history, IConfiguration config, ILlmSessionAdapterFactory adapters,
        FalCostService falCost, UsageService usage,
        AppSettingsService appSettings, UserStore users, JwtService jwt,
        Microsoft.AspNetCore.Hosting.Server.IServer server,
        LlmProviderRegistry llmProviders,
        FeatureFlagService flags, PersonaManager personas,
        PersonaBindingsService bindings,
        ClaudeSubscriptionPool subscriptionPool,
        ILogger<SessionManager> log,
        Execution.ILauncherFactory launchers,
        Execution.SandboxManager sandbox,
        // Шов Ф4 (Этап 5): ISessionBroadcaster нужен TeamCoordinator — его экземпляр
        // ядро держит в owning-обёртке (см. комментарий TeamCoordinator.cs:14). Сам
        // SessionManager пока сидит на IHubContext<SessionHub> — миграция будет
        // отдельным коммитом (это корневой сервис, см. задачу Ф4).
        Composition.ISessionBroadcaster broadcaster = null!,
        // Опционально (в тестах не передаётся): синк файловых сабагентов-персон
        PersonaAgentFileSync? agentSync = null,
        UserHomeResolver? homes = null,
        // Опционально: «дешёвый» раннер для авто-заголовка чата (локальная модель / claude)
        Llm.ICheapTextRunner? cheap = null,
        // Опционально (в тестах не передаётся): платформа внешних модулей —
        // реестр манифестов + выпуск модульных токенов для их MCP-серверов (R7)
        Modules.ModuleRegistry? modules = null,
        Modules.ModuleTokenService? moduleTokens = null,
        // Опционально (в тестах не передаётся): git-операции worktree чата
        Git.GitService? git = null,
        // Опционально (в тестах не передаётся): сбор расхода токенов (Spend Analytics)
        Spend.ISpendCollector? spend = null,
        // Опционально: резолвер моделей агентных мест (назначения + слоты тиров);
        // без него собирается локально от appSettings — слоты работают и в тестах
        Llm.ModelAssignmentResolver? assignments = null,
        // Опционально (в тестах не передаётся): граф кода и watcher'ы файлов — нужны для уборки
        // за отдельным деревом чата (снимок графа + watcher его файлов), ADR-003
        CodeGraph.CodeGraphService? codeGraphs = null,
        FileWatcherService? fileWatchers = null,
        // Опционально: планирование режима «Командная реализация» (Э2). Без него режим
        // включается, но план не строится — CreateTeamPlanAsync отдаёт причину отказа.
        TeamPlanningService? teamPlanning = null,
        // Опционально (в тестах не передаётся): трекер активности аккаунтов пула подписок
        SubscriptionActivityTracker? activity = null,
        // Опционально: учёт glif-генераций; без него детект glif_cost не работает
        GlifAccountService? glif = null,
        // Опционально (в тестах не передаётся): снимки промпта ходов — кнопка «какой промпт
        // ушёл» под постом. Без него ходы идут как раньше, просто без снимков.
        PromptSnapshotStore? promptSnapshots = null,
        // Опционально (в тестах не передаётся): личный реестр MCP-серверов владельца
        // и значения их секретов — состав серверов хода поверх встроенных
        Mcp.McpRegistry? mcpRegistry = null,
        Mcp.McpSecretStore? mcpSecrets = null,
        // Опционально (в тестах не передаётся): последний известный статус MCP-серверов —
        // наблюдение из system/init каждого хода, фонового поллинга нет
        Mcp.McpStatusStore? mcpStatus = null,
        // Опционально (в тестах не передаётся): OAuth внешних серверов — обновление
        // истекающего токена перед ходом, иначе инструменты сервера получали бы 401
        Mcp.McpOAuthService? mcpOAuth = null,
        // Опционально (в тестах не передаётся): OAuth-сервис Higgsfield (инстансное подключение)
        Mcp.HiggsfieldOAuthService? higgsfieldOAuth = null,
        // Опционально (в тестах не передаётся): паспорта прогонов сабагентов. Без него
        // диагностики обрывов нет и автодобивание молчит — ходы идут как раньше.
        Llm.Claude.SubagentRunLog? subagentRuns = null,
        // Опционально (в тестах не передаётся): паспорта ходов. Без него стор не ведётся,
        // но и контракт «ровно один источник записи» соблюдён — без стора запись не идёт
        // ни в finally фолбэк-адаптера, ни в шинный подписчик (нет подписчика → нет события).
        Llm.TurnRunLog? turnRuns = null,
        // Опционально (в тестах не передаётся): маршрутизатор мест и клиент локальной
        // модели — ветка локального голосового хода (chat-voice). Без них разговор
        // идёт через claude CLI как раньше.
        Llm.LocalActionRouter? router = null,
        Llm.ILocalLlmClient? ollama = null,
        // Опционально (в тестах не передаётся): реестр контрибьюторов секций промпта
        // (этап 2 плана «Шина событий хода»). Без DI бак пуст, шина работает как раньше.
        IEnumerable<Turn.IPromptSectionContributor>? promptSectionContributors = null,
        // Опционально (в тестах не передаётся): шина событий хода (ADR-013). Подписчики
        // SessionManager ведут паспорта ходов/сабагентов и снимки промпта (этап 1).
        Turn.ITurnEventBus? turnEvents = null,
        // Опционально (в тестах не передаётся): реестр задач — единая точка вычисления
        // вычисляемых «связей» сессии (SessionTaskLinks.ParentSessionId / IsTaskDone).
        TaskManager? tasks = null,
        // Опционально (в тестах не передаётся): фабрика логгеров — нужна вертикали
        // TeamPlanService с собственным типизированным логгером (волна В). Без неё
        // TeamPlanService работает на NullLogger.
        ILoggerFactory? loggerFactory = null,
        // Опционально (в тестах не передаётся): готовность устройства локального проекта
        Execution.IProjectDeviceGate? deviceGate = null,
        // Корень DI: реестр MCP-тулсетов резолвится лениво (BuildImageEditorContext) — прямая
        // зависимость дала бы цикл SessionManager → реестр → тулсеты → SessionManager
        IServiceProvider? services = null,
        // Опционально (в тестах не передаётся): снимок загруженных подсистем — факт загрузки
        // динамических модулей (Architecture). Без него сервер arch_* ходу не объявляется.
        Composition.SubsystemStateStore? subsystemStates = null,
        // Опционально (в тестах без сфер не передаётся): справочник сфер для зоны персон; без него
        // персона сферы нигде не видна (fail-closed)
        ISphereDirectory? spheres = null,
        // Опционально (ADR-031, шаг 2.1): шов каталогов плагинов владельца для --plugin-dir.
        // null — подсистема Plugins выключена (IPluginDirSource не в DI): ход идёт без --plugin-dir.
        Plugins.IPluginDirSource? pluginDirSource = null,
        // Опционально (в тестах не передаётся): шов графа кода — прогрев графа свежепривязанного
        // worktree, чтобы первый вызов codegraph_* не упирался в «граф строится»
        CodeGraph.ICodeGraphInspector? graphInspector = null,
        // Стор метаданных сессий (S3-1, AddSessionCore). Опционально: без DI (57 мест new SessionManager
        // в тестах) стор собирается здесь же из тех же зависимостей. Запасная сборка снимается в S3-8c.
        SessionStore? store = null,
        // Опционально (S3-2, AddSessionCore): роли ядра — история, цены, очередь, маршрут CLI.
        // Без DI (юниты, new SessionManager) собираются здесь же из тех же зависимостей;
        // переходный режим S3-0 §4, запасная сборка снимается в S3-8c.
        Sessions.SessionHistory? historyRole = null,
        Sessions.TurnCostLedger? costs = null,
        Sessions.PendingQueueService? pending = null,
        Sessions.CliRoute? route = null,
        // Опционально (S3-2 → S3-3, AddSessionCore): рабочая папка чата и жизненный цикл —
        // worktree, именование, смена провайдера, настройки, видимость. Без DI (юниты,
        // new SessionManager) собираются здесь же из тех же зависимостей; переходный режим
        // S3-0 §4, запасная сборка снимается в S3-8c.
        Sessions.ChatWorktree? worktree = null,
        Sessions.ProviderMigration? providerMigration = null,
        Sessions.ChatNaming? naming = null,
        Sessions.ChatVisibility? visibility = null,
        Sessions.ChatSettings? settings = null,
        // Опционально: учёт лимитов подписок и стор копий транскриптов архивных чатов — теперь
        // ОБЩИЕ экземпляры с ролями (TurnCostLedger / CliRoute), не отдельный счётчик здесь.
        SubscriptionLimitRecorder? limitRecorder = null,
        ArchivedTranscriptStore? archivedTranscripts = null)
    {
        // Стор — первым: на него опираются поле реестра, швы штаба и всё, что ниже читает сессии
        _store = store ?? new SessionStore(config, projects, broadcaster,
            loggerFactory?.CreateLogger<SessionStore>()
                ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<SessionStore>.Instance,
            tasks is null ? null : new Composition.TaskLookupAdapter(tasks));
        _sessions = _store.Sessions;
        _spheres = spheres;
        _pluginDirSource = pluginDirSource;
        _deviceGate = deviceGate;
        _services = services;
        _subsystemStates = subsystemStates;
        _turnEvents = turnEvents;
        _loggerFactory = loggerFactory;
        _subagentRuns = subagentRuns;
        _turnRuns = turnRuns;
        _router = router;
        _ollama = ollama;

        _mcpRegistry = mcpRegistry;
        _mcpSecrets = mcpSecrets;
        _mcpStatus = mcpStatus;
        _mcpOAuth = mcpOAuth;
        _higgsfieldOAuth = higgsfieldOAuth;
        _promptSnapshots = promptSnapshots;
        _teamPlanning = teamPlanning;
        _activity = activity;
        _spend = spend;
        _agentSync = agentSync;
        _cheap = cheap;
        _modules = modules;
        _moduleTokens = moduleTokens;
        _git = git;
        _homes = homes ?? UserHomeResolver.WithoutOverrides(appSettings, sandbox);
        _launchers = launchers;
        _sandbox = sandbox;
        _projects = projects;
        _broadcaster = broadcaster;
        _teamCoordinator = new TeamCoordinator(broadcaster);
        // Хранитель состояния режима (волна А): создаётся ДО _teamNotifier/teamHistory
        // и до LoadSessions, чтобы восстановление состояния режима после рестарта
        // (через публичные обёртки WithTeamState) могло идти через TeamStateService
        // сразу. Owning по тому же шаблону, что _teamCoordinator. Волна Б: в
        // конструктор добавлен LlmProviderRegistry — вертикаль сама проверяет
        // CapabilitiesFor(model).SupportsPlanMode при входе в план-фазу.
        _teamState = new TeamStateService(this, _teamPlanning, _projects, _config, llmProviders);
        // _personas обязаны присвоить ДО создания TeamPlanService — иначе вертикаль
        // получает null в конструкторе (порядок инициализации полей в классе идёт до
        // тела конструктора, а тело выполняется по тексту). На строгом конструкторе
        // это поймал бы компилятор (CS8618), но поле non-null и присваивается ниже,
        // поэтому компилятор верит, а в runtime — null.
        _personas = personas;
        // Волна В: создаётся ПОСЛЕ _teamState, потому что TeamPlanService зовёт публичные
        // обёртки ядра (RestoreUserMode, SaveSessions, BroadcastTeamImplementAsync,
        // ResolveTeamPlanRoot) и видит швы данных через сам SessionManager. Жизненный цикл
        // симметричен _teamCoordinator/TeamStateService — owning в конструкторе, регистрация
        // через DI придёт в шаге 2г-4.
        _teamPlan = new TeamPlanService(this, this, this, _teamPlanning, _teamCoordinator,
            _personas, _projects, _teamState,
            // Опциональный ILoggerFactory (для вертикали TeamPlanService с собственным
            // типизированным логгером). В тестах SessionManagerTests логгер не передаётся —
            // подменяем на null-логгер, чтобы вертикаль могла логировать не падая.
            loggerFactory?.CreateLogger<TeamPlanService>() ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<TeamPlanService>.Instance);
        // Волна Г: координатор решений по карточке плана (запуск работы, закрытие
        // интервью, реакция на карточку). Owning-паттерн (создаётся в конструкторе
        // SessionManager — здесь тот же приём, что у _teamCoordinator/_teamState/_teamPlan).
        // В DI переедет на шаге 2г-4, owning-обёртки StartTeamWorkAsync/CloseTeamTalkAsync/
        // RespondTeamPlanAsync в SessionManager будут сняты.
        _teamDecision = new TeamDecisionService(this, this, this, this, this, _personas, _teamState, _teamPlan,
            loggerFactory?.CreateLogger<TeamDecisionService>() ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<TeamDecisionService>.Instance);
        // Квоты и бюджет практики (волна Е). Owning-паттерн (создаётся в конструкторе,
        // в DI переедет на шаге 2г-4) разрывает цикл «SessionManager хочет TeamBudgetService,
        // TeamBudgetService хочет SessionManager» — вертикаль видит ядро по прямой ссылке,
        // а ядро знает о вертикали через поле _teamBudget.
        _teamBudget = new TeamBudgetService(this, this, this, _teamState,
            loggerFactory?.CreateLogger<TeamBudgetService>() ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<TeamBudgetService>.Instance);
        // Шапка разбора хода штаба и доклад о блокере (волна Ж): owning-паттерн по тому же
        // рецепту, что у _teamDecision/_teamBudget. Подписчик turn/completed переехал в
        // вертикаль — шинная подписка ниже регистрирует делегат на метод TeamTurnCompletionService.
        _teamTurnCompletion = new TeamTurnCompletionService(this, this, this, this, _teamDecision,
            loggerFactory?.CreateLogger<TeamTurnCompletionService>() ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<TeamTurnCompletionService>.Instance,
            tasks);
        // Включение и переключение режима «Командная реализация» (волна Ж): SetTeamImplementAsync
        // и SetTeamImplementAutoAsync. Owning-паттерн по тому же рецепту, что и _teamBudget.
        _teamEnable = new TeamEnableService(this, this, this, this, _teamState,
            loggerFactory?.CreateLogger<TeamEnableService>() ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<TeamEnableService>.Instance);
        _history = history;
        _adapters = adapters;
        _llmProviders = llmProviders;
        _usage = usage;
        _appSettings = appSettings;
        _assignments = assignments ?? new Llm.ModelAssignmentResolver(appSettings);
        _users = users;
        _jwt = jwt;
        _server = server;
        _config = config;
        // Копии транскриптов архивных чатов (шаг 0 плана «Архив чатов»): стор файловый и
        // без зависимостей. Точки вызова (SetArchived/DeleteAsync) живут здесь, а поиск,
        // перенос и удаление — в CliRoute (S3-2), поэтому экземляр общий: без DI собирается
        // здесь, с DI приходит из AddSessionCore тем же синглтоном.
        _archivedTranscripts = archivedTranscripts ?? new ArchivedTranscriptStore(config);
        config.GetSection(Models.DifyOptions.Section).Bind(_dify);
        // Порог свежести хода для гейта перезапуска (этап 3): тот же ключ/дефолт, что у
        // пульса волны — TeamWaveService._quietThreshold
        _freshTurnThreshold = TimeSpan.FromMinutes(
            int.TryParse(config["TeamImplement:QuietMinutes"], out var quietMin) && quietMin > 0 ? quietMin : 15);
        _flags = flags;
        // _personas инициализирован выше (до создания TeamPlanService, чтобы вертикаль
        // не получила null в конструкторе).
        _bindings = bindings;
        _subscriptionPool = subscriptionPool;
        _limitRecorder = limitRecorder ?? new SubscriptionLimitRecorder(_usage, subscriptionPool, _activity);
        _log = log;

        // Роли ядра (S3-2): история, журнал цен, очередь, маршрут CLI. В DI приходят готовыми
        // (AddSessionCore); без DI (юниты, new SessionManager) собираются здесь из тех же
        // зависимостей — переходный режим S3-0 §4, запасная сборка снимается в S3-8c.
        // Порядок важен: конструктор TurnCostLedger ставит FalCostService.OnCostResolved на
        // СВОЮ публикацию, поэтому журнал собирается после того, как параметр falCost дошёл
        // до него (сам falCost полем фасада больше не является); _limitRecorder и
        // _archivedTranscripts — общие экземпляры, они собраны выше. Очередь и маршрут
        // читают стор, он собран первым.
        _sessionHistory = historyRole ?? new Sessions.SessionHistory(
            history, _store,
            loggerFactory?.CreateLogger<Sessions.SessionHistory>()
                ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<Sessions.SessionHistory>.Instance);
        _costs = costs ?? new Sessions.TurnCostLedger(
            falCost, glif, spend, _limitRecorder, _sessionHistory, history, _store,
            loggerFactory?.CreateLogger<Sessions.TurnCostLedger>()
                ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<Sessions.TurnCostLedger>.Instance);
        // Шов «очередь → приём хода» (S3-0 §3): ленивый, на этот же экземляр — при переходе
        // очереди 0→1 очередь просит разбор, а разбор живёт в ядре до S3-4/S3-7.
        _pending = pending ?? new Sessions.PendingQueueService(
            _store, history, _sessionHistory, new Lazy<Sessions.ITurnStarter>(() => this),
            loggerFactory?.CreateLogger<Sessions.PendingQueueService>()
                ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<Sessions.PendingQueueService>.Instance);
        _route = route ?? new Sessions.CliRoute(
            llmProviders, subscriptionPool, _assignments, users, _homes, launchers, sandbox,
            projects, _store, _archivedTranscripts,
            loggerFactory?.CreateLogger<Sessions.CliRoute>()
                ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<Sessions.CliRoute>.Instance);
        // Рабочая папка чата (S3-3): опирается на стор и маршрут (WorktreesBase/CwdForOwner/
        // ConfigRootFor), поэтому собирается после них. Прогрев сборки — его собственный:
        // без DI здесь же (как раньше), с DI приходит из AddSessionCore тем же синглтоном.
        _worktree = worktree ?? new Sessions.ChatWorktree(
            git, codeGraphs, graphInspector, fileWatchers,
            new WorktreeBuildWarmup(launchers, config, log),
            projects, _store, _route,
            loggerFactory?.CreateLogger<Sessions.ChatWorktree>()
                ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<Sessions.ChatWorktree>.Instance);
        // Смена провайдера (S3-3): шов «миграция → штаб» — ленивый на этот же экземляр:
        // прямая зависимость дала бы цикл ProviderMigration → SessionManager → UpdateAsync
        // → ProviderMigration (S3-0 §3); на резолве шва RestoreUserMode идёт через фасад.
        _providerMigration = providerMigration ?? new Sessions.ProviderMigration(
            _store, _route, llmProviders, subscriptionPool,
            new Lazy<Team.ITeamNotifier>(() => this),
            loggerFactory?.CreateLogger<Sessions.ProviderMigration>()
                ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<Sessions.ProviderMigration>.Instance);
        // Именование чата (S3-3): читает ленту через ту же склейку, что фасад GetHistoryAsync
        // (history + досчёт цен), поэтому собирается после журнала цен.
        _naming = naming ?? new Sessions.ChatNaming(
            _store, _sessionHistory, _costs, cheap, config, projects, broadcaster,
            loggerFactory?.CreateLogger<Sessions.ChatNaming>()
                ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<Sessions.ChatNaming>.Instance);
        // Видимость чата (S3-3): шов к привязке чатам фичи — ленивый и опциональный (цикл
        // FeatureChatLinks → ядро через фасад); без DI остаётся null, как прежний null _services:
        // привязка к фиче не проверяется.
        _visibility = visibility ?? new Sessions.ChatVisibility(
            _store, history, projects, broadcaster,
            null,
            loggerFactory?.CreateLogger<Sessions.ChatVisibility>()
                ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<Sessions.ChatVisibility>.Instance);
        // Настройки и жизненный цикл чата (S3-3): опирается на стор, маршрут и роль смены
        // провайдера (UpdateAsync зовёт MigrateProviderAsync), поэтому собирается после них.
        _settings = settings ?? new Sessions.ChatSettings(
            _store, _sessionHistory, _route, _providerMigration, projects, llmProviders,
            _assignments, subscriptionPool, personas, spheres,
            loggerFactory?.CreateLogger<Sessions.ChatSettings>()
                ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<Sessions.ChatSettings>.Instance);

        // Швы данных «штаб → ядро» (ITeamHistoryStore/ITeamRunState/ITeamTurnIntake)
        // назначаем ДО создания TeamPlanService/TeamDecisionService: они передают `this`
        // в конструкторы вертикальных сервисов, и без присваивания поля upcast'ятся в null.
        // Тот же приём, что у _teamNotifier (волна Б), но вынесен выше — вертикальная
        // команда растёт, и швы нужны раньше создания самих вертикальных сервисов.
        _teamHistory = this;
        // Рантайм-поля записи и транзакция над состоянием режима — в сторе (S3-1); вертикаль
        // по-прежнему получает `this` и ходит через явные реализации, которые переадресуют сюда.
        _teamRunState = _store;
        _teamIntake = this;
        // Найденную стоимость fal.ai публикует TurnCostLedger: он ставит
        // FalCostService.OnCostResolved на свою PublishFalCostAsync в своём конструкторе
        // (S3-2). Здесь колбэк не ставим — иначе последний присвоенный затёр бы запись роли.
        // Изменение персоны (профиль/возможности/привязки) — сбрасываем адаптеры её живых
        // сессий, чтобы Tool-рубильники и MCP-серверы перемонтировались со следующего хода
        _personas.OnPersonaChanged += p => InvalidatePersonaSessions(p.Id);

        // Шов «ядро → штаб» смотрит на this через ITeamNotifier. Назначаем ДО LoadSessions,
        // чтобы TrySweepStuckActive (его зовёт SaveSessions) мог читать TeamPlanningInFlight
        // через этот шов, а SaveSessions может сработать из конструктора через
        // Llm.ChatTopicMigration.Apply.
        _teamNotifier = this;

        // Корень берётся через GetFullPath (тот же приём, что у Program.cs и NotesService):
        // относительный DataPath отдал бы в deny-правило CLI относительный корень, а «//…» в
        // синтаксисе правил — абсолютный путь (ревью S1c, Н5).
        var dataDir = Path.GetDirectoryName(
            Path.GetFullPath(config["DataPath"] ?? Path.Combine(AppContext.BaseDirectory, "data", "projects.json")))
            ?? Path.Combine(AppContext.BaseDirectory, "data");
        _dataDir = dataDir;

        // Подписки на шину событий хода (ADR-013, этап 1): снимок промпта → PromptSnapshotStore,
        // паспорт сабагента → SubagentRunLog + side-effects, паспорт хода → TurnRunLog.
        // Шина регистрируется через DI как singleton; в тестах без DI лениво создаётся
        // свой экземпляр на SessionManager (TurnEvents), чтобы подписчики могли работать.
        {
            var bus = TurnEvents;
            bus.OnNotification<PromptAssembled>(HandlePromptAssembled,
                "SessionManager.PromptSnapshotStore");
            bus.OnNotification<SubagentRunCompleted>(HandleSubagentRunCompleted,
                "SessionManager.SubagentRunLog");
            bus.OnNotification<TurnCompleted>(HandleTurnCompleted,
                "SessionManager.TurnRunLog");
            // Этап 4 / шаг 1б: теневой подписчик turn/completed для штаба. ИНЕРТЕН — боевую
            // логику HandleTeamTurnEndAsync НЕ зовёт, только сверяет факт вызова старого пути с
            // тем, что диктует Outcome. Старый путь работает как единственный. Переключение
            // (под-шаг 3) снимет прямой вызов из OnMessageAsync и дедуп SkipNextTeamTurnEnd.
            bus.OnNotification<TurnCompleted>(HandleTeamTurnCompletedShim,
                "SessionManager.TeamShadowSubscriber");
            // Этап 2: реестр IPromptSectionContributor (7 контрибьюторов)
            // подключается к шине Filter-событием prompt/assembling. В тестах без DI бак
            // пуст — сборка секций остаётся на инлайне (он закрыт гейтами IsEnabled).
            if (promptSectionContributors is not null)
            {
                Turn.PromptSectionContributorsRegistration.RegisterAll(bus, promptSectionContributors);
            }
        }

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

    // --- MCP tasks-server ---

    // Базовый URL API для MCP-сервера: среда владельца (из песочницы Kestrel виден
    // как host.docker.internal) → конфиг → адрес Kestrel → дефолт.
    // 0.0.0.0/[::] заменяем на localhost — MCP-сервер ходит с той же машины.
    // Среди адресов Kestrel предпочитаем http: MCP-серверы на node ходят обычным
    // fetch, а боевой серт выписан на внешний домен — по https://localhost они
    // упираются в ERR_TLS_CERT_ALTNAME_INVALID (localhost/127.0.0.1 нет в SAN).
    // Если http-адреса нет вообще, поднимите локальный http-эндпоинт и пропишите
    // McpTasksApiUrl явно — иначе все MCP-прокси (tasks/notes/memory/wsp) отвалятся.
    internal string ResolveTasksApiUrl(string? ownerId = null)
    {
        if (ownerId is not null && _launchers.ForOwner(ownerId).McpApiUrlOverride is { } sandboxUrl)
            return sandboxUrl.TrimEnd('/');

        var fromConfig = _config["McpTasksApiUrl"];
        if (!string.IsNullOrWhiteSpace(fromConfig)) return fromConfig.TrimEnd('/');

        var addresses = _server.Features
            .Get<Microsoft.AspNetCore.Hosting.Server.Features.IServerAddressesFeature>()?
            .Addresses;
        var addr = addresses?.FirstOrDefault(a => a.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
                   ?? addresses?.FirstOrDefault();
        if (string.IsNullOrEmpty(addr)) return "http://localhost:5000";
        return LoopbackApiUrl(addr);
    }

    // Wildcard-хост адреса прослушивания → localhost. Kestrel и хост отдают в
    // IServerAddressesFeature и сырые формы конфига: ASPNETCORE_HTTP_PORTS/URLS дают
    // http://*:порт или http://+:порт, а такой адрес не разбирается в Uri (UriFormatException
    // у шлюза MCP и у всех MCP-конфигов хода).
    private static readonly Regex WildcardHost =
        new(@"^(?<scheme>[a-z][a-z0-9+.-]*://)(?:\*|\+|0\.0\.0\.0|\[::\])(?=[:/]|$)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    internal static string LoopbackApiUrl(string address) =>
        WildcardHost.Replace(address, "${scheme}localhost").TrimEnd('/');

    // tasks-MCP доступен, когда разрешён персоной (Persona.Tools/привязки), ЛИБО сессия
    // является исполнителем задачи — тогда tasks-MCP форсируется: исполнитель обязан
    // управлять задачей через mcp__tasks__* (иначе ограниченная персона не сможет её
    // ни прочитать, ни завершить и свалится в нерабочий встроенный Task-тул).
    // internal — ту же формулу резолвит TasksToolset по живой сессии-вызывателю (http,
    // ADR-012 волна 2): право на сервер проверяется на каждый tools/list и tools/call
    internal bool TasksMcpEnabled(string? ownerId, Session session, Persona? persona) =>
        session.TaskExecution || _bindings.EffectiveToolEnabled(ownerId, persona, "tasks");

    // Единый сервисный токен владельца для MCP-серверов (tasks/notes/memory/personas/…):
    // per-owner JWT с перевыпуском за сутки до истечения (сервер может жить дольше срока токена).
    internal string GetServiceToken(string ownerId) =>
        _serviceTokens.AddOrUpdate(ownerId,
            id => (_jwt.IssueServiceToken(id), DateTime.UtcNow),
            (id, old) => DateTime.UtcNow - old.IssuedAt > JwtService.ServiceTokenLifetime - TimeSpan.FromDays(1)
                ? (_jwt.IssueServiceToken(id), DateTime.UtcNow)
                : old).Token;

    // Контекст MCP-сервера задач для сессии; null — только для чата без владельца.
    // persona — для кросс-проектных ProjectTasks-привязок (доступ к задачам ДРУГИХ проектов).
    // Токен — фабрикой (ADR-012 волна 2, как widgets/memory): захваченный строкой JWT у
    // долгоживущего чата истекал, и задачи пропадали у модели молча.
    private TasksMcpContext? BuildTasksContext(string? ownerId, string? projectId, Persona? persona = null)
    {
        if (ownerId is null) return null;
        var extraScopes = _bindings.BuildExternalTaskScopes(ownerId, persona);
        var extraIds = extraScopes.Select(s => s.ProjectId).Distinct().ToList();
        var extraReadOnly = extraScopes.Where(s => s.ReadOnly).Select(s => s.ProjectId).Distinct().ToList();
        var apiUrl = ResolveTasksApiUrl(ownerId);
        return new TasksMcpContext(apiUrl, () => GetServiceToken(ownerId), projectId,
            extraIds.Count > 0 ? extraIds : null, extraReadOnly.Count > 0 ? extraReadOnly : null,
            UseHttp: HttpEndpointUsable(apiUrl));
    }

    // Контекст MCP-сервера заметок; null — только для чата без владельца.
    // Модуль комментариев к документам и редких операций — за ключом notes-annotations
    // (дефолт выключен, решение ПО ПЕРСОНЕ: PersonaBindingsService.SectionEnabled).
    private NotesMcpContext? BuildNotesContext(string? ownerId, string? projectId, Persona? persona)
    {
        if (ownerId is null) return null;
        var apiUrl = ResolveTasksApiUrl(ownerId);
        return new NotesMcpContext(apiUrl, () => GetServiceToken(ownerId), projectId,
            AnnotationsEnabled: _bindings.SectionEnabled(ownerId, persona, "notes-annotations"),
            UseHttp: HttpEndpointUsable(apiUrl));
    }

    // Контекст MCP-сервера виджетов чата: адрес API и сервисный токен владельца — сервер
    // переехал в Kestrel (ADR-012), ход зовёт его по http, а не поднимает процесс node.
    // Адрес даёт та же ResolveTasksApiUrl, что и остальным серверам: второго правила про
    // песочницу здесь нет — оно уже внутри резолвера.
    // Фича штатная (без фич-флага), как personas/notifications; персона может выключить
    // сервер Off-привязкой tool:widgets (PersonaBindingsService.ServerToolEnabled).
    private WidgetsMcpContext? BuildWidgetsContext(string? ownerId, Persona? persona)
    {
        if (ownerId is null || !_bindings.ServerToolEnabled(ownerId, persona, "widgets")) return null;
        var apiUrl = ResolveTasksApiUrl(ownerId);
        // Токен — фабрикой, как у памяти: контекст живёт столько же, сколько адаптер, а
        // захваченный строкой JWT у долгоживущего чата истекал (ADR-012, урок фазы 1)
        return new WidgetsMcpContext(apiUrl, () => GetServiceToken(ownerId), HttpEndpointUsable(apiUrl));
    }

    // Контекст MCP-сервера сторожей чатов: null — только для чата без владельца.
    // Сессия-вызыватель едет хвостом URL — как у tasks/notes волны 2.
    private WatchMcpContext? BuildWatchContext(string? ownerId)
    {
        if (ownerId is null) return null;
        var apiUrl = ResolveTasksApiUrl(ownerId);
        return new WatchMcpContext(apiUrl, () => GetServiceToken(ownerId!), HttpEndpointUsable(apiUrl));
    }

    // Контекст MCP-сервера веб-поиска: null — чат без владельца ИЛИ пустой Perplexity:ApiKey
    // (единственный рубильник фичи, как у Dify). Ключ читается ЖИВЬЁМ, но остаётся свойством
    // ИНСТАНСА, а не хода: инвариант стабильности состава (ADR-012) не задет — правка ключа
    // штатно меняет сигнатуру запуска и перезапускает CLI, как любое изменение shapes.
    // Сам ключ в контекст не кладётся: наружу он не уезжает, тулсет берёт его из конфига.
    //
    // Третье условие — возможность персоны «web» (EffectiveToolEnabled, та же, что гасит
    // встроенные WebSearch/WebFetch у CLI): персона с выключенным веб-поиском не должна
    // получать обходной путь в интернет через MCP. Гейт стоит на объявлении сервера, а не
    // только в запретах: так у такой персоны схемы ещё и не занимают окно. Персона — свойство
    // сессии, инвариант стабильности состава не задет (как у widgets).
    private WebSearchMcpContext? BuildWebSearchContext(string? ownerId, Persona? persona)
    {
        if (ownerId is null) return null;
        if (string.IsNullOrWhiteSpace(_config["Perplexity:ApiKey"])) return null;
        if (!_bindings.EffectiveToolEnabled(ownerId, persona, "web")) return null;
        var apiUrl = ResolveTasksApiUrl(ownerId);
        return new WebSearchMcpContext(apiUrl, () => GetServiceToken(ownerId!), HttpEndpointUsable(apiUrl));
    }

    // Контекст MCP-сервера архитектуры (arch_*: C4-модель проекта). Все оси — свойства
    // владельца/сессии/процесса, не хода (инвариант стабильности состава ADR-012): чат
    // проекта, модуль Architecture реально загружен (динамическая сборка, Viaduct 10.2: без
    // dll тулсета нет в реестре, и ход объявил бы CLI мёртвый сервер — урок форвардера
    // Knowledge у Notes) и не выключен вторым замком Subsystems:architecture:Enabled, плюс
    // Off-привязка персоны tool:architecture. Фич-флага у раздела нет (решение 2026-09-26):
    // включение — только конфигом модуля. Тулсет перепроверяет то же на каждом вызове.
    private ArchitectureMcpContext? BuildArchitectureContext(string? ownerId, string? projectId, Persona? persona)
    {
        if (ownerId is null || string.IsNullOrEmpty(projectId)) return null;
        // Локальный проект (ADR-016): модели и графа кода на сервере нет — тулсету не с чем работать
        if (_projects.GetById(projectId) is not { } project || !ProjectCapabilities.FilesOnServer(project)) return null;
        if (_subsystemStates?.ActiveKeys().Contains(McpEndpoints.ArchitectureName, StringComparer.OrdinalIgnoreCase) != true) return null;
        if (!Composition.SubsystemGate.IsEnabled(_config, McpEndpoints.ArchitectureName)) return null;
        if (!_bindings.ServerToolEnabled(ownerId, persona, "architecture")) return null;
        var apiUrl = ResolveTasksApiUrl(ownerId);
        return new ArchitectureMcpContext(apiUrl, () => GetServiceToken(ownerId), HttpEndpointUsable(apiUrl));
    }

    // MCP-сервер Higgsfield: инстансное OAuth-подключение (единый вход админа, шарится
    // всеми владельцами). Узел присутствует в конфиге хода только когда:
    //   1) инстанс подключён (EnsureFresh() ≠ null) — иначе прокси некому ретранслировать
    //   2) персона НЕ ReadOnly — RO-гейт (аналог «инстанс не подключён → узла нет»)
    // URL — тот же Kestrel, что и websearch: /mcp/higgsfield/{sessionId}.
    // Токен — сервисный JWT владельца (Bearer), как у websearch/dify/wsp.
    internal HiggsfieldMcpContext? BuildHiggsfieldContext(string? ownerId, Persona? persona)
    {
        if (ownerId is null) return null;
        if (_higgsfieldOAuth is null) return null;
        // RO-гейт: ReadOnly-персона не получает higgsfield (аналог «инстанс не подключён»)
        if (persona is { Access: PersonaAccess.ReadOnly }) return null;
        // Инстанс подключён: EnsureFresh() ≠ null
        if (_higgsfieldOAuth.EnsureFresh() is null) return null;
        var apiUrl = ResolveTasksApiUrl(ownerId);
        return new HiggsfieldMcpContext(apiUrl, () => GetServiceToken(ownerId!), HttpEndpointUsable(apiUrl));
    }

    // MCP-сервер редактора картинок (ADR-019 §4): в любом чате владельца — проектном и личном,
    // при загруженном модуле. Контекст собирается во ВСЕХ трёх точках
    // (StartNewSessionAsync и обе ветки EnsureProcessCoreAsync) — иначе состав прыгает между
    // первым ходом и перезапуском процесса (сторож в McpToolsetStabilityTests). Последнее — наличие тулсета в реестре: модуль выключен, а
    // контекст собран — CLI получил бы сервер, отвечающий 404, и «fetch failed» у всех
    // инструментов хода. Все условия — свойства сессии, владельца и процесса; от хода, фокуса и
    // нитей картинок состав не зависит (McpToolsetStabilityTests).
    internal ImageEditorMcpContext? BuildImageEditorContext(string? ownerId, Session session)
    {
        if (ownerId is null) return null;
        _mcpToolsets ??= _services?.GetService<Services.Mcp.Http.McpToolsetRegistry>();
        if (_mcpToolsets?.Find(McpEndpoints.ImageEditorName) is null) return null;
        var apiUrl = ResolveTasksApiUrl(ownerId);
        return new ImageEditorMcpContext(apiUrl, () => GetServiceToken(ownerId), HttpEndpointUsable(apiUrl),
            ImageEditor.ImageEditorAgentTools.AutoAllowTools);
    }

    // MCP-сервер модуля «Звук» (ADR-021 §5) — по тем же правилам, что image-editor: любой чат владельца,
    // тулсет в реестре (модуль загружен), все три точки сборки контекста. От хода,
    // фокуса, режима и нитей звука состав не зависит (McpToolsetStabilityTests).
    internal AudioEditorMcpContext? BuildAudioEditorContext(string? ownerId, Session session)
    {
        if (ownerId is null) return null;
        _mcpToolsets ??= _services?.GetService<Services.Mcp.Http.McpToolsetRegistry>();
        if (_mcpToolsets?.Find(McpEndpoints.AudioEditorName) is null) return null;
        var apiUrl = ResolveTasksApiUrl(ownerId);
        return new AudioEditorMcpContext(apiUrl, () => GetServiceToken(ownerId), HttpEndpointUsable(apiUrl),
            AudioEditor.AudioEditorAgentTools.AutoAllowTools);
    }

    // MCP-сервер модуля «Видео» (ADR-022 §5) — те же правила, что у «Звука»: любой чат владельца,
    // тулсет в реестре (модуль загружен), все три точки сборки контекста. От хода, фокуса и
    // поставщика состав не зависит (McpToolsetStabilityTests).
    internal VideoEditorMcpContext? BuildVideoEditorContext(string? ownerId, Session session)
    {
        if (ownerId is null) return null;
        _mcpToolsets ??= _services?.GetService<Services.Mcp.Http.McpToolsetRegistry>();
        if (_mcpToolsets?.Find(McpEndpoints.VideoEditorName) is null) return null;
        var apiUrl = ResolveTasksApiUrl(ownerId);
        return new VideoEditorMcpContext(apiUrl, () => GetServiceToken(ownerId), HttpEndpointUsable(apiUrl),
            VideoEditor.VideoEditorAgentTools.AutoAllowTools);
    }

    // MCP-сервер plugins (ADR-031, шаг 4.1): любой чат владельца, тулсет в реестре (подсистема
    // plugins загружена). Ограничения (флаг plugins-build-yourself, локальный проект) — на
    // вызове, не в составе. От хода, фокуса и черновиков состав не зависит (McpToolsetStabilityTests).
    internal PluginsMcpContext? BuildPluginsContext(string? ownerId, Session session)
    {
        if (ownerId is null) return null;
        _mcpToolsets ??= _services?.GetService<Services.Mcp.Http.McpToolsetRegistry>();
        if (_mcpToolsets?.Find(McpEndpoints.PluginsName) is null) return null;
        var apiUrl = ResolveTasksApiUrl(ownerId);
        return new PluginsMcpContext(apiUrl, () => GetServiceToken(ownerId), HttpEndpointUsable(apiUrl));
    }

    // MCP-сервер «Контекст чата» (ADR-023 §3.2) — любой чат владельца, тулсет в реестре, все три точки
    // сборки контекста. От хода и
    // содержимого контекста состав не зависит (McpToolsetStabilityTests).
    internal TurnContextMcpContext? BuildTurnContextContext(string? ownerId, Session session)
    {
        if (ownerId is null) return null;
        _mcpToolsets ??= _services?.GetService<Services.Mcp.Http.McpToolsetRegistry>();
        if (_mcpToolsets?.Find(McpEndpoints.TurnContextName) is null) return null;
        var apiUrl = ResolveTasksApiUrl(ownerId);
        return new TurnContextMcpContext(apiUrl, () => GetServiceToken(ownerId), HttpEndpointUsable(apiUrl),
            Services.ChatContext.TurnContextAgentTools.AutoAllowTools);
    }

    // MCP-сервер локальной генерации (ComfyUI на своей GPU). Узел есть в конфиге хода, только когда:
    //   1) включён машинный тумблер LocalMedia:Enabled и подсистема images (там живёт движок);
    //   2) чат проекта, чьи файлы на сервере: результат пишется в папку проекта
    //      (локальный проект ADR-016 — отказ через ProjectCapabilities);
    //   3) персона НЕ ReadOnly — сервер пишет файлы (тот же RO-гейт, что у higgsfield).
    // Всё это — свойства инстанса, сессии и персоны; тумблер читается живьём, но его поворот
    // штатно меняет сигнатуру запуска, как правка ключа у websearch. Гейты тулсета на вызове
    // повторяют проверки (defense-in-depth).
    internal LocalMediaMcpContext? BuildLocalMediaContext(string? ownerId, string? projectId, Persona? persona)
    {
        if (ownerId is null || projectId is null) return null;
        if (!Images.LocalMedia.LocalMediaOptions.IsEnabled(_config)) return null;
        if (!Composition.SubsystemGate.IsEnabled(_config, "images")) return null;
        if (persona is { Access: PersonaAccess.ReadOnly }) return null;
        if (_projects.GetById(projectId) is not { } project || !ProjectCapabilities.FilesOnServer(project)) return null;
        var apiUrl = ResolveTasksApiUrl(ownerId);
        return new LocalMediaMcpContext(apiUrl, () => GetServiceToken(ownerId), HttpEndpointUsable(apiUrl));
    }

    // MCP-сервер прогона тестов (run_tests). Узел есть в конфиге хода, только когда:
    //   1) включена подсистема test-runs (там живёт движок прогона);
    //   2) чат проекта, чьи файлы на сервере (локальный проект ADR-016 в v1 не поддерживаем:
    //      TRX и лог лежали бы на устройстве — тесты там агент гоняет Bash'ем);
    //   3) персоне не запрещён Bash (ReadOnly или Custom без Bash): прогон исполняет код
    //      проекта и пишет bin/obj и .cc-attachments — это тот же запуск кода, что и Bash.
    // Всё это — свойства сессии, персоны и процесса, не хода. Тулсет на вызове повторяет проверки.
    internal TestsMcpContext? BuildTestsContext(string? ownerId, string? projectId, Persona? persona)
    {
        if (ownerId is null || projectId is null) return null;
        if (!Composition.SubsystemGate.IsEnabled(_config, TestRuns.TestRunsSubsystem.SubsystemKey)) return null;
        if (!PersonaAccessPolicy.AllowsBash(persona)) return null;
        if (_projects.GetById(projectId) is not { } project || !ProjectCapabilities.FilesOnServer(project)) return null;
        var apiUrl = ResolveTasksApiUrl(ownerId);
        return new TestsMcpContext(apiUrl, () => GetServiceToken(ownerId), HttpEndpointUsable(apiUrl));
    }

    // Допускает ли АДРЕС бэкенда http-транспорт (ADR-012) — СХЕМА и форма строки, без
    // рубильника. Не http — значит https: боевой серт выписан на внешний домен, CLI упрётся
    // в ERR_TLS_CERT_ALTNAME_INVALID и спрячет инструмент от модели МОЛЧА, а *.naychenko.me
    // ещё и редиректится на https в пайплайне. В этом случае ход объявляет прежний
    // stdio-сервер, а причина уходит в лог: тихо терять инструмент нельзя. Схема — свойство
    // адреса контекста и в жизни адаптера не меняется; рубильник Mcp:HttpTransport сюда НЕ
    // входит — он живой, читается провайдером на каждый ход (HttpMcpEnabledProvider).
    private string? _httpMcpWarnedFor;
    private bool HttpEndpointUsable(string apiUrl)
    {
        var http = Services.Mcp.Http.McpHttpTransport.Usable(apiUrl, enabled: true);
        // Предупреждаем один раз на адрес: состояние постоянное, ход идёт каждую минуту.
        // Узнаёт и хозяин выключенного рубильника: включённый транспорт этот адрес всё равно
        // не поднимет — инстанс останется на stdio.
        if (!http && Interlocked.Exchange(ref _httpMcpWarnedFor, apiUrl) != apiUrl)
            _log.LogWarning("MCP-over-HTTP невозможен: адрес бэкенда «{Url}» не http — "
                + "продуктовые серверы объявляются ходу по-старому, через stdio", apiUrl);
        return http;
    }

    // Живое значение рубильника Mcp:HttpTransport для провайдера контекста: IConfiguration
    // перечитывается (appsettings.Local.json подключён с reloadOnChange), поэтому поворот
    // ключа доезжает до уже поднятых чатов следующим ходом — без рестарта бэкенда.
    private bool HttpMcpEnabled() =>
        _config.GetValue(Services.Mcp.Http.McpHttpTransport.EnabledKey, true);

    // Сводный признак «у сессии есть продуктовые MCP-серверы, чей адрес допускает http»
    // (ADR-012): от него (вместе с живым рубильником) зависит NO_PROXY хода — обход прокси
    // нужен ЛЮБОМУ http-серверу, а не только виджетам. Решение стоит на едином гейте
    // HttpEndpointUsable (каждый Build*Context уже прогнал через него свой UseHttp) —
    // отдельного условия «виджеты на http» больше нет; во волне 2 к widgets/memory
    // добавились tasks/notes/personas, в волне 3 — wsp/notifications/codegraph, в волне 4 —
    // dify. pmem-консультанты приезжают списком на каждый ход и уточняют признак на стороне
    // ClaudeSession.
    private static bool HttpMcpActive(WidgetsMcpContext? widgets, MemoryMcpContext? memory,
        TasksMcpContext? tasks = null, NotesMcpContext? notes = null, PersonasMcpContext? personas = null,
        WorkspaceMcpContext? workspace = null, NotificationsMcpContext? notifications = null,
        CodeGraphMcpContext? codeGraph = null, DifyMcpContext? dify = null,
        WatchMcpContext? watch = null, WebSearchMcpContext? webSearch = null,
        HiggsfieldMcpContext? higgsfield = null, ImageEditorMcpContext? imageEditor = null,
        LocalMediaMcpContext? localMedia = null, ArchitectureMcpContext? architecture = null,
        AudioEditorMcpContext? audioEditor = null, VideoEditorMcpContext? videoEditor = null,
        TurnContextMcpContext? turnContext = null, TestsMcpContext? tests = null,
        PluginsMcpContext? plugins = null) =>
        architecture is { UseHttp: true } || tests is { UseHttp: true }
        || widgets is { UseHttp: true } || memory is { UseHttp: true }
        || tasks is { UseHttp: true } || notes is { UseHttp: true } || personas is { UseHttp: true }
        || workspace is { UseHttp: true } || notifications is { UseHttp: true }
        || codeGraph is { UseHttp: true } || dify is { UseHttp: true }
        || watch is { UseHttp: true } || webSearch is { UseHttp: true }
        || higgsfield is { UseHttp: true } || imageEditor is { UseHttp: true }
        || localMedia is { UseHttp: true } || audioEditor is { UseHttp: true }
        || videoEditor is { UseHttp: true } || plugins is { UseHttp: true }
        || turnContext is { UseHttp: true };

    // Браузер (плагин playwright): нужен по роли тестировщику, остальным персонам — нет.
    // Ключ-надстройка «browser» с дефолтом по пресету (SectionEnabled → SpecialtySections),
    // как git/kb; чат без персоны получает браузер как раньше — ручную проверку страницы
    // человек делает из своего чата. Решение зависит только от персоны, поэтому постоянно
    // в рамках сессии (оно входит в сигнатуру прогона — см. ClaudeRuntimeSettings).
    // Единственное место чтения Llm:SystemPromptViaFile (нет ключа = файл включён)
    private bool SystemPromptViaFile =>
        !bool.TryParse(_config["Llm:SystemPromptViaFile"], out var viaFile) || viaFile;

    private bool BrowserEnabled(string? ownerId, Persona? persona) =>
        persona is null || _bindings.SectionEnabled(ownerId, persona, "browser");


    // Контекст MCP-сервера баз знаний Dify (ADR-012, волна 4 — последний продуктовый
    // сервер фазы 2). Подключается при настроенной секции Dify (ApiUrl/ApiKey — источник
    // правды ключа с волны 4: внешний dify-узел базового конфига им перекрывается).
    // Проект/дефолтный датасет в контекст не входят: тулсет резолвит их живьём из
    // сессии-вызывателя (датасет появляется у проекта в середине жизни чата).
    // DifyUrl/DifyKey — только stdio-ветке отката (env узла mcp-dify/dist).
    private DifyMcpContext? BuildDifyContext(string? ownerId)
    {
        if (ownerId is null) return null;
        if (string.IsNullOrEmpty(_dify.ApiUrl) || string.IsNullOrEmpty(_dify.ApiKey)) return null;
        var apiUrl = ResolveTasksApiUrl(ownerId);
        return new DifyMcpContext(apiUrl, _dify.ApiUrl.TrimEnd('/'), _dify.ApiKey,
            () => GetServiceToken(ownerId), UseHttp: HttpEndpointUsable(apiUrl));
    }

    // Руки локального проекта (ADR-016 §7) — свойство чата, а не хода: матрица
    // ProjectCapabilities.HandsRefusal (локальный проект, hands у устройства,
    // тумблер проекта). Провайдер хода не сужает ни руки, ни фолбэк (решение владельца
    // 2026-09-27): снимки окон отсекает зрение провайдера в ClaudeSession.
    // Канал устройства резолвится лениво: прямая зависимость замкнула бы граф синглтонов.
    private bool HandsFor(string? projectId)
    {
        if (projectId is null || _projects.GetById(projectId) is not { OwnerId: { } ownerId } project) return false;
        var device = ProjectCapabilities.IsDeviceBound(project)
            ? _services?.GetService<Execution.IDeviceExecChannel>()?.GetStatus(ownerId, project.DeviceId!)
            : null;
        var refusal = ProjectCapabilities.HandsRefusal(project, device, project.HandsEnabled);
        return refusal is null;
    }

    // Работает ли у проекта чата группа «нужен контент на сервере» (ADR-016 §4). Чат вне
    // проекта — да: его папка серверная
    private bool ServerContentFor(string? projectId) =>
        projectId is null || _projects.GetById(projectId) is not { } p || ProjectCapabilities.ServerContentEnabled(p);

    // Лежит ли транскрипт CLI чата на диске сервера (ADR-016). Тело переехало в CliRoute
    // (S3-2): оно нужно поиску, копированию и удалению транскрипта — это маршрут.
    private bool TranscriptOnServer(Session info) => _route.TranscriptOnServer(info);

    // Адрес шлюза LLM для серверного процесса CLI (ADR-016 §2, серверный ход провайдера с
    // NormalizeToolInputArrays) — тот же, что у MCP-серверов хода, у песочницы это мост хоста.
    // У чата проекта, привязанного к устройству, — null: CLI там живёт на устройстве, и шлюз
    // ходу ставит раннер устройства.
    private string? LlmGatewayApiUrlFor(Session info, string? ownerId) =>
        info.ProjectId is not null && _projects.GetById(info.ProjectId) is { } p && ProjectCapabilities.IsDeviceBound(p)
            ? null
            : ResolveTasksApiUrl(ownerId);

    // Контекст MCP-сервера графа кода: инструменты codegraph_* доступны только в чате проекта —
    // граф ключуется проектом (в чате вне проекта искать нечего). Тот же сервисный токен
    // владельца, что у tasks/notes; владение проектом дополнительно проверяет CodeGraphController.
    // rootPath — рабочее дерево сессии (EffectiveRoot): у чата с отдельным worktree свой граф,
    // иначе инструменты смотрели бы в основное дерево, а правки шли в другое (ADR-003).
    // Персона может выключить граф Off-привязкой tool:codegraph — тогда нет ни сервера,
    // ни slice в промпте (CodeGraphContributor).
    private CodeGraphMcpContext? BuildCodeGraphContext(string? ownerId, string? projectId, string sessionId,
        string? rootPath, Persona? persona)
    {
        if (ownerId is null || string.IsNullOrEmpty(projectId)) return null;
        // CodeGraph — группа «контент на сервере»: у локального проекта инструмента нет
        if (!ServerContentFor(projectId)) return null;
        if (!_bindings.ServerToolEnabled(ownerId, persona, "codegraph")) return null;
        var apiUrl = ResolveTasksApiUrl(ownerId);
        return new CodeGraphMcpContext(apiUrl, () => GetServiceToken(ownerId), projectId, sessionId, rootPath,
            UseHttp: HttpEndpointUsable(apiUrl));
    }

    // Контекст MCP-сервера памяти персоны (та же фабрика сервисного токена, что у tasks/notes).
    // projectId — проект ТЕКУЩЕГО чата (③-3.4: даёт доступ к team_memory_* команды), не scope
    // персоны — см. BuildPersonaLayer: любая персона в проектном чате получает эти инструменты,
    // пишет ли она в команду реально — решает бэкенд-гейт (TeamMemoryService.WriteDeniedFor).
    // DossierToolsEnabled — секция dossier_lookup/dossier_get (этап 2, ADR-004 §5): включена
    // всегда (флаг change-dossiers-recall снят). Входит в отпечаток состава сервера (shapes
    // memory) — от СВОЙСТВ ХОДА состав tools/list не зависит (инвариант McpToolsetStabilityTests).
    // UseHttp — сервер памяти переехал в Kestrel (ADR-012, фаза 2): ход зовёт его по http
    // (personaId/projectId едут хвостом URL), процесса node нет; false — откат на stdio.
    private MemoryMcpContext BuildMemoryContext(string ownerId, string personaId, string? projectId)
    {
        var apiUrl = ResolveTasksApiUrl(ownerId);
        return new MemoryMcpContext(apiUrl, () => GetServiceToken(ownerId), personaId, projectId,
            DossierToolsEnabled: true,
            UseHttp: HttpEndpointUsable(apiUrl),
            SphereToolsEnabled: _flags.IsEnabled(ownerId, FeatureFlagKeys.Spheres));
    }

    // Контекст memory-server для проектной сессии БЕЗ персоны: только team_memory_* (③-3.4) —
    // память проекта доступна из ЛЮБОГО чата проекта, не только персонного (personaId пуст →
    // personal-инструменты memory_* не регистрируются, см. mcp/memory-server/index.js).
    private MemoryMcpContext? BuildTeamMemoryContext(string? ownerId, string? projectId) =>
        ownerId is not null && !string.IsNullOrEmpty(projectId)
            ? BuildMemoryContext(ownerId, "", projectId)
            : null;

    // --- Персистентность сессий ---

    // Кандидаты на auto-resume, захваченные LoadSessions: исходный Working (ход оборван,
    // result не пришёл). TakeRestartResumeCandidates возвращает и очищает список (второй
    // вызов — пусто).
    private readonly List<string> _restartResumeCandidates = [];

    // Task записи маркеров обрыва ходов (LoadSessions). Не fire-and-forget: B2 дождётся его
    // перед запуском resume, чтобы маркер не обогнал снимок. Task.CompletedTask — маркеров не было.
    public Task AbortMarkersWritten { get; private set; } = Task.CompletedTask;

    // Загрузка sessions.json — делегируется стору; сборка поставляет колбэк восстановления очереди
    // (RestorePending) и пишет маркеры обрыва ходов в историю (у стора доступа к истории нет).
    private void LoadSessions()
    {
        // Колбэк загрузки — роль очереди: восстановление pending.json при старте сервера
        var loaded = _store.Load(_pending.Restore);
        foreach (var id in loaded.RestartResumeCandidates)
            _restartResumeCandidates.Add(id);
        var abortedMidTurn = loaded.AbortedMidTurn;
        // Маркер обрыва в историю оборванных ходов — фоном, чтобы не тормозить старт.
        // Task храним в AbortMarkersWritten (не fire-and-forget): B2 дождётся его перед
        // запуском resume, чтобы маркер не обогнал снимок.
        if (abortedMidTurn.Count > 0)
            AbortMarkersWritten = Task.Run(async () =>
            {
                foreach (var csid in abortedMidTurn)
                    try { await _history.AppendTurnAbortedAsync(csid); }
                    catch (Exception ex)
                    {
                        Console.Error.WriteLine($"[SessionManager] Маркер обрыва хода ({csid}) не записан: {ex.Message}");
                    }
            });
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
    // сообщения получают плашку «не доставлено». Чат с возобновлённым ходом (Working/Waiting/
    // Starting) не трогаем — очередь разберёт его конец. Идемпотентно: PendingRestored гасится.
    public async Task DrainRestoredPendingAsync()
    {
        foreach (var (sessionId, entry) in _sessions.ToArray())
        {
            try
            {
                var expired = entry.ExpiredOnRestore;
                if (expired is not null)
                {
                    entry.ExpiredOnRestore = null;
                    await AppendPendingExpiredCardAsync(sessionId, entry, expired);
                }

                if (!entry.PendingRestored) continue;
                if (entry.Info.Status is SessionStatus.Working or SessionStatus.Starting or SessionStatus.Waiting)
                    continue;
                entry.PendingRestored = false;
                if (entry.QueueFrozen || !HasPending(entry)) continue;
                await DrainNextPendingAsync(sessionId);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[SessionManager] Автодренаж очереди ({sessionId}) после рестарта: {ex.Message}");
            }
        }
    }

    private async Task AppendPendingExpiredCardAsync(string sessionId, SessionEntry entry, List<QueuedMessage> expired) =>
        await _pending.AppendPendingExpiredCardAsync(sessionId, entry, expired);

    // Выдача кандидатов на auto-resume: возвращает захваченный LoadSessions список и
    // очищает его в памяти, чтобы второй вызов не получил кандидатов повторно.
    public IReadOnlyList<string> TakeRestartResumeCandidates()
    {
        lock (_restartResumeCandidates)
        {
            var result = _restartResumeCandidates.ToList();
            _restartResumeCandidates.Clear();
            return result;
        }
    }

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

    // Подписчики шины событий хода (ADR-013, этап 1): вместо прямых полей sinks в
// LlmSessionContext каждый наблюдатель подписан на событие своего типа. Контракт тот же
// (запись в стор + side-effects на сессии), но заводится ОДИН раз на инстанс SessionManager
// — без привязки к конкретной сессии, потому что id сессии едет В СОБЫТИИ (TurnContext).
//
// Состав:
//
//   PromptAssembled     → PromptSnapshotStore (запись черновика / дозапись tools/mcp)
//                         + McpStatusStore (статусы MCP из system/init, попутно с AttachCliLayer).
//   SubagentRunCompleted → SubagentRunLog (запись паспорта) + side-effects на сессии
//                         (TruncatedSubagent/TruncatedBgNote, сброс счётчика добиваний).
//
//   TurnCompleted       → TurnRunLog (запись паспорта хода; в finally фолбэк-цикла больше
//                         НЕТ прямого вызова Record — это и есть тот самый «ровно один
//                         источник», который задача фиксирует как инвариант).

// Приёмник паспортов сабагента через шину: сюда стекается тестовый код через рефлексию
// (раньше этим путём ходил настоящий sink для ватчера сабагентов; сейчас ватчер публикует
// subagent/completed сам, а этот метод — синхронный мост через шину для unit-тестов,
// которые проверяют side-effects на сессии непосредственно после emit). Синхронный мост
// здесь ОК: подписчик шины пишет в стор и взводит флаги, оба эти действия идемпотентны
// и не могут зациклиться. null — шины нет (тесты без SessionManager), ватчер сам бы
// отказался публиковать.
internal Action<Llm.SubagentRunPassport>? SubagentRunSinkFor(string sessionId)
{
    return passport =>
    {
        // Синхронный мост: тесты берут делегат через рефлексию и зовут его из тестового
        // метода, который потом немедленно проверяет side-effects на entry. В проде никто
        // этим методом не пользуется — ватчер публикует subagent/completed в свой ITurnEventBus.
        TurnEvents.PublishAsync(new SubagentRunCompleted(
            Turn: new TurnContext(SessionId: sessionId, OwnerId: null, TurnSeq: 0,
                AgentDepth: 0, ProjectId: null),
            Passport: passport)).GetAwaiter().GetResult();
    };
}

// Ошибочные ветки возвращают Task.CompletedTask: подписчик Notification не должен
//   TurnCompleted       → TurnRunLog (запись паспорта хода; в finally фолбэк-цикла больше
//                         НЕТ прямого вызова Record — это и есть тот самый «ровно один
//                         источник», который задача фиксирует как инвариант).

private Task HandlePromptAssembled(PromptAssembled e)
{
    var snap = e.Snapshot;
    if (snap is null) return Task.CompletedTask; // событие не наш — большинство подписчиков его не носят
    switch (snap.Phase)
    {
        case PromptSnapshotPhase.Draft:
            if (_promptSnapshots is null || snap.Draft is null) return Task.CompletedTask;
            // ЧЕРНОВИК идёт в стор с id, который ClaudeSession уже сгенерировал и положил
            // в payload — тот же id едет в UI-кнопку «какой промпт ушёл» через _onMessage.
            // Счётчик общий с PromptSnapshotStore.NewPublicId, и без записи в стор id
            // всё равно бесполезен.
            SafePromptSnapshotDraft(e.Turn.SessionId, snap.SnapshotId, snap.Draft);
            break;
        case PromptSnapshotPhase.Tools:
            if ((_promptSnapshots is null && _mcpStatus is null) || snap.SnapshotId is null) return Task.CompletedTask;
            SafePromptSnapshotAttach(e.Turn.SessionId, snap.SnapshotId,
                snap.ToolNames ?? [], snap.McpServers ?? []);
            break;
    }
    return Task.CompletedTask;
}

// Все ошибочные ветки возвращают Task.CompletedTask: подписчик Notification не должен
// бросать наружу — шина гасит исключения, но мы и сами не плодим трейс ради диагностики.
private void SafePromptSnapshotDraft(string sessionId, string? snapshotId, PromptSnapshotDraft draft)
{
    if (snapshotId is null) { _promptSnapshots?.Save(sessionId, draft); return; }
    try { _promptSnapshots?.Save(sessionId, snapshotId, draft); }
    catch (Exception ex) { Console.Error.WriteLine($"[SessionManager] Снимок промпта не записан: {ex.Message}"); }
}

private void SafePromptSnapshotAttach(string sessionId, string snapshotId,
    IReadOnlyList<string> tools, IReadOnlyList<McpServerInfo> servers)
{
    try
    {
        _promptSnapshots?.AttachCliLayer(sessionId, snapshotId, tools, servers);
        if (_mcpStatus is null || servers.Count == 0) return;
        if (_sessions.TryGetValue(sessionId, out var entry)
            && ResolveOwnerId(entry.Info) is { } ownerId)
            _mcpStatus.RecordFromInit(ownerId, sessionId, servers);
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"[SessionManager] Дозапись снимка промпта не удалась: {ex.Message}");
    }
}

private Task HandleSubagentRunCompleted(SubagentRunCompleted e)
{
    var sessionId = e.Turn.SessionId;
    var passport = e.Passport;
    if (_subagentRuns is not null) _subagentRuns.Record(passport);
    if (!_sessions.TryGetValue(sessionId, out var entry)) return Task.CompletedTask;
    if (passport.Truncated && passport.FinishedBy != "interrupted")
    {
        entry.TruncatedSubagent = passport;
        // Фоновый агент: продукт ТОЛЬКО ЧТО объявил его результат готовым посреди хода
        // координатора (bg_agent_done), и координатор принял обрывок последней реплики
        // за итог. Ждать result здесь нельзя: ход координатора не заканчивается, а сам
        // фоновый агент часто дозавершается уже ПОСЛЕ конца хода — тогда отметку не
        // разбирает никто и чат стоит до сообщения человека (ровно то, что видно в логе:
        // у исполнителей задач добивание срабатывало, в обычном чате — ни разу).
        if (passport.FinishedInBackground) NoteTruncatedBgAgent(sessionId, entry, passport);
    }
    else
    {
        // Опровержение обрыва: сигнал bg_agent_done обгоняет дозапись финального отчёта
        // в транскрипт, и пометка могла взвеститься по хвосту tool_use агента, который
        // на деле дописал end_turn. Штатный отчёт гасит ТОЛЬКО СВОЮ пометку — иначе
        // в чат уходит ложная директива добивания давно завершившегося агента, а чужая
        // пометка (другой AgentId) ждёт отчёта своего агента.
        if (RefutesTruncation(entry.TruncatedSubagent?.AgentId, passport.AgentId))
            entry.TruncatedSubagent = null;
        if (RefutesTruncation(entry.TruncatedBgNote?.AgentId, passport.AgentId))
            entry.TruncatedBgNote = null;
        // Агент, доложившийся штатно, снимает счётчик добиваний: потолок в две попытки —
        // на серию подряд, а не на всю жизнь чата. Но снимает ТОЛЬКО СВОЙ счётчик: в ходе
        // работают несколько агентов, и штатный отчёт соседа не значит, что оборвавшегося
        // добили — иначе потолок не достигается никогда (добивание уходит с attempt=1 по кругу).
        if (ResetsNudgeSeries(entry.NudgeAgentId, passport.AgentId))
        {
            entry.SubagentNudges = 0;
            entry.NudgeAgentId = null;
        }
    }
    return Task.CompletedTask;
}

private Task HandleTurnCompleted(TurnCompleted e)
{
    // Запись TurnRunLog — РОВНО ОДИН источник (CLAUDE.md, раздел LLM-провайдеры). До
    // переезда на шину источником был finally-блок FallbackLlmSessionAdapter; теперь
    // подписчик здесь, а finally публикует событие. Подписчик шины не должен бросать,
    // но try всё равно — запись НЕ бросает даже при сбое файла (см. TurnRunLog), try тут
    // для понятного журнала, если в сторе что-то поломается.
    if (e.Passport is null) return Task.CompletedTask;
    try { _turnRuns?.Record(e.Passport); }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"[SessionManager] Паспорт хода не записан ({e.Turn.SessionId}): {ex.Message}");
    }
    return Task.CompletedTask;
}

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

    // Уборка за удалённым деревом чата (ADR-003): тело — в ChatWorktree (S3-3); здесь
    // обёртка, потому что точка вызова (DeleteAsync) ещё живёт в фасаде.
    private void ReleaseWorktreeGraph(string sessionId, string worktreePath)
        => _worktree.ReleaseWorktreeGraph(sessionId, worktreePath);

    // Рабочая папка сессии (для поиска транскрипта по уплощённому cwd);
    // null — папку определить не удалось (миграцию в этом случае не делаем). Тело — в CliRoute (S3-2).
    private string? TryResolveCwd(Session s) => _route.TryResolveCwd(s);

    // Корни профилей CLI для поиска транскрипта чата (тело — в CliRoute, S3-2): тот же список
    // нужен и при ветвлении (поиск самой длинной копии), и при архивации/удалении.
    private IEnumerable<string> TranscriptSearchRoots(Session info) => _route.TranscriptSearchRoots(info);

    // Авто-фейловер пула подписок: аккаунт чата исчерпан, а в пуле есть здоровая
    // альтернатива — тихо перевозим транскрипт в её профиль и меняем Session.Provider.
    // Тело переехало в CliRoute (S3-2): весь оно про профиль CLI и перенос транскрипта.
    private void TryPoolFailover(string sessionId, SessionEntry entry) => _route.TryPoolFailover(sessionId, entry);

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

    // Значения include ветвления чата (docs/research/chat-branching-2026-09.md §5, §6):
    // turn — история по конец хода (кнопка под ответом ассистента), beforePrompt — история
    // до этого сообщения, а его текст возвращается как draft для композера (транскрипт
    // обязан заканчиваться завершённым ходом — висящий промпт склеился бы со следующим).
    public static class ChatBranchInclude
    {
        public const string Turn = "turn";
        public const string BeforePrompt = "beforePrompt";
    }

    // Итог ветвления: новый чат + черновик композера (не null только у include=beforePrompt).
    public sealed record ChatBranchResult(Session Session, string? Draft);

    // Ветвление чата (шаг 3 плана chat-branch, документ-основание §3/§8/§9/§11): новый чат
    // со своим ClaudeSessionId, которому подложены обрезанный транскрипт CLI и обрезанная
    // history.json. Оригинал НЕ изменяется ни одним мутатором (ни SaveSessions по нему, ни
    // UpdatedAt/ArchivedAt/ClaudeSessionId) — источник читается, не трогается.
    //
    // userMessageIndex — 0-based номер элемента StoredUserMessage в истории оригинала
    // (включая служебные ходы auto/staffNote/systemDirective — они тоже user_message).
    // anchorText — фрагмент текста этого сообщения от клиента; сервер сверяет его с
    // найденным по индексу и на расхождении отказывает 409 (индекс — договорённость двух
    // счётчиков, молча резать не там нельзя).
    public async Task<ChatBranchResult> BranchAsync(string sessionId, string ownerId,
        int userMessageIndex, string anchorText, string include, string? name = null)
    {
        // S2d-7b-1: GetOwnedVisible, а не GetOwned — чужой приватный чат не ветвится даже владельцем
        // проекта (не копируем его транскрипт и не плодим чат-преемник с его содержимым)
        if (GetOwnedVisible(sessionId, ownerId) is not { } source
            || !_sessions.TryGetValue(sessionId, out var entry))
            throw new KeyNotFoundException("Чат не найден");

        // §9.1 — идёт ход или живут фоновые агенты (копируем файл, в который пишет CLI)
        if (HasTurnInFlight(entry))
            throw new InvalidOperationException(
                "В чате идёт ход — дождитесь его завершения или прервите его, затем попробуйте снова");
        if (entry.Process is { HasTrackedBg: true })
            throw new InvalidOperationException(
                "В чате работают фоновые агенты — дождитесь их завершения, затем попробуйте снова");

        // Локальный проект: транскрипт-источник на устройстве, копировать его серверу неоткуда
        if (source.ProjectId is { } branchProjectId && _projects.GetById(branchProjectId) is { } branchProject)
            Composition.ProjectCapabilityGuard.EnsureAllowed(branchProject, Composition.ProjectCapabilityArea.Transcript);

        // §9.2 — нет ClaudeSessionId: на экране история есть, в памяти модели — нет
        if (source.ClaudeSessionId is not string csid)
            throw new InvalidOperationException("Чат ещё не обращался к модели: ветвить нечего");

        // §9.6, §9.8 — групповой чат, штаб, отдельное worktree: отказы общие для всех
        // способов породить из чата новый (ветвление, перенос в новый чат)
        EnsureSuccessorAllowed(source, "ветвить", "ветки");

        if (include != ChatBranchInclude.Turn && include != ChatBranchInclude.BeforePrompt)
            throw new InvalidOperationException($"Неизвестное значение include: «{include}»");

        // §5 — адресация шага: тот же номер, что считает клиент по StoredUserMessage истории
        var history = await _history.LoadAsync(csid);
        var userMessages = history.OfType<StoredUserMessage>().ToList();
        if (userMessageIndex < 0 || userMessageIndex >= userMessages.Count)
            throw new InvalidOperationException(
                $"Сообщение с индексом {userMessageIndex} не найдено в истории чата");
        var anchorMessage = userMessages[userMessageIndex];

        // Сверка текста с индексом — fail-closed: живая лента клиента и серверная история
        // расходятся штатно, молча резать не там нельзя (§6 документа-основания)
        var normalizedFound = Llm.TranscriptBrancher.Normalize(anchorMessage.Text);
        var normalizedProvided = Llm.TranscriptBrancher.Normalize(anchorText);
        if (normalizedProvided.Length == 0
            || !normalizedFound.Contains(normalizedProvided, StringComparison.Ordinal))
            throw new InvalidOperationException(
                "Индекс сообщения и присланный текст разошлись — обновите чат и попробуйте снова");

        // §Резолв источника: самая длинная копия среди всех профилей (147 из 487 чатов имеют
        // копии разной длины — короткая дала бы ветку без начала разговора), фолбэк — архив
        // заархивированного чата. §9.4 — не найдено нигде — 400 (переносить нечего ≠ копировать
        // нечего). Оригинал из архива не возвращаем — ArchivedAt не трогаем.
        var searchRoots = TranscriptSearchRoots(source);
        var hostCwd = TryResolveCwd(source);
        var sourceCwd = hostCwd is null ? null : CwdForOwner(ownerId, hostCwd);

        string? srcPath = null;
        var bestLen = -1L;
        foreach (var file in Llm.TranscriptMigrator.FindAllTranscripts(searchRoots, sourceCwd, csid))
        {
            var len = new FileInfo(file).Length;
            if (len > bestLen) { bestLen = len; srcPath = file; }
        }
        srcPath ??= _archivedTranscripts.FindCopyPath(csid);

        if (srcPath is null)
            throw new InvalidOperationException(
                "Память этого разговора уже убрана плановой уборкой CLI");

        // §9.7 — потолок размера, тот же, что у ArchivedTranscriptStore.MaxCopyBytes
        if (new FileInfo(srcPath).Length > _archivedTranscripts.MaxCopyBytes)
            throw new InvalidOperationException(
                "Транскрипт чата слишком большой для ветвления (потолок 512 МБ)");

        // Новый csid — свой, не общий с оригиналом (иначе два чата делили бы один транскрипт
        // и одну историю, и удаление одного унесло бы память другого)
        var newCsid = Guid.NewGuid().ToString();

        // Схема хранения (§3): профиль тот же, что у оригинала; cwd — ВЕТКИ, не оригинала
        // (ветка не наследует WorktreePath — гейт §9.8 гарантирует, что у source его и нет,
        // поэтому cwd ветки = корень проекта/чатов владельца)
        var branchConfigRoot = ConfigRootFor(ownerId, source.Provider);
        var branchRootPath = source.ProjectId is { } sourcePid
            ? (_projects.GetById(sourcePid)?.RootPath
                ?? throw new InvalidOperationException("Проект чата не найден"))
            : ResolveChatRoot(ownerId);
        var branchCwd = CwdForOwner(ownerId, branchRootPath);

        var dstDir = Path.Combine(branchConfigRoot, "projects", Llm.TranscriptMigrator.FlattenCwd(branchCwd));
        Directory.CreateDirectory(dstDir);
        var dstPath = SafePath.Join(dstDir, newCsid + ".jsonl");

        // Якоря для резака: неслужебные сообщения истории до якоря (не включая его) +
        // сам якорь последним (TranscriptBrancher трактует последний элемент как якорь шага)
        var anchors = new List<string>();
        for (var i = 0; i < userMessageIndex; i++)
        {
            var m = userMessages[i];
            if (m.Auto == true || m.SystemDirective == true || m.StaffNote != null || m.ViaAgent == true)
                continue;
            anchors.Add(m.Text);
        }
        anchors.Add(anchorMessage.Text);

        // Точный якорь (шаг 5): uuid хвоста транскрипта, записанный на конце ЯКОРНОГО хода —
        // последний result этого хода, то есть до следующего сообщения пользователя. Есть —
        // резак берёт границу точным сравнением; нет (чат до этого поля) — текстовый путь.
        var anchorHistoryIndex = history.IndexOf(anchorMessage);
        string? anchorUuid = null;
        for (var i = anchorHistoryIndex + 1; i < history.Count; i++)
        {
            if (history[i] is StoredUserMessage) break;
            if (history[i] is StoredResultMessage { TranscriptTailUuid: { } uuid }) anchorUuid = uuid;
        }

        // §9.5 — границу не удалось сопоставить (отказ резака) — 409, резать наугад нельзя.
        // include резак обязан знать: у beforePrompt граница транскрипта — сам якорный промпт
        // (не включая), иначе лента ветки и её транскрипт расходятся — на экране вопроса нет,
        // а в памяти модели и вопрос, и прежний ответ на него.
        // anchorTimestampMs — главный ключ текстового пути резака (ResolveAnchorPrompt):
        // время отправки якорного сообщения, по лагу до записи промпта в транскрипт
        // различаются повторяющиеся тексты («продолжай», тики /loop). null — история до
        // этого поля, там резак работает по цепочке.
        var branchResult = Llm.TranscriptBrancher.Branch(srcPath, anchors, newCsid, dstPath, anchorUuid,
            include == ChatBranchInclude.BeforePrompt
                ? Llm.TranscriptBrancher.BranchInclude.BeforePrompt
                : Llm.TranscriptBrancher.BranchInclude.Turn,
            anchorMessage.Timestamp);
        if (!branchResult.Ok)
        {
            Console.Error.WriteLine($"[SessionManager] Ветвление чата {sessionId} отказано резаком: {branchResult.Reason}");
            throw new InvalidOperationException(
                "Не удалось найти этот шаг в памяти модели; попробуйте ветвиться от другого сообщения");
        }

        // Обрезанная история ветки: turn — по конец хода (следующий user_message или конец
        // файла), beforePrompt — до якорного сообщения (оно не входит, его текст — draft,
        // иначе транскрипт заканчивался бы висящим промптом и следующий ход подклеил бы второй)
        int cutAt;
        string? draft = null;
        if (include == ChatBranchInclude.BeforePrompt)
        {
            cutAt = anchorHistoryIndex;
            draft = anchorMessage.Text;
        }
        else
        {
            // Граница — следующий user_message ИЛИ собственная плашка источника: когда
            // ветвят саму ветку от её последнего хода, чужая плашка «Ветка от …» (§7 — она
            // всегда последняя запись истории) попала бы в копию и встала бы второй рядом
            // со свежей. Унаследованная плашка неверна: у новой ветки источник свой.
            cutAt = history.Count;
            for (var i = anchorHistoryIndex + 1; i < history.Count; i++)
                if (history[i] is StoredUserMessage or StoredBranchedFromMessage) { cutAt = i; break; }
        }

        // Резак отступил назад: хвост последнего хода префикса оказался непарным (прерванный
        // ход, кнопка «Стоп») и фактическая граница транскрипта ушла к НАЧАЛУ этого хода.
        // История обязана совпасть с ней шаг в шаг, иначе в ленте остался бы ход, которого
        // нет в памяти модели. 409 тут не отдаём (продуктовое решение): отказывать за то, что
        // разговор когда-то прервался, хуже, чем перенести незавершённый ввод в черновик —
        // тем же путём, что уже работает у beforePrompt.
        if (branchResult.AnchorTurnExcluded)
        {
            // Сообщение, начавшее исключённый ход: у turn это сам якорь, у beforePrompt —
            // предыдущее сообщение пользователя (якорный ход в ветку и так не входил).
            var droppedIndex = anchorHistoryIndex;
            if (include == ChatBranchInclude.BeforePrompt)
            {
                droppedIndex = -1;
                for (var i = anchorHistoryIndex - 1; i >= 0; i--)
                    if (history[i] is StoredUserMessage) { droppedIndex = i; break; }
                if (droppedIndex < 0)
                    throw new InvalidOperationException(
                        "Не удалось найти этот шаг в памяти модели; попробуйте ветвиться от другого сообщения");
            }
            cutAt = droppedIndex;
            draft = (history[droppedIndex] as StoredUserMessage)?.Text;
        }

        var branchHistory = history.Take(cutAt).ToList();
        // Плашка «Ветка от …» — последней записью, ровно в точке расхождения (§7)
        branchHistory.Add(new StoredBranchedFromMessage
        {
            SourceSessionId = sessionId,
            SourceName = source.Name ?? "Чат",
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        });
        await _history.SaveAsync(newCsid, branchHistory);

        // Имя по умолчанию — «{имя оригинала} (ветка)»; явно переданное имя перебивает
        var branchName = string.IsNullOrWhiteSpace(name)
            ? $"{(string.IsNullOrWhiteSpace(source.Name) ? "Чат" : source.Name)} (ветка)"
            : name.Trim();

        // Создание чата штатной фабрикой через общий хелпер преемника: resumeSessionId = новый
        // csid — StartNewSessionAsync подхватит уже подготовленные транскрипт и историю.
        // Поля, специфичные именно для ветки (снимок cost-state родителя и ссылка на источник),
        // передаются configure.
        var branchSession = await CreateSuccessorChatAsync(source, ownerId, branchName, newCsid,
            b =>
            {
                // Накопленный cost-state CLI: транскрипт ветки — копия префикса родителя (с его
                // cost-state), поэтому --resume ветки восстановит накопленный итог родителя. Базой
                // для дельты первого хода ветки берём снимок родителя, иначе он засчитал бы всю
                // историю родителя. (См. Session.LastCostState / ClaudeSession.ComputeTurnDelta.)
                b.LastCostState = source.LastCostState;
                b.BranchedFromSessionId = sessionId;
            });

        // Подсистемы со своим состоянием по sessionId (нити редактора картинок) копируют его под
        // ветку (ADR-019 §2). Notification: сбой подписчика ветвление не роняет
        await TurnEvents.PublishAsync(new SessionBranched(
            new TurnContext(branchSession.Id, ownerId, 0, 0, branchSession.ProjectId), sessionId));

        return new ChatBranchResult(branchSession, draft);
    }

    // Отказы, общие для всех способов породить из чата новый (ветвление, перенос в новый чат):
    // состояние группового чата (спикеры) и штаба (волны) живёт в Session, а не в разговоре —
    // преемник унаследовал бы ленту без состояния; worktree-чат указывал бы преемника на
    // дерево, которое сносится вместе с чатом-источником. verb — «ветвить»/«переносить»,
    // successor — родительный падеж преемника («ветки»/«нового чата»).
    private static void EnsureSuccessorAllowed(Session source, string verb, string successor)
    {
        if (source.Participants is { Count: > 0 })
            throw new InvalidOperationException(
                $"Групповой чат нельзя {verb}: состав спикеров живёт в чате, а не в транскрипте");
        if (source.TeamImplement is not null)
            throw new InvalidOperationException(
                $"Чат в режиме «Командная реализация» нельзя {verb}: состояние волн живёт в чате, а не в транскрипте");
        if (source.WorktreePath is not null)
            throw new InvalidOperationException(
                $"Чат в отдельном рабочем дереве нельзя {verb}: контекст {successor} указывал бы на дерево, которое сносится вместе с чатом-источником");
    }

    // Новый чат-преемник штатной фабрикой (выбор по PersonaId/ProjectId источника) плюс
    // наследование полей §11 (docs/research/chat-branching-2026-09.md). Фабрики персоны/проекта
    // не берут все поля параметрами (персональная вовсе подставляет модель персоны, а не
    // источника), поэтому поля §11 переписываются на значения источника уже ПОСЛЕ создания.
    // resumeSessionId — csid заранее подложенных транскрипта и истории (ветвление) или null
    // (перенос: чистый разговор, csid выдаст первый ход). configure — поля конкретного способа
    // (у ветки — BranchedFromSessionId и снимок cost-state), сохраняются тем же SaveSessions.
    private async Task<Session> CreateSuccessorChatAsync(Session source, string ownerId, string name,
        string? resumeSessionId, Action<Session>? configure = null)
    {
        // Приватность преемника (S2d-7b-1): содержимое приватного чата не уходит в общий, ветка/перенос
        // остаются приватными у того же автора. Читаем один раз и синхронно — до первого await
        var privateAuthor = source.PrivateToUserId;
        var successor = !string.IsNullOrWhiteSpace(source.PersonaId)
            ? await CreatePersonaChatAsync(ownerId, source.PersonaId!, source.Mode,
                resumeSessionId: resumeSessionId, name: name, contextProjectId: source.ProjectId,
                createdByUserId: privateAuthor, privateToUserId: privateAuthor)
            : source.ProjectId is not null
                ? await CreateAsync(source.ProjectId, source.Mode, resumeSessionId: resumeSessionId,
                    name: name, model: source.Model, effort: source.Effort,
                    createdByUserId: privateAuthor, privateToUserId: privateAuthor)
                : await CreateChatAsync(ownerId, source.Mode, resumeSessionId: resumeSessionId,
                    name: name, model: source.Model, effort: source.Effort);

        if (!_sessions.TryGetValue(successor.Id, out var successorEntry)) return successor;
        var s = successorEntry.Info;
        // Наследуем: Model/Provider/Effort/Context/VoiceMode/VoiceStyle/NotificationsMuted —
        // тот же разговор тем же собеседником; ExcludeFromDossiers — иначе преемник обходит
        // явный opt-out «не сохранять решения» (Session.cs:380-382).
        s.Model = source.Model;
        s.Provider = source.Provider;
        s.Effort = source.Effort;
        s.Context = [.. source.Context.Select(c => new SessionContextEntry
            { Type = c.Type, Id = c.Id, Title = c.Title })];
        s.VoiceMode = source.VoiceMode;
        s.VoiceStyle = source.VoiceStyle;
        s.NotificationsMuted = source.NotificationsMuted;
        s.ExcludeFromDossiers = source.ExcludeFromDossiers;
        // НЕ наследуем: AutoAllowTools (человек переспросит один раз, а не тихая эскалация
        // прав), WorktreePath/WorktreeBranch/TaskId/TaskExecution/ExpiresAfterMinutes/
        // ArchivedAt/ArchiveBatchId/LastReadAt — фабрика их и так не выставляет.
        configure?.Invoke(s);
        SaveSessions();
        return s;
    }

    // Проверка «из чата можно перенести разговор в новый» (холодный путь переноса, шаг B-4
    // фичы про prompt cache) — до вызова модели, чтобы не платить за записку,
    // которую некуда положить. Старый чат только читается.
    public void EnsureHandoffAllowed(Session source) =>
        EnsureSuccessorAllowed(source, "переносить", "нового чата");

    // Почему тёплый перенос (передачу пишет сам чат своим ходом, читая префикс из тёплого
    // кэша) сейчас нельзя; null — можно. Любая причина — тихий уход на холодный путь.
    // Ход передачи не должен ни ждать в очереди, ни прерывать чужое: идущий ход, очередь,
    // живые фоновые агенты. Группа и штаб — ход ушёл бы спикерам/координатору. Архивный чат
    // от хода вернулся бы из архива, а офлайн-устройство припарковало бы ход до своего
    // возвращения.
    public string? WarmHandoffBlocker(string sessionId)
    {
        if (!_sessions.TryGetValue(sessionId, out var entry)) return "чат не найден";
        var s = entry.Info;
        if (s.ClaudeSessionId is null) return "разговора в CLI ещё не было";
        if (s.Status is SessionStatus.Working or SessionStatus.Waiting || HasTurnInFlight(entry))
            return "идёт ход или ждёт очередь";
        if (s.WorkLoop is not null) return "цикл «до готово»";
        if (entry.Process is { } p && (p.HasLiveBgAgents || p.HasTrackedBg)) return "живые фоновые агенты";
        if (s.Participants is { Count: > 0 } || s.TeamImplement is not null) return "групповой чат или штаб";
        if (s.IsArchived) return "архивный чат";
        if (DeviceWaitGate(entry) is not null) return "устройство проекта офлайн";
        return null;
    }

    // Новый чат для переноса: без resume (новый csid выдаст первый ход), с наследованием §11.
    // Источник не трогается — ни UpdatedAt, ни архив.
    public Task<Session> CreateHandoffChatAsync(Session source, string ownerId, string name)
    {
        EnsureHandoffAllowed(source);
        return CreateSuccessorChatAsync(source, ownerId, name, resumeSessionId: null);
    }

    // Имя чата-продолжения: «<имя> · продолжение»; если имя уже кончается на «· продолжение»
    // или «· продолжение N» — счётчик растёт («· продолжение 2», «· продолжение 3»…).
    internal static string ContinuationName(string? name)
    {
        const string Suffix = " · продолжение";
        var baseName = string.IsNullOrWhiteSpace(name) ? "Чат" : name.Trim();
        var m = System.Text.RegularExpressions.Regex.Match(baseName, @"^(?<head>.*) · продолжение(?: (?<n>\d+))?$",
            System.Text.RegularExpressions.RegexOptions.Singleline);
        if (!m.Success) return baseName + Suffix;
        var next = m.Groups["n"].Success && int.TryParse(m.Groups["n"].Value, out var n) ? n + 1 : 2;
        return $"{m.Groups["head"].Value}{Suffix} {next}";
    }

    // «Продолжить в новом чате»: новый чат той же персоны с теми же настройками, но БЕЗ истории
    // и транскрипта — только смысл (записку сервис шлёт первым сообщением). В отличие от
    // BranchAsync не копирует транскрипт CLI, поэтому годится и локальному проекту. Групповой чат и
    // режим «Командная реализация» не поддерживаются: их состояние живёт в Session, а не в записке.
    // Старый чат НЕ архивируется и UpdatedAt не двигается; ссылки ставятся в обе стороны.
    // Занятость чата проверяет вызывающий (ChatContinueService): ход передачи уже завершён.
    public async Task<Session> CreateContinuationAsync(string sessionId, string ownerId,
        string? actorUserId = null)
    {
        // S1: участнику общего проекта отказ (GetOwned → 404): продолжение ушло бы в личное
        // пространство вызывающего вместе с содержимым проекта. S2d-7b-1: чужой приватный чат
        // не продолжается даже владельцем проекта. Видимость считаем по актору (MCP шлёт арендатора
        // ownerId и человека-актора отдельно; REST — один и тот же id), поэтому не GetOwnedVisible
        if (GetOwned(sessionId, ownerId) is not { } source
            || !_sessions.TryGetValue(sessionId, out _)
            || (source.PrivateToUserId is not null && !VisibleTo(source, actorUserId ?? ownerId)))
            throw new KeyNotFoundException("Чат не найден");

        if (source.Participants is { Count: > 0 })
            throw new ChatContinueNotSupportedException(
                "Групповой чат нельзя продолжить в новом: состав спикеров живёт в чате");
        if (source.TeamImplement is not null)
            throw new ChatContinueNotSupportedException(
                "Чат в режиме «Командная реализация» нельзя продолжить в новом: состояние волн живёт в чате");

        var newName = ContinuationName(source.Name);
        // Продолжение приватного чата остаётся приватным у того же автора (S2d-7b-1)
        var privateAuthor = source.PrivateToUserId;
        var created = !string.IsNullOrWhiteSpace(source.PersonaId)
            ? await CreatePersonaChatAsync(ownerId, source.PersonaId!, source.Mode,
                name: newName, contextProjectId: source.ProjectId,
                createdByUserId: privateAuthor, privateToUserId: privateAuthor)
            : source.ProjectId is not null
                ? await CreateAsync(source.ProjectId, source.Mode, name: newName,
                    model: source.Model, effort: source.Effort,
                    createdByUserId: privateAuthor, privateToUserId: privateAuthor)
                : await CreateChatAsync(ownerId, source.Mode, name: newName,
                    model: source.Model, effort: source.Effort);

        if (!_sessions.TryGetValue(created.Id, out var newEntry)) return created;
        var n = newEntry.Info;
        // Те же настройки, что у источника: персональная фабрика подставляет модель персоны, а не
        // выбранную человеком в этом чате, поэтому модель/провайдер/effort переписываются ПОСЛЕ
        n.Model = source.Model;
        n.Provider = source.Provider;
        n.Effort = source.Effort;
        n.Mode = source.Mode;
        n.AgentName = source.AgentName;
        // Разрешённые «всегда» инструменты едут с работой: смысл передачи — не заставлять
        // человека заново проходить карточки разрешений (в отличие от ветки, где это эскалация прав)
        n.AutoAllowTools = [.. source.AutoAllowTools];
        n.Context = [.. source.Context.Select(c => new SessionContextEntry
            { Type = c.Type, Id = c.Id, Title = c.Title })];
        n.VoiceMode = source.VoiceMode;
        n.VoiceStyle = source.VoiceStyle;
        n.NotificationsMuted = source.NotificationsMuted;
        n.ExcludeFromDossiers = source.ExcludeFromDossiers;
        // Рабочее дерево тот же путь, но владеет им по-прежнему источник (WorktreeOwned=false):
        // удаление продолжения не снесёт дерево под старым чатом. Дерево есть только у проектного
        // чата и до первого хода — как в AttachWorktreeAsync; следующий ход пересоздаст адаптер с cwd
        if (source.WorktreePath is not null && n.ProjectId is not null)
        {
            n.WorktreePath = source.WorktreePath;
            n.WorktreeBranch = source.WorktreeBranch;
            n.WorktreeOwned = false;
        }
        // Model/Provider/Effort переписаны ПОСЛЕ создания адаптера (фабрика подставила модель
        // персоны, а не выбранную человеком) — следующий ход обязан пересобрать адаптер с
        // перенесёнными значениями, а не повторить путь персоны. Worktree-ветка тоже сюда:
        // адаптер строится с cwd, не с дерева.
        newEntry.AdapterStale = true;
        n.ContinuedFromSessionId = sessionId;
        source.ContinuedInSessionId = n.Id;
        SaveSessions();

        await TurnEvents.PublishAsync(new SessionContinued(
            new TurnContext(n.Id, ownerId, 0, 0, n.ProjectId), sessionId));
        // Живое событие для владельца: человек, смотрящий исходный чат, перейдёт в новый
        // (фронт решает по открытому чату). Рассылка в session- + project/user-группу
        // исходного чата — тем же каналом, что и прочие уведомления сессии.
        await BroadcastSessionMessageAsync(sessionId, new ChatContinuedMessage(n.Id, n.ProjectId));
        return n;
    }

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

    // Ветка tool_use снятия пометки (тело — в ChatWorktree, S3-3): вызывается из петли
    // сообщений хода, которая ещё живёт здесь.
    private void TryUnmarkCommittedOnToolUse(string sessionId, SessionEntry? entry, string toolName, object? input)
        => _worktree.TryUnmarkCommittedOnToolUse(sessionId, entry, toolName, input);

    public async Task<Session> CreateAsync(string projectId, ClaudeMode mode,
        string? resumeSessionId = null, string? name = null, string? model = null, string? agentName = null,
        string? effort = null, string? personaId = null, bool taskExecution = false, string? taskId = null,
        string? onboardingKind = null, string? createdByUserId = null, string? privateToUserId = null)
    {
        var project = _projects.GetById(projectId)
            ?? throw new KeyNotFoundException($"Проект не найден: {projectId}");

        var defaultModel = ResolveDefaultModel(UsageKeyFor(taskExecution, taskId, personaId),
            model, resumeSessionId, project.OwnerId);
        var session = new Session
        {
            ProjectId = projectId,
            Mode = mode,
            ClaudeSessionId = resumeSessionId,
            Name = name,
            Model = string.IsNullOrWhiteSpace(defaultModel) ? null : defaultModel.Trim(),
            AgentName = string.IsNullOrWhiteSpace(agentName) ? null : agentName.Trim(),
            Provider = ResolveSubscriptionProvider(defaultModel),
            Effort = string.IsNullOrWhiteSpace(effort) ? null : effort.Trim(),
            // Персона-слой подхватится общим механизмом (BuildPersonaLayer).
            // Маршрутизация остаётся по вызывающему коду (задача), не по зоне персоны.
            PersonaId = string.IsNullOrWhiteSpace(personaId) ? null : personaId,
            TaskExecution = taskExecution,
            TaskId = taskId,
            // Онбординг-сессия: задаётся ДО старта — BuildPersonaLayer читает поле при сборке слоя
            OnboardingKind = onboardingKind,
            // Автор чата — кто его создал (S2d-1c): id вызывающего из JWT контроллера. На видимость
            // не влияет (S2d-1); ключом хранилищ по-прежнему служит владелец проекта, не автор.
            CreatedByUserId = string.IsNullOrWhiteSpace(createdByUserId) ? null : createdByUserId.Trim(),
            // Приватность задаётся ДО регистрации сессии (S2d-7b-1): чат-преемник приватного чата не
            // должен ни на миг стать общим — иначе его увидели бы участники проекта
            // Пустая строка — «не видит никто» (fail-closed): не нормализуем её в null
            PrivateToUserId = privateToUserId,
        };

        await StartNewSessionAsync(session, project.RootPath, project.SystemPrompt,
            () => _projects.GetById(projectId)?.PermissionRules ?? (IReadOnlyList<PermissionRule>)Array.Empty<PermissionRule>());
        return session;
    }

    // Создание чата вне проекта: рабочая папка — {домашняя папка владельца}/Chats,
    // системный промпт — только встроенная часть (rawSystemPrompt=null), без проектных правил.
    public async Task<Session> CreateChatAsync(string ownerId, ClaudeMode mode,
        string? resumeSessionId = null, string? name = null, string? model = null, string? effort = null,
        string? personaId = null, bool taskExecution = false, string? taskId = null,
        string? onboardingKind = null)
    {
        var rootPath = ResolveChatRoot(ownerId);

        var defaultModel = ResolveDefaultModel(UsageKeyFor(taskExecution, taskId, personaId),
            model, resumeSessionId, ownerId);
        var session = new Session
        {
            ProjectId = null,
            OwnerId = ownerId,
            Mode = mode,
            ClaudeSessionId = resumeSessionId,
            Name = name,
            Model = string.IsNullOrWhiteSpace(defaultModel) ? null : defaultModel.Trim(),
            Effort = string.IsNullOrWhiteSpace(effort) ? null : effort.Trim(),
            // Персона-слой подхватится общим механизмом (BuildPersonaLayer)
            Provider = ResolveSubscriptionProvider(defaultModel),
            PersonaId = string.IsNullOrWhiteSpace(personaId) ? null : personaId,
            TaskExecution = taskExecution,
            TaskId = taskId,
            // Онбординг-сессия: задаётся ДО старта — BuildPersonaLayer читает поле при сборке слоя
            OnboardingKind = onboardingKind,
        };

        await StartNewSessionAsync(session, rootPath, rawSystemPrompt: null, permissionRules: null);
        return session;
    }

    private readonly ISphereDirectory? _spheres;
    // Шов каталогов плагинов владельца для --plugin-dir (ADR-031, шаг 2.1); null — подсистема
    // Plugins выключена (опциональная зависимость, ход идёт без --plugin-dir).
    private readonly Plugins.IPluginDirSource? _pluginDirSource;

    private ISphereDirectory SphereDir => _spheres ?? NoSphereDirectory.Instance;

    /// <summary>Видна ли персоне работа в проекте (зона Global/Project/Sphere) — единая проверка чатов.</summary>
    public bool PersonaVisibleIn(Persona persona, string? projectId) => _settings.PersonaVisibleIn(persona, projectId);

    /// <summary>Отказ хода, если проект вышел из сферы персоны; null — ход разрешён (см. <see cref="PersonaZone.OutOfZoneRefusal"/>).</summary>
    public string? OutOfZoneRefusal(Persona persona, string? projectId) => _settings.OutOfZoneRefusal(persona, projectId);

    // Отказ 400 (InvalidOperationException), если персона не видна в проекте чата
    private void EnsurePersonaVisible(Persona persona, string? projectId) =>
        _settings.EnsurePersonaVisible(persona, projectId);

    // Создание чата от лица персоны. Маршрутизация по зоне:
    // проектная персона → сессия в её проекте (scope = проект); глобальная (или проект
    // недоступен) → чат вне проекта (scope = все данные владельца). Модель по умолчанию — из персоны.
    // contextProjectId — проект, ИЗ которого зовут глобальную персону («Поговорить» в проекте):
    // чат создаётся в нём, а не вне проекта (как давно позволяет смена собеседника SetPersona).
    public async Task<Session> CreatePersonaChatAsync(string ownerId, string personaId,
        ClaudeMode mode, string? resumeSessionId = null, string? name = null,
        string? contextProjectId = null, string? automationRuleId = null,
        string? createdByUserId = null, string? privateToUserId = null)
    {
        var persona = _personas.Get(personaId, ownerId)
            ?? throw new KeyNotFoundException($"Персона не найдена: {personaId}");

        // Проект сессии: у проектной персоны — её собственный; у глобальной — контекстный
        var targetProjectId = PersonaZone.IsProjectPersona(persona)
            ? persona.ProjectId
            : contextProjectId;
        // Персона сферы без проекта сферы чата не получает: её зона — только проекты сферы
        if (PersonaZone.IsSpherePersona(persona)) EnsurePersonaVisible(persona, targetProjectId);

        // Персона без своей модели идёт своим уровнем, без уровня — назначением места «чат с персоной».
        // Дефолт места (Strong) передаётся в резолв, чтобы ячейка персоны без явного уровня сработала.
        var personaModel = ResolveDefaultModel(Llm.LocalActionCatalog.ChatPersona,
            _assignments.PersonaModel(persona, ownerId,
                Llm.LocalActionCatalog.DefaultTierOf(Llm.LocalActionCatalog.ChatPersona)),
            resumeSessionId, ownerId);

        if (!string.IsNullOrEmpty(targetProjectId)
            && _projects.GetById(targetProjectId) is { } project && project.OwnerId == ownerId)
        {
            // Глобальной персоне чат общего проекта не назначается (доработка R-S2a-8): общая проверка
            // рамки — здесь проект чата известен точно (иначе чат вышел бы вне проекта, где отказа нет).
            EnsurePersonasVisibleInFrame(project.Id, new[] { persona });
            var projectSession = new Session
            {
                ProjectId = project.Id,
                OwnerId = ownerId,
                PersonaId = personaId,
                Mode = mode,
                ClaudeSessionId = resumeSessionId,
                Name = name,
                Model = personaModel,
                Provider = ResolveSubscriptionProvider(personaModel),
                Effort = persona.Effort,
                AutomationRuleId = automationRuleId,
                // Преемник приватного чата (S2d-7b-1): автор и приватность — до регистрации сессии.
                // Чат вне проекта (ветка ниже) приватным не бывает: он и так виден только владельцу
                CreatedByUserId = string.IsNullOrWhiteSpace(createdByUserId) ? null : createdByUserId.Trim(),
                // Пустая строка — «не видит никто» (fail-closed): не нормализуем её в null
                PrivateToUserId = privateToUserId,
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
            Model = personaModel,
            Provider = ResolveSubscriptionProvider(personaModel),
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
        // Проект группового чата — проект ведущей проектной персоны; у остальных чат вне проекта,
        // и персоне сферы в нём не место
        var groupProjectId = _personas.Get(personaIds.FirstOrDefault() ?? "", ownerId) is { } l
            ? PersonaZone.OwnProjectId(l) : null;
        var participants = ValidateParticipants(ownerId, personaIds, groupProjectId);
        var leader = participants[0];
        var participantIds = participants.Select(p => p.Id).ToList();
        // Ведущая без своей модели идёт своим уровнем, без уровня — назначением места «чат с персоной»
        // (дефолт места передаётся в резолв, чтобы ячейка ведущей без уровня сработала).
        var leaderModel = ResolveDefaultModel(Llm.LocalActionCatalog.ChatPersona,
            _assignments.PersonaModel(leader, ownerId,
                Llm.LocalActionCatalog.DefaultTierOf(Llm.LocalActionCatalog.ChatPersona)),
            resumeSessionId: null, ownerId);

        if (PersonaZone.OwnProjectId(leader) is { } leaderProjectId
            && _projects.GetById(leaderProjectId) is { } project && project.OwnerId == ownerId)
        {
            // Групповой чат общего проекта без глобальных участников (доработка R-S2a-8): та же
            // общая проверка рамки, что для собеседника — состав чата и есть его спикеры.
            // Здесь проект чата известен точно (иначе чат создался бы вне проекта, где отказа нет).
            EnsurePersonasVisibleInFrame(project.Id, participants);
            var projectSession = new Session
            {
                ProjectId = project.Id,
                OwnerId = ownerId,
                PersonaId = leader.Id,
                Participants = participantIds,
                Mode = mode,
                Name = name,
                Model = leaderModel,
                Provider = ResolveSubscriptionProvider(leaderModel),
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
            Model = leaderModel,
            Effort = leader.Effort,
            Provider = ResolveSubscriptionProvider(leaderModel),
        };
        await StartNewSessionAsync(session, rootPath, rawSystemPrompt: null, permissionRules: null);
        return session;
    }

    // Обновить состав участников группового чата. Активный спикер сохраняется,
    // если остался в составе, иначе — новая ведущая. Адаптер пересоздаётся
    // (состав участников зашит в подсказку @упоминаний и групповой слой промпта).
    public Session? SetParticipants(string sessionId, string ownerId, IReadOnlyList<string> personaIds) =>
        _settings.SetParticipants(sessionId, ownerId, personaIds);

    // Участники группового чата: 2-8 уникальных персон, все принадлежат владельцу
    private List<Persona> ValidateParticipants(string ownerId, IReadOnlyList<string> personaIds,
        string? projectId = null) =>
        _settings.ValidateParticipants(ownerId, personaIds, projectId);

    // Чаты владельца, ведущиеся от лица конкретной персоны (для раздела «Персоны») — список
    // человеческой ручки (S2d-4a): плюс VisibleTo, чужой приватный чат в нём не живёт
    public IReadOnlyList<Session> GetPersonaChats(string ownerId, string personaId) =>
        _sessions.Values
            .Select(e => e.Info)
            .Where(s => s.PersonaId == personaId && ResolveOwnerId(s) == ownerId && VisibleTo(s, ownerId))
            .OrderByDescending(s => s.UpdatedAt)
            .ToList();

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
    // null — у чата нет персоны (или владельца).
    private Func<Persona?> BuildPersonaProvider(Session session, string? ownerId) =>
        () => session.PersonaId is { } pid && ownerId is not null ? _personas.Get(pid, ownerId) : null;

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

    // Руки — свойство чата (LlmSessionContext.HandsEnabled): тумблер проекта или список
    // провайдеров владельца поменялся — адаптеры его живых чатов пересоздаются при следующем
    // сообщении. Уборка ленивая, как у персоны: активный ход и доживающих агентов не рвём.
    // projectId null — все проектные чаты владельца (правка списка провайдеров).
    public void InvalidateHandsSessions(string ownerId, string? projectId)
    {
        foreach (var entry in _sessions.Values.Where(e => e.Info.ProjectId is not null
                     && (projectId is null ? ResolveOwnerId(e.Info) == ownerId : e.Info.ProjectId == projectId)))
            if (entry.Process is not null) entry.AdapterStale = true;
    }

    // Сброс адаптеров живых сессий персоны (изменился профиль/возможности/привязки):
    // процесс пересоздаётся при следующем сообщении с актуальным контекстом,
    // транскрипт продолжается через --resume (паттерн SetPersona)
    private void InvalidatePersonaSessions(string personaId)
    {
        foreach (var entry in _sessions.Values.Where(e => e.Info.PersonaId == personaId))
            // Ленивая уборка (см. SwitchSpeaker): не рвём активный ход и доживающих агентов
            if (entry.Process is not null) entry.AdapterStale = true;
    }

    // Кандидаты на консультацию: участники группового чата либо доступные в контексте
    // персоны (глобальные + текущего проекта) + кросс-проектные ProjectPersonas-привязки
    // персоны САМОГО ЧАТА (persona) — команда/точечные персоны другого проекта; без персоны
    // самого чата. В групповом чате extra-персоны тоже примешиваются: остаются
    // консультантами через persona_ask (в MentionsHint, не участники/спикеры).
    private List<Persona> ResolveOtherPersonas(string ownerId, string? projectId, Session session, Persona? persona = null)
    {
        var isGroup = session.Participants is { Count: > 1 };
        var result = (isGroup
                ? session.Participants!.Select(id => _personas.Get(id, ownerId)).OfType<Persona>()
                : _personas.GetForContext(ownerId, projectId))
            .Where(p => p.Id != session.PersonaId)
            .ToList();

        if (persona is not null)
        {
            var seen = result.Select(p => p.Id).ToHashSet(StringComparer.Ordinal);
            foreach (var (extProjectId, extPersonaId) in _bindings.BuildExternalPersonaScopes(ownerId, persona))
            {
                IEnumerable<Persona> toAdd;
                if (extPersonaId is not null)
                    toAdd = _personas.Get(extPersonaId, ownerId) is { } single
                        ? new[] { single } : Array.Empty<Persona>();
                else
                    toAdd = _personas.GetByOwner(ownerId).Where(p =>
                        PersonaZone.IsProjectTeam(p, extProjectId));
                foreach (var p in toAdd)
                {
                    if (p.Id == session.PersonaId || !seen.Add(p.Id)) continue;
                    result.Add(p);
                }
            }
        }
        return result;
    }

    // Разделение персон по способу консультации: с файлом сабагента — через встроенный
    // Task; остальные (зарезервированный handle, за капом файлов) — через persona_ask.
    // Провайдер модели персоны роли НЕ играет: файлы сабагентов пинят максимум алиас-тир
    // Claude (opus/sonnet/haiku, см. PersonaAgentFileSync.ModelAliasFor) — он резолвится
    // у любого провайдера, кросс-провайдерная персона остаётся запускаемой.
    private (List<Persona> Subagents, List<Persona> ViaAsk) SplitConsultants(
        string ownerId, Session session, List<Persona> personas)
    {
        if (_agentSync is null)
            return ([], personas);
        var eligible = _agentSync.EligiblePersonas(ownerId).Select(p => p.Id)
            .ToHashSet(StringComparer.Ordinal);
        var subagents = new List<Persona>();
        var viaAsk = new List<Persona>();
        foreach (var p in personas)
        {
            // Файл сабагента персоны физически лежит в ЭТОМ проекте (Project-персона своего
            // проекта) либо во всех проектах (Global) — см. PersonaAgentFileSync. Персона
            // чужого проекта (видна здесь только через ProjectPersonas-привязку) файла тут не
            // имеет — Task(agentType=handle) её не найдёт, консультация только через persona_ask.
            var reachable = p.Scope == PersonaScope.Global || p.ProjectId == session.ProjectId;
            if (reachable && eligible.Contains(p.Id) && !PersonaAgentFileSync.IsReserved(p.Handle))
                subagents.Add(p);
            else
                viaAsk.Add(p);
        }
        return (subagents, viaAsk);
    }

    // Решение «даём ли персоне консультантов» (сабагенты .md + их pmem-серверы + подсказка
    // с persona_ask): ключ tool:consultants. В ГРУППОВОМ чате ключ ИГНОРИРУЕТСЯ — спикер
    // обязан уметь спросить коллег по чату (BuildGroupChatHint прямо отсылает к этому блоку),
    // иначе групповой чат ломается по замыслу. Решение зависит только от персоны и состава
    // чата — детерминировано на сессию, состав tools/list от хода не зависит.
    // internal: ту же формулу резолвит PersonasToolset по живой сессии-вызывателю (http,
    // ADR-012 волна 2) — право на сервер проверяется на каждый tools/list и tools/call,
    // а не только при построении контекста адаптера
    internal bool ConsultantsEnabled(string? ownerId, Session session, Persona? persona) =>
        session.Participants is { Count: > 1 }
        || _bindings.ServerToolEnabled(ownerId, persona, "consultants");

    // Решение «даём ли персоне сервер персон» (CRUD + persona_ask): ключ tool:personas.
    // В ГРУППОВОМ чате ключ ИГНОРИРУЕТСЯ по той же причине, что и tool:consultants —
    // BuildGroupChatHint безусловно отсылает к блоку о консультациях (MentionsHint из
    // этого же сервера), Off-привязка сняла бы сервер и подсказка стала бы враньём.
    // internal — по той же причине, что ConsultantsEnabled: PersonasToolset проверяет
    // право на сервер персон по живой сессии на каждый вызов (http, ADR-012 волна 2)
    internal bool PersonasEnabled(string? ownerId, Session session, Persona? persona) =>
        session.Participants is { Count: > 1 }
        || _bindings.ServerToolEnabled(ownerId, persona, "personas");

    // Решение «в составе ли mentions-группа (persona_ask)» — ЕДИНАЯ формула для tools/list
    // тулсета (живой резолв по сессии-вызывателю) и отпечатка сигнатуры запуска CLI (shape
    // через PersonasMcpContext.MentionsToolsEnabled). Две формулы расходились при единственной
    // персоне владельца: MentionsHint обнулялся (спрашивать некого), а инструмент оставался —
    // shape говорил m0, tools/list отдавал persona_ask, и любой из переходов бил по запуску
    // (блокер приёмки волны 2.1). НЕ «MentionsHint != null»: подсказка — про текст промпта,
    // инструмент — про состав, у них разные условия.
    internal bool MentionsToolsEnabled(string? ownerId, Session session, Persona? persona) =>
        PersonasEnabled(ownerId, session, persona) && ConsultantsEnabled(ownerId, session, persona);

    // План файловых сабагентов-персон на ход: папки для --add-dir + pmem-серверы памяти
    // видимых персон. Замыкание вычисляется на каждый ход (актуальные персоны и модель
    // сессии); внутри — троттлёный reconcile файлов. Ошибки → null (ход идёт без
    // консультантов, persona_ask остаётся).
    private Func<PersonaAgentsContext?>? BuildPersonaAgentsProvider(string? ownerId, Session session, Persona? persona)
    {
        if (ownerId is null || _agentSync is null) return null;
        // Off-привязка tool:consultants убирает и pmem-серверы, и --add-dir с .md-агентами
        // (подсказка про Workflow отпадает следом — она условна от AgentHandles)
        if (!ConsultantsEnabled(ownerId, session, persona)) return null;
        return () =>
        {
            try
            {
                var projectId = session.ProjectId;
                var addDirs = _agentSync.GetAddDirs(ownerId, session.Model, projectId);
                // pmem — для ВСЕХ видимых персон с памятью (включая персону самого чата):
                // файлы в add-dir видны все, а Task(agentType=handle) может позвать любую.
                // С stdio объявление стоило процессом node на каждого консультанта (CLI
                // поднимает все stdio-серверы конфига на старте, alwaysLoad на это не
                // влияет — см. docs/architecture/mcp-servers.md); на http-транспорте
                // (ADR-012, фаза 2) все pmem_* живут в Kestrel одним тулсетом — процессов
                // нет, сколько бы персон ни было смонтировано. Сузишь список — сузишь и
                // круг персон, которых можно позвать сабагентом.
                var (subagents, _) = SplitConsultants(ownerId, session,
                    _personas.GetForContext(ownerId, projectId).ToList());
                var apiUrl = ResolveTasksApiUrl(ownerId);
                var tokenFactory = () => GetServiceToken(ownerId);
                var useHttp = HttpEndpointUsable(apiUrl);
                // ProjectId консультанта — проект ТЕКУЩЕГО чата (как у BuildPersonaLayer выше), не
                // scope самого консультанта: приглашённая в проектный workflow глобальная персона
                // тоже должна видеть team_memory_list/search этого проекта (read-only — пишет только
                // персона САМОГО проекта, гейт в TeamMemoryService.WriteDeniedFor).
                var servers = subagents
                    .Where(p => p.MemoryEnabled)
                    .Select(p => new ConsultantMemoryServer(
                        PersonaConsultantToolset.PmemServerKey(p.Handle),
                        apiUrl, tokenFactory, p.Id, projectId, useHttp))
                    .ToList();
                var handles = subagents.Select(p => p.Handle).Where(h => !string.IsNullOrWhiteSpace(h)).ToList()!;
                // Определения для per-run --agents JSON: omitClaudeMd ClaudeSession дополнит
                // по факту BareMode (свойство прогона, известное только там)
                var agents = _agentSync.GetAgentsDefs(subagents, omitClaudeMd: false);
                return new PersonaAgentsContext(addDirs, servers, handles, agents);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "План сабагентов-персон не собрался — ход без файловых консультантов");
                return null;
            }
        };
    }

    // Блок-подсказка о консультациях: две группы — сабагенты (Task) и persona_ask
    internal string? BuildMentionsHint(string ownerId, Session session, List<Persona> others)
    {
        if (others.Count == 0) return null;
        var (subagents, viaAsk) = SplitConsultants(ownerId, session, others);
        var sb = new System.Text.StringBuilder();
        if (subagents.Count > 0)
        {
            sb.AppendLine("Персоны-консультанты доступны как сабагенты: вызывай встроенный инструмент " +
                "Task с subagent_type=\"<handle>\" и самодостаточным вопросом в prompt — персона не видит " +
                "этот разговор; она сама прочитает нужные файлы, заметки и свою память и ответит от " +
                "своего лица. Не вызывай сабагента со своим собственным handle — отвечай сам. " +
                "Инструменты mcp__pmem_* НЕ вызывай напрямую — это личная память персон-консультантов, " +
                "она доступна только им самим; чтобы узнать, что персона помнит, спроси её через Task. " +
                "Когда пользователь упоминает персону через @handle — обязательно обратись к ней " +
                "и учти её ответ. В мультиагентных обсуждениях (workflow /panel-of-experts) по умолчанию " +
                "распределяй роли панели между этими персонами: передавай их handle в args.participants " +
                "(роли по порядку: Генератор, Критик, Адвокат, Модератор), подбирая персону под роль " +
                "по её характеру. Доступные консультанты:");
            AppendPersonaLines(sb, subagents, session.ProjectId);
        }
        if (viaAsk.Count > 0)
        {
            if (subagents.Count > 0)
                sb.AppendLine("Эти персоны доступны только инструментом persona_ask (параметры: handle, " +
                    "question, context?) — ответ придёт от их лица, с их характером и памятью:");
            else
                sb.AppendLine("Любую персону можно спросить инструментом persona_ask (параметры: handle, " +
                    "question, context?) — она ответит от своего лица, со своим характером и памятью. " +
                    "Когда пользователь упоминает персону через @handle — обязательно обратись к ней " +
                    "через persona_ask и учти её ответ. Вопрос формулируй самодостаточно: персона не " +
                    "видит этот разговор. Если вызов вернул «No such tool available» — сервер персон ещё " +
                    "подключается: подожди мгновение и повтори тот же вызов. Доступные собеседники:");
            AppendPersonaLines(sb, viaAsk, session.ProjectId);
        }
        return sb.ToString().TrimEnd();
    }

    // currentProjectId — ProjectId сессии: персона Project-скоупа ДРУГОГО проекта (видна здесь
    // только через кросс-проектную ProjectPersonas-привязку) помечается «[проект «Имя»]», чтобы
    // не путать тёзок из разных команд.
    private void AppendPersonaLines(System.Text.StringBuilder sb, List<Persona> personas, string? currentProjectId)
    {
        // Персоны сферы — отдельной группой «Команда сферы»: в одном списке с проектными и
        // глобальными их не отличить
        var ordered = personas.Where(p => !PersonaZone.IsSpherePersona(p))
            .Concat(personas.Where(PersonaZone.IsSpherePersona)).ToList();
        var sphereHeaderDone = false;
        foreach (var p in ordered)
        {
            if (PersonaZone.IsSpherePersona(p) && !sphereHeaderDone)
            {
                sphereHeaderDone = true;
                var sphereName = p.OwnerId is not null && p.SphereId is not null
                    ? SphereDir.SphereName(p.OwnerId, p.SphereId) : null;
                sb.AppendLine(sphereName is null ? "Команда сферы:" : $"Команда сферы «{sphereName}»:");
            }
            var title = string.IsNullOrWhiteSpace(p.Role) ? p.Name : $"{p.Role} ({p.Name})";
            sb.Append($"- @{p.Handle} — {title}");
            if (PersonaZone.IsProjectPersona(p) && p.ProjectId != currentProjectId
                && _projects.GetById(p.ProjectId!) is { } foreignProject)
                sb.Append($" [проект «{foreignProject.Name}»]");
            if (!string.IsNullOrWhiteSpace(p.Description)) sb.Append($": {p.Description.Trim()}");
            sb.AppendLine();
        }
    }

    // Контекст MCP-сервера персон: CRUD персон из любого чата (за флагом personas);
    // при включённом persona-mentions и наличии других персон в контексте — плюс
    // @упоминания: MentionsHint (блок «@handle — Роль (Имя)» для промпта) и persona_ask.
    // В групповом чате mentions-режим (persona_ask + подсказка по УЧАСТНИКАМ) включён
    // всегда, независимо от флага persona-mentions — иначе спикер не сможет спросить коллег.
    // persona — для кросс-проектных ProjectPersonas-привязок (доступ к команде ДРУГОГО проекта).
    private PersonasMcpContext? BuildPersonasContext(string? ownerId, string? projectId, Session session, Persona? persona = null)
    {
        if (ownerId is null) return null;
        // Off-привязка tool:personas снимает сервер персон целиком (вместе с CRUD и persona_ask);
        // в групповом чате исключение — см. PersonasEnabled
        if (!PersonasEnabled(ownerId, session, persona)) return null;

        var selfPersonaId = session.PersonaId;
        // @упоминания (persona_ask + подсказка) включены всегда, кроме Off-привязки
        // tool:consultants: без консультаций и подсказки не нужно, и persona_ask
        // выключается вместе с ней (PERSONAS_MENTIONS собирается из MentionsHint).
        var mentionsHint = ConsultantsEnabled(ownerId, session, persona)
            ? BuildMentionsHint(ownerId, session, ResolveOtherPersonas(ownerId, projectId, session, persona))
            : null;

        var externalPersonaScopes = _bindings.BuildExternalPersonaScopes(ownerId, persona);
        var extraProjectIds = externalPersonaScopes.Where(s => s.PersonaId is null)
            .Select(s => s.ProjectId).Distinct().ToList();
        var extraPersonaIds = externalPersonaScopes.Where(s => s.PersonaId is not null)
            .Select(s => s.PersonaId!).Distinct().ToList();

        // Модули сервера персон: manage (CRUD персон) и automation (правила проактивности) —
        // за своими tool-ключами с дефолтом по роли (SectionEnabled → SpecialtySections).
        // Ядро (personas_list/get, привязки, persona_ask) остаётся у всех, у кого сервер включён.
        // Проектный онбординг (знакомство v2, п.5) форсирует manage: шаг команды зовёт
        // personas_ai_team/personas_create, а ведёт сессию скромная личная дефолт-персона,
        // у которой по роли этих инструментов нет. Решение — по СВОЙСТВУ сессии
        // (OnboardingKind пишется при создании и не мутирует), не по ходу: состав tools/list
        // стабилен. Форс не сужается состоянием PresetKey по той же причине. Работает только
        // при включённом сервере персон: Off-привязка tool:personas снимает его целиком,
        // и шаг команды честно проговаривается оверлеем как ограничение.
        var manage = session.OnboardingKind == OnboardingKinds.Project
            || _bindings.SectionEnabled(ownerId, persona, "personas-manage");
        var automation = _bindings.SectionEnabled(ownerId, persona, "personas-automation");

        var apiUrl = ResolveTasksApiUrl(ownerId);
        return new PersonasMcpContext(apiUrl, () => GetServiceToken(ownerId), projectId, selfPersonaId,
            mentionsHint, BindingsEnabled: true,
            extraProjectIds.Count > 0 ? extraProjectIds : null,
            extraPersonaIds.Count > 0 ? extraPersonaIds : null,
            ManageEnabled: manage, AutomationEnabled: automation,
            UseHttp: HttpEndpointUsable(apiUrl),
            MentionsToolsEnabled: MentionsToolsEnabled(ownerId, session, persona));
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
        var plan = BuildWorkspacePlan(ownerId, projectId, persona);
        if (plan is null) return null;
        var apiUrl = ResolveTasksApiUrl(ownerId);
        return new WorkspaceMcpContext(apiUrl, () => GetServiceToken(ownerId), projectId,
            plan.Sections, plan.AllowedProjectIds, selfSessionId,
            UseHttp: HttpEndpointUsable(apiUrl));
    }

    /// <summary>
    /// План сервера рабочего пространства: секции инструментов и зона проектов.
    /// ЕДИНАЯ формула состава (ADR-012, волна 3): её зовёт и BuildWorkspaceContext (конфиг
    /// хода и отпечаток shapes), и WorkspaceToolset (живой tools/list по сессии из хвоста
    /// маршрута). Состав и его отпечаток, посчитанные двумя формулами, расходятся — блокер
    /// приёмки волны 2 у personas. null — сервер не подключается (все секции выключены).
    /// </summary>
    internal sealed record WorkspaceMcpPlan(IReadOnlyList<string> Sections,
        IReadOnlyList<string>? AllowedProjectIds);

    // Id-заглушка пустой зоны: ни один проект владельца под ним не живёт
    internal const string NoProjectsZone = "__no-projects__";

    internal WorkspaceMcpPlan? BuildWorkspacePlan(string ownerId, string? projectId, Persona? persona)
    {
        var sections = new List<string>();
        foreach (var key in new[] { "projects", "files", "knowledge" })
            if (_bindings.EffectiveToolEnabled(ownerId, persona, key)) sections.Add(key);
        // chats — явный Tool-ключ ИЛИ неявный opt-in через ProjectPersonas-привязки:
        // персона, допущенная к чужому проекту, может писать в его чаты (решение —
        // PersonaBindingsService.ChatsSectionEnabled, там же семантика).
        var chatScopes = _bindings.BuildChatScopes(ownerId, persona);
        var chatsEnabled = _bindings.ChatsSectionEnabled(ownerId, persona);
        if (chatsEnabled) sections.Add("chats");
        // Диагностика решения по chats: набор chats-инструментов обязан быть одинаковым на всех
        // ходах персоны — по этой строке видно, из чего решение сложилось в конкретной сессии
        if (persona is not null)
            _log.LogDebug("Секция chats для персоны {Persona}: {Decision} (tools={Tools}, " +
                "toolBinding={Binding}, chatScopes={Scopes})",
                persona.Handle ?? persona.Id, chatsEnabled ? "on" : "off",
                persona.Tools is null ? "null" : string.Join("|", persona.Tools),
                persona.Bindings?.LastOrDefault(b => b.Type == PersonaBindingType.Tool
                    && string.Equals(b.Target, "chats", StringComparison.OrdinalIgnoreCase))?.Mode
                    .ToString() ?? "нет",
                chatScopes is null ? "null" : string.Join("|", chatScopes));
        if (sections.Count == 0) return null;
        // Git-инструменты (read: status/diff/log; write: stage/commit — секция git_write)
        // и базы знаний Dify владельца (kb_list/search/add_document) —
        // надстройки над базовыми секциями files/knowledge: без базовой секции не монтируются,
        // а внутри неё решает свой tool-ключ (git/kb) с дефолтом по роли персоны
        // (PersonaBindingsService.SectionEnabled — пресет SpecialtySections). Раньше обе ехали
        // с базовой секцией безусловно и стоили контекста персонам, которым не нужны.
        // Секция git приходит двумя ступенями: пресет по роли даёт ЧТЕНИЕ истории
        // (git_status/diff/log), запись (git_stage/git_commit) добавляет только явно
        // включённый ключ git — исполнители коммитят через Bash, а ролям ReadOnly запись
        // не нужна по определению (SectionOrigin, там же данные использования).
        var gitOrigin = _bindings.SectionOrigin(ownerId, persona, "git");
        if (sections.Contains("files") && gitOrigin != SectionSource.Off)
        {
            sections.Add("git");
            if (gitOrigin == SectionSource.Explicit) sections.Add("git_write");
        }
        if (sections.Contains("knowledge") && _bindings.SectionEnabled(ownerId, persona, "kb"))
            sections.Add("knowledge_bases");
        // Разрушающие операции (files_delete/chats_delete) — за отдельным флагом
        // workspace-destructive; персоне дополнительно нужен tool-ключ destructive
        // (Tool-привязка или Persona.Tools). Одна destructive без базовых секций не монтируется.
        // Профиль «Только чтение» строже любых привязок — секцию не монтируем вовсе.
        if (_flags.IsEnabled(ownerId, FeatureFlagKeys.WorkspaceDestructive)
            && persona?.Access != PersonaAccess.ReadOnly
            && _bindings.EffectiveToolEnabled(ownerId, persona, "destructive"))
            sections.Add("destructive");
        // Выкатка прода из чата (ADR-010): секция монтируется только там, где контур реально
        // настроен, и только владельцу-админу — REST всё равно admin-only, а держать три схемы
        // в контексте у всех незачем. Профиль «Только чтение» её не получает: выкатка меняет
        // прод. Все три условия постоянны в рамках сессии (конфиг машины, роль пользователя,
        // профиль персоны), поэтому состав tools/list между ходами не мерцает.
        if (Deploy.DeployOptions.From(_config).Enabled
            && persona?.Access != PersonaAccess.ReadOnly
            && string.Equals(_users.GetById(ownerId)?.Role, "admin", StringComparison.OrdinalIgnoreCase))
            sections.Add("deploy");
        sections.Add("search");

        IReadOnlyList<string>? allowedIds = null;
        var fileScopes = _bindings.BuildFileScopes(ownerId, persona);
        // Персона сферы: зона — проекты сферы ∪ проекты привязок, никогда не null (иначе все проекты
        // владельца). Проект сессии вне сферы не добавляем — чат уже отказан по OutOfZoneRefusal.
        if (_bindings.BuildZoneScopes(persona) is { } zone)
        {
            var set = new HashSet<string>(zone);
            foreach (var id in fileScopes ?? []) set.Add(id);
            foreach (var id in chatScopes ?? []) set.Add(id);
            // Потребители читают пустой список как «без сужения» — пустую зону гасим заглушкой
            allowedIds = set.Count > 0 ? set.ToList() : [NoProjectsZone];
        }
        else if (fileScopes is { Count: > 0 } || chatScopes is { Count: > 0 })
        {
            // Привязки есть — зона ужимается; проект самой сессии всегда доступен
            var set = new HashSet<string>(fileScopes ?? []);
            if (chatScopes is { Count: > 0 })
                foreach (var id in chatScopes) set.Add(id);
            if (projectId is not null) set.Add(projectId);
            allowedIds = set.ToList();
        }

        return new WorkspaceMcpPlan(sections, allowedIds);
    }

    // Контекст MCP-серверов внешних модулей (контракт §6, ТЗ R7): по каждому модулю
    // с mcp[] в манифесте и включённым у владельца флагом module-{id} (R8) — записи
    // серверов с адресом ЧЕРЕЗ gateway ядра и фабрикой свежего токена chan=mcp (TTL 60 мин;
    // ход длиннее часа получит 401 на инструментах модуля — по контракту это корректно).
    // args манифеста резолвятся от каталога модуля. null — модулей нет/все скрыты.
    private ModulesMcpContext? BuildModulesContext(string? ownerId)
    {
        if (ownerId is null || _modules is null || _moduleTokens is null) return null;
        var user = _users.GetById(ownerId);
        if (user is null) return null;
        var apiBase = ResolveTasksApiUrl(ownerId);
        var servers = new List<ModuleMcpServer>();
        foreach (var module in _modules.All)
        {
            if (module.Manifest.Mcp is not { Count: > 0 } mcpList) continue;
            if (!_flags.IsEnabled(ownerId, module.FeatureFlagKey)) continue;
            var moduleRef = module;
            foreach (var mcp in mcpList)
            {
                var args = (mcp.Args ?? [])
                    .Select(a => Path.IsPathRooted(a) ? a : Path.GetFullPath(Path.Combine(module.ModuleDir, a)))
                    .ToList();
                servers.Add(new ModuleMcpServer(
                    mcp.Key, mcp.Command, args, module.Id,
                    $"{apiBase}{module.Manifest.Backend!.RoutePrefix}",
                    () => _moduleTokens.Issue(moduleRef, user.Id, user.DisplayName ?? user.Username, "mcp")));
            }
        }
        return servers.Count > 0 ? new ModulesMcpContext(servers) : null;
    }

    // Живой состав контекста чата для подсказки хода (фича chat-context): читаем сессию из
    // реестра на КАЖДЫЙ вызов — материал, добавленный кнопкой в идущем разговоре, обязан
    // попасть в промпт следующего хода без пересоздания адаптера. Снимок здесь был бы
    // тихой ложью: вкладки у человека есть, а модель о них не знает.
    // На СОСТАВ инструментов не влияет — только на текст подсказки (гейт самого инструмента
    // живёт в BuildWorkspaceContext и решается флагом владельца).
    public Func<IReadOnlyList<SessionContextEntry>> BuildChatContextProvider(string sessionId) =>
        () => _sessions.TryGetValue(sessionId, out var entry)
            ? entry.Info.Context
            : [];

    // Серверы личного реестра владельца в ход. Решение принимается ТОЛЬКО по
    // owner/project/persona и вердикту рамки хода (свойство сессии/проекта, S2a-7) —
    // состав tools/list не смеет зависеть от свойств хода
    // (иначе сигнатура запуска мерцает и процесс CLI перезапускается со всеми MCP).
    // Провайдер, а не готовое значение: правка реестра применяется со следующего хода
    // без пересоздания адаптера. Секреты разворачиваются здесь и живут только во
    // временном конфиге хода.
    private Func<ExternalMcpContext?>? BuildExternalMcpProvider(string? ownerId, string? projectId, Persona? persona, string? sessionId)
    {
        if (ownerId is null || _mcpRegistry is null || _mcpSecrets is null) return null;
        // Профиль «Только чтение»: имён инструментов чужого сервера мы не знаем, а гасить их
        // deny-правилами нельзя — список живёт на стороне сервера и меняется, а неизвестное имя
        // в правиле роняет запуск CLI (см. историю MultiEdit в PersonaAccessPolicy). Поэтому
        // решение принимается ЦЕЛИКОМ по серверу: такая персона получает только записи с явным
        // разрешением AllowReadOnlyPersonas. Свойство персоны, не хода — состав не мерцает.
        var readOnly = persona?.Access == PersonaAccess.ReadOnly;
        var registry = _mcpRegistry;
        var secretStore = _mcpSecrets;
        return () =>
        {
            try
            {
                var servers = new List<ExternalMcpServer>();
                // Настройки проекта читаем на каждый ход — правка настроек проекта
                // применяется со следующего хода, без пересоздания адаптера.
                // Чат вне проекта каскад проекта пропускает.
                var project = projectId is null ? null : _projects.GetById(projectId);
                var onInProject = project?.McpServersOn;     // allow-модель доступа
                var isProjectChat = projectId is not null;
                // Рамка хода (S2a-7): тот же вердикт GetTurnFrame/ITurnFrameSource, что входит
                // в сигнатуру запуска (ClaudeSession). Его читаем по живому состоянию
                // сессии/проекта, а не по ходу: общий проект — Narrow любому актору включая
                // владельца (решение 29.10), личный и внепроектный — Wide. Добавление или
                // снятие участников меняет рамку вместе с сигнатурой — следующий ход стартует
                // новым процессом с новым составом реестра, состав доживающего процесса
                // при этом неизменен.
                var frame = GetTurnFrame(sessionId, ownerId);
                foreach (var record in registry.GetByOwner(ownerId))
                {
                    if (!record.Enabled) continue;
                    // Встроенные интеграции (IntegrationKeys: dify/fal-ai/glif/higgsfield)
                    // доставляются собственной веткой (TryAddHiggsfieldBuiltin) по рубильнику
                    // записи и RO-гейту. Реестровый путь для них НЕ применяется: иначе у
                    // доставки две точки истины — higgsfield, лежащий в реестре и включённый
                    // в проекте (или выданный персоне), доедет мимо продуктовой формулы, да
                    // ещё дублем. Сейчас записи dify/fal-ai/glif в реестре не заводятся
                    // (живут как HTTP-узлы Kestrel), условие держим общим — защита от случайного
                    // возврата в реестр.
                    if (Mcp.McpRegistry.IntegrationKeys.Contains(record.Key, StringComparer.OrdinalIgnoreCase)) continue;
                    // allow-модель: сервер едет, если включён «здесь» (проект этого чата
                    // по McpServersOn либо, вне проектов, AllowOutsideProjects записи) ИЛИ
                    // выдан персоне (McpServerGranted). В узкой рамке обе личные оси
                    // не смотрятся вовсе — единственный путь реестрового сервера в общий
                    // проект это явная пометка SharedWithMembers (S2a-7, решение 29.10).
                    // Чистое условие — McpDelivery.ShouldDeliver,
                    // его OR-матрицу гоняем юнитами без SessionManager. Все входы — свойства
                    // owner/project/persona/записи и вердикт рамки, ни один не смотрит на ход.
                    var granted = _bindings.McpServerGranted(persona, "mcp:" + record.Key);
                    if (!Mcp.McpDelivery.ShouldDeliver(record, onInProject, isProjectChat, granted, readOnly, frame))
                        continue;
                    var stdio = record.Transport == McpTransport.Stdio;
                    // OAuth: токен, доживающий последние секунды, обновляем ДО сборки конфига —
                    // заголовок запекается на старте CLI и живому процессу уже не доедет.
                    // null — вход протух и не восстановился: сервер снимается с хода, а статус
                    // «нужен вход» уже записан (молча ронять инструменты в 401 нельзя)
                    var fresh = record.Auth.Kind == McpAuthKind.OAuth2 && _mcpOAuth is not null
                        ? _mcpOAuth.EnsureFresh(ownerId, record)
                        : record;
                    if (fresh is null)
                    {
                        _log.LogWarning("MCP-сервер «{Key}» снят с хода: нужен вход (OAuth)", record.Key);
                        continue;
                    }
                    var env = ResolveSecretValues(ownerId, fresh.Env);
                    var headers = ResolveSecretValues(ownerId, fresh.Headers);
                    if (!stdio && !TryApplyAuthHeaders(ownerId, fresh, headers)) continue;
                    servers.Add(new ExternalMcpServer(
                        fresh.Key,
                        fresh.Transport.ToString().ToLowerInvariant(),
                        stdio ? fresh.Command : null,
                        fresh.Args ?? [],
                        env,
                        stdio ? null : fresh.Url,
                        headers,
                        fresh.AlwaysLoad,
                        fresh.AuthVersion));
                }

                return servers.Count > 0 ? new ExternalMcpContext(servers) : null;
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Реестр MCP-серверов не собрался — ход без своих серверов");
                return null;
            }
        };
    }

    // Локальные обёртки вокруг Mcp.McpAuthHeaders / секрет-стора — нужны в лямбде
    // BuildExternalMcpProvider, поэтому живут на классе. Поведение и сообщения
    // логов совпадают с теми, что были внутри лямбды.
    private Dictionary<string, string> ResolveSecretValues(string ownerId, Dictionary<string, string>? map)
    {
        if (_mcpSecrets is null) return new Dictionary<string, string>(StringComparer.Ordinal);
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (name, value) in map ?? [])
            result[name] = _mcpSecrets.Resolve(ownerId, value) ?? "";
        return result;
    }

    private bool TryApplyAuthHeaders(string ownerId, McpServerRecord record, Dictionary<string, string> headers)
    {
        if (Mcp.McpAuthHeaders.TryApply(record, headers, r => _mcpSecrets?.Resolve(ownerId, r))) return true;
        // Заголовок авторизации http/sse-сервера (общая точка с пробой — Mcp.McpAuthHeaders).
        // Потерянный секрет (запись ссылается в пустоту) — не повод отдавать серверу заведомо
        // анонимный запрос: пропускаем сервер с предупреждением, иначе инструменты молча
        // отвечали бы 401.
        _log.LogWarning("MCP-сервер «{Key}» снят с хода: не найдено значение авторизации", record.Key);
        return false;
    }

    // Контекст MCP-сервера уведомлений: обычному чату — всегда, персоне — по роли
    // (модуль автоматизации) либо по явной привязке tool:notifications; Off-привязка
    // выключает в любом случае. Единая точка решения — PersonaBindingsService.NotificationsEnabled
    // (по ПЕРСОНЕ, не по ходу). Тот же сервисный токен, что у tasks/notes/workspace.
    private NotificationsMcpContext? BuildNotificationsContext(string? ownerId, string? personaId, Persona? persona)
    {
        if (ownerId is null) return null;
        if (!_bindings.NotificationsEnabled(ownerId, persona)) return null;
        var apiUrl = ResolveTasksApiUrl(ownerId);
        return new NotificationsMcpContext(apiUrl, () => GetServiceToken(ownerId), personaId,
            UseHttp: HttpEndpointUsable(apiUrl));
    }

    // Дополнительные запреты сессии персоны: профиль доступа (PersonaAccessPolicy — «пол»
    // запретов: ReadOnly/Custom) + capability-решение «web» через привязки
    // (EffectiveToolEnabled: Tool-привязка приоритетнее Persona.Tools). Web-решение передаём
    // в policy параметром, чтобы не дублировать логику; запреты складываются — побеждает
    // более строгий (ReadOnly режет мутации, даже если binding разрешил инструмент).
    // Сессия нужна для правила «координатор не пишет код сам» (режим «Командная реализация»):
    // у чата-штаба поверх прав персоны запрещены инструменты правки файлов — работа идёт
    // задачами на исполнителей, а не руками координатора.
    private IReadOnlyList<string>? BuildExtraDisallowed(string? ownerId, Persona? persona, Session session)
    {
        var byPersona = PersonaAccessPolicy.BuildExtraDisallowed(persona,
            webAllowed: _bindings.EffectiveToolEnabled(ownerId, persona, "web"));
        if (session.TeamImplement is not { } team) return byPersona;
        // Режим включён — ExitPlanMode запрещён всегда (Э8): на стадиях интервью и планирования
        // штаб сидит в план-режиме, и штатная карточка plan_review дала бы второе согласование
        // поверх командной карточки плана. Правки файлов режет отдельная настройка.
        var extra = new List<string>(byPersona ?? []);
        extra.AddRange(TeamImplementPrompts.ModeDisallowed);
        if (team.CoordinatorNoCode) extra.AddRange(TeamImplementPrompts.CoordinatorDisallowed);
        return extra;
    }


    // Глобальная персона не бывает собеседником чата общего проекта (S2a-8): рамка хода в
    // общем проекте узкая для всех, включая арендатора (TurnFrame, решение 29.10), а
    // глобальная персона — личное пространство арендатора: её характер и память утекали бы
    // в общий чат. Отдельный тип, а не текст: контроллер отвечает code=global_persona_in_shared,
    // фронт различает этот отказ среди прочих 400 (по образцу transcript_damaged).
    public sealed class GlobalPersonaInSharedException(string message) : InvalidOperationException(message)
    {
        public const string Code = "global_persona_in_shared";
    }

    /// <summary>
    /// Персона, назначаемая чату, обязана быть видна в рамке этого чата (доработка R-S2a-8):
    /// каждая точка назначения обязана собрать набор персон и позвать этот метод до записи.
    /// Полное описание рамки и точек входа — в ChatSettings (S3-3).
    /// </summary>
    public void EnsurePersonasVisibleInFrame(string? projectId, IEnumerable<Persona?> personas) =>
        _settings.EnsurePersonasVisibleInFrame(projectId, personas);

    /// <summary>
    /// Продолжение чата общего проекта с глобальной персоной (R-S2a-8): отказ ДО платного хода
    /// передачи. Чат вне проекта и личный проект проходят (рамка Wide).
    /// </summary>
    public void EnsureContinuationPersonaAllowed(Session source, string ownerId) =>
        _settings.EnsureContinuationPersonaAllowed(source, ownerId);

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

    // Общее ядро смены собеседника/спикера (SetPersona и роутинг группового чата):
    // PersonaId и модель/усилие персоны (у начатой сессии — только при том же провайдере),
    // флаг PersonaSwitched, сброс адаптера (новый слой подхватится при следующем ходе), Save.
    // Общее ядро смены собеседника/спикера (SetPersona и роутинг группового чата) — тело в
    // ChatSettings (S3-3). Здесь остаётся точка вызова роутинга: смена спикера по @упоминанию
    // живёт в ядре до S3-4/S3-7.
    private void SwitchSpeaker(SessionEntry entry, Persona? persona, string? agentName = null) =>
        _settings.SwitchSpeaker(entry, persona, agentName);

    // Роутинг спикера группового чата перед ходом: @упоминание участника переключает
    // активного спикера (SwitchSpeaker + speaker_changed клиентам). Во время активного
    // хода (Working/Waiting) состав не трогаем — переключение подействует со следующего.
    private async Task RouteGroupSpeakerAsync(string sessionId, SessionEntry entry, string text)
    {
        if (entry.Info.Participants is not { Count: > 1 } participantIds) return;
        if (entry.Info.Status is SessionStatus.Working or SessionStatus.Waiting) return;
        var ownerId = ResolveOwnerId(entry.Info);
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
        // ClaudeSessionId приходит не только от CLI: параметр resumeSessionId тела запроса
        // (POST /sessions, /chats, /personas/{id}/chats) садится в него как есть. А дальше он
        // становится ИМЕНЕМ ПАПКИ в data/sessions и именем файла транскрипта, которые при
        // удалении чата удаляются рекурсивно — «..» в значении увел бы удаление на всю папку
        // data (projects.json, users.json, история, сторы персон и задач). Единая точка старта
        // любой сессии, поэтому гейт стоит здесь; сообщение уходит клиенту как 400.
        if (session.ClaudeSessionId is { } resumeId && !Llm.TranscriptMigrator.IsSafeSessionId(resumeId))
            throw new InvalidOperationException(
                "Недопустимый resumeSessionId: разрешены только буквы, цифры, дефис и подчеркивание");

        // Ключ истории до первого ответа агента — id чата (HistoryKeyOf): иначе внеходовые
        // записи в свежий чат (карточки нитей картинок) жили бы только в памяти
        var historyKey = HistoryKeyOf(session);
        var accumulator = new TurnAccumulator(await _history.LoadAsync(historyKey), historyKey);

        var entry = new SessionEntry { Info = session, Accumulator = accumulator };
        AttachPendingPersistence(entry);
        _sessions[session.Id] = entry;

        var ownerId = ResolveOwnerId(session);

        // Персона: её характер инжектится в системный промпт.
        // Scope контекста уже задан типом сессии (глобальная персона → чат без проекта →
        // доступ ко всем данным владельца; проектная → сессия проекта → только он).
        var persona = BuildPersonaLayer(session, ownerId);
        var workspace = BuildWorkspaceContext(ownerId, session.ProjectId, session.Id, persona.Persona);

        // Чат в отдельном worktree: рабочая папка сессии — его дерево, не корень проекта
        // (корень запоминаем — он fallback для slice графа, пока свой граф дерева не построен)
        var projectRoot = rootPath;
        rootPath = EffectiveRoot(session, rootPath);

        // Идентификатор прогона несёт колбэк: по нему OnMessageAsync отличает exited этого
        // прогона от позднего exited доживающего (см. SessionEntry.DrainOnExitedRun)
        var runId = Interlocked.Increment(ref _runSeq);

        var widgetsMcp = BuildWidgetsContext(ownerId, persona.Persona);
        var watchMcp = BuildWatchContext(ownerId);
        var webSearchMcp = BuildWebSearchContext(ownerId, persona.Persona);
        var higgsfieldMcp = BuildHiggsfieldContext(ownerId, persona.Persona);
        var imageEditorMcp = BuildImageEditorContext(ownerId, session);
        var audioEditorMcp = BuildAudioEditorContext(ownerId, session);
        var videoEditorMcp = BuildVideoEditorContext(ownerId, session);
        var pluginsMcp = BuildPluginsContext(ownerId, session);
        var turnContextMcp = BuildTurnContextContext(ownerId, session);
        var localMediaMcp = BuildLocalMediaContext(ownerId, session.ProjectId, persona.Persona);
        var testsMcp = BuildTestsContext(ownerId, session.ProjectId, persona.Persona);
        var memoryMcp = persona.Memory ?? BuildTeamMemoryContext(ownerId, session.ProjectId);
        var tasksMcp = TasksMcpEnabled(ownerId, session, persona.Persona)
            ? BuildTasksContext(ownerId, session.ProjectId, persona.Persona) : null;
        var notesMcp = _bindings.EffectiveToolEnabled(ownerId, persona.Persona, "notes")
            ? BuildNotesContext(ownerId, session.ProjectId, persona.Persona) : null;
        var personasMcp = BuildPersonasContext(ownerId, session.ProjectId, session, persona.Persona);
        var notificationsMcp = BuildNotificationsContext(ownerId, session.PersonaId, persona.Persona);
        var codeGraphMcp = BuildCodeGraphContext(ownerId, session.ProjectId, session.Id, rootPath, persona.Persona);
        var architectureMcp = BuildArchitectureContext(ownerId, session.ProjectId, persona.Persona);
        var difyMcp = BuildDifyContext(ownerId);
        var hands = HandsFor(session.ProjectId);
        var adapter = _adapters.Create(session, new LlmSessionContext(rootPath,
            msg => OnMessageQueuedAsync(session.Id, accumulator, msg, runId),
            rawSystemPrompt, ProjectManager.BuiltInSystemPrompt, permissionRules,

            ContentRootPath: AppContext.BaseDirectory,
            DataDir: _dataDir,
            TasksMcp: tasksMcp,
            OwnerId: ownerId,
            NotesMcp: notesMcp,
            PersonaProvider: BuildPersonaProvider(session, ownerId),
            MemoryMcp: memoryMcp,
            ExtraDisallowedTools: BuildExtraDisallowed(ownerId, persona.Persona, session),
            PersonasMcp: personasMcp,
            NotificationsMcp: notificationsMcp,
            WorkspaceMcp: workspace,
            PersonaAgentsProvider: BuildPersonaAgentsProvider(ownerId, session, persona.Persona),
            // Проектный чат — среда проекта (ADR-016: локальный проект исполняется на устройстве)
            Launcher: session.ProjectId is { } launchProjectId && _projects.GetById(launchProjectId) is { } launchProject
                ? _launchers.ForProject(launchProject)
                : _launchers.ForOwner(ownerId),
            ModulesMcp: BuildModulesContext(ownerId),
            WidgetsMcp: widgetsMcp,
            CodeGraphMcp: codeGraphMcp,
            DifyMcp: difyMcp,
            BrowserEnabled: BrowserEnabled(ownerId, persona.Persona),
            SystemPromptViaFile: SystemPromptViaFile,
            CliConfigRoot: ConfigRootFor(ownerId, session.Provider),
            ExternalMcpProvider: BuildExternalMcpProvider(ownerId, session.ProjectId, persona.Persona, session.Id),
            PersistSessions: SaveSessions,
            EnqueueBypass: BuildEnqueueBypass(session.Id),
            OrchestrationDone: BuildOrchestrationDone(session.Id),
            HttpMcpActive: HttpMcpActive(widgetsMcp, memoryMcp, tasksMcp, notesMcp, personasMcp,
                workspace, notificationsMcp, codeGraphMcp, difyMcp, watchMcp, webSearchMcp, higgsfieldMcp,
                imageEditorMcp, localMediaMcp, architectureMcp, audioEditorMcp, videoEditorMcp, turnContextMcp, testsMcp,
                pluginsMcp),
            HttpMcpEnabledProvider: HttpMcpEnabled,
            ArchitectureMcp: architectureMcp,
            // Материалы контекста — только у проектных чатов (адреса file/task живут внутри
            // проекта); во внепроектной ветке восстановления провайдер не передаётся вовсе
            ChatContextProvider: session.ProjectId is not null ? BuildChatContextProvider(session.Id) : null,
            Events: _turnEvents,
            WatchMcp: watchMcp,
            WebSearchMcp: webSearchMcp,
            HiggsfieldMcp: higgsfieldMcp,
            ImageEditorMcp: imageEditorMcp,
            AudioEditorMcp: audioEditorMcp,
            VideoEditorMcp: videoEditorMcp,
            PluginsMcp: pluginsMcp,
            TurnContextMcp: turnContextMcp,
            LocalMediaMcp: localMediaMcp,
            TestsMcp: testsMcp,
            // Корень ГЛАВНОЙ ветки проекта — fallback для slice графа кода, пока свой граф
            // worktree-ветки не построен (ADR-003). У не-worktree чата совпадает с rootPath,
            // fallback сводится к no-op в CodeGraphPromptProvider.GetSliceAsync.
            MainRootPath: projectRoot,
            ServerContent: ServerContentFor(session.ProjectId),
            TranscriptOnServer: TranscriptOnServer(session),
            HandsEnabled: hands,
            LlmGatewayApiUrl: LlmGatewayApiUrlFor(session, ownerId),
            // ADR-031, шаг 2.1: каталоги плагинов для --plugin-dir (опционально) и проект
            // для проверки локальности (у локального проекта в MVP плагинов нет).
            PluginDirSource: _pluginDirSource,
            // Рамка хода для подборки (S2a-4): вердикт читает сам ClaudeSession через этот шов
            Project: session.ProjectId is { } pluginProjectId ? _projects.GetById(pluginProjectId) : null,
            FrameSource: this));
        entry.Process = adapter;
        entry.RunId = runId;

        await adapter.StartAsync();
        SaveSessions();
    }

    // Приём сообщения от пользователя (Hub SendMessage) и серверных отправок (авто-ходы).
    // Пользовательское сообщение в занятом чате встаёт в видимую серверную очередь
    // (pending_messages) и ЖДЁТ штатного конца хода — доставку разбирает drain по result.
    // Идущий ход при этом НЕ убивается (поведение claude CLI: отправка ≠ остановка): kill
    // посреди хода выбрасывал сделанную работу и оплаченные токены, оставлял tool_use без
    // tool_result в транскрипте и рваные правки в проекте, а пользователь в 9 случаях из 10
    // дописывает уточнение, а не просит остановиться. Для «перебить сейчас» есть явные
    // действия: кнопка «Стоп» и PreemptForPending (кнопка на карточке очереди).
    // Возвращаемый исход (Started/Queued) говорит клиенту, рисовать ли оптимистичный баллон.
    public async Task<SendUserOutcome> SendMessageAsync(string sessionId, string text, IReadOnlyList<string> attachedPaths, string? mode = null, bool systemDirective = false, bool auto = false, string? senderPersonaId = null, bool suppressTasksExecute = false, string? senderOrigin = null, string? senderConnectionId = null, string? staffNote = null, DeliveryCause cause = DeliveryCause.Unknown, int agentDepth = 0, string? senderUserId = null)
    {
        if (!_sessions.TryGetValue(sessionId, out var entry))
            throw new InvalidOperationException("Сессия не найдена");

        ClaimFirstMessage(entry);

        if (!auto && !systemDirective)
        {
            // Идёт awaited-ход агента (REST SendMessageAndWaitAsync выставил TurnWaiter): не
            // запускаем параллельный пользовательский ход — иначе его result ошибочно разрезолвил
            // бы чужой ожидатель (agentDepth). Авто-ходы (work-loop/automation) не в счёт: они
            // не выставляют TurnWaiter и идут строго после result предыдущего.
            //
            // Осознанное исключение из «честной очереди»: здесь пользователь получает отказ, а
            // не enqueue + interrupt. Прерывание такого хода отдало бы ждущему агенту огрызок
            // (exited резолвит ожидателя тем, что успело написаться) — а сообщение пользователя
            // всё равно легло бы в очередь. Окно узкое: awaited-ход живёт до таймаута chats_send,
            // после которого ожидатель снимается и следующая попытка идёт обычным путём.
            if (entry.TurnWaiter is not null)
                throw new InvalidOperationException("Сессия занята другим ходом");

            // Человек написал в чат — цепочка автоотчётов оборвана: дальше это уже новый разговор,
            // и накопленная глубина не должна запрещать отчёты по нему
            entry.ReportChainDepth = 0;
            // Отметка «человек писал сюда» — по ней сортируется список проектов. Сохраняем
            // сразу: сообщение может встать в очередь, и ход с его SaveSessions будет нескоро
            entry.Info.LastUserMessageAt = DateTime.UtcNow;
            SaveSessions();

            // Режим «Командная реализация» (Э5): вводная человека начинает новую итерацию —
            // бюджет с нуля. Делаем это на приёме сообщения, ДО очереди: иначе вводная,
            // постоявшая в очереди, доехала бы до координатора уже с исчерпанным потолком.
            _teamNotifier.OnHumanInput(sessionId);
            // Э4/M3: ответ на карточку остановки обычным сообщением — равноправная замена
            // её кнопок (спека: «написал, что делать, координатор учёл и пошёл дальше с того
            // же места»). Стадию возвращаем ДО очереди, по тем же правилам, что решение по
            // карточке, — иначе текст человека упирался бы в гейты стадии «ждёт решения».
            await _teamNotifier.OnHumanInputAsync(sessionId);

            // Занятый чат (ход в полёте) ИЛИ активный цикл «до готово»: сообщение встаёт в
            // видимую очередь (pending_messages) и ждёт конца хода — разбор по result
            // (см. drain в OnMessageAsync). Цикл при этом НЕ снимается (решение владельца
            // 2026-08-08): пользовательское сообщение само продолжит цикл как следующую
            // итерацию (между итерациями — форсирует dispatchNow, при живом ходе — по
            // прерыванию, см. ниже).
            var loopActive = entry.Info.WorkLoop is not null;
            // Снимок статуса: ниже он решает, ждать ход или прерывать, а перечитывание дало бы
            // уже статус собственной доставки (см. про Dispatched)
            var statusSnapshot = entry.Info.Status;
            var turnInFlight = statusSnapshot is SessionStatus.Working or SessionStatus.Waiting;
            if (loopActive || turnInFlight)
            {
                // Потолок очереди проверяем ДО побочных эффектов: разморозка очереди на отказе
                // QueueFull не откатывается, и пользователь получил бы исключение при уже
                // нарушенном состоянии. Проверка предварительная — точная (под PendingLock)
                // остаётся в EnqueuePendingAsync, конкурентная постановка между ними лишь
                // вернёт тот же отказ на шаг позже.
                lock (entry.PendingLock)
                {
                    if (entry.Pending.Count >= PendingQueueService.MaxPendingPerSession)
                        throw new InvalidOperationException(
                            $"В очереди чата уже {PendingQueueService.MaxPendingPerSession} сообщений — дождитесь, пока она разберётся");
                }

                // Пользователь возобновил разговор — заморозка «Стоп» снимается ДО постановки,
                // иначе форсаж dispatchNow и разбор по концу хода упёрлись бы в QueueFrozen
                if (entry.QueueFrozen) { entry.QueueFrozen = false; PersistPending(entry); }
                var enqueued = await EnqueuePendingAsync(sessionId, entry, text, senderPersonaId, senderOrigin,
                    agentDepth: 0, kind: PendingKind.User, attachedPaths: attachedPaths, mode: mode,
                    senderUserId: senderUserId);
                if (enqueued is SendAndWaitResult.QueueFull f)
                    throw new InvalidOperationException(
                        $"В очереди чата уже {f.Limit} сообщений — дождитесь, пока она разберётся");
                // Ход прерываем ТОЛЬКО там, где ожидание его конца бессмысленно:
                //  • Waiting — ход стоит на запросе разрешения/вопросе к человеку и сам не
                //    закончится никогда; текст вместо ответа на диалог означает, что отвечать
                //    на него не будут, и без прерывания сообщение висело бы в очереди вечно;
                //  • активный цикл «до готово» — сообщение человека становится следующей
                //    итерацией (решение владельца 2026-08-08), ждать конца цикла ему незачем.
                // Обычный Working не трогаем: ход доживает сам, очередь разберёт его result.
                // Занятость и статус — по снимку ДО постановки: свободный между итерациями
                // цикла чат прерывать нечего (доставку форсирует dispatchNow), а перечитывание
                // статуса ЗДЕСЬ увидело бы Working уже своего только что доставленного
                // dispatchNow'ом хода и убило бы его. Тот же ход мог быть доставлен и форсажем
                // самой постановки (Dispatched): сообщение уже в работе — прерывать нечего,
                // иначе убьём собственный ход.
                // Снимок статуса устарел, пока шла постановка: ход, стоявший на карточке
                // разрешения, мог получить ответ из другой вкладки, дойти до result, и штатный
                // drain уже унёс НАШЕ же сообщение в новый ход. Убивать его — потерять
                // доставленное (метка разбора отработает по пустой очереди). Поэтому перед
                // kill сверяемся с текущим состоянием: ждущий ход всё ещё ждёт, а очередь
                // ещё не разобрана. Для цикла «до готово» такой сверки нет — там прерывание
                // не привязано к Waiting и ход в любом случае продолжится нашей итерацией.
                var stillPreemptable = loopActive
                    || (entry.Info.Status is SessionStatus.Waiting && HasPending(entry));
                if (turnInFlight
                    && (loopActive || statusSnapshot is SessionStatus.Waiting)
                    && stillPreemptable
                    && enqueued is not SendAndWaitResult.Queued { Dispatched: true })
                {
                    PreemptTurnForQueue(sessionId, entry, "user-message (preempt хода пользователя)",
                        byUser: cause == DeliveryCause.User);
                    return SendUserOutcome.QueuedPreempted;
                }
                return SendUserOutcome.Queued;
            }

            // Чат свободен, но очередь непуста (заморожена «Стоп» либо процесс умер аварийно):
            // новое сообщение — в КОНЕЦ очереди, в работу берётся голова (FIFO: очередь
            // приоритетнее свежего сообщения), заморозка снимается — это и есть возобновление
            QueuedMessage? head = null;
            lock (entry.PendingLock)
            {
                if (entry.Pending.Count > 0)
                {
                    if (entry.QueueFrozen) { entry.QueueFrozen = false; PersistPending(entry); }
                    head = entry.Pending[0];
                    entry.Pending.RemoveAt(0);
                    // Потолок не пробивается: голова изъята до добавления
                    entry.Pending.Add(new QueuedMessage(Guid.NewGuid().ToString("N"), text, senderPersonaId,
                        senderOrigin, AgentDepth: 0, DateTime.UtcNow, Kind: PendingKind.User,
                        AttachedPaths: attachedPaths, Mode: mode, SenderUserId: senderUserId));
                }
            }
            if (head is not null)
            {
                await BroadcastPendingAsync(sessionId, entry);
                await DeliverPendingAsync(sessionId, entry, head);
                return SendUserOutcome.Queued;
            }
        }

        await SendDirectAsync(sessionId, entry, text, attachedPaths, mode, systemDirective, auto,
            senderPersonaId, suppressTasksExecute, senderOrigin, senderConnectionId: senderConnectionId, staffNote: staffNote, cause: cause, agentDepth: agentDepth,
            senderUserId: senderUserId);
        return SendUserOutcome.Started;
    }

    // Ход, не начатый по причине отказа: сообщение об ошибке в ленту, статус Error, процесс не трогаем
    private async Task RefuseTurnAsync(string sessionId, SessionEntry entry, string reason)
    {
        _log.LogWarning("Ход чата {Session} отклонён: {Reason}", sessionId, reason);
        var runId = Interlocked.Increment(ref _runSeq);
        entry.RunId = runId;
        var acc = entry.Accumulator!;
        await OnMessageQueuedAsync(sessionId, acc, new ErrorMessage(reason, ExpectResultFollows: true, Action: PersonaZone.ChangeCompanionAction), runId);
        await OnMessageQueuedAsync(sessionId, acc, new ResultMessage(
            Subtype: "error", DurationMs: 0, NumTurns: 0, Usage: null, TotalCostUsd: null), runId);
        await OnMessageQueuedAsync(sessionId, acc, new ExitedMessage(), runId);
    }

    // Непосредственный запуск хода в процесс (гейты очереди уже пройдены либо не требуются).
    // fromQueue — доставка пользовательского сообщения из очереди: клиент рисовал его
    // призраком, поэтому live-баллон бродкастим так же, как для сервер-инициированных отправок.
    private async Task SendDirectAsync(string sessionId, SessionEntry entry, string text,
        IReadOnlyList<string> attachedPaths, string? mode, bool systemDirective, bool auto,
        string? senderPersonaId, bool suppressTasksExecute, string? senderOrigin, string? senderConnectionId = null,
        bool fromQueue = false, string? staffNote = null, DeliveryCause cause = DeliveryCause.Unknown,
        int agentDepth = 0, string? senderUserId = null)
    {
        // Общая точка доставки хода: сюда идут и три входа с отметкой, и разбор очереди отложенных
        // после рестарта (DrainRestoredPendingAsync → DeliverPendingAsync), которая отметки не ставила
        ClaimFirstMessage(entry);
        // Проект вышел из сферы персоны чата: ход не стартует, персону молча не подменяем
        if (entry.Info.PersonaId is { } zonePersonaId && ResolveOwnerId(entry.Info) is { } zoneOwnerId
            && _personas.Get(zonePersonaId, zoneOwnerId) is { } zonePersona
            && OutOfZoneRefusal(zonePersona, entry.Info.ProjectId) is { } refusal)
        {
            await RefuseTurnAsync(sessionId, entry, refusal);
            return;
        }

        // ДИАГНОСТИКА повторных доставок (инцидент 2026-08-10): каждая доставка хода в
        // процесс проходит через эту точку. src различает источник — hub (пользователь
        // через SignalR), auto (серверный ход: цикл/автоматизация/доклад исполнителя),
        // fromQueue (доставка пользовательского сообщения из серверной очереди pending).
        // cause — атрибуция callsite внутри auto/fromQueue (drain/WorkLoop/обход байпаса/…):
        // pinpoint'ит источник повторных доставок, прежде неразличимых при пустом origin.
        // Дубли видны как повторные строки с одинаковым/похожим text — pinpoint'ят источник.
        // Фоновая или отложенная доставка в чат локального проекта с офлайн-устройством: хода
        // не будет (канал отказал бы до старта) — сообщение ждёт устройство на самой сессии.
        // Прямой ввод человека и системные директивы цикла сюда не попадают: человек у экрана
        // и получает честный отказ хода, директива без своего цикла смысла не имеет.
        if (!systemDirective && (auto || fromQueue) && DeviceWaitGate(entry) is { } deviceGate)
        {
            await ParkForDeviceAsync(sessionId, entry, deviceGate, new DeviceWaitMessage(
                Guid.NewGuid().ToString("N"), text,
                fromQueue && !auto ? DeviceWaitMessage.RouteUser : DeviceWaitMessage.RouteAuto,
                DateTime.UtcNow, senderPersonaId, senderOrigin, agentDepth,
                SuppressTasksExecute: suppressTasksExecute, StaffNote: staffNote,
                AttachedPaths: attachedPaths.Count > 0 ? attachedPaths : null, Mode: mode,
                SenderUserId: senderUserId));
            return;
        }

        var deliverySrc = fromQueue ? "fromQueue" : auto ? "auto" : "hub";
        var effectiveCause = cause != DeliveryCause.Unknown ? cause : !auto ? DeliveryCause.User : DeliveryCause.Unknown;
        _log.LogInformation("Доставка хода {Session}: src={Src} cause={Cause} origin={Origin} mode={Mode} text=\"{Text}\"",
            sessionId, deliverySrc, effectiveCause, senderOrigin ?? "-", mode ?? "-",
            (text.Length > 60 ? text[..60] : text).Replace('\n', ' '));

        // Ход РЕАЛЬНО стартует по сообщению человека (hub или из очереди) — обнуляем счётчик
        // auto-resume: один раз продолженный и снова оборванный чат иначе навсегда терял бы
        // авто-продолжение, даже если следующий ход запустил человек (инцидент 20.10, чат df485156).
        // Точка — после гейтов отказа/парковки (выше), т.е. ход действительно пошёл, а не встал
        // в очередь. Авто-причины (ServerRestartResume/WorkLoop/Direct/подталкивания/Unknown)
        // счётчик не трогают: защита от crash-loop цела, человекское сообщение не повторяется
        // автоматически, поэтому зациклиться обнуление не может.
        if (effectiveCause is DeliveryCause.User or DeliveryCause.QueueUser)
            entry.Info.RestartResumeAttempts = 0;

        if (TurnStarting is { } onTurnStarting)
        {
            try { await onTurnStarting(sessionId); }
            catch (Exception ex) { _log.LogWarning(ex, "Хук старта хода {Session} упал", sessionId); }
        }

        // Режим, выбранный в Composer, применяется со следующего хода: процесс claude
        // пересоздаётся в RunTurnAsync и читает --permission-mode из Info.Mode.
        // Режим «План» у провайдера без поддержки тихо игнорируем (защита от рассинхрона UI).
        var caps = _llmProviders.CapabilitiesFor(entry.Info.Model);
        if (mode is not null && Enum.TryParse<ClaudeMode>(mode, true, out var parsedMode)
            && (parsedMode != ClaudeMode.Plan || caps.SupportsPlanMode))
        {
            // M1: сообщение с mode — та же точка смены режима, что и селектор (SetMode).
            // На стадиях интервью/планирования чат держится в план-режиме (SavedMode), и
            // сообщение его не сбрасывает — молча, отказ здесь стоил бы самого сообщения.
            if (entry.Info.TeamImplement is { SavedMode: not null })
                parsedMode = ClaudeMode.Plan;
            // …а режим, в котором CLI не спрашивает разрешений (acceptEdits/bypass), штабу
            // запрещён в любой точке смены — иначе CoordinatorWriteGuard молчит.
            if (entry.Info.TeamImplement is { } teamForGuard)
                parsedMode = PermissionModeGuard.GuardCompatibleMode(parsedMode, teamForGuard.CoordinatorNoCode);
            if (entry.Info.Mode != parsedMode)
            {
                entry.Info.Mode = parsedMode;
                SaveSessions();
            }
        }

        // M7: помечаем инициатора хода для добавочного авто-подтверждения — ход человека
        // (не авто и не директива) является контрольной точкой, агентский — нет.
        entry.TeamTurnFromHuman = !auto && !systemDirective;

        // Человек вмешался в чат — счётчик добиваний сабагента начинается заново
        // (потолок в две попытки считается на серию подряд, а не на всю жизнь чата)
        if (!auto && !systemDirective)
        {
            entry.SubagentNudges = 0;
            entry.NudgeAgentId = null;
        }

        // Групповой чат: @упоминание участника в тексте переключает активного спикера
        // ДО пересоздания процесса — новый персона-слой применяется уже к этому ходу
        await RouteGroupSpeakerAsync(sessionId, entry, text);

        // Аккаунт чата могли исчерпать другие чаты, пока этот простаивал: перед ходом
        // тихо перевозим его на здоровую подписку пула (та же модель и эндпоинт)
        TryPoolFailover(sessionId, entry);

        // Live-баллон пользовательского сообщения — в session-группу, до тяжёлого старта
        // CLI-процесса. Адресация зависит от того, кто рисовал реплику оптимистично:
        //  • авто-отправка (командный ход, автоматизация, задача) и доставка из очереди
        //    (fromQueue — клиент рисовал призрак, а не баллон): клиент не добавлял её в
        //    ленту сам — рассылаем всем;
        //  • ручной ввод из хаба (senderConnectionId): отправитель уже нарисовал баллон
        //    по исходу 'started' (useSession.send), его соединение исключаем — а остальным
        //    устройствам того же чата реплику отдаём live. Раньше они не получали её вовсе
        //    и видели только ответ «в пустоту» до перезагрузки страницы;
        //  • ручной ввод без соединения (REST/сервисные вызовы): оптимистичного баллона
        //    нет ни у кого — рассылаем всем.
        // ТОЛЬКО в session-группу: клиент открытого чата состоит и в user_/project_-группе,
        // широкая рассылка дублировала сообщение в ленте (см. комментарий у внутриходовых событий).
        if (!systemDirective)
        {
            var userMsg = new UserMessageMessage(text, attachedPaths.Count > 0 ? attachedPaths : null,
                senderPersonaId, auto, senderOrigin, StaffNote: staffNote,
                SenderUserId: HumanAuthorOf(senderUserId, agentDepth, senderPersonaId));
            if (!auto && !fromQueue && senderConnectionId is not null)
                await BroadcastExceptAsync(sessionId, senderConnectionId, userMsg);
            else
                await BroadcastAsync(sessionId, userMsg);
        }

        // Локальный голосовой ход (место chat-voice на «Локальная»): CLI-процесс не нужен,
        // достаточно аккумулятора истории — ответ принесёт RunLocalVoiceTurnAsync. Гейт
        // обязан быть детерминирован на протяжении всего SendDirectAsync (второй вызов
        // ниже, перед диспетчеризацией): все его входы — константы вызова плюс ручной
        // тумблер VoiceMode, между вызовами они не меняются.
        var localVoice = ShouldRunLocalVoice(entry, auto, systemDirective, attachedPaths);
        if (localVoice)
            await EnsureAccumulatorAsync(entry);
        else
            await EnsureProcessAsync(sessionId, entry);

        // Авторство реплик хода: text-сообщения истории получают персону на момент хода
        // (после смены собеседника старые реплики сохраняют прежний аватар)
        entry.Accumulator?.SetPersona(entry.Info.PersonaId);

        // Авто-имя сессии по первому сообщению (Claude в --print не отдаёт title/summary).
        // Только у по-настоящему нового чата: имя ещё пустое, не залочено явным заданием
        // (вручную/MCP/ретайтл) и ход ещё НЕ запускался (ClaudeSessionId == null). Иначе после
        // рестарта сообщение-продолжение («продолжи») перетирало бы уже сложившееся название.
        // Работает и для чатов вне проекта, и для проектных сессий.
        if (string.IsNullOrWhiteSpace(entry.Info.Name) && !entry.Info.NameLocked && entry.Info.ClaudeSessionId is null)
        {
            var title = MakeChatTitle(text);
            if (!string.IsNullOrEmpty(title))
            {
                entry.Info.Name = title;
                SaveSessions();
                // Фоново уточняем заголовок локальной моделью (best-effort, не блокирует ход)
                _ = RefineChatTitleAsync(sessionId, text, title, ResolveOwnerId(entry.Info));
            }
        }

        await ApplyStatusAsync(sessionId, entry, SessionStatus.Working);

        entry.Accumulator?.OnUserMessage(text, attachedPaths, viaAgent: agentDepth >= 1, systemDirective: systemDirective, auto: auto, senderPersonaId: senderPersonaId, senderOrigin: senderOrigin, staffNote: staffNote,
            senderUserId: HumanAuthorOf(senderUserId, agentDepth, senderPersonaId));
        // Сообщение пользователя = начало нового хода в основном дереве (зеркало
        // сброса skippingWorktreeTurn в SessionChangedPaths.Extract)
        entry.TurnInWorktree = false;

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
        // история и UI хранят исходное сообщение пользователя.
        // Снимок хода человека — для возврата в композер по «Стоп», если очередь пуста;
        // авто/агентские/директивные ходы не восстанавливаются (null)
        entry.CurrentTurnSnapshot = !auto && !systemDirective
            ? new UserTurnSnapshot(text, attachedPaths, mode)
            : null;
        // Превью чата — исходным сообщением, без обвязок BuildCliTurnText (адаптер превью
        // не выставляет: ему текст приходит уже обвязанным и повторяется на каждой попытке
        // фолбэка). Служебные директивы (цикл «до готово», добивание сабагента) превью НЕ
        // трогают вовсе: их сырой текст («[СИСТЕМНАЯ ДИРЕКТИВА — …]») человеку в списке чатов
        // не адресован (MINOR B-п.5) — в карточке остаётся то, что он видел последним.
        if (!systemDirective)
            entry.Info.LastMessage = ChatPreview(text);
        NoteRunDelegation(entry, agentDepth, suppressTasksExecute, fromHuman: !auto && !systemDirective);
        NoteTurnActor(entry, senderUserId, fromHuman: !auto && !systemDirective && agentDepth == 0 && !suppressTasksExecute);
        // Диспетчеризация: локальный голосовой ход идёт мимо CLI (fire-and-forget — как
        // CLI-ветка, где SendMessageAsync лишь ставит ход в процесс; ответ приходит
        // событиями через OnMessageAsync). Реплика уже в аккумуляторе (OnUserMessage выше).
        if (localVoice)
        {
            // Отметка активности: у CLI-ветки её ставит адаптер (SendMessageAsync), а
            // локальный ход идёт мимо него — без явной записи архивный чат остался бы в
            // архиве (ApplyStatusAsync его больше не двигает)
            entry.Info.UpdatedAt = DateTime.UtcNow;
            _ = Task.Run(async () =>
            {
                try { await RunLocalVoiceTurnAsync(sessionId, entry); }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[SessionManager] Локальный голосовой ход ({sessionId}): {ex.Message}");
                }
            });
        }
        else
        {
            // Голосовой ход (не-локальная ветка): модель места chat-voice по ЯВНОМУ выбору
            // админа (слот → UserModelTierResolver.ModelFor, конкретная модель → как есть).
            // «Локальная» (ветка выше), «по умолчанию» и отсутствие выбора → null: ход идёт
            // на модели чата, как раньше. Неголосовой ход override не получает (место
            // chat-voice к нему не относится), поэтому обычная модель чата не подменяется.
            var voiceModel = entry.Info.VoiceMode
                ? _router?.VoiceModelFor(Llm.LocalActionCatalog.ChatVoice, ResolveOwnerId(entry.Info))
                : null;
            // Блокер: override на модели ДРУГОГО провайдера ломает транскрипт — CLI стартует
            // в чужом профиле (CLAUDE_CONFIG_DIR) с --resume на транскрипт, которого там нет
            // («No conversation found» / ход без контекста). Штатный переезд провайдера идёт
            // только через MigrateProviderAsync с guard'ом, а override его обходит. Поэтому
            // override применяем, только если провайдер модели override совпадает с провайдером
            // модели чата (резолв — LlmProviderRegistry, тот же, что у guard'а; профиль
            // локального CLI-провайдера тоже считается другим). Миграцию на каждый ход НЕ
            // делаем: ход идёт на модели чата, как раньше.
            if (voiceModel is not null
                && !string.Equals(_llmProviders.ProviderKey(voiceModel), _llmProviders.ProviderKey(entry.Info.Model),
                    StringComparison.OrdinalIgnoreCase))
            {
                _log.LogWarning(
                    "Голосовой ход чата {SessionId}: модель места chat-voice «{VoiceModel}» (провайдер {VoiceKey}) " +
                    "не совпадает с провайдером модели чата {ChatKey} — ход идёт на модели чата, транскрипт не мигрируем",
                    sessionId, voiceModel, _llmProviders.ProviderKey(voiceModel), _llmProviders.ProviderKey(entry.Info.Model));
                voiceModel = null;
            }
            await entry.Process!.SendMessageAsync(BuildCliTurnText(entry, text, auto, systemDirective, senderPersonaId), attachedPaths,
                agentDepth, suppressTasksExecute: suppressTasksExecute, turnModelOverride: voiceModel);
        }
    }

    // Превью чата для карточки списка: первые 100 символов сообщения
    internal static string ChatPreview(string text) => text.Length > 100 ? text[..100] + "…" : text;

    // Сигнал модели после «Стоп» человека — общий для кнопки в чате и в трее рук
    internal const string UserStopNoteText =
        "[Ход остановлен человеком (кнопка «Стоп»). Прерванное молча не доделывай: продолжай его, " +
        "только если об этом просят ниже.]";

    // Текст хода для CLI: исходное сообщение + обвязки.
    // Протокол цикла «до готово» — пока Session.WorkLoop активен. Своей вставки ultrawork
    // больше нет: слова ultrawork/ulw ловит keyword-detector плагина oh-my-claudecode.
    private string BuildCliTurnText(SessionEntry entry, string text, bool auto, bool systemDirective, string? senderPersonaId)
    {
        // Маркер реплики человека (шаг 1 сжатия): только на прозу человека (ShouldMarkHumanText) —
        // не на слэш-команды/скиллы (разворот скилла важнее) и не на не-человеческие ходы (auto,
        // systemDirective, реплики персон). Непосредственно перед СЫРЫМ текстом, до любой обвязки
        // ниже и до вложений (AttachmentInliner в ClaudeSession.SendMessageAsync) — N и sha
        // относятся только к тому, что человек ввёл. Магслова ниже ищем по сырому text.
        // Только в текст для CLI: история, лента и превью берут text без маркера (SendDirectAsync).
        var result = ClaudeHomeServer.Services.Llm.Claude.ClaudeSession.ShouldMarkHumanText(text, auto, systemDirective, senderPersonaId)
            ? ClaudeHomeServer.Services.Llm.Claude.ClaudeSession.WithHumanTextMarker(text)
            : text;

        // Обрыв фонового агента (см. NoteTruncatedBgAgent): CLI объявил его прогон
        // завершённым и отдал координатору последнюю реплику как результат. Пометка едет
        // префиксом ближайшего хода — своего хода на неё не тратим, а координатор обязан
        // знать, что итог по обрывку подводить нельзя. Одноразовая: уехала — снята.
        if (entry.TruncatedBgNote is { } cutBgAgent)
        {
            entry.TruncatedBgNote = null;
            result = SubagentPrompts.TruncatedBgAgent(cutBgAgent) + "\n\n" + result;
        }

        if (entry.UserStopNote)
        {
            entry.UserStopNote = false;
            result = UserStopNoteText + "\n\n" + result;
        }

        if (entry.Info.WorkLoop is { } loop)
        {
            // Буфер LoopTurnText здесь НЕ чистим: ход ставится в очередь, а стримящийся
            // сейчас ход ещё копит текст — очистка на постановке стирала бы его маркер.
            // Буфер потребляет и чистит ContinueWorkLoopAsync по result хода.
            entry.LoopTurnInFlight = true;
            // Верификационный ход идёт со своей директивой — рабочий протокол не дописываем
            if (loop.Phase != "verifying")
                result += "\n\n" + OmoPrompts.WorkLoopTurn(loop.Promise);
        }

        // Режим «Командная реализация»: штаб на каждом ходу получает правило «любая работа —
        // через задачу» и свою стадию. Без напоминания координатор срезает углы и делает
        // работу сам — гард на инструменты правки его только останавливает, но не направляет.
        if (entry.Info.TeamImplement is { } team)
            result += "\n\n" + TeamImplementPrompts.CoordinatorTurn(team);

        // Магслова oh-my-claudecode (ultrawork, ralph, autopilot…): хук keyword-detector
        // плагина отключён вместе со всеми хуками (disableAllHooks — иначе окна консоли
        // на хосте), поэтому активируем скилл сами — дописываем инструкцию его запуска.
        if (OmcKeywordRouting.BuildKeywordHint(text) is { } keywordHint)
            result += "\n\n" + keywordHint;

        // Процессы oh-my-claudecode: советнические роли плагина замещаются
        // персонами-сабагентами с подходящей специальностью (таблица соответствий)
        if (OmcPersonaRouting.MentionsPluginCommand(text) && ResolveOwnerId(entry.Info) is { } ownerId)
        {
            var (subagents, _) = SplitConsultants(ownerId, entry.Info,
                ResolveOtherPersonas(ownerId, entry.Info.ProjectId, entry.Info));
            if (OmcPersonaRouting.BuildHint(subagents) is { } routingHint)
                result += "\n\n" + routingHint;
        }

        // Возврат на CLI после локальных голосовых ходов: транскрипт CLI реплик разговора
        // не знает, без сводки модель отвечала бы так, будто разговора не было. Берём
        // хвост истории (лента едина) тем же лимитом, что и разговорный контекст.
        if (entry.LocalTurnsSinceCli > 0 && entry.Accumulator is { } voiceAcc)
        {
            var summary = BuildVoiceContextBlock(voiceAcc);
            if (!string.IsNullOrEmpty(summary))
                result = summary + "\n\n" + result;
            entry.LocalTurnsSinceCli = 0;
        }

        return result;
    }

    // Сводка локального разговора для CLI-хода: «Пользователь: … / Ассистент: …» из
    // хвоста истории. null/пусто — реплик не нашлось (тогда и блок не нужен).
    private static string? BuildVoiceContextBlock(TurnAccumulator acc)
    {
        var lines = acc.GetAll()
            .Where(m => m is StoredUserMessage { SystemDirective: not true, Text: { Length: > 0 } }
                        or StoredTextMessage { Text: { Length: > 0 } })
            .TakeLast(VoiceHistoryMessages)
            .Select(m => m switch
            {
                StoredUserMessage u => $"Пользователь: {TrimVoiceMessage(u.Text!)}",
                StoredTextMessage t => $"Ассистент: {TrimVoiceMessage(t.Text!)}",
                _ => null,
            })
            .Where(l => l is not null)
            .ToList();
        if (lines.Count == 0) return null;
        return "[Контекст: до этого пользователь разговаривал голосом с локальной моделью " +
               "(краткие реплики, без инструментов). Последние реплики разговора:\n" +
               string.Join("\n", lines) + "\n]";
    }

    // Гейт локального голосового хода: разговор (Session.VoiceMode) на локальной модели
    // (место chat-voice маршрутизировано на «Локальная»). Только ходы человека без
    // вложений и без протоколов CLI (цикл «до готово»/штаб свои маркеры локаль не
    // воспроизведёт); авто-ходы и доклады автоматизаций — через CLI как раньше.
    // Персона не блокирует: её характер подмешивается в system-промпт разговора.
    private bool ShouldRunLocalVoice(SessionEntry entry, bool auto, bool systemDirective,
        IReadOnlyList<string> attachedPaths) =>
        entry.Info.VoiceMode
        // Только стиль talk: digest — это полный агентный ответ с маркером <voice> в конце,
        // а локальная болталка (LocalCompanionSection) не умеет ни инструменты, ни маркер.
        // Забыть этот гейт — значит молча озвучить фолбэком всю реплику Ollama целиком.
        && !entry.Info.IsVoiceDigest
        && _router is not null && _ollama is not null
        && _router.UsesLocal(Llm.LocalActionCatalog.ChatVoice)
        && !auto && !systemDirective
        && entry.Info.WorkLoop is null
        && entry.Info.TeamImplement is null
        && attachedPaths.Count == 0;

    // messages[] для разговорного вызова Ollama: system-промпт собеседника (+ характер
    // персоны при чате с персоной — дёшево, без полного слоя персоны: инструменты, память
    // и привязки локальному ходу всё равно недоступны) + хвост истории. Текущая реплика
    // уже лежит в аккумуляторе (OnUserMessage общего хвоста SendDirectAsync) — отдельно
    // не добавляется, иначе продублировалась бы. GetAll() сам берёт лок аккумулятора.
    private List<Llm.ChatMsg> BuildVoiceMessages(SessionEntry entry, TurnAccumulator acc)
    {
        var system = Prompts.VoicePrompts.LocalCompanionSection;
        if (entry.Info.PersonaId is { } pid
            && _personas.GetByIdInternal(pid)?.Contract is { } contract)
        {
            var character = new List<string>(2);
            if (!string.IsNullOrWhiteSpace(contract.Character)) character.Add(contract.Character);
            if (!string.IsNullOrWhiteSpace(contract.Tone)) character.Add($"Тон: {contract.Tone}.");
            if (character.Count > 0)
                system += "\n\nТы говоришь от лица персонажа: " + string.Join(" ", character) +
                          "\nЭто сокращённый характер — полный образ и память недоступны в этом режиме.";
        }

        var history = acc.GetAll()
            .Where(m => m is StoredUserMessage { SystemDirective: not true, Text: { Length: > 0 } }
                        or StoredTextMessage { Text: { Length: > 0 } })
            .TakeLast(VoiceHistoryMessages)
            .Select(m => m switch
            {
                StoredUserMessage u => new Llm.ChatMsg("user", TrimVoiceMessage(u.Text!)),
                StoredTextMessage t => new Llm.ChatMsg("assistant", TrimVoiceMessage(t.Text!)),
                _ => null,
            })
            .Where(m => m is not null)
            .Cast<Llm.ChatMsg>()
            .ToList();

        var result = new List<Llm.ChatMsg>(history.Count + 1) { new("system", system) };
        result.AddRange(history);
        return result;
    }

    // Лимиты разговорного контекста: хвост истории и усечение одной реплики. Разговор
    // короткий, а num_ctx профиля Text — 8192 токенов: без усечения длинный старый ход
    // молча вытеснил бы свежие реплики.
    internal const int VoiceHistoryMessages = 16;
    internal static string TrimVoiceMessage(string text) =>
        text.Length <= 1500 ? text : text[..1500] + "…";

    // Локальный голосовой ход: прямой вызов Ollama мимо claude CLI. Ответ приходит
    // синтетическими ServerMessage через OnMessageAsync — тот же конвейер, что у CLI
    // (аккумулятор, статусы Working→Active→Finished, разбор очереди, бродкаст).
    // Фолбэка на CLI нет: тихий 15-секундный старт подпроцесса в разговоре хуже
    // видимой ошибки в ленте.
    private async Task RunLocalVoiceTurnAsync(string sessionId, SessionEntry entry)
    {
        var runId = Interlocked.Increment(ref _runSeq);
        entry.RunId = runId;
        var acc = entry.Accumulator!;
        // Ключ истории: транскрипт CLI, если чат уже начат (лента едина), иначе id чата.
        // Info.ClaudeSessionId ветка НЕ ставит — его ставит CLI штатно при первом CLI-ходе.
        acc.SetSaveKey(entry.Info.ClaudeSessionId ?? sessionId);

        // Provider не заполняем (дефолт claude): фронт модель из каталога не знает и
        // деградирует в Claude-облик — бейдж не ломается, фронт не трогаем.
        await OnMessageQueuedAsync(sessionId, acc, new SessionStartedMessage(
            entry.Info.ClaudeSessionId ?? sessionId, IsResume: false,
            Model: _router!.LocalModel, Mode: entry.Info.Mode.ToString().ToLowerInvariant()), runId);

        // Профиль читаем по ключу места — раньше жёстко `CheapProfile.Text` и таймаут
        // по ключу места; пока значения совпадали — `chat-voice` объявлен `CheapProfile.Text`.
        // Одна строка правды устраняет рассинхрон при переводе места на другой профиль.
        var spec = _router.ProfileFor(Llm.LocalActionCatalog.ChatVoice);
        var messages = BuildVoiceMessages(entry, acc);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        using var cts = new CancellationTokenSource();
        entry.LocalVoiceCts = cts;
        Llm.ChatTurnResult? turn = null;
        var cancelled = false;
        // Сколько текста уже ушло в ленту потоком: по нему решается, слать ли ответ
        // целиком в конце (страховка на случай, если поток не дал ни куска)
        var streamed = 0;
        try
        {
            turn = await _ollama!.ChatTurnAsync(messages, _router.LocalModel,
                TimeSpan.FromMilliseconds(_router.TimeoutMsFor(Llm.LocalActionCatalog.ChatVoice)),
                spec.NumPredict, spec.NumCtx, ResolveOwnerId(entry.Info),
                // Поток: куски ответа уходят в ленту (и в озвучку разговора) по мере
                // генерации — иначе первый звук ждал бы конца всего ответа
                onDelta: async chunk =>
                {
                    streamed += chunk.Length;
                    await OnMessageQueuedAsync(sessionId, acc, new TextDeltaMessage(chunk), runId);
                },
                ct: cts.Token);
        }
        catch (OperationCanceledException)
        {
            // «Стоп» пользователя: сообщения не шлём, exited ниже закроет ход (Working→Active)
            cancelled = true;
        }
        catch (Exception ex)
        {
            await OnMessageQueuedAsync(sessionId, acc, new ErrorMessage($"Локальная модель: {ex.Message}"), runId);
        }
        finally
        {
            entry.LocalVoiceCts = null;
            entry.LocalTurnsSinceCli++;
        }

        if (turn?.Text is { Length: > 0 } answer)
        {
            // Обычный путь: текст уже ушёл кусками из onDelta. Целиком шлём только если
            // поток не дал ничего (не-потоковый ответ, сбой до первого куска)
            if (streamed == 0)
                await OnMessageQueuedAsync(sessionId, acc, new TextDeltaMessage(answer), runId);
            await OnMessageQueuedAsync(sessionId, acc, new ResultMessage(
                Subtype: "success", DurationMs: sw.ElapsedMilliseconds, NumTurns: 1,
                Usage: turn.Usage, TotalCostUsd: 0), runId);
        }
        else if (!cancelled)
        {
            // Пустой ответ/сбой HTTP — честная ошибка без фолбэка на CLI
            // (ErrorMessage исключения уже ушёл выше — второй не дублируем).
            // Занятость общего счётчика — не отказ движка: человеку так и говорим
            if (turn is not null)
                await OnMessageQueuedAsync(sessionId, acc, new ErrorMessage(turn.Busy
                    ? "Локальный движок занят — ход не запущен, повторите чуть позже"
                    : "Локальная модель не ответила"), runId);
        }

        await OnMessageQueuedAsync(sessionId, acc, new ExitedMessage(), runId);
    }


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
    public async Task<SendAndWaitResult> SendMessageAndWaitAsync(string sessionId, string text,
        TimeSpan timeout, int agentDepth = 0, string? senderPersonaId = null,
        string? senderOrigin = null, string? senderChatName = null, bool preempt = true,
        bool systemDirective = false, string? senderUserId = null)
    {
        if (!_sessions.TryGetValue(sessionId, out var entry))
            throw new InvalidOperationException("Сессия не найдена");

        ClaimFirstMessage(entry);

        // Занята только при реально идущем ходе (Working) или ожидании человека (Waiting).
        // Starting у живой сессии означает лишь «создан, ход ещё не запускался»: после первого
        // хода статус идёт Working→Active/Finished и назад в Starting не возвращается (обратно
        // Starting ставит только рестарт → Orphaned). Поэтому свежесозданный чат (у него Process
        // уже присвоен в StartNewSessionAsync, но ходов не было) НЕ занят — принимаем сообщение
        // и стартуем первый ход. Гонку двух одновременных ходов ловит TurnWaiter ниже.
        var status = entry.Info.Status;
        if (status is SessionStatus.Working or SessionStatus.Waiting)
        {
            var queued = await EnqueuePendingAsync(sessionId, entry, text, senderPersonaId, senderOrigin, agentDepth,
                senderChatName: senderChatName, senderUserId: senderUserId);
            // Агентское сообщение по умолчанию (preempt=true) прерывает текущий ход
            // (доставится сразу по его концу), но НЕ рушит цикл «до готово» и штаб
            // «Командной реализации» — там доклад ждёт штатного конца хода, как раньше.
            // preempt=false рабочего хода (Working) не прерывает: сообщение ждёт штатного
            // result, и живой процесс получает его same-process — Kill ради доставки убивал
            // бы фоновых сабагентов, работающих внутри чужого хода (диагностика обрывов
            // агентов 28.08, чат ea9a0f18). Waiting прерывается и при preempt=false: ход на
            // карточке разрешения сам не закончится, «не прерывать» там — висеть вечно
            // (та же логика, что у пользовательского пути в SendMessageAsync).
            // Дубликат и переполнение ничего не прерывают;
            // замороженную «Стоп» очередь агент не возобновляет — прерывать ход тоже не ему.
            // Dispatched — ход успел кончиться между снимком занятости и постановкой, и
            // доставку уже форсировал сам enqueue: прерывать теперь означало бы убить
            // собственный только что запущенный ход.
            if (queued is SendAndWaitResult.Queued { Duplicate: false, Dispatched: false }
                && entry.Info.WorkLoop is null && entry.Info.TeamImplement is null
                && !entry.QueueFrozen
                && (preempt || status is SessionStatus.Waiting))
            {
                entry.DrainOnExitedRun = entry.RunId;
                _log.LogInformation("Interrupt адаптера {Session}: callsite=agent-message (preempt хода агента, chats_send)", sessionId);
                entry.Process?.Interrupt();
            }
            return queued;
        }

        // Чат локального проекта, устройство офлайн: агентское сообщение (chats_send,
        // будильник сторожа) ждёт устройство на сессии — принято, но хода пока не будет
        if (DeviceWaitGate(entry) is { } deviceGate)
            return await ParkForDeviceAsync(sessionId, entry, deviceGate, new DeviceWaitMessage(
                Guid.NewGuid().ToString("N"), text, DeviceWaitMessage.RouteAgent, DateTime.UtcNow,
                senderPersonaId, senderOrigin, agentDepth, SenderChatName: senderChatName,
                SenderUserId: senderUserId));

        await EnsureProcessAsync(sessionId, entry);
        entry.Accumulator?.SetPersona(entry.Info.PersonaId);

        // Авто-имя по первому сообщению — как при отправке человеком
        if (string.IsNullOrWhiteSpace(entry.Info.Name) && !entry.Info.NameLocked && entry.Info.ClaudeSessionId is null)
        {
            var title = MakeChatTitle(text);
            if (!string.IsNullOrEmpty(title))
            {
                entry.Info.Name = title;
                SaveSessions();
                // Фоново уточняем заголовок локальной моделью (best-effort, не блокирует ход)
                _ = RefineChatTitleAsync(sessionId, text, title, ResolveOwnerId(entry.Info));
            }
        }

        // Один ожидатель на ход: параллельная отправка проиграла гонку — в очередь БЕЗ
        // прерывания (чужой ход только стартует, рубить его чужим сообщением нельзя)
        var tcs = new TaskCompletionSource<TurnResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (Interlocked.CompareExchange(ref entry.TurnWaiter, tcs, null) is not null)
            return await EnqueuePendingAsync(sessionId, entry, text, senderPersonaId, senderOrigin, agentDepth,
                senderChatName: senderChatName, senderUserId: senderUserId);
        entry.TurnWaiterBaseline = entry.Accumulator?.GetAll().Count ?? 0;

        await ApplyStatusAsync(sessionId, entry, SessionStatus.Working);
        entry.Accumulator?.OnUserMessage(text, [], viaAgent: agentDepth >= 1, senderPersonaId: senderPersonaId,
            systemDirective: systemDirective, senderOrigin: senderOrigin,
            senderUserId: HumanAuthorOf(senderUserId, agentDepth, senderPersonaId));
        entry.TurnInWorktree = false; // сообщение = новый ход в основном дереве (зеркало Extract)
        entry.CurrentTurnSnapshot = null; // ход агента — по «Стоп» в композер не возвращается
        entry.TeamTurnFromHuman = false; // ход поднят агентом (chats_send), не человеком (M7)
        // Превью чата — сообщением агента: адаптер его больше не выставляет (см. ChatPreview)
        entry.Info.LastMessage = ChatPreview(text);
        NoteRunDelegation(entry, agentDepth, suppressTasksExecute: false, fromHuman: false);
        NoteTurnActor(entry, senderUserId, fromHuman: false);
        await entry.Process!.SendMessageAsync(text, null, agentDepth);

        if (timeout <= TimeSpan.Zero) return new SendAndWaitResult.Running();
        var completed = await Task.WhenAny(tcs.Task, Task.Delay(timeout));
        return completed == tcs.Task
            ? new SendAndWaitResult.Completed(await tcs.Task)
            : new SendAndWaitResult.Running(); // ход продолжается, ожидатель очистит OnMessageAsync
    }

    // === Очередь сообщений занятой сессии ===
    // Тела переехали в PendingQueueService (S3-2): постановка с дедупом, снимки, отмена,
    // заморозка-проверки. Здесь обёртки с прежними сигнатурами: вызывающие места
    // (приём хода, «Стоп», байпас фолбэка, DeviceWait) живут в фасаде до S3-4/S3-7.
    // Рекурсия «постановка → разбор» (dispatchNow → DrainNextPendingAsync) разорвана
    // швом Lazy<ITurnStarter>: его реализация в переходном режиме — сам фасад.

    private async Task<SendAndWaitResult> EnqueuePendingAsync(string sessionId, SessionEntry entry,
        string text, string? senderPersonaId, string? senderOrigin, int agentDepth,
        bool silent = false, bool suppressTasksExecute = false, string? senderChatName = null,
        PendingKind kind = PendingKind.Agent, IReadOnlyList<string>? attachedPaths = null,
        string? mode = null, string? staffNote = null, string? senderUserId = null) =>
        await _pending.EnqueuePendingAsync(sessionId, entry, text, senderPersonaId, senderOrigin, agentDepth,
            silent, suppressTasksExecute, senderChatName, kind, attachedPaths, mode, staffNote, senderUserId);

    // Постановка хода в серверную Pending взамен байпаса в _inner (инцидент 2026-08-10 П3):
    // фолбэк-адаптер вызывает при попытке доставки под активной оркестрацией. Ход уходит в
    // очередь как агентский (kind=Agent, дедуп по text+persona), origin помечает источник для
    // лога «Доставка хода». dispatchNow внутри EnqueuePendingAsync гейтится OrchestrationActive —
    // разбор откладывается до OrchestrationDone (finally адаптера).
    private async Task EnqueueBypassTurn(string sessionId, string text,
        IReadOnlyList<string>? attachedPaths, int agentDepth, bool suppressTasksExecute)
    {
        if (!_sessions.TryGetValue(sessionId, out var entry)) return;
        await EnqueuePendingAsync(sessionId, entry, text,
            senderPersonaId: null, senderOrigin: "orchestration-bypass",
            agentDepth, silent: false, suppressTasksExecute,
            kind: PendingKind.Agent, attachedPaths: attachedPaths);
    }

    // Билдеры колбэков для LlmSessionContext: замыкают sessionId сессии (адаптер передаёт тот же
    // Info.Id, но захват надёжнее — сессия уже известна при создании контекста).
    private Func<string, string, IReadOnlyList<string>?, int, bool, Task> BuildEnqueueBypass(string sessionId)
        => (_, text, paths, depth, suppress) => EnqueueBypassTurn(sessionId, text, paths, depth, suppress);

    private Action<string> BuildOrchestrationDone(string sessionId)
        => __ => { _ = Task.Run(async () =>
            {
                try { await DrainNextPendingAsync(sessionId); }
                catch (Exception ex) { Console.Error.WriteLine($"[SessionManager] Разбор очереди после оркестрации ({sessionId}): {ex.Message}"); }
            }); };

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
    public async Task<bool> SendOrEnqueueAsync(string sessionId, string text,
        string? senderPersonaId = null, string? senderOrigin = null,
        bool silent = false, bool suppressTasksExecute = false, string? staffNote = null,
        PendingKind kind = PendingKind.Agent, string? senderUserId = null)
    {
        if (!_sessions.TryGetValue(sessionId, out var entry))
            throw new InvalidOperationException("Сессия не найдена");

        if (entry.Info.Status is SessionStatus.Working or SessionStatus.Waiting)
        {
            await EnqueuePendingAsync(sessionId, entry, text, senderPersonaId, senderOrigin,
                agentDepth: 0, silent, suppressTasksExecute, senderChatName: null, kind: kind,
                staffNote: staffNote, senderUserId: senderUserId);
            return true;
        }

        await SendMessageAsync(sessionId, text, [], auto: true, senderPersonaId: senderPersonaId,
            suppressTasksExecute: suppressTasksExecute, senderOrigin: senderOrigin, staffNote: staffNote,
            cause: DeliveryCause.Direct, senderUserId: senderUserId);
        return false;
    }

    // === Отчёт «наверх»: в родительский чат ===

    // Результат попытки отчитаться. TooDeep — цепочка автоотчётов упёрлась в потолок;
    // NoParent — у чата нет эффективного родителя (обычный чат либо вынесен в корень).
    public enum ReportUpResult { Delivered, Queued, NoParent, TooDeep, NotFound }

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

    // Прервать идущий ход РАДИ очереди: убитый процесс result не пришлёт, поэтому доставку
    // разберёт exited того же прогона (DrainOnExitedRun). В отличие от «Стоп» очередь НЕ
    // морозится — прерывание здесь и есть требование доставить ждущее сообщение сейчас.
    // byUser — перебой затеял человек (кнопка на карточке очереди, его сообщение в ход, ждавший
    // его ответа): живая лента ставит на нём отметку «Ход остановлен пользователем», история
    // обязана её повторить, иначе после F5 разъедется с ней (и со сверкой длин на фронте).
    private void PreemptTurnForQueue(string sessionId, SessionEntry entry, string callsite, bool byUser)
    {
        if (byUser) RecordUserInterrupt(sessionId, entry);
        // Ход убит — result по нему не придёт, а с ним не придёт и потребление буфера маркеров
        // (конец хода в OnMessageAsync, у штаба ещё и HandleTeamTurnEndAsync). Чистим синхронно:
        // иначе маркер мёртвого хода склеился бы с текстом следующего и применился задним
        // числом — фантомная эскалация и сдвиг стадии (класс «волны-призрака»).
        // Буфер копится в любом чате, поэтому и чистим без оглядки на режим штаба.
        lock (entry.TeamTurnLock)
        {
            entry.TeamTurnText.Clear();
            entry.TeamTurnShownLength = 0;
            entry.TurnSawAngleBracket = false;
            entry.TeamTurnAsked = false;
        }
        // Прерванный ход result не пришлёт — погасим маркер итерации цикла, иначе он
        // заблокирует разбор очереди (drain уступает, пока LoopTurnInFlight).
        entry.LoopTurnInFlight = false;
        entry.DrainOnExitedRun = entry.RunId;
        _log.LogInformation("Interrupt адаптера {Session}: callsite={Callsite}", sessionId, callsite);
        entry.Process?.Interrupt();
    }

    // Явное «прервать ход и доставить ждущее сейчас» (кнопка на карточке очереди). Отдельно
    // от «Стоп»: тот морозит очередь и возвращает сообщение в композер, а здесь наоборот —
    // очередь разбирается сразу по exited. Без живого хода и без очереди делать нечего:
    // холостой kill убил бы чужой только что стартовавший ход. false — прерывать было нечего.
    public bool PreemptForPending(string sessionId)
    {
        if (!_sessions.TryGetValue(sessionId, out var entry)) return false;
        if (entry.Info.Status is not (SessionStatus.Working or SessionStatus.Waiting)) return false;
        // Живого прогона нет — убивать нечего, exited не придёт, а взведённая метка разбора
        // осталась бы висеть. Два случая, и оба должны получить честный отказ:
        //  • чат залип в Working/Waiting (ход убил ватчдог/сбой либо статус выставил протухший
        //    ответ на карточку) — реанимирует его «Стоп», клиенту подскажет 409;
        //  • окно ротации фолбэка (OrchestrationActive без прогона): Interrupt внутреннего
        //    адаптера там no-op, придержанного терминала у попытки нет (Held очищен при
        //    SwallowCleanup), и SettleAsync вынесет наружу пустоту — ни exited, ни доставки.
        if (entry.Process is null or { HasLiveTurn: false }) return false;
        // Идёт сворачивание контекста: это тоже ход (CompactAsync → QueueTurnAsync), но обёртка
        // фолбэка его не оркеструет (_turn == null), и exited убитой компакции она ГЛОТАЕТ —
        // до SessionManager не дойдёт ничего. Перебой оставил бы чат в вечном Working с
        // застрявшей очередью, поэтому отказываем: компакция короткая, её стоит дождаться.
        if (entry.CompactRun != 0 && entry.CompactRun == entry.RunId) return false;
        if (entry.QueueFrozen) return false;
        lock (entry.PendingLock)
            if (entry.Pending.Count == 0) return false;
        PreemptTurnForQueue(sessionId, entry, "pending-preempt (кнопка «прервать и отправить»)", byUser: true);
        return true;
    }

    // Отменить ожидающее сообщение (крестик на карточке-призраке). false — уже доставлено.
    public async Task<bool> CancelPendingAsync(string sessionId, string messageId)
    {
        if (!_sessions.TryGetValue(sessionId, out var entry)) return false;
        bool removed;
        var removedWaiting = false;
        lock (entry.PendingLock)
        {
            removed = entry.Pending.RemoveAll(p => p.Id == messageId) > 0;
            // Крестик на карточке «ждёт устройство» — снимает сообщение с сессии
            if (!removed && entry.Info.DeviceWaitQueue is { } waiting && waiting.Any(m => m.Id == messageId))
            {
                var rest = waiting.Where(m => m.Id != messageId).ToList();
                entry.Info.DeviceWaitQueue = rest.Count > 0 ? rest : null;
                removed = removedWaiting = true;
            }
        }
        if (removedWaiting) SaveSessions();
        if (removed) await BroadcastPendingAsync(sessionId, entry);
        return removed;
    }

    // Убрать поставленное (queued) сообщение по тексту — для служебных отправок (записка-передача),
    // которые не должны отработать отдельным ходом, если чат занялся между проверкой занятости и
    // отправкой. true, если что-то убрано. Тело — в PendingQueueService (S3-2).
    public bool RemovePendingByText(string sessionId, string text) => _pending.RemovePendingByText(sessionId, text);

    // Прерывание хода («Стоп») замораживает очередь: сообщения НЕ выбрасываются (как было
    // раньше), а остаются стоять — автодоставки после прерывания нет до возобновления
    // пользователем. Одновременно последнее пользовательское возвращается в композер:
    //   • пользовательские в очереди есть → последнее ИЗЫМАЕТСЯ и уходит в composer_restore;
    //   • пользовательских нет, но прерванный ход был пользовательским → копия этого хода
    //     (из ленты не убирается, вернётся как ghost при возобновлении);
    //   • прерван авто/агентский ход и очередь пуста → composer_restore пустой.
    private async Task FreezePendingAsync(string sessionId, SessionEntry entry)
    {
        QueuedMessage? lastUser = null;
        bool hadAny;
        lock (entry.PendingLock)
        {
            entry.QueueFrozen = true;
            PersistPending(entry);
            hadAny = entry.Pending.Count > 0;
            // Последнее пользовательское изымаем — оно вернётся в композер для правки/повтора.
            // Агентские и более ранние пользовательские остаются ждать возобновления.
            for (var i = entry.Pending.Count - 1; i >= 0; i--)
            {
                if (entry.Pending[i].Kind == PendingKind.User)
                {
                    lastUser = entry.Pending[i];
                    entry.Pending.RemoveAt(i);
                    break;
                }
            }
        }
        if (hadAny) await BroadcastPendingAsync(sessionId, entry);

        // Что вернуть в композер: изъятое из очереди, иначе — снимок прерванного хода (если он
        // был пользовательским). Авто/агентский ход и пустая очередь → пустой restore.
        var restore = lastUser is not null
            ? new ComposerRestoreMessage(lastUser.Text,
                lastUser.AttachedPaths is { Count: > 0 } ? lastUser.AttachedPaths : null, lastUser.Mode)
            : entry.CurrentTurnSnapshot is { } snap
                ? new ComposerRestoreMessage(snap.Text,
                    snap.AttachedPaths is { Count: > 0 } ? snap.AttachedPaths : null, snap.Mode)
                : new ComposerRestoreMessage(null, null, null);
        await BroadcastSessionMessageAsync(sessionId, restore);
        // Snapshot гасим после доставки restore — разовый, повторный FreezePending не должен
        // формировать restore из того же snapshot
        entry.CurrentTurnSnapshot = null;
    }

    // Шов ITurnStarter (S3-2): очередь просит разбор при переходе 0→1. В переходном режиме его
    // реализует ядро; шов снимается на шаге S3-4/S3-7, когда приём хода уходит в свою роль.
    async Task Sessions.ITurnStarter.DrainNextPendingAsync(string sessionId) => await DrainNextPendingAsync(sessionId);

    // Достать следующее сообщение и отправить его обычным ходом. Вызывается по концу хода
    // (OnMessageAsync). Замороженная «Стоп» очередь не разбирается автоматически — только
    // возобновление (новое пользовательское сообщение при свободном чате) снимает заморозку.
    // Ошибка отправки не должна ронять разбор — сообщение уже снято с очереди, иначе оно
    // застряло бы навсегда и блокировало остальные.
    private async Task DrainNextPendingAsync(string sessionId)
    {
        // Окно штатной остановки: очередь осталась в памяти и новый ход не запускает
        if (_stopping) return;
        if (!_sessions.TryGetValue(sessionId, out var entry)) return;
        if (entry.QueueFrozen) return;

        QueuedMessage? next;
        var scheduleContinue = false;
        lock (entry.PendingLock)
        {
            if (entry.QueueFrozen) return;
            if (entry.Info.WorkLoop is not null)
            {
                // Цикл «до готово» активен: пользовательские сообщения продолжают цикл как
                // следующие итерации, агентские ждут конца ВСЕГО цикла. Если итерация уже
                // стартует (LoopTurnInFlight — системная директива продолжения из
                // ContinueWorkLoopAsync либо ход, извлечённый здесь же в параллельном drain),
                // очередь не трогаем: иначе два хода подряд ушли бы в один процесс. Маркер
                // LoopTurnInFlight выставляем атомарно с извлечением — тогда параллельный
                // ContinueWorkLoopAsync по result увидит его и уступит, не дублируя директиву.
                if (entry.LoopTurnInFlight) return;
                // При активном цикле подхватываем не только User, но и Report (доклад исполнителя):
                // иначе доклад пролежит до конца ВСЕГО цикла, а цикл тем временем жжёт итерации в
                // фазе waiting. Посторонние Agent-сообщения по-прежнему ждут (решение владельца
                // 2026-09-01: посторонний агент не сбивает координатора).
                next = entry.Pending.FirstOrDefault(p => p.Kind is PendingKind.User or PendingKind.Report);
                if (next is null)
                {
                    // Minor 6: при активном цикле, свободном маркере и пустой user-очереди
                    // (напр. сообщение удалили из очереди до exited прерванного хода, а result
                    // не пришёл) — цикл продолжит свою работу директивой, иначе он висел бы
                    // «активным» без движения. ContinueWorkLoopAsync пройдёт гейты сама: если
                    // параллельный запуск (по result) уже взвёл маркер — она сразу уступит.
                    scheduleContinue = true;
                }
                else
                {
                    entry.Pending.Remove(next);
                    entry.LoopTurnInFlight = true;
                }
            }
            else
            {
                // Параллельный drain уже вытащил сообщение и отправляет — уступаем, чтобы не
                // запустить второй ход. Сообщение не теряется: по result этого хода отработает
                // следующий drain (DrainInFlight к тому моменту уже погашен).
                if (entry.DrainInFlight) return;
                next = entry.Pending.FirstOrDefault();
                if (next is not null)
                {
                    entry.Pending.RemoveAt(0);
                    entry.DrainInFlight = true;
                }
            }
        }
        if (scheduleContinue)
        {
            _ = Task.Run(async () =>
            {
                try { await ContinueWorkLoopAsync(sessionId); }
                catch (Exception ex) { Console.Error.WriteLine($"[SessionManager] Продолжение цикла из drain ({sessionId}): {ex.Message}"); }
            });
            return;
        }
        if (next is null) return;

        bool delivered = false;
        try
        {
            // Бродкаст внутри try (Minor 1): исключение хаба (disposed/отвал транспорта) не
            // должно оставлять DrainInFlight взведённым навсегда и глушить разбор очереди чата
            // до перезапуска сервера — finally гарантированно погасит флаг.
            await BroadcastPendingAsync(sessionId, entry);
            delivered = await DeliverPendingAsync(sessionId, entry, next);
        }
        finally
        {
            entry.DrainInFlight = false;
            // Гейт каскада (Minor 3): перепроверяем очередь, ТОЛЬКО если предыдущая доставка
            // выполнена (delivered — не бросилась). Имя шире «ход стартовал»: для гейта важен лишь
            // факт «доставка не провалилась». При провале (CLI не поднялся) статус не ушёл в
            // Working — без гейта перепроверка вычерпала бы ВСЮ очередь подряд, теряя каждое
            // сообщение (одно потерянное — прежнее поведение, весь хвост — регрессия). Гонка
            // «result пришёл раньше finally» (Minor 2): статус уже Idle, очередь непуста —
            // перезапуск разыграет следующее сообщение. Сам DrainNextPendingAsync имеет гейт
            // DrainInFlight, так что повторный запуск безопасен (уступит, если кто-то уже взял).
            if (delivered && !entry.QueueFrozen
                && entry.Info.Status is not (SessionStatus.Working or SessionStatus.Waiting or SessionStatus.Starting))
            {
                // Minor 2: чтение Pending.Count — под PendingLock, по конвенции файла.
                bool hasPending;
                lock (entry.PendingLock) hasPending = entry.Pending.Count > 0;
                if (hasPending)
                {
                    _ = Task.Run(async () =>
                    {
                        try { await DrainNextPendingAsync(sessionId); }
                        catch (Exception ex) { Console.Error.WriteLine($"[SessionManager] Повторный разбор очереди ({sessionId}): {ex.Message}"); }
                    });
                }
            }
        }
    }

    // Флаг «уже предупредили про opt-out»: выводим ровно один раз на инстанс процесса, иначе
    // прод-настройки с потолком 0 будут сыпать Warning на каждом ходе. opt-out — исключительный
    // кейс (тесты или явное выключение защиты), в нормальной работе потолок = 8.
    private static int _awaitExitOptOutLogged;

    // Ожидание подтверждённой смерти прогона предыдущего хода перед отложенной доставкой
    // (инцидент 2026-08-10, П2-Ф1 «сериализация на смерти»). См. комментарий в DeliverPendingAsync.
    // Потолок — Delivery:AwaitProcessExitSeconds (дефолт 8): латентность старта авто-хода в
    // секунды принята владельцем как цена за устранение класса гонок с доживающим прогоном.
    // По истечении — НЕ fail-open (вернуло бы гонку) и НЕ честная ошибка (доставка нужна):
    // прерываем доживающий прогон (фоновые агенты гибнут, добиваются в следующем ходе через
    // notification — как при смене окружения) и коротко ждём его финализации.
    private async Task AwaitPreviousTurnExitAsync(string sessionId, SessionEntry entry)
    {
        var ceiling = int.TryParse(_config["Delivery:AwaitProcessExitSeconds"], out var s) && s >= 0
            ? s : 8;
        // Потолок 0 — отключить сериализацию (тесты, или явный opting-out). Иначе ждём смерти.
        // Один раз на процесс предупреждаем в лог: защита от гонок на смерти прогона выключена,
        // это исключительный кейс — в проде потолок должен быть положительным.
        if (ceiling <= 0)
        {
            if (Interlocked.Exchange(ref _awaitExitOptOutLogged, 1) == 0)
                _log.LogWarning(
                    "Delivery:AwaitProcessExitSeconds={Ceiling} — сериализация отложенной доставки выключена (opt-out)",
                    ceiling);
            return;
        }
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(ceiling);
        while (Busy(entry.Process))
        {
            if (DateTime.UtcNow >= deadline)
            {
                _log.LogInformation(
                    "Ожидание смерти прогона {Session} истекло ({Ceiling} с) — прерываю доживание ради отложенной доставки",
                    sessionId, ceiling);
                entry.Process?.Interrupt();
                // Interrupt асинхронен: прогон финализируется за секунды, даём короткий grace.
                var grace = DateTime.UtcNow + TimeSpan.FromSeconds(2);
                while (Busy(entry.Process) && DateTime.UtcNow < grace)
                    await Task.Delay(50);
                break;
            }
            await Task.Delay(50);
        }
    }

    internal static bool Busy(ILlmSessionAdapter? p) => SessionStore.Busy(p);

    // Доставка конкретного сообщения из очереди обычным ходом (без повторных гейтов очереди).
    // Пользовательское идёт со своими вложениями и режимом; агентское — как серверная отправка.
    // Возвращает true, если доставка выполнена (ход стартовал — можно ждать result); false —
    // бросилась внутри, ход не поднялся и его повторная обработка по result не придёт (Minor 3).
    private async Task<bool> DeliverPendingAsync(string sessionId, SessionEntry entry, QueuedMessage next)
    {
        // Сериализация отложенной доставки на смерти предыдущего хода (инцидент 2026-08-10, П2-Ф1):
        // статус Active ставится по result РАНЬШЕ, чем ClaudeSession отпустит _turnLock и финализирует
        // прогон. Старт нового хода в этом окне гоняется с доживающим процессом — same-process reuse
        // (Ч1), лок миграции (Ч2 — держатель .jsonl жив), interrupt свежего прогона. Ждём, пока прогон
        // умрёт (HasLiveTurn=false) И фолбэк-оркестрация снимется (OrchestrationActive=false) — тогда
        // _turnLock свободен, транзрипт закрыт, новый ход стартует свежим процессом. Только для
        // отложенной доставки (из Pending): свежий ход человека через SendDirectAsync сюда не попадает.
        await AwaitPreviousTurnExitAsync(sessionId, entry);

        // F2-O3 доработка 2 (дельта-ревью 627bd324): гонка «отзыв против уже начатой доставки».
        // Сообщение изымается из очереди под PendingLock (DrainNextPendingAsync), а доставляется
        // вне замка: в окне между изъятием и стартом хода может прийти отзыв участника.
        // StopChatsOfRevokedMember чистит очередь, но до уже изъятых сообщений не дотягивается.
        // Без перепроверки здесь стартует ход-сирота в приватном чате отозванного: его события
        // ушли бы в user_{id}, а сам автор чата его больше не видит. Перепроверяем перед стартом:
        // автор приватного чата всё ещё участник проекта. Нет — ход не начинаем, сообщение уже
        // вычерпано. Образец — «Стоп успел снять цикл — ход-сироту не шлём» (ContinueWorkLoopAsync).
        // Обычный (не приватный) чат и чат неотозванного участника не затронуты: гейт срабатывает
        // только когда автор приватного чата выведен из проекта.
        if (PrivateChatAuthorRevoked(entry))
        {
            // Ход не стартовал. При активном цикле drain выставил LoopTurnInFlight до отправки —
            // гасим, иначе цикл завис бы на мёртвой итерации (тот же финализирующий путь, что в
            // catch ниже). После отзыва цикла уже нет (StopChatsOfRevokedMember его снимает),
            // поэтому ветка доходит только до общего пути.
            if (entry.Info.WorkLoop is not null) entry.LoopTurnInFlight = false;
            return false;
        }

        try
        {
            if (next.Kind == PendingKind.User)
                // fromQueue: true — клиент рисовал призраком, бродкастим live-баллон (как auto).
                // Режим уже применён при постановке, повторно не передаём, чтобы не сбросить
                // возможную смену режима после (SetMode) — Info.Mode источник правды.
                await SendDirectAsync(sessionId, entry, next.Text,
                    next.AttachedPaths ?? [], mode: next.Mode, systemDirective: false, auto: false,
                    senderPersonaId: next.SenderPersonaId, suppressTasksExecute: next.SuppressTasksExecute,
                    senderOrigin: next.SenderOrigin, fromQueue: true, cause: DeliveryCause.QueueUser,
                    senderUserId: next.SenderUserId);
            else
                // Глубина сообщения едет в ход: агентское из очереди так же делегированное,
                // как поданное сразу (иначе chats_send, отстоявший очередь, получил бы
                // возможности хода человека)
                await SendMessageAsync(sessionId, next.Text, [], auto: true,
                    senderPersonaId: next.SenderPersonaId, senderOrigin: next.SenderOrigin,
                    suppressTasksExecute: next.SuppressTasksExecute, staffNote: next.StaffNote,
                    cause: DeliveryCause.QueueAgent, agentDepth: next.AgentDepth,
                    senderUserId: next.SenderUserId);
            return true;
        }
        catch (Exception ex)
        {
            // При активном цикле drain выставил LoopTurnInFlight=true до отправки. Если ход
            // так и не стартовал — ход-то не пришлёт result, и маркер повис бы навсегда,
            // заблокировав разбор очереди. Сбрасываем, чтобы цикл не завис на мёртвой итерации.
            if (entry.Info.WorkLoop is not null)
                entry.LoopTurnInFlight = false;
            Console.Error.WriteLine($"[SessionManager] Доставка отложенного сообщения ({sessionId}): {ex.Message}");
            return false;
        }
    }

    // F2-O3 доработка 2: автор приватного чата больше не участник проекта (или проекта нет).
    // Приватный чат виден только автору (SessionAccess.CanSee): после его отзыва сообщения и
    // ходы в этом чате некому ни показать, ни начать. Только для приватных чатов
    // (PrivateToUserId != null): общий чат принадлежит проекту и доставляется как раньше.
    private bool PrivateChatAuthorRevoked(SessionEntry entry)
    {
        if (entry.Info.PrivateToUserId is not { } author) return false;
        var project = entry.Info.ProjectId is not null ? _projects.GetById(entry.Info.ProjectId) : null;
        // Тот же признак видимости приватного чата, что у шлюзов этого файла (GetOwnedVisible,
        // VisibleTo): после вывода автора из проекта CanSee гаснет (ProjectAccess.Check = null),
        // и стартующий ход-сирота сливал бы события в user_{id} отозванного. Id автора входит
        // только в видимость его же чата; право уже реализовано RemoveAsync — здесь перечитывается
        // его следствие, нового права никто не получает.
        return !Access.SessionAccess.CanSee(entry.Info, project, author);
    }

    // Клиенту уходят только видимые элементы (silent — не призрак). Тело — в PendingQueueService (S3-2).
    private Task BroadcastPendingAsync(string sessionId, SessionEntry entry) =>
        _pending.BroadcastPendingAsync(sessionId, entry);

    private static IReadOnlyList<PendingMessageDto> VisiblePending(SessionEntry entry) =>
        PendingQueueService.VisiblePending(entry);

    // === Ожидание устройства локального проекта (ADR-016, вариант А плана §5) ===

    // Вердикт «ждать устройство» для чата: только проектный чат локального проекта, чьё
    // устройство не готово. Устройства нет вовсе (отозвано) — не ждём: ход откажет честно.
    private ProjectBackgroundGate? DeviceWaitGate(SessionEntry entry)
    {
        if (_deviceGate is null || entry.Info.ProjectId is not { } pid) return null;
        if (_projects.GetById(pid) is not { } project) return null;
        var gate = _deviceGate.Check(project);
        return gate.MustWait ? gate : null;
    }

    // Парковка на сессии: список заменяется целиком (копия с добавлением) — SaveSessions
    // сериализует Info без лока очереди и не должен застать список посреди правки. Дедуп
    // агентских — как у Pending: наивный ретрай агента не множит одинаковые ходы.
    private async Task<SendAndWaitResult> ParkForDeviceAsync(string sessionId, SessionEntry entry,
        ProjectBackgroundGate gate, DeviceWaitMessage message)
    {
        int position;
        lock (entry.PendingLock)
        {
            var queue = entry.Info.DeviceWaitQueue ?? [];
            if (message.Route == DeviceWaitMessage.RouteAgent
                && queue.Any(m => m.Route == DeviceWaitMessage.RouteAgent && m.Text == message.Text
                    && m.SenderPersonaId == message.SenderPersonaId))
                return new SendAndWaitResult.Queued(queue.Count, Duplicate: true);
            entry.Info.DeviceWaitQueue = [.. queue, message];
            position = entry.Info.DeviceWaitQueue.Count;
        }
        SaveSessions();
        await BroadcastPendingAsync(sessionId, entry);
        _log.LogInformation("Чат {Session}: сообщение ждёт устройство {Device} ({Reason}), в ожидании {Count}",
            sessionId, gate.DeviceId, gate.Reason, position);
        return new SendAndWaitResult.Queued(position, Duplicate: false);
    }

    /// <summary>Чаты, у которых есть сообщения, ждущие устройство.</summary>
    public IReadOnlyList<Session> GetDeviceWaitingSessions() =>
        [.. _sessions.Values.Select(e => e.Info).Where(i => i.DeviceWaitQueue is { Count: > 0 })];

    /// <summary>
    /// Устройство готово: ждавшие сообщения встают в голову обычной очереди (порядок прихода
    /// сохраняется) и разбираются штатным drain — первое сразу, если чат свободен, остальные
    /// по концу хода. Возвращает число выпущенных сообщений.
    /// </summary>
    public async Task<int> ReleaseDeviceWaitAsync(string sessionId)
    {
        if (!_sessions.TryGetValue(sessionId, out var entry)) return 0;
        List<DeviceWaitMessage> released;
        bool dispatchNow;
        lock (entry.PendingLock)
        {
            released = entry.Info.DeviceWaitQueue ?? [];
            if (released.Count == 0) return 0;
            entry.Info.DeviceWaitQueue = null;
            entry.Pending.InsertRange(0, released.Select(m => new QueuedMessage(
                m.Id, m.Text, m.SenderPersonaId, m.SenderOrigin, m.AgentDepth, m.QueuedAt,
                SuppressTasksExecute: m.SuppressTasksExecute, SenderChatName: m.SenderChatName,
                Kind: m.Route == DeviceWaitMessage.RouteUser ? PendingKind.User : PendingKind.Agent,
                AttachedPaths: m.AttachedPaths, Mode: m.Mode, StaffNote: m.StaffNote,
                SenderUserId: m.SenderUserId)));
            dispatchNow = !entry.QueueFrozen
                && entry.Info.Status is not (SessionStatus.Working or SessionStatus.Waiting)
                && entry.Process?.OrchestrationActive != true;
        }
        SaveSessions();
        await BroadcastPendingAsync(sessionId, entry);
        _log.LogInformation("Чат {Session}: устройство готово, в работу {Count} ждавших сообщений", sessionId, released.Count);
        if (dispatchNow) _ = Task.Run(() => DrainNextPendingAsync(sessionId));
        return released.Count;
    }

    /// <summary>
    /// Снять сообщения, ждущие устройство дольше потолка (поставлены раньше <paramref name="olderThanUtc"/>).
    /// Возвращает снятые — уведомление владельцу шлёт вызывающий.
    /// </summary>
    public async Task<IReadOnlyList<DeviceWaitMessage>> ExpireDeviceWaitAsync(string sessionId, DateTime olderThanUtc)
    {
        if (!_sessions.TryGetValue(sessionId, out var entry)) return [];
        List<DeviceWaitMessage> expired;
        lock (entry.PendingLock)
        {
            var queue = entry.Info.DeviceWaitQueue ?? [];
            expired = [.. queue.Where(m => m.QueuedAt <= olderThanUtc)];
            if (expired.Count == 0) return [];
            var rest = queue.Where(m => m.QueuedAt > olderThanUtc).ToList();
            entry.Info.DeviceWaitQueue = rest.Count > 0 ? rest : null;
        }
        SaveSessions();
        await BroadcastPendingAsync(sessionId, entry);
        return expired;
    }

    // Есть ли в очереди сообщение, продолжающее цикл (User/Report). Тело — в PendingQueueService (S3-2).
    private static bool HasContinuingPending(SessionEntry entry) => PendingQueueService.HasContinuingPending(entry);

    private static bool HasPending(SessionEntry entry) => PendingQueueService.HasPending(entry);

    // Снимок для replay при JoinSession (без служебных)
    public IReadOnlyList<PendingMessageDto> GetVisiblePending(string sessionId) => _pending.GetVisiblePending(sessionId);

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

        // Метка «идёт компакция» — её читает PreemptForPending: прерывать компакцию нельзя,
        // её exited глотает обёртка фолбэка. Гасится на конце хода в OnMessageAsync — но
        // только терминалом ЭТОГО прогона, поэтому и метка хранит его идентификатор.
        // Ход не стартовал (адаптер отвалился на запуске compact) — снимаем сразу: терминала
        // не будет, а повисшая метка запретила бы перебой в этом чате навсегда.
        entry.CompactRun = entry.RunId;
        try { await entry.Process!.CompactAsync(); }
        catch { entry.CompactRun = 0; throw; }
    }

    // После перезапуска сервера Process может быть null — восстанавливаем сессию
    // Создание/переиспользование аккумулятора истории (без CLI-процесса). Вырезано из
    // EnsureProcessCoreAsync: локальному голосовому ходу (chat-voice) процесс не нужен,
    // а аккумулятор — да (реплики разговора пишутся в ту же историю). Ключ: ClaudeSessionId
    // чата, а при его отсутствии (ходов CLI ещё не было) — id чата: локальные ходы пишут
    // историю в data/sessions/{id чата}/history.json, и после рестарта сервера, и при
    // возврате на CLI она подхватывается отсюда же.
    private async Task EnsureAccumulatorAsync(SessionEntry entry)
    {
        if (entry.Accumulator is not null) return;
        // Оживление под _falPersistLock — сериализуем с прямой записью fal-стоимости в
        // историю неактивной сессии (PublishFalCostAsync): иначе LoadAsync тут и запись там
        // теряли бы друг друга (lost update). Повторная проверка под локом.
        await WithFalPersistLockAsync(async () =>
        {
            if (entry.Accumulator is not null) return;
            // Ключ сохранения — тот же, что у чтения: до первого ответа агента это id чата, иначе
            // внеходовая запись в свежий чат (AppendStoredAsync) не легла бы на диск вовсе.
            // Ответ CLI переключит ключ на ClaudeSessionId (SessionStartedMessage → SetSaveKey)
            var key = HistoryKeyOf(entry.Info);
            var existingHistory = await _history.LoadAsync(key);
            entry.Accumulator = new TurnAccumulator(existingHistory, key);
        });
    }

    private async Task EnsureProcessAsync(string sessionId, SessionEntry entry)
    {
        // Сериализуем весь check-then-act под per-entry локом: иначе два конкурентных хода
        // создали бы два адаптера одной сессии (см. EnsureLock).
        await entry.EnsureLock.WaitAsync();
        try
        {
            await EnsureProcessCoreAsync(sessionId, entry);
        }
        finally { entry.EnsureLock.Release(); }
    }

    private async Task EnsureProcessCoreAsync(string sessionId, SessionEntry entry)
    {
        // Отложенная уборка устаревшего адаптера (смена собеседника/правка персоны):
        // дожидаемся dispose ДО старта нового — иначе старый процесс ещё умирает,
        // когда новый уже поднялся с --resume того же транскрипта
        if (entry.AdapterStale && entry.Process is { } stale)
        {
            entry.AdapterStale = false;
            entry.Process = null;
            try { await stale.DisposeAsync(); }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[SessionManager] Уборка устаревшего адаптера ({sessionId}): {ex.Message}");
            }
        }
        if (entry.Process is not null) return;

        // Переиспускаем существующий in-memory аккумулятор (процесс мог быть сброшен
        // сменой собеседника — SwitchSpeaker; его состояние, включая ещё не сохранённые
        // внеходовые карточки конвейера/совещания, нельзя терять). Новый создаём только
        // при ленивом восстановлении сессии после рестарта сервера (Accumulator == null).
        await EnsureAccumulatorAsync(entry);
        var accumulator = entry.Accumulator!;

        // Чат вне проекта — рабочая папка Chats, без проектного промпта и правил;
        // проектная сессия — RootPath/SystemPrompt/PermissionRules из проекта.
        // Персона-слой восстанавливаем так же, как при первом старте (иначе после рестарта
        // сервера персонная сессия теряла бы характер и долгую память).
        // Идентификатор прогона — как в StartNewSessionAsync (см. SessionEntry.RunId)
        var runId = Interlocked.Increment(ref _runSeq);

        LlmSessionContext context;
        if (entry.Info.ProjectId is null)
        {
            var rootPath = ResolveChatRoot(entry.Info.OwnerId
                ?? throw new InvalidOperationException("У чата не задан владелец"));
            var persona = BuildPersonaLayer(entry.Info, entry.Info.OwnerId);
            var workspace = BuildWorkspaceContext(entry.Info.OwnerId, null, entry.Info.Id, persona.Persona);
            var widgetsMcp = BuildWidgetsContext(entry.Info.OwnerId, persona.Persona);
            var watchMcp = BuildWatchContext(entry.Info.OwnerId);
            var webSearchMcp = BuildWebSearchContext(entry.Info.OwnerId, persona.Persona);
            var higgsfieldMcp = BuildHiggsfieldContext(entry.Info.OwnerId, persona.Persona);
            var imageEditorMcp = BuildImageEditorContext(entry.Info.OwnerId, entry.Info);
            var audioEditorMcp = BuildAudioEditorContext(entry.Info.OwnerId, entry.Info);
            var videoEditorMcp = BuildVideoEditorContext(entry.Info.OwnerId, entry.Info);
            var pluginsMcp = BuildPluginsContext(entry.Info.OwnerId, entry.Info);
            var turnContextMcp = BuildTurnContextContext(entry.Info.OwnerId, entry.Info);
            var tasksMcp = TasksMcpEnabled(entry.Info.OwnerId, entry.Info, persona.Persona)
                ? BuildTasksContext(entry.Info.OwnerId, null, persona.Persona) : null;
            var notesMcp = _bindings.EffectiveToolEnabled(entry.Info.OwnerId, persona.Persona, "notes")
                ? BuildNotesContext(entry.Info.OwnerId, null, persona.Persona) : null;
            var personasMcp = BuildPersonasContext(entry.Info.OwnerId, null, entry.Info, persona.Persona);
            var notificationsMcp = BuildNotificationsContext(entry.Info.OwnerId, entry.Info.PersonaId, persona.Persona);
            var difyMcp = BuildDifyContext(entry.Info.OwnerId);
            context = new LlmSessionContext(rootPath,
                msg => OnMessageQueuedAsync(sessionId, accumulator, msg, runId),
                RawSystemPrompt: null, BuiltInSystemPrompt: ProjectManager.BuiltInSystemPrompt,
                PermissionRules: null,
                ContentRootPath: AppContext.BaseDirectory,
                DataDir: _dataDir,
                TasksMcp: tasksMcp,
                OwnerId: entry.Info.OwnerId,
                NotesMcp: notesMcp,
                PersonaProvider: BuildPersonaProvider(entry.Info, entry.Info.OwnerId),
                MemoryMcp: persona.Memory,
                ExtraDisallowedTools: BuildExtraDisallowed(entry.Info.OwnerId, persona.Persona, entry.Info),
                PersonasMcp: personasMcp,
                NotificationsMcp: notificationsMcp,
                WorkspaceMcp: workspace,
                PersonaAgentsProvider: BuildPersonaAgentsProvider(entry.Info.OwnerId, entry.Info, persona.Persona),
                Launcher: _launchers.ForOwner(entry.Info.OwnerId),
                ModulesMcp: BuildModulesContext(entry.Info.OwnerId),
                WidgetsMcp: widgetsMcp,
                // Чат вне проекта — графа кода нет (он ключуется проектом)
                CodeGraphMcp: null,
                DifyMcp: difyMcp,
                BrowserEnabled: BrowserEnabled(entry.Info.OwnerId, persona.Persona),
                SystemPromptViaFile: SystemPromptViaFile,
                CliConfigRoot: ConfigRootFor(entry.Info.OwnerId, entry.Info.Provider),
                ExternalMcpProvider: BuildExternalMcpProvider(entry.Info.OwnerId, null, persona.Persona, entry.Info.Id),
                PersistSessions: SaveSessions,
                EnqueueBypass: BuildEnqueueBypass(sessionId),
                OrchestrationDone: BuildOrchestrationDone(sessionId),
                HttpMcpActive: HttpMcpActive(widgetsMcp, persona.Memory, tasksMcp, notesMcp, personasMcp,
                    workspace, notificationsMcp, dify: difyMcp, watch: watchMcp, webSearch: webSearchMcp,
                    higgsfield: higgsfieldMcp, imageEditor: imageEditorMcp, audioEditor: audioEditorMcp, videoEditor: videoEditorMcp,
                    turnContext: turnContextMcp, plugins: pluginsMcp),
                HttpMcpEnabledProvider: HttpMcpEnabled,
                Events: _turnEvents,
                WatchMcp: watchMcp,
                WebSearchMcp: webSearchMcp,
                HiggsfieldMcp: higgsfieldMcp,
                ImageEditorMcp: imageEditorMcp,
                AudioEditorMcp: audioEditorMcp,
                VideoEditorMcp: videoEditorMcp,
                PluginsMcp: pluginsMcp,
                TurnContextMcp: turnContextMcp,
                // Чат вне проекта — fallback для slice графа не применяется (граф ключуется проектом)
                MainRootPath: null,
                LlmGatewayApiUrl: LlmGatewayApiUrlFor(entry.Info, entry.Info.OwnerId),
                // ADR-031, шаг 2.1: каталоги плагинов для --plugin-dir (опционально);
                // чат вне проекта — локальности нет (все включённые пакеты владельца).
                PluginDirSource: _pluginDirSource,
                Project: null,
                FrameSource: this);
                // Чат вне проекта: трейлер CCS-Session в подсказке досье (DossierTrailerContributor) пропускается
        }
        else
        {
            var project = _projects.GetById(entry.Info.ProjectId)
                ?? throw new InvalidOperationException("Проект не найден");
            var persona = BuildPersonaLayer(entry.Info, project.OwnerId);
            var workspace = BuildWorkspaceContext(project.OwnerId, project.Id, entry.Info.Id, persona.Persona);
            // Корень проекта запоминаем ДО EffectiveRoot: у worktree-чата это и есть fallback
            // для slice графа (ADR-003). Совпадение с rootPath — чат без worktree, fallback
            // сводится к no-op в CodeGraphPromptProvider.GetSliceAsync.
            var projectRoot = project.RootPath;
            var rootPath = EffectiveRoot(entry.Info, projectRoot);
            var widgetsMcp = BuildWidgetsContext(project.OwnerId, persona.Persona);
            var watchMcp = BuildWatchContext(project.OwnerId);
            var webSearchMcp = BuildWebSearchContext(project.OwnerId, persona.Persona);
            var higgsfieldMcp = BuildHiggsfieldContext(project.OwnerId, persona.Persona);
            var imageEditorMcp = BuildImageEditorContext(project.OwnerId, entry.Info);
            var audioEditorMcp = BuildAudioEditorContext(project.OwnerId, entry.Info);
            var videoEditorMcp = BuildVideoEditorContext(project.OwnerId, entry.Info);
            var pluginsMcp = BuildPluginsContext(project.OwnerId, entry.Info);
            var turnContextMcp = BuildTurnContextContext(project.OwnerId, entry.Info);
            var localMediaMcp = BuildLocalMediaContext(project.OwnerId, project.Id, persona.Persona);
            var testsMcp = BuildTestsContext(project.OwnerId, project.Id, persona.Persona);
            var memoryMcp = persona.Memory ?? BuildTeamMemoryContext(project.OwnerId, project.Id);
            var tasksMcp = TasksMcpEnabled(project.OwnerId, entry.Info, persona.Persona)
                ? BuildTasksContext(project.OwnerId, project.Id, persona.Persona) : null;
            var notesMcp = _bindings.EffectiveToolEnabled(project.OwnerId, persona.Persona, "notes")
                ? BuildNotesContext(project.OwnerId, project.Id, persona.Persona) : null;
            var personasMcp = BuildPersonasContext(project.OwnerId, project.Id, entry.Info, persona.Persona);
            var notificationsMcp = BuildNotificationsContext(project.OwnerId, entry.Info.PersonaId, persona.Persona);
            var codeGraphMcp = BuildCodeGraphContext(project.OwnerId, project.Id, entry.Info.Id, rootPath, persona.Persona);
            var architectureMcp = BuildArchitectureContext(project.OwnerId, project.Id, persona.Persona);
            var difyMcp = BuildDifyContext(project.OwnerId);
            var hands = HandsFor(project.Id);
            context = new LlmSessionContext(rootPath,
                msg => OnMessageQueuedAsync(sessionId, accumulator, msg, runId),
                project.SystemPrompt,
                ProjectManager.BuiltInSystemPrompt,
                () => _projects.GetById(entry.Info.ProjectId!)?.PermissionRules ?? (IReadOnlyList<PermissionRule>)Array.Empty<PermissionRule>(),
                ContentRootPath: AppContext.BaseDirectory,
                DataDir: _dataDir,
                TasksMcp: tasksMcp,
                OwnerId: project.OwnerId,
                NotesMcp: notesMcp,
                PersonaProvider: BuildPersonaProvider(entry.Info, project.OwnerId),
                MemoryMcp: memoryMcp,
                ExtraDisallowedTools: BuildExtraDisallowed(project.OwnerId, persona.Persona, entry.Info),
                PersonasMcp: personasMcp,
                NotificationsMcp: notificationsMcp,
                WorkspaceMcp: workspace,
                PersonaAgentsProvider: BuildPersonaAgentsProvider(project.OwnerId, entry.Info, persona.Persona),
                Launcher: _launchers.ForProject(project),
                ModulesMcp: BuildModulesContext(project.OwnerId),
                WidgetsMcp: widgetsMcp,
                CodeGraphMcp: codeGraphMcp,
                DifyMcp: difyMcp,
                BrowserEnabled: BrowserEnabled(project.OwnerId, persona.Persona),
                SystemPromptViaFile: SystemPromptViaFile,
                CliConfigRoot: ConfigRootFor(project.OwnerId, entry.Info.Provider),
                ExternalMcpProvider: BuildExternalMcpProvider(project.OwnerId, project.Id, persona.Persona, entry.Info.Id),
                PersistSessions: SaveSessions,
                EnqueueBypass: BuildEnqueueBypass(sessionId),
                OrchestrationDone: BuildOrchestrationDone(sessionId),
                HttpMcpActive: HttpMcpActive(widgetsMcp, memoryMcp, tasksMcp, notesMcp, personasMcp,
                    workspace, notificationsMcp, codeGraphMcp, difyMcp, watchMcp, webSearchMcp, higgsfieldMcp,
                    imageEditorMcp, localMediaMcp, architectureMcp, audioEditorMcp, videoEditorMcp, turnContextMcp, testsMcp,
                    pluginsMcp),
                HttpMcpEnabledProvider: HttpMcpEnabled,
                ArchitectureMcp: architectureMcp,
                ChatContextProvider: BuildChatContextProvider(sessionId),
                Events: _turnEvents,
                WatchMcp: watchMcp,
                WebSearchMcp: webSearchMcp,
                HiggsfieldMcp: higgsfieldMcp,
                ImageEditorMcp: imageEditorMcp,
                AudioEditorMcp: audioEditorMcp,
                VideoEditorMcp: videoEditorMcp,
                PluginsMcp: pluginsMcp,
                TurnContextMcp: turnContextMcp,
                LocalMediaMcp: localMediaMcp,
                TestsMcp: testsMcp,
                // Корень ГЛАВНОЙ ветки проекта — fallback для slice графа кода, пока свой граф
                // worktree-ветки не построен (ADR-003).
                MainRootPath: projectRoot,
                ServerContent: ProjectCapabilities.ServerContentEnabled(project),
                TranscriptOnServer: ProjectCapabilities.TranscriptOnServer(project),
                HandsEnabled: hands,
                LlmGatewayApiUrl: LlmGatewayApiUrlFor(entry.Info, project.OwnerId),
                // ADR-031, шаг 2.1: каталоги плагинов для --plugin-dir (опционально) и проект
                // для проверки локальности (у локального проекта в MVP плагинов нет).
                PluginDirSource: _pluginDirSource,
                Project: project,
                FrameSource: this);
        }
        var adapter = _adapters.Create(entry.Info, context);
        entry.Process = adapter;
        entry.RunId = runId;
        await adapter.StartAsync();
    }

    // Именование чата (S3-3): авто-заголовок и его уточнение — тело в ChatNaming. Обёртки
    // сохранены и как точка вызова (создание/приём хода ещё живут здесь), и как место,
    // на которое смотрит белый тест MakeChatTitle (SessionManagerTestBase, рефлексия).
    private static string MakeChatTitle(string text) => ChatNaming.MakeChatTitle(text);

    private Task RefineChatTitleAsync(string sessionId, string firstMessage, string expectedTitle, string? ownerId)
        => _naming.RefineChatTitleAsync(sessionId, firstMessage, expectedTitle, ownerId);

    // Именование чата (S3-3): явное переименование и значки тем — тела в ChatNaming;
    // здесь тонкие обёртки с прежними сигнатурами (их зовут контроллеры и тесты).
    public Task<Session?> RetitleAsync(string userId, string sessionId, CancellationToken ct)
        => _naming.RetitleAsync(userId, sessionId, ct);

    public Task<Session?> SetChatIconAsync(string userId, string sessionId, CancellationToken ct)
        => _naming.SetChatIconAsync(userId, sessionId, ct);

    // Результат пакетного прогона значков: сколько чатов получили иконку и сколько пропущено
    // (значок уже стоит, нет переписки, модель не дала имя). Идёт в тост палитры.
    // Тип — часть контракта фасада (его читает контроллер), остался здесь; тело прогона —
    // в ChatNaming (обращение через alias).
    public sealed record IconBatchResult(int Processed, int Skipped);

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

    // Ответ на карточку, которой уже нет, — протухший: конец хода (result/error/exited) снял её
    // сам. Гонка живая: ватчдог обрывает зависший ход, а клик пользователя долетает мгновением
    // позже — раньше такой ответ безусловно ставил Working на мёртвом процессе, и чат залипал
    // в нём навсегда (новые сообщения уходят в Pending, разбор которой ждёт конца хода, а хода
    // уже не будет). Сверяем только наличие карточки, но не id: у одного хода их может быть
    // несколько (параллельные tool_use ждут каждый своего ответа), и ответ на неактуальную
    // из них — законный.
    private static bool IsStaleInteractionAnswer(string sessionId, SessionEntry entry, string what)
    {
        if (entry.PendingInteraction is not null) return false;
        Console.Error.WriteLine(
            $"[SessionManager] Протухший ответ ({what}) в сессии {sessionId}: карточки уже нет — игнорируем");
        return true;
    }

    // Статус после ответа на карточку. Внутри хода — Working: ход продолжается, закроет его
    // result. Карточка вне хода (фоновый агент, ход-продолжение после result) — чат возвращается
    // в свободный Active, как до карточки: result такой работы до SessionManager не доходит
    // (ClaudeSession отбрасывает его как «между ходами»), и Working не снял бы никто. Метка
    // LastTurnEndedAt — как у result: sweep доведёт до Finished, когда фоновая работа кончится.
    private void ResumeAfterInteraction(string sessionId, SessionEntry entry, string what)
    {
        var outsideTurn = entry.InteractionOutsideTurn && entry.Info.Status == SessionStatus.Waiting;
        entry.InteractionOutsideTurn = false;
        var status = outsideTurn ? SessionStatus.Active : SessionStatus.Working;
        if (outsideTurn)
            lock (entry.PendingLock) entry.LastTurnEndedAt = DateTimeOffset.UtcNow;
        FireAndForget(ApplyStatusAsync(sessionId, entry, status), $"смена статуса после {what} ({sessionId})");
    }

    public void RespondPermission(string sessionId, string requestId, string behavior)
    {
        if (!_sessions.TryGetValue(sessionId, out var entry)) return;
        if (IsStaleInteractionAnswer(sessionId, entry, $"permission {requestId}")) return;
        // «Разрешать всегда» запоминаем НА СЕССИИ, а не в памяти адаптера: тот пересоздаётся
        // рестартом сервера, ленивым восстановлением чата и сменой собеседника, и человеку
        // приходилось жать «всегда» заново. Имя инструмента берём из висящей карточки —
        // в ответе клиента его нет. Сверяем requestId: у одного хода карточек может быть
        // несколько (параллельные tool_use), и ответ на неактуальную законен — но запомнить
        // «всегда» по чужой карточке значило бы выдать бессрочные права инструменту, которому
        // их не давали. Карточка не та (или имя пустое) — просто не запоминаем, ход идёт
        // своим чередом.
        if (behavior == "allow_always"
            && entry.PendingInteraction is PermissionRequestMessage
                { ToolName: { Length: > 0 } tool } pending
            && pending.RequestId == requestId
            && !entry.Info.AutoAllowTools.Contains(tool, StringComparer.OrdinalIgnoreCase))
        {
            entry.Info.AutoAllowTools.Add(tool);
            SaveSessions();
        }
        entry.Process?.RespondPermission(requestId, behavior);
        entry.PendingInteraction = null;
        // Вердикт — в тех же токенах, что хранит карточка на фронте (ChatItem.decision)
        var decision = behavior switch
        {
            "allow" => "allowed",
            "allow_always" => "always",
            _ => "denied",
        };
        FireAndForget(BroadcastAsync(sessionId,
                new InteractionResolvedMessage("permission", requestId, Decision: decision)),
            $"рассылка ответа на permission ({sessionId})");
        ResumeAfterInteraction(sessionId, entry, "permission");
    }

    // Снять инструмент с «Разрешать всегда» этого чата: следующий его вызов снова спросит.
    // Идемпотентно — инструмента в списке нет, отдаём сессию как есть (диск не трогаем).
    // Сравнение имён — OrdinalIgnoreCase, как при проверке в ClaudeSession.
    // null — сессии нет (владение проверяет контроллер, как у соседних эндпоинтов).
    // UpdatedAt не двигаем: настройка чата — не активность (см. соглашение о настройках).
    public Session? RemoveAutoAllowTool(string sessionId, string tool)
    {
        if (!_sessions.TryGetValue(sessionId, out var entry)) return null;
        if (entry.Info.AutoAllowTools.RemoveAll(t => string.Equals(t, tool, StringComparison.OrdinalIgnoreCase)) > 0)
            SaveSessions();
        return entry.Info;
    }

    // «Стоп» человека (хаб, доска агентов). Штаб останавливает исполнителя через
    // ITeamTurnIntake.InterruptTurn — та же механика, но без отметки в истории.
    public void Interrupt(string sessionId) => InterruptCore(sessionId, byUser: true);

    private void InterruptCore(string sessionId, bool byUser)
    {
        if (_sessions.TryGetValue(sessionId, out var entry))
        {
            // Живой локальный голосовой ход: отменяем и выходим ДО stuck-детекта и
            // заморозки очереди — exited придёт из самой ветки (RunLocalVoiceTurnAsync),
            // вернёт статус Active и разберёт очередь (drainOnDeadRun). Ход короткий
            // (1-3 с), composer_restore для него не нужен.
            if (entry.LocalVoiceCts is { } localCts)
            {
                if (byUser) RecordUserInterrupt(sessionId, entry);
                localCts.Cancel();
                return;
            }

            // Стоп пользователя прерывает и цикл «до готово»: снимаем СИНХРОННО,
            // чтобы exited прерванного хода не запустил автопродолжение
            if (entry.Info.WorkLoop is not null)
            {
                entry.Info.WorkLoop = null;
                // Прерванный ход result не пришлёт — погасим маркер итерации, иначе он
                // повис бы и заблокировал разбор очереди по концу следующего хода.
                entry.LoopTurnInFlight = false;
                SaveSessions();
                _ = BroadcastWorkLoopAsync(sessionId, entry);
            }
            // Чат числится занятым, а живого прогона нет: ход убил ватчдог/сбой, а статус
            // остался (или его выставил протухший ответ на карточку). Такой Working терминален —
            // сообщения уходят в Pending, разбор которой ждёт конца несуществующего хода.
            // «Стоп» — единственная кнопка пользователя в этом состоянии, поэтому вместо
            // прежнего no-op реанимируем чат (ниже, после общей уборки состояния хода).
            // HasQueuedTurn обязателен наравне с HasLiveTurn: прогон появляется только после
            // старта процесса CLI (секунды), и «Стоп», нажатый сразу после отправки, попадал
            // ровно в это окно — чат объявлялся зависшим, адаптер выбрасывался из-под живого
            // хода, и тот падал ObjectDisposedException'ом в ленту (диагноз 2026-08-15).
            var stuck = entry.Info.Status is SessionStatus.Working or SessionStatus.Waiting
                && entry.Process is null or { HasLiveTurn: false, HasQueuedTurn: false };
            // Отметка в историю — только когда было что останавливать (чат занят, в том числе
            // зависший: человек нажал «Стоп» и видит отметку в живой ленте)
            if (byUser && entry.Info.Status is SessionStatus.Working or SessionStatus.Waiting)
            {
                RecordUserInterrupt(sessionId, entry);
                // Пометка модели — только когда прерывается живой ход: у зависшего чата ход уже
                // мёртв, и «Стоп» лишь реанимирует его
                if (!stuck) entry.UserStopNote = true;
            }
            // «Стоп» замораживает очередь (не чистит): сообщения остаются ждать возобновления,
            // а последнее пользовательское возвращается в композер (composer_restore).
            // При реанимации не замораживаем: размораживающего конца хода уже не будет,
            // и отложенные сообщения застряли бы в очереди насовсем.
            if (!stuck)
                _ = FreezePendingAsync(sessionId, entry);
            // M5: тот же сброс буфера маркеров, что при прерывании очередью (SendMessageAsync):
            // убитый ход даёт exited без result, буфер не потребляется — маркер мёртвого хода
            // (<escalate:*>, <team:work>, <no-reply/>) доклеился бы к следующему и применился
            // задним числом (фантомная эскалация, «волна-призрак»).
            lock (entry.TeamTurnLock)
            {
                entry.TeamTurnText.Clear();
                entry.TeamTurnShownLength = 0;
                entry.TurnSawAngleBracket = false;
                entry.TeamTurnAsked = false;
            }
            if (stuck)
                ReviveStuckSession(sessionId, entry);
            else
            {
                _log.LogInformation("Interrupt адаптера {Session}: callsite=stop (Interrupt(sessionId), stuck={Stuck})", sessionId, stuck);
                entry.Process?.Interrupt();
            }
        }
    }

    /// <summary>
    /// F2-O3 (финальное ревью «Общих проектов»): отзыв участника гасит ходы и очищает очередь
    /// его приватных чатов в этом проекте. Приватный чат виден только автору (SessionAccess.CanSee),
    /// а автор после отзыва проекта не видит: стоящие в очереди сообщения ждали бы хода, который
    /// некому начать, а цикл «до готово» жёг бы итерации, которых никто не увидит. В отличие от
    /// «Стопа»: очередь ЧИСТИТСЯ, а не морозится — заморозка ждёт возобновления, которого не
    /// будет; отметки человека и composer_restore нет: это не действие человека. Область — ровно
    /// приватные чаты отозванного (PrivateToUserId): ходы общих чатов принадлежат проекту и
    /// продолжаются для владельца.
    /// </summary>
    public void StopChatsOfRevokedMember(string projectId, string memberUserId)
    {
        foreach (var session in GetByProject(projectId))
        {
            if (session.PrivateToUserId != memberUserId) continue;
            if (!_sessions.TryGetValue(session.Id, out var entry)) continue;
            // Остановка хода — той же механикой, что «Стоп» (byUser: false — как штабной
            // InterruptTurn, без отметки в истории): цикл «до готово» снимается синхронно, чтобы
            // exited не запустил автопродолжение; живой ход прерывается; «зависший» чат
            // (статус занят, хода нет) реанимируется — иначе остался бы занятым навсегда.
            // Заморозку очереди InterruptCore снимает ниже этот метод: здесь очередь чистится.
            InterruptCore(session.Id, byUser: false);
            lock (entry.PendingLock) entry.Pending.Clear();
            entry.QueueFrozen = false;
            PersistPending(entry);
        }
    }

    // Отметка «Ход остановлен пользователем» в history.json. Пишется ТОЛЬКО на прерывании
    // человеком — в точке, где его намерение известно наверняка: ниже по стеку (адаптер,
    // паспорт хода с исходом interrupted) «Стоп» человека уже не отличить от остановки
    // исполнителя штабом, а падение процесса и внутренние перезапуски хода эту точку не
    // проходят вовсе. Живая лента ставит такую же отметку сама, оптимистично.
    private void RecordUserInterrupt(string sessionId, SessionEntry entry)
    {
        if (entry.Accumulator is not { } acc || !acc.OnUserInterrupted()) return;
        FireAndForget(acc.SaveSnapshotAsync(_history), $"отметка прерывания хода ({sessionId})");
    }

    // Возврат зависшего чата в рабочее состояние: снимаем ожидающую карточку, выбрасываем
    // отравленный адаптер и переводим статус в Active. Просто сбросить статус мало — у адаптера
    // мог остаться захваченный зависшей финализацией _turnLock, и первое же сообщение встало бы
    // намертво снова; DisposeAsync (в нём _cts.Cancel) разблокирует ожидателей. Следующий ход
    // поднимет свежий адаптер через EnsureProcessAsync с --resume по ClaudeSessionId —
    // контекст переписки цел.
    private void ReviveStuckSession(string sessionId, SessionEntry entry)
    {
        entry.PendingInteraction = null;
        if (entry.Process is { } dead)
        {
            entry.Process = null;
            FireAndForget(dead.DisposeAsync().AsTask(),
                $"уборка адаптера зависшего хода ({sessionId})");
        }
        FireAndForget(ReviveStuckSessionAsync(sessionId, entry),
            $"реанимация зависшего чата ({sessionId})");
    }

    // Гасит процесс CLI сессии-исполнителя ЗАКРЫТОЙ задачи вместе с деревом (фоновые Bash,
    // агенты, Monitor) — иначе поздний task-notification будит завершённого агента новым ходом
    // (инцидент 13.10). Зовётся из join-а R+D (TaskExecutionService.TryDeliverCompletionAsync)
    // ровно один раз: после CAS доставки. Только для сессии-исполнителя этой задачи — обычные
    // чаты держат процесс ради HasPendingBg/продолжений как раньше. Идёт через уборку адаптера
    // (DisposeAsync → Kill всего дерева), как ReviveStuckSession: статус и UpdatedAt не трогаем —
    // ExitedMessage переведёт Active→Finished штатно, без сдвига UpdatedAt. Идущий ход
    // (Starting/Working/Waiting) не рвём: гасить рано — по его концу ход гасит повторный R
    // (TaskExecutionService.OnSessionMessageAsync, ветка ResultMessage), когда статус уже не Working.
    // Возвращает true, если процесс был и снят.
    internal bool StopClosedExecutorProcess(string sessionId, string taskId)
    {
        if (!_sessions.TryGetValue(sessionId, out var entry)) return false;
        if (!entry.Info.TaskExecution || entry.Info.TaskId != taskId) return false;
        if (entry.Info.Status is SessionStatus.Starting or SessionStatus.Working or SessionStatus.Waiting)
            return false;
        if (entry.Process is not { } proc) return false;
        entry.Process = null;
        _log.LogInformation("Исполнитель закрытой задачи {TaskId}: гашу процесс CLI сессии {Session} с фоновой работой",
            taskId, sessionId);
        FireAndForget(proc.DisposeAsync().AsTask(), $"гашение процесса исполнителя закрытой задачи ({sessionId})");
        return true;
    }

    private async Task ReviveStuckSessionAsync(string sessionId, SessionEntry entry)
    {
        await ApplyStatusAsync(sessionId, entry, SessionStatus.Active);
        await AddWorkLoopStoppedNoticeAsync(sessionId, entry, "stuck_reset",
            "Зависший ход сброшен — чат снова доступен.");
    }

    // === КР-наблюдаемость, этап 3: перезапуск хода штаба без потери работы ===

    // Повреждённый транскрипт: resume запрещён, человеку предлагаем начать ход заново.
    // Отдельный тип, а не текст в InvalidOperationException: контроллер различает отказ
    // гейта (просто текст) и этот случай (code=transcript_damaged + кнопка «начать заново»).
    public sealed class TurnTranscriptDamagedException(string message) : InvalidOperationException(message);

    // Результат перезапуска хода. Resumed=true — следующий ход продолжит разговор по
    // транскрипту (--resume); false — ход начнётся заново без старого контекста (startFresh).
    public sealed record StuckTurnRestartResult(bool Resumed, string Message);

    // Ожидание смерти процесса после kill: Kill асинхронен, и «вызвали kill» ≠ «процесс
    // мёртв». internal — тесты сужают до сотен миллисекунд; прод живёт 20 с (Kill плюс
    // WaitForExitAsync внутри финализации адаптера занимает до ~10 с).
    internal TimeSpan KillWaitTimeout { get; set; } = TimeSpan.FromSeconds(20);

    // Порог «живой ход начался недавно — убивать рано» (защита честной долгой работы от
    // случайного перезапуска). Тот же ключ конфига и дефолт, что у пульса волны
    // (TeamWaveService._quietThreshold): гейт и индикация обязаны соглашаться по порогу.
    private readonly TimeSpan _freshTurnThreshold;

    // Гейт повторного вызова: перезапуск — это kill → уборка адаптера → revive, второй
    // параллельный вызов того же чата (двойной клик) плодил бы процессы и карточки.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> _turnRestarts = new();

    // Перезапуск зависшего хода (главный сценарий этапа 3): чат занят, процесс молчит,
    // написать в него нельзя. Строго по шагам: проверка живости → kill → дождаться смерти →
    // валидация транскрипта → revive → новый ход с --resume (разбор отложенной очереди:
    // EnsureProcessAsync поднимёт процесс с --resume по ClaudeSessionId). Рецепт уборки —
    // тот же, что ReviveStuckSession (карточка → адаптер → статус), но отдельной сборкой:
    // та шлёт СВОЮ карточку «зависший ход сброшен» fire-and-forget, а здесь статус обязан
    // встать в Active до разбора очереди и с карточкой перезапуска. Стадия, бюджет и версия
    // плана живут на Session.TeamImplement и этим путём не трогаются — режим переживает.
    // Ходов модели сам путь не запускает и квоту пробуждений не тратит: до модели доехают
    // только сообщения, уже стоявшие в очереди до зависания.
    public async Task<StuckTurnRestartResult> RestartStuckTurnAsync(string sessionId, bool startFresh)
    {
        if (!_sessions.TryGetValue(sessionId, out var entry))
            throw new InvalidOperationException("Сессия не найдена");
        // Гейт режима: кнопка и тексты — про штаб «Командной реализации», семантика
        // перезапуска (kill → resume хода штаба) работает только там. Обобщение на
        // все чаты — отдельное решение, не побочный эффект.
        if (entry.Info.TeamImplement is null)
            throw new InvalidOperationException(
                "Перезапуск хода доступен только в чате штаба «Командной реализации»");
        if (!_turnRestarts.TryAdd(sessionId, 0))
            throw new InvalidOperationException("Ход уже перезапускается — дождитесь результата");
        try
        {
            // 1. Живость: чат обязан быть занят — свободному чату перезапуск не нужен.
            // Спасательная ветка (major ревью): первый вызов успел убить процесс и
            // вернуть 409 transcript_damaged, финализация убитого прогона перевела
            // чат в Active, а отложенная очередь умерла на --resume по тому же битому
            // файлу. Чат свободен, но resume-якорь отравлен — без пропуска здесь
            // повторный startFresh вечно упирался бы в «Чат не занят», и у чата не
            // было бы штатного выхода из повреждённого транскрипта.
            if (entry.Info.Status is not (SessionStatus.Working or SessionStatus.Waiting))
            {
                var rescue = startFresh
                    && FindResumeTranscript(entry) is { } damaged
                    && !Llm.Claude.TranscriptProbe.IsTailIntact(damaged);
                if (!rescue)
                    throw new InvalidOperationException(
                        "Чат не занят: ход не идёт. Просто напишите сообщение — оно продолжит разговор");
            }
            // Штаб ждёт ответа человека на карточку (разрешение/вопрос/план) — это не
            // зависание: ответ лежит в ленте чата, прерывать ожидание рестартом нельзя
            if (entry.PendingInteraction is not null)
                throw new InvalidOperationException(
                    "Штаб ждёт вашего ответа на карточку в чате — перезапуск не нужен, ответьте на неё");
            // Живой прогон, начавшийся недавно, — честная работа, а не зависание (тот же
            // порог тишины, что у пульса волны). Долгий немой ход прервать можно — с
            // сохранением контекста через resume.
            var quiet = DateTime.UtcNow - entry.Info.UpdatedAt;
            if (HasLiveTurnProcess(sessionId) && quiet < _freshTurnThreshold)
                throw new InvalidOperationException(
                    $"Штаб работает — {(int)Math.Max(0, quiet.TotalMinutes)} мин назад была активность. " +
                    "Дождитесь результата или остановите ход кнопкой «Стоп»");

            // 2-3. Kill и ОЖИДАНИЕ смерти: HasLiveTurn гаснет в финализации прогона — уже
            // после WaitForExit самого процесса, поэтому поллинг по нему и есть ожидание
            // настоящей смерти, а не факта вызова kill.
            if (entry.Process is { HasLiveTurn: true })
            {
                entry.Process.Interrupt();
                var deadline = DateTime.UtcNow + KillWaitTimeout;
                while (entry.Process is { HasLiveTurn: true })
                {
                    if (DateTime.UtcNow >= deadline)
                        throw new InvalidOperationException(
                            "Не удалось остановить зависший процесс — попробуйте ещё раз через минуту");
                    await Task.Delay(TimeSpan.FromMilliseconds(250));
                }
            }

            // 4. Валидация транскрипта: повреждённый (оборванная последняя строка) через
            // --resume не продолжится. Файл не нашёлся — проверку пропускаем: профиль
            // подписки пула может не входить в AllowedRoots, и ложный «повреждён» на
            // здоровом разговоре хуже пропущенной проверки (CLI сам скажет при resume).
            // startFresh идёт мимо — транскрипт всё равно выбрасываем.
            if (!startFresh && FindResumeTranscript(entry) is { } transcript
                && !Llm.Claude.TranscriptProbe.IsTailIntact(transcript))
                throw new TurnTranscriptDamagedException(
                    "Файл разговора повреждён — продолжить с прежним контекстом нельзя. " +
                    "Можно начать ход заново: старый контекст будет потерян, чат снова заработает");

            // 5. Revive: убираем ожидающую карточку и отравленный адаптер (у него мог
            // остаться захваченным _turnLock зависшей финализацией — DisposeAsync с
            // _cts.Cancel разблокирует ожидателей), статус в Active.
            entry.PendingInteraction = null;
            if (entry.Process is { } dead)
            {
                entry.Process = null;
                FireAndForget(dead.DisposeAsync().AsTask(),
                    $"уборка адаптера перезапущенного хода ({sessionId})");
            }
            // M5 — тот же сброс буфера маркеров, что у Interrupt: убитый ход result не
            // пришлёт, буфер не потребляется, и маркер мёртвого хода (<escalate:*>,
            // <team:work>) доклеился бы к следующему и применился задним числом.
            lock (entry.TeamTurnLock)
            {
                entry.TeamTurnText.Clear();
                entry.TeamTurnShownLength = 0;
                entry.TurnSawAngleBracket = false;
                entry.TeamTurnAsked = false;
            }
            // «Начать заново»: снимаем resume-якорь — следующий адаптер стартует без
            // --resume, CLI заведёт новую сессию и пришлёт новый id (init ниже допишет)
            if (startFresh && entry.Info.ClaudeSessionId is not null)
            {
                entry.Info.ClaudeSessionId = null;
                SaveSessions();
            }
            await ApplyStatusAsync(sessionId, entry, SessionStatus.Active);
            // Честный текст (minor ревью): продолжение разговора обещаем, только если
            // в очереди правда что-то стоит или активный цикл продолжит себя сам —
            // при пустой очереди нового хода не будет, и «разговор продолжится
            // с того же места» обещало бы его напрасно.
            bool willContinue;
            lock (entry.PendingLock) willContinue = !entry.QueueFrozen && entry.Pending.Count > 0;
            willContinue |= entry.Info.WorkLoop is not null;
            await AddWorkLoopStoppedNoticeAsync(sessionId, entry, "team_restart",
                startFresh
                    ? "Ход перезапущен вручную с чистого листа: прежний контекст утерян — напишите, что делать дальше"
                    : willContinue
                        ? "Ход перезапущен вручную — разговор продолжится с сохранённым контекстом"
                        : "Ход перезапущен вручную — чат снова доступен, контекст сохранён");

            // Новый ход с --resume: сообщения, отложенные зависшим ходом, уходят обычной
            // доставкой (DrainInFlight гасит параллельные разборы). Замороженная «Стоп»
            // очередь не разбирается — её держал человек, не мы.
            await DrainNextPendingAsync(sessionId);

            return new StuckTurnRestartResult(
                Resumed: !startFresh,
                Message: startFresh
                    ? willContinue
                        ? "Ход начнётся заново — контекст сброшен, чат снова доступен"
                        : "Контекст сброшен, чат снова доступен — напишите сообщение, чтобы начать новый ход"
                    : willContinue
                        ? "Ход перезапущен: чат снова доступен, разговор продолжится с того же места"
                        : "Ход перезапущен: чат снова доступен — напишите сообщение, чтобы продолжить разговор");
        }
        finally { _turnRestarts.TryRemove(sessionId, out _); }
    }

    // === Авто-возобновление ходов, оборванных рестартом сервера (фича turn-auto-resume) ===

    // Исход возобновления. Sent=true — resume-ход отправлен (агент продолжит с того же
    // места); false — отказ (статус не Orphaned / битый хвост), ход не шлём, в ленте
    // остаётся карточка-баннер. Reason — человекочитаемая причина отказа для лога/отчёта.
    public sealed record RestartResumeResult(bool Sent, string? Reason = null);

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
    public async Task<RestartResumeResult> ResumeAfterRestartAsync(string sessionId)
    {
        if (!_sessions.TryGetValue(sessionId, out var entry))
            return new RestartResumeResult(false, "Сессия не найдена");

        // 1. Кандидат: исходный Working → Orphaned при рестарте.
        if (entry.Info.Status != SessionStatus.Orphaned)
            return new RestartResumeResult(false,
                $"Статус {entry.Info.Status}, ход не оборван рестартом");

        // 2. Целостность хвоста: битый транскрипт через --resume не продолжится.
        if (!IsTailIntactForResume(sessionId))
            return new RestartResumeResult(false, "Хвост транскрипта повреждён");

        // 3. Явная гидратация аккумулятора + карточка в ленту.
        //    EnsureAccumulatorAsync создаёт аккумулятор из существующей истории, но в
        //    сценарии 1 (чат уже имел ClaudeSessionId, ход был Error) аккумулятор может
        //    остаться null, если LoadAsync вернул пустой список и TurnAccumulator не создан.
        //    Явная гидратация гарантирует, что карточка дописана в _history и сохранена.
        await AppendServerRestartCardAsync(sessionId, entry);

        // 5. Счётчик попыток + сохранение. UpdatedAt не двигаем: SaveSessions лишь
        //    персистит, Touch/UpdatedAt=now здесь не вызываем (служебная операция).
        entry.Info.RestartResumeAttempts += 1;
        SaveSessions();

        // 6. Resume-ход обычным путём: auto — для прохода через DeviceWaitGate.
        var prompt = TurnResumePrompts.ServerRestartResume();
        await SendRestartResumeTurnAsync(sessionId, prompt);
        return new RestartResumeResult(true);
    }

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
    public Session? SetMode(string sessionId, string mode, string? userId = null)
    {
        if (!_sessions.TryGetValue(sessionId, out var entry)) return null;
        if (userId is not null && ResolveOwnerId(entry.Info) != userId) return null;
        if (!Enum.TryParse<ClaudeMode>(mode, true, out var parsed))
            throw new InvalidOperationException($"Неизвестный режим: {mode}");
        // Штаб думает (Э8): на стадиях интервью и планирования чат держится в план-режиме, а
        // селектор у человека заблокирован — менять режим мимо него нельзя, иначе правки
        // перестали бы упираться в permission-механику там, где план ещё не согласован.
        // Признак — навязанный режим (SavedMode), а не стадия: у провайдера без поддержки
        // плана мы деградировали и ничего не навязывали, блокировать там нечего.
        if (entry.Info.TeamImplement is { SavedMode: not null } && parsed != ClaudeMode.Plan)
            throw new InvalidOperationException(
                "Штаб планирует. Режим вернётся после согласования плана");
        // «План» у провайдера без поддержки не принимаем — та же защита, что и на ходе
        var caps = _llmProviders.CapabilitiesFor(entry.Info.Model);
        if (parsed == ClaudeMode.Plan && !caps.SupportsPlanMode)
            throw new InvalidOperationException("Провайдер не поддерживает режим «План»");
        // M1: гард «координатор не пишет код» держится на permission-запросах CLI, а в
        // acceptEdits/bypassPermissions CLI не спрашивает — такой режим штабу запрещён в
        // любой точке смены, не только при включении режима (аудит 2026-08-01: обход
        // селектором в стадии волны → координатор писал файлы мимо задач).
        if (entry.Info.TeamImplement is { } teamForGuard)
            parsed = PermissionModeGuard.GuardCompatibleMode(parsed, teamForGuard.CoordinatorNoCode);
        if (entry.Info.Mode == parsed) return entry.Info;
        entry.Info.Mode = parsed;
        SaveSessions();
        // Живой ход перенастраиваем на лету (control-протокол set_permission_mode):
        // новый режим применяется уже к идущему ходу, а не только со следующего.
        entry.Process?.TrySetPermissionModeLive(parsed);
        return entry.Info;
    }

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
            // При выключении гасим маркер итерации: висящий LoopTurnInFlight заблокировал бы
            // разбор очереди по концу следующего хода (гейт !LoopTurnInFlight).
            if (newLoop is null)
                entry.LoopTurnInFlight = false;
        }
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
    {
        if (entry.Accumulator is not { } acc) return;
        acc.Append(new StoredWorkLoopStoppedMessage(reason, text));
        await acc.SaveSnapshotAsync(_history);
        await BroadcastAsync(sessionId, new WorkLoopStoppedMessage(reason, text));
    }

    // Явная гидратация аккумулятора + идемпотентная карточка server_restart (B-cards).
    // В отличие от AddWorkLoopStoppedNoticeAsync (молча выходит при null), здесь аккумулятор
    // создаётся явно: в сценарии 1 (чат уже имел ClaudeSessionId, ход был Error) аккумулятор
    // после LoadSessions может быть null, и AddWorkLoopStoppedNoticeAsync просто выходил.
    // Идемпотентность: если карточка с reason=server_restart уже в _history (повторный вызов
    // после рестарта), не дублируем.
    private async Task AppendServerRestartCardAsync(string sessionId, SessionEntry entry)
    {
        const string reason = "server_restart";
        const string text = "Сервер был перезапущен — продолжаю ход с того же места";

        // Явная гидратация: если аккумулятор null — создаём из существующей истории.
        if (entry.Accumulator is null)
            await EnsureAccumulatorAsync(entry);

        if (entry.Accumulator is not { } acc) return;

        // Идемпотентность: карточка с таким reason уже есть — не дублируем.
        if (acc.GetAll().Any(m => m is StoredWorkLoopStoppedMessage w && w.Reason == reason))
            return;

        acc.Append(new StoredWorkLoopStoppedMessage(reason, text));
        await acc.SaveSnapshotAsync(_history);
        await BroadcastAsync(sessionId, new WorkLoopStoppedMessage(reason, text));
    }

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
    public Func<string, Task>? TurnStarting { get; set; }

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

    private static bool AsyncAgentInFlight(SessionEntry entry) => SessionStore.AsyncAgentInFlight(entry);

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
    // Явные реализации ITeamNotifier: наружу недоступны, наружу только через _teamNotifier.
    // Тела повторяют прежние приватные методы один в один — поведение не меняется. В шаге 2г
    // реализация переедет в вертикаль штаба целиком, эти методы уйдут вместе с телом.

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

    Task<bool> ITeamTurnIntake.SendOrEnqueueAsync(string sessionId, string text,
        string? senderPersonaId, bool silent, bool suppressTasksExecute, string? staffNote, string? senderUserId)
        => SendOrEnqueueAsync(sessionId, text, senderPersonaId,
            silent: silent, suppressTasksExecute: suppressTasksExecute, staffNote: staffNote,
            senderUserId: senderUserId);

    void ITeamTurnIntake.InterruptTurn(string sessionId) => InterruptCore(sessionId, byUser: false);

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

    // Потолок добиваний подряд. Две попытки — сознательный предел: бесконечный цикл добивания
    // хуже честного отказа, после него судьбу работы решает человек.
    internal const int MaxSubagentNudges = 2;

    /// <summary>
    /// Слать ли добивание оборванного сабагента. Чистая функция — вся политика в одном месте
    /// (под тестом). Уступаем всем, у кого свой протокол продолжения: цикл «до готово» и штаб
    /// продолжат ход сами, непустая очередь продолжит его сообщением, а параллельный ход цикла
    /// (LoopTurnInFlight) уже в пути — второй systemDirective ушёл бы в тот же процесс.
    ///
    /// <paramref name="isInterruptedRun"/> — признак обрыва класса «убит прерыванием хода»
    /// (паспорт FinishedBy == "interrupted"): такие обрывы счётчик MaxSubagentNudges не
    /// расходуют (причина в нашем коде и не свидетельствует о неисправном агенте), а
    /// автоматически слать сообщение «продолжи» означало бы действовать за человека — он
    /// сам нажал «Стоп», решение о продолжении принадлежит ему.
    /// </summary>
    internal static bool ShouldNudgeSubagent(int nudgesSent, bool workLoopActive, bool teamActive,
        bool hasPending, bool loopTurnInFlight, bool isInterruptedRun = false) =>
        !isInterruptedRun && nudgesSent < MaxSubagentNudges
        && !workLoopActive && !teamActive && !hasPending && !loopTurnInFlight;

    /// <summary>
    /// Ход координатора в полёте: сообщение ему уже отдано в процесс и result ещё не пришёл.
    /// Пока это так, второй systemDirective-ход слать нельзя (он уйдёт в тот же процесс) —
    /// добивание ждёт конца хода, координатору достаётся только пометка.
    /// </summary>
    internal static bool TurnInFlight(SessionStatus status) =>
        status is SessionStatus.Starting or SessionStatus.Working or SessionStatus.Waiting;

    /// <summary>
    /// Обрыв ФОНОВОГО агента: пометка координатору обязательна всегда, добивание — только
    /// если ход координатора не идёт (иначе ждём result, как раньше).
    /// </summary>
    private void NoteTruncatedBgAgent(string sessionId, SessionEntry entry,
        Llm.SubagentRunPassport run)
    {
        // Пометка уедет префиксом ближайшего хода — чем бы он ни был поднят (человеком,
        // очередью, добиванием): координатор обязан узнать, что обрывок не итог, даже когда
        // добивать мы не стали.
        entry.TruncatedBgNote = run;

        if (entry.Process is null || TurnInFlight(entry.Info.Status)) return;
        // Оборвался другой агент — у него своя серия попыток (счётчик per-agentId)
        if (StartsNudgeSeries(entry.NudgeAgentId, run.AgentId)) entry.SubagentNudges = 0;
        if (!ShouldNudgeSubagent(entry.SubagentNudges, entry.Info.WorkLoop is not null,
                entry.Info.TeamImplement is not null, HasPending(entry), entry.LoopTurnInFlight,
                isInterruptedRun: run.FinishedBy == "interrupted")) return;

        // Разбираем отметку здесь — иначе по ближайшему result добивание ушло бы вторым разом
        entry.TruncatedSubagent = null;
        entry.NudgeAgentId = run.AgentId;
        var attempt = ++entry.SubagentNudges;
        _ = Task.Run(async () =>
        {
            try
            {
                // Ход мог стартовать, пока планировалась отправка (очередь, автоматизация,
                // цикл): откатываем попытку и возвращаем отметку — добивание уедет по его
                // result, штатным путём. Второй ход в живой процесс не отправляем никогда.
                if (TurnInFlight(entry.Info.Status))
                {
                    entry.SubagentNudges = Math.Max(0, entry.SubagentNudges - 1);
                    entry.TruncatedSubagent = run;
                    return;
                }
                await NudgeTruncatedSubagentAsync(sessionId, run, attempt);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[SessionManager] Добивание фонового сабагента ({sessionId}): {ex.Message}");
            }
        });
    }

    /// <summary>
    /// Снимает ли штатный отчёт агента серию добиваний. Счётчик общий на сессию, а агентов
    /// в ходе несколько — поэтому серию закрывает ТОТ ЖЕ агент, которого добивали (либо любой,
    /// пока серии нет). Чужой отчёт счётчик не трогает: иначе потолок MaxSubagentNudges
    /// не достигается вовсе и чат крутит системные директивы автономно.
    /// </summary>
    internal static bool ResetsNudgeSeries(string? nudgeAgentId, string reportedAgentId) =>
        nudgeAgentId is null || nudgeAgentId == reportedAgentId;

    /// <summary>
    /// Начинает ли оборвавшийся агент новую серию: попытки считаются per-agentId, поэтому
    /// другой агент получает свои две, а не остаток чужих.
    /// </summary>
    internal static bool StartsNudgeSeries(string? nudgeAgentId, string truncatedAgentId) =>
        nudgeAgentId != truncatedAgentId;

    /// <summary>
    /// Гасит ли штатный отчёт агента пометку обрыва. Пометка бывает ложной: bg_agent_done
    /// обгоняет дозапись финала в транскрипт, и по хвосту tool_use агент числится оборванным,
    /// хотя дописал end_turn. Опровергает пометку только отчёт ТОГО ЖЕ агента.
    /// </summary>
    internal static bool RefutesTruncation(string? markedAgentId, string reportedAgentId) =>
        markedAgentId == reportedAgentId;

    // Добивание: директива координатору дослать оборванному сабагенту продолжение. Тем же
    // способом, что и цикл «до готово» (systemDirective-ход после result), и с тем же
    // потолком попыток — см. SubagentPrompts.ResumeTruncated. Для класса «убит прерыванием»
    // (FinishedBy == "interrupted") политика сюда не пустит (ShouldNudgeSubagent с
    // isInterruptedRun: true), но если когда-то дойдёт — текст берётся по-другому
    // (ResumeInterrupted: не «дослать продолжить», а «ход прерван, транскрипт цел»).
    private async Task NudgeTruncatedSubagentAsync(string sessionId,
        Llm.SubagentRunPassport run, int attempt)
    {
        // Окно штатной остановки: директива ушла бы в умирающий адаптер
        if (_stopping) return;
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
        await SendMessageAsync(sessionId, directive,
            [], systemDirective: true, cause: DeliveryCause.SubagentNudge);
    }

    // Автопродолжение цикла «до готово»: вызывается по result хода, нёсшего протокол цикла.
    // Маркер найден → верификационный ход, затем стоп; нет → продолжение до лимита итераций
    // либо уход в фазу waiting, если у чата есть живые делегированные задачи.
    private async Task ContinueWorkLoopAsync(string sessionId)
    {
        // Окно штатной остановки: новый ход цикла поднял бы процесс, который тут же убьют
        if (_stopping) return;
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
                await SendMessageAsync(sessionId, OmoPrompts.WorkLoopVerification, [], systemDirective: true);
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
            await SendMessageAsync(sessionId,
                OmoPrompts.WorkLoopContinuation(loop.Promise, loop.Iteration, loop.MaxIterations), [], systemDirective: true);
        }
        catch
        {
            // Директива так и не ушла (сбой до/в отправке) — ход result не пришлёт, и взведённый
            // выше маркер повис бы, заблокировав разбор очереди (drain уступает, пока он взведён).
            // Пути остановки снимают цикл через SetWorkLoop(false) — та сбрасывает маркер сама;
            // здесь ловим только исключение до отправки. Перебрасываем — вызывающий (Task.Run по
            // result) залогирует.
            if (entry.Info.WorkLoop is not null)
                entry.LoopTurnInFlight = false;
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
            // BuildCliTurnText взводит LoopTurnInFlight (под LoopTurnLock), а ContinueWorkLoopAsync
            // по result хода-тика проверит, остался ли маркер, и вернёт фазу.
            _log.LogInformation("Цикл {Session}: тик ожидания {Ticks}/{Max} (причина: {Reason})",
                sessionId, loop.WaitingTicks, _maxWaitingTicks, loop.WaitingReason);
            SaveSessions();
            await BroadcastWorkLoopAsync(sessionId, entry);
            if (entry.Info.WorkLoop is null) continue; // стоп успел снять цикл
            await SendMessageAsync(sessionId,
                OmoPrompts.WorkLoopWaitingTick(loop.WaitingReason,
                    loop.WaitingTicks, _maxWaitingTicks,
                    (int)_waitingTickInterval.TotalSeconds),
                [], systemDirective: true);
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

    public void AnswerQuestion(string sessionId, string toolUseId, string answerText)
    {
        if (!_sessions.TryGetValue(sessionId, out var entry)) return;
        if (IsStaleInteractionAnswer(sessionId, entry, $"вопрос {toolUseId}")) return;
        entry.Process?.AnswerQuestion(toolUseId, answerText);
        entry.PendingInteraction = null;
        object? answers = null;
        try
        {
            using var doc = JsonDocument.Parse(answerText);
            if (doc.RootElement.TryGetProperty("answers", out var a))
                answers = JsonSerializer.Deserialize<object>(a.GetRawText());
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[SessionManager] Ответ на вопрос ({sessionId}) не распарсился: карточка погаснет без сводки выбора (и в историю, и в рассылку уйдёт без answers): {ex.Message}");
        }
        // Фиксируем ответ в истории, чтобы карточка вопроса пережила перезагрузку
        if (entry.Accumulator is not null)
        {
            entry.Accumulator.OnQuestionAnswered(toolUseId, answers);
            FireAndForget(entry.Accumulator.SaveSnapshotAsync(_history),
                $"сохранение истории после ответа на вопрос ({sessionId})");
        }
        FireAndForget(BroadcastAsync(sessionId,
                new InteractionResolvedMessage("question", toolUseId, Answers: answers)),
            $"рассылка ответа на вопрос ({sessionId})");
        ResumeAfterInteraction(sessionId, entry, "ответа на вопрос");
    }

    public void RespondPlan(string sessionId, string requestId, bool approve, string? feedback)
    {
        if (!_sessions.TryGetValue(sessionId, out var entry)) return;
        if (IsStaleInteractionAnswer(sessionId, entry, $"план {requestId}")) return;
        entry.Process?.RespondPlan(requestId, approve, feedback);
        entry.PendingInteraction = null;
        // Фиксируем решение по плану в истории, чтобы карточка пережила перезагрузку
        if (entry.Accumulator is not null)
        {
            entry.Accumulator.OnPlanResolved(requestId, approve, feedback);
            FireAndForget(entry.Accumulator.SaveSnapshotAsync(_history),
                $"сохранение истории после решения по плану ({sessionId})");
        }
        FireAndForget(BroadcastAsync(sessionId,
                new InteractionResolvedMessage("plan", requestId, Approved: approve, Feedback: feedback)),
            $"рассылка решения по плану ({sessionId})");
        ResumeAfterInteraction(sessionId, entry, "решения по плану");
    }

    // Решение (Minor, волна 3, осознанно оставлено как есть): удаление чата-штаба НЕ отменяет
    // живые волны, НЕ трогает дочерние чаты исполнения (Session.ParentSessionId вычисляется из
    // Task.SourceSessionId — после удаления штаба они всплывают в корень дерева чатов) и не
    // закрывает под-задачи явно — эскалации незакрытых волн (TeamWaveService.RaiseEscalationAsync
    // → PublishTeamEscalationAsync) молча падают в лог: `_sessions.TryGetValue` не находит
    // удалённую запись и пишет предупреждение, дальше эскалация никуда не уходит.
    // Почему не чиним сейчас: полный каскад (остановить живые процессы исполнителей, решить,
    // что делать с их чатами — удалять, переносить в корень явной карточкой или оставлять как
    // есть, закрыть/пометить под-задачи) — самостоятельная фича с продуктовыми развилками
    // (UX «осиротевших» чатов), а не точечный фикс. Удаление чата с фоновой работой уже и
    // так ничего не отменяет каскадно нигде в системе (обычная задача с исполнителем — тот
    // же класс). Ниже — минимальная страховка: явный лог, чтобы находка не терялась молча.
    public async Task DeleteAsync(string sessionId)
    {
        if (!_sessions.TryRemove(sessionId, out var entry)) return;
        if (entry.Info.TeamImplement is { WaveNumber: > 0 } liveTeam && liveTeam.WaveNumber > liveTeam.ClosedWave)
            _log.LogWarning("Чат-штаб {SessionId} удалён посреди незакрытой волны {Wave} — " +
                "живые задачи и дочерние чаты исполнения не отменяются и не переносятся " +
                "(осознанное решение волны 3, см. комментарий у DeleteAsync)", sessionId, liveTeam.WaveNumber);
        // Стоп снапшотам ДО удаления файлов: финализация прогона (drain сабагентов,
        // поздние tool_result) доигрывается после dispose и пересоздала бы history.json
        entry.Accumulator?.MarkDeleted();
        if (entry.Process is not null)
            await entry.Process.DisposeAsync();
        // Дренаж очереди сессии до конца: ResultMessage последнего хода обязан дойти до всех
        // наблюдателей (иначе TaskExecutionService не закроет задачу исполнителя)
        // HTTP-путь удаления не ждёт зависшего наблюдателя: дренаж идёт в фоне, исход — в лог
        if (entry.Pipeline is { } pipeline)
            _ = Task.Run(async () =>
            {
                try
                {
                    await pipeline.CompleteAsync(TimeSpan.FromSeconds(30));
                    _log.LogDebug("Очередь удалённой сессии {SessionId} разобрана", sessionId);
                }
                catch (Exception ex)
                {
                    _log.LogWarning(ex, "Дренаж очереди удалённой сессии {SessionId} упал", sessionId);
                }
            });

        // Дочищаем историю на диске — иначе data/sessions/{id} копится мусором
        if (entry.Info.ClaudeSessionId is string csid)
        {
            // И история, и транскрипт CLI могут быть ОБЩИМИ у двух чатов: сессия, созданная с
            // resumeSessionId (POST /sessions, /chats, /personas/{id}/chats), несет тот же
            // ClaudeSessionId. Пока на него ссылается другой чат, не трогаем ни то, ни другое:
            // иначе у него пропадет лента в UI (история) и вся память разговора (транскрипт —
            // его читает --resume). Свою запись из реестра мы вынули выше, поэтому Any видит
            // только чужие ссылки.
            if (_sessions.Values.Any(e => e.Info.ClaudeSessionId == csid))
                _log.LogInformation(
                    "История и транскрипт чата {SessionId} оставлены: на сессию {ClaudeSessionId} ссылается другой чат",
                    entry.Info.Id, csid);
            else
            {
                _history.Delete(csid);
                // Локальный проект: транскрипт на устройстве, архивной копии сервер не делал —
                // искать и удалять файл по своему пути значило бы задеть чужой
                if (TranscriptOnServer(entry.Info))
                {
                    DeleteTranscript(entry.Info, csid);
                    // Архивная копия транскрипта уходит вместе с чатом — иначе переписка
                    // переживёт его и в data, и в бэкапе. Гейт тот же (общий csid у чата-двойника)
                    _archivedTranscripts.Delete(csid);
                }
            }
        }
        // Снимки промпта ключуются id ЧАТА, а не транскриптом, — гейт общего разговора выше
        // к ним не относится: у чата-двойника свои снимки, и его лента их не потеряет
        _promptSnapshots?.DeleteAll(sessionId);
        // История ЛОКАЛЬНЫХ голосовых ходов пишется под id чата (до первого CLI-хода у чата
        // нет ClaudeSessionId) — чистим всегда: id чата уникален, общим с другим чатом быть
        // не может. Убиратся и файл-дубль после перехода локаль→CLI (история тогда уже
        // пишется под csid, а старая папка оставалась бы мусором до удаления чата). Гейт
        // снизу — единственный экзотический случай:resume-чата с ClaudeSessionId, равным
        // id ЭТОГО чата (resumeSessionId валидируется белым списком, но может совпадать).
        var localHistoryKey = entry.Info.Id.ToString();
        if (!_sessions.Values.Any(e => e.Info.ClaudeSessionId == localHistoryKey))
            _history.Delete(localHistoryKey);

        // Отдельное worktree чата: своё (создано тумблером, WorktreeOwned) сносим вместе с
        // чатом; чужое/общее (привязано) или старое (без признака) — только ReleaseWorktreeGraph,
        // без git worktree remove: дерево делят другие живые чаты, и его снос унёс бы их работу.
        // Запись этого чата из реестра уже вынята выше (TryRemove), поэтому Any видит только чужие.
        if (entry.Info.WorktreePath is string wt && entry.Info.ProjectId is string wpid && _git is not null)
        {
            var removeTree = entry.Info.WorktreeOwned
                && !_sessions.Values.Any(e => e.Info.WorktreePath is string other
                    && WorkspaceKnowledgeStore.NormalizePath(other) == WorkspaceKnowledgeStore.NormalizePath(wt));
            if (removeTree)
            {
                try
                {
                    if (_projects.GetById(wpid) is { } wproj)
                    {
                        // Слоты ревью — детища дерева волны; имя слота строится по ветке дерева,
                        // поэтому сносим их ДО самого дерева (после него ветку не узнать)
                        if (ProjectCapabilities.FilesOnServer(wproj) && _services?.GetService<ReviewTreePool>() is { } pool)
                            await pool.RemoveSlotsOfWaveAsync(ResolveOwnerId(entry.Info), wproj, wt);
                        await _git.WorktreeRemoveAsync(ResolveOwnerId(entry.Info), wproj.RootPath, wt, force: true);
                    }
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[SessionManager] Worktree чата не удалён ({sessionId}): {ex.Message}");
                }
            }
            ReleaseWorktreeGraph(sessionId, wt);
        }
        SaveSessions();
        try { OnSessionDeleted?.Invoke(entry.Info); } catch { /* наблюдатель не должен ронять удаление */ }
        // Подсистемы сносят своё состояние чата (ADR-019 §2); сбой подписчика шина гасит сама
        await TurnEvents.PublishAsync(new SessionDeleted(
            new TurnContext(sessionId, ResolveOwnerId(entry.Info), 0, 0, entry.Info.ProjectId)));
        await BroadcastChatDeletedAsync(sessionId, entry.Info);
    }

    // Транскрипт claude CLI удаленного чата ({профиль}/projects/{уплощенный cwd}/{csid}.jsonl).
    // Тело — в CliRoute (S3-2): там же TranscriptSearchRoots и TryResolveCwd. Инвариант
    // «уносим ровно {csid}.jsonl, не по маске и не папку» — в теле роли, покрыт
    // SessionManagerCrudTests (DeleteAsync_УдаляетТранскриптCliИНеТрогаетЧужой).
    private void DeleteTranscript(Session info, string claudeSessionId) => _route.DeleteTranscript(info, claudeSessionId);

    // Подготовленная ветвлением пара «{csid}.jsonl + data/sessions/{csid}» осиротела: CLI на
    // первом ходе ветки выдал другой session_id, и подложенный транскрипт уже никто не
    // прочитает (история с этого момента пишется под новым ключом). Гейт общего разговора —
    // тот же, что в DeleteAsync: пока на csid ссылается другой чат (двойник, созданный с тем
    // же resumeSessionId), не трогаем ни историю, ни транскрипт. Сам чат-ветка под этот гейт
    // уже не попадает — его ClaudeSessionId к этому моменту переписан на пришедший от CLI.
    // Best-effort: уборка не должна ронять ход, все сбои — в лог (внутри DeleteTranscript).
    private void CleanupOrphanBranchTranscript(Session info, string orphanCsid)
    {
        if (_sessions.Values.Any(e => e.Info.ClaudeSessionId == orphanCsid))
        {
            _log.LogInformation(
                "Ветка {SessionId}: CLI сменил сессию на другую, но подготовленный {Csid} оставлен — на него ссылается другой чат",
                info.Id, orphanCsid);
            return;
        }
        _log.LogInformation(
            "Ветка {SessionId}: CLI выдал свой session_id — убираем осиротевшую подготовленную сессию {Csid}",
            info.Id, orphanCsid);
        try { _history.Delete(orphanCsid); }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Историю осиротевшей сессии {Csid} убрать не удалось", orphanCsid);
        }
        DeleteTranscript(info, orphanCsid);
    }

    // Уведомить клиентов об удалении чата (в т.ч. авто-удалении временного) —
    // адресация как у BroadcastStatusChangeAsync: проект или владелец чата
    private async Task BroadcastChatDeletedAsync(string sessionId, Session info)
    {
        var msg = new ChatDeletedMessage() with { SessionId = sessionId };
        var tasks = new List<Task> { _broadcaster.ToSession(sessionId, msg) };
        tasks.Add(_broadcaster.ToChatAudience(info, msg));
        await Task.WhenAll(tasks);
    }

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
    internal async Task OnMessageAsync(string sessionId, TurnAccumulator acc, ServerMessage msg, long runId = 0)
    {
        await OnMessageQueuedAsync(sessionId, acc, msg, runId);
        // Тестовый шов сохраняет прежнюю семантику «вернулся — рассылка и наблюдатели отработали»;
        // боевые вызывающие идут в OnMessageQueuedAsync и очередь не ждут
        await DrainPipelineAsync(sessionId);
    }

    internal SessionMessagePipeline? PipelineForTest(string sessionId) =>
        _sessions.TryGetValue(sessionId, out var entry) ? entry.Pipeline : null;

    // Дожидается разбора всего, что легло в очередь сессии (тесты и точки, которым нужны
    // отработавшие наблюдатели)
    internal Task DrainPipelineAsync(string sessionId) =>
        _sessions.TryGetValue(sessionId, out var entry) && entry.Pipeline is { } pipeline
            ? pipeline.DrainAsync() : Task.CompletedTask;

    // Боевой вход обработчика сообщений адаптера. Всё, что меняет состояние сессии (снимок в
    // аккумулятор, статус, гейты прогона, запуск очереди и цикла), выполняется здесь же и по
    // порядку; рассылка в SignalR и обход наблюдателей уходят в очередь сессии
    // (SessionMessagePipeline), чтобы медленный клиент или наблюдатель не держал read-loop CLI.
    internal async Task OnMessageQueuedAsync(string sessionId, TurnAccumulator acc, ServerMessage msg, long runId = 0)
    {
        _sessions.TryGetValue(sessionId, out var entry);
        // Волна 6: живая трансляция хода штаба фильтрует маркеры протокола (см. кейс ниже) —
        // иногда дельте нечего транслировать (весь её текст — часть маркера/незавершённый
        // хвост), тогда исходное сообщение до низового BroadcastAsync не доходит вовсе.
        var sendBroadcast = true;

        // Аккумулятор — отдельный try, чтобы ошибка сохранения истории
        // не заблокировала обновление статуса и широковещание.
        try
        {
            switch (msg)
            {
                case SessionStartedMessage m:
                    // Страховка ветвления: CLI 2.1.276 сессию по --resume не форкает (разведка
                    // шага 0), но это поведение версионно-зависимое. Ветке мы подложили
                    // транскрипт и историю под СВОИМ csid — если CLI выдал другой session_id,
                    // подготовленная пара осиротела и её надо убрать, иначе она пролежит на
                    // диске до плановой уборки CLI, а в data/sessions останется навсегда.
                    // Гейт BranchedFromSessionId — у обычных чатов проверка бесплатна.
                    if (entry is { Info.BranchedFromSessionId: not null }
                        && acc.SaveKey is { Length: > 0 } preparedCsid
                        && !string.Equals(preparedCsid, m.ClaudeSessionId, StringComparison.Ordinal))
                        CleanupOrphanBranchTranscript(entry.Info, preparedCsid);
                    acc.SetSaveKey(m.ClaudeSessionId);
                    acc.OnSessionStarted(m.Model, m.Mode, m.TurnWorktree);
                    if (entry is not null)
                    {
                        entry.TurnInWorktree = m.TurnWorktree != null;
                        // Этап 4 / шаг 1а: фиксируем TurnSeq текущего хода для слота осушенного
                        // буфера. SubmittedTurnSeq инкрементируется в SubmitTurn
                        // (ClaudeSession.cs:2351), а SessionStartedMessage шлётся после неё
                        // (ClaudeSession.cs:4099) — к моменту нашего чтения номер уже актуален.
                        // 0 — локальный голосовой ход (адаптер не Claude, SubmittedTurnSeq = 0),
                        // turn/completed по нему не публикуется, слот не пополняется.
                        entry.LastTurnSeq = entry.Process is { } adapter
                            ? (int)adapter.SubmittedTurnSeq
                            : 0;
                    }
                    SaveSessions();
                    break;
                case TextDeltaMessage m:
                    acc.OnTextDelta(m.Text);
                    // Цикл «до готово»: копим текст хода для поиска маркера завершения
                    if (entry?.Info.WorkLoop is not null)
                        lock (entry.LoopTurnLock) entry.LoopTurnText.Append(m.Text);
                    // Маркеры протокола в живой ленте: копим текст хода и решаем, что из
                    // накопленного уже безопасно показать человеку (волна 6). Целиком
                    // завершённый маркер вырезаем, незавершённый хвост придерживаем до
                    // следующей дельты — иначе полтега мелькнёт на экране раньше, чем мы
                    // поймём, что это протокол, а не текст персоны. Режим штаба ещё и ловит
                    // отсюда маркер эскалации координатора (разбор — на конце хода).
                    // Стрижка идёт в ЛЮБОМ чате, не только в штабе: сохранённая история
                    // чистится безусловно (TurnAccumulator.FlushBuffers), и без этого маркер
                    // (в т.ч. `<no-reply/>` постановщика) мелькал бы в стриме и пропадал
                    // после перезагрузки страницы.
                    if (entry is not null)
                    {
                        string? displayDelta;
                        lock (entry.TeamTurnLock)
                        {
                            entry.TeamTurnText.Append(m.Text);
                            if (!entry.TurnSawAngleBracket && m.Text.Contains('<'))
                                entry.TurnSawAngleBracket = true;
                            if (!entry.TurnSawAngleBracket)
                            {
                                // Быстрый путь обычного чата: без '<' маркеру взяться неоткуда,
                                // стрижка вернула бы тот же текст — не платим за ToString()
                                // всего хода на каждой дельте.
                                entry.TeamTurnShownLength += m.Text.Length;
                                displayDelta = m.Text;
                            }
                            else
                            {
                                var safe = TeamProtocolMarkers.TrimAmbiguousMarkerTail(
                                    TeamProtocolMarkers.TrimUnresolvedMarkerOpen(TeamProtocolMarkers.StripTeamProtocolMarkers(entry.TeamTurnText.ToString())));
                                // Пока в очищенном тексте нет ни одного непробельного символа,
                                // показывать нечего: ход, ответивший ровно маркером, не должен
                                // родить в ленте пустой пузырь из «\n» вокруг маркера. Длину
                                // показанного не двигаем — придержанные пробелы уйдут вместе с
                                // первым настоящим текстом, если он появится.
                                if (safe.Length > entry.TeamTurnShownLength && safe.Trim().Length > 0)
                                {
                                    displayDelta = safe[entry.TeamTurnShownLength..];
                                    entry.TeamTurnShownLength = safe.Length;
                                }
                                else displayDelta = null;
                            }
                            // Всё накопленное показано и ничего не придержано — маркеру в
                            // буфере взяться неоткуда. В обычном чате буфер нужен ровно для
                            // показа, поэтому сжимаем его: иначе каждая следующая дельта
                            // хода с кодом (там '<' на каждом шагу) пересканировала бы весь
                            // ход целиком — квадрат по длине ответа. В штабе так нельзя:
                            // там весь текст хода разбирается на маркеры в его конце.
                            if (entry.Info.TeamImplement is null
                                && entry.TeamTurnShownLength == entry.TeamTurnText.Length)
                            {
                                entry.TeamTurnText.Clear();
                                entry.TeamTurnShownLength = 0;
                                entry.TurnSawAngleBracket = false;
                            }
                        }
                        if (string.IsNullOrEmpty(displayDelta)) sendBroadcast = false;
                        else msg = m with { Text = displayDelta };
                    }
                    break;
                case ThinkingDeltaMessage m: acc.OnThinkingDelta(m.Text); break;
                case AgentTextMessage m:
                    acc.OnAgentText(m.ParentToolUseId, m.Text);
                    await acc.SaveSnapshotAsync(_history); // reload посреди долгого сабагента видит уже написанное
                    break;
                case AgentThinkingMessage m: acc.OnAgentThinking(m.ParentToolUseId, m.Text); break;
                case ToolUseMessage m:
                    // Время старта — по часам сервера и одним числом в ленту и в историю:
                    // иначе таймер карточки после F5 начинал бы отсчёт заново
                    m = m with { StartedAt = m.StartedAt ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() };
                    msg = m;
                    acc.OnToolUse(m.Id, m.Name, m.Input, m.ParentToolUseId, m.StartedAt);
                    TryUnmarkCommittedOnToolUse(sessionId, entry, m.Name, m.Input);
                    if (entry is not null && SpendMapping.TryExtractHiggsfieldGeneration(m.Name))
                        SpendMapping.RecordHiggsfieldGeneration(_spend, ResolveOwnerId, _log, entry.Info);
                    break;
                case ToolStartedMessage m:
                    m = m with { StartedAt = m.StartedAt ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() };
                    msg = m;
                    acc.OnToolStarted(m.ToolUseId, m.StartedAt.Value);
                    break;
                case ToolResultMessage m:
                    m = m with { FinishedAt = m.FinishedAt ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() };
                    msg = m;
                    acc.OnToolResult(m.ToolUseId, m.Content, m.IsError, m.FinishedAt);
                    await acc.SaveSnapshotAsync(_history); // промежуточное сохранение после каждого tool call
                    // Продолженный после рестарта ход дошёл до результата инструмента — это уже не
                    // crash-loop, а живая работа: счётчик auto-resume обнуляем, чтобы СЛЕДУЮЩИЙ
                    // рестарт (вечер 22.10: девять выкаток подряд, долгие ходы исполнителей не
                    // успевали дойти до result) снова мог продолжить этот ход. До первого
                    // tool_result счётчик стоит: ход, падающий сразу после --resume, не зациклится.
                    if (entry is not null && entry.Info.RestartResumeAttempts > 0)
                    {
                        entry.Info.RestartResumeAttempts = 0;
                        SaveSessions();
                    }
                    TryTrackFalCost(sessionId, m.Content); // fire-and-forget: стоимость придёт позже
                    TryTrackGlifCost(sessionId, m.Content); // синхронно: кредиты уже в tool_result
                    break;
                case WorkflowProgressMessage m:
                    if (entry is not null)
                    {
                        // Кэш мутируют таймер-потоки ватчеров (несколько workflow = несколько
                        // потоков), читает JoinSession — только под локом
                        lock (entry.WorkflowProgress)
                        {
                            if (m.IsDone) entry.WorkflowProgress.Remove(m.ToolUseId);
                            else entry.WorkflowProgress[m.ToolUseId] = m;
                        }
                    }
                    // Последний снапшот — в историю: карточка workflow и вкладка «Агенты»
                    // должны переживать перезагрузку страницы и рестарт сервера
                    acc.OnWorkflowProgress(m.ToolUseId, m.IsDone, m.Agents);
                    if (m.IsDone) await acc.SaveSnapshotAsync(_history);
                    break;
                case BgAgentDoneMessage m:
                    acc.OnBgAgentsDone(m.ToolUseIds);
                    await acc.SaveSnapshotAsync(_history);
                    // Волна 3 задачи b63fd8ea: хук на завершение фонового async-агента.
                    //
                    // Снимок «есть ли ещё живой async-агент» берётся ПОСЛЕ учёта текущего
                    // сообщения — гонки «читаем старое состояние» нет ни на одном из четырёх
                    // источников BgAgentDoneMessage. Все четыре ДО отправки сообщения уже
                    // убрали завершившуюся задачу из run.PendingBg (а для FinalizeRunAsync —
                    // из всего словаря):
                    //   • HandleStructuredTaskNotification (ClaudeSession.cs:5055) — снимает
                    //     задачу через `lock (run.PendingBg) run.PendingBg.Remove(taskId, ...)`
                    //     синхронно и публикует BgAgentDoneMessage через Task.Run из того же
                    //     пути, что HandleStructuredTaskNotification читает;
                    //   • HandleTaskNotification (ClaudeSession.cs:4954) — то же самое, по
                    //     текстовому <task-notification> вместо структурного события;
                    //   • HandleTaskOutputCompletion (ClaudeSession.cs:4989) — снимает через
                    //     `lock (run.PendingBg) run.PendingBg.Remove(agentId, ...)` и
                    //     публикует через Task.Run;
                    //   • FinalizeRunAsync (ClaudeSession.cs:3706) — единственный путь,
                    //     где публикация идёт inline await (не Task.Run): прогон уже умер,
                    //     `lock (run.PendingBg) { orphanedTools = run.PendingBg.Values...;
                    //     run.PendingBg.Clear(); }` происходит ДО вызова CompleteBgTasksAsync
                    //     и до возврата await, поэтому HasPendingBg к моменту чтения ниже
                    //     уже отражает очищенный словарь.
                    //
                    // Решения (Major + Minor 2, оба на одной проверке HasAsyncAgent):
                    //   • Метка AsyncAgentStallSince сбрасывается ТОЛЬКО когда после
                    //     текущего done агентов больше нет вообще (HasAsyncAgent == false).
                    //     Безусловный сброс (как в волне 2) перезапускал 10-минутный
                    //     потолок подавления на каждом агенте цепочки — суппрессия
                    //     тянулась неограниченно, ровно против того, что баг b63fd8ea
                    //     чинил.
                    //   • Карточка молчаливого тупика поднимается ТОЛЬКО когда (а) агент
                    //     умер абортивно (Aborted=true) и (б) других живых async-агентов
                    //     больше нет (HasAsyncAgent == false). Иначе — структурный
                    //     task_notification ставит Aborted для ОДНОГО агента, пока
                    //     параллельно работает ДРУГОЙ: карточка «Координатор не понял
                    //     вводную» сразу на первом аборте уводила стадию в AwaitingDecision,
                    //     хотя координатор ЖИВ — дословный регресс P16 (Major, найден
                    //     ревью Глеба).
                    //
                    // Карточка публикуется через TeamTurnCompletionService.HandleBgAgentDoneAsync
                    // — та же точка, что HandleTeamTurnEndAsync, и обе сериализуются на
                    // общем локе TeamStateService.WithTeamState через TryClaimSilentStall
                    // (Minor 1, идемпотентность): гонка «оба пути публикуют одновременно»
                    // закрывается атомарным pre-claim — первый путь переводит стадию в
                    // AwaitingDecision под локом, второй видит её и выходит.
                    if (entry is not null)
                    {
                        // Снимок HasAsyncAgent — ПОСЛЕ учёта текущего сообщения (см. шапку):
                        // HandleStructuredTaskNotification (ClaudeSession.cs:5055) уже удалил
                        // задачу из run.PendingBg синхронно до публикации BgAgentDoneMessage,
                        // entry.Process?.HasPendingBg отражает обновлённое состояние.
                        var hasAsync = AsyncAgentInFlight(entry);
                        bool asked;
                        lock (entry.TeamTurnLock)
                        {
                            // Minor 2: метка сбрасывается ТОЛЬКО когда после текущего done
                            // других async-агентов больше нет. Безусловный сброс перезапускал
                            // бы 10-минутный потолок подавления на КАЖДОМ агенте цепочки —
                            // суппрессия тянулась бы неограниченно (ровно то, против чего
                            // баг b63fd8ea был написан).
                            if (!hasAsync && entry.AsyncAgentStallSince is not null)
                                entry.AsyncAgentStallSince = null;
                            asked = entry.TeamTurnAsked;
                        }
                        await HandleBgAgentDoneAsync(sessionId, m.Aborted, hasAsync, asked);
                    }
                    break;
                // Присутствие фона — сигнал для СПИСКА чатов, а не для ленты: в историю не
                // пишем (состояние живёт ровно столько, сколько процесс) и статус сессии не
                // трогаем — ApplyStatusAsync двигал бы UpdatedAt, а по нему идут сортировка,
                // секции дерева и непрочитанность. Рассылка — в session + project/user-группы
                case BgAgentsPresenceMessage m:
                    if (entry is not null)
                        await EnqueueWorkAsync(entry, () => BroadcastSessionMessageAsync(sessionId, m));
                    else
                        await BroadcastSessionMessageAsync(sessionId, m);
                    break;
                case FileChangedMessage m:
                    acc.OnFileChanged(m.Path, m.Added, m.Removed, m.External);
                    // External не гейтим: сырое множество индекса содержит и внешние правки
                    // (фильтр «только файлы чата») — пометка снимается любой из них
                    if (entry is { TurnInWorktree: false })
                        UnmarkFileCommitted(sessionId, SessionChangedPaths.Normalize(m.Path));
                    break;
                case CompactBoundaryMessage m:
                    acc.OnCompactBoundary(m.Trigger, m.PreTokens, m.PostTokens);
                    await acc.SaveSnapshotAsync(_history); // авто-компакт бывает посреди хода — фиксируем сразу
                    break;
                case AskQuestionMessage m:
                    acc.OnAskQuestion(m.ToolUseId, m.Input);
                    await acc.SaveSnapshotAsync(_history);
                    // Э8: вопрос координатора штаба — раунд интервью. Из волны или ожидания
                    // он же возвращает практику в интервью (волны на паузе, карточка + push).
                    if (entry?.Info.TeamImplement is not null)
                    {
                        // Ход задал вопросы — гард молчаливого тупика по его концу молчит (M9)
                        lock (entry.TeamTurnLock) entry.TeamTurnAsked = true;
                        await _teamNotifier.OnAskQuestionStabAsync(sessionId);
                    }
                    break;
                case PlanReviewMessage m:
                    acc.OnPlanReview(m.RequestId, m.Plan);
                    await acc.SaveSnapshotAsync(_history);
                    break;
                case RecallManifestMessage m:
                    if (entry is not null) entry.LastRecallManifest = m;
                    break;
                case PromptSnapshotMessage m:
                    // Привязываем снимок к сообщению, которым начался ход: под ним живёт
                    // кнопка «какой промпт ушёл». Событие приходит ДО result — иначе
                    // FlushAsync уже унёс бы текущий ход из _currentTurn.
                    acc.SetPromptSnapshot(m.SnapshotId);
                    break;
                case ResultMessage m:
                    // Точный якорь границы хода для ветвления (фича chat-branch, §4): uuid
                    // последней записи транскрипта на КОНЕЦ хода. Снимается здесь, потому что
                    // result — последнее событие хода, и дальше файл уже не растёт до
                    // следующего. Не прочиталось — null, чат остаётся на текстовом пути.
                    await acc.OnResultAsync(m.Subtype, m.DurationMs, m.NumTurns, m.Usage, m.TotalCostUsd, m.ApiErrorStatus, m.PermissionDenials, _history, m.ContextTokens, m.UsageModel, m.DurationApiMs,
                        entry is not null ? Llm.Claude.TranscriptProbe.LastRecordUuid(FindResumeTranscript(entry)) : null,
                        m.PromptCache);
                    if (entry is not null) entry.LoopTurnFailed = m.Subtype == "error";
                    // Актор читается здесь, пока ход жив: переход статуса по result и снятие
                    // актора идут ниже по методу (ApplyStatusQueuedAsync) — потребитель после
                    // конца хода получил бы null (урок R-S2a-6c).
                    SpendMapping.RecordTurnSpend(_spend, _llmProviders, ResolveOwnerId, _log, entry?.Info, m, this);
                    break;
                case ProviderSwitchedMessage m:
                    // Пометка автоподмены модели в историю — после F5/рестарта человек видит,
                    // что отвечала не та модель, что был выбран. Уровень 1 (ротация подписок)
                    // модель не трогает — пилюля там не нужна; адаптер шлёт Auto=true без
                    // Model, OnModelSwitched в таком случае no-op.
                    // ErrorDetails — сырой текст погашенной подменой ошибки провайдера: красной
                    // карточки в ленте нет, но текст доступен «Подробностями» внутри пометки,
                    // поэтому кладём его в историю вместе с ней.
                    if (m.Auto && !string.IsNullOrEmpty(m.Model))
                        acc.OnModelSwitched(m.Model, acc.LastStartedModel(), m.Reason, m.ErrorDetails);
                    break;
                case RateLimitMessage m:
                    {
                        // Учёт лимита — единая точка SubscriptionLimitRecorder (её же зовёт шлюз
                        // LLM по заголовкам anthropic-ratelimit-unified-*). Здесь — только реакция
                        // на свой ход. Подавленное событие выходит из обработчика целиком, как и
                        // до выноса: дальше для rate_limit делать нечего.
                        var limit = _limitRecorder.Record(entry?.Info.Provider, m, "turn",
                            () => entry?.Process is FallbackLlmSessionAdapter fb && fb.FallbackTurnActive,
                            $"ход {sessionId}");
                        if (limit == LimitRecordOutcome.Suppressed)
                            return;
                        if (limit == LimitRecordOutcome.Exhausted && entry is not null)
                        {
                            // Сразу перевозим чат на здоровый аккаунт пула — кнопка «Повторить»
                            // упавшего хода пойдёт уже через него. Если переключиться некуда,
                            // а ход реально отбит — предлагаем сторонний провайдер карточкой.
                            TryPoolFailover(sessionId, entry);
                            if (m.Status == "rejected")
                                await OfferProviderFallbackAsync(sessionId, m.ResetsAt);
                        }
                    }
                    break;
                case ErrorMessage m:
                    // Сюда доезжают только настоящие провалы: промежуточную ошибку попытки,
                    // за которой пошла подмена, адаптер наружу не выпускает (её текст едет
                    // в ErrorDetails маркера) — значит и LoopTurnFailed на ней не взводится.
                    // Details — сырой техтекст под «Подробностями» карточки.
                    await acc.OnErrorAsync(m.Text, _history, m.Details, m.Action);
                    // Ошибка хода (в т.ч. упавший старт процесса) — цикл «до готово»
                    // не продолжаем; иначе ретрай-шторм до лимита итераций
                    if (entry is not null) entry.LoopTurnFailed = true;
                    break;
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
                        LastAssistantText(acc, entry.TurnWaiterBaseline), rm.DurationMs, rm.TotalCostUsd,
                        Succeeded: true));
                    break;
                case ErrorMessage em:
                    // Ошибка хода (протухший логин CLI и пр.) — не штатная реплика
                    waiter.TrySetResult(new TurnResult(em.Text, 0, null, Succeeded: false));
                    break;
                case ExitedMessage:
                    // Прерван без result — отдаём то, что ассистент успел написать (обрубок,
                    // не готовая реплика)
                    waiter.TrySetResult(new TurnResult(
                        LastAssistantText(acc, entry.TurnWaiterBaseline), 0, null, Succeeded: false));
                    break;
            }
        }

        // Этап 4 / шаг 2в (MAJOR 1 этап 4, доработка швов): бэкстоп
        // `RestoreWaveWatchdogIfPaused` на любом `ExitedMessage` возвращён. Шим
        // `HandleTeamTurnCompletedShim` тоже зовёт его на `interrupted | crashed`,
        // а `HandleTeamTurnEndAsync:7593` — на `success | failed | egress_down |
        // local_down`: всего бэкстоп здесь выглядит избыточным. НО он покрывает случай,
        // когда план в `LastTeamTurnEnds` не нашёлся (вытеснение потолком 8, чужой
        // `TurnSeq`): шим уходит с WARN и без восстановления, и единственный путь
        // сохранить отсечки сторожа — вызвать здесь, не дожидаясь подписчика.
        // Двойной вызов с шимом/HandleTeamTurnEnd идемпотентен
        // (`WaveStartedAt = DateTime.UtcNow` поверх себя), контракт «один исход —
        // одна публикация» шины не нарушается: бэкстоп жёстко локальный и срабатывает
        // только после того, как downstream получил `ExitedMessage`.
        if (msg is ExitedMessage && entry is not null)
            RestoreWaveWatchdogIfPaused(sessionId, entry);

        // Обновление статуса — всегда, независимо от аккумулятора; SessionManager —
        // ЕДИНСТВЕННЫЙ владелец переходов Session.Status (ClaudeSession статус не пишет).
        // Если OnResultAsync выбросит, статус всё равно обновится.
        if (entry is not null)
        {
            SessionStatus? newStatus = null;

            if (msg is PermissionRequestMessage or AskQuestionMessage or PlanReviewMessage)
            {
                // Вторая карточка поверх висящей наследует признак первой; иначе свободный
                // (не Working) чат = карточка пришла между ходами
                if (entry.Info.Status is not SessionStatus.Waiting)
                    entry.InteractionOutsideTurn = entry.Info.Status is not SessionStatus.Working;
                newStatus = SessionStatus.Waiting;
                // Кэш для replay при JoinSession: после F5 клиент должен снова увидеть
                // карточку, которую ждёт CLI
                entry.PendingInteraction = msg;
            }
            else if (msg is ResultMessage rm)
            {
                // Active (не Finished): клиент по active перезагружает историю хода;
                // финальный Finished выставится по ExitedMessage ниже
                newStatus = rm.Subtype == "error" ? SessionStatus.Error : SessionStatus.Active;
                // Result хода: сбрасываем счётчик auto-resume (B2). Поле служебное — UpdatedAt
                // не двигаем сами (сдвинет ApplyStatusAsync ниже, как при любой смене статуса).
                // Персистится тем же SaveSessions внутри ApplyStatusAsync.
                entry.Info.RestartResumeAttempts = 0;
            }
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

            // Штатная остановка (ADR-033): обрывы (error/exited умирающего процесса) статус не
            // меняют — sessions.json сохранит Working, и LoadSessions отберёт сессию в кандидаты
            // на auto-resume. Working живёт на диске только до следующего старта — там LoadSessions
            // сразу переводит его в Orphaned. result хода пишется как обычно: ход честно
            // завершился, и «продолжать» его после рестарта нельзя.
            if (_stopping && msg is ErrorMessage or ExitedMessage)
                newStatus = null;

            // Конец хода/обрыв — ожидающей карточки больше нет
            if (msg is ResultMessage or ErrorMessage or ExitedMessage)
                entry.PendingInteraction = null;

            if (newStatus.HasValue)
            {
                // P12/P15: отметим момент, когда статус стал Active — sweep в SaveSessions по этой
                // метке доведёт до Finished, если exited прогона не придёт (доживающий прогон при
                // живых фоновых агентах до BgLingerTimeout, подавленный SuppressExited, висящий без
                // работы процесс). Любой иной статус (Working/Waiting — идёт ход/карточка; Finished/
                // Error — терминальный) — «завершённого Active» нет, метку гасим. Под PendingLock:
                // struct DateTimeOffset? читает sweep под тем же локом; lock короткий, без await.
                lock (entry.PendingLock)
                    entry.LastTurnEndedAt = newStatus == SessionStatus.Active ? DateTimeOffset.UtcNow : null;
                // exited — та же доводка, что у sweep: содержимое хода пришло раньше
                // (result/сообщения), а обрыв без result его не добавляет
                // Состояние и SaveSessions — здесь же; рассылка смены статуса уходит в очередь
                // (раньше await на SignalR держал read-loop), порядок «статус → сообщение» сохранён
                await ApplyStatusQueuedAsync(sessionId, entry, newStatus.Value,
                    touchUpdatedAt: msg is not ExitedMessage);
            }

            // Цикл «до готово»: решение о продолжении — по result/error хода, нёсшего
            // протокол цикла (LoopTurnInFlight). Раньше триггером был exited, но после
            // механики доживания exited приходит лишь со смертью прогона — цикл замирал
            // на десятки минут при живых фоновых агентах.
            // В фоне, чтобы не блокировать read-loop адаптера пересозданием процесса.
            // Режим «Командная реализация»: ход штаба закончился — разбираем его маркеры
            // (эскалация, новая работа) и переводим завершённую проверку в ожидание вводной.
            // В фоне по той же причине, что и цикл «до готово»: публикация карточки,
            // уведомление и планирование не должны держать read-loop адаптера.
            // Буфер стрижки маркеров потребляем в ЛЮБОМ чате (он и копится везде): ход
            // закончился — ждать больше нечего, и то, что живая трансляция придерживала как
            // «вдруг это начало маркера» (TrimAmbiguousMarkerTail), дальше не дорастёт ни во
            // что — довешиваем как обычный текст и обнуляем буфер под следующий ход.
            var turnText = "";
            var turnAsked = false;
            if (msg is ResultMessage or ErrorMessage)
            {
                string? catchUpDelta;
                lock (entry.TeamTurnLock)
                {
                    turnText = entry.TeamTurnText.ToString();
                    var finalSafe = TeamProtocolMarkers.StripTeamProtocolMarkers(turnText);
                    // Тот же гард, что в живой трансляции: ход, ответивший ровно маркером,
                    // не должен догнать ленту пробелами вокруг вырезанного маркера.
                    catchUpDelta = finalSafe.Length > entry.TeamTurnShownLength && finalSafe.Trim().Length > 0
                        ? finalSafe[entry.TeamTurnShownLength..] : null;
                    entry.TeamTurnShownLength = 0;
                    entry.TeamTurnText.Clear();
                    entry.TurnSawAngleBracket = false;
                    turnAsked = entry.TeamTurnAsked;
                    entry.TeamTurnAsked = false;
                    // Этап 4 / шаг 1в: текст и план вызова HandleTeamTurnEndAsync кладутся в LastTeamTurnEnds
                    // ниже отдельным вызовом RecordTeamTurnEnd (эта функция сама берёт
                    // TeamTurnLock через реентрантность lock()). Здесь — только осушение буфера.
                }
                if (!string.IsNullOrEmpty(catchUpDelta))
                {
                    // Догоняющая дельта — единственная порция придержанного текста: слить её нельзя
                    var catchUp = new TextDeltaMessage(catchUpDelta);
                    await EnqueueWorkAsync(entry, () => BroadcastAsync(sessionId, catchUp));
                }
            }

            // При поднятом _stopping план штабу не кладём: разбор хода (HandleTeamTurnEndAsync)
            // планирует новую работу, а процессы уже гасятся. Подписчик turn/completed без плана
            // только пишет предупреждение; состояние штаба остаётся на диске как есть.
            if (!_stopping && msg is ResultMessage or ErrorMessage && entry.Info.TeamImplement is not null)
            {
                // Этап 4 / шаг 1в: кладём план вызова HandleTeamTurnEndAsync в LastTeamTurnEnds.
                // Подписчик turn/completed изымает план и асинхронно зовёт HandleTeamTurnEndAsync
                // ровно один раз на ход. Дедуп SkipNextTeamTurnEnd, стоявший здесь прежде, ушёл
                // вместе с переключением: первая запись по ключу TurnSeq выигрывает (двойной
                // терминал ErrorMessage{ExpectResultFollows=true} + ResultMessage того же хода
                // даёт один план, второй no-op), а шина гарантирует единственность публикации
                // turn/completed на ход — на этом держится защита от потери маркера.
                var teamTurnFailed = msg is ErrorMessage or ResultMessage { Subtype: "error" };
                entry.RecordTeamTurnEnd(entry.LastTurnSeq, turnText, teamTurnFailed, turnAsked);
            }

            // Сабагент этого хода оборвался на середине — добиваем его продолжением. Строго по
            // result (ход в процесс уже не идёт): systemDirective-отправка очередь не проходит
            // и запустила бы второй ход параллельно живому.
            //
            // ПОРЯДОК ВАЖЕН: этот блок стоит ПЕРЕД разбором цикла «до готово» ниже. Цикл поднимает
            // следующий ход из Task.Run, и пометка об обрыве (TruncatedBgNote) обязана лечь раньше —
            // иначе она опаздывает ровно на один ход, а на последней итерации цикла теряется совсем.
            // Перенос сделан поведенчески нейтральным: раньше блок стоял ПОСЛЕ сброса
            // LoopTurnInFlight и потому всегда видел флаг ложным — здесь он ещё взведён, поэтому
            // в ShouldNudgeSubagent идёт «ход цикла реально продолжится» (флаг И живой WorkLoop),
            // а не сырой флаг. Иначе снятый посреди хода цикл (WorkLoop=null при взведённом флаге)
            // потерял бы добивание, которое получал до переноса.
            // Ошибочный ход добивать нечем —
            // сначала разбирается ошибка, поэтому ErrorMessage только гасит отметку.
            if (msg is ResultMessage or ErrorMessage && entry.TruncatedSubagent is { } cutAgent)
            {
                entry.TruncatedSubagent = null;
                // Оборвался другой агент — у него своя серия попыток (счётчик per-agentId)
                if (StartsNudgeSeries(entry.NudgeAgentId, cutAgent.AgentId)) entry.SubagentNudges = 0;
                if (msg is ResultMessage && ShouldNudgeSubagent(entry.SubagentNudges,
                        entry.Info.WorkLoop is not null, entry.Info.TeamImplement is not null,
                        HasPending(entry), entry.LoopTurnInFlight && entry.Info.WorkLoop is not null,
                        isInterruptedRun: cutAgent.FinishedBy == "interrupted"))
                {
                    entry.NudgeAgentId = cutAgent.AgentId;
                    var attempt = ++entry.SubagentNudges;
                    _ = Task.Run(async () =>
                    {
                        try { await NudgeTruncatedSubagentAsync(sessionId, cutAgent, attempt); }
                        catch (Exception ex)
                        {
                            Console.Error.WriteLine($"[SessionManager] Добивание сабагента ({sessionId}): {ex.Message}");
                        }
                    });
                }
                else
                {
                    // Добивать не стали (уступили циклу «до готово»/штабу, очередь непуста, потолок
                    // исчерпан, ход ошибочный) — но МОЛЧАТЬ об обрыве нельзя: координатор уже принял
                    // последнюю реплику агента за его итог. Раньше отметка здесь просто гасилась, и в
                    // чате с активным циклом обрыв не оставлял следа вообще — ни добивания, ни
                    // предупреждения (инцидент 25.08.2026, чат «Зависание чатов практики»: обрыв
                    // заметил сам координатор, потому что обрывок случайно не был похож на отчёт).
                    // Пометка уедет префиксом ближайшего хода — а его цикл и штаб поднимут сами,
                    // это и есть их «свой протокол продолжения», ради которого им уступает добивание.
                    entry.TruncatedBgNote = cutAgent;
                }
            }

            if (msg is ResultMessage or ErrorMessage && entry.LoopTurnInFlight)
            {
                entry.LoopTurnInFlight = false;
                if (entry.Info.WorkLoop is not null)
                    _ = Task.Run(async () =>
                    {
                        try { await ContinueWorkLoopAsync(sessionId); }
                        catch (Exception ex)
                        {
                            Console.Error.WriteLine($"[SessionManager] Цикл «до готово» ({sessionId}): {ex.Message}");
                        }
                    });
            }

            // Ход закончился — выпускаем следующее сообщение из очереди (и агентские chats_send,
            // и пользовательские «честной очереди»). По одному за раз: следующее уйдёт по result
            // уже этого хода. Цикл «до готово» приоритетнее агентских — он продолжает работу,
            // начатую человеком, и его директива уже поставлена выше; замороженная «Стоп»
            // очередь Drain'ом не трогается (проверка внутри). Ход, прерванный ради очереди
            // (enqueue + interrupt в SendMessageAsync/SendMessageAndWaitAsync), result не шлёт —
            // процесс убит: его очередь разбирает exited ТОГО ЖЕ прогона (DrainOnExitedRun).
            // Привязка к прогону обязательна: exited доживающего чужого прогона приходит с
            // опозданием до ~30 мин и увёл бы сообщение в умирающий от interrupt адаптер.
            // На штатном конце хода метка гасится — иначе поздний exited этого же прогона
            // доставил бы второе сообщение параллельно уже запущенному ходу. В фоне — как и
            // work-loop, чтобы не держать read-loop адаптера.
            // Компакт-ход кончился (штатно или обрывом) — метка своё отработала. Гейт по
            // прогону обязателен, как у DrainOnExitedRun ниже: exited чужого доживающего
            // прогона (опаздывает до ~30 мин) иначе снял бы защиту с ЖИВОЙ компакции, и
            // PreemptForPending убил бы её — ровно тот вечный Working, ради которого метка и есть.
            if (msg is ResultMessage or ErrorMessage or ExitedMessage && runId != 0 && entry.CompactRun == runId)
                entry.CompactRun = 0;

            var drainOnExited = msg is ExitedMessage && runId != 0 && entry.DrainOnExitedRun == runId;
            if (msg is ResultMessage or ErrorMessage or ExitedMessage && entry.DrainOnExitedRun == runId)
                entry.DrainOnExitedRun = 0;
            // Прогон умер без result (кнопка «Стоп», смерть процесса, ватчдог), а очередь
            // непуста. Раньше эту дыру закрывала метка: её ставила КАЖДАЯ постановка
            // пользовательского сообщения, потому что она же и убивала ход. Теперь обычная
            // отправка ход не трогает, и без разбора здесь сообщение висело бы призраком до
            // следующей отправки (ходовой случай: «Стоп» → сразу отправить исправленный текст,
            // пока exited убитого хода ещё в пути). Гейты: свой прогон (поздний exited чужого
            // доживающего очередь не трогает), у адаптера нет живого прогона CLI и очередь не
            // заморожена — заморозку «Стоп» снимает только возобновление пользователем.
            //
            // Смотрим ИМЕННО HasLiveTurn, не статус и не Busy — оба здесь ложны:
            //  • статус exited уже сбросил Working→Active выше по методу (ApplyStatusAsync);
            //  • Busy включает OrchestrationActive, а боевой адаптер — всегда обёртка
            //    FallbackLlmSessionAdapter, которая отдаёт придержанный exited вниз из
            //    SettleAsync и обнуляет _turn лишь в finally ПОСЛЕ нас: гейт по Busy не
            //    срабатывал бы никогда (найдено на ревью).
            // ClaudeSession обнуляет _run ДО отправки exited, поэтому у мёртвого прогона
            // HasLiveTurn=false, а при уже запущенном следующем ходе — true (второй ход
            // параллельно не уйдёт). Если разбор всё же попадёт в окно живой оркестрации,
            // доставка не потеряется: SendMessageAsync обёртки вернёт ход в Pending через
            // EnqueueBypass, а её finally добьёт разбор сигналом _orchestrationDone. Известное
            // ограничение этой страховки: EnqueueBypassTurn кладёт ход обратно как агентский и
            // теряет Mode — то есть пользовательское сообщение, попавшее в это редкое окно,
            // доедет пузырём «Автоматически» и без своего режима.
            // Дубль с разбором по метке безопасен: DrainInFlight пропустит второй заход.
            var drainOnDeadRun = msg is ExitedMessage && runId != 0 && runId == entry.RunId
                && !entry.QueueFrozen && entry.Process is not { HasLiveTurn: true } && HasPending(entry);
            if (drainOnExited || drainOnDeadRun
                || (msg is ResultMessage or ErrorMessage && !entry.LoopTurnInFlight
                    && (entry.Info.WorkLoop is null || HasContinuingPending(entry))))
            {
                _ = Task.Run(async () =>
                {
                    try { await DrainNextPendingAsync(sessionId); }
                    catch (Exception ex)
                    {
                        Console.Error.WriteLine($"[SessionManager] Разбор очереди сообщений ({sessionId}): {ex.Message}");
                    }
                });
            }
        }

        // Карточка в ленте — старт MCP-вызова, обогнавший её рассылку, уходит следом.
        // Вычисляется здесь, синхронно: аккумулятор принадлежит порядку чтения stdout
        ToolStartedMessage? earlyStarted = msg is ToolUseMessage announced
            && acc.OnToolAnnounced(announced.Id) is { } earlyStart
            ? new ToolStartedMessage(announced.Id, earlyStart) : null;

        if (entry is null)
        {
            // Записи сессии нет (удалена) — наблюдателей у кадра не было и раньше
            if (sendBroadcast) await BroadcastAsync(sessionId, msg);
            if (earlyStarted is not null) await BroadcastAsync(sessionId, earlyStarted);
            return;
        }

        // Дальше рассылку и наблюдателей ведёт потребитель очереди сессии, read-loop свободен
        var broadcastMsg = sendBroadcast;
        await GetPipeline(entry).EnqueueAsync(new PipelineItem(
            async () =>
            {
                if (broadcastMsg) await BroadcastAsync(sessionId, msg);
                if (earlyStarted is not null) await BroadcastAsync(sessionId, earlyStarted);
            },
            msg, entry.Info,
            Droppable: SessionMessagePipeline.IsDroppable(msg),
            EnqueuedAt: Stopwatch.GetTimestamp(),
            // Слитой дельте очередь пересобирает рассылку по новому тексту (кадр без показа не рассылается)
            WorkFor: broadcastMsg ? merged => () => BroadcastAsync(sessionId, merged) : null));
    }

    private SessionMessagePipeline GetPipeline(SessionEntry entry) =>
        LazyInitializer.EnsureInitialized(ref entry.Pipeline,
            () => new SessionMessagePipeline(item => ProcessPipelineItemAsync(entry, item),
                sessionId: entry.Info.Id, sessionKind: () => entry.Info.TaskId is null ? "human" : "executor",
                log: _log));

    // Работа без сообщения для наблюдателей (догоняющая дельта, presence, смена статуса)
    private ValueTask EnqueueWorkAsync(SessionEntry entry, Func<Task> work) =>
        GetPipeline(entry).EnqueueAsync(new PipelineItem(work, null, null, false, Stopwatch.GetTimestamp()));

    // Единственный потребитель очереди сессии: рассылка, затем наблюдатели по порядку
    private async Task ProcessPipelineItemAsync(SessionEntry entry, PipelineItem item)
    {
        var sessionId = entry.Info.Id;
        var kind = entry.Info.TaskId is null ? "human" : "executor";
        ServerMetrics.RecordSessionQueueWait(Stopwatch.GetElapsedTime(item.EnqueuedAt).TotalMilliseconds, kind);

        if (item.Work is { } work)
        {
            try { await work(); }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[SessionManager] Ошибка рассылки сообщения сессии ({sessionId}): {ex.Message}");
            }
        }

        if (item.Msg is null || item.Info is null || OnSessionMessage is not { } observers) return;
        // Multicast Func<> await-ит только Task ПОСЛЕДНЕГО подписчика — ждём всех явно,
        // иначе исключения и незавершённая работа не-последних наблюдателей теряются
        foreach (var observer in observers.GetInvocationList().Cast<Func<Session, ServerMessage, Task>>())
        {
            var started = Stopwatch.GetTimestamp();
            try { await observer(item.Info, item.Msg); }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[SessionManager] Ошибка наблюдателя сессии ({sessionId}): {ex.Message}");
            }
            ServerMetrics.RecordSessionObserverDuration(
                Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                observer.Method.DeclaringType?.Name ?? observer.Target?.GetType().Name ?? "unknown", kind);
        }
    }

    // Цены хода (трекинг/догоняющий учёт/публикация fal и glif) — в TurnCostLedger (S3-2).
    // Здесь только делегирующие обёртки: вызывающие места (приём хода, загрузка истории)
    // остаются в ядре, а их тело — в роли.
    private void TryTrackFalCost(string sessionId, string content) => _costs.TryTrackFalCost(sessionId, content);

    private void TryTrackGlifCost(string sessionId, string content) => _costs.TryTrackGlifCost(sessionId, content);

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

    // То же, что ApplyStatusAsync, но рассылка смены статуса идёт через очередь сессии:
    // состояние и запись sessions.json остаются синхронными, read-loop не ждёт SignalR
    private async Task ApplyStatusQueuedAsync(string sessionId, SessionEntry entry, SessionStatus status,
        bool touchUpdatedAt = true)
    {
        entry.Info.Status = status;
        if (touchUpdatedAt && !entry.Info.IsArchived) entry.Info.UpdatedAt = DateTime.UtcNow;
        SaveSessions();
        var lastMessage = entry.Info.LastMessage;
        var messageCount = entry.Info.MessageCount;
        await EnqueueWorkAsync(entry,
            () => BroadcastStatusChangeAsync(sessionId, entry.Info, status, lastMessage, messageCount));
    }

    // Текст последней реплики ассистента текущего хода (сообщения после baseline) —
    // ответ для синхронного ожидателя SendMessageAndWaitAsync
    private static string LastAssistantText(TurnAccumulator acc, int baseline) =>
        acc.GetAll().Skip(Math.Max(0, baseline)).OfType<StoredTextMessage>()
            .LastOrDefault(t => t.ParentToolUseId is null)?.Text ?? "";

    // Для fire-and-forget задач: ошибку логируем, а не теряем молча. Тело — в DeferredRun
    // (Services.Sessions, S3-0 §3): тот же хелпер нужен и ролям ядра, звать фасад сверху они
    // не могут. Здесь остаётся делегирующая обёртка: места вызова в ядре не переписываются.
    private static void FireAndForget(Task task, string context) => DeferredRun.FireAndForget(task, context);

    // Остановка всех живых адаптеров — вызывается при graceful shutdown приложения,
    // иначе после остановки сервера остаются зомби-процессы (claude + node MCP-серверов)
    public void KillAllProcesses()
    {
        // Первая строка — ДО гашения адаптеров: умирающий процесс присылает ErrorMessage/
        // ExitedMessage, и OnMessageAsync при поднятом признаке гасит их (newStatus = null),
        // не перетирая Working в Error/Active (ADR-033). Новый SaveSessions() здесь НЕ
        // добавляем: его убрали (5c6606282) как источник зависаний остановки (ожидание _saveLock).
        _stopping = true;
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

// Исход смены видимости чата (SetChatVisibility, S2d-7a). Отказы различимы для контроллера:
// NotAuthor — 403, NotSharedProject / HasMessages / AttachmentsMoveFailed / BoundToFeature / TeamOrGroup — 409, NotFound — 404.
public enum ChatVisibilityOutcome { Ok, NotFound, NotAuthor, NotSharedProject, HasMessages, AttachmentsMoveFailed, BoundToFeature, TeamOrGroup }

// AttachmentMoves (S2d-6b-4): карта «старый путь → новый» перенесённых вложений — композеру
// заменить ссылки черновика; null — переносить было нечего.
public record ChatVisibilityResult(ChatVisibilityOutcome Outcome, Session? Session)
{
    public IReadOnlyDictionary<string, string>? AttachmentMoves { get; init; }
}

// Итог завершённого хода для синхронного ожидания (SendMessageAndWaitAsync):
// текст последней реплики ассистента, длительность и стоимость (если провайдер её отдал).
// Succeeded — ход завершился штатным result (true); error/«Стоп» (exited) — false: тогда Reply
// это сообщение об ошибке или обрубок, а не готовая реплика.
public record TurnResult(string Reply, long DurationMs, double? CostUsd, bool Succeeded);

// Результат отправки с ожиданием: Queued — сессия была занята, сообщение встало в очередь
// и уйдёт после текущего хода; QueueFull — очередь переполнена, сообщение отброшено;
// Completed — ход завершился в срок; Running — ход продолжается (wait=none или истёк таймаут).
// Busy остаётся для совместимости сигнатуры, но занятость сама по себе больше не отказ.
public abstract record SendAndWaitResult
{
    public sealed record Busy(SessionStatus CurrentStatus) : SendAndWaitResult;
    // Dispatched — постановка сама форсировала доставку (очередь была пуста, а ход успел
    // кончиться): вызывающему прерывать нечего, ход уже идёт с этим сообщением
    public sealed record Queued(int Position, bool Duplicate, bool Dispatched = false) : SendAndWaitResult;
    public sealed record QueueFull(int Limit) : SendAndWaitResult;
    public sealed record Completed(TurnResult Result) : SendAndWaitResult;
    public sealed record Running : SendAndWaitResult;


}
