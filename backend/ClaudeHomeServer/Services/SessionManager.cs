using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services.Llm;
using ClaudeHomeServer.Services.Prompts;
using Microsoft.AspNetCore.SignalR;

namespace ClaudeHomeServer.Services;

public class SessionManager : IDisposable
{
    private class SessionEntry
    {
        public required Session Info;
        public ILlmSessionAdapter? Process;
        public TurnAccumulator? Accumulator;
        // Кэш последних workflow_progress для replay при подключении нового клиента
        public Dictionary<string, WorkflowProgressMessage> WorkflowProgress = new();
        // Последний манифест recall (F3) — как и workflow_progress, транзитное WS-событие;
        // без кэша клиент, открывший чат уже после хода (или переподключившийся), никогда
        // не увидит «использовано сейчас», хотя ход реально опирался на память/команду.
        public RecallManifestMessage? LastRecallManifest;
        // Текст ответа текущего хода — для поиска маркера завершения цикла «до готово».
        // StringBuilder не потокобезопасен: Append идёт из read-loop адаптера, а ToString/Clear —
        // из ContinueWorkLoopAsync (Task.Run) и SetWorkLoopAsync. Все доступы под LoopTurnLock.
        public System.Text.StringBuilder LoopTurnText = new();
        public readonly object LoopTurnLock = new();
        // Текст ответа текущего хода штаба — для маркера эскалации координатора (Э4).
        // Отдельный буфер от LoopTurnText: у режимов разные потребители, и очистка одного
        // не должна съедать маркер другого (оба режима могут быть включены разом).
        public System.Text.StringBuilder TeamTurnText = new();
        public readonly object TeamTurnLock = new();
        // Сколько символов «очищенного от маркеров» текста текущего хода штаба уже ушло
        // в живую трансляцию (волна 6): считаем от StripTeamProtocolMarkers(TeamTurnText),
        // а не от длины самого TeamTurnText — иначе диффом в дельту попал бы вырезанный
        // маркер. Живёт рядом с TeamTurnText, сбрасывается вместе с ним под TeamTurnLock.
        public int TeamTurnShownLength;
        // В текущем ходу штаба координатор задал вопрос ASK-карточкой (Э8). Гард молчаливого
        // тупика по концу хода смотрит сюда: ход, закончившийся вопросами человеку, — это
        // работающее интервью, а не тупик, и карточка «вопросов не будет» там была бы враньём.
        // Живёт рядом с TeamTurnText и чистится вместе с ним (под TeamTurnLock).
        public bool TeamTurnAsked;
        // Планировщик прямо сейчас строит план по вводной (StartTeamWorkAsync обернул
        // CreateTeamPlanAsync). Гард молчаливого тупика по концу хода смотрит сюда: пока
        // планирование живо, «Planning && WaveNumber == 0» — это работающий планировщик,
        // а не тупик координатора, и карточка «Координатор не понял вводную» была бы ложной
        // тревогой (прод 2026-08-04: тревога поднялась на живом планировании, а через 28 с
        // пришёл готовый план). Память, а не стор: рестарт сервера убивает сам планировщик,
        // и «планирование живо» после него неправда по определению.
        public volatile bool TeamPlanningInFlight;
        // Дубль уведомления эскалации (Minor, волна 3): ветка is_error в ClaudeSession шлёт
        // синтетический ErrorMessage(ExpectResultFollows: true) И следом ResultMessage того же
        // хода — оба матчили `msg is ResultMessage or ErrorMessage` и параллельно дёргали
        // HandleTeamTurnEndAsync, давая гонку с двумя одинаковыми карточками/push. Разбираем
        // ход по ErrorMessage (несёт верный failed=true), а спаренный ResultMessage — глушим.
        public volatile bool SkipNextTeamTurnEnd;
        // Текущий ход штаба поднят сообщением ЧЕЛОВЕКА (M7): авто-подтверждение добавочного
        // плана опирается на «вводная человека и есть точка контроля», поэтому инициатора
        // хода помечаем при запуске (SendDirectAsync / SendMessageAndWaitAsync) — классификация
        // агентской вводной как работы публикует план неподтверждённым и ждёт человека.
        public bool TeamTurnFromHuman;
        // Счётчики бюджета итерации правит и раздача волны, и гейт запуска на ходу-реакции
        public readonly object TeamLock = new();
        // Ход завершился ошибкой (result error / error) — цикл не продолжаем
        public bool LoopTurnFailed;
        // Текущий ход нёс протокол цикла «до готово» (выставляет BuildCliTurnText):
        // продолжение цикла решается по result ИМЕННО этого хода, а не по exited —
        // после механики доживания exited приходит лишь со смертью прогона (до 30 мин
        // при живых фоновых агентах). Чужие ходы (REST-канал агентов, /compact,
        // ходы-продолжения CLI) протокола не несут и цикл не двигают.
        public volatile bool LoopTurnInFlight;
        // Ожидающая карточка взаимодействия (разрешение/вопрос/план) — replay при
        // JoinSession: без него клиент после F5 видел бы «Claude печатает…» без
        // возможности ответить, а CLI ждал бы до часового таймаута
        public ServerMessage? PendingInteraction;
        // Контекст адаптера устарел (смена собеседника / правка персоны) — убирается
        // ЛЕНИВО перед следующим ходом, чтобы не рвать активный ход и доживающих агентов
        public volatile bool AdapterStale;
        // Одиночный per-turn ожидатель хода (SendMessageAndWaitAsync): резолвится в
        // OnMessageAsync на result/error/exited и безусловно обнуляется (Interlocked)
        public TaskCompletionSource<TurnResult>? TurnWaiter;
        // Число сообщений истории до хода — чтобы взять реплику ответа именно этого хода
        public int TurnWaiterBaseline;
        // Сериализует ensure/dispose адаптера. Без него два конкурентных входа
        // (хаб + REST-агент/work-loop/compact) проходили бы check-then-act на Process/
        // AdapterStale и создавали два адаптера → два claude --resume на один транскрипт.
        public readonly SemaphoreSlim EnsureLock = new(1, 1);
        // Сообщения (агентов — chats_send, пользователя — «честная очередь»), пришедшие в
        // занятую сессию: постановка прерывает текущий ход (enqueue + interrupt), и сообщение
        // доставляется по его концу; агентские ход в цикле «до готово» и в штабе не прерывают —
        // там они ждут конца цикла и штатного конца хода соответственно.
        // Только в памяти — при рестарте сессия и так становится Orphaned, доставлять
        // накопленное в умерший контекст незачем. QueueFrozen — «Стоп» заморозил разбор:
        // автодоставки нет до возобновления пользователем (новое сообщение).
        public readonly List<QueuedMessage> Pending = [];
        public readonly Lock PendingLock = new();
        public volatile bool QueueFrozen;
        // Идентификатор текущего прогона адаптера: колбэк КАЖДОГО прогона несёт свой,
        // поэтому exited доживающего процесса отличим от exited текущего (см. LoopTurnInFlight —
        // exited опаздывает до ~30 мин). Присваивается вместе с Process.
        public long RunId;
        // Прогон, прерванный ради доставки вставшего в очередь сообщения (enqueue + interrupt):
        // убитый процесс не шлёт result — только exited, поэтому разбор очереди по exited
        // включается этим полем (штатный триггер разбора висит на result/error). Хранится
        // именно идентификатор прогона, а не флаг: поздний exited ЧУЖОГО (доживающего)
        // прогона иначе увёл бы сообщение в SendDirectAsync на умирающий от interrupt
        // адаптер — из видимой очереди изъято, в семафоре адаптера потеряно. 0 — прерывания нет.
        public long DrainOnExitedRun;
        // Снимок последнего ПОЛЬЗОВАТЕЛЬСКОГО сообщения, ушедшего в работу: по «Стоп» его
        // копия возвращается в композер (как в десктопном Claude). null — ход авто/агентский.
        public volatile UserTurnSnapshot? CurrentTurnSnapshot;
        // Длина цепочки автоматических отчётов, приведшей в этот чат: доклад исполнителя
        // ставит сюда «своя глубина + 1». Гасит лавину «отчёт → реакция → отчёт выше»:
        // человек в переписке её не создаёт, поэтому его ход счётчик обнуляет.
        public volatile int ReportChainDepth;
    }

    // Дальше какой глубины цепочка автоотчётов не идёт. 3 — как у делегирования задач:
    // исполнитель → постановщик → его постановщик, дальше эскалация теряет смысл.
    private const int MaxReportChainDepth = 3;

    // Ожидающее доставки сообщение. Kind: User — сообщение человека из «честной очереди»
    // (доставляется со своими вложениями и режимом, как при обычной отправке); Agent —
    // chats_send/серверные отправки. SenderOrigin заполняется, только если отправитель из
    // ДРУГОГО места (иной проект / вне проектов) — получателю показываем чип-источник,
    // чтобы было видно, откуда прилетело.
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
        IReadOnlyList<string>? AttachedPaths = null, string? Mode = null, string? StaffNote = null);

    public enum PendingKind { Agent, User }

    // Снимок пользовательского сообщения, ушедшего в работу, — для возврата в композер
    // по «Стоп», если пользовательских в очереди не оказалось
    public record UserTurnSnapshot(string Text, IReadOnlyList<string> AttachedPaths, string? Mode);

    // Исход постановки пользовательского сообщения (Hub SendMessage возвращает клиенту):
    // Started — ход запущен сразу; Queued — чат занят/очередь непуста, сообщение встало в
    // серверную очередь и уйдёт по FIFO (оптимистичный баллон рисовать не надо — придёт
    // снимок pending_messages, а доставленное вернётся событием user_message).
    public enum SendUserOutcome { Started, Queued }

    // Потолок очереди на сессию: агент может ретраить, а занятый чат — стоять долго.
    // Переполнение — честный отказ вызывающему, а не молчаливая потеря.
    private const int MaxPendingPerSession = 10;

    private readonly ConcurrentDictionary<string, SessionEntry> _sessions = new();
    // Сквозной счётчик прогонов адаптера (SessionEntry.RunId): один на процесс сервера —
    // сравниваются только прогоны одной сессии, глобальная уникальность лишь упрощает отладку
    private static long _runSeq;
    private readonly ProjectManager _projects;
    private readonly IHubContext<Hubs.SessionHub> _hub;
    private readonly Llm.ICheapTextRunner? _cheap;
    // Планировщик режима «Командная реализация» (Э2); null — режим без планирования
    private readonly TeamPlanningService? _teamPlanning;
    // Платформа внешних модулей: реестр манифестов + выпуск модульных токенов (R7)
    private readonly Modules.ModuleRegistry? _modules;
    private readonly Modules.ModuleTokenService? _moduleTokens;
    private readonly ChatHistoryService _history;
    // Снимки промпта ходов (кнопка «какой промпт ушёл»); null — в тестах
    private readonly PromptSnapshotStore? _promptSnapshots;
    private readonly string _sessionsFilePath;
    private readonly Lock _saveLock = new();
    // Автосохранение сессий каждые 30с
    private static readonly TimeSpan AutoSaveInterval = TimeSpan.FromSeconds(30);
    private Timer? _autoSaveTimer;
    // Сериализует прямую запись стоимости fal.ai в историю неактивных сессий
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
    // Резолвер моделей агентных мест: пустая модель → назначение места → слот тира
    private readonly Llm.ModelAssignmentResolver _assignments;
    private readonly UserStore _users;
    private readonly JwtService _jwt;
    private readonly Microsoft.AspNetCore.Hosting.Server.IServer _server;
    private readonly IConfiguration _config;
    // Сервисный токен MCP-серверов — ОДИН на владельца (задачи/заметки/память/персоны/…
    // используют один и тот же owner-scoped JWT), с перевыпуском до истечения. См. GetServiceToken.
    private readonly ConcurrentDictionary<string, (string Token, DateTime IssuedAt)> _serviceTokens = new();

    // Зрители сессии: sessionId → множество SignalR-соединений в её группе.
    // Уникальность по connectionId: повторный JoinSession того же соединения (клиент
    // перезаходит перед каждым send и при reconnect) не раздувает счётчик, а обрыв
    // соединения без LeaveSession вычищается RemoveConnectionViewers из OnDisconnectedAsync.
    // Нужен PersonaAutomationService чтобы не слать уведомления, когда пользователь и так смотрит чат.
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, byte>> _sessionViewers = new();

    // Добавить зрителя сессии (вызывается из JoinSession)
    public void AddViewer(string sessionId, string connectionId) =>
        _sessionViewers.GetOrAdd(sessionId, _ => new())[connectionId] = 1;

    // Убрать зрителя сессии (вызывается из LeaveSession)
    public void RemoveViewer(string sessionId, string connectionId)
    {
        if (!_sessionViewers.TryGetValue(sessionId, out var conns)) return;
        conns.TryRemove(connectionId, out _);
        if (conns.IsEmpty) _sessionViewers.TryRemove(sessionId, out _);
    }

    // Обрыв соединения (OnDisconnectedAsync): хаб не знает, в какие сессии заходило
    // соединение, — убираем его из всех
    public void RemoveConnectionViewers(string connectionId)
    {
        foreach (var (sid, conns) in _sessionViewers)
        {
            conns.TryRemove(connectionId, out _);
            if (conns.IsEmpty) _sessionViewers.TryRemove(sid, out _);
        }
    }

    // Есть ли хотя бы один зритель у сессии?
    public bool HasViewers(string sessionId) =>
        _sessionViewers.TryGetValue(sessionId, out var conns) && !conns.IsEmpty;

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
    private readonly ClaudeSubscriptionPool _subscriptionPool;
    // Время последней фактической активности аккаунта пула (живой ход/пинг) для идл-пинга
    // подписок (SubscriptionUsageWarmupService); null — в тестах, тогда просто не трогаем.
    private readonly SubscriptionActivityTracker? _activity;
    private readonly ILogger<SessionManager> _log;
    // Драйверы среды исполнения владельцев (local / docker-песочница)
    private readonly Execution.ILauncherFactory _launchers;
    private readonly Execution.SandboxManager _sandbox;
    // Домашняя папка владельца ({база по среде}/{username} либо override из конфига)
    private readonly UserHomeResolver _homes;

    private readonly PersonaAgentFileSync? _agentSync;
    // Git-операции worktree чата (null — в тестах: worktree-фича выключена)
    private readonly Git.GitService? _git;
    // Учёт glif-генераций (null — в тестах или когда фича не настроена)
    private readonly GlifAccountService? _glif;
    // Аналитика расхода токенов (null — в тестах: сбор выключен)
    private readonly Spend.ISpendCollector? _spend;
    // Per-ход slice top-10 god-nodes Code Graph в системный промпт (ADR вариант A);
    // null — в тестах, тогда блок графа в промпт не попадает
    private readonly CodeGraph.CodeGraphPromptProvider? _codeGraphPrompt;
    // Граф кода: уборка снимка отдельного дерева чата при его удалении (ADR-003); null — в тестах
    private readonly CodeGraph.CodeGraphService? _codeGraphs;
    // Watcher'ы файлов: снятие watcher'а отдельного дерева чата при его удалении; null — в тестах
    private readonly FileWatcherService? _fileWatchers;

    public SessionManager(ProjectManager projects, IHubContext<Hubs.SessionHub> hub,
        ChatHistoryService history, IConfiguration config, ILlmSessionAdapterFactory adapters,
        FalCostService falCost, UsageService usage,
        AppSettingsService appSettings, UserStore users, JwtService jwt,
        Microsoft.AspNetCore.Hosting.Server.IServer server,
        LlmProviderRegistry llmProviders,
        NotesKnowledgeService notesKb, FeatureFlagService flags, PersonaManager personas,
        PersonaMemoryService personaMemory, PersonaBindingsService bindings,
        PersonaPromptBuilder promptBuilder,
        ClaudeSubscriptionPool subscriptionPool,
        ILogger<SessionManager> log,
        Execution.ILauncherFactory launchers,
        Execution.SandboxManager sandbox,
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
        // Опционально (в тестах не передаётся): провайдер slice Code Graph в системный промпт
        CodeGraph.CodeGraphPromptProvider? codeGraphPrompt = null,
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
        PromptSnapshotStore? promptSnapshots = null)
    {
        _promptSnapshots = promptSnapshots;
        _teamPlanning = teamPlanning;
        _activity = activity;
        _glif = glif;
        _spend = spend;
        _codeGraphPrompt = codeGraphPrompt;
        _codeGraphs = codeGraphs;
        _fileWatchers = fileWatchers;
        _agentSync = agentSync;
        _cheap = cheap;
        _modules = modules;
        _moduleTokens = moduleTokens;
        _git = git;
        _homes = homes ?? UserHomeResolver.WithoutOverrides(appSettings, sandbox);
        _launchers = launchers;
        _sandbox = sandbox;
        _projects = projects;
        _hub = hub;
        _history = history;
        _adapters = adapters;
        _llmProviders = llmProviders;
        _falCost = falCost;
        _usage = usage;
        _appSettings = appSettings;
        _assignments = assignments ?? new Llm.ModelAssignmentResolver(appSettings);
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
        _subscriptionPool = subscriptionPool;
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

        // Автосохранение: периодический сброс in-memory данных на диск
        _autoSaveTimer = new Timer(_ => SaveSessions(), null,
            AutoSaveInterval, AutoSaveInterval);
    }

    // --- MCP tasks-server ---

    // Базовый URL API для MCP-сервера: среда владельца (из песочницы Kestrel виден
    // как host.docker.internal) → конфиг → адрес Kestrel → дефолт.
    // 0.0.0.0/[::] заменяем на localhost — MCP-сервер ходит с той же машины.
    // Среди адресов Kestrel предпочитаем http: MCP-серверы на node ходят обычным
    // fetch, а боевой серт выписан на внешний домен — по https://localhost они
    // упираются в ERR_TLS_CERT_ALTNAME_INVALID (localhost/127.0.0.1 нет в SAN).
    // Если http-адреса нет вообще, поднимите локальный http-эндпоинт и пропишите
    // McpTasksApiUrl явно — иначе все MCP-прокси (tasks/notes/memory/wsp) отвалятся.
    private string ResolveTasksApiUrl(string? ownerId = null)
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
        return addr.Replace("0.0.0.0", "localhost").Replace("[::]", "localhost").TrimEnd('/');
    }

    // tasks-MCP доступен, когда разрешён персоной (Persona.Tools/привязки), ЛИБО сессия
    // является исполнителем задачи — тогда tasks-MCP форсируется: исполнитель обязан
    // управлять задачей через mcp__tasks__* (иначе ограниченная персона не сможет её
    // ни прочитать, ни завершить и свалится в нерабочий встроенный Task-тул).
    private bool TasksMcpEnabled(string? ownerId, Session session, Persona? persona) =>
        session.TaskExecution || _bindings.EffectiveToolEnabled(ownerId, persona, "tasks");

    // Единый сервисный токен владельца для MCP-серверов (tasks/notes/memory/personas/…):
    // per-owner JWT с перевыпуском за сутки до истечения (сервер может жить дольше срока токена).
    private string GetServiceToken(string ownerId) =>
        _serviceTokens.AddOrUpdate(ownerId,
            id => (_jwt.IssueServiceToken(id), DateTime.UtcNow),
            (id, old) => DateTime.UtcNow - old.IssuedAt > JwtService.ServiceTokenLifetime - TimeSpan.FromDays(1)
                ? (_jwt.IssueServiceToken(id), DateTime.UtcNow)
                : old).Token;

    // Контекст MCP-сервера задач для сессии; null — только для чата без владельца.
    // persona — для кросс-проектных ProjectTasks-привязок (доступ к задачам ДРУГИХ проектов).
    private TasksMcpContext? BuildTasksContext(string? ownerId, string? projectId, Persona? persona = null)
    {
        if (ownerId is null) return null;
        // Перевыпуск за сутки до истечения — сервер может жить дольше срока токена
        var token = GetServiceToken(ownerId);
        var extraScopes = _bindings.BuildExternalTaskScopes(ownerId, persona);
        var extraIds = extraScopes.Select(s => s.ProjectId).Distinct().ToList();
        var extraReadOnly = extraScopes.Where(s => s.ReadOnly).Select(s => s.ProjectId).Distinct().ToList();
        return new TasksMcpContext(ResolveTasksApiUrl(ownerId), token, projectId,
            extraIds.Count > 0 ? extraIds : null, extraReadOnly.Count > 0 ? extraReadOnly : null);
    }

    // Контекст MCP-сервера заметок; null — только для чата без владельца.
    // Модуль комментариев к документам и редких операций — за ключом notes-annotations
    // (дефолт выключен, решение ПО ПЕРСОНЕ: PersonaBindingsService.SectionEnabled).
    private NotesMcpContext? BuildNotesContext(string? ownerId, string? projectId, Persona? persona)
    {
        if (ownerId is null) return null;
        var token = GetServiceToken(ownerId);
        return new NotesMcpContext(ResolveTasksApiUrl(ownerId), token, projectId,
            AnnotationsEnabled: _bindings.SectionEnabled(ownerId, persona, "notes-annotations"));
    }

    // Контекст MCP-сервера виджетов чата: чистый маркер «сессия с владельцем» —
    // серверу не нужны ни API, ни токен, он только валидирует input (HTML рендерит фронт).
    // Фича штатная (без фич-флага), как personas/notifications; персона может выключить
    // сервер Off-привязкой tool:widgets (PersonaBindingsService.ServerToolEnabled).
    private WidgetsMcpContext? BuildWidgetsContext(string? ownerId, Persona? persona) =>
        ownerId is not null && _bindings.ServerToolEnabled(ownerId, persona, "widgets")
            ? new WidgetsMcpContext() : null;

    // Браузер (плагин playwright): нужен по роли тестировщику, остальным персонам — нет.
    // Ключ-надстройка «browser» с дефолтом по пресету (SectionEnabled → SpecialtySections),
    // как git/kb; чат без персоны получает браузер как раньше — ручную проверку страницы
    // человек делает из своего чата. Решение зависит только от персоны, поэтому постоянно
    // в рамках сессии (оно входит в сигнатуру прогона — см. ClaudeRuntimeSettings).
    private bool BrowserEnabled(string? ownerId, Persona? persona) =>
        persona is null || _bindings.SectionEnabled(ownerId, persona, "browser");

    // Контекст MCP-сервера графа кода: инструменты codegraph_* доступны только в чате проекта —
    // граф ключуется проектом (в чате вне проекта искать нечего). Тот же сервисный токен
    // владельца, что у tasks/notes; владение проектом дополнительно проверяет CodeGraphController.
    // rootPath — рабочее дерево сессии (EffectiveRoot): у чата с отдельным worktree свой граф,
    // иначе инструменты смотрели бы в основное дерево, а правки шли в другое (ADR-003).
    // Персона может выключить граф Off-привязкой tool:codegraph — тогда нет ни сервера,
    // ни slice в промпте (BuildCodeGraphProvider).
    private CodeGraphMcpContext? BuildCodeGraphContext(string? ownerId, string? projectId, string sessionId,
        string? rootPath, Persona? persona)
    {
        if (ownerId is null || string.IsNullOrEmpty(projectId)) return null;
        if (!_bindings.ServerToolEnabled(ownerId, persona, "codegraph")) return null;
        var token = GetServiceToken(ownerId);
        return new CodeGraphMcpContext(ResolveTasksApiUrl(ownerId), token, projectId, sessionId, rootPath);
    }

    // Контекст MCP-сервера памяти персоны (тот же сервисный токен владельца, что и tasks/notes).
    // projectId — проект ТЕКУЩЕГО чата (③-3.4: даёт доступ к team_memory_* команды), не scope
    // персоны — см. BuildPersonaLayer: любая персона в проектном чате получает эти инструменты,
    // пишет ли она в команду реально — решает бэкенд-гейт (ProjectsController.TeamMemoryWriteAllowed).
    private MemoryMcpContext BuildMemoryContext(string ownerId, string personaId, string? projectId)
    {
        var token = GetServiceToken(ownerId);
        return new MemoryMcpContext(ResolveTasksApiUrl(ownerId), token, personaId, projectId);
    }

    // Контекст memory-server для проектной сессии БЕЗ персоны: только team_memory_* (③-3.4) —
    // память проекта доступна из ЛЮБОГО чата проекта, не только персонного (personaId пуст →
    // personal-инструменты memory_* не регистрируются, см. mcp/memory-server/index.js).
    private MemoryMcpContext? BuildTeamMemoryContext(string? ownerId, string? projectId) =>
        ownerId is not null && !string.IsNullOrEmpty(projectId)
            ? BuildMemoryContext(ownerId, "", projectId)
            : null;

    // Auto-recall долгой памяти персоны: по тексту хода возвращает markdown-блок релевантных
    // записей (взвешенная сумма PersonaMemoryScorer) + рабочий фокус первым блоком, а вдобавок —
    // айтемы манифеста (что реально подтянулось) для «использовано сейчас» (F3).
    // Failsafe-таймаут; ошибки → null (ход без recall).
    private Func<string, Task<RecallBlock?>> BuildPersonaRecallProvider(string ownerId, string personaId)
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
                var recall = await recallTask;
                if (recall?.Text is null) return null;
                // Манифест: hits личной памяти + команды проекта → айтемы (F3)
                var items = recall.Hits.Select(h => new RecallItem("memory", h.Id, h.Text, null))
                    .Concat(recall.TeamHits.Select(e => new RecallItem("team", e.Id, e.Text, null)))
                    .ToList();
                return new RecallBlock(recall.Text, items);
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
    private Func<string, Task<RecallBlock?>>? BuildRecallProvider(string? ownerId)
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
                var hits = (await searchTask).Where(h => h.Score >= minScore).Take(topK).ToList();
                if (hits.Count == 0) return null;
                var blockText = NotesKnowledgeService.BuildRecallBlock(hits, minScore, topK);
                if (string.IsNullOrWhiteSpace(blockText)) return null;
                // Манифест: hits заметок → айтемы (F3)
                var items = hits.Select(h => new RecallItem("note", h.Id, h.Title, h.Snippet)).ToList();
                return new RecallBlock(blockText, items);
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
        List<string> abortedMidTurn = [];
        foreach (var session in list)
        {
            // Процесс умер при рестарте — "живые" статусы переводим в orphaned
            var wasLive = session.Status is SessionStatus.Working or SessionStatus.Waiting;
            session.Status = session.Status switch
            {
                SessionStatus.Working or SessionStatus.Starting or SessionStatus.Waiting
                    => SessionStatus.Orphaned,
                SessionStatus.Active => SessionStatus.Finished,
                _ => session.Status,
            };
            if (wasLive && session.ClaudeSessionId is not null)
                abortedMidTurn.Add(session.ClaudeSessionId);
            _sessions[session.Id] = new SessionEntry { Info = session };
        }
        // Маркер обрыва в историю оборванных ходов — фоном, чтобы не тормозить старт
        if (abortedMidTurn.Count > 0)
            _ = Task.Run(async () =>
            {
                foreach (var csid in abortedMidTurn)
                    try { await _history.AppendTurnAbortedAsync(csid); }
                    catch (Exception ex)
                    {
                        Console.Error.WriteLine($"[SessionManager] Маркер обрыва хода ({csid}) не записан: {ex.Message}");
                    }
            });
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

    /// <summary>Всего зарегистрированных сессий (для OTel gauge). ConcurrentDictionary.Count — thread-safe, sub-ms.</summary>
    public int ActiveCount => _sessions.Count;

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

    // Ручная группировка чатов (drag-and-drop в списке): назначить родителя либо вынести
    // в корень (parentId == null). Единственная точка записи ParentOverrideId/ParentDetached —
    // поля взаимоисключающие, снаружи их не трогает никто.
    //
    // Чат не «переезжает»: UpdatedAt намеренно НЕ обновляется. Корни сортируются по активности
    // поддерева (chatTree.ts), и отметка времени от перетаскивания перекидывала бы чат наверх
    // списка, будто в нём был ход.
    public Session? SetParent(string sessionId, string? parentId, string ownerId)
    {
        if (GetOwned(sessionId, ownerId) is not { } chat) return null;
        if (!_sessions.TryGetValue(sessionId, out var entry)) return null;

        if (parentId is null)
        {
            entry.Info.ParentOverrideId = null;
            // Гасим авто-связь только у чата, у которого она есть, — иначе обычный чат
            // навсегда унёс бы бессмысленный флаг в sessions.json.
            entry.Info.ParentDetached = chat.TaskId is not null;
            SaveSessions();
            return entry.Info;
        }

        if (parentId == sessionId)
            throw new InvalidOperationException("Чат нельзя вложить в самого себя");
        if (GetOwned(parentId, ownerId) is not { } parent)
            throw new InvalidOperationException("Родительский чат не найден");
        // Разные списки рендерятся на разных экранах: ребёнок из чужого скоупа не нашёл бы
        // родителя в своей выборке и молча всплыл бы в корень (chatTree.ts: byId.has(pid)).
        if (parent.ProjectId != chat.ProjectId)
            throw new InvalidOperationException(
                "Чат можно группировать только внутри одного проекта или списка чатов вне проектов");
        if (IsDescendantOf(parent.Id, sessionId))
            throw new InvalidOperationException("Нельзя вложить чат в его собственный дочерний чат");

        entry.Info.ParentOverrideId = parentId;
        entry.Info.ParentDetached = false;
        SaveSessions();
        return entry.Info;
    }

    // Является ли candidate потомком ancestor по эффективной иерархии (ParentSessionId,
    // т.е. с учётом ручных override). Счётчик шагов — страховка от цикла, уже лежащего
    // в данных: инварианты SetParent новых циклов не создают, но старый sessions.json
    // мог приехать из бэкапа, а обход вверх по кольцу здесь бы завис.
    private bool IsDescendantOf(string candidateId, string ancestorId)
    {
        var cur = GetById(candidateId);
        for (var steps = 0; cur is not null && steps < 256; steps++)
        {
            if (cur.ParentSessionId is not { } pid) return false;
            if (pid == ancestorId) return true;
            cur = GetById(pid);
        }
        return false;
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

    // Рабочая папка чата, принадлежащего пользователю (для загрузки вложений): у чата вне
    // проекта — {дом}/Chats, у проектного — рабочая папка сессии (worktree, иначе корень
    // проекта), чтобы Claude нашёл вложение по относительному пути из своего cwd.
    // null — чужая/несуществующая сессия либо папку определить не удалось.
    public string? GetChatRoot(string sessionId, string ownerId)
    {
        var s = GetById(sessionId);
        if (s is null || ResolveOwnerId(s) != ownerId) return null;
        if (s.ProjectId is null) return ResolveChatRoot(ownerId);
        var root = _projects.GetById(s.ProjectId)?.RootPath;
        return root is null ? null : EffectiveRoot(s, root);
    }

    // Рабочая папка чата вне проекта: {домашняя папка владельца}/Chats (создаётся при отсутствии)

    // Выбрать подписку Claude для новой сессии: если модель Claude (не сторонний провайдер),
    // выбираем из пула подписок (least-loaded из способных обслужить модель — пин Opus
    // не должен попасть на аккаунт без Opus); для сторонних — по модели.
    private string ResolveSubscriptionProvider(string? model)
    {
        var provider = _llmProviders.ResolveByModel(model);
        if (provider is not null)
            return provider.Key;
        return _subscriptionPool.Pick(model);
    }

    // Модель места по назначению (слоты «сильная/средняя/слабая» + таблица назначений + per-user слоты).
    // Подставляется, только когда модель НЕ задана явно и это НЕ resume: у транскрипта
    // resumed-сессии уже зафиксированы своя модель и провайдер, и подмена здесь сменила бы
    // провайдер и упёрлась в guard смены провайдера (400).
    private string? ResolveDefaultModel(string usageKey, string? model, string? resumeSessionId, string? ownerId) =>
        !string.IsNullOrEmpty(resumeSessionId) ? model : _assignments.Resolve(usageKey, model, ownerId);

    // Место применения по признакам сессии — тот же порядок, что у ClaudeSession.UsageKey:
    // исполнитель задач специфичнее персоны, персона специфичнее обычного чата.
    private static string UsageKeyFor(bool taskExecution, string? taskId, string? personaId) =>
        taskExecution || taskId is not null ? Llm.LocalActionCatalog.TasksExecutor
        : !string.IsNullOrWhiteSpace(personaId) ? Llm.LocalActionCatalog.ChatPersona
        : Llm.LocalActionCatalog.ChatNew;

    private string ResolveChatRoot(string ownerId)
    {
        var user = _users.GetById(ownerId)
            ?? throw new KeyNotFoundException($"Пользователь не найден: {ownerId}");
        // Container-пользователи живут в отдельном корне (Sandbox:ProjectsRoot):
        // только он монтируется в песочницу — данные local-пользователей туда не попадают
        var home = _homes.Resolve(user)
            ?? throw new InvalidOperationException(
                UserHomeResolver.NotConfiguredMessage(user.ExecutionEnvironment));
        var path = Path.Combine(home, "Chats");
        Directory.CreateDirectory(path);
        return path;
    }

    // Корень профиля CLI (CLAUDE_CONFIG_DIR) для ключа провайдера сессии: подписка пула
    // (включая "claude", если задана с токеном) — claude-profiles/sub-{key}; ключ CLI-провайдера
    // — claude-profiles/{key}; иначе (локальный Claude — пул пуст, или provider не задан) —
    // пользовательский ~/.claude без оверрайда. Зеркалит выбор env хода в ClaudeSession
    // (BuildOAuthCliEnv / BuildCliEnv). ХОСТОВАЯ раскладка: у container-пользователя профиль
    // переписывается на песочный, поэтому ходить сюда напрямую нельзя — только через
    // ConfigRootFor(ownerId, key), который знает обе раскладки.
    private string ConfigRootForProvider(string? providerKey)
    {
        if (string.IsNullOrEmpty(providerKey))
            return _llmProviders.UserProfileDir;
        if (_llmProviders.GetByKey(providerKey) is not null)
            return _llmProviders.GetProfileDir(providerKey);
        if (_subscriptionPool.All.Any(s => s.Key == providerKey && s.Enabled))
            return _llmProviders.GetProfileDir("sub-" + providerKey);
        return _llmProviders.UserProfileDir;
    }

    // Корень профиля CLI С УЧЁТОМ СРЕДЫ владельца. У container-пользователя ход идёт в
    // песочнице, и DockerProcessRunner.RewriteProfileEnv подменяет профиль на
    // {ProfilesHostDir}/{ownerId}/{ключ}, где ключ — имя папки хостового профиля, а «без
    // оверрайда» (~/.claude) → "default". Ключ выводится именно из имени папки (та же
    // операция, что в RewriteProfileEnv), а не из providerKey: ConfigRootForProvider("claude")
    // отдаёт sub-claude, если запись «claude» задана в пуле с токеном, и наивный маппинг
    // «primary → default» разошёлся бы с реальной раскладкой хода.
    private string ConfigRootFor(string? ownerId, string? providerKey)
    {
        var hostRoot = ConfigRootForProvider(providerKey);
        if (ownerId is null || _users.GetById(ownerId)?.ExecutionEnvironment != ExecutionEnvironments.Container)
            return hostRoot;
        var key = string.Equals(Path.GetFullPath(hostRoot), Path.GetFullPath(_llmProviders.UserProfileDir),
                StringComparison.OrdinalIgnoreCase)
            ? "default"
            : Path.GetFileName(hostRoot.TrimEnd('\\', '/'));
        return Path.Combine(_sandbox.ProfilesHostDir, ownerId, key);
    }

    // Рабочая папка ГЛАЗАМИ CLI: у container-пользователя процесс видит контейнерный путь
    // (/projects/…), по нему же CLI уплощает имя папки транскрипта. Путь вне монтирований
    // песочницы ToRuntime отвергает исключением (аналог SafeJoin) — вызывающий решает сам,
    // отдать его наружу (явная миграция → 400) или деградировать тихо (авто-фейловер).
    private string CwdForOwner(string? ownerId, string hostCwd)
    {
        var launcher = _launchers.ForOwner(ownerId);
        return launcher.IsSandboxed ? launcher.Paths.ToRuntime(hostCwd) : hostCwd;
    }

    // Приёмник снимков промпта для сессии: замыкает её id — адаптер ключа хранилища не знает.
    // null — стор не подключён (тесты): ходы идут как раньше, просто без снимков.
    private Func<PromptSnapshotDraft, string?>? PromptSinkFor(string sessionId) =>
        _promptSnapshots is null ? null : draft => _promptSnapshots.Save(sessionId, draft);

    // Дозапись состава инструментов в снимок: приходит из system/init, уже после его записи
    private Action<string, IReadOnlyList<string>, IReadOnlyList<McpServerInfo>>? PromptToolsSinkFor(string sessionId) =>
        _promptSnapshots is null
            ? null
            : (snapshotId, tools, servers) => _promptSnapshots.AttachCliLayer(sessionId, snapshotId, tools, servers);

    // Рабочая папка сессии: отдельное worktree чата приоритетнее корня проекта.
    // Единая точка подмены cwd — через неё идут обе funnel-точки LlmSessionContext.
    private static string EffectiveRoot(Session session, string fallbackRoot) =>
        session.WorktreePath ?? fallbackRoot;

    // Корень, куда «Командная реализация» пишет файл полного плана (Э8-доп., 2026-08-02):
    // worktree штаба, если он в нём работает, иначе корень проекта. null — чат вне проекта,
    // писать план некуда (глобальный чат — раздел «Состав команды» продуктового плана).
    private string? ResolveTeamPlanRoot(Session session) =>
        session.ProjectId is { } pid && _projects.GetById(pid) is { } project
            ? EffectiveRoot(session, project.RootPath)
            : null;

    // Уборка за удалённым деревом чата (ADR-003): снимаем watcher его файлов и выбрасываем
    // снимок графа из data/code-graphs — иначе он остался бы сиротой на диске, а watcher
    // держал бы handle на исчезнувшую папку. Best-effort: уборка не должна ронять удаление.
    private void ReleaseWorktreeGraph(string sessionId, string worktreePath)
    {
        try { _fileWatchers?.UnwatchPath("worktree:" + sessionId); } catch { /* уборка best-effort */ }
        try { _codeGraphs?.Invalidate(worktreePath); } catch { /* уборка best-effort */ }
    }

    // Рабочая папка сессии (для поиска транскрипта по уплощённому cwd);
    // null — папку определить не удалось (миграцию в этом случае не делаем)
    private string? TryResolveCwd(Session s)
    {
        try
        {
            if (s.ProjectId is not null)
            {
                var root = _projects.GetById(s.ProjectId)?.RootPath;
                return root is null ? null : EffectiveRoot(s, root);
            }
            return s.OwnerId is null ? null : ResolveChatRoot(s.OwnerId);
        }
        catch (Exception)
        {
            return null;
        }
    }

    // Авто-фейловер пула подписок: аккаунт чата исчерпан, а в пуле есть здоровая
    // альтернатива — тихо перевозим транскрипт в её профиль и меняем Session.Provider.
    // Для пользователя незаметно: та же модель, тот же эндпоинт, та же предоплаченная
    // подписка. Сторонние провайдеры сюда не входят — другая модель/качество/оплата,
    // это только явная миграция (MigrateProviderAsync + карточка-предложение).
    private void TryPoolFailover(string sessionId, SessionEntry entry)
    {
        if (_llmProviders.ResolveByModel(entry.Info.Model) is not null) return; // сторонний — не пул
        if (!_subscriptionPool.HasExtra) return;
        var current = entry.Info.Provider ?? ClaudeSubscriptionPool.PrimaryKey;
        if (!_subscriptionPool.IsExhausted(current)) return;
        var pick = _subscriptionPool.Pick(entry.Info.Model);
        if (pick == current || _subscriptionPool.IsExhausted(pick)) return; // переключаться некуда

        var ownerId = ResolveOwnerId(entry.Info);
        if (entry.Info.ClaudeSessionId is not null)
        {
            var hostCwd = TryResolveCwd(entry.Info);
            if (hostCwd is null) return;
            // Транскрипт container-пользователя лежит под КОНТЕЙНЕРНЫМ cwd в песочном
            // профиле. Путь вне монтирований (проект переехал наружу) — не повод ронять
            // ход: фейловер здесь и так деградирует тихо, чат просто ждёт сброса окна
            string cwd;
            try { cwd = CwdForOwner(ownerId, hostCwd); }
            catch (InvalidOperationException ex)
            {
                Console.Error.WriteLine($"[SessionManager] Фейловер пула отменён ({sessionId}): {ex.Message}");
                return;
            }
            if (!TranscriptMigrator.TryMigrate(ConfigRootFor(ownerId, current),
                    ConfigRootFor(ownerId, pick), cwd, entry.Info.ClaudeSessionId, out var error))
            {
                Console.Error.WriteLine($"[SessionManager] Фейловер пула отменён ({sessionId}): {error}");
                return;
            }
        }

        entry.Info.Provider = pick;
        entry.Info.UpdatedAt = DateTime.UtcNow;
        SaveSessions();
        Console.WriteLine($"[SessionManager] Чат {sessionId} переключён на подписку «{pick}» (лимит «{current}»)");
        FireAndForget(BroadcastAsync(sessionId, new ProviderSwitchedMessage(pick, Auto: true)),
            $"broadcast provider_switched ({sessionId})");
    }

    // Карточка «Продолжить на …»: чат родного Claude упёрся в лимит, внутри пула
    // автофейловер (TryPoolFailover) не переключил. Предлагаем варианты: здоровые
    // аккаунты ТОГО ЖЕ пула (пользователь выбирает сам — TryPoolFailover либо уже
    // проверил их все на исчерпание, либо переключаться было некуда) и настроенные
    // сторонние провайдеры. Эфемерно (в history не пишется): после сброса окна
    // предложение неактуально. internal — тестируется без розыгрыша целого хода
    // (см. OfferProviderFallbackAsync_*Tests).
    internal async Task OfferProviderFallbackAsync(string sessionId, string? resetsAt)
    {
        if (!_sessions.TryGetValue(sessionId, out var entry)) return;
        if (_llmProviders.ResolveByModel(entry.Info.Model) is not null) return; // уже сторонний
        var current = entry.Info.Provider ?? ClaudeSubscriptionPool.PrimaryKey;
        if (!_subscriptionPool.IsExhausted(current)) return;

        var subscriptionOptions = _subscriptionPool.All
            .Where(s => s.Key != current && !_subscriptionPool.IsExhausted(s.Key)
                && _subscriptionPool.SupportsModel(s.Key, entry.Info.Model))
            .Select(s => new ProviderFallbackOption(
                s.Key,
                string.IsNullOrWhiteSpace(s.DisplayName) ? s.Key : s.DisplayName,
                entry.Info.Model ?? "",
                Kind: "subscription",
                TierLabel: _subscriptionPool.TierLabel(s.Key),
                Utilization: _subscriptionPool.EffectiveUtilization(s.Key)))
            .Where(o => !string.IsNullOrEmpty(o.Model));

        var providerOptions = _llmProviders.Enabled
            .Select(p => new ProviderFallbackOption(p.Key,
                string.IsNullOrWhiteSpace(p.DisplayName) ? p.Key : p.DisplayName,
                p.Models.FirstOrDefault()?.Id ?? ""))
            .Where(o => !string.IsNullOrEmpty(o.Model));

        var options = subscriptionOptions.Concat(providerOptions).ToList();
        if (options.Count > 0)
            await BroadcastAsync(sessionId, new ProviderLimitMessage(resetsAt, options));
    }

    // Явная миграция начатого чата на другого провайдера (кнопка «Продолжить на …» при
    // исчерпании лимитов). Guard «смена провайдера у начатой сессии — 400» в Update
    // остаётся: здесь обход осознанный — транскрипт CLI локальный, переносим его в
    // профиль целевого провайдера и продолжаем разговор через --resume без потери контекста.
    // subscriptionKey — явный выбор аккаунта ТОГО ЖЕ пула подписок (кнопка карточки с
    // Kind="subscription"): вместо автовыбора Pick пользователь указывает конкретный ключ.
    public async Task<Session> MigrateProviderAsync(string sessionId, string ownerId, string model,
        string? subscriptionKey = null)
    {
        if (GetOwned(sessionId, ownerId) is null || !_sessions.TryGetValue(sessionId, out var entry))
            throw new KeyNotFoundException("Чат не найден");

        var newModel = model?.Trim();
        if (string.IsNullOrEmpty(newModel))
            throw new InvalidOperationException("Не указана модель");

        var target = _llmProviders.ResolveByModel(newModel);
        if (target is { Enabled: false })
            throw new InvalidOperationException(
                $"Провайдер «{target.DisplayName}» не настроен: задай LlmProviders:{target.Key}:ApiKey");

        var currentKey = _llmProviders.ResolveByModel(entry.Info.Model)?.Key
            ?? entry.Info.Provider ?? ClaudeSubscriptionPool.PrimaryKey;

        ClaudeSubscriptionConfig? pickedSub = null;
        string targetKey;
        if (!string.IsNullOrWhiteSpace(subscriptionKey))
        {
            if (target is not null)
                throw new InvalidOperationException("Ключ подписки задан вместе со сторонним провайдером");
            var sub = _subscriptionPool.All.FirstOrDefault(s => s.Key == subscriptionKey);
            if (sub is null)
                throw new InvalidOperationException($"Подписка «{subscriptionKey}» не настроена");
            if (!_subscriptionPool.SupportsModel(sub.Key, newModel))
                throw new InvalidOperationException($"Подписка «{sub.Key}» не поддерживает модель «{newModel}»");
            pickedSub = sub;
            targetKey = sub.Key;
        }
        else
        {
            // Цель: сторонний провайдер — его ключ; родной Claude — доступный аккаунт пула
            targetKey = target?.Key ?? _subscriptionPool.Pick(newModel);
        }

        if (string.Equals(targetKey, currentKey, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Чат уже на этом провайдере");

        if (entry.Info.ClaudeSessionId is not null)
        {
            var hostCwd = TryResolveCwd(entry.Info)
                ?? throw new InvalidOperationException("Не удалось определить рабочую папку чата");
            // У container-пользователя и корни (песочные профили), и cwd (контейнерный путь)
            // другие. Исключение ToRuntime (путь вне монтирований) намеренно не глушим:
            // операция явная, пользователь должен увидеть причину отказа (400)
            var cwd = CwdForOwner(ownerId, hostCwd);
            if (!TranscriptMigrator.TryMigrate(ConfigRootFor(ownerId, currentKey),
                    ConfigRootFor(ownerId, targetKey), cwd, entry.Info.ClaudeSessionId, out var error))
                throw new InvalidOperationException($"Не удалось перенести транскрипт: {error}");
        }

        entry.Info.Model = newModel;
        entry.Info.Provider = targetKey;
        entry.Info.UpdatedAt = DateTime.UtcNow;
        // Адаптер собран под прежнего провайдера (env и сигнатура хода) — убираем лениво,
        // как при смене собеседника: мгновенный dispose рвал бы доживающих агентов
        if (entry.Process is not null) entry.AdapterStale = true;
        // Minor (волна 3): миграция посреди план-фазы (интервью/планирование) на провайдера
        // без поддержки «План» — раньше SavedMode оставался висеть навсегда: селектор режима
        // заблокирован (SetMode отказывает при SavedMode != null), а ход уже идёт не в
        // план-режиме (новый провайдер --permission-mode plan не понимает). Та же деградация
        // «молча остаёмся в прежнем режиме», что и EnterPlanPhaseMode на входе в план-фазу —
        // только тут применяется задним числом, когда план-фаза уже шла на другом провайдере.
        if (entry.Info.TeamImplement is { SavedMode: not null }
            && !_llmProviders.CapabilitiesFor(newModel).SupportsPlanMode)
            RestoreUserMode(sessionId, entry);
        SaveSessions();

        // Явно выбранный аккаунт пула — подпись «на подписке», а не безликое «на AI»
        var switchLabel = pickedSub is not null
            ? $"Продолжено на подписке «{(string.IsNullOrWhiteSpace(pickedSub.DisplayName) ? pickedSub.Key : pickedSub.DisplayName)}»"
            : $"Продолжено на {(target is null ? "AI" : string.IsNullOrWhiteSpace(target.DisplayName) ? target.Key : target.DisplayName)}";
        await BroadcastAsync(sessionId, new ProviderSwitchedMessage(targetKey, newModel, switchLabel));
        Console.WriteLine($"[SessionManager] Чат {sessionId} мигрирован: {currentKey} → {targetKey} ({newModel})");
        return entry.Info;
    }

    public Session? GetById(string id) =>
        _sessions.TryGetValue(id, out var entry) ? entry.Info : null;

    // Состояние делегирования ИДУЩЕГО хода сессии. Спрашивает DenyOnDelegatedTurnAttribute по
    // заголовку MCP-сервера: единственный достоверный источник — живой адаптер, тогда как
    // заголовок/env запекаются при старте процесса и протухают при переиспользовании прогона.
    // Чужая сессия или отсутствие процесса — «обычный ход» (запрет не применяется).
    // Владение проверяем ТОЛЬКО через GetOwned (внутри — ResolveOwnerId): у проектной сессии
    // Session.OwnerId равен null, владелец живёт у проекта. Прямое сравнение с этим полем молча
    // отключало запрет, и делегированный ход спокойно запускал исполнителя — поймано live-тестом,
    // юнит-тесты такое не видят. Один способ резолва владельца на весь класс.
    public TurnDelegationState GetActiveTurnDelegation(string sessionId, string ownerId) =>
        GetOwned(sessionId, ownerId) is not null
            && _sessions.TryGetValue(sessionId, out var entry)
            && entry.Process is { } adapter
            ? new TurnDelegationState(adapter.CurrentTurnAgentDepth, adapter.CurrentTurnSuppressTasksExecute)
            : new TurnDelegationState(0, false);

    // Запомнить заметку-итог сессии (SessionSummaryService) — для обновления при повторной генерации
    public void SetSummaryNoteId(string sessionId, string noteId)
    {
        if (!_sessions.TryGetValue(sessionId, out var entry)) return;
        entry.Info.SummaryNoteId = noteId;
        SaveSessions();
    }

    public async Task<Session> CreateAsync(string projectId, ClaudeMode mode,
        string? resumeSessionId = null, string? name = null, string? model = null, string? agentName = null,
        string? effort = null, string? personaId = null, bool taskExecution = false, string? taskId = null)
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
        };

        await StartNewSessionAsync(session, project.RootPath, project.SystemPrompt,
            () => _projects.GetById(projectId)?.PermissionRules ?? (IReadOnlyList<PermissionRule>)Array.Empty<PermissionRule>());
        return session;
    }

    // Создание чата вне проекта: рабочая папка — {домашняя папка владельца}/Chats,
    // системный промпт — только встроенная часть (rawSystemPrompt=null), без проектных правил.
    public async Task<Session> CreateChatAsync(string ownerId, ClaudeMode mode,
        string? resumeSessionId = null, string? name = null, string? model = null, string? effort = null,
        string? personaId = null, bool taskExecution = false, string? taskId = null)
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

        // Персона без своей модели идёт своим уровнем, без уровня — назначением места «чат с персоной»
        var personaModel = ResolveDefaultModel(Llm.LocalActionCatalog.ChatPersona,
            _assignments.PersonaModel(persona, ownerId), resumeSessionId, ownerId);

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
                Model = personaModel,
                Provider = ResolveSubscriptionProvider(personaModel),
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
        var participants = ValidateParticipants(ownerId, personaIds);
        var leader = participants[0];
        var participantIds = participants.Select(p => p.Id).ToList();
        // Ведущая без своей модели идёт своим уровнем, без уровня — назначением места «чат с персоной»
        var leaderModel = ResolveDefaultModel(Llm.LocalActionCatalog.ChatPersona,
            _assignments.PersonaModel(leader, ownerId), resumeSessionId: null, ownerId);

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
    public Session? SetParticipants(string sessionId, string ownerId, IReadOnlyList<string> personaIds)
    {
        if (!_sessions.TryGetValue(sessionId, out var entry)) return null;
        if (ResolveOwnerId(entry.Info) != ownerId) return null;

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
        if (ids.Count is < 2 or > 8)
            throw new InvalidOperationException("В групповом чате участвуют от 2 до 8 персон");
        return ids.Select(id => _personas.Get(id, ownerId)
            ?? throw new KeyNotFoundException($"Персона не найдена: {id}")).ToList();
    }

    // Чаты владельца, ведущиеся от лица конкретной персоны (для раздела «Персоны»)
    public IReadOnlyList<Session> GetPersonaChats(string ownerId, string personaId) =>
        _sessions.Values
            .Select(e => e.Info)
            .Where(s => s.PersonaId == personaId && ResolveOwnerId(s) == ownerId)
            .OrderByDescending(s => s.UpdatedAt)
            .ToList();

    // Владелец сессии: у чата — OwnerId, у проектной — владелец проекта
    // Единая точка резолва владельца сессии: у проектной — владелец проекта, у чата вне
    // проекта — сама сессия. Единственный источник истины для хаба и контроллеров тоже.
    public string? ResolveOwnerId(Session s) =>
        s.ProjectId is not null ? _projects.GetById(s.ProjectId)?.OwnerId : s.OwnerId;

    // Есть ли у пользователя хоть одна сессия/чат — среда исполнения меняется только «начисто»
    // (корни проектов и профили сред различаются, resume привязан к путям старой среды)
    public bool HasSessionsOwnedBy(string ownerId) =>
        _sessions.Values.Any(e => ResolveOwnerId(e.Info) == ownerId);

    // Все сессии пользователя (проектные + чаты) — для сводки дашборда «Домой»
    public IReadOnlyCollection<Session> GetAllOwnedBy(string ownerId) =>
        _sessions.Values
            .Select(e => e.Info)
            .Where(s => ResolveOwnerId(s) == ownerId)
            .ToList();

    // Персона-слой сессии (промпт характера + контекст памяти + auto-recall + сама персона
    // для гейтов возможностей). Строится одинаково при первом старте и при восстановлении процесса.
    // Промпт — замыкание: адаптер зовёт его на каждый ход, поэтому правки персоны
    // (контракт/характер), смена модели сессии и флаг PersonaSwitched применяются сразу.
    private (Func<string?>? Prompt, MemoryMcpContext? Memory, Func<string, Task<RecallBlock?>>? Recall, Persona? Persona)
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
        {
            // team_memory_* (③-3.4, диета памяти команды ч.3) — по проекту ТЕКУЩЕГО чата, не по
            // scope персоны: состав MCP-инструментов один и тот же у проектных и глобальных персон
            // (инвариант «tools/list не зависит от хода» — тем более не от того, какая персона),
            // а пишет ли персона в команду — решает бэкенд (ProjectsController.TeamMemoryWriteAllowed:
            // Persona.Scope==Project && Persona.ProjectId==id проекта памяти). Глобальная персона в
            // проектном чате получает team_memory_list/search (read-only), персона другого проекта —
            // так же; вне проектного чата (session.ProjectId пуст) команды памяти нет вообще.
            return (prompt, BuildMemoryContext(ownerId, persona.Id, session.ProjectId),
                BuildPersonaRecallProvider(ownerId, persona.Id), persona);
        }
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

    // Per-ход slice top-10 god-nodes Code Graph в системный промпт (ADR вариант A). Per-owner
    // автоматически: rootPath проекта однозначно принадлежит владельцу сессии. Текст хода
    // god-узлам не нужен (они структурны) — замыкаем rootPath и игнорируем аргумент. null —
    // провайдер не injecting (тесты) или сессия без rootPath (чат вне проекта).
    // fallbackRoot — корень проекта у чата с отдельным worktree: пока свой граф дерева не
    // построен, в промпт идёт slice главной ветки с пометкой (ADR-003), а не пустота.
    private Func<string?, Task<string?>>? BuildCodeGraphProvider(string? ownerId, Persona? persona,
        string? rootPath, string? fallbackRoot = null)
    {
        if (_codeGraphPrompt is null || string.IsNullOrWhiteSpace(rootPath)) return null;
        // Off-привязка tool:codegraph убирает и выжимку графа из промпта — заодно с сервером
        if (!_bindings.ServerToolEnabled(ownerId, persona, "codegraph")) return null;
        return _ => _codeGraphPrompt.GetSliceAsync(rootPath, fallbackRoot);
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
                  "спроси его (способ указан в блоке о консультациях с персонами) и передай " +
                  "суть ответа своими словами, явно указав автора.");
        return sb.ToString();
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
                        p.Scope == PersonaScope.Project && p.ProjectId == extProjectId);
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
    private bool ConsultantsEnabled(string? ownerId, Session session, Persona? persona) =>
        session.Participants is { Count: > 1 }
        || _bindings.ServerToolEnabled(ownerId, persona, "consultants");

    // Решение «даём ли персоне сервер персон» (CRUD + persona_ask): ключ tool:personas.
    // В ГРУППОВОМ чате ключ ИГНОРИРУЕТСЯ по той же причине, что и tool:consultants —
    // BuildGroupChatHint безусловно отсылает к блоку о консультациях (MentionsHint из
    // этого же сервера), Off-привязка сняла бы сервер и подсказка стала бы враньём.
    private bool PersonasEnabled(string? ownerId, Session session, Persona? persona) =>
        session.Participants is { Count: > 1 }
        || _bindings.ServerToolEnabled(ownerId, persona, "personas");

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
                // файлы в add-dir видны все, определение сервера дешёвое (ленивый, без процесса)
                var (subagents, _) = SplitConsultants(ownerId, session,
                    _personas.GetForContext(ownerId, projectId).ToList());
                var token = GetServiceToken(ownerId);
                // ProjectId консультанта — проект ТЕКУЩЕГО чата (как у BuildPersonaLayer выше), не
                // scope самого консультанта: приглашённая в проектный workflow глобальная персона
                // тоже должна видеть team_memory_list/search этого проекта (read-only — пишет только
                // персона САМОГО проекта, гейт в ProjectsController.TeamMemoryWriteAllowed).
                var servers = subagents
                    .Where(p => p.MemoryEnabled)
                    .Select(p => new ConsultantMemoryServer(
                        PersonaConsultantToolset.PmemServerKey(p.Handle),
                        ResolveTasksApiUrl(ownerId), token, p.Id, projectId))
                    .ToList();
                var handles = subagents.Select(p => p.Handle).Where(h => !string.IsNullOrWhiteSpace(h)).ToList()!;
                return new PersonaAgentsContext(addDirs, servers, handles);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "План сабагентов-персон не собрался — ход без файловых консультантов");
                return null;
            }
        };
    }

    // Блок-подсказка о консультациях: две группы — сабагенты (Task) и persona_ask
    private string? BuildMentionsHint(string ownerId, Session session, List<Persona> others)
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
        foreach (var p in personas)
        {
            var title = string.IsNullOrWhiteSpace(p.Role) ? p.Name : $"{p.Role} ({p.Name})";
            sb.Append($"- @{p.Handle} — {title}");
            if (p.Scope == PersonaScope.Project && p.ProjectId != currentProjectId
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
        var manage = _bindings.SectionEnabled(ownerId, persona, "personas-manage");
        var automation = _bindings.SectionEnabled(ownerId, persona, "personas-automation");

        var token = GetServiceToken(ownerId);
        return new PersonasMcpContext(ResolveTasksApiUrl(ownerId), token, projectId, selfPersonaId,
            mentionsHint, BindingsEnabled: true,
            extraProjectIds.Count > 0 ? extraProjectIds : null,
            extraPersonaIds.Count > 0 ? extraPersonaIds : null,
            ManageEnabled: manage, AutomationEnabled: automation);
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
        sections.Add("search");

        IReadOnlyList<string>? allowedIds = null;
        var fileScopes = _bindings.BuildFileScopes(ownerId, persona);
        if (fileScopes is { Count: > 0 } || chatScopes is { Count: > 0 })
        {
            // Привязки есть — зона ужимается; проект самой сессии всегда доступен
            var set = new HashSet<string>(fileScopes ?? []);
            if (chatScopes is { Count: > 0 })
                foreach (var id in chatScopes) set.Add(id);
            if (projectId is not null) set.Add(projectId);
            allowedIds = set.ToList();
        }

        var token = GetServiceToken(ownerId);
        return new WorkspaceMcpContext(ResolveTasksApiUrl(ownerId), token, projectId,
            sections, allowedIds, selfSessionId);
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

    // Контекст MCP-сервера уведомлений: обычному чату — всегда, персоне — по роли
    // (модуль автоматизации) либо по явной привязке tool:notifications; Off-привязка
    // выключает в любом случае. Единая точка решения — PersonaBindingsService.NotificationsEnabled
    // (по ПЕРСОНЕ, не по ходу). Тот же сервисный токен, что у tasks/notes/workspace.
    private NotificationsMcpContext? BuildNotificationsContext(string? ownerId, string? personaId, Persona? persona)
    {
        if (ownerId is null) return null;
        if (!_bindings.NotificationsEnabled(ownerId, persona)) return null;
        var token = GetServiceToken(ownerId);
        return new NotificationsMcpContext(ResolveTasksApiUrl(ownerId), token, personaId);
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


    // Назначить/сменить собеседника чату (единый селектор): персону (personaId) ИЛИ
    // стандартного .md-агента Claude (agentName) — взаимоисключающе. Оба пустые = снять.
    // Разрешено и ПО ХОДУ разговора: персона-слой строится на каждый ход, транскрипт
    // продолжается через --resume с новым системным слоем. Модель/усилие подтягиваются
    // из персоны; у начатой сессии — только при том же провайдере (guard «смена
    // провайдера у начатой сессии — 400» нерушим: транскрипт живёт у эндпоинта).
    public Session? SetPersona(string sessionId, string ownerId, string? personaId, string? agentName = null)
    {
        if (!_sessions.TryGetValue(sessionId, out var entry)) return null;
        if (ResolveOwnerId(entry.Info) != ownerId) return null;

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
        // .md-агент и персона взаимоисключающие: назначение одного сбрасывает другого
        var newAgentName = persona is null && !string.IsNullOrWhiteSpace(agentName)
            ? agentName.Trim()
            : null;
        // Спикер реально сменился, только если изменилась личность собеседника (персона ИЛИ
        // .md-агент). Повторное назначение той же персоны — не смена (иначе PersonaSwitched
        // взводился бы навсегда и в ленту лез разделитель «теперь отвечает чужие прошлые ответы»).
        var switching = started &&
            (entry.Info.PersonaId != persona?.Id || entry.Info.AgentName != newAgentName);

        entry.Info.PersonaId = persona?.Id;
        entry.Info.AgentName = newAgentName;
        if (persona is not null)
        {
            // Модель персоны: своя сильнее её уровня (уровень уже развёрнут в модель по
            // слотам владельца) — дальше по тексту работаем только с этим значением.
            // Владелец — ТОЛЬКО через ResolveOwnerId: у проектной сессии Session.OwnerId
            // равен null (он живёт у проекта), и личные слоты тиров молча подменялись бы
            // глобальными — см. комментарий у GetActiveTurnDelegation
            var personaModel = _assignments.PersonaModel(persona, ResolveOwnerId(entry.Info));
            if (!started)
            {
                // ?? — а не присваивание в лоб: у персоны без своей модели чат остаётся на той,
                // что уже подставлена при создании (глобальная «модель по умолчанию»).
                // Раньше здесь её затирало в null, и назначение персоны в свежий чат молча
                // возвращало ход к дефолту CLI (заметнее всего в MCP chats_create + personaId)
                entry.Info.Model = personaModel ?? entry.Info.Model;
                entry.Info.Effort = persona.Effort ?? entry.Info.Effort;
            }
            else if (_llmProviders.ProviderKey(personaModel) == _llmProviders.ProviderKey(entry.Info.Model)
                && _subscriptionPool.SupportsModel(entry.Info.Provider ?? ClaudeSubscriptionPool.PrimaryKey, personaModel))
            {
                // Тот же провайдер И подписка сессии способна обслужить модель персоны —
                // модель применяется со следующего хода; иначе оставляем модель сессии
                // (характер всё равно её): пин Opus на аккаунте без Opus валил бы ход
                entry.Info.Model = personaModel ?? entry.Info.Model;
                entry.Info.Effort = persona.Effort ?? entry.Info.Effort;
            }
        }
        if (switching) entry.Info.PersonaSwitched = true;
        entry.Info.UpdatedAt = DateTime.UtcNow;

        // Адаптер несёт контекст прежнего собеседника (memory-MCP, привязки) — пометим
        // устаревшим. Уборка ЛЕНИВАЯ (EnsureProcessAsync перед следующим ходом), а не
        // немедленная: мгновенный dispose обрывал бы активный ход и убивал доживающих
        // фоновых агентов молча
        if (entry.Process is not null) entry.AdapterStale = true;
        SaveSessions();
    }

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

        var existingHistory = session.ClaudeSessionId != null
            ? await _history.LoadAsync(session.ClaudeSessionId)
            : [];
        var accumulator = new TurnAccumulator(existingHistory, session.ClaudeSessionId);

        var entry = new SessionEntry { Info = session, Accumulator = accumulator };
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

        var adapter = _adapters.Create(session, new LlmSessionContext(rootPath,
            msg => OnMessageAsync(session.Id, accumulator, msg, runId),
            rawSystemPrompt, permissionRules,
            TasksMcp: TasksMcpEnabled(ownerId, session, persona.Persona) ? BuildTasksContext(ownerId, session.ProjectId, persona.Persona) : null,
            NotesMcp: _bindings.EffectiveToolEnabled(ownerId, persona.Persona, "notes") ? BuildNotesContext(ownerId, session.ProjectId, persona.Persona) : null,
            RecallProvider: BuildRecallProvider(ownerId),
            PersonaPromptProvider: persona.Prompt,
            MemoryMcp: persona.Memory ?? BuildTeamMemoryContext(ownerId, session.ProjectId),
            PersonaRecallProvider: persona.Recall,
            ExtraDisallowedTools: BuildExtraDisallowed(ownerId, persona.Persona, session),
            PersonasMcp: BuildPersonasContext(ownerId, session.ProjectId, session, persona.Persona),
            NotificationsMcp: BuildNotificationsContext(ownerId, session.PersonaId, persona.Persona),
            WorkspaceMcp: workspace,
            BindingsProvider: BuildBindingsProvider(ownerId, session.PersonaId, workspace?.Sections),
            CodeGraphProvider: BuildCodeGraphProvider(ownerId, persona.Persona, rootPath, projectRoot),
            PersonaAgentsProvider: BuildPersonaAgentsProvider(ownerId, session, persona.Persona),
            Launcher: _launchers.ForOwner(ownerId),
            ModulesMcp: BuildModulesContext(ownerId),
            WidgetsMcp: BuildWidgetsContext(ownerId, persona.Persona),
            CodeGraphMcp: BuildCodeGraphContext(ownerId, session.ProjectId, session.Id, rootPath, persona.Persona),
            BrowserEnabled: BrowserEnabled(ownerId, persona.Persona),
            PromptSnapshotSink: PromptSinkFor(session.Id),
            PromptSnapshotToolsSink: PromptToolsSinkFor(session.Id),
            CliConfigRoot: ConfigRootFor(ownerId, session.Provider)));
        entry.Process = adapter;
        entry.RunId = runId;

        await adapter.StartAsync();
        SaveSessions();
    }

    // Приём сообщения от пользователя (Hub SendMessage) и серверных отправок (авто-ходы).
    // Пользовательское сообщение в занятом чате встаёт в видимую серверную очередь
    // (pending_messages) и тут же прерывает текущий ход — доставляется немедленно по его
    // концу, а не после пассивного ожидания. Возвращаемый исход (Started/Queued) говорит
    // клиенту, рисовать ли оптимистичный баллон.
    public async Task<SendUserOutcome> SendMessageAsync(string sessionId, string text, IReadOnlyList<string> attachedPaths, string? mode = null, bool systemDirective = false, bool auto = false, string? senderPersonaId = null, bool suppressTasksExecute = false, string? senderOrigin = null, string? staffNote = null)
    {
        if (!_sessions.TryGetValue(sessionId, out var entry))
            throw new InvalidOperationException("Сессия не найдена");

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

            // Режим «Командная реализация» (Э5): вводная человека начинает новую итерацию —
            // бюджет с нуля. Делаем это на приёме сообщения, ДО очереди: иначе вводная,
            // постоявшая в очереди, доехала бы до координатора уже с исчерпанным потолком.
            ResetTeamIterationOnUserInput(sessionId, entry);
            // Э4/M3: ответ на карточку остановки обычным сообщением — равноправная замена
            // её кнопок (спека: «написал, что делать, координатор учёл и пошёл дальше с того
            // же места»). Стадию возвращаем ДО очереди, по тем же правилам, что решение по
            // карточке, — иначе текст человека упирался бы в гейты стадии «ждёт решения».
            ResumeTeamFromDecisionOnUserInput(sessionId, entry);

            // Занятый чат: сообщение встаёт в видимую очередь (pending_messages) и СРАЗУ
            // прерывает текущий ход — доставка идёт по его концу, а не после пассивного
            // ожидания. Пользовательское прерывает всё, включая цикл «до готово»: цикл
            // снимаем СИНХРОННО (как в Interrupt), чтобы exited прерванного хода не
            // запустил автопродолжение.
            var loopActive = entry.Info.WorkLoop is not null;
            var turnInFlight = entry.Info.Status is SessionStatus.Working or SessionStatus.Waiting;
            if (loopActive || turnInFlight)
            {
                // Потолок очереди проверяем ДО побочных эффектов: снятый цикл и разморозка
                // очереди на отказе QueueFull не откатываются, и пользователь получил бы
                // исключение при уже убитом цикле. Проверка предварительная — точная (под
                // PendingLock) остаётся в EnqueuePendingAsync, конкурентная постановка между
                // ними лишь вернёт тот же отказ на шаг позже.
                lock (entry.PendingLock)
                {
                    if (entry.Pending.Count >= MaxPendingPerSession)
                        throw new InvalidOperationException(
                            $"В очереди чата уже {MaxPendingPerSession} сообщений — дождитесь, пока она разберётся");
                }

                if (loopActive)
                {
                    entry.Info.WorkLoop = null;
                    SaveSessions();
                    _ = BroadcastWorkLoopAsync(sessionId, entry);
                }
                // Пользователь возобновил разговор — заморозка «Стоп» снимается ДО постановки,
                // иначе форсаж dispatchNow и разбор по концу хода упёрлись бы в QueueFrozen
                entry.QueueFrozen = false;
                var enqueued = await EnqueuePendingAsync(sessionId, entry, text, senderPersonaId, senderOrigin,
                    agentDepth: 0, kind: PendingKind.User, attachedPaths: attachedPaths, mode: mode);
                if (enqueued is SendAndWaitResult.QueueFull f)
                    throw new InvalidOperationException(
                        $"В очереди чата уже {f.Limit} сообщений — дождитесь, пока она разберётся");
                // Прерываем идущий ход: убитый процесс не даёт result, поэтому очередь
                // разберёт exited (по DrainOnExitedRun прерванного прогона). Занятость — по
                // снимку ДО постановки: свободный между итерациями цикла чат прерывать нечего
                // (доставку форсирует dispatchNow), а перечитывание статуса ЗДЕСЬ увидело бы
                // Working уже своего только что доставленного dispatchNow'ом хода и убило бы
                // его. Тот же ход мог быть доставлен и форсажем самой постановки (Dispatched):
                // сообщение уже в работе — прерывать нечего, иначе убьём собственный ход.
                if (turnInFlight && enqueued is not SendAndWaitResult.Queued { Dispatched: true })
                {
                    // Ход штаба «Командной реализации» убит — result по нему не придёт, а с ним
                    // не придёт и HandleTeamTurnEndAsync, который потребляет буфер маркеров.
                    // Чистим синхронно: иначе маркер мёртвого хода склеился бы с текстом
                    // следующего и применился задним числом — фантомная эскалация и сдвиг
                    // стадии (класс «волны-призрака»).
                    if (entry.Info.TeamImplement is not null)
                    {
                        lock (entry.TeamTurnLock)
                        {
                            entry.TeamTurnText.Clear();
                            entry.TeamTurnShownLength = 0;
                            entry.TeamTurnAsked = false;
                        }
                        entry.SkipNextTeamTurnEnd = false;
                    }
                    entry.DrainOnExitedRun = entry.RunId;
                    entry.Process?.Interrupt();
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
                    entry.QueueFrozen = false;
                    head = entry.Pending[0];
                    entry.Pending.RemoveAt(0);
                    // Потолок не пробивается: голова изъята до добавления
                    entry.Pending.Add(new QueuedMessage(Guid.NewGuid().ToString("N"), text, senderPersonaId,
                        senderOrigin, AgentDepth: 0, DateTime.UtcNow, Kind: PendingKind.User,
                        AttachedPaths: attachedPaths, Mode: mode));
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
            senderPersonaId, suppressTasksExecute, senderOrigin, staffNote: staffNote);
        return SendUserOutcome.Started;
    }

    // Непосредственный запуск хода в процесс (гейты очереди уже пройдены либо не требуются).
    // fromQueue — доставка пользовательского сообщения из очереди: клиент рисовал его
    // призраком, поэтому live-баллон бродкастим так же, как для сервер-инициированных отправок.
    private async Task SendDirectAsync(string sessionId, SessionEntry entry, string text,
        IReadOnlyList<string> attachedPaths, string? mode, bool systemDirective, bool auto,
        string? senderPersonaId, bool suppressTasksExecute, string? senderOrigin, bool fromQueue = false,
        string? staffNote = null)
    {
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
                parsedMode = GuardCompatibleMode(parsedMode, teamForGuard.CoordinatorNoCode);
            if (entry.Info.Mode != parsedMode)
            {
                entry.Info.Mode = parsedMode;
                SaveSessions();
            }
        }

        // M7: помечаем инициатора хода для добавочного авто-подтверждения — ход человека
        // (не авто и не директива) является контрольной точкой, агентский — нет.
        entry.TeamTurnFromHuman = !auto && !systemDirective;

        // Групповой чат: @упоминание участника в тексте переключает активного спикера
        // ДО пересоздания процесса — новый персона-слой применяется уже к этому ходу
        await RouteGroupSpeakerAsync(sessionId, entry, text);

        // Аккаунт чата могли исчерпать другие чаты, пока этот простаивал: перед ходом
        // тихо перевозим его на здоровую подписку пула (та же модель и эндпоинт)
        TryPoolFailover(sessionId, entry);

        // Авто-отправка (командный ход, автоматизация, задача) и доставка пользовательского
        // из очереди (fromQueue — клиент рисовал призрак, а не баллон): клиент не добавлял
        // её оптимистично — показываем сразу, до тяжёлого старта CLI-процесса. ТОЛЬКО в
        // session-группу: клиент открытого чата состоит и в user_/project_-группе, широкая
        // рассылка дублировала сообщение в ленте (см. комментарий у внутриходовых событий).
        if ((auto || fromQueue) && !systemDirective)
            await BroadcastAsync(sessionId,
                new UserMessageMessage(text, attachedPaths.Count > 0 ? attachedPaths : null, senderPersonaId, auto, senderOrigin, StaffNote: staffNote));

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

        entry.Accumulator?.OnUserMessage(text, attachedPaths, systemDirective: systemDirective, auto: auto, senderPersonaId: senderPersonaId, senderOrigin: senderOrigin, staffNote: staffNote);

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
        await entry.Process!.SendMessageAsync(BuildCliTurnText(entry, text), attachedPaths,
            suppressTasksExecute: suppressTasksExecute);
        // Превью чата (LastMessage выставляет адаптер из текста для CLI) — исходным сообщением.
        // Служебные директивы цикла (verifying/continuation) пропускаем: их сырой текст
        // («[СИСТЕМНАЯ ДИРЕКТИВА — …]») человеку в списке чатов не адресован (MINOR B-п.5) —
        // превью остаётся тем, что видел человек в последний раз
        if (!systemDirective)
            entry.Info.LastMessage = text.Length > 100 ? text[..100] + "…" : text;
    }

    // Текст хода для CLI: исходное сообщение + обвязки.
    // Протокол цикла «до готово» — пока Session.WorkLoop активен. Своей вставки ultrawork
    // больше нет: слова ultrawork/ulw ловит keyword-detector плагина oh-my-claudecode.
    private string BuildCliTurnText(SessionEntry entry, string text)
    {
        var result = text;

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

        return result;
    }


    // Отправка сообщения с ожиданием завершения хода — REST-канал агентов (chats_send).
    // Занятая или ждущая человека сессия НЕ отвергает сообщение: оно встаёт в очередь
    // (Queued), обычный ход при этом прерывается — доставка сразу по его концу. Цикл
    // «до готово» и штаб «Командной реализации» агентское НЕ рушит: в цикле сообщение ждёт
    // конца ВСЕГО цикла (гейт разбора очереди по WorkLoop), в штабе — штатного конца хода
    // координатора. Таймаут НЕ отменяет ход: вызывающий получает Running и позже читает
    // результат через историю (chats_history).
    public async Task<SendAndWaitResult> SendMessageAndWaitAsync(string sessionId, string text,
        TimeSpan timeout, int agentDepth = 0, string? senderPersonaId = null,
        string? senderOrigin = null, string? senderChatName = null)
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
        {
            var queued = await EnqueuePendingAsync(sessionId, entry, text, senderPersonaId, senderOrigin, agentDepth,
                senderChatName: senderChatName);
            // Агентское сообщение прерывает текущий ход (доставится сразу по его концу),
            // но НЕ рушит цикл «до готово» и штаб «Командной реализации» — там доклад ждёт
            // штатного конца хода, как раньше. Дубликат и переполнение ничего не прерывают;
            // замороженную «Стоп» очередь агент не возобновляет — прерывать ход тоже не ему.
            // Dispatched — ход успел кончиться между снимком занятости и постановкой, и
            // доставку уже форсировал сам enqueue: прерывать теперь означало бы убить
            // собственный только что запущенный ход.
            if (queued is SendAndWaitResult.Queued { Duplicate: false, Dispatched: false }
                && entry.Info.WorkLoop is null && entry.Info.TeamImplement is null
                && !entry.QueueFrozen)
            {
                entry.DrainOnExitedRun = entry.RunId;
                entry.Process?.Interrupt();
            }
            return queued;
        }

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
                senderChatName: senderChatName);
        entry.TurnWaiterBaseline = entry.Accumulator?.GetAll().Count ?? 0;

        await ApplyStatusAsync(sessionId, entry, SessionStatus.Working);
        entry.Accumulator?.OnUserMessage(text, [], viaAgent: agentDepth >= 1, senderPersonaId: senderPersonaId, senderOrigin: senderOrigin);
        entry.CurrentTurnSnapshot = null; // ход агента — по «Стоп» в композер не возвращается
        entry.TeamTurnFromHuman = false; // ход поднят агентом (chats_send), не человеком (M7)
        await entry.Process!.SendMessageAsync(text, null, agentDepth);

        if (timeout <= TimeSpan.Zero) return new SendAndWaitResult.Running();
        var completed = await Task.WhenAny(tcs.Task, Task.Delay(timeout));
        return completed == tcs.Task
            ? new SendAndWaitResult.Completed(await tcs.Task)
            : new SendAndWaitResult.Running(); // ход продолжается, ожидатель очистит OnMessageAsync
    }

    // === Очередь сообщений занятой сессии ===

    // Поставить сообщение в очередь занятой сессии. Дедуп по (текст, отправитель) — ТОЛЬКО
    // для агентских: прежний контракт chats_send советовал ретраить при отказе, и наивный
    // агент насыпал бы дублей одного и того же текста. Человек может осознанно слать
    // повторы («ещё раз», «продолжи») — его сообщения не дедупятся.
    private async Task<SendAndWaitResult> EnqueuePendingAsync(string sessionId, SessionEntry entry,
        string text, string? senderPersonaId, string? senderOrigin, int agentDepth,
        bool silent = false, bool suppressTasksExecute = false, string? senderChatName = null,
        PendingKind kind = PendingKind.Agent, IReadOnlyList<string>? attachedPaths = null,
        string? mode = null, string? staffNote = null)
    {
        bool dispatchNow;
        int position;
        lock (entry.PendingLock)
        {
            if (kind == PendingKind.Agent
                && entry.Pending.Any(p => p.Kind == PendingKind.Agent && p.Text == text && p.SenderPersonaId == senderPersonaId))
                return new SendAndWaitResult.Queued(entry.Pending.Count, Duplicate: true);
            if (entry.Pending.Count >= MaxPendingPerSession)
                return new SendAndWaitResult.QueueFull(MaxPendingPerSession);

            entry.Pending.Add(new QueuedMessage(Guid.NewGuid().ToString("N"), text, senderPersonaId,
                senderOrigin, agentDepth, DateTime.UtcNow, silent, suppressTasksExecute, senderChatName,
                kind, attachedPaths, mode, staffNote));
            position = entry.Pending.Count;

            // Защита от гонки TOCTOU: статус занятости читается БЕЗ лока выше (в SendMessageAsync/
            // SendMessageAndWaitAsync), а Add — здесь, под PendingLock. Если между чтением и Add ход
            // успел завершиться (статус упал из Working, а ResultMessage уже прошёл и его DrainNextPendingAsync
            // отработал по ЕЩЁ ПУСТОЙ очереди) — сообщение зависнет при свободном чате: триггер автодоставки
            // (по result) уже стрелял, а нового не будет. Форсируем разбор при переходе очереди 0→1: drain
            // идемпотентен (RemoveAt атомарен), повторный безопасен. Запускаем ровно один раз на переход,
            // чтобы конкурентные постановки не стимулировали несколько drain'ов. Условия НЕ срабатывания:
            // замороженная «Стоп» очередь (возобновляет только новое пользовательское сообщение) и активный
            // цикл «до готово» — между итерациями чат на мгновение свободен, но агентское сообщение должно
            // ждать конца ВСЕГО цикла (пользовательское сюда с живым циклом не попадает: SendMessageAsync
            // снимает цикл ДО постановки).
            dispatchNow = position == 1
                && !entry.QueueFrozen
                && entry.Info.WorkLoop is null
                && entry.Info.Status is not (SessionStatus.Working or SessionStatus.Waiting);
        }
        await BroadcastPendingAsync(sessionId, entry);

        // ВНЕ лока: drain сам возьмёт PendingLock и достанет entry из реестра по sessionId.
        if (dispatchNow)
            _ = Task.Run(() => DrainNextPendingAsync(sessionId));

        // Dispatched говорит вызывающему, что доставка уже форсирована: прерывать ход после
        // такой постановки нельзя — это был бы собственный, только что запущенный ход
        return new SendAndWaitResult.Queued(position, Duplicate: false, Dispatched: dispatchNow);
    }

    // Отправить сообщение сразу либо, если чат занят, поставить в очередь — единая точка
    // для серверных отправок (доклад исполнителя). Раньше такие ходы полагались на неявную
    // очередь семафора в адаптере: она невидима, безразмерна и молча теряет ходы при
    // Interrupt. Возвращает true, если сообщение отложено.
    public async Task<bool> SendOrEnqueueAsync(string sessionId, string text,
        string? senderPersonaId = null, string? senderOrigin = null,
        bool silent = false, bool suppressTasksExecute = false, string? staffNote = null)
    {
        if (!_sessions.TryGetValue(sessionId, out var entry))
            throw new InvalidOperationException("Сессия не найдена");

        if (entry.Info.Status is SessionStatus.Working or SessionStatus.Waiting)
        {
            await EnqueuePendingAsync(sessionId, entry, text, senderPersonaId, senderOrigin,
                agentDepth: 0, silent, suppressTasksExecute, staffNote: staffNote);
            return true;
        }

        await SendMessageAsync(sessionId, text, [], auto: true, senderPersonaId: senderPersonaId,
            suppressTasksExecute: suppressTasksExecute, senderOrigin: senderOrigin, staffNote: staffNote);
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
        if (GetOwned(sessionId, ownerId) is not { } chat) return ReportUpResult.NotFound;
        if (!_sessions.TryGetValue(sessionId, out var from)) return ReportUpResult.NotFound;
        if (chat.ParentSessionId is not { } parentId) return ReportUpResult.NoParent;
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
    // запускается сразу. Иначе «я застрял» лежит в ленте штаба до конца волны, а координатор
    // всё это время ждёт докладов о завершении, которых не будет.
    // Родитель — чат-штаб «Командной реализации» → человек дополнительно получает карточку
    // остановки с кнопками (молчаливых остановок в режиме не бывает).
    public async Task<ReportUpResult> ReportBlockerAsync(string sessionId, string text, string ownerId)
    {
        var chat = GetOwned(sessionId, ownerId);
        var parentId = chat?.ParentSessionId;

        // Пробуждение штаба — платный ход, инициированный агентом, поэтому оно под квотой:
        // иначе исполнитель поднимал бы координатора докладом-блокером в бесконечном цикле,
        // не расходуя ни одной другой единицы бюджета (та же лавина, только с другого входа).
        var wake = parentId is null ? (true, true, null) : TryConsumeTeamWakeup(parentId);

        if (!wake.Allowed)
        {
            // Квота выбрана либо практика остановлена — ход не поднимаем, НО молча не
            // отходим: застрявший исполнитель без карточки означал бы ровно то зависание,
            // которого в режиме быть не должно. Доклад ложится в ленту, человек — видит
            // карточку и push, а координатор проснётся уже по его решению.
            var quiet = await ReportUpAsync(sessionId, TeamImplementPrompts.BlockerReportText(text),
                ownerId, withTurn: false);
            // Карточку и push шлём ОДИН раз на остановку: практика уже ждёт решения человека
            // (стадия awaitingDecision), и каждый следующий блокер волны добавлял бы к той же
            // причине ещё одну карточку и ещё один push — спам вместо сигнала.
            if (quiet is (ReportUpResult.Delivered or ReportUpResult.Queued)
                && GetById(parentId!) is { TeamImplement: { } blockedTeam } blockedStab
                && blockedTeam.Stage != TeamImplementStage.AwaitingDecision)
            {
                var card = new TeamEscalation
                {
                    Kind = blockedTeam.Stopped ? TeamEscalationKind.Stopped : TeamEscalationKind.BudgetExhausted,
                    Title = blockedTeam.Stopped
                        ? "Исполнитель застрял, а практика остановлена"
                        : "Исполнитель застрял, а бюджет итерации израсходован",
                    Details = $"{text.Trim()}\n\nКоординатор не разбужен: {wake.Reason}.\n\n"
                              + TeamImplementPrompts.BudgetLine(blockedTeam.Budget),
                    TaskId = chat?.TaskId,
                    Wave = blockedTeam.WaveNumber,
                    Actions = TeamEscalationActions.For(blockedTeam.Stopped
                        ? TeamEscalationKind.Stopped
                        : TeamEscalationKind.BudgetExhausted),
                };
                if (TeamEscalationRaiser is { } raiseBlocked) await raiseBlocked(blockedStab, card);
                else await PublishTeamEscalationAsync(parentId!, card);
            }
            _log.LogWarning("Доклад-блокер из чата {SessionId}: ход штаба не запущен ({Reason})", sessionId, wake.Reason);
            return quiet;
        }

        var result = await ReportUpAsync(sessionId, TeamImplementPrompts.BlockerReportText(text), ownerId,
            withTurn: true, reactionPrompt: TeamImplementPrompts.BlockerReactionTurn(chat?.Name));
        if (result is not (ReportUpResult.Delivered or ReportUpResult.Queued))
        {
            // Пробуждение списано выше (wake.Allowed), а доклад не дошёл (TooDeep/NoParent/
            // NotFound) — координатор фактически не разбужен, платить команде не за что (m3)
            if (parentId is not null) RefundTeamWakeup(parentId);
            return result;
        }

        if (parentId is not null && GetById(parentId) is { TeamImplement: { } team } stab)
        {
            var escalation = new TeamEscalation
            {
                Kind = TeamEscalationKind.Blocker,
                Title = TeamImplementPrompts.EscalationTitle(TeamEscalationKind.Blocker, text),
                Details = text,
                TaskId = chat?.TaskId,
                Wave = team.WaveNumber,
                Actions = TeamEscalationActions.For(TeamEscalationKind.Blocker),
            };
            if (TeamEscalationRaiser is { } raise) await raise(stab, escalation);
            else await PublishTeamEscalationAsync(parentId, escalation);
        }
        return result;
    }

    // Снимок очереди для клиента и REST
    public IReadOnlyList<QueuedMessage> GetPending(string sessionId)
    {
        if (!_sessions.TryGetValue(sessionId, out var entry)) return [];
        lock (entry.PendingLock) return entry.Pending.ToList();
    }

    // Отменить ожидающее сообщение (крестик на карточке-призраке). false — уже доставлено.
    public async Task<bool> CancelPendingAsync(string sessionId, string messageId)
    {
        if (!_sessions.TryGetValue(sessionId, out var entry)) return false;
        bool removed;
        lock (entry.PendingLock) removed = entry.Pending.RemoveAll(p => p.Id == messageId) > 0;
        if (removed) await BroadcastPendingAsync(sessionId, entry);
        return removed;
    }

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

    // Достать следующее сообщение и отправить его обычным ходом. Вызывается по концу хода
    // (OnMessageAsync). Замороженная «Стоп» очередь не разбирается автоматически — только
    // возобновление (новое пользовательское сообщение при свободном чате) снимает заморозку.
    // Ошибка отправки не должна ронять разбор — сообщение уже снято с очереди, иначе оно
    // застряло бы навсегда и блокировало остальные.
    private async Task DrainNextPendingAsync(string sessionId)
    {
        if (!_sessions.TryGetValue(sessionId, out var entry)) return;
        if (entry.QueueFrozen) return;

        QueuedMessage? next;
        lock (entry.PendingLock)
        {
            if (entry.QueueFrozen) return;
            next = entry.Pending.FirstOrDefault();
            if (next is not null) entry.Pending.RemoveAt(0);
        }
        if (next is null) return;

        await BroadcastPendingAsync(sessionId, entry);
        await DeliverPendingAsync(sessionId, entry, next);
    }

    // Доставка конкретного сообщения из очереди обычным ходом (без повторных гейтов очереди).
    // Пользовательское идёт со своими вложениями и режимом; агентское — как серверная отправка.
    private async Task DeliverPendingAsync(string sessionId, SessionEntry entry, QueuedMessage next)
    {
        try
        {
            if (next.Kind == PendingKind.User)
                // fromQueue: true — клиент рисовал призраком, бродкастим live-баллон (как auto).
                // Режим уже применён при постановке, повторно не передаём, чтобы не сбросить
                // возможную смену режима после (SetMode) — Info.Mode источник правды.
                await SendDirectAsync(sessionId, entry, next.Text,
                    next.AttachedPaths ?? [], mode: next.Mode, systemDirective: false, auto: false,
                    senderPersonaId: next.SenderPersonaId, suppressTasksExecute: next.SuppressTasksExecute,
                    senderOrigin: next.SenderOrigin, fromQueue: true);
            else
                await SendMessageAsync(sessionId, next.Text, [], auto: true,
                    senderPersonaId: next.SenderPersonaId, senderOrigin: next.SenderOrigin,
                    suppressTasksExecute: next.SuppressTasksExecute, staffNote: next.StaffNote);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[SessionManager] Доставка отложенного сообщения ({sessionId}): {ex.Message}");
        }
    }

    // Клиенту уходят только видимые элементы: silent — ход-реакция на доклад, чей текст
    // уже лежит в ленте отдельной репликой, призрак дублировал бы её служебным промптом.
    // Kind/AttachedPaths/Mode — только для пользовательских (карточка-призрак и композер).
    private Task BroadcastPendingAsync(string sessionId, SessionEntry entry) =>
        BroadcastSessionMessageAsync(sessionId, new PendingMessagesMessage(VisiblePending(entry)));

    private static IReadOnlyList<PendingMessageDto> VisiblePending(SessionEntry entry)
    {
        lock (entry.PendingLock)
            return [.. entry.Pending.Where(p => !p.Silent).Select(p => new PendingMessageDto(
                p.Id, p.Text, p.SenderPersonaId, p.SenderOrigin, p.EnqueuedAt, p.SenderChatName,
                p.Kind == PendingKind.User ? "user" : "agent",
                p.AttachedPaths, p.Kind == PendingKind.User ? p.Mode : null))];
    }

    // Снимок для replay при JoinSession (без служебных)
    public IReadOnlyList<PendingMessageDto> GetVisiblePending(string sessionId) =>
        _sessions.TryGetValue(sessionId, out var entry) ? VisiblePending(entry) : [];

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
        var accumulator = entry.Accumulator;
        if (accumulator is null)
        {
            // Оживление под _falPersistLock — сериализуем с прямой записью fal-стоимости в
            // историю неактивной сессии (PublishFalCostAsync): иначе LoadAsync тут и запись там
            // теряли бы друг друга (lost update). Повторная проверка под локом.
            await _falPersistLock.WaitAsync();
            try
            {
                accumulator = entry.Accumulator;
                if (accumulator is null)
                {
                    var existingHistory = entry.Info.ClaudeSessionId != null
                        ? await _history.LoadAsync(entry.Info.ClaudeSessionId)
                        : [];
                    accumulator = new TurnAccumulator(existingHistory, entry.Info.ClaudeSessionId);
                    entry.Accumulator = accumulator;
                }
            }
            finally { _falPersistLock.Release(); }
        }

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
            context = new LlmSessionContext(rootPath,
                msg => OnMessageAsync(sessionId, accumulator, msg, runId),
                RawSystemPrompt: null, PermissionRules: null,
                TasksMcp: TasksMcpEnabled(entry.Info.OwnerId, entry.Info, persona.Persona) ? BuildTasksContext(entry.Info.OwnerId, null, persona.Persona) : null,
                NotesMcp: _bindings.EffectiveToolEnabled(entry.Info.OwnerId, persona.Persona, "notes") ? BuildNotesContext(entry.Info.OwnerId, null, persona.Persona) : null,
                RecallProvider: BuildRecallProvider(entry.Info.OwnerId),
                PersonaPromptProvider: persona.Prompt,
                MemoryMcp: persona.Memory,
                PersonaRecallProvider: persona.Recall,
                ExtraDisallowedTools: BuildExtraDisallowed(entry.Info.OwnerId, persona.Persona, entry.Info),
                PersonasMcp: BuildPersonasContext(entry.Info.OwnerId, null, entry.Info, persona.Persona),
                NotificationsMcp: BuildNotificationsContext(entry.Info.OwnerId, entry.Info.PersonaId, persona.Persona),
                WorkspaceMcp: workspace,
                BindingsProvider: BuildBindingsProvider(entry.Info.OwnerId, entry.Info.PersonaId, workspace?.Sections),
                CodeGraphProvider: BuildCodeGraphProvider(entry.Info.OwnerId, persona.Persona, rootPath),
                PersonaAgentsProvider: BuildPersonaAgentsProvider(entry.Info.OwnerId, entry.Info, persona.Persona),
                Launcher: _launchers.ForOwner(entry.Info.OwnerId),
                ModulesMcp: BuildModulesContext(entry.Info.OwnerId),
                WidgetsMcp: BuildWidgetsContext(entry.Info.OwnerId, persona.Persona),
                // Чат вне проекта — графа кода нет (он ключуется проектом)
                CodeGraphMcp: null,
                BrowserEnabled: BrowserEnabled(entry.Info.OwnerId, persona.Persona),
                PromptSnapshotSink: PromptSinkFor(entry.Info.Id),
                PromptSnapshotToolsSink: PromptToolsSinkFor(entry.Info.Id),
                CliConfigRoot: ConfigRootFor(entry.Info.OwnerId, entry.Info.Provider));
        }
        else
        {
            var project = _projects.GetById(entry.Info.ProjectId)
                ?? throw new InvalidOperationException("Проект не найден");
            var persona = BuildPersonaLayer(entry.Info, project.OwnerId);
            var workspace = BuildWorkspaceContext(project.OwnerId, project.Id, entry.Info.Id, persona.Persona);
            var rootPath = EffectiveRoot(entry.Info, project.RootPath);
            context = new LlmSessionContext(rootPath,
                msg => OnMessageAsync(sessionId, accumulator, msg, runId),
                project.SystemPrompt,
                () => _projects.GetById(entry.Info.ProjectId!)?.PermissionRules ?? (IReadOnlyList<PermissionRule>)Array.Empty<PermissionRule>(),
                TasksMcp: TasksMcpEnabled(project.OwnerId, entry.Info, persona.Persona) ? BuildTasksContext(project.OwnerId, project.Id, persona.Persona) : null,
                NotesMcp: _bindings.EffectiveToolEnabled(project.OwnerId, persona.Persona, "notes") ? BuildNotesContext(project.OwnerId, project.Id, persona.Persona) : null,
                RecallProvider: BuildRecallProvider(project.OwnerId),
                PersonaPromptProvider: persona.Prompt,
                MemoryMcp: persona.Memory ?? BuildTeamMemoryContext(project.OwnerId, project.Id),
                PersonaRecallProvider: persona.Recall,
                ExtraDisallowedTools: BuildExtraDisallowed(project.OwnerId, persona.Persona, entry.Info),
                PersonasMcp: BuildPersonasContext(project.OwnerId, project.Id, entry.Info, persona.Persona),
                NotificationsMcp: BuildNotificationsContext(project.OwnerId, entry.Info.PersonaId, persona.Persona),
                WorkspaceMcp: workspace,
                BindingsProvider: BuildBindingsProvider(project.OwnerId, entry.Info.PersonaId, workspace?.Sections),
                CodeGraphProvider: BuildCodeGraphProvider(project.OwnerId, persona.Persona, rootPath, project.RootPath),
                PersonaAgentsProvider: BuildPersonaAgentsProvider(project.OwnerId, entry.Info, persona.Persona),
                Launcher: _launchers.ForOwner(project.OwnerId),
                ModulesMcp: BuildModulesContext(project.OwnerId),
                WidgetsMcp: BuildWidgetsContext(project.OwnerId, persona.Persona),
                CodeGraphMcp: BuildCodeGraphContext(project.OwnerId, project.Id, entry.Info.Id, rootPath, persona.Persona),
                BrowserEnabled: BrowserEnabled(project.OwnerId, persona.Persona),
                PromptSnapshotSink: PromptSinkFor(entry.Info.Id),
                PromptSnapshotToolsSink: PromptToolsSinkFor(entry.Info.Id),
                CliConfigRoot: ConfigRootFor(project.OwnerId, entry.Info.Provider));
        }
        var adapter = _adapters.Create(entry.Info, context);
        entry.Process = adapter;
        entry.RunId = runId;
        await adapter.StartAsync();
    }

    // Заголовок чата из первого сообщения: первая строка, обрезанная до разумной длины.
    // Ход командной механики (/team-implement {...}, /oh-my-claudecode:ralplan "тема"…) — не
    // текст для человека, поэтому сначала пробуем вытащить тему из обвязки (см.
    // ExtractTeamMechanicTopic), а к сырому тексту падаем только если распознать не удалось.
    private static string MakeChatTitle(string text)
    {
        var topic = ExtractTeamMechanicTopic(text);
        var t = (string.IsNullOrWhiteSpace(topic) ? text : topic).Trim();
        var nl = t.IndexOfAny(['\n', '\r']);
        if (nl >= 0) t = t[..nl].Trim();
        const int max = 48;
        if (t.Length > max) t = string.Concat(t.AsSpan(0, max).TrimEnd(), "…");
        return t;
    }

    // Слаги командных механик с JSON-аргументами — тема лежит в одном из полей ниже
    // (порядок — приоритет: первое непустое). Зеркалит describeTeamTurn во frontend/teamMechanics.ts.
    private static readonly string[] JsonMechanicSlugs =
        ["/panel-of-experts", "/review-consilium", "/red-team", "/team-implement"];
    private static readonly string[] JsonTopicKeys = ["task", "topic", "target", "brief"];

    // Слаги строковых механик — тема в кавычках (см. quoteTopic во frontend/teamMechanics.ts:
    // внутренние " заменены на «», поэтому кавычки темы — первая и единственная пара).
    private static readonly string[] QuotedTopicSlugs =
    [
        "/oh-my-claudecode:ralplan", "/oh-my-claudecode:deep-interview",
        "/oh-my-claudecode:autopilot", "/oh-my-claudecode:trace", "/oh-my-claudecode:sciomc",
    ];
    private const string UltraqaSlug = "/oh-my-claudecode:ultraqa";
    private static readonly Regex QuotedTopicRegex = new("\"([^\"]*)\"", RegexOptions.Compiled);

    // Тема хода командной механики или null, если текст — не вызов известной механики
    // (обычное сообщение, режим «Командная реализация» без обвязки — тема уходит как есть).
    private static string? ExtractTeamMechanicTopic(string text)
    {
        var trimmed = text.TrimStart();
        if (trimmed.Length == 0 || trimmed[0] != '/') return null;

        foreach (var slug in JsonMechanicSlugs)
        {
            if (!trimmed.StartsWith(slug, StringComparison.Ordinal)) continue;
            var braceIdx = trimmed.IndexOf('{');
            if (braceIdx < 0) return null;
            try
            {
                using var doc = JsonDocument.Parse(trimmed[braceIdx..]);
                foreach (var key in JsonTopicKeys)
                {
                    if (doc.RootElement.TryGetProperty(key, out var val) && val.ValueKind == JsonValueKind.String)
                    {
                        var s = val.GetString();
                        if (!string.IsNullOrWhiteSpace(s)) return s;
                    }
                }
            }
            catch (JsonException) { /* битый JSON — падаем к обрезке сырого текста */ }
            return null;
        }

        foreach (var slug in QuotedTopicSlugs)
        {
            if (!trimmed.StartsWith(slug, StringComparison.Ordinal)) continue;
            var m = QuotedTopicRegex.Match(trimmed);
            return m.Success ? m.Groups[1].Value : null;
        }

        if (trimmed.StartsWith(UltraqaSlug, StringComparison.Ordinal))
        {
            var rest = trimmed[UltraqaSlug.Length..].TrimStart();
            if (rest.StartsWith("--", StringComparison.Ordinal))
            {
                var spaceIdx = rest.IndexOf(' ');
                rest = spaceIdx >= 0 ? rest[(spaceIdx + 1)..] : "";
            }
            return rest.Trim();
        }

        return null;
    }

    // Уточнение авто-заголовка чата (действие chat-title) по маршруту, назначенному месту в
    // «Поставщиках моделей» (локаль/direct-модель/слот — решает CheapTextRunner.RunAsync).
    // Best-effort: молчим при любой проблеме, не трогаем имя, переименованное вручную.
    private async Task RefineChatTitleAsync(string sessionId, string firstMessage, string expectedTitle, string? ownerId)
    {
        if (_cheap is null) return;
        try
        {
            var prompt =
                "Придумай короткий заголовок (3-6 слов, по-русски, без кавычек и точки в конце) для чата " +
                "по первому сообщению пользователя. " + Llm.TitleExtraction.JsonHint + "\n\n" +
                (firstMessage.Length > 1500 ? firstMessage[..1500] : firstMessage);
            var raw = await _cheap.RunAsync(Llm.LocalActionCatalog.ChatTitle, prompt,
                ownerId: ownerId, jsonFormat: Llm.TitleExtraction.Schema);
            var line = Llm.TitleExtraction.Extract(raw);
            if (line is null || line.Length > 80) return;

            if (!_sessions.TryGetValue(sessionId, out var entry)) return;
            // Пользователь мог переименовать вручную, пока модель думала — тогда не трогаем
            if (entry.Info.Name != expectedTitle) return;
            entry.Info.Name = line;
            entry.Info.UpdatedAt = DateTime.UtcNow;
            SaveSessions();
            await BroadcastChatRenamedAsync(sessionId, entry.Info, line);
        }
        catch (Exception ex) { _log.LogDebug(ex, "Уточнение заголовка чата {Session}", sessionId); }
    }

    // Обновление названия чата по ТЕКУЩЕЙ переписке — явное действие пользователя (AI-хаб).
    // В отличие от авто-заголовка (только первое сообщение, ставится один раз) — читает весь
    // транскрипт и ВСЕГДА перезаписывает имя. Раннер как у «Итога сессии»: локаль или claude-фолбэк.
    public async Task<Session?> RetitleAsync(string userId, string sessionId, CancellationToken ct)
    {
        var session = GetOwned(sessionId, userId);
        if (session is null) return null;
        if (_cheap is null) throw new InvalidOperationException("ИИ недоступен");

        var history = await GetHistoryAsync(sessionId);
        var transcript = SessionSummaryService.BuildTranscript(history, 8000);
        if (string.IsNullOrWhiteSpace(transcript))
            throw new InvalidOperationException("В чате ещё нет сообщений");

        var prompt =
            "Ниже — переписка чата. Придумай короткое название (3-6 слов, по-русски, без кавычек и точки в конце), " +
            "отражающее суть текущего разговора. " + Llm.TitleExtraction.JsonHint + "\n\n" + transcript;
        var raw = await _cheap.RunAsync(Llm.LocalActionCatalog.ChatRetitle, prompt,
            _config["Notes:AiModel"] ?? "haiku", userId, jsonFormat: Llm.TitleExtraction.Schema, ct: ct);
        var line = Llm.TitleExtraction.Extract(raw);
        if (line is null || line.Length > 80)
            throw new InvalidOperationException("Модель вернула пустое название");

        if (!_sessions.TryGetValue(sessionId, out var entry)) return null;
        entry.Info.Name = line;
        entry.Info.NameLocked = true; // явное действие пользователя — авто больше не трогает
        entry.Info.UpdatedAt = DateTime.UtcNow;
        SaveSessions();
        await BroadcastChatRenamedAsync(sessionId, entry.Info, line);
        return entry.Info;
    }

    // Уведомить клиентов об авто-переименовании чата (адресация как у BroadcastChatDeletedAsync)
    private async Task BroadcastChatRenamedAsync(string sessionId, Session info, string name)
    {
        var msg = new ChatRenamedMessage(name) with { SessionId = sessionId };
        var tasks = new List<Task> { _hub.Clients.Group(sessionId).SendAsync("message", msg) };
        if (info.ProjectId is string pid)
            tasks.Add(_hub.Clients.Group("project_" + pid).SendAsync("message", msg));
        else if (info.OwnerId is string oid)
            tasks.Add(_hub.Clients.Group("user_" + oid).SendAsync("message", msg));
        await Task.WhenAll(tasks);
    }

    // Редактирование названия и модели. Модель применяется со следующего хода
    // (процесс claude пересоздаётся в RunTurnAsync), Info — общая ссылка с адаптером.
    //
    // PATCH-семантика: null = «поле не передано, не трогать». Иначе частичные апдейты
    // (MCP chats_update только с name; PUT {pinned} из togglePin) затирали бы модель/имя,
    // а для начатой сессии стороннего провайдера ещё и падали с «нельзя сменить провайдера».
    public Session? Update(string sessionId, string? name, string? model, string? effort, List<string>? tags = null)
    {
        if (!_sessions.TryGetValue(sessionId, out var entry)) return null;

        if (model is not null)
        {
            var newModel = string.IsNullOrWhiteSpace(model) ? null : model.Trim();
            // Провайдера резолвим по ЭФФЕКТИВНЫМ моделям: пустая означает «по назначению места»,
            // а назначение может указывать на модель стороннего провайдера. По сырому null
            // возврат glm-чата к «По умолчанию» (при назначении на glm) выглядел бы как переезд
            // на claude и упирался бы в guard, а переход claude-чата на «По умолчанию» с чужой
            // моделью, наоборот, проскакивал бы мимо guard и ломал транскрипт.
            var usageKey = UsageKeyFor(entry.Info.TaskExecution, entry.Info.TaskId, entry.Info.PersonaId);
            var effectiveNew = _assignments.Resolve(usageKey, newModel, entry.Info.OwnerId);
            var effectiveCur = _assignments.Resolve(usageKey, entry.Info.Model, entry.Info.OwnerId);

            // Смена провайдера: контекст сессии живёт у провайдера (транскрипт эндпоинта),
            // «переехавшая» сессия молча потеряла бы его — для начатых сессий запрещаем.
            // В рамках одного провайдера/пула модель менять можно.
            var newProvKey = _llmProviders.ResolveByModel(effectiveNew)?.Key;
            var curProvKey = _llmProviders.ResolveByModel(effectiveCur)?.Key;
            if (newProvKey != curProvKey)
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

            // В Info.Model кладём именно то, что выбрали: null = «следовать настройке»
            // (резолвится на каждом ходу, смена настройки подхватывается сама)
            entry.Info.Model = newModel;
            // При смене модели на стороннего провайдера — обновляем Provider
            if (effectiveNew is not null && _llmProviders.ResolveByModel(effectiveNew) is { } newProv)
                entry.Info.Provider = newProv.Key;
            // Родной Claude (ResolveByModel == null, в т.ч. пул подписок): модель меняем на
            // лету у живого хода — применится к его последующим round-trip'ам. У сторонних
            // провайдеров модель зашита в env процесса, там смена только со следующего хода.
            else if (effectiveNew is not null)
                entry.Process?.TrySetModelLive(effectiveNew);
        }

        if (name is not null)
        {
            var trimmed = string.IsNullOrWhiteSpace(name) ? null : name.Trim();
            entry.Info.Name = trimmed;
            // Явно заданное имя (вручную/MCP chats_update) — лочим от авто-переименования;
            // очистка имени (пустое) снимает лок, чтобы авто-заголовок мог сработать снова.
            entry.Info.NameLocked = trimmed is not null;
        }
        if (effort is not null)
            entry.Info.Effort = string.IsNullOrWhiteSpace(effort) ? null : effort.Trim();
        if (tags is not null)
            entry.Info.Tags = tags;

        entry.Info.UpdatedAt = DateTime.UtcNow;
        SaveSessions();
        return entry.Info;
    }

    public void RespondPermission(string sessionId, string requestId, string behavior)
    {
        if (!_sessions.TryGetValue(sessionId, out var entry)) return;
        entry.Process?.RespondPermission(requestId, behavior);
        entry.PendingInteraction = null;
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
            // «Стоп» замораживает очередь (не чистит): сообщения остаются ждать возобновления,
            // а последнее пользовательское возвращается в композер (composer_restore)
            _ = FreezePendingAsync(sessionId, entry);
            // M5: тот же сброс буфера маркеров, что при прерывании очередью (SendMessageAsync):
            // убитый ход даёт exited без result, буфер не потребляется — маркер мёртвого хода
            // (<escalate:*>, <team:work>) доклеился бы к следующему и применился задним числом
            // (фантомная эскалация, «волна-призрак»).
            if (entry.Info.TeamImplement is not null)
            {
                lock (entry.TeamTurnLock)
                {
                    entry.TeamTurnText.Clear();
                    entry.TeamTurnShownLength = 0;
                    entry.TeamTurnAsked = false;
                }
                entry.SkipNextTeamTurnEnd = false;
            }
            entry.Process?.Interrupt();
        }
    }

    // Включение/выключение цикла «до готово» (флаг work-loop). Включение сбрасывает
    // счётчик итераций; лимит — из конфига Loop:MaxIterations (дефолт 20).
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
            parsed = GuardCompatibleMode(parsed, teamForGuard.CoordinatorNoCode);
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
        entry.Info.WorkLoop = enabled
            ? new SessionWorkLoop
            {
                MaxIterations = int.TryParse(_config["Loop:MaxIterations"], out var m) ? m : 20,
            }
            : null;
        lock (entry.LoopTurnLock) entry.LoopTurnText.Clear();
        SaveSessions();
        await BroadcastWorkLoopAsync(sessionId, entry);

        // Явное сообщение в ленту (B5): иначе гаснет только бейдж, и непонятно, доделана
        // работа или брошена — вторая фраза важна, текущий ход после снятия цикла продолжается.
        if (manual && wasEnabled && !enabled)
            await AddWorkLoopStoppedNoticeAsync(sessionId, entry, "manual",
                "Цикл остановлен вами. Текущий ход продолжает работу.");

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

    private Task BroadcastWorkLoopAsync(string sessionId, SessionEntry entry)
    {
        var loop = entry.Info.WorkLoop;
        return BroadcastAsync(sessionId, new WorkLoopMessage(
            loop is not null, loop?.Iteration ?? 0, loop?.MaxIterations ?? 0, loop?.Phase));
    }

    // Режим «Командная реализация»: вкл/выкл режима чата-штаба. При включении задаётся
    // начальный состав (пустой список исполнителей = вся команда проекта) и стартовый
    // бюджет итерации из дефолтов/конфига. Выкл обнуляет поле — как work-loop.
    public async Task<Session?> SetTeamImplementAsync(string sessionId, bool enabled,
        bool autoWaves = true, string? coordinatorPersonaId = null, string? plannerPersonaId = null,
        IReadOnlyCollection<string>? executorPersonaIds = null, string? userId = null,
        bool coordinatorNoCode = true)
    {
        if (!_sessions.TryGetValue(sessionId, out var entry)) return null;
        if (userId is not null && ResolveOwnerId(entry.Info) != userId) return null;

        // Гард B4 (симметрично SetWorkLoopAsync): автопилот и «Командная реализация» не
        // сочетаются в одном чате — см. SessionModeConflictException.
        if (enabled && entry.Info.WorkLoop is not null)
            throw new SessionModeConflictException(
                "Командная реализация недоступна, пока в чате активен Автопилот — сначала выключите цикл «до готово».");

        // Гард на входе (B2 приёмки): чат без координатора или без состава исполнителей режимом
        // не станет. Раньше те же проверки жили только в CreateTeamPlanAsync — отказ приходил
        // ПОСЛЕ полного интервью, и вся постановка (десятки минут хода и токены) уходила впустую.
        if (enabled && TeamImplementSetupError(entry.Info, coordinatorPersonaId, executorPersonaIds)
            is { } setupError)
            throw new TeamImplementSetupException(setupError.Code, setupError.Message);

        // Minor (волна 3): выключение режима посреди незакрытой волны раньше не оставляло
        // следа — задачи волны сиротели молча (доисполняются, но никто не подводит итог).
        // Снимок ДО обнуления TeamImplement ниже.
        var interruptedWave = !enabled && entry.Info.TeamImplement is { WaveNumber: > 0 } wi
            && wi.WaveNumber > wi.ClosedWave ? wi.WaveNumber : (int?)null;
        var interruptedWaveAuthor = interruptedWave is not null
            ? entry.Info.TeamImplement!.CoordinatorPersonaId ?? entry.Info.PersonaId : null;

        // Выключение режима посреди интервью/планирования: сначала вернуть человеку его
        // режим прав, пока состояние с SavedMode ещё живо — иначе чат навсегда остался бы
        // в план-режиме, который ему навязал штаб (Э8).
        if (!enabled) RestoreUserMode(sessionId, entry);

        if (enabled && entry.Info.TeamImplement is { } active)
        {
            // M4: повторное включение поверх активного режима — правка настроек, а не рестарт.
            // Пересоздание объекта стирало SavedMode, бюджет, стадию, PlanCardId и счёт волн:
            // волна сиротела — задачи доисполнялись, а закрытия, сводки и проверки не было
            // никогда (план по пустому PlanCardId не находился). Меняем только настраиваемое.
            active.AutoWaves = autoWaves;
            active.CoordinatorPersonaId = coordinatorPersonaId;
            active.PlannerPersonaId = plannerPersonaId;
            active.ExecutorPersonaIds = executorPersonaIds?.ToList() ?? [];
            active.CoordinatorNoCode = coordinatorNoCode;
        }
        else
        {
            entry.Info.TeamImplement = enabled
                ? new SessionTeamImplement
                {
                    // Minor (волна 3): по спеке Э8 первая стадия итерации — интервью, а не
                    // планирование (дефолт модели). До этой правки бейдж окно между включением
                    // режима и первой вводной мог показать «планирование» — тексту спеки
                    // соответствует только по совпадению (первая вводная тут же переводит
                    // стадию через ResetTeamIterationOnUserInput).
                    Stage = TeamImplementStage.Interview,
                    AutoWaves = autoWaves,
                    CoordinatorPersonaId = coordinatorPersonaId,
                    PlannerPersonaId = plannerPersonaId,
                    ExecutorPersonaIds = executorPersonaIds?.ToList() ?? [],
                    Budget = NewTeamImplementBudget(),
                    CoordinatorNoCode = coordinatorNoCode,
                }
                : null;
        }
        // Гард «координатор не пишет код» (CoordinatorWriteGuard) проверяет команду Bash/
        // PowerShell в момент permission-запроса — а CLI спрашивает разрешение не в любом
        // --permission-mode: в acceptEdits/bypassPermissions запись через shell проходит мимо
        // сервера целиком (проверено вживую той же командой из находки Веры). Default/Auto
        // спрашивают всегда — переводим координатора туда, не трогая уже совместимые режимы.
        if (enabled && GuardCompatibleMode(entry.Info.Mode, coordinatorNoCode) is var guarded
            && guarded != entry.Info.Mode)
        {
            entry.Info.Mode = guarded;
            entry.Process?.TrySetPermissionModeLive(guarded);
        }
        entry.Info.UpdatedAt = DateTime.UtcNow;
        // Правило «координатор не пишет код» режет инструменты правки через --disallowedTools,
        // а он запекается при создании адаптера — помечаем устаревшим, иначе гард применился бы
        // только со следующего пересоздания процесса (уборка ленивая, как в SwitchSpeaker)
        if (entry.Process is not null) entry.AdapterStale = true;
        SaveSessions();
        await BroadcastTeamImplementAsync(sessionId, entry);

        // След «итерация оборвана» (Minor, волна 3): молчаливых пауз не бывает и у ручного
        // выключения — задачи незакрытой волны продолжат исполняться сами по себе, но человек
        // должен узнать об этом здесь и сейчас, а не догадываться по пропавшему бейджу режима.
        if (interruptedWave is { } wave && interruptedWaveAuthor is { } author)
        {
            var text = $"Режим «Командная реализация» выключен посреди волны {wave} — " +
                "задачи волны продолжат исполняться сами по себе, но закрытия волны, сводки и " +
                "итога итерации больше не будет. Проверьте их вручную.";
            var ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            await AppendStoredAsync(sessionId, new StoredTextMessage(text, personaId: author, timestamp: ts),
                new GuestTextMessage(text, author, ts));
        }
        return entry.Info;
    }

    // Причина, по которой режим включать нельзя — до единого хода интервью (B2 приёмки).
    // Порядок проверок совпадает с CreateTeamPlanAsync: сначала координатор, затем состав.
    // null — включать можно. Состав проверяем по БУДУЩЕМУ состоянию (пробный объект), чтобы
    // не дублировать логику подбора — она живёт в TeamPlanningService.
    internal (string Code, string Message)? TeamImplementSetupError(Session session,
        string? coordinatorPersonaId, IReadOnlyCollection<string>? executorPersonaIds)
    {
        // Координатор = собеседник чата, если явно не выбран другой (см. ResolveCoordinator)
        var coordinatorId = coordinatorPersonaId ?? session.PersonaId;
        if (string.IsNullOrWhiteSpace(coordinatorId))
            return (TeamImplementSetupException.NoCoordinator,
                "Выберите координатора — чат без персоны штабом быть не может. "
                + "Назначьте собеседника чата или укажите координатора при включении режима.");

        var ownerId = ResolveOwnerId(session);
        if (_teamPlanning is null || ownerId is null) return null;

        var probe = new Session
        {
            Id = session.Id,
            ProjectId = session.ProjectId,
            OwnerId = session.OwnerId,
            PersonaId = session.PersonaId,
            TeamImplement = new SessionTeamImplement
            {
                CoordinatorPersonaId = coordinatorPersonaId,
                ExecutorPersonaIds = executorPersonaIds?.ToList() ?? [],
            },
        };

        if (_teamPlanning.ResolveCoordinator(probe, ownerId) is null)
            return (TeamImplementSetupException.NoCoordinator,
                "Координатор не найден — выберите персону-собеседника чата, которая будет штабом.");

        if (_teamPlanning.ResolveCandidates(probe, ownerId).Count == 0)
            return (TeamImplementSetupException.NoExecutors, session.ProjectId is null
                ? "Выберите исполнителей — вне проекта команды нет, и подбирать не из кого"
                : "В команде проекта нет персон — выберите исполнителей явно");

        return null;
    }

    // Переключение авто-волн на ходу (из бейджа режима): не включает/выключает режим,
    // только флаг внутри. Режим не активен → поля не трогает, возвращает сессию как есть.
    public async Task<Session?> SetTeamImplementAutoAsync(string sessionId, bool autoWaves, string? userId = null)
    {
        if (!_sessions.TryGetValue(sessionId, out var entry)) return null;
        if (userId is not null && ResolveOwnerId(entry.Info) != userId) return null;
        if (entry.Info.TeamImplement is not { } ti) return entry.Info;

        ti.AutoWaves = autoWaves;
        entry.Info.UpdatedAt = DateTime.UtcNow;
        SaveSessions();
        await BroadcastTeamImplementAsync(sessionId, entry);
        return entry.Info;
    }

    // Режим прав, совместимый с гардом «координатор не пишет код»: в acceptEdits,
    // bypassPermissions И dontAsk (Minor, волна 3 — открытый вопрос предыдущего аудита:
    // имя режима у CLI означает ровно «не спрашивать разрешение», тот же класс, что
    // acceptEdits/bypass) CLI разрешение не спрашивает, и запись файла через shell (heredoc,
    // tee, sed -i) проходит мимо CoordinatorWriteGuard. Такие режимы поднимаем до Auto —
    // остальные оставляем как есть (в т.ч. Plan: он спрашивает всегда).
    private static ClaudeMode GuardCompatibleMode(ClaudeMode mode, bool coordinatorNoCode) =>
        coordinatorNoCode && mode is ClaudeMode.AcceptEdits or ClaudeMode.Bypass or ClaudeMode.DontAsk
            ? ClaudeMode.Auto : mode;

    // Вход в план-режим стадий интервью и планирования (Э8): запоминаем режим прав человека
    // и переводим чат в Plan — на этих стадиях правки запрещает сама permission-механика CLI,
    // а не только список инструментов. Живому ходу режим меняем на лету (control-протокол
    // set_permission_mode), как это делает SetMode.
    // Провайдер без поддержки плана — деградируем молча: чат остаётся в прежнем режиме
    // (гард «координатор не пишет код» продолжает работать), стадия при этом штатная.
    private void EnterPlanPhaseMode(string sessionId, SessionEntry entry)
    {
        if (entry.Info.TeamImplement is null) return;
        if (entry.Info.Mode == ClaudeMode.Plan) return;
        if (!_llmProviders.CapabilitiesFor(entry.Info.Model).SupportsPlanMode) return;
        // Сохранённый режим НЕ перезаписываем: цикл «интервью → волна → снова интервью»
        // обязан вернуть исходный выбор человека, а не Plan, поставленный прошлым заходом.
        WithTeamState(sessionId, t => { t.SavedMode ??= entry.Info.Mode; return true; });
        entry.Info.Mode = ClaudeMode.Plan;
        entry.Process?.TrySetPermissionModeLive(ClaudeMode.Plan);
    }

    // Возврат режима человека после согласования плана (Confirming → Wave) либо при
    // выключении режима. Выбор пользователя не затирается: после планирования чат работает
    // в том режиме, в котором был — с поправкой на гард «координатор не пишет код».
    private void RestoreUserMode(string sessionId, SessionEntry entry)
    {
        if (entry.Info.TeamImplement is not { SavedMode: { } saved } team) return;
        var restored = GuardCompatibleMode(saved, team.CoordinatorNoCode);
        WithTeamState(sessionId, t => { t.SavedMode = null; return true; });
        if (entry.Info.Mode == restored) return;
        entry.Info.Mode = restored;
        entry.Process?.TrySetPermissionModeLive(restored);
    }

    // Бюджет итерации из дефолтов плана с optional override из конфига TeamImplement:Max*
    private TeamImplementBudget NewTeamImplementBudget() => new()
    {
        MaxTasks = int.TryParse(_config["TeamImplement:MaxTasks"], out var t) ? t : 12,
        MaxWaves = int.TryParse(_config["TeamImplement:MaxWaves"], out var w) ? w : 4,
        MaxRuns = int.TryParse(_config["TeamImplement:MaxRuns"], out var r) ? r : 20,
        MaxRetries = int.TryParse(_config["TeamImplement:MaxRetries"], out var rt) ? rt : 3,
        MaxWakeups = int.TryParse(_config["TeamImplement:MaxWakeups"], out var wu) ? wu : 10,
    };

    private Task BroadcastTeamImplementAsync(string sessionId, SessionEntry entry)
    {
        var ti = entry.Info.TeamImplement;
        return BroadcastAsync(sessionId, new TeamImplementMessage(
            ti is not null,
            ti?.Stage.ToWireToken(),
            ti?.WaveNumber ?? 0,
            ti?.AutoWaves ?? true,
            ti?.CoordinatorPersonaId,
            ti?.PlannerPersonaId,
            ti?.ExecutorPersonaIds,
            ti?.Budget,
            ti?.PlanCardId,
            ti?.PlannedWaves ?? 0,
            ti?.CoordinatorNoCode ?? true,
            ti?.Stopped ?? false,
            ti?.SavedMode is not null,
            ti?.PlanVersion ?? 0));
    }

    // --- Э2: планирование по компетенциям и карточка плана ---

    // Построить план по вводной и опубликовать карточкой в ленту штаба.
    // Возвращает план либо null с причиной отказа в reason (нет координатора, пустой состав,
    // планировщик не ответил) — вызывающая сторона показывает её человеку.
    // fromHuman (M7) — вводная пришла от человека: только тогда добавочный план может
    // авто-подтвердиться. Прямые вызовы (кнопки человека) не передают параметр — true.
    public async Task<(TeamImplementPlan? Plan, string? Reason)> CreateTeamPlanAsync(
        string sessionId, string request, string? userId = null, CancellationToken ct = default,
        bool fromHuman = true)
    {
        if (!_sessions.TryGetValue(sessionId, out var entry)) return (null, "Чат не найден");
        var ownerId = ResolveOwnerId(entry.Info);
        if (ownerId is null || (userId is not null && ownerId != userId)) return (null, "Чат не найден");
        if (entry.Info.TeamImplement is null) return (null, "Режим «Командная реализация» не включён");
        if (_teamPlanning is null) return (null, "Планирование недоступно");

        // Координатор = собеседник чата. Без персоны режим планировать не может —
        // фронт показывает пикер координатора при включении режима.
        if (_teamPlanning.ResolveCoordinator(entry.Info, ownerId) is null)
            return (null, "Выберите координатора — чат без персоны штабом быть не может");

        if (_teamPlanning.ResolveCandidates(entry.Info, ownerId).Count == 0)
            return (null, entry.Info.ProjectId is null
                ? "Выберите исполнителей — вне проекта команды нет, и подбирать не из кого"
                : "В команде проекта нет персон — выберите исполнителей явно");

        var projectHint = entry.Info.ProjectId is { } pid ? _projects.GetById(pid)?.Name : null;
        // Перепланирование после интервью (Э8): планировщик получает предыдущую версию плана —
        // из неё он и выводит блок «Что изменилось». Не нашли карточку (чат чистили) — строим
        // с нуля: план без «что изменилось» лучше, чем отсутствие плана.
        var previous = entry.Info.TeamImplement is { Replanning: true, PlanCardId: { } prevId }
            ? await GetTeamPlanAsync(sessionId, prevId)
            : null;
        var (plan, timedOut) = await _teamPlanning.CreatePlanAsync(entry.Info, ownerId, request, projectHint, ct, previous);
        if (plan is null)
            return (null, timedOut
                ? TeamPlanningService.PlannerTimeoutReason
                : "Планировщик не смог построить план — уточните задачу");

        // План построен — сохранённая вводная отказа отработана
        WithTeamState(sessionId, t => { t.LastPlanRequest = null; return true; });
        await PublishTeamPlanAsync(sessionId, entry, plan, fromHuman);
        return (plan, null);
    }

    // Публикация карточки плана: история (переживает рестарт) + WS + стадия «ждёт подтверждения».
    // Добавочный план (Э5) при включённых авто-волнах подтверждения не ждёт: первоначальный
    // план итерации человек утверждает всегда, а для добавочного точкой контроля была сама
    // его вводная — карточка публикуется уже решённой, работа стартует сразу.
    // fromHuman (M7): «вводная человека» — буквально. Агентская вводная (chats_send в штаб),
    // классифицированная координатором как работа, авто-подтверждения НЕ получает: план
    // ждёт клика человека, как первоначальный — иначе единственное согласование обходится.
    private async Task PublishTeamPlanAsync(string sessionId, SessionEntry entry, TeamImplementPlan plan,
        bool fromHuman)
    {
        // Версия плана (Э8): перепланирование после интервью даёт vN+1, обычная публикация —
        // v1 новой итерации. Автор карточки — планировщик НА МОМЕНТ публикации: карточка
        // рисуется как его речь и переживает смену координатора.
        var replanning = entry.Info.TeamImplement is { Replanning: true };
        plan.Version = replanning ? (entry.Info.TeamImplement?.PlanVersion ?? 0) + 1 : 1;
        plan.PlannerPersonaId ??= entry.Info.TeamImplement?.CoordinatorPersonaId ?? entry.Info.PersonaId;

        // Полный план файлом (решение владельца 2026-08-02): сервер рендерит markdown из
        // структуры плана и кладёт рядом с проектом — координатору писать файлы запрещено
        // (CoordinatorWriteGuard). Версия — отдельный файл: plan.Version уже проставлен выше,
        // поэтому перепланирование ложится рядом с предыдущим, не поверх него. Подпапка на
        // IterationNumber той же логикой разводит разные вводные одного чата (прод 2026-08-03).
        // Глобальный чат без проекта — писать некуда (null), карточка покажет только «Замысел»;
        // ошибка записи не должна ронять публикацию карточки — TryWrite её не бросает.
        if (ResolveTeamPlanRoot(entry.Info) is { } planRoot)
        {
            var ownerIdForLabels = ResolveOwnerId(entry.Info);
            plan.PlanFilePath = TeamPlanFileRenderer.TryWrite(planRoot, entry.Info.Name, sessionId,
                entry.Info.TeamImplement?.IterationNumber ?? 0, plan,
                personaId => personaId is not null && _personas.Get(personaId, ownerIdForLabels ?? "") is { } p
                    ? PersonaManager.PersonaLabel(p) : personaId ?? "не назначен", _log);
        }

        // Добавочный = в режиме уже был план (первый ставит PlanCardId). Отменённый план
        // обнуляет PlanCardId, поэтому после «Отменить» следующий снова требует подтверждения.
        // Перепланирование (Э8) добавочным НЕ считается: новую версию плана человек утверждает
        // всегда — авто-волны покрывают волны по неизменному плану, но не смену самого плана.
        // И M7: авто-подтверждение — только за вводной человека, агентская идёт через карточку.
        var additional = fromHuman && !replanning
            && entry.Info.TeamImplement is { PlanCardId: not null, AutoWaves: true, Stopped: false };
        if (additional) plan.Approved = true;

        // Ветка аккумулятора — под тем же локом, что и ленивое оживление в EnsureProcessCoreAsync
        // (см. PublishFalCostAsync): иначе check-then-act на entry.Accumulator гоняется с ним.
        await _falPersistLock.WaitAsync();
        try
        {
            if (entry.Accumulator is not null)
            {
                entry.Accumulator.OnTeamPlan(plan);
                // Добавочный план кликом не гасится — гасим сразу, иначе карточка осталась бы
                // висеть открытой над уже идущей волной
                if (additional) entry.Accumulator.OnTeamPlanUpdated(plan.Id, plan, approved: true);
                try { await entry.Accumulator.SaveSnapshotAsync(_history); }
                catch (Exception ex) { _log.LogWarning(ex, "Сохранение карточки плана ({SessionId}) не удалось", sessionId); }
            }
            else if (entry.Info.ClaudeSessionId is string key)
            {
                // Чат неактивен — пишем карточку прямо в историю на диске
                try
                {
                    var stored = await _history.LoadAsync(key);
                    stored.Add(new StoredTeamPlanMessage
                    {
                        PlanId = plan.Id,
                        Plan = plan,
                        Resolved = additional,
                        Approved = additional ? true : null,
                        PersonaId = plan.PlannerPersonaId,
                    });
                    await _history.SaveAsync(key, stored);
                }
                catch (Exception ex) { _log.LogWarning(ex, "Прямая запись карточки плана ({SessionId}) не удалась", sessionId); }
            }
        }
        finally { _falPersistLock.Release(); }

        if (entry.Info.TeamImplement is not null)
        {
            WithTeamState(sessionId, t =>
            {
                t.PlanCardId = plan.Id;
                t.PlanVersion = plan.Version;
                // Перепланирование закончилось публикацией: дальше по этому плану идёт обычный
                // цикл, а признак снимаем — иначе следующая версия считалась бы от него же.
                t.Replanning = false;
                // Новый план — новый счёт волн итерации: без обнуления ClosedWave волна 1
                // добавочного плана считалась бы уже закрытой и никогда не закрылась бы снова.
                if (additional)
                {
                    t.Stage = TeamImplementStage.Wave;
                    t.WaveNumber = 0;
                    t.ClosedWave = 0;
                    t.PlannedWaves += plan.WaveCount;
                    // Добавочная волна при авто клика не ждёт: точкой контроля была сама
                    // вводная человека — значит эта версия плана и есть подтверждённая.
                    t.ApprovedPlanVersion = plan.Version;
                }
                else
                    t.Stage = TeamImplementStage.Confirming;
                return true;
            });
            // B1: добавочный план при авто-волнах согласования не ждёт — работа уже пошла,
            // а значит и режим прав человеку возвращается ЗДЕСЬ. Иначе SavedMode, поставленный
            // входом в интервью по этой же вводной, снять было бы негде (RestoreUserMode звался
            // только по клику «Запустить» и при выключении режима), и селектор оставался бы
            // залоченным «Штаб планирует…» до конца жизни чата.
            if (additional) RestoreUserMode(sessionId, entry);
            entry.Info.UpdatedAt = DateTime.UtcNow;
            SaveSessions();
            await BroadcastTeamImplementAsync(sessionId, entry);
        }
        await BroadcastAsync(sessionId, new TeamPlanMessage(plan.Id, plan, additional,
            additional ? true : null));

        if (!additional) return;

        // Информационная карточка состава (Э5): работа уже пошла, поэтому у карточки одна
        // кнопка — «Остановить», и стадию режима она не двигает.
        var card = new TeamEscalation
        {
            Kind = TeamEscalationKind.WaveAdded,
            Title = TeamImplementPrompts.EscalationTitle(TeamEscalationKind.WaveAdded,
                string.IsNullOrWhiteSpace(plan.Summary) ? plan.Request : plan.Summary),
            Details = TeamImplementPrompts.WaveAddedDetails(plan),
            Wave = entry.Info.TeamImplement?.WaveNumber ?? 0,
            Actions = TeamEscalationActions.For(TeamEscalationKind.WaveAdded),
        };
        if (TeamEscalationRaiser is { } raise) await raise(entry.Info, card);
        else await PublishTeamEscalationAsync(sessionId, card);

        // Раздача — тем же путём, что «Запустить» и авто-волна: план у TeamWaveService
        if (TeamWaveStarter is { } starter)
        {
            try { await starter(entry.Info, plan); }
            catch (Exception ex)
            {
                _log.LogError(ex, "Раздача добавочной волны по плану {PlanId} (чат {SessionId}) не удалась",
                    plan.Id, sessionId);
            }
        }
    }

    // Ответ человека по карточке плана (SessionHub.RespondTeamPlan).
    // Run — согласование получено, стадия уходит в «волна» (раздача — Э3);
    // Reassign — сменить исполнителя под-задачи, карточка остаётся открытой;
    // Cancel — план отклонён, режим возвращается к планированию.
    public async Task<TeamImplementPlan?> RespondTeamPlanAsync(string sessionId, string planId,
        TeamPlanDecision decision, string? subtaskId = null, string? executorPersonaId = null,
        string? userId = null)
    {
        if (!_sessions.TryGetValue(sessionId, out var entry)) return null;
        var ownerId = ResolveOwnerId(entry.Info);
        if (ownerId is null || (userId is not null && ownerId != userId)) return null;

        // Карточка живёт в аккумуляторе идущего хода, а после рестарта его ещё нет — тогда
        // читаем её с диска, как это давно делают карточки остановок. Без fallback кнопка
        // «Запустить» после перезапуска сервера молча не работала.
        var plan = entry.Accumulator?.FindTeamPlan(planId)
            ?? (entry.Accumulator is null ? await LoadPendingStoredPlanAsync(entry, sessionId, planId) : null);
        if (plan is null) return null;

        // M8: клик по УСТАРЕВШЕЙ карточке — в ленте висит v1, а опубликован уже v2 (либо
        // текущая карточка вообще другая). Пропускать такое решение нельзя: стадия ушла бы
        // в Wave при WaveNumber=0 («волна-призрак» — сторож не тикает), ApprovedPlanVersion
        // откатился бы на старую версию, а RestoreUserMode снял бы план-режим посреди
        // перепланирования. Волна всё равно не стартовала (гард версий в TeamWaveService),
        // то есть отказ был молчаливым, а состояние — враньём.
        if (entry.Info.TeamImplement is { } current && IsStalePlanCard(current, planId, plan))
        {
            await ResolveStalePlanCardAsync(sessionId, entry, current, planId, plan);
            return null;
        }

        if (decision == TeamPlanDecision.Reassign)
        {
            if (subtaskId is null || executorPersonaId is null) return null;
            var subtask = plan.Subtasks.FirstOrDefault(s => s.Id == subtaskId);
            if (subtask is null) return null;
            // Новый исполнитель — только своя персона: чужая утекла бы в задачу Э3
            var persona = _personas.Get(executorPersonaId, ownerId);
            if (persona is null) return null;
            subtask.ExecutorPersonaId = persona.Id;
            subtask.ExecutorRationale = $"Выбран вручную: {PersonaManager.PersonaLabel(persona)}";
        }
        else
            plan.Approved = decision == TeamPlanDecision.Run;

        var resolved = decision != TeamPlanDecision.Reassign;
        if (entry.Accumulator is { } acc)
        {
            acc.OnTeamPlanUpdated(planId, plan, resolved ? plan.Approved : null);
            FireAndForget(acc.SaveSnapshotAsync(_history),
                $"сохранение истории после решения по плану команды ({sessionId})");
        }
        // Аккумулятора нет — решение ложится прямо в историю на диске. Фильтр по Resolved
        // делает путь идемпотентным: двойной клик по карточке (обычное дело сразу после
        // рестарта) второй раз не пройдёт и волну дважды не раздаст.
        else if (!await MutateStoredAsync<StoredTeamPlanMessage>(entry, sessionId,
            m => m.PlanId == planId && !m.Resolved,
            m => { m.Plan = plan; m.Resolved = resolved; if (resolved) m.Approved = plan.Approved; }))
            return null;

        if (resolved && entry.Info.TeamImplement is not null)
        {
            WithTeamState(sessionId, t =>
            {
                t.Stage = decision == TeamPlanDecision.Run
                    ? TeamImplementStage.Wave
                    : TeamImplementStage.Planning;
                // Плановое число волн итерации — из самого плана, а не из потолка бюджета:
                // при плане в 2 волны бейдж обязан показать «волна 1 из 2»
                if (decision == TeamPlanDecision.Run)
                {
                    t.PlannedWaves = plan.WaveCount;
                    // Утверждённый план — новый счёт волн: иначе волна 1 нового плана попадала бы
                    // под защиту «эта волна уже закрыта» от предыдущего (Э5, повторные итерации)
                    t.WaveNumber = 0;
                    t.ClosedWave = 0;
                    // Э8: работа разрешена именно этой версии плана — по ней и только по ней
                    // стартуют волны (гард в TeamWaveService).
                    t.ApprovedPlanVersion = plan.Version;
                }
                if (decision == TeamPlanDecision.Cancel) { t.PlanCardId = null; t.PlannedWaves = 0; }
                return true;
            });
            // Э8: «Запустить» закрывает стадии интервью и планирования — человеку возвращается
            // его режим прав (селектор снова разблокирован). «Отменить» возвращает штаб в
            // планирование, поэтому план-режим там остаётся.
            if (decision == TeamPlanDecision.Run) RestoreUserMode(sessionId, entry);
            entry.Info.UpdatedAt = DateTime.UtcNow;
            SaveSessions();
            await BroadcastTeamImplementAsync(sessionId, entry);
        }

        await BroadcastAsync(sessionId, new TeamPlanMessage(planId, plan, resolved,
            resolved ? plan.Approved : null));

        // Раздача под-задач и пакетный запуск волны (Э3) — в TeamWaveService: он знает про
        // задачи и исполнителей, которых SessionManager по построению не знает (цикл DI
        // разорван хуком, как OnSessionMessage у TaskExecutionService).
        if (decision == TeamPlanDecision.Run && TeamWaveStarter is { } starter)
        {
            try { await starter(entry.Info, plan); }
            catch (Exception ex)
            {
                _log.LogError(ex, "Раздача волны по плану {PlanId} (чат {SessionId}) не удалась", planId, sessionId);
            }
        }
        return plan;
    }

    // Карточка плана уже не актуальна (Э8, M8): либо текущая карточка режима другая, либо
    // её версия старше опубликованной. Нули — состояние до Э8 (версий не было): гард выключен,
    // прежнее поведение цело. PlanCardId=null — план ещё не публиковали, сравнивать не с чем.
    private static bool IsStalePlanCard(SessionTeamImplement team, string planId, TeamImplementPlan plan) =>
        (team.PlanCardId is { } currentId && currentId != planId)
        || (team.PlanVersion > 0 && plan.Version > 0 && plan.Version < team.PlanVersion);

    // Гасим устаревшую карточку и объясняем человеку, почему решение по ней не сработало.
    // Стадию, версии и режим прав НЕ трогаем: практика живёт по актуальному плану, а этот
    // клик — по карточке из прошлого. Молчать нельзя (правило «молчаливых пауз не бывает»):
    // человек нажал кнопку и обязан узнать, что она больше ни к чему не ведёт.
    private async Task ResolveStalePlanCardAsync(string sessionId, SessionEntry entry,
        SessionTeamImplement team, string planId, TeamImplementPlan plan)
    {
        plan.Approved = false;
        if (entry.Accumulator is { } acc)
        {
            acc.OnTeamPlanUpdated(planId, plan, approved: false);
            FireAndForget(acc.SaveSnapshotAsync(_history),
                $"сохранение истории после гашения устаревшей карточки плана ({sessionId})");
        }
        else
            await MutateStoredAsync<StoredTeamPlanMessage>(entry, sessionId,
                m => m.PlanId == planId && !m.Resolved,
                m => { m.Plan = plan; m.Resolved = true; m.Approved = false; });

        await BroadcastAsync(sessionId, new TeamPlanMessage(planId, plan, true, false));

        var text = TeamImplementPrompts.StalePlanCardNotice(plan.Version, team.PlanVersion);
        // Пояснение идёт репликой планировщика — карточка плана рисуется его речью, и ответ
        // про её устаревание логично слышать от него же. Персоны нет (режим включён у чата
        // без собеседника — возможно у состояний до гарда B2): канала для реплики нет,
        // ограничиваемся гашением карточки и логом.
        var personaId = team.PlannerPersonaId ?? team.CoordinatorPersonaId ?? entry.Info.PersonaId;
        _log.LogInformation("Решение по устаревшей карточке плана {PlanId} (v{Version} при актуальной v{Current}) " +
            "в чате {SessionId} отклонено", planId, plan.Version, team.PlanVersion, sessionId);
        if (personaId is null) return;
        var ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        await AppendStoredAsync(sessionId, new StoredTextMessage(text, personaId: personaId, timestamp: ts),
            new GuestTextMessage(text, personaId, ts));
    }

    // Хук раздачи волны (Э3): назначается TeamWaveService при старте — так разрывается
    // цикл зависимостей (TaskExecutionService → SessionManager). null — раздача недоступна
    // (юнит-тесты без полного DI, инспекционный режим): режим тогда лишь меняет стадию.
    public Func<Session, TeamImplementPlan, Task>? TeamWaveStarter { get; set; }

    // Сохранить карточку плана в историю чата после правки бэкендом (Э3 проставляет
    // TeamImplementSubtask.TaskId). Карточка уже Resolved, поэтому обновляем её напрямую:
    // FindTeamPlan ищет только неразрешённые.
    public async Task SaveTeamPlanCardAsync(string sessionId, TeamImplementPlan plan)
    {
        if (!_sessions.TryGetValue(sessionId, out var entry)) return;
        if (entry.Accumulator is { } acc)
        {
            acc.OnTeamPlanUpdated(plan.Id, plan, approved: null);
            try { await acc.SaveSnapshotAsync(_history); }
            catch (Exception ex) { _log.LogWarning(ex, "Сохранение карточки плана ({SessionId}) не удалось", sessionId); }
            return;
        }
        // Чат неактивен (после рестарта аккумулятор ещё не оживлён) — пишем прямо в историю.
        // Молча выйти нельзя: раздача волны проставляет под-задачам TaskId, и без записи
        // следующее чтение плана с диска увидело бы их нерозданными и создало дубли задач.
        await MutateStoredAsync<StoredTeamPlanMessage>(entry, sessionId,
            m => m.PlanId == plan.Id, m => m.Plan = plan);
    }

    // Неразрешённая карточка плана из истории на диске: путь для чата без аккумулятора
    // (сервер перезапустился, ход ещё не начинался). Фильтр по Resolved — та же защита от
    // повторного клика, что даёт FindTeamPlan у активного чата.
    private async Task<TeamImplementPlan?> LoadPendingStoredPlanAsync(SessionEntry entry,
        string sessionId, string planId)
    {
        if (entry.Info.ClaudeSessionId is not string key) return null;
        try
        {
            var stored = await _history.LoadAsync(key);
            return stored.OfType<StoredTeamPlanMessage>()
                .LastOrDefault(m => m.PlanId == planId && !m.Resolved)?.Plan;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Чтение карточки плана {PlanId} с диска ({SessionId}) не удалось", planId, sessionId);
            return null;
        }
    }

    // Сохранить и разослать состояние режима после правки его полей снаружи (Э3 двигает
    // номер волны и счётчики бюджета в точке запуска — счёт ведёт бэкенд, не модель).
    public async Task SaveTeamImplementStateAsync(string sessionId)
    {
        if (!_sessions.TryGetValue(sessionId, out var entry)) return;
        entry.Info.UpdatedAt = DateTime.UtcNow;
        SaveSessions();
        await BroadcastTeamImplementAsync(sessionId, entry);
    }

    // --- Э4: автономный цикл, бюджет и эскалации ---

    // Сессии с включённым режимом — сторожу зависших волн (TeamWaveWatchdog) и сводкам.
    public IReadOnlyList<Session> GetTeamImplementSessions() =>
        [.. _sessions.Values.Select(e => e.Info).Where(s => s.TeamImplement is not null)];

    // План итерации по id карточки — источник правды автономного цикла: раздача остатка
    // волн и счётчик попыток под-задач живут в нём. В отличие от FindTeamPlan карточка
    // уже разрешена («Запустить» нажали), поэтому ищем без фильтра по Resolved.
    // Неактивный чат (аккумулятора нет) — читаем историю с диска.
    public async Task<TeamImplementPlan?> GetTeamPlanAsync(string sessionId, string planId)
    {
        if (!_sessions.TryGetValue(sessionId, out var entry)) return null;
        if (entry.Accumulator is { } acc) return acc.FindTeamPlanAny(planId);
        if (entry.Info.ClaudeSessionId is not string key) return null;
        try
        {
            var stored = await _history.LoadAsync(key);
            return stored.OfType<StoredTeamPlanMessage>().LastOrDefault(m => m.PlanId == planId)?.Plan;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Чтение карточки плана {PlanId} с диска ({SessionId}) не удалось", planId, sessionId);
            return null;
        }
    }

    // Правка карточки истории у НЕактивного чата (аккумулятора нет): загрузить, изменить,
    // сохранить. У активного чата тем же занимается аккумулятор — там снимок хода в памяти
    // и правка под его локом. Здесь read-modify-write файла, поэтому идём под _falPersistLock —
    // тем же, что сериализует остальные внеходовые записи истории. Без него двойной клик по
    // карточке (обычное дело сразу после рестарта, когда аккумулятор ещё не оживлён) проходил
    // бы дважды: удвоенная прибавка бюджета и двойная раздача волны.
    private async Task<bool> MutateStoredAsync<T>(SessionEntry entry, string sessionId,
        Func<T, bool> match, Action<T> mutate) where T : StoredMessage
    {
        if (entry.Info.ClaudeSessionId is not string key) return false;
        await _falPersistLock.WaitAsync();
        try
        {
            var stored = await _history.LoadAsync(key);
            var card = stored.OfType<T>().LastOrDefault(match);
            if (card is null) return false;
            mutate(card);
            await _history.SaveAsync(key, stored);
            return true;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Правка карточки в истории на диске ({SessionId}) не удалась", sessionId);
            return false;
        }
        finally { _falPersistLock.Release(); }
    }

    // Транзакция над состоянием режима: ЕДИНСТВЕННЫЙ способ править счётчики бюджета и
    // попытки под-задач. Точки записи разнесены по потокам (раздача волны из колбэка
    // завершения задачи, перевыдача из колбэка провала хода, квота из HTTP-фильтра), а
    // `int++` не атомарен — частичный лок означал бы потерянные инкременты и нечестный счёт
    // ровно там, ради чего Э4 и делался. Внутри — только синхронная работа с моделью.
    public T? WithTeamState<T>(string sessionId, Func<SessionTeamImplement, T> mutate)
    {
        if (!_sessions.TryGetValue(sessionId, out var entry)) return default;
        if (entry.Info.TeamImplement is not { } team) return default;
        lock (entry.TeamLock) return mutate(team);
    }

    // Вердикт квоты запуска исполнителя со штабного хода-реакции (Э4).
    // NotTeamMode — чат не в режиме: работает прежний запрет DenyOnDelegatedTurn.
    public enum TeamRunQuota { NotTeamMode, Allowed, Exhausted }

    // Гейт лавины запусков: на реакционном ходу координатора (ответ на доклад исполнителя)
    // запуск задачи разрешён, ПОКА цел бюджет итерации — запрет заменён квотой, а не снят.
    // Разрешение сразу же расходует единицу: счёт ведёт бэкенд в точке запуска, иначе
    // координатор в цикле «доклад → запуск → доклад» уходит в бесконечный платный круг.
    public (TeamRunQuota Verdict, string? Reason) TryConsumeTeamImplementRun(string sessionId, string ownerId)
    {
        // Запуск приходит не только из самого штаба, но и со второго уровня — из чата
        // исполнения под ним: расход ложится на бюджет ЕГО штаба, иначе исполнитель заводит
        // и запускает задачи мимо квоты (тот же обход, только этажом ниже).
        if (ResolveTeamStabId(sessionId, ownerId) is not { } stabId) return (TeamRunQuota.NotTeamMode, null);
        if (!_sessions.TryGetValue(stabId, out var entry)) return (TeamRunQuota.NotTeamMode, null);
        if (entry.Info.TeamImplement is not { } team) return (TeamRunQuota.NotTeamMode, null);

        string? reason;
        lock (entry.TeamLock)
        {
            // Стадия волны — вторая проверка после «Остановлено»: без неё квота честно
            // считала расход, но разрешала запуск ДО публикации и подтверждения плана —
            // единственное согласование (карточка плана) обходилось целиком (Э7-фикс).
            reason = team.Stopped
                ? "практика остановлена человеком — новые запуски не идут, пока он не продолжит"
                // M3: причина отказа обязана быть честной. Из «ждёт решения» ссылаться на
                // неподтверждённый план — враньё: план как раз подтверждён, а ждём мы ответа
                // человека по карточке остановки (кнопкой или обычным сообщением в чат).
                : team.Stage == TeamImplementStage.AwaitingDecision
                    ? "практика ждёт решения человека по карточке остановки — запуск исполнителей " +
                      "возобновится, когда он ответит (кнопкой карточки или сообщением в чат)"
                    : team.Stage != TeamImplementStage.Wave
                        ? "план ещё не подтверждён человеком — запуск исполнителей доступен только " +
                          "в стадии волны, единственное согласование — карточка плана"
                        : team.Budget.ExceededReason();
            if (reason is null)
            {
                team.Budget.RunsUsed++;
                // Задача, запущенная руками координатора, — такая же задача итерации, как
                // розданная волной: без этого счётчика потолок задач обходился ручной раздачей
                team.Budget.TasksUsed++;
            }
        }
        if (reason is not null) return (TeamRunQuota.Exhausted, reason);

        entry.Info.UpdatedAt = DateTime.UtcNow;
        SaveSessions();
        FireAndForget(BroadcastTeamImplementAsync(stabId, entry),
            $"рассылка состояния режима после расхода квоты ({stabId})");
        return (TeamRunQuota.Allowed, null);
    }

    // Компенсация квоты запуска (m3, второй проход Глеба): TryConsumeTeamImplementRun списывает
    // единицу авансом, в точке РЕШЕНИЯ (до попытки запуска) — иначе гейт нечестно разрешал бы
    // потратить лишнее между «проверить» и «списать». Но реальный запуск может не состояться
    // (задача не найдена, неверное состояние) — тогда платить команде не с чего, и вызывающая
    // сторона (фильтр DenyOnDelegatedTurn.OnActionExecuted) возвращает единицу сюда.
    public void RefundTeamImplementRun(string sessionId, string ownerId)
    {
        if (ResolveTeamStabId(sessionId, ownerId) is not { } stabId) return;
        if (!_sessions.TryGetValue(stabId, out var entry)) return;
        if (entry.Info.TeamImplement is not { } team) return;

        lock (entry.TeamLock)
        {
            if (team.Budget.RunsUsed > 0) team.Budget.RunsUsed--;
            if (team.Budget.TasksUsed > 0) team.Budget.TasksUsed--;
        }
        entry.Info.UpdatedAt = DateTime.UtcNow;
        SaveSessions();
        FireAndForget(BroadcastTeamImplementAsync(stabId, entry),
            $"рассылка состояния режима после возврата квоты ({stabId})");
    }

    // Чат-штаб для запроса: сам чат, если режим включён у него, иначе ближайший предок
    // в режиме (чат исполнения висит под штабом через вычисляемый ParentSessionId).
    // null — к режиму запрос отношения не имеет. Шагов немного: иерархия исполнения мелкая,
    // а счётчик — страховка от кольца в данных (как в IsDescendantOf).
    private string? ResolveTeamStabId(string sessionId, string ownerId)
    {
        var cur = GetOwned(sessionId, ownerId);
        for (var steps = 0; cur is not null && steps < 8; steps++)
        {
            if (cur.TeamImplement is not null) return cur.Id;
            if (cur.ParentSessionId is not { } parentId) return null;
            cur = GetOwned(parentId, ownerId);
        }
        return null;
    }

    // Квота пробуждения штаба агентом (Э4): любой платный ход чата-штаба, поднятый НЕ
    // человеком, а другим агентом (доклад-блокер, chats_send из чата исполнителя), считается
    // против отдельного потолка. Без этого бюджет обходится соседним инструментом: запуск
    // задач гейтит квота `TryConsumeTeamImplementRun`, а разбудить координатора можно было
    // бесплатно и бесконечно.
    // TeamMode=false — чат не штаб: ограничение не наше дело, пропускаем как раньше.
    public (bool TeamMode, bool Allowed, string? Reason) TryConsumeTeamWakeup(string sessionId)
    {
        if (!_sessions.TryGetValue(sessionId, out var entry)) return (false, true, null);
        if (entry.Info.TeamImplement is null) return (false, true, null);

        string? reason = null;
        var allowed = WithTeamState(sessionId, t =>
        {
            reason = t.Stopped
                ? "практика остановлена человеком — команда не будит координатора, пока он не продолжит"
                : t.Budget.ExceededReason();
            if (reason is not null) return false;
            t.Budget.WakeupsUsed++;
            return true;
        }) is true;

        if (allowed)
        {
            entry.Info.UpdatedAt = DateTime.UtcNow;
            SaveSessions();
            FireAndForget(BroadcastTeamImplementAsync(sessionId, entry),
                $"рассылка состояния режима после расхода пробуждения ({sessionId})");
        }
        return (true, allowed, reason);
    }

    // Компенсация квоты пробуждения (m3, второй проход Глеба): TryConsumeTeamWakeup списывает
    // единицу авансом, ДО того как сообщение реально дойдёт — ReportBlockerAsync может после
    // этого упереться в TooDeep, а chats_send — в дубль/переполнение очереди/занятость
    // (SessionMessagesController). Платить за несостоявшееся пробуждение команде не с чего —
    // возвращаем единицу.
    public void RefundTeamWakeup(string sessionId)
    {
        if (!_sessions.TryGetValue(sessionId, out var entry)) return;
        if (entry.Info.TeamImplement is null) return;

        WithTeamState(sessionId, t =>
        {
            if (t.Budget.WakeupsUsed > 0) t.Budget.WakeupsUsed--;
            return true;
        });
        entry.Info.UpdatedAt = DateTime.UtcNow;
        SaveSessions();
        FireAndForget(BroadcastTeamImplementAsync(sessionId, entry),
            $"рассылка состояния режима после возврата пробуждения ({sessionId})");
    }

    // Публикация карточки остановки: запись в ленту (переживает рестарт) + WS + стадия
    // «ждёт решения». Молчаливых остановок в режиме быть не должно, поэтому карточку
    // публикуем всегда, даже если человека сейчас нет в чате — уведомление и push шлёт
    // вызывающая сторона (TeamWaveService), она же знает про NotificationService.
    public async Task PublishTeamEscalationAsync(string sessionId, TeamEscalation escalation)
    {
        if (!_sessions.TryGetValue(sessionId, out var entry))
        {
            // Чат удалён вместе с режимом — показывать карточку некуда, но след нужен:
            // «остановка без следа» и есть тот самый молчаливый провал, которого не должно быть
            _log.LogWarning("Карточка остановки «{Title}» не опубликована: чата {SessionId} больше нет",
                escalation.Title, sessionId);
            return;
        }

        // Автор карточки (Э8) — координатор НА МОМЕНТ публикации: карточка идёт от его лица,
        // а смена координатора позже историю не переписывает. Уже проставленного автора не
        // трогаем: карточку мог составить другой участник штаба (например планировщик).
        escalation.PersonaId ??= entry.Info.TeamImplement?.CoordinatorPersonaId ?? entry.Info.PersonaId;

        await AppendStoredAsync(sessionId,
            new StoredTeamEscalationMessage { EscalationId = escalation.Id, Escalation = escalation },
            new TeamEscalationMessage(escalation.Id, escalation.Kind.ToWireToken(), escalation.Title,
                escalation.Details, escalation.Actions, escalation.TaskId, escalation.Wave,
                false, null, escalation.PersonaId));

        if (entry.Info.TeamImplement is null) return;
        // Информационная карточка (добавочная волна) практику не останавливает: стадию и
        // отсечку таймаута не трогаем — работа по ней идёт прямо сейчас
        if (escalation.Kind.IsInformational()) return;
        // Тупик в волне (Э8) ведёт не в «ждёт решения», а в интервью: стадию ставит
        // EnterInterviewAsync — вместе с план-режимом и признаком перепланирования.
        if (escalation.Kind == TeamEscalationKind.NeedsClarification) return;
        WithTeamState(sessionId, t =>
        {
            // Запоминаем, откуда практика пришла в ожидание: ответ человека до первой волны
            // вернёт её в эту стадию, а не в Wave. Повторная карточка поверх ожидания исходную
            // стадию не затирает — иначе возврат шёл бы в «ждёт решения» самого себя.
            if (t.Stage != TeamImplementStage.AwaitingDecision)
                t.StageBeforeDecision = t.Stage;
            t.Stage = TeamImplementStage.AwaitingDecision;
            // Волна больше не считается идущей: сторож зависших волн не должен второй раз
            // эскалировать то, что уже ждёт человека
            t.WaveStartedAt = null;
            t.WaveActivityAt = null;
            return true;
        });
        entry.Info.UpdatedAt = DateTime.UtcNow;
        SaveSessions();
        await BroadcastTeamImplementAsync(sessionId, entry);
    }

    // Решение человека по карточке остановки (SessionHub.RespondTeamEscalation).
    // Кнопка — это ярлык: карточка гаснет, а координатору уходит ход с текстом решения,
    // как если бы человек написал его сам. Часть действий дополнительно двигает бэкенд:
    // addBudget расширяет потолки, runNext раздаёт следующую волну, resume снимает «Стоп»,
    // retryPlan повторяет планирование по сохранённой вводной (без хода координатору).
    public async Task<bool> RespondTeamEscalationAsync(string sessionId, string escalationId,
        string? actionId, string? comment = null, string? userId = null)
    {
        if (!_sessions.TryGetValue(sessionId, out var entry)) return false;
        var ownerId = ResolveOwnerId(entry.Info);
        if (ownerId is null || (userId is not null && ownerId != userId)) return false;

        var escalation = entry.Accumulator?.FindTeamEscalation(escalationId);
        var resolved = entry.Accumulator?.OnTeamEscalationResolved(escalationId, actionId) ?? false;
        if (entry.Accumulator is not null)
        {
            if (!resolved) return false;
            FireAndForget(entry.Accumulator.SaveSnapshotAsync(_history),
                $"сохранение истории после решения по карточке остановки ({sessionId})");
        }
        else
        {
            // Чат неактивен — карточка лежит только на диске
            StoredTeamEscalationMessage? card = null;
            var ok = await MutateStoredAsync<StoredTeamEscalationMessage>(entry, sessionId,
                m => m.EscalationId == escalationId && !m.Escalation.Resolved,
                m => { m.Escalation.Resolved = true; m.Escalation.ChosenActionId = actionId; card = m; });
            if (!ok) return false;
            escalation = card?.Escalation;
        }

        var label = escalation?.Actions.FirstOrDefault(a => a.Id == actionId)?.Label ?? actionId;
        var kind = escalation?.Kind;

        if (entry.Info.TeamImplement is not null)
        {
            // Всё состояние решения — одной транзакцией: потолки, «Стоп», стадия и отсечки
            // сторожа правятся из разных потоков (квота хода, раздача волны, колбэки задач).
            WithTeamState(sessionId, team =>
            {
                switch (actionId)
                {
                    // Добавить бюджет может ТОЛЬКО человек — этот путь идёт из хаба, у агента
                    // такого инструмента нет. Иначе потолок обходился бы действием координатора.
                    case "addBudget":
                        var fresh = NewTeamImplementBudget();
                        team.Budget.MaxTasks += fresh.MaxTasks;
                        team.Budget.MaxWaves += fresh.MaxWaves;
                        team.Budget.MaxRuns += fresh.MaxRuns;
                        team.Budget.MaxRetries += fresh.MaxRetries;
                        team.Budget.MaxWakeups += fresh.MaxWakeups;
                        break;
                    case "resume":
                        team.Stopped = false;
                        break;
                    // «Остановить» с информационной карточки добавочной волны (Э5) — то же, что
                    // кнопка режима: запущенные исполнители дорабатывают, новые волны не идут
                    case "stop":
                        team.Stopped = true;
                        team.WaveStartedAt = null;
                        team.WaveActivityAt = null;
                        break;
                }
                // Практика возвращается в работу: волны идут дальше, стадию вернём в «волна»
                // (для «завершить» координатор сам подведёт итог — стадию двигать не станем;
                // для «остановить» стадия остаётся прежней — работа не возобновляется).
                // «Чинить дальше» (m4, второй проход Глеба) — координатор чинит и перепроверяет
                // САМ, раздачи волны здесь нет (starter ниже зовётся только для
                // runNext/addBudget/resume): уводить стадию в Wave означало бы, что упавший
                // следующий ход не даст checkFailed (HandleTeamTurnEndAsync требует
                // Stage == Checking), а сторож волн в Checking не смотрит — молчаливый тупик.
                team.Stage = actionId switch
                {
                    "finish" or "finishWithIssues" => TeamImplementStage.Checking,
                    "stop" => team.Stage,
                    "keepFixing" => TeamImplementStage.Checking,
                    // Повтор планирования по сохранённой вводной: интервью уже пройдено,
                    // сразу в планирование — даже когда волна уже была (сбой перепланирования)
                    "retryPlan" => TeamImplementStage.Planning,
                    // editRest (Minor, волна 3): «Изменить остаток плана» — не «продолжай как
                    // есть» (Wave), а перепланирование. EnterInterviewAsync ниже переставит
                    // стадию и корректно обнулит отсечки сторожа сам — здесь стадию не трогаем,
                    // чтобы не мелькала «волна» без отсечек (сторож её не увидел бы: волна уже
                    // закрыта, ClosedWave == WaveNumber, ветка обновления отсечек ниже не сработает).
                    "editRest" => team.Stage,
                    // До первой волны «вернуть в работу» некуда: волны ещё не стартовали,
                    // и Wave здесь — «волна-призрак» (WaveNumber=0, PlanCardId=null, сторож
                    // не тикает, статус врёт про доклады — прод 2026-07-31). Возвращаем
                    // стадию, из которой пришла карточка (интервью/планирование). Если волна
                    // реально стартует по этому решению (runNext/addBudget/resume с планом),
                    // стадию Wave выставит сама раздача (TeamWaveService.StartWaveCore).
                    _ => team.WaveNumber == 0
                        ? team.StageBeforeDecision ?? team.Stage
                        : TeamImplementStage.Wave,
                };
                // Решение принято, карточка гаснет — сохранённая стадия отработана
                team.StageBeforeDecision = null;
                // Вернулись в волну — заводим страховку таймаута заново: без отсечки сторож
                // молчал бы, и повторное зависание той же волны снова осталось бы незамеченным
                if (team.Stage == TeamImplementStage.Wave && team.WaveNumber > 0
                    && team.ClosedWave < team.WaveNumber)
                {
                    team.WaveStartedAt = DateTime.UtcNow;
                    team.WaveActivityAt = DateTime.UtcNow;
                }
                return true;
            });
            entry.Info.UpdatedAt = DateTime.UtcNow;
            SaveSessions();
            await BroadcastTeamImplementAsync(sessionId, entry);
        }

        await BroadcastAsync(sessionId, new TeamEscalationMessage(escalationId,
            (kind ?? TeamEscalationKind.Blocker).ToWireToken(),
            escalation?.Title ?? "", escalation?.Details ?? "",
            escalation?.Actions ?? [], escalation?.TaskId, escalation?.Wave ?? 0, true, actionId,
            escalation?.PersonaId));

        // Раздача волны по решению человека — тем же путём, что автоволна: план лежит в
        // карточке, раздаёт TeamWaveService (хук разрывает цикл DI). Кнопок здесь три:
        // «Запустить», а ещё «Добавить бюджет» и «Продолжить» — после них практика обязана
        // поехать сама. Без раздачи волна не стартовала, WaveStartedAt оставался пустым и
        // сторож молчал: человек нажал кнопку, а работа встала навсегда без единого сигнала.
        // Раздавать нечего (волна уже идёт) — StartWave вернёт пустой список и не навредит.
        if (actionId is "runNext" or "addBudget" or "resume"
            && entry.Info.TeamImplement?.PlanCardId is { } planId
            && TeamWaveStarter is { } starter)
        {
            var plan = await GetTeamPlanAsync(sessionId, planId);
            if (plan is not null)
            {
                try { await starter(entry.Info, plan); }
                catch (Exception ex)
                {
                    _log.LogError(ex, "Раздача волны по решению человека (чат {SessionId}) не удалась", sessionId);
                }
            }
        }

        // skip (TaskFailed) / drop (Blocker) — Minor, волна 3: под-задача помечается Done
        // (хук TeamSubtaskDropHandler), тем же путём закрывая волну, что и обычный доклад —
        // раньше кнопки ничего не делали, и волна не могла закрыться до ручного tasks_complete.
        if (actionId is "skip" or "drop" && escalation?.TaskId is { } droppedTaskId
            && TeamSubtaskDropHandler is { } dropHandler)
        {
            try
            {
                await dropHandler(droppedTaskId,
                    $"Снято решением человека по карточке остановки ({label}).");
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Снятие под-задачи {TaskId} по решению человека (чат {SessionId}) не удалось",
                    droppedTaskId, sessionId);
            }
        }

        // editRest (WaveGate) — Minor, волна 3: «Изменить остаток плана» это перепланирование,
        // а не «продолжай как есть» — заводим его тем же путём, что тупик в волне (clarify).
        if (actionId == "editRest")
        {
            await EnterInterviewAsync(sessionId, "человек попросил изменить остаток плана",
                withTurn: true);
            return true;
        }

        // retryPlan (сбой планирования): повтор идёт НАПРЯМУЮ по сохранённой вводной —
        // без хода координатору (интервью уже пройдено, текст маркера сохранён дословно).
        // Не получится снова — StartTeamWorkAsync опубликует новую карточку с той же кнопкой.
        if (actionId == "retryPlan")
        {
            var retryRequest = entry.Info.TeamImplement?.LastPlanRequest;
            if (!string.IsNullOrWhiteSpace(retryRequest))
                await StartTeamWorkAsync(sessionId, retryRequest);
            else
                _log.LogWarning("Повтор планирования в чате {SessionId}: сохранённая вводная пуста", sessionId);
            return true;
        }

        // Координатор узнаёт решение обычным ходом — как если бы человек написал его текстом.
        // В ленте — плашка механики, а не пузырь «Автоматически» с сырым текстом директивы.
        await SendOrEnqueueAsync(sessionId,
            TeamImplementPrompts.EscalationResolvedTurn(escalation, label, comment),
            senderPersonaId: null, silent: true, suppressTasksExecute: true,
            staffNote: "Ответ на карточку передан координатору");
        return true;
    }

    // «Остановить» (кнопка человека): текущие исполнители дорабатывают, новые волны не
    // стартуют. Карточку остановки публикует вызывающая сторона — здесь только состояние.
    public async Task<Session?> StopTeamImplementAsync(string sessionId, string? userId = null)
    {
        if (!_sessions.TryGetValue(sessionId, out var entry)) return null;
        if (userId is not null && ResolveOwnerId(entry.Info) != userId) return null;
        if (entry.Info.TeamImplement is null) return entry.Info;

        WithTeamState(sessionId, t =>
        {
            t.Stopped = true;
            t.WaveStartedAt = null;
            t.WaveActivityAt = null;
            return true;
        });
        entry.Info.UpdatedAt = DateTime.UtcNow;
        SaveSessions();
        await BroadcastTeamImplementAsync(sessionId, entry);
        return entry.Info;
    }

    // Хук эскалации (Э4): вешает TeamWaveService — он публикует карточку и шлёт уведомление
    // с push. Как TeamWaveStarter, разрывает цикл зависимостей (уведомления и задачи
    // SessionManager по построению не знает). null — эскалация деградирует до карточки.
    public Func<Session, TeamEscalation, Task>? TeamEscalationRaiser { get; set; }

    // Хук уведомления о вопросе интервью (Э8): вешает TeamWaveService — он шлёт уведомление
    // «ждёт ответов» и push, когда человека нет в чате. Тот же приём разрыва зависимостей,
    // что у TeamEscalationRaiser: NotificationService SessionManager по построению не знает.
    public Func<Session, Task>? TeamQuestionNotifier { get; set; }

    // Хук «снять под-задачу» (Minor, волна 3): кнопки skip (TaskFailed)/drop (Blocker) карточки
    // эскалации раньше не двигали бэкенд вовсе — под-задача оставалась незакрытой, и волна не
    // могла закрыться до ручного tasks_complete. Вешает TeamWaveService — он один знает
    // TaskManager (SessionManager по построению не знает, как и TeamWaveStarter/Raiser).
    // Помечает задачу Done с пояснением — тот же путь, что закрывает волну обычным докладом
    // исполнителя (TaskManager.TaskCompleted → TeamWaveService.OnTaskDone).
    public Func<string, string, Task>? TeamSubtaskDropHandler { get; set; }

    // Маркер эскалации в ответе координатора: `<escalate:deviation>суть</escalate>`.
    // Инструмента для этого не заводим — состав tools/list не должен зависеть от режима хода
    // (перезапуск CLI со всеми MCP), а маркер в тексте у нас уже работает в цикле «до готово».
    // Как и там, ищем вне код-блоков: модель часто цитирует протокол, прежде чем им пользоваться.
    internal static (TeamEscalationKind Kind, string Text)? ParseEscalationMarker(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var stripped = System.Text.RegularExpressions.Regex.Replace(text, "```[\\s\\S]*?(```|$)", "");
        stripped = System.Text.RegularExpressions.Regex.Replace(stripped, "`[^`\n]*`", "");
        // Закрывающий тег — с именем или без: модель по XML-привычке пишет </escalate:check>
        // вместо канонического </escalate> (в thinking цитирует формат верно, при генерации
        // закрывает по имени) — строгое сравнение роняло маркер в молчаливый тупик.
        var m = System.Text.RegularExpressions.Regex.Match(stripped,
            @"<escalate:(deviation|check|decision|clarify)>([\s\S]*?)</escalate(?::\w+)?>");
        if (!m.Success) return null;
        var kind = m.Groups[1].Value switch
        {
            "deviation" => TeamEscalationKind.PlanDeviation,
            "check" => TeamEscalationKind.CheckFailed,
            // Тупик в волне (Э8): не остановка «жду решения», а возврат в интервью
            "clarify" => TeamEscalationKind.NeedsClarification,
            _ => TeamEscalationKind.ProductDecision,
        };
        return (kind, m.Groups[2].Value.Trim());
    }

    // Маркер работы в ответе координатора (Э5): `<team:work>постановка</team>`. Им координатор
    // говорит, что вводная человека требует правки файлов — бэкенд разложит её планировщиком
    // и развернёт волну. Разговорный ответ маркера не несёт и не стоит ничего.
    // Разбор — как у эскалации: вне код-блоков, потому что протокол модель любит цитировать.
    internal static string? ParseWorkMarker(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var stripped = System.Text.RegularExpressions.Regex.Replace(text, "```[\\s\\S]*?(```|$)", "");
        stripped = System.Text.RegularExpressions.Regex.Replace(stripped, "`[^`\n]*`", "");
        // Закрытие — </team> или </team:work>: на длинной постановке модель закрывает тег
        // по имени, и строгий </team> не распознавал маркер — вводная падала в карточку
        // «Координатор не понял вводную» (прод 2026-07-31).
        var m = System.Text.RegularExpressions.Regex.Match(stripped, @"<team:work>([\s\S]*?)</team(?::work)?>");
        if (!m.Success) return null;
        var request = m.Groups[1].Value.Trim();
        return request.Length == 0 ? null : request;
    }

    // Маркер разговора (M6): `<team:talk/>` — координатор честно разобрал сообщение человека:
    // работы нет, файлы менять не нужно. Легальный выход из интервью без плана — по голому
    // тексту бэкенд не отличит такой ответ от молчаливого тупика (stall-гард). Разбор — как
    // у прочих маркеров: вне код-блоков, потому что протокол модель любит цитировать.
    internal static bool HasTalkMarker(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        var stripped = System.Text.RegularExpressions.Regex.Replace(text, "```[\\s\\S]*?(```|$)", "");
        stripped = System.Text.RegularExpressions.Regex.Replace(stripped, "`[^`\n]*`", "");
        return System.Text.RegularExpressions.Regex.IsMatch(stripped, @"<team:talk\s*/>");
    }

    // Волна 6 (живая приёмка волны 5): маркеры протокола — внутренняя договорённость между
    // координатором и бэкендом (их же разбирают Parse*/Has* выше), в реплике, которую видит
    // человек, им не место. Модель периодически закрывает тег по имени длинного маркера
    // (`</team:work>`, `</escalate:check>`) — парсер это уже терпит, а сырой текст хода
    // раньше уходил в ленту/историю как есть, и закрывающий тег протекал буквально.
    // Код-блоки не трогаем — симметрично тому, что их же исключают Parse*/Has* выше:
    // модель вправе процитировать протокол примером, это не активный вызов.
    private static readonly System.Text.RegularExpressions.Regex CodeSpanOrFenceRegex =
        new("```[\\s\\S]*?```|`[^`\n]*`", System.Text.RegularExpressions.RegexOptions.Compiled);
    private static readonly System.Text.RegularExpressions.Regex EscalateMarkerRegex =
        new(@"<escalate:(?:deviation|check|decision|clarify)>[\s\S]*?</escalate(?::\w+)?>",
            System.Text.RegularExpressions.RegexOptions.Compiled);
    private static readonly System.Text.RegularExpressions.Regex WorkMarkerRegex =
        new(@"<team:work>[\s\S]*?</team(?::work)?>", System.Text.RegularExpressions.RegexOptions.Compiled);
    private static readonly System.Text.RegularExpressions.Regex TalkMarkerRegex =
        new(@"<team:talk\s*/>", System.Text.RegularExpressions.RegexOptions.Compiled);
    // Осиротевший закрывающий тег без пары (прод 2026-08-02, находка Веры): в длинном
    // структурированном ответе модель иногда закрывает маркер повторно или цитирует закрытие
    // отдельно от открытия, которое уже вырезано парным регэкспом выше (например тем же именем
    // маркера двумя абзацами раньше). Такой закрывающий тег — всегда служебный синтаксис
    // нашего протокола (`</team>`/`</team:work>`, `</escalate>`/`</escalate:kind>`), человеку
    // он не нужен ни в какой форме — вырезаем и его.
    private static readonly System.Text.RegularExpressions.Regex OrphanCloserRegex =
        new(@"</escalate(?::\w+)?>|</team(?::work)?>", System.Text.RegularExpressions.RegexOptions.Compiled);

    internal static string StripTeamProtocolMarkers(string text)
    {
        if (string.IsNullOrEmpty(text) || !text.Contains('<')) return text;
        var sb = new System.Text.StringBuilder(text.Length);
        var pos = 0;
        foreach (System.Text.RegularExpressions.Match code in CodeSpanOrFenceRegex.Matches(text))
        {
            sb.Append(StripMarkersOutsideCode(text[pos..code.Index]));
            sb.Append(text, code.Index, code.Length);
            pos = code.Index + code.Length;
        }
        sb.Append(StripMarkersOutsideCode(text[pos..]));
        return sb.ToString();
    }

    private static string StripMarkersOutsideCode(string text)
    {
        if (text.Length == 0 || !text.Contains('<')) return text;
        text = EscalateMarkerRegex.Replace(text, "");
        text = WorkMarkerRegex.Replace(text, "");
        text = TalkMarkerRegex.Replace(text, "");
        text = OrphanCloserRegex.Replace(text, "");
        return text;
    }

    // Полные открывающие теги маркеров (без вариативных \s* — тем, которые их допускают,
    // соответствует отдельная проверка ниже). Хвост текста, совпадающий с СОБСТВЕННЫМ
    // префиксом одного из них, ещё может дорасти до настоящего маркера следующей дельтой —
    // до этого момента показывать его нельзя (иначе полтега мелькнёт в стриме раньше, чем
    // мы поймём, что это протокол).
    private static readonly string[] MarkerOpenTags =
    [
        "<escalate:deviation>", "<escalate:check>", "<escalate:decision>", "<escalate:clarify>",
        "<team:work>",
    ];

    internal static bool IsAmbiguousMarkerTail(string tail)
    {
        if (tail.Length == 0 || tail[0] != '<') return false;
        foreach (var open in MarkerOpenTags)
            if (open.Length > tail.Length && open.StartsWith(tail, StringComparison.Ordinal))
                return true;
        // `<team:talk/>` пробелы перед `/>` не фиксированы регэкспом разбора — сюда попадает
        // только незавершённый префикс (полный маркер уже вырезан StripTeamProtocolMarkers)
        return System.Text.RegularExpressions.Regex.IsMatch(tail, @"^<team:talk\s*/?$");
    }

    // Обрезает с хвоста текста потенциально незавершённый маркер (см. IsAmbiguousMarkerTail).
    // Используется только при живой трансляции хода — на финальном тексте хода обрезка не
    // нужна: дальше дельт не будет, и придержанный хвост можно просто показать как есть.
    internal static string TrimAmbiguousMarkerTail(string text)
    {
        var idx = text.LastIndexOf('<');
        if (idx < 0) return text;
        var tail = text[idx..];
        return IsAmbiguousMarkerTail(tail) ? text[..idx] : text;
    }

    // Полностью открытый маркер (открывающий тег уже целиком напечатан), у которого просто
    // ЕЩЁ НЕ пришло закрытие, — IsAmbiguousMarkerTail его пропускает (он больше не префикс
    // открывающего тега, он им равен), а StripTeamProtocolMarkers его не трогает (регэксп
    // требует закрывающую часть). Раз открывающий тег буквально присутствует в уже очищенном
    // от ЗАВЕРШЁННЫХ маркеров тексте — значит, этот конкретный маркер ещё не закрылся: прячем
    // с его начала и до конца буфера (тело маркера — постановка для планировщика, не для
    // человека, и в любом случае может дописываться следующими дельтами).
    private static readonly string[] MarkerOpenLiterals =
    [
        "<escalate:deviation>", "<escalate:check>", "<escalate:decision>", "<escalate:clarify>",
        "<team:work>", "<team:talk",
    ];

    internal static string TrimUnresolvedMarkerOpen(string strippedText)
    {
        var cut = strippedText.Length;
        foreach (var open in MarkerOpenLiterals)
        {
            var idx = strippedText.IndexOf(open, StringComparison.Ordinal);
            if (idx >= 0 && idx < cut) cut = idx;
        }
        return cut == strippedText.Length ? strippedText : strippedText[..cut];
    }

    // Конец хода штаба (Э4 + Э5): маркеры координатора и переход в ожидание вводной.
    // Приоритет — эскалация: она останавливает практику, и разворачивать волну поверх
    // остановки незачем. Стадию «ожидание» ставим только на успешном ходу: упавший ход
    // итог не подвёл, и «итерация завершена» было бы враньём.
    // asked — в этом ходу координатор задал вопрос ASK-карточкой: тогда интервью работает,
    // и гард молчаливого тупика молчит (иначе карточка «вопросов не будет» приходила бы
    // ровно поверх пришедших вопросов).
    internal async Task HandleTeamTurnEndAsync(string sessionId, string turnText, bool failed,
        bool asked = false)
    {
        if (!_sessions.TryGetValue(sessionId, out var entry)) return;
        if (entry.Info.TeamImplement is not { } team) return;

        if (ParseEscalationMarker(turnText) is { } marker)
        {
            // Тупик в волне (Э8) — не «жду решения», а возврат в интервью: волны на паузе,
            // карточка с push, следом ход с просьбой задать вопросы ASK-карточками.
            if (marker.Kind == TeamEscalationKind.NeedsClarification)
                await EnterInterviewAsync(sessionId, marker.Text, withTurn: true);
            else
                await RaiseCoordinatorEscalationAsync(sessionId, marker.Kind, marker.Text);
            return;
        }

        if (ParseWorkMarker(turnText) is { } request)
        {
            await StartTeamWorkAsync(sessionId, request);
            return;
        }

        // Разговорный ответ в интервью (M6): работы нет — закрываем интервью без плана
        // и без ложной эскалации, практика возвращается в прежнее состояние.
        if (HasTalkMarker(turnText))
        {
            await CloseTeamTalkAsync(sessionId);
            return;
        }

        // Молчаливый тупик (Э7-фикс, находка Веры Major №3; Э8 расширил на Interview —
        // ревью Глеба): координатор ни разу не довёл дело до волны (WaveNumber == 0) и
        // закончил ход в planning или interview без маркера работы/эскалации — вводная, с
        // которой начинается практика, повисла бы без следа: ход завершился, плана нет,
        // карточки нет, бейдж «интервью»/«планирование» никогда не сдвинется. После первой
        // волны (WaveNumber > 0) такой же ответ без маркера — легитимный разговор по
        // WorkClassificationProtocol («что сейчас в работе?» и т.п.), эскалацию не поднимаем.
        // M9: интервью, вызванное тупиком в волне, приходит с WaveNumber > 0 — на него гард
        // «только до первой волны» не распространялся, и клятва карточки «сейчас придут
        // вопросы» нарушалась молча: вопросов нет, маркера нет, сторож волн в Interview
        // не тикает. Теперь стадия интервью под гардом при любом номере волны.
        // Прод 2026-08-04: гард обязан молчать, пока живо планирование по этой вводной
        // (TeamPlanningInFlight). Планировщик работает ДОЛЬШЕ хода (потолок 300 с), и за
        // это время в чате спокойно заканчиваются другие ходы — их конец без маркера при
        // Planning && WaveNumber == 0 не тупик координатора: план уже строится и придёт
        // карточкой (а не построится — карточку даст сбой/таймаут планировщика). Без флага
        // тревога «Координатор не понял вводную» поднималась на живой работе и висела
        // красной рядом с пришедшим планом.
        var stalledStage = team.Stage == TeamImplementStage.Interview
            || (team.Stage == TeamImplementStage.Planning && team.WaveNumber == 0);
        if (stalledStage && !asked && !entry.TeamPlanningInFlight)
        {
            // Волна 6 (живая приёмка волны 5): ход мог не завершиться маркером по ДВУМ разным
            // причинам, и текст карточки должен их различать. «Координатор не понял вводную»/
            // «Уточнения так и не пришли» — координатор ОТВЕТИЛ, но без маркера: это честная
            // реакция на его текст (SilentPlanningStallDetails/ClarifyStallDetails цитируют
            // turnText). А `failed` — ход оборван технически (рестарт сервера, упавший процесс,
            // таймаут провайдера) ДО того, как координатор вообще успел ответить по существу:
            // turnText в этом случае пуст или обрублен, и цитировать в карточке нечего, а текст
            // «не понял вводную» отправляет человека переформулировать задачу, хотя проблема не
            // в ней. Формулировка карточки-инфраструктурного обрыва согласована с владельцем.
            var stalled = failed
                ? new TeamEscalation
                {
                    Kind = TeamEscalationKind.ProductDecision,
                    Title = "Ход прервался",
                    Details = TeamImplementPrompts.TurnInterruptedDetails(),
                    Wave = team.WaveNumber,
                    Actions = TeamEscalationActions.For(TeamEscalationKind.ProductDecision),
                }
                : BuildSilentStallEscalation(team, turnText);
            if (TeamEscalationRaiser is { } raise) await raise(entry.Info, stalled);
            else await PublishTeamEscalationAsync(sessionId, stalled);
            return;
        }

        // Ход проверки упал (процесс умер, лимит провайдера, ошибка): итог не подведён, но и
        // висеть в «проверке» вечно нельзя — сторож волн сюда не смотрит, а новая вводная
        // из этой стадии не разворачивается. Зовём человека карточкой «проверка не прошла».
        if (failed && team.Stage == TeamImplementStage.Checking)
        {
            await RaiseCoordinatorEscalationAsync(sessionId, TeamEscalationKind.CheckFailed,
                "Ход проверки завершился ошибкой — итог итерации не подведён. "
                + "Продолжить починку или закрыть итерацию с замечаниями?");
            return;
        }

        // Проверка завершилась без эскалации — итерация закрыта, режим ждёт следующую вводную
        // (сам режим при этом НЕ выключается: выключает его только человек из бейджа).
        if (!failed && team.Stage == TeamImplementStage.Checking)
        {
            WithTeamState(sessionId, t =>
            {
                t.Stage = TeamImplementStage.Idle;
                t.WaveStartedAt = null;
                t.WaveActivityAt = null;
                return true;
            });
            await SaveTeamImplementStateAsync(sessionId);
            _log.LogInformation("Итерация чата-штаба {SessionId} завершена — режим ждёт следующей вводной", sessionId);
        }
    }

    // Карточка молчаливого тупика (ход завершился штатно, но без маркера) — вынесено из
    // HandleTeamTurnEndAsync, чтобы её не спутать с веткой инфраструктурного обрыва (см. там).
    private static TeamEscalation BuildSilentStallEscalation(SessionTeamImplement team, string turnText)
    {
        var clarifyStall = team.Stage == TeamImplementStage.Interview && team.WaveNumber > 0;
        return new TeamEscalation
        {
            Kind = TeamEscalationKind.ProductDecision,
            Title = clarifyStall ? "Уточнения так и не пришли" : "Координатор не понял вводную",
            Details = clarifyStall
                ? TeamImplementPrompts.ClarifyStallDetails(turnText, team.WaveNumber)
                : TeamImplementPrompts.SilentPlanningStallDetails(turnText),
            Wave = team.WaveNumber,
            Actions = TeamEscalationActions.For(TeamEscalationKind.ProductDecision),
        };
    }

    // Новая вводная разложена планировщиком и уходит в волну (Э5). Гард по стадии: работу
    // разворачиваем только когда итерация не идёт — иначе маркер посреди волны запустил бы
    // вторую поверх первой. «Остановить» удерживает маркеры в стадиях идущей итерации, но
    // не новую вводную в ожидании: классифицированная как работа — она и есть решение
    // человека продолжить (спека «Бюджет»: «Остановить» относится к прошлой итерации).
    private async Task StartTeamWorkAsync(string sessionId, string request)
    {
        if (!_sessions.TryGetValue(sessionId, out var entry)) return;
        if (entry.Info.TeamImplement is not { } team) return;
        // Э8: интервью — легальная точка выхода в план (маркером его и закрывает координатор).
        if ((team.Stopped && team.Stage != TeamImplementStage.Idle)
            || team.Stage is not (TeamImplementStage.Interview
                or TeamImplementStage.Planning or TeamImplementStage.Idle))
        {
            _log.LogInformation("Маркер работы в чате-штабе {SessionId} пропущен: стадия {Stage}, остановка {Stopped}",
                sessionId, team.Stage, team.Stopped);
            return;
        }

        // Интервью закончено — идёт планирование. Стадию двигаем ДО вызова планировщика:
        // он работает секунды, и всё это время бейдж обязан показывать «планирование», а не
        // «интервью», в котором человек ждал бы новых вопросов. План-режим остаётся: обе
        // стадии живут в одном непрерывном план-режиме.
        if (team.Stage == TeamImplementStage.Interview)
        {
            WithTeamState(sessionId, t => { t.Stage = TeamImplementStage.Planning; return true; });
            entry.Info.UpdatedAt = DateTime.UtcNow;
            SaveSessions();
            await BroadcastTeamImplementAsync(sessionId, entry);
        }

        // M6: новая итерация в ожидании открывается ЗДЕСЬ — классификацией вводной как работы,
        // а не приёмом сообщения (спека «Бюджет»: сброс — по вводной, которую координатор
        // классифицировал как работу). Разговорный вопрос в Idle потолки не обнуляет,
        // план-режим не навязывает и ложной эскалации не даёт.
        if (team.Stage == TeamImplementStage.Idle)
        {
            WithTeamState(sessionId, t =>
            {
                t.Budget = NewTeamImplementBudget();
                t.WaveNumber = 0;
                t.ClosedWave = 0;
                t.PlannedWaves = 0;
                t.WaveStartedAt = null;
                t.WaveActivityAt = null;
                // «Остановить» относилось к прошлой итерации — новая вводная её снимает
                t.Stopped = false;
                t.Stage = TeamImplementStage.Planning;
                t.InterviewRounds = 0;
                t.Replanning = false;
                // Новая вводная после Idle (М6) — тоже НОВАЯ вводная в счёте IterationNumber,
                // отдельно от ResetTeamIterationOnUserInput (та ловит только самую первую):
                // без этого файл плана снова писался бы в тот же путь, что и у прошлой (прод
                // 2026-08-03, находка Веры).
                t.IterationNumber++;
                return true;
            });
            // План-режим — с классификации, а не с приёма сообщения: разговорный ход в
            // ожидании идёт в режиме человека, селектор не лочится.
            EnterPlanPhaseMode(sessionId, entry);
            entry.Info.UpdatedAt = DateTime.UtcNow;
            SaveSessions();
            await BroadcastTeamImplementAsync(sessionId, entry);
        }

        // Вводная сохраняется на состоянии ДО планировщика: при его отказе человек сможет
        // повторить планирование кнопкой карточки, не проходя интервью заново.
        WithTeamState(sessionId, t => { t.LastPlanRequest = request; return true; });

        // Флаг живого планирования: гард молчаливого тупика по концу хода (см.
        // HandleTeamTurnEndAsync) не поднимает тревогу, пока планировщик реально строит
        // план. Снимается в finally — успех даёт карточку плана, сбой и таймаут дают свою
        // карточку ниже, так что после снятия флага тишины не будет в любом исходе.
        entry.TeamPlanningInFlight = true;
        try
        {
            var (plan, reason) = await CreateTeamPlanAsync(sessionId, request,
                fromHuman: entry.TeamTurnFromHuman);
            if (plan is not null) return;

            // Молчаливых тупиков в режиме не бывает: человек написал вводную и ждёт волну —
            // значит про несостоявшийся план он должен узнать карточкой, а не по тишине.
            // Таймаут планировщика — отдельный случай: причина не в постановке человека,
            // и текст карточки называет её как есть (PlanTimeoutDetails).
            var timedOut = reason == TeamPlanningService.PlannerTimeoutReason;
            var failed = new TeamEscalation
            {
                Kind = TeamEscalationKind.ProductDecision,
                Title = timedOut
                    ? "План не построился: планировщик не уложился во время"
                    : "План по вашей вводной не построился",
                Details = timedOut
                    ? TeamImplementPrompts.PlanTimeoutDetails(request)
                    : TeamImplementPrompts.PlanFailedDetails(request, reason),
                Wave = team.WaveNumber,
                // Кнопка повторяет планирование по сохранённой вводной (retryPlan в
                // RespondTeamEscalationAsync) — без хода координатору и без повторного интервью.
                Actions = [new TeamEscalationAction("retryPlan", "Повторить планирование")],
            };
            if (TeamEscalationRaiser is { } raise) await raise(entry.Info, failed);
            else await PublishTeamEscalationAsync(sessionId, failed);
        }
        finally
        {
            entry.TeamPlanningInFlight = false;
        }
    }

    // Выход из интервью без работы (M6, маркер `<team:talk/>`): координатор честно разобрал
    // сообщение — это разговор, практику на пустом месте не разворачиваем. Свежая «итерация»
    // возвращается в ожидание первой вводной (Planning), прерванное clarify-интервью — обратно
    // в волну со свежими отсечками сторожа (как выход из «ждёт решения»). План-режим был
    // навязан на время интервью — возвращаем режим человека. Бюджет не трогаем: разговор
    // ничего не стоит (WorkClassificationProtocol).
    private async Task CloseTeamTalkAsync(string sessionId)
    {
        if (!_sessions.TryGetValue(sessionId, out var entry)) return;
        if (entry.Info.TeamImplement is not { } team) return;
        if (team.Stage != TeamImplementStage.Interview) return;

        WithTeamState(sessionId, t =>
        {
            if (t.WaveNumber > 0)
            {
                t.Stage = TeamImplementStage.Wave;
                // Волна продолжается — страховка таймаута заводится заново
                if (t.ClosedWave < t.WaveNumber)
                {
                    t.WaveStartedAt = DateTime.UtcNow;
                    t.WaveActivityAt = DateTime.UtcNow;
                }
                // Интервью закончилось без плана — следующий план снова обычный, а не «новая
                // версия с обязательным подтверждением» (признак ставил вход в clarify)
                t.Replanning = false;
            }
            else
            {
                t.Stage = TeamImplementStage.Planning;
                t.InterviewRounds = 0;
            }
            return true;
        });
        RestoreUserMode(sessionId, entry);
        entry.Info.UpdatedAt = DateTime.UtcNow;
        SaveSessions();
        await BroadcastTeamImplementAsync(sessionId, entry);
    }

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
        EnterPlanPhaseMode(sessionId, entry);
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
    // Карточку в ленте не гасим: она остаётся историей, а повторное решение по ней приведёт
    // практику в то же состояние (путь идемпотентен по стадии).
    private void ResumeTeamFromDecisionOnUserInput(string sessionId, SessionEntry entry)
    {
        if (entry.Info.TeamImplement is not { Stage: TeamImplementStage.AwaitingDecision }) return;

        WithTeamState(sessionId, t =>
        {
            t.Stage = t.WaveNumber == 0
                ? t.StageBeforeDecision ?? TeamImplementStage.Planning
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
        FireAndForget(BroadcastTeamImplementAsync(sessionId, entry),
            $"рассылка состояния режима после ответа человека на карточку ({sessionId})");
    }

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
        EnterPlanPhaseMode(sessionId, entry);
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
        if (TeamEscalationRaiser is { } raise) await raise(entry.Info, card);
        else await PublishTeamEscalationAsync(sessionId, card);

        if (withTurn)
            await SendOrEnqueueAsync(sessionId, TeamImplementPrompts.ClarifyInterviewTurn(reason, team),
                senderPersonaId: null, silent: true, suppressTasksExecute: true,
                staffNote: "Возврат в интервью — координатор задаст вопросы");
    }

    // Координатор задал вопрос ASK-карточкой (Э8). В интервью это очередной раунд (их не
    // больше двух на вводную — счёт ведёт бэкенд, модель своих раундов не помнит); в волне
    // или ожидании — сигнал «требования неясны», и практика возвращается в интервью.
    // Добавочная вводная тем и отличается от первой: интервью на ней случается только по
    // реальному вопросу планировщика, а не потому, что стадия обязательна.
    internal async Task OnStabAskQuestionAsync(string sessionId)
    {
        if (!_sessions.TryGetValue(sessionId, out var entry)) return;
        if (entry.Info.TeamImplement is not { } team) return;

        WithTeamState(sessionId, t => { t.InterviewRounds++; return true; });
        if (team.Stage == TeamImplementStage.Interview)
        {
            entry.Info.UpdatedAt = DateTime.UtcNow;
            SaveSessions();
            await BroadcastTeamImplementAsync(sessionId, entry);
        }
        else
            await EnterInterviewAsync(sessionId, "координатор задал вопрос по постановке", withTurn: false);

        // Вопрос ждёт человека: уведомление и push, если его нет в чате. Отдельно от карточки
        // возврата в интервью — вопросы второго раунда карточку не переиздают, а звать
        // человека всё равно надо (иначе интервью молча ждёт ответа).
        if (TeamQuestionNotifier is { } notify)
        {
            try { await notify(entry.Info); }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Уведомление о вопросе интервью ({SessionId}) не отправлено", sessionId);
            }
        }
    }

    // Эскалация, поднятая самим координатором маркером в ходе (расхождение с планом, красная
    // проверка, продуктовый вопрос). Заголовки — из таблицы «Эскалация и остановки».
    private async Task RaiseCoordinatorEscalationAsync(string sessionId, TeamEscalationKind kind, string details)
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
        if (TeamEscalationRaiser is { } raise) await raise(entry.Info, escalation);
        else await PublishTeamEscalationAsync(sessionId, escalation);
    }

    // Отдельное git worktree чата: вкл — создать дерево на новой ветке от HEAD проекта и
    // перевести туда рабочую папку сессии; выкл — вернуть чат в корень проекта и снять дерево.
    // Начатый чат переезжает С КОНТЕКСТОМ: транскрипт CLI копируется в папку нового cwd
    // (--resume ищет его по уплощённому cwd); не удалось скопировать — операция отменяется,
    // контекст дороже фичи. У container-пользователя и профиль, и cwd берутся в песочной
    // раскладке (ConfigRootFor/CwdForOwner) — переезд работает так же, как на хосте.
    // Процесс не трогаем: между ходами его нет, AdapterStale пересоберёт контекст со
    // следующего хода.
    public async Task<Session?> SetWorktreeAsync(string sessionId, bool enabled,
        string? branch = null, bool force = false, string? userId = null)
    {
        if (!_sessions.TryGetValue(sessionId, out var entry)) return null;
        var ownerId = ResolveOwnerId(entry.Info);
        if (userId is not null && ownerId != userId) return null;
        if (_git is null)
            throw new InvalidOperationException("Git-операции недоступны");
        if (entry.Info.ProjectId is not string projectId)
            throw new InvalidOperationException("Отдельное дерево доступно только в чате проекта");
        var project = _projects.GetById(projectId)
            ?? throw new InvalidOperationException("Проект не найден");

        // Идемпотентность: повторное включение/выключение — no-op
        if (enabled == (entry.Info.WorktreePath is not null)) return entry.Info;

        if (enabled)
        {
            if (!Git.GitService.IsGitRepo(project.RootPath))
                throw new Git.GitCommandException("В папке проекта нет git-репозитория");

            // Ветка: заданная вручную либо wt/<slug имени чата>; коллизии решаем суффиксом
            var slug = PersonaManager.Slugify(entry.Info.Name ?? "");
            if (slug.Length == 0) slug = sessionId[..Math.Min(8, sessionId.Length)];
            var branchName = string.IsNullOrWhiteSpace(branch) ? $"wt/{slug}" : branch.Trim();
            var taken = (await _git.BranchesAsync(ownerId, project.RootPath))
                .Select(b => b.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var unique = branchName;
            for (var n = 2; taken.Contains(unique); n++) unique = $"{branchName}-{n}";
            branchName = unique;

            // Папка: {home}/.worktrees/<проект>/<ветка> — вне дерева главной репы и
            // гарантированно внутри Sandbox:ProjectsRoot у container-пользователей
            var user = ownerId is null ? null : _users.GetById(ownerId);
            var home = (user is null ? null : _homes.Resolve(user))
                ?? throw new InvalidOperationException("Не удалось определить домашнюю папку владельца");
            var projSlug = PersonaManager.Slugify(project.Name);
            if (projSlug.Length == 0) projSlug = project.Id[..Math.Min(8, project.Id.Length)];
            var wtPath = Path.Combine(home, ".worktrees", projSlug, branchName.Replace('/', '-'));
            Directory.CreateDirectory(Path.GetDirectoryName(wtPath)!);

            // Рабочие папки ГЛАЗАМИ CLI считаем ДО создания дерева: у container-пользователя
            // ToRuntime отвергает путь вне монтирований, а исключение после WorktreeAddAsync
            // обошло бы rollback ниже и оставило дерево-сироту. ClaudeSessionId читаем ОДИН
            // РАЗ здесь же: WorktreeAddAsync ниже содержит await, и повторное чтение после
            // него могло бы увидеть другое значение (null → появился, если параллельно
            // стартовал первый ход чата), а srcCwd/dstCwd остались бы не посчитаны
            (string Csid, string Src, string Dst)? migration = entry.Info.ClaudeSessionId is string csid0
                ? (csid0, CwdForOwner(ownerId, project.RootPath), CwdForOwner(ownerId, wtPath))
                : null;

            await _git.WorktreeAddAsync(ownerId, project.RootPath, wtPath, branchName);

            if (migration is { } m
                && !Llm.TranscriptMigrator.TryRelocateCwd(
                    ConfigRootFor(ownerId, entry.Info.Provider), m.Src, m.Dst, m.Csid, out var err))
            {
                // Дерево без контекста бесполезно — откатываем и отдаём причину наружу
                try { await _git.WorktreeRemoveAsync(ownerId, project.RootPath, wtPath, force: true); }
                catch { /* уборка best-effort */ }
                throw new Git.GitCommandException($"Не удалось перенести контекст разговора: {err}");
            }

            entry.Info.WorktreePath = wtPath;
            entry.Info.WorktreeBranch = branchName;
        }
        else
        {
            var wtPath = entry.Info.WorktreePath!;
            // Как и при включении — перевод путей до снятия дерева (см. выше), ClaudeSessionId
            // читаем один раз здесь же (ниже есть await StatusAsync/WorktreeRemoveAsync)
            (string Csid, string Src, string Dst)? migration = entry.Info.ClaudeSessionId is string csid0
                ? (csid0, CwdForOwner(ownerId, wtPath), CwdForOwner(ownerId, project.RootPath))
                : null;

            // Гейт: незакоммиченные правки в дереве пропадут вместе с ним (ветка остаётся)
            if (!force)
            {
                var st = await _git.StatusAsync(ownerId, wtPath);
                if (st.Staged.Count > 0 || st.Unstaged.Count > 0 || st.Untracked.Count > 0)
                    throw new Git.GitCommandException(
                        "В отдельном дереве есть несохранённые изменения — зафиксируйте их или подтвердите принудительное удаление");
            }

            if (migration is { } m
                && !Llm.TranscriptMigrator.TryRelocateCwd(
                    ConfigRootFor(ownerId, entry.Info.Provider), m.Src, m.Dst, m.Csid, out var err))
                throw new Git.GitCommandException($"Не удалось перенести контекст разговора: {err}");

            await _git.WorktreeRemoveAsync(ownerId, project.RootPath, wtPath, force);
            entry.Info.WorktreePath = null;
            entry.Info.WorktreeBranch = null;
            ReleaseWorktreeGraph(sessionId, wtPath);
        }

        // Следующий ход пересоздаст адаптер с новым cwd (между ходами процесса нет)
        entry.AdapterStale = true;
        SaveSessions();
        return entry.Info;
    }

    /// <summary>
    /// Привязать чат к УЖЕ СУЩЕСТВУЮЩЕМУ дереву (чат-исполнитель задачи с worktree в её поле):
    /// проставляет Session.WorktreePath/WorktreeBranch без создания дерева и переноса
    /// транскрипта — этим он и отличается от SetWorktreeAsync. Вызывать до первого хода:
    /// cwd процесса подставит EffectiveRoot сам, а начатый чат переезжает только с контекстом
    /// (SetWorktreeAsync). Дерево должно лежать на диске и числиться в «git worktree list»
    /// репы проекта. false — привязка не состоялась: вызывающий стартует в корне проекта.
    /// </summary>
    public async Task<bool> AttachWorktreeAsync(string sessionId, string worktreePath, string? branch = null)
    {
        if (string.IsNullOrWhiteSpace(worktreePath)) return false;
        if (!_sessions.TryGetValue(sessionId, out var entry)) return false;
        // Дерево бывает только у чата проекта; начатый чат не трогаем — его контекст
        // привязан к прежнему cwd (--resume ищет транскрипт по уплощённому пути)
        if (entry.Info.ProjectId is not string projectId || entry.Info.ClaudeSessionId is not null) return false;
        if (_projects.GetById(projectId) is not { } project || _git is null) return false;

        var path = Path.GetFullPath(worktreePath.Trim());
        if (!Directory.Exists(path)) return false;

        // Числится ли дерево в главной репе: чужая (или уже снятая) папка увела бы ход мимо
        // проекта. Сравнение нормализованными путями — как у worktree-чатов в CodeGraphController.
        var wanted = WorkspaceKnowledgeStore.NormalizePath(path);
        GitWorktreeInfo? known;
        try
        {
            known = (await _git.WorktreeListAsync(ResolveOwnerId(entry.Info), project.RootPath))
                .FirstOrDefault(w => WorkspaceKnowledgeStore.NormalizePath(w.Path) == wanted);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Не удалось прочитать git worktree list проекта {Project}", project.Id);
            return false;
        }
        if (known is null) return false;

        entry.Info.WorktreePath = path;
        // Ветка задана вызывающим либо берётся из самого дерева (метка в git-баре чата)
        entry.Info.WorktreeBranch = string.IsNullOrWhiteSpace(branch) ? known.Branch : branch.Trim();
        entry.AdapterStale = true;
        SaveSessions();
        return true;
    }

    // Автопродолжение цикла «до готово»: вызывается по result хода, нёсшего протокол цикла.
    // Маркер найден → верификационный ход, затем стоп; нет → продолжение до лимита итераций.
    private async Task ContinueWorkLoopAsync(string sessionId)
    {
        if (!_sessions.TryGetValue(sessionId, out var entry)) return;
        if (entry.Info.WorkLoop is not { } loop) return;

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
            // сообщение-остановка тут не нужна, в отличие от лимита/ошибки/ручного стопа)
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

        loop.Iteration++;
        if (loop.Iteration >= loop.MaxIterations)
        {
            await AddWorkLoopStoppedNoticeAsync(sessionId, entry, "limit",
                $"Цикл остановлен: исчерпан лимит в {loop.MaxIterations} ходов. " +
                "Работа могла остаться незавершённой — проверьте результат.");
            await SetWorkLoopAsync(sessionId, false);
            return;
        }

        SaveSessions();
        await BroadcastWorkLoopAsync(sessionId, entry);
        if (entry.Info.WorkLoop is null) return; // Стоп успел снять цикл — ход-сироту не шлём
        await SendMessageAsync(sessionId,
            OmoPrompts.WorkLoopContinuation(loop.Promise, loop.Iteration, loop.MaxIterations), [], systemDirective: true);
    }

    // Маркер завершения ищем вне код-блоков и с точным регистром: модель часто цитирует
    // протокол в начале хода («когда закончу — выведу `<promise>…</promise>`») — бэктики
    // и ``` не считаются исполнением обещания
    internal static bool ContainsPromiseMarker(string text, string promise)
    {
        var stripped = System.Text.RegularExpressions.Regex.Replace(text, "```[\\s\\S]*?(```|$)", "");
        stripped = System.Text.RegularExpressions.Regex.Replace(stripped, "`[^`\n]*`", "");
        return stripped.Contains($"<promise>{promise}</promise>", StringComparison.Ordinal);
    }

    public void AnswerQuestion(string sessionId, string toolUseId, string answerText)
    {
        if (!_sessions.TryGetValue(sessionId, out var entry)) return;
        entry.Process?.AnswerQuestion(toolUseId, answerText);
        entry.PendingInteraction = null;
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
        entry.PendingInteraction = null;
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
                DeleteTranscript(entry.Info, csid);
            }
        }
        // Снимки промпта ключуются id ЧАТА, а не транскриптом, — гейт общего разговора выше
        // к ним не относится: у чата-двойника свои снимки, и его лента их не потеряет
        _promptSnapshots?.DeleteAll(sessionId);

        // Отдельное worktree чата сносим вместе с чатом (best-effort; ветка остаётся в репе)
        if (entry.Info.WorktreePath is string wt && entry.Info.ProjectId is string wpid && _git is not null)
        {
            try
            {
                if (_projects.GetById(wpid) is { } wproj)
                    await _git.WorktreeRemoveAsync(ResolveOwnerId(entry.Info), wproj.RootPath, wt, force: true);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[SessionManager] Worktree чата не удалён ({sessionId}): {ex.Message}");
            }
            ReleaseWorktreeGraph(sessionId, wt);
        }
        SaveSessions();
        try { OnSessionDeleted?.Invoke(entry.Info); } catch { /* наблюдатель не должен ронять удаление */ }
        await BroadcastChatDeletedAsync(sessionId, entry.Info);
    }

    // Транскрипт claude CLI удаленного чата ({профиль}/projects/{уплощенный cwd}/{csid}.jsonl).
    // Своя история (data/sessions/{csid}) уходит через ChatHistoryService, а этот файл раньше
    // не убирал никто — переписка удаленного чата лежала на диске до плановой уборки CLI
    // (~30 дней), хотя воспользоваться ей уже нельзя: --resume делать некому.
    //
    // Ищем во ВСЕХ профилях, а не только в текущем: переезды между профилями (TryMigrate при
    // смене провайдера) и между рабочими папками (TryRelocateCwd при worktree) намеренно
    // оставляют копии. Точность обеспечивает сам ключ — файл называется uuid'ом сессии, так
    // что чужие чаты, другие инстансы сервера и интерактивные сессии пользователя, живущие в
    // том же ~/.claude, задеть невозможно.
    //
    // Best-effort: удаление чата важнее уборки, поэтому любой сбой остается в логе.
    private void DeleteTranscript(Session info, string claudeSessionId)
    {
        try
        {
            var roots = new List<string>(_llmProviders.GetAllConfigRoots());
            // Профили песочницы владельца: {ProfilesHostDir}/{ownerId}/{key} (раскладку задает
            // DockerProcessRunner.RewriteProfileEnv, ее же зеркалит ConfigRootFor). Здесь берем
            // ВСЕ папки владельца, а не считаем ключ: чат мог мигрировать между профилями
            if (ResolveOwnerId(info) is string ownerId)
            {
                var ownerProfiles = Path.Combine(_sandbox.ProfilesHostDir, ownerId);
                if (Directory.Exists(ownerProfiles))
                    roots.AddRange(Directory.GetDirectories(ownerProfiles));
            }

            var removed = Llm.TranscriptMigrator.DeleteEverywhere(
                roots, TryResolveCwd(info), claudeSessionId);
            if (removed > 0)
                _log.LogInformation("Транскрипт чата {SessionId} удален ({Count} файлов)", info.Id, removed);
            else
                // Штатных причин две: транскрипт уже вычистил сам CLI (плановая уборка ~30 дней)
                // либо ходов в чате не было. Но сюда же попадает «файл нашелся, а удалить не
                // дали» — поэтому не утверждаем, что убирать было нечего, и отправляем за
                // подробностями в лог TranscriptMigrator
                _log.LogInformation(
                    "Транскрипт чата {SessionId} не убран: не найден либо не удалился (подробности выше)",
                    info.Id);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Не удалось убрать транскрипт чата {SessionId}", info.Id);
        }
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
        // Догоняем учёт старых glif-генераций, у которых ещё нет glif_cost
        BackfillGlifCosts(sessionId, list);
        return list;
    }

    public IReadOnlyList<WorkflowProgressMessage> GetWorkflowProgress(string sessionId)
    {
        if (!_sessions.TryGetValue(sessionId, out var entry)) return [];
        // Снимок под локом: словарь конкурентно мутируют таймер-потоки ватчеров
        lock (entry.WorkflowProgress) return entry.WorkflowProgress.Values.ToList();
    }

    // Ожидающая карточка взаимодействия (разрешение/вопрос/план) — replay при JoinSession
    public ServerMessage? GetPendingInteraction(string sessionId) =>
        _sessions.TryGetValue(sessionId, out var entry) ? entry.PendingInteraction : null;

    // Последний манифест recall (F3) сессии — replay при подключении клиента (JoinSession),
    // как и у workflow_progress: без этого «использовано сейчас» видно только тем, кто был
    // на связи в момент самого хода.
    public RecallManifestMessage? GetLastRecallManifest(string sessionId) =>
        _sessions.TryGetValue(sessionId, out var entry) ? entry.LastRecallManifest : null;

    public Session? GetSessionInfo(string sessionId)
    {
        _sessions.TryGetValue(sessionId, out var entry);
        return entry?.Info;
    }

    // --- Внутренняя логика ---

    // runId — прогон адаптера, чей read-loop прислал сообщение (0 у внутренних вызовов без
    // прогона): по нему отличаем exited ЭТОГО прогона от позднего exited доживающего
    private async Task OnMessageAsync(string sessionId, TurnAccumulator acc, ServerMessage msg, long runId = 0)
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
                    acc.SetSaveKey(m.ClaudeSessionId);
                    acc.OnSessionStarted(m.Model, m.Mode, m.TurnWorktree);
                    SaveSessions();
                    break;
                case TextDeltaMessage m:
                    acc.OnTextDelta(m.Text);
                    // Цикл «до готово»: копим текст хода для поиска маркера завершения
                    if (entry?.Info.WorkLoop is not null)
                        lock (entry.LoopTurnLock) entry.LoopTurnText.Append(m.Text);
                    // Режим «Командная реализация»: тем же способом ловим маркер эскалации
                    // координатора (расхождение с планом, красная проверка, вопрос человеку) —
                    // и заодно решаем, что из накопленного уже безопасно показать человеку
                    // (волна 6): маркеры — внутренний протокол, в живой ленте им не место.
                    // Целиком завершённый маркер вырезаем, незавершённый хвост придерживаем
                    // до следующей дельты — иначе полтега мелькнёт на экране раньше, чем мы
                    // поймём, что это протокол, а не текст координатора.
                    if (entry?.Info.TeamImplement is not null)
                    {
                        string? displayDelta;
                        lock (entry.TeamTurnLock)
                        {
                            entry.TeamTurnText.Append(m.Text);
                            var safe = TrimAmbiguousMarkerTail(
                                TrimUnresolvedMarkerOpen(StripTeamProtocolMarkers(entry.TeamTurnText.ToString())));
                            if (safe.Length > entry.TeamTurnShownLength)
                            {
                                displayDelta = safe[entry.TeamTurnShownLength..];
                                entry.TeamTurnShownLength = safe.Length;
                            }
                            else displayDelta = null;
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
                case ToolUseMessage m:      acc.OnToolUse(m.Id, m.Name, m.Input, m.ParentToolUseId); break;
                case ToolResultMessage m:
                    acc.OnToolResult(m.ToolUseId, m.Content, m.IsError);
                    await acc.SaveSnapshotAsync(_history); // промежуточное сохранение после каждого tool call
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
                    break;
                case FileChangedMessage m:  acc.OnFileChanged(m.Path, m.Added, m.Removed, m.External); break;
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
                        await OnStabAskQuestionAsync(sessionId);
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
                    await acc.OnResultAsync(m.Subtype, m.DurationMs, m.NumTurns, m.Usage, m.TotalCostUsd, m.ApiErrorStatus, m.PermissionDenials, _history, m.ContextTokens, m.UsageModel);
                    if (entry is not null) entry.LoopTurnFailed = m.Subtype == "error";
                    RecordTurnSpend(entry, m);
                    break;
                case RateLimitMessage m:
                    _usage.Record(m.LimitType, m.Utilization, m.Status, m.IsUsingOverage, m.ResetsAt, m.OverageStatus, m.OverageResetsAt, subscriptionKey: entry?.Info.Provider, source: "turn");
                    _activity?.Touch(entry?.Info.Provider);
                    // Состояние пула правим только по известным окнам (IsExhaustionWindow):
                    // rejected неизвестного окна — транзитная телеметрия CLI, она попадает
                    // в usage для экрана, но ротацию не трогает.
                    if (entry is not null && ClaudeSubscriptionPool.IsExhaustionWindow(m.LimitType))
                    {
                        // Исчерпание лимита подписки → помечаем exhausted в пуле, чтобы новые чаты
                        // пошли на другую подписку. "rejected" — CLI отклонил ход; utilization >= 1.0
                        // без overage — окно выбрано (с overage ходы ещё проходят).
                        if (m.Status == "rejected" || (m.Utilization >= 1.0 && !m.IsUsingOverage))
                        {
                            var resetsAt = m.ResetsAt is not null && DateTime.TryParse(m.ResetsAt, out var dt)
                                ? (DateTime?)dt.ToUniversalTime() : null;
                            _subscriptionPool.MarkExhausted(entry.Info.Provider, resetsAt);
                            // Сразу перевозим чат на здоровый аккаунт пула — кнопка «Повторить»
                            // упавшего хода пойдёт уже через него. Если переключиться некуда,
                            // а ход реально отбит — предлагаем сторонний провайдер карточкой.
                            TryPoolFailover(sessionId, entry);
                            if (m.Status == "rejected")
                                await OfferProviderFallbackAsync(sessionId, m.ResetsAt);
                        }
                        // Самолечение: живой ход через аккаунт — сильнейший сигнал, что он
                        // работает; снимаем пометку, как это делает идл-пинг warmup
                        // (RecordAndGuard). Без этого ложный бан висел до resetsAt: активные
                        // аккаунты warmup не пингует, а ходовой обработчик только маркировал.
                        // Компромисс осознанный: allowed по five_hour снимет пометку и при
                        // реально выбранном seven_day — следующий ход тут же перемаркирует,
                        // false-negative на минуты дешевле false-positive на сутки.
                        else if (_subscriptionPool.IsExhausted(entry.Info.Provider))
                        {
                            _subscriptionPool.Reset(entry.Info.Provider);
                            Console.WriteLine($"[SessionManager] Подписка «{entry.Info.Provider}» отвечает (ход {sessionId}) — снята пометка исчерпания");
                        }
                    }
                    break;
                case ErrorMessage m:
                    await acc.OnErrorAsync(m.Text, _history);
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
            {
                newStatus = SessionStatus.Waiting;
                // Кэш для replay при JoinSession: после F5 клиент должен снова увидеть
                // карточку, которую ждёт CLI
                entry.PendingInteraction = msg;
            }
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

            // Конец хода/обрыв — ожидающей карточки больше нет
            if (msg is ResultMessage or ErrorMessage or ExitedMessage)
                entry.PendingInteraction = null;

            if (newStatus.HasValue)
                await ApplyStatusAsync(sessionId, entry, newStatus.Value);

            // Цикл «до готово»: решение о продолжении — по result/error хода, нёсшего
            // протокол цикла (LoopTurnInFlight). Раньше триггером был exited, но после
            // механики доживания exited приходит лишь со смертью прогона — цикл замирал
            // на десятки минут при живых фоновых агентах.
            // В фоне, чтобы не блокировать read-loop адаптера пересозданием процесса.
            // Режим «Командная реализация»: ход штаба закончился — разбираем его маркеры
            // (эскалация, новая работа) и переводим завершённую проверку в ожидание вводной.
            // В фоне по той же причине, что и цикл «до готово»: публикация карточки,
            // уведомление и планирование не должны держать read-loop адаптера.
            if (msg is ResultMessage or ErrorMessage && entry.Info.TeamImplement is not null)
            {
                // Пара ErrorMessage(ExpectResultFollows)+ResultMessage одного хода (см. комментарий
                // у SessionEntry.SkipNextTeamTurnEnd) — обработали по ErrorMessage, спаренный
                // ResultMessage только гасит флаг и второй раз ход не разбирает.
                if (msg is ResultMessage && entry.SkipNextTeamTurnEnd)
                {
                    entry.SkipNextTeamTurnEnd = false;
                }
                else
                {
                    if (msg is ErrorMessage { ExpectResultFollows: true }) entry.SkipNextTeamTurnEnd = true;
                    string teamTurnText;
                    bool teamTurnAsked;
                    string? catchUpDelta;
                    lock (entry.TeamTurnLock)
                    {
                        teamTurnText = entry.TeamTurnText.ToString();
                        // Ход закончился — ждать больше нечего: то, что живая трансляция
                        // придерживала как «вдруг это начало маркера» (TrimAmbiguousMarkerTail),
                        // дальше не дорастёт ни во что — довешиваем как обычный текст.
                        var finalSafe = StripTeamProtocolMarkers(teamTurnText);
                        catchUpDelta = finalSafe.Length > entry.TeamTurnShownLength
                            ? finalSafe[entry.TeamTurnShownLength..] : null;
                        entry.TeamTurnShownLength = 0;
                        entry.TeamTurnText.Clear();
                        teamTurnAsked = entry.TeamTurnAsked;
                        entry.TeamTurnAsked = false;
                    }
                    if (!string.IsNullOrEmpty(catchUpDelta))
                        await BroadcastAsync(sessionId, new TextDeltaMessage(catchUpDelta));
                    var teamTurnFailed = msg is ErrorMessage or ResultMessage { Subtype: "error" };
                    _ = Task.Run(async () =>
                    {
                        try { await HandleTeamTurnEndAsync(sessionId, teamTurnText, teamTurnFailed, teamTurnAsked); }
                        catch (Exception ex)
                        {
                            Console.Error.WriteLine($"[SessionManager] Конец хода штаба ({sessionId}): {ex.Message}");
                        }
                    });
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
            // доставил бы второе сообщение параллельно уже запущенному ходу. Аварийная смерть
            // процесса (exited без предшествующего result и без метки) очередь не разбирает —
            // она остаётся ждать следующего пользовательского хода. В фоне — как и work-loop,
            // чтобы не держать read-loop адаптера.
            var drainOnExited = msg is ExitedMessage && runId != 0 && entry.DrainOnExitedRun == runId;
            if (msg is ResultMessage or ErrorMessage or ExitedMessage && entry.DrainOnExitedRun == runId)
                entry.DrainOnExitedRun = 0;
            if (drainOnExited
                || (msg is ResultMessage or ErrorMessage && !entry.LoopTurnInFlight
                    && entry.Info.WorkLoop is null))
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

        if (sendBroadcast) await BroadcastAsync(sessionId, msg);

        if (entry is not null && OnSessionMessage is { } observers)
        {
            // Multicast Func<> await-ит только Task ПОСЛЕДНЕГО подписчика — ждём всех явно,
            // иначе исключения и незавершённая работа не-последних наблюдателей теряются
            foreach (var observer in observers.GetInvocationList().Cast<Func<Session, ServerMessage, Task>>())
            {
                try { await observer(entry.Info, msg); }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[SessionManager] Ошибка наблюдателя сессии ({sessionId}): {ex.Message}");
                }
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

    // Детектит завершённую glif-генерацию прямо в tool_result: кредиты уже есть в _meta.glif,
    // поэтому публикуем сообщение синхронно, без фонового опроса как у fal.
    private void TryTrackGlifCost(string sessionId, string content)
    {
        if (_glif?.Enabled != true) return;
        var msg = GlifCostParser.TryParse(content);
        if (msg is not null)
            _ = PublishGlifCostAsync(sessionId, msg);
    }

    // Догоняет учёт для СТАРЫХ glif-генераций в истории, у которых ещё нет glif_cost.
    // Вызывается при загрузке истории сессии.
    private void BackfillGlifCosts(string sessionId, IReadOnlyList<StoredMessage> history)
    {
        if (_glif?.Enabled != true) return;
        var have = new HashSet<string>();
        foreach (var m in history)
            if (m is StoredGlifCostMessage g) have.Add(g.JobId);
        foreach (var m in history)
        {
            if (m is not StoredToolUseMessage t || t.IsError || string.IsNullOrEmpty(t.Result)) continue;
            var msg = GlifCostParser.TryParse(t.Result);
            if (msg is not null && !have.Contains(msg.JobId))
                _ = PublishGlifCostAsync(sessionId, msg);
        }
    }

    // Публикация учёта glif-генерации: запись в историю (дедуп) + broadcast клиентам.
    // Зеркало PublishFalCostAsync: активная сессия → аккумулятор; неактивная → прямо на диск.
    public async Task PublishGlifCostAsync(string sessionId, GlifCostMessage msg)
    {
        if (!_sessions.TryGetValue(sessionId, out var entry)) return;

        await _falPersistLock.WaitAsync();
        bool duplicate = false;
        try
        {
            if (entry.Accumulator is not null)
            {
                if (!entry.Accumulator.OnGlifCost(msg.JobId, msg.OutputType, msg.MediaCount, msg.Credits, msg.Model))
                    duplicate = true;
                else
                {
                    try { await entry.Accumulator.SaveSnapshotAsync(_history); }
                    catch (Exception ex) { Console.Error.WriteLine($"[GlifCost] Сохранение истории ({sessionId}) не удалось: {ex.Message}"); }
                }
            }
            else if (entry.Info.ClaudeSessionId is string key)
            {
                try
                {
                    var stored = await _history.LoadAsync(key);
                    if (stored.Any(m => m is StoredGlifCostMessage g && g.JobId == msg.JobId))
                        duplicate = true;
                    else
                    {
                        stored.Add(new StoredGlifCostMessage(msg.JobId, msg.OutputType, msg.MediaCount, msg.Credits, msg.Model));
                        await _history.SaveAsync(key, stored);
                    }
                }
                catch (Exception ex) { Console.Error.WriteLine($"[GlifCost] Прямая запись истории ({sessionId}) не удалась: {ex.Message}"); }
            }
        }
        finally { _falPersistLock.Release(); }

        if (duplicate) return;

        // Аналитика: генерация glif — счётчик операций, кредиты про запас, стоимость USD неизвестна.
        if (_spend is not null)
            try
            {
                var s = entry.Info;
                _spend.Record(new SpendRecord
                {
                    OwnerId = ResolveOwnerId(s) ?? "",
                    ProjectId = s.ProjectId,
                    SessionId = s.Id,
                    TaskId = s.TaskId,
                    PersonaId = s.PersonaId,
                    Provider = "glif",
                    Model = msg.Model ?? msg.OutputType,
                    Source = SpendSources.Glif,
                    CostUsd = null,
                    Generations = 1,
                    Label = msg.OutputType,
                });
            }
            catch (Exception ex) { _log.LogWarning(ex, "spend: запись генерации glif не удалась"); }

        await BroadcastAsync(sessionId, msg);
    }

    // Публикация найденной стоимости fal.ai: запись в историю (дедуп) + broadcast клиентам.
    // Активная сессия → через аккумулятор; неактивная (нет аккумулятора) → прямо в файл истории,
    // иначе стоимость не переживёт переоткрытие и «считается…» зависнет.
    public async Task PublishFalCostAsync(string sessionId, FalCostMessage msg)
    {
        if (!_sessions.TryGetValue(sessionId, out var entry)) return;

        // Весь выбор ветки (аккумулятор vs прямая запись на диск) — под _falPersistLock, тем же,
        // что берёт ленивое оживление аккумулятора в EnsureProcessCoreAsync. Иначе check-then-act
        // на entry.Accumulator гонялся бы с оживлением → двойная запись/потеря стоимости.
        await _falPersistLock.WaitAsync();
        bool duplicate = false;
        try
        {
            if (entry.Accumulator is not null)
            {
                if (!entry.Accumulator.OnFalCost(msg.RequestId, msg.EndpointId, msg.CostUsd, msg.OutputUnits, msg.UnitPrice))
                    duplicate = true; // уже опубликован
                else
                {
                    try { await entry.Accumulator.SaveSnapshotAsync(_history); }
                    catch (Exception ex) { Console.Error.WriteLine($"[FalCost] Сохранение истории ({sessionId}) не удалось: {ex.Message}"); }
                }
            }
            else if (entry.Info.ClaudeSessionId is string key)
            {
                // Сессия не активна — пишем стоимость напрямую в историю на диске
                try
                {
                    var stored = await _history.LoadAsync(key);
                    if (stored.Any(m => m is StoredFalCostMessage f && f.RequestId == msg.RequestId))
                        duplicate = true; // уже в истории
                    else
                    {
                        stored.Add(new StoredFalCostMessage(msg.RequestId, msg.EndpointId, msg.CostUsd, msg.OutputUnits, msg.UnitPrice));
                        await _history.SaveAsync(key, stored);
                    }
                }
                catch (Exception ex) { Console.Error.WriteLine($"[FalCost] Прямая запись истории ({sessionId}) не удалась: {ex.Message}"); }
            }
        }
        finally { _falPersistLock.Release(); }

        if (duplicate) return; // дубликат — не ретранслируем

        // Аналитика расхода: генерация fal.ai — счётчик операций (токенов у fal нет),
        // фактическая стоимость про запас. Дедуп выше гарантирует одну запись на request_id.
        if (_spend is not null)
            try
            {
                var s = entry.Info;
                _spend.Record(new SpendRecord
                {
                    OwnerId = ResolveOwnerId(s) ?? "",
                    ProjectId = s.ProjectId,
                    SessionId = s.Id,
                    TaskId = s.TaskId,
                    PersonaId = s.PersonaId,
                    Provider = "fal",
                    Model = msg.EndpointId,
                    Source = SpendSources.Fal,
                    CostUsd = msg.CostUsd,
                    Generations = 1,
                    Label = msg.EndpointId,
                });
            }
            catch (Exception ex) { _log.LogWarning(ex, "spend: запись генерации fal не удалась"); }

        await BroadcastAsync(sessionId, msg);
    }

    // Запись расхода штатного хода в аналитику (Spend Analytics): все разрезы из Session,
    // модель — фактическая из modelUsage result'а (субагенты могли считать другой моделью),
    // фолбэк — модель сессии. Ошибка записи ход не роняет.
    private void RecordTurnSpend(SessionEntry? entry, ResultMessage m)
    {
        if (_spend is null || entry is null || m.Usage is null) return;
        try
        {
            var s = entry.Info;
            var provider = SpendSources.NormalizeProvider(s.Provider);
            // Фактическая модель хода из modelUsage (субагенты могли считать другой), фолбэк —
            // модель сессии; пустой результат резолвится в дефолт подписки, чтобы SpendRecord
            // никогда не оставался без модели (иначе в аналитике копилась «Модель по умолчанию»).
            var model = _llmProviders.ResolveModelOrDefault(m.UsageModel ?? s.Model, provider);
            _spend.Record(new SpendRecord
            {
                OwnerId = ResolveOwnerId(s) ?? "",
                ProjectId = s.ProjectId,
                SessionId = s.Id,
                TaskId = s.TaskId,
                PersonaId = s.PersonaId,
                Provider = provider,
                Model = model,
                Source = SpendSources.IsFree(provider, model) ? SpendSources.Free : SpendSources.ChatTurn,
                InputTokens = m.Usage.InputTokens,
                OutputTokens = m.Usage.OutputTokens,
                CacheReadTokens = m.Usage.CacheReadTokens,
                CacheCreationTokens = m.Usage.CacheCreationTokens,
                CostUsd = m.TotalCostUsd,
                DurationMs = m.DurationMs,
            });
        }
        catch (Exception ex) { _log.LogWarning(ex, "spend: запись хода не удалась"); }
    }

    // Запись StoredMessage в историю сессии ВНЕ хода + broadcast (обобщение паттерна
    // PublishFalCostAsync): активная сессия → через Accumulator + SaveSnapshot;
    // неактивная → LoadAsync + append + SaveAsync под локом. Используется совещаниями.
    public async Task AppendStoredAsync(string sessionId, StoredMessage stored, ServerMessage broadcast)
    {
        if (!_sessions.TryGetValue(sessionId, out var entry)) return;

        // Как в PublishFalCostAsync: выбор ветки под _falPersistLock, чтобы check-then-act на
        // entry.Accumulator не гонялся с ленивым оживлением аккумулятора (EnsureProcessCoreAsync).
        await _falPersistLock.WaitAsync();
        try
        {
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
            }
        }
        finally { _falPersistLock.Release(); }

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
        acc.GetAll().Skip(Math.Max(0, baseline)).OfType<StoredTextMessage>()
            .LastOrDefault(t => t.ParentToolUseId is null)?.Text ?? "";

    // Для fire-and-forget задач: ошибку логируем, а не теряем молча
    private static void FireAndForget(Task task, string context) =>
        task.ContinueWith(
            t => Console.Error.WriteLine($"[SessionManager] {context}: {t.Exception?.GetBaseException().Message}"),
            TaskContinuationOptions.OnlyOnFaulted);

    // Остановка всех живых адаптеров — вызывается при graceful shutdown приложения,
    // иначе после остановки сервера остаются зомби-процессы (claude + node MCP-серверов)
    public void KillAllProcesses()
    {
        // Таймер не должен пытаться писать одновременно с убийством процессов:
        // сценарий — shutdown, SaveSessions() ждёт _saveLock, а в это время адаптеры
        // claude не диспозятся → процессы зависают в памяти (боролись ранее).
        _autoSaveTimer?.Dispose();

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

    // IDisposable — только для _autoSaveTimer. Адаптеры (процессы claude) убивает
    // KillAllProcesses() из ApplicationStopping. Не дублируем — иначе два cleanup-пути.
    public void Dispose()
    {
        _autoSaveTimer?.Dispose();
        GC.SuppressFinalize(this);
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
        return s is not null && ResolveOwnerId(s) == ownerId ? s : null;
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
