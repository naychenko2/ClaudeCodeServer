using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
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
        // Текст ответа текущего хода — для маркера эскалации координатора (Э4) и для стрижки
        // маркеров протокола в живой ленте. Копится в ЛЮБОМ чате: маркер молчания
        // `<no-reply/>` живёт вне штаба, да и сохранённая история чистится безусловно.
        // Отдельный буфер от LoopTurnText: у режимов разные потребители, и очистка одного
        // не должна съедать маркер другого (оба режима могут быть включены разом).
        public System.Text.StringBuilder TeamTurnText = new();
        public readonly object TeamTurnLock = new();
        // Сколько символов «очищенного от маркеров» текста текущего хода уже ушло
        // в живую трансляцию (волна 6): считаем от StripTeamProtocolMarkers(TeamTurnText),
        // а не от длины самого TeamTurnText — иначе диффом в дельту попал бы вырезанный
        // маркер. Живёт рядом с TeamTurnText, сбрасывается вместе с ним под TeamTurnLock.
        public int TeamTurnShownLength;
        // В тексте текущего хода уже встречался '<'. Пока не встречался, маркеру взяться
        // неоткуда — дельта транслируется как есть, без склейки и разбора всего хода
        // (стрижка идёт во всех чатах, и платить за неё на каждой дельте обычной прозы
        // незачем). Живёт рядом с TeamTurnText и чистится вместе с ним (под TeamTurnLock).
        public bool TurnSawAngleBracket;
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
        // Под фолбэком пара доезжает только при настоящем провале (FailClosed/FailExhausted):
        // если попытку сменила подмена, ни её ошибка, ни её result наружу не идут — флаг не
        // взводится и не виснет, а конец хода разбирается по финальному result.
        public volatile bool SkipNextTeamTurnEnd;
        // Текущий ход штаба поднят сообщением ЧЕЛОВЕКА (M7): авто-подтверждение добавочного
        // плана опирается на «вводная человека и есть точка контроля», поэтому инициатора
        // хода помечаем при запуске (SendDirectAsync / SendMessageAndWaitAsync) — классификация
        // агентской вводной как работы публикует план неподтверждённым и ждёт человека.
        public bool TeamTurnFromHuman;
        // Счётчики бюджета итерации правит и раздача волны, и гейт запуска на ходу-реакции
        public readonly object TeamLock = new();
        // Сабагент этого хода оборвался на середине (паспорт прогона с Truncated) — по концу
        // хода уходит добивание. Пишет приёмник паспортов (поток ватчера сабагентов), читает
        // обработчик result — отсюда volatile. null — обрывов не было либо уже добили.
        public volatile Llm.Claude.SubagentRunPassport? TruncatedSubagent;
        // Пометка координатору: ФОНОВЫЙ агент оборвался на tool_use, а CLI выдал координатору
        // его последнюю реплику за готовый результат. Своего хода на пометку не тратим (второй
        // systemDirective в идущий процесс слать нельзя) — она уезжает префиксом ближайшего
        // хода, см. BuildCliTurnText. null — пометки нет либо она уже уехала.
        public volatile Llm.Claude.SubagentRunPassport? TruncatedBgNote;
        // Сколько добиваний подряд отправлено ЗА ОДНОГО агента (потолок — MaxSubagentNudges).
        // Обнуляется штатным отчётом ТОГО ЖЕ агента и любым ходом человека: две попытки — на серию.
        public int SubagentNudges;
        // Агент, чью серию добиваний считает SubagentNudges. Без привязки к agentId потолок
        // не достигался вовсе: в ходе с двумя агентами штатный отчёт агента B обнулял счётчик
        // оборвавшегося агента A, и добивание уходило с attempt=1 по кругу.
        public volatile string? NudgeAgentId;
        // Ход завершился ошибкой (result error / error) — цикл не продолжаем
        public bool LoopTurnFailed;
        // Текущий ход нёс протокол цикла «до готово» (выставляет BuildCliTurnText):
        // продолжение цикла решается по result ИМЕННО этого хода, а не по exited —
        // после механики доживания exited приходит лишь со смертью прогона (до 30 мин
        // при живых фоновых агентах). Чужие ходы (REST-канал агентов, /compact,
        // ходы-продолжения CLI) протокола не несут и цикл не двигают.
        public volatile bool LoopTurnInFlight;
        // Момент, когда result/error хода перевёл статус в Active (ход завершён, ждём exited для
        // финального Finished). Источник «зависшей» Active: exited приходит лишь со смертью
        // прогона, а прогон доживает фоновых агентов до BgLingerTimeout (30 мин) или вовсе не
        // выходит — статуса Finished нет, чат висит active. Sweep-terminus в SaveSessions по этой
        // метке отличает «зависший Active» (нужно довести до Finished) от «идущий ход» (Working/
        // Waiting метки не имеют). DateTime? — struct, доступ под PendingLock. null — хода нет.
        public DateTimeOffset? LastTurnEndedAt;
        // Разбор очереди (DrainNextPendingAsync) вне цикла идёт по одному сообщению за раз —
        // следующее уйдёт по result уже этого хода. Параллельные drain (выключение цикла из
        // SetWorkLoopAsync + штатный drain из OnMessageAsync по result) без этого флага
        // вытащили бы ДВА сообщения и послали два хода. Взводится атомарно с извлечением,
        // гасится по концу доставки — второй drain в окне видит его и уступает (сообщение
        // не теряется: по result хода отработает следующий drain).
        public volatile bool DrainInFlight;
        // Ожидающая карточка взаимодействия (разрешение/вопрос/план) — replay при
        // JoinSession: без него клиент после F5 видел бы «Claude печатает…» без
        // возможности ответить, а CLI ждал бы до часового таймаута
        public ServerMessage? PendingInteraction;
        // Контекст адаптера устарел (смена собеседника / правка персоны) — убирается
        // ЛЕНИВО перед следующим ходом, чтобы не рвать активный ход и доживающих агентов
        public volatile bool AdapterStale;
        // Текущий ход идёт в чужом git worktree (session_started с TurnWorktree): правки
        // такого хода живут в другом дереве и пометку «зафиксировано в git»
        // (CommittedFilePaths) с путей КОРНЯ не снимают — зеркало гейта
        // SessionChangedPaths.Extract. Взводится/сбрасывается session_started,
        // сбрасывается сообщением пользователя (начало нового хода в основном дереве).
        public volatile bool TurnInWorktree;
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
        // занятую сессию, и доставляемые по концу текущего хода. Пользовательские ход НЕ
        // прерывают (отправка ≠ остановка, как в claude CLI) — кроме случаев, когда ждать
        // нечего: ход стоит на вопросе человеку (Waiting) или идёт цикл «до готово».
        // Агентские (chats_send) прерывают обычный ход — ждущий их агент упрётся в таймаут, —
        // но ход в цикле «до готово» и в штабе не рушат: там они ждут конца цикла и штатного
        // конца хода соответственно. Явный перебой по кнопке — PreemptForPending.
        // Только в памяти — при рестарте сессия и так становится Orphaned, доставлять
        // накопленное в умерший контекст незачем. QueueFrozen — «Стоп» заморозил разбор:
        // автодоставки нет до возобновления пользователем (новое сообщение).
        public readonly List<QueuedMessage> Pending = [];
        public readonly Lock PendingLock = new();
        public volatile bool QueueFrozen;
        // Прогон, в котором идёт сворачивание контекста (/compact). Обычный ход, но обёртка
        // фолбэка его не оркеструет, а exited убитой компакции глотает — поэтому прерывать
        // компакцию нельзя (чат залип бы в Working навсегда). Хранится идентификатор прогона,
        // а не флаг (та же причина, что у DrainOnExitedRun): поздний терминал ЧУЖОГО
        // доживающего прогона иначе снял бы защиту с идущей компакции. 0 — компакции нет.
        public long CompactRun;
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
        // Живой локальный голосовой ход (место chat-voice на «Локальная»): отмена для
        // «Стоп». Ставится RunLocalVoiceTurnAsync, гасится в её finally. volatile:
        // Interrupt читает из другого потока. null — локального хода нет.
        public volatile CancellationTokenSource? LocalVoiceCts;
        // Сколько локальных голосовых ходов прошло с последнего CLI-хода: при возврате
        // на CLI BuildCliTurnText допишет сводку разговора (транскрипт CLI этих реплик
        // не знает). Не персистится — рестарт сервера сводку теряет (v1, редкий кейс).
        public int LocalTurnsSinceCli;
    }

    // Дальше какой глубины цепочка автоотчётов не идёт. 3 — как у делегирования задач:
    // исполнитель → постановщик → его постановщик, дальше эскалация теряет смысл.
    private const int MaxReportChainDepth = 3;

    // Grace-окно для sweep-terminus Active→Finished (P12/P15): если после result хода exited
    // прогона не пришёл (доживающий прогон при живых фоновых агентах, подавленный SuppressExited,
    // висящий без работы процесс) — по истечении окна sweep доводит статус до Finished сам.
    // Размер — ровно на перечитывание клиентом истории хода по active-статусу (useSession.ts:
    // по active клиент перечитывает, по finished — нет; мгновенный переход закрыл бы окно доставки).
    // Секунды, не минуты: долгий grace лишь растягивает окно ложного active. По истечении решает
    // признак живости (HasLiveTurn/HasPendingBg), а не время — sweep не лезет в прогон с живой
    // фоновой работой (панель экспертов идёт > 10 мин). Калибруется через Session:StuckActiveGraceSeconds.
    private const int DefaultStuckActiveGraceSeconds = 15;
    private readonly int _stuckActiveGraceSeconds;

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
    // Маршруты мест каталога (локаль/слот/модель) и параметры профилей — для ветки
    // локального голосового хода (chat-voice). null — в тестах без локали.
    private readonly Llm.LocalActionRouter? _router;
    // Прямой HTTP-клиент Ollama для локальных голосовых ходов; null — в тестах.
    private readonly Llm.OllamaClient? _ollama;
    // Планировщик режима «Командная реализация» (Э2); null — режим без планирования
    private readonly TeamPlanningService? _teamPlanning;
    // Платформа внешних модулей: реестр манифестов + выпуск модульных токенов (R7)
    private readonly Modules.ModuleRegistry? _modules;
    private readonly Modules.ModuleTokenService? _moduleTokens;
    private readonly ChatHistoryService _history;
    // Снимки промпта ходов (кнопка «какой промпт ушёл»); null — в тестах
    private readonly PromptSnapshotStore? _promptSnapshots;
    // Паспорта прогонов сабагентов (диагностика обрывов + сигнал для автодобивания); null — в тестах
    private readonly Llm.Claude.SubagentRunLog? _subagentRuns;
    private readonly string _sessionsFilePath;
    private readonly Lock _saveLock = new();
    // Автосохранение сессий каждые 30с
    /// <summary>
    /// Период автосохранения (оно же — единственный фоновый триггер sweep-terminus, см. SaveSessions).
    /// Настраивается ключом <c>Session:AutoSaveSeconds</c>; 0 и меньше — таймер не заводится вовсе.
    ///
    /// Ноль нужен ТЕСТАМ, и не ради скорости: sweep живёт внутри SaveSessions, поэтому фоновый
    /// таймер выполняет его в произвольный момент — в том числе между двумя ассертами теста.
    /// Вместе с глобальным Session.TaskSourceSessionResolver, который переустанавливает конструктор
    /// каждого нового TaskManager в параллельном классе, это давало плавающее падение
    /// Sweep_ЖивойПотомокВГлубину: иерархия делегирования на миг переставала резолвиться, и sweep
    /// закрывал сессию, которую тест только что проверил живой. Поодиночке ни один из двух факторов
    /// не воспроизводился — падало только на полном прогоне и не каждый раз.
    /// </summary>
    private static readonly TimeSpan DefaultAutoSaveInterval = TimeSpan.FromSeconds(30);
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
    // Токены грани десктопа (ADR-008): кеш по чату поверх _jwt, отдельный от сервисных
    private readonly Desktop.DesktopCapabilityTokenService _desktopTokens;
    private readonly Microsoft.AspNetCore.Hosting.Server.IServer _server;
    private readonly IConfiguration _config;
    // Копии транскриптов заархивированных чатов (data/archived-transcripts) — шаг 0 плана
    // «Архив чатов»: создаётся в конструкторе напрямую, см. там же
    private readonly ArchivedTranscriptStore _archivedTranscripts;
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

    // Есть ли у сессии живой прогон CLI (ход идёт либо вот-вот начнётся — принят адаптером,
    // процесс поднимается секунды). Тот же предикат, что stuck-детект Interrupt, вынесен
    // наружу для пульса волны «Командной реализации»: «штаб заявляет работу (Working/
    // Waiting), а прогона нет» = мёртвый штаб, а не живая волна.
    public bool HasLiveTurnProcess(string sessionId) =>
        _sessions.TryGetValue(sessionId, out var entry)
        && entry.Process is { HasLiveTurn: true } or { HasQueuedTurn: true };

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
    private readonly SkillsService? _skills;
    // Аналитика расхода токенов (null — в тестах: сбор выключен)
    private readonly Spend.ISpendCollector? _spend;
    // Per-ход slice top-10 god-nodes Code Graph в системный промпт (ADR вариант A);
    // null — в тестах, тогда блок графа в промпт не попадает
    private readonly CodeGraph.CodeGraphPromptProvider? _codeGraphPrompt;
    // Граф кода: уборка снимка отдельного дерева чата при его удалении (ADR-003); null — в тестах
    private readonly CodeGraph.CodeGraphService? _codeGraphs;
    // Watcher'ы файлов: снятие watcher'а отдельного дерева чата при его удалении; null — в тестах
    private readonly FileWatcherService? _fileWatchers;
    // Личный реестр MCP-серверов владельца + значения их секретов (null — в тестах:
    // ход идёт только со встроенными серверами и наследством .mcp.json)
    private readonly Mcp.McpRegistry? _mcpRegistry;
    private readonly Mcp.McpSecretStore? _mcpSecrets;
    // Последний известный статус серверов: пишется из system/init каждого хода; null — в тестах
    private readonly Mcp.McpStatusStore? _mcpStatus;
    // OAuth внешних серверов: обновление протухшего токена перед сборкой конфига хода; null — в тестах
    private readonly Mcp.McpOAuthService? _mcpOAuth;
    // Recall паспортов изменений (этап 2, ADR-004 §5); null — в тестах, секции паспортов нет
    private readonly Dossiers.DossierRecallService? _dossierRecall;
    // Резолвер секций промпта специальности (план «Секции промптов», флаг
    // specialty-prompt-sections); null — в тестах, секция prompt-sections в промпт не попадает
    // (перестановка блока досье в dossier-recall от него не зависит — только от флага).
    private readonly SpecialtySettingsStore? _specialtySettings;
    // Кеш якорей «файлы предыдущего хода» для recall паспортов: sessionId → (отпечаток истории,
    // файлы). Пересбор — только когда файл истории сменился (LastWriteUtc), не на каждый ход.
    private readonly Dictionary<string, (DateTime? Stamp, List<string> Files)> _dossierAnchorCache = new();
    // Секция Dify (ApiUrl/ApiKey/неймспейс) — для BuildDifyContext (волна 4): единственное
    // потребление тут — проверка настроенности и строки stdio-ветки отката; вся работа с
    // Dify — в KnowledgeService со своей копией IOptions
    private readonly Models.DifyOptions _dify = new();

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
        // Опционально (в тестах не передаётся): скиллы для блока «Командные механики»
        // руководителя проекта; без него в блоке остаются механики без скилла
        SkillsService? skills = null,
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
        // Опционально (в тестах не передаётся): recall паспортов изменений (этап 2,
        // ADR-004 §5) — пассивная секция промпта персоны; без него ходы идут как раньше
        Dossiers.DossierRecallService? dossierRecall = null,
        // Опционально (в тестах не передаётся): паспорта прогонов сабагентов. Без него
        // диагностики обрывов нет и автодобивание молчит — ходы идут как раньше.
        Llm.Claude.SubagentRunLog? subagentRuns = null,
        // Опционально (в тестах не передаётся): маршрутизатор мест и клиент локальной
        // модели — ветка локального голосового хода (chat-voice). Без них разговор
        // идёт через claude CLI как раньше.
        Llm.LocalActionRouter? router = null,
        Llm.OllamaClient? ollama = null,
        // Опционально (в тестах не передаётся): резолвер секций промпта специальности
        // (план «Секции промптов») — без него секция prompt-sections не собирается
        SpecialtySettingsStore? specialtySettings = null)
    {
        _subagentRuns = subagentRuns;
        _router = router;
        _ollama = ollama;
        _specialtySettings = specialtySettings;

        _skills = skills;
        _mcpRegistry = mcpRegistry;
        _mcpSecrets = mcpSecrets;
        _mcpStatus = mcpStatus;
        _mcpOAuth = mcpOAuth;
        _dossierRecall = dossierRecall;
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
        _desktopTokens = new Desktop.DesktopCapabilityTokenService(jwt);
        _server = server;
        _config = config;
        // Копии транскриптов архивных чатов (шаг 0 плана «Архив чатов»): стор файловый и
        // без зависимостей, создаём напрямую — точки вызова (SetArchived/DeleteAsync) живут
        // здесь, отдельная регистрация в Program.cs ничего не добавляет.
        _archivedTranscripts = new ArchivedTranscriptStore(config);
        config.GetSection(Models.DifyOptions.Section).Bind(_dify);
        // Sweep-terminus grace (P12/P15): потолок ожидания exited после result до принудительного
        // Active→Finished. <=0 — выключить sweep (только для тестов/отладки).
        _stuckActiveGraceSeconds = int.TryParse(config["Session:StuckActiveGraceSeconds"], out var grace)
            && grace >= 0 ? grace : DefaultStuckActiveGraceSeconds;
        // Порог свежести хода для гейта перезапуска (этап 3): тот же ключ/дефолт, что у
        // пульса волны — TeamWaveService._quietThreshold
        _freshTurnThreshold = TimeSpan.FromMinutes(
            int.TryParse(config["TeamImplement:QuietMinutes"], out var quietMin) && quietMin > 0 ? quietMin : 15);
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
        var autoSave = int.TryParse(config["Session:AutoSaveSeconds"], out var secs)
            ? TimeSpan.FromSeconds(secs)
            : DefaultAutoSaveInterval;
        if (autoSave > TimeSpan.Zero)
            _autoSaveTimer = new Timer(_ => SaveSessions(), null, autoSave, autoSave);
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
    // internal — ту же формулу резолвит TasksToolset по живой сессии-вызывателю (http,
    // ADR-012 волна 2): право на сервер проверяется на каждый tools/list и tools/call
    internal bool TasksMcpEnabled(string? ownerId, Session session, Persona? persona) =>
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
        CodeGraphMcpContext? codeGraph = null, DifyMcpContext? dify = null) =>
        widgets is { UseHttp: true } || memory is { UseHttp: true }
        || tasks is { UseHttp: true } || notes is { UseHttp: true } || personas is { UseHttp: true }
        || workspace is { UseHttp: true } || notifications is { UseHttp: true }
        || codeGraph is { UseHttp: true } || dify is { UseHttp: true };

    // Браузер (плагин playwright): нужен по роли тестировщику, остальным персонам — нет.
    // Ключ-надстройка «browser» с дефолтом по пресету (SectionEnabled → SpecialtySections),
    // как git/kb; чат без персоны получает браузер как раньше — ручную проверку страницы
    // человек делает из своего чата. Решение зависит только от персоны, поэтому постоянно
    // в рамках сессии (оно входит в сигнатуру прогона — см. ClaudeRuntimeSettings).
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
        var apiUrl = ResolveTasksApiUrl(ownerId);
        return new CodeGraphMcpContext(apiUrl, () => GetServiceToken(ownerId), projectId, sessionId, rootPath,
            UseHttp: HttpEndpointUsable(apiUrl));
    }

    // Право чата на десктопную грань по СУЩНОСТИ чата — единая точка правды (ADR-008:
    // «Грань не доставляется в ходы исполнения задач, отложенные и регулярные чаты,
    // групповые чаты»), никаких дублей-предикатов рядом. internal static — чистая функция,
    // тестируется напрямую (DesktopTurnEligibleTests).
    internal static bool DesktopTurnEligible(Session session) =>
        // Десктопный ли чат (тип чата «Десктопный») — свойство конфигурации чата, а не хода
        session.DesktopChat
        // Чат-исполнитель задачи (в том числе отложенной и регулярной — их создаёт
        // TaskExecutionService по расписанию, человека у машины в этот момент нет) и чат
        // правила проактивности: Origin выводится из TaskId/AutomationRuleId (Session.Origin)
        && session.Origin == ChatOrigin.Manual
        && !session.TaskExecution
        // Групповой чат: руки одного устройства на несколько собеседников не делятся.
        // Participants заполняется ТОЛЬКО у групповых (ValidateParticipants: 2–8 персон);
        // чат с одной персоной хранит её в PersonaId — поэтому «есть участники» == «групповой».
        // Проверяем Count > 0, а не Count > 1: если валидацию состава когда-нибудь ослабят
        // до одиночных участников, грань не должна молча поехать в чат с чужой персоной.
        && session.Participants is not { Count: > 0 };

    // Контекст MCP-сервера десктопной грани (ADR-008, «Два уровня, которые нельзя смешивать»):
    // состав грани решает КОНФИГУРАЦИЯ на момент запуска CLI — тип чата «Десктопный» плюс
    // включение грани в проекте, — и никогда состояние хода. Право на каждый конкретный вызов
    // проверяет бэкенд (DesktopAccessGate), поэтому здесь нет ни сеанса рук, ни устройства:
    // их появление и исчезновение не должно менять tools/list и перезапускать процесс CLI.
    // Право чата по его сущности — DesktopTurnEligible (единственная точка правды);
    // персона может отказаться от грани Off-привязкой tool:desktop, как от codegraph/widgets.
    private DesktopMcpContext? BuildDesktopContext(string? ownerId, Session session, Persona? persona)
    {
        if (ownerId is null || string.IsNullOrEmpty(session.ProjectId)) return null;
        if (!DesktopTurnEligible(session)) return null;
        if (!_flags.IsEnabled(ownerId, FeatureFlagKeys.DesktopAgent)) return null;
        if (_projects.GetById(session.ProjectId!)?.DesktopAgentEnabled != true) return null;
        if (!_bindings.ServerToolEnabled(ownerId, persona, "desktop")) return null;
        // Capability-токен чата, а не сервисный JWT владельца: /api/devices/* его не принимают
        return new DesktopMcpContext(ResolveTasksApiUrl(ownerId),
            _desktopTokens.TokenFor(ownerId, session.Id), session.Id);
    }

    // Подсказка про трейлер CCS-Session/CCS-Task (ADR-004, «Паспорта изменений»): только
    // проектные сессии владельца — DossierCaptureService захватит коммит с этим трейлером.
    private string? BuildDossierTrailerHint(string? ownerId, Session session)
    {
        if (ownerId is null || session.ProjectId is null) return null;
        var taskLine = session.TaskId is null ? "" : $"\nCCS-Task: {session.TaskId}";
        return "Если делаешь `git commit` в этом проекте — добавь в сообщение коммита трейлер " +
            $"отдельной строкой (рядом с Co-Authored-By):\nCCS-Session: {session.Id}{taskLine}\n" +
            "Он привязывает коммит к этому чату/задаче для фичи «История решений» (паспорт изменения " +
            "с выжимкой «зачем/решения/отказы/грабли») — без него автоматическая выжимка не соберётся. " +
            "Не убирай и не меняй значение при amend/squash.";
    }

    // Контекст MCP-сервера памяти персоны (та же фабрика сервисного токена, что у tasks/notes).
    // projectId — проект ТЕКУЩЕГО чата (③-3.4: даёт доступ к team_memory_* команды), не scope
    // персоны — см. BuildPersonaLayer: любая персона в проектном чате получает эти инструменты,
    // пишет ли она в команду реально — решает бэкенд-гейт (TeamMemoryService.WriteDeniedFor).
    // DossierToolsEnabled — секция dossier_lookup/dossier_get (этап 2, ADR-004 §5): гейт по
    // флагу ВЛАДЕЛЬЦА change-dossiers-recall. Решение стабильно в рамках сессии (флаг меняется
    // человеком из меню редко) и входит в отпечаток состава сервера (shapes memory) — от
    // СВОЙСТВ ХОДА состав tools/list не зависит (инвариант McpToolsetStabilityTests).
    // UseHttp — сервер памяти переехал в Kestrel (ADR-012, фаза 2): ход зовёт его по http
    // (personaId/projectId едут хвостом URL), процесса node нет; false — откат на stdio.
    private MemoryMcpContext BuildMemoryContext(string ownerId, string personaId, string? projectId)
    {
        var apiUrl = ResolveTasksApiUrl(ownerId);
        return new MemoryMcpContext(apiUrl, () => GetServiceToken(ownerId), personaId, projectId,
            DossierToolsEnabled: _flags.IsEnabled(ownerId, FeatureFlagKeys.ChangeDossiersRecall),
            UseHttp: HttpEndpointUsable(apiUrl));
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
    // session — для контекста паспортов изменений (этап 2, ADR-004 §5): проект/дерево чата,
    // задача и якоря «файлы предыдущего хода». Гейт флага change-dossiers-recall — на каждый
    // ход внутри (переключение действует сразу, как у заметок).
    private Func<string, Task<RecallBlock?>> BuildPersonaRecallProvider(string ownerId, Session session, string personaId)
    {
        var topK = int.TryParse(_config["Persona:RecallTopK"], out var k) ? k : 5;
        // Шкала скоринга — взвешенная сумма (PersonaMemoryScorer), порог ~0.30;
        // старый дефолт 0.02 относился к шкале произведения и больше не валиден
        var minScore = double.TryParse(_config["Persona:RecallMinScore"],
            System.Globalization.CultureInfo.InvariantCulture, out var s) ? s : 0.30;
        var timeoutMs = int.TryParse(_config["Persona:RecallTimeoutMs"], out var t) ? t : 2500;

        return async text =>
        {
            var query = KnowledgeService.TrimQuery(text);
            if (query.Length == 0) return null;
            try
            {
                // Паспорта изменений: контекст проекта чата (не scope персоны — как team-memory),
                // гейт по флагу владельца на каждый ход
                Dossiers.DossierRecallRequest? dossier = null;
                if (_dossierRecall is not null && session.ProjectId is { } dossierProjectId
                    && _flags.IsEnabled(ownerId, FeatureFlagKeys.ChangeDossiersRecall))
                {
                    var prevTurnFiles = await LastTurnChangedFiles(session);
                    dossier = new Dossiers.DossierRecallRequest(
                        dossierProjectId,
                        EffectiveRootOf(session),
                        session.TaskId,
                        [.. Dossiers.DossierRecallService.ExtractPathsFromText(text), .. prevTurnFiles],
                        text);
                }

                // Перестановка блока досье в свою секцию (план «Секции промптов» этап 3) —
                // за тем же флагом, что и вклейка prompt-sections (dark launch единым флагом):
                // выключен — досье остаётся ВНУТРИ recall-memory, как до фичи.
                var splitDossier = _flags.IsEnabled(ownerId, FeatureFlagKeys.SpecialtyPromptSections);
                var recallTask = _personaMemory.BuildRecallAsync(ownerId, personaId, query, topK, minScore,
                    dossier, splitDossier);
                var completed = await Task.WhenAny(recallTask, Task.Delay(timeoutMs));
                if (completed != recallTask) return null;   // таймаут — ход без recall
                var recall = await recallTask;
                if (recall?.Text is null && recall?.DossierText is null) return null;
                // Манифест: hits личной памяти + команды проекта + паспорта → айтемы (F3).
                // Паспорта — видимость для человека: видно, какие записи истории решений
                // реально учтены персоной в этом ходу.
                var items = recall.Hits.Select(h => new RecallItem("memory", h.Id, h.Text, null))
                    .Concat(recall.TeamHits.Select(e => new RecallItem("team", e.Id, e.Text, null)))
                    .Concat(recall.DossierHits.Select(d => new RecallItem("dossier", d.Id,
                        $"Паспорт {d.CommitSha[..Math.Min(7, d.CommitSha.Length)]}: {d.CommitSubject}", null)))
                    .ToList();
                return new RecallBlock(recall.Text, items, recall.DossierText);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Persona memory recall для {Persona}", personaId);
                return null;
            }
        };
    }

    // Рабочее дерево сессии (ADR-003): у чата с worktree своё дерево — паспорта и их статусы
    // считаются по нему (HEAD и снимок графа у деревьев разные).
    private string? EffectiveRootOf(Session session)
    {
        if (session.WorktreePath is { } wt) return wt;
        return session.ProjectId is { } pid ? _projects.GetById(pid)?.RootPath : null;
    }

    // Якоря «файлы предыдущего хода этой сессии» (ADR-004 §5): write-инструменты последнего
    // завершённого хода из истории. Перечитываем историю только когда её файл сменился
    // (LastWriteUtc) — кеш не гоняет повторное чтение на каждом ходу персоны.
    private async Task<IReadOnlyList<string>> LastTurnChangedFiles(Session session)
    {
        try
        {
            var stamp = session.ClaudeSessionId is null ? null : _history.LastWriteUtc(session.ClaudeSessionId);
            lock (_saveLock)
            {
                if (_dossierAnchorCache.TryGetValue(session.Id, out var cached) && cached.Stamp == stamp)
                    return cached.Files;
            }
            if (session.ClaudeSessionId is null) return [];

            var history = await _history.LoadAsync(session.ClaudeSessionId);

            // Хвост от предпоследнего сообщения пользователя: последнее — текущий ход (уже
            // дописан к моменту сборки промпта) либо прошлый ход (если текущее ещё не в
            // истории); в обоих случаях последний ЗАВЕРШЁННЫЙ ход попадает в диапазон.
            var userIdx = new List<int>();
            for (var i = 0; i < history.Count; i++)
                if (history[i] is StoredUserMessage) userIdx.Add(i);
            var start = userIdx.Count >= 2 ? userIdx[^2] : 0;
            var root = EffectiveRootOf(session) ?? "";
            List<string> files = root.Length == 0
                ? []
                : [.. SessionChangedPaths.Extract(history.Skip(start).ToList(), root).Keys];

            lock (_saveLock) _dossierAnchorCache[session.Id] = (stamp, files);
            return files;
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "dossiers: якоря прошлого хода {Session}", session.Id);
            return [];
        }
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

            var query = KnowledgeService.TrimQuery(text);
            if (query.Length == 0) return null;

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
        // Значок темы переехал из имени в Session.Topic — переносим старые эмодзи-имена.
        // Идемпотентно: на уже перенесённых сессиях это no-op без записи на диск
        if (Llm.ChatTopicMigration.Apply(list)) SaveSessions();
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

            // P12/P15: sweep-terminus. SaveSessions зовётся из ~50 точек (включая автосохранение
            // по таймеру Session:AutoSaveSeconds) — этого довольно, чтобы без отдельного таймера
            // гарантированно довести зависшую в Active сессию до Finished. Ранний выход, когда
            // кандидатов нет, — O(N) под _saveLock только если хоть одна сессия в «завершённом Active».
            if (_stuckActiveGraceSeconds <= 0) return;
            if (!_sessions.Values.Any(e => e.Info.Status == SessionStatus.Active && e.LastTurnEndedAt.HasValue))
                return;

            // P28: id сессий, в поддереве которых (по иерархии делегирования) есть живой
            // исполнитель. Корень такого поддерева не терминируется, иначе ложный Finished в
            // разгар волны (дефект P28). Рекурсивно в ГЛУБИНУ: цепочка делегирования законна до
            // глубины 3 (координатор → исполнитель → суб-исполнитель), и если жив только нижний
            // узел, а средний уже отдал result и ждёт потомка — правило по прямым детям закрыло
            // бы корень, и дефект просто уехал бы на уровень глубже.
            //
            // Живость узла — HasLiveWork (мёртвый процесс не жив ни при каком статусе: клинч P30,
            // когда Waiting залипает у мёртвого исполнителя, не должен держать предков вечно).
            // Иерархия — по TaskId → SourceSessionId (резолв lock-free через ConcurrentDictionary в
            // TaskManager), без пятого хранилища состояния. От каждого живого узла поднимаемся к
            // корню, отмечая всех предков. Ограничитель цикла — как в IsDescendantOf: данных из
            // бэкапа достаточно для кольца в иерархии, и битая связь не должна завесить SaveSessions.
            var protectedByLiveSubtree = new HashSet<string>();
            foreach (var e in _sessions.Values)
            {
                if (!HasLiveWork(e)) continue;
                var cur = ParentSourceSession(e.Info.Id);
                for (var steps = 0; cur is not null && steps < 256; steps++)
                {
                    if (!protectedByLiveSubtree.Add(cur)) break; // уже отмечен — цикл или общий предок
                    cur = ParentSourceSession(cur);
                }
            }

            foreach (var entry in _sessions.Values)
                TrySweepStuckActive(entry, protectedByLiveSubtree);
        }
    }

    // P12/P15: гарантированный terminus Active→Finished. Штатно Finished выставляет ExitedMessage,
    // но exited приходит лишь со смертью прогона — а прогон доживает фоновых агентов (до
    // BgLingerTimeout, 30 мин) либо вовсе не выходит (висящий без работы процесс, подавленный
    // SuppressExited). Без sweep чат висел active неопределённо долго (формы а/б/в).
    // Условие terminus: ход завершён (LastTurnEndedAt по result→Active) И grace истёк И НЕТ живой
    // фоновой работы — процесс мёртв (!HasLiveTurn) либо доживает впустую без единой фоновой задачи
    // (HasLiveTurn && !HasPendingBg). Живой Workflow (HasPendingBg) НЕ трогаем: Finished при живой
    // работе хуже вечного active — синтез панели прилетел бы в «завершённый» чат. Планировщик
    // штаба (TeamPlanningInFlight, до 300 с без CLI-прогона) и активная итерация цикла
    // (LoopTurnInFlight) гейтятся отдельно — фоновые режимы между ходами, не держащие прогон.
    // ApplyStatusAsync запускаем через Task.Run ВНЕ _saveLock: это асинхронный бродкаст с
    // повторной записью стора — держать его под локом пути сохранения незачем (сам
    // System.Threading.Lock реентерабелен, вложенный SaveSessions не клинил бы, но await
    // под локом невозможен, а удлинять критическую секцию sweep'а нет смысла). Порядок
    // локов _saveLock→PendingLock консистентен, обратного нигде нет.
    private void TrySweepStuckActive(SessionEntry entry, HashSet<string> protectedByLiveSubtree)
    {
        if (entry.Info.Status != SessionStatus.Active) return;
        // Фоновые режимы без прогона между ходами: HasLiveTurn=false, но работа идёт, Finished был
        // бы ложным («Finished при живой работе хуже вечного active»). Планировщик штаба живёт до
        // 300 с без CLI-прогона. Цикл «до готово» между итерациями: после result хода LoopTurnInFlight
        // уже сброшен, статус Active, метка свежая — но цикл ещё включён (WorkLoop != null) и вот-вот
        // поднимет следующую итерацию; пауза при ретрае/фолбэке/тормозах провайдера может превысить
        // grace, и ложный Finished мигнул бы «завершено» посреди цикла. Поэтому гейт — по включённому
        // циклу, а не по маркеру итерации: как только SetWorkLoopAsync обнулил WorkLoop (форма в),
        // сессия становится обычным кандидатом, и sweep её закрывает.
        if (entry.TeamPlanningInFlight) return;
        if (entry.Info.WorkLoop is not null) return;

        // P28: в поддереве этой сессии есть живой исполнитель (прямой потомок или глубже) — sweep
        // не имеет права мигать Finished в разгар волны. Гейт по РЕАЛЬНОЙ живости потомка (набор
        // protectedByLiveSubtree собран в SaveSessions рекурсивно через HasLiveWork), а не по статусу
        // задачи/сессии: зависший исполнитель (мёртвый процесс в Waiting/Working, дефект P30) в набор
        // не попадает и предка не держит — иначе клинч хуже минутного ложного Finished.
        if (protectedByLiveSubtree.Contains(entry.Info.Id)) return;

        // Живая собственная работа (фон/продолжение) — оценка в HasLiveWork, единая точка истины
        // о живости (тот же предикат, что отсеивает живые узлы поддерева выше). Для Active-кандидата
        // это «процесс жив и есть фоновая работа или идёт/готово продолжение» (HasPendingBg — панель
        // экспертов/Workflow, IsContinuationInFlight — ход-ответ на task_notification bg-агента), иначе
        // terminus: процесс мёртв либо доживает впустую, и после ContinuationStartGrace ридер его убьёт.
        if (HasLiveWork(entry)) return;

        // Захват решения под PendingLock — повторный sweep или OnMessageAsync (новый ход) увидят сброс
        // и не запустят дубль. LastTurnEndedAt читается/пишется ТОЛЬКО под PendingLock (контракт поля,
        // см. объявление): DateTimeOffset? неатомарен, чтение вне лока даёт порванное значение
        // (HasValue=true, устаревшие ticks). Перепроверка статуса — гонка с параллельным сменщиком.
        lock (entry.PendingLock)
        {
            if (entry.Info.Status != SessionStatus.Active) return;
            if (entry.LastTurnEndedAt is not { } endedAt) return;
            if ((DateTimeOffset.UtcNow - endedAt).TotalSeconds < _stuckActiveGraceSeconds) return;
            entry.LastTurnEndedAt = null;
        }

        var sid = entry.Info.Id;
        var adapter = entry.Process as ILlmSessionAdapter;
        // Sweep-терминус — штатное завершение хода через grace (P28): не авария, лог не должен
        // подсвечивать её как Warning. На Information она и остаётся в обычном прогоне.
        _log.LogInformation(
            "[SessionManager] Sweep terminus: Active→Finished после result хода ({Sid}: exited прогона не было, alive={Alive}, bg={Bg}, cont={Cont})",
            sid, adapter is { HasLiveTurn: true }, adapter is { HasPendingBg: true }, adapter is { IsContinuationInFlight: true });
        _ = Task.Run(async () =>
        {
            try { await ApplyStatusAsync(sid, entry, SessionStatus.Finished); }
            catch (Exception ex) { _log.LogError(ex, "[SessionManager] Sweep ApplyStatus не удался ({Sid})", sid); }
        });
    }

    // «Сессия прямо сейчас ведёт живую работу» — единая оценка живости для sweep-terminus (гейт
    // собственного хода в TrySweepStuckActive) и для гейта «в поддереве есть живой исполнитель»
    // (P28, сбор protectedByLiveSubtree в SaveSessions). Одна точка истины — чтобы не плодить
    // расходящиеся формулы живости (следующий фиксер не гадал, какая из них верная).
    //
    // Живость ПРОЦЕССА (HasLiveTurn) обязательна при ЛЮБОМ статусе: мёртвый исполнитель не
    // считается работающим ни в Waiting (открытый дефект P30 — статус залипает у мёртвого процесса),
    // ни в Working — иначе он держал бы предков вечно (клинч P28). Active — особый случай: ход уже
    // отдан (result), процесс доживает, и жив лишь пока есть фоновая работа (HasPendingBg) или идёт/
    // готово ход-продолжение (IsContinuationInFlight). Starting/Working/Waiting — ход идёт, процесс
    // поднимается или стоит на permission-запросе: живы при живом процессе. Finished/Error/Orphaned
    // мертвы.
    private static bool HasLiveWork(SessionEntry entry)
    {
        if (entry.Process is not ILlmSessionAdapter adapter) return false;
        if (!adapter.HasLiveTurn) return false;
        return entry.Info.Status switch
        {
            SessionStatus.Active => adapter.HasPendingBg || adapter.IsContinuationInFlight,
            SessionStatus.Starting or SessionStatus.Working or SessionStatus.Waiting => true,
            _ => false,
        };
    }

    // Источник делегирования «порождён этим прогоном» для sweep-гейта P28: SourceSessionId задачи
    // сессии (резолв lock-free через ConcurrentDictionary в TaskManager). null — обычный чат без
    // задачи либо задача удалена (корень иерархии). Не ParentSessionId: тот учитывает ручную
    // группировку (override/detach), а нас интересует именно «порождён прогоном штаба».
    private string? ParentSourceSession(string sessionId)
    {
        if (!_sessions.TryGetValue(sessionId, out var entry)) return null;
        return entry.Info.TaskId is { } tid
            ? Session.TaskSourceSessionResolver?.Invoke(tid)
            : null;
    }

    // Только для тестов: запустить sweep-terminus (P12/P15) вне обычных триггеров SaveSessions,
    // чтобы детерминированно проверить переход Active→Finished по истечению grace. Прод-код
    // триггерит sweep каждым SaveSessions (включая автосохранение) — этого API тестам не нужно.
    internal void RunStuckActiveSweepForTests() => SaveSessions();

    // --- Публичное API ---

    public IReadOnlyCollection<Session> GetByProject(string projectId) =>
        _sessions.Values
            .Where(e => e.Info.ProjectId == projectId)
            .Select(e => e.Info)
            .OrderByDescending(s => s.UpdatedAt)
            .ToList();

    // Сессии владельца с ЖИВЫМИ фоновыми агентами — снимок для холодного старта списка чатов
    // (событие bg_agents_presence клиент мог пропустить, пока не вошёл в группы). Состояния
    // не держим: источник истины — сам прогон, HasPendingBg берёт свой лок на мгновение.
    // Сначала фильтр по фону (живой фон — редкость), потом резолв владельца.
    public IReadOnlyList<string> GetSessionsWithLiveAgents(string ownerId) =>
        _sessions.Values
            .Where(e => e.Process is { HasTrackedBg: true } && ResolveOwnerId(e.Info) == ownerId)
            .Select(e => e.Info.Id)
            .ToList();

    // Та же выборка для фоновых КОМАНД (Bash с run_in_background): у них свой, тихий значок в
    // списке чатов. Отдельный метод, а не флаг в предыдущем: наборы пересекаются (в чате может
    // идти и агент, и дев-сервер), и клиенту нужны оба списка целиком.
    public IReadOnlyList<string> GetSessionsWithLiveBgCommands(string ownerId) =>
        _sessions.Values
            .Where(e => e.Process is { HasTrackedCommandBg: true } && ResolveOwnerId(e.Info) == ownerId)
            .Select(e => e.Info.Id)
            .ToList();

    // Чаты, подпадающие под автоправило архивации «без сообщений дольше N дней» (план v4,
    // флаг chat-auto-archive) — ЕДИНАЯ точка отбора для счётчика превью
    // (GET /api/chats/archive-preview) и тика правила (ChatArchiveService.TickAsync):
    // превью обязано считать той же функцией, что архивирует, иначе счётчик покажет «3»,
    // а исчезнет 200 (пре-мортем №2). nowUtc — параметром, чтобы превью и тик сходились
    // при одном моменте времени; фронт этот отбор повторить не может в принципе
    // (HasTurnInFlight и живость агентов — серверные).
    //
    // projectId != null — чаты проекта (владение проектом контроллер проверил отдельно,
    // у проектных сессий OwnerId null); null — чаты вне проекта владельца ownerId
    // (личный дефолт правила). Потолок пачки и ArchivedBy="rule" — забота тика, не отбора.
    public IReadOnlyList<Session> GetArchiveRuleCandidates(string ownerId, string? projectId, int days, DateTime nowUtc)
    {
        var cutoff = nowUtc - TimeSpan.FromDays(days);
        var result = new List<Session>();
        foreach (var entry in _sessions.Values)
        {
            var info = entry.Info;
            if (projectId is null)
            {
                if (info.ProjectId is not null || info.OwnerId != ownerId) continue;
            }
            else if (info.ProjectId != projectId) continue;
            if (!MatchesArchiveRule(info, cutoff)) continue;
            // Живость в чистый предикат не входит: она — свойство entry/адаптера, не Session
            if (HasTurnInFlight(entry) || entry.Process is { HasTrackedBg: true }) continue;
            result.Add(info);
        }
        return result;
    }

    // Чистая часть предиката правила (порог + исключения плана v4; живость хода/фоновых
    // агентов — в GetArchiveRuleCandidates). internal static для юнит-тестов, образец —
    // ShouldExpire в ChatExpiryService. Исключения: закреплённые («чат нужен»), временные
    // (ими управляет свой срок), онбординг (человек в середине знакомства), штаб в работе
    // (Idle с закрытыми волнами — можно), чат живой задачи-исполнителя (выполненной — можно).
    internal static bool MatchesArchiveRule(Session s, DateTime cutoff) =>
        !s.IsArchived
        && !s.IsPinned
        && s.ExpiresAfterMinutes is null
        && s.OnboardingKind is null
        && s.UpdatedAt <= cutoff
        && (s.TeamImplement is not { } ti
            || (ti.Stage == TeamImplementStage.Idle && ti.WaveNumber <= ti.ClosedWave))
        && (s.TaskId is null || s.TaskDone);

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

    // Убрать чат в архив (archived = true) / вернуть (false). Единственная точка записи
    // полей архива — как SetParent для группировки. by — "user" (пункт меню) | "rule"
    // (автоправило), batchId — идентификатор прохода правила (откат возвращает ровно одну
    // пачку; у ручной архивации null).
    //
    // UpdatedAt/LastReadAt намеренно НЕ трогает (как SetExpiry): по ним сортируется список
    // и считается непрочитанность, а архивация — не активность; возврат не должен всплывать
    // чат наверх и метить его непрочитанным. Признак архива производный (Session.IsArchived),
    // поэтому «снять архив» — это сброс полей, и повторная активность снимет его и без
    // мутатора. Попутно копируем транскрипт в data/archived-transcripts (архивация) или
    // возвращаем его в профиль (возврат) — best-effort, сбой файловой части не роняет вызов.
    //
    // SaveSessions сознательно НЕ зовём: ручная архивация сохранит стор сразу после вызова,
    // а проход автоправила пишет файл ОДИН раз на всю пачку (до 200 чатов за тик — иначе
    // каждая архивация перезаписывала бы sessions.json целиком и дёргала sweep).
    public Session? SetArchived(string sessionId, bool archived, string by, string? batchId = null)
    {
        if (!_sessions.TryGetValue(sessionId, out var entry)) return null;
        if (archived)
        {
            entry.Info.ArchivedAt = DateTime.UtcNow;
            entry.Info.ArchivedBy = by;
            entry.Info.ArchiveBatchId = batchId;
            ArchiveTranscriptCopy(entry.Info);
        }
        else
        {
            entry.Info.ArchivedAt = null;
            entry.Info.ArchivedBy = null;
            entry.Info.ArchiveBatchId = null;
            RestoreTranscriptCopy(entry.Info);
        }
        return entry.Info;
    }

    // Копия транскрипта при архивации: источники — ВСЕ корни профилей, как у уборки при
    // удалении (DeleteTranscript): за время жизни чат мог мигрировать между профилями и
    // рабочими папками, а миграции исходники не удаляют. Сам стор валидирует csid белым
    // списком и гейтит десктопные чаты; best-effort — сбой не имеет права ронять архивацию.
    private void ArchiveTranscriptCopy(Session info)
    {
        try
        {
            if (info.ClaudeSessionId is not string csid) return;
            _archivedTranscripts.Archive(csid, info.DesktopChat, TranscriptSearchRoots(info), TryResolveCwd(info));
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Копия транскрипта при архивации чата {SessionId} не создана", info.Id);
        }
    }

    // Возврат копии при возврате чата: цель резолвится НА МОМЕНТ возврата — за время в
    // архиве могли смениться профиль провайдера (MigrateProviderAsync) и папка уплощённого
    // cwd (worktree, правка RootPath), поэтому исходный путь не запоминаем.
    private void RestoreTranscriptCopy(Session info)
    {
        try
        {
            if (info.ClaudeSessionId is not string csid) return;
            var hostCwd = TryResolveCwd(info);
            if (hostCwd is null) return;
            var ownerId = ResolveOwnerId(info);
            _archivedTranscripts.Restore(csid,
                ConfigRootFor(ownerId, info.Provider), CwdForOwner(ownerId, hostCwd));
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Копия транскрипта при возврате чата {SessionId} не возвращена", info.Id);
        }
    }

    // Корни профилей CLI для поиска транскрипта чата: все конфиг-корни провайдеров плюс
    // профили песочницы владельца (раскладка RewriteProfileEnv; берём ВСЕ папки владельца,
    // а не один ключ — чат мог мигрировать). Выделено из DeleteTranscript: тот же список
    // нужен и копии при архивации.
    private IEnumerable<string> TranscriptSearchRoots(Session info)
    {
        var roots = new List<string>(_llmProviders.GetAllConfigRoots());
        if (ResolveOwnerId(info) is string ownerId)
        {
            var ownerProfiles = Path.Combine(_sandbox.ProfilesHostDir, ownerId);
            if (Directory.Exists(ownerProfiles))
                roots.AddRange(Directory.GetDirectories(ownerProfiles));
        }
        return roots;
    }

    // Ручная архивация/возврат из архива (PUT /api/chats/{id}/archived, шаг 2 плана
    // «Архив чатов»): обёртка над SetArchived для точки входа из UI — гейт живости,
    // снятие срока временного чата, персист и событие ленты chat_archived. Автоправило
    // (ChatArchiveService) зовёт SetArchived напрямую: у него свой гейт отбора и одна
    // SaveSessions() на всю пачку.
    //
    // Гейт живости — только на архивацию: возврат ничего не рвёт, а «вернуть из архива,
    // пока идёт ход» — ровно то, что происходит при снятии архива активностью. 409 на
    // живом ходе отсюда уезжает InvalidOperationException (конвенция RestartWave и др.).
    public async Task<Session?> SetArchivedAsync(string sessionId, string ownerId, bool archived)
    {
        if (GetOwned(sessionId, ownerId) is not { } info) return null;
        if (archived && _sessions.TryGetValue(sessionId, out var entry))
        {
            if (HasTurnInFlight(entry))
                throw new InvalidOperationException(
                    "В чате идёт ход — дождитесь его завершения или прервите его, затем уберите чат в архив");
            if (entry.Process is { HasTrackedBg: true })
                throw new InvalidOperationException(
                    "В чате работают фоновые агенты — дождитесь их завершения, затем уберите чат в архив");
            // Временный чат: архив бессрочен до возврата, срок снимаем ДО записи признака
            // архива — иначе чат умер бы по таймеру ChatExpiryService уже в архиве
            // (SetExpiry заодно обнуляет ExpiryAnchor). SetExpiry пишет стор сам —
            // промежуточное состояние «срок снят, архива нет» безопасно.
            if (info.ExpiresAfterMinutes is not null) SetExpiry(sessionId, null);
        }
        var updated = SetArchived(sessionId, archived, by: "user");
        if (updated is null) return null;
        SaveSessions();
        await BroadcastChatArchivedAsync(sessionId, updated, archived);
        return updated;
    }

    // Уведомить клиентов об архивации/возврате чата (адресация как у BroadcastChatDeletedAsync):
    // project-группа для проектной сессии, user-группа для чата вне проекта
    private async Task BroadcastChatArchivedAsync(string sessionId, Session info, bool archived)
    {
        var msg = new ChatArchivedMessage(archived) with { SessionId = sessionId };
        var tasks = new List<Task> { _hub.Clients.Group(sessionId).SendAsync("message", msg) };
        if (info.ProjectId is string pid)
            tasks.Add(_hub.Clients.Group("project_" + pid).SendAsync("message", msg));
        else if (info.OwnerId is string oid)
            tasks.Add(_hub.Clients.Group("user_" + oid).SendAsync("message", msg));
        await Task.WhenAll(tasks);
    }

    // Проход автоправила архивации (ChatArchiveService, шаг 6 плана v4): заархивировать
    // пачку одним batchId — ОДНОЙ SaveSessions на весь проход (до 200 чатов; иначе каждая
    // архивация переписывала бы sessions.json целиком и дёргала sweep) и событием
    // chat_archived каждому чату. Гейт живости не повторяем: отбор кандидатов
    // (GetArchiveRuleCandidates) уже его отработал; кому-то стать живым между отбором и
    // записью — допустимая гонка, чат вернёт из архива собственная активность.
    public async Task ArchiveBatchAsync(IReadOnlyCollection<string> sessionIds, string batchId)
    {
        var archived = new List<(string Id, Session Info)>();
        foreach (var id in sessionIds)
        {
            var updated = SetArchived(id, archived: true, by: "rule", batchId);
            if (updated is not null) archived.Add((id, updated));
        }
        if (archived.Count == 0) return;
        SaveSessions();
        foreach (var (id, info) in archived)
            await BroadcastChatArchivedAsync(id, info, archived: true);
    }

    // Откат пачки автоправила из уведомления/раздела «Архив»: вернуть РОВНО чаты прохода
    // batchId (ArchivedBy="rule" и чат ещё в архиве), а не всю историю правила. Одна
    // SaveSessions на пачку, событие возврата каждому. Владелец — по GetOwned: батч-id
    // приходит из URL, чужой не должен возвращать чужие чаты (даже угаданный).
    public async Task<int> RestoreArchiveBatchAsync(string ownerId, string batchId)
    {
        var restored = new List<(string Id, Session Info)>();
        foreach (var s in _sessions.Values.Select(e => e.Info)
                     .Where(s => s.ArchiveBatchId == batchId && s.ArchivedBy == "rule" && s.IsArchived)
                     .ToList())
        {
            if (GetOwned(s.Id, ownerId) is null) continue;
            var updated = SetArchived(s.Id, archived: false, by: "user");
            if (updated is not null) restored.Add((s.Id, updated));
        }
        if (restored.Count == 0) return 0;
        SaveSessions();
        foreach (var (id, info) in restored)
            await BroadcastChatArchivedAsync(id, info, archived: false);
        return restored.Count;
    }

    // Включить/выключить временность чата: minutes > 0 — авто-удаление через N минут
    // после последней активности, null — обычный чат.
    //
    // UpdatedAt намеренно НЕ обновляется (как в SetParent): по нему сортируется список и
    // считается непрочитанность, а смена настройки хранения — не активность чата. Отсчёт
    // срока при этом не должен стартовать в прошлом, поэтому включение ставит ExpiryAnchor —
    // дедлайн считается от него, если он позже последней активности.
    public Session? SetExpiry(string sessionId, int? minutes)
    {
        if (!_sessions.TryGetValue(sessionId, out var entry)) return null;
        entry.Info.ExpiresAfterMinutes = minutes;
        entry.Info.ExpiryAnchor = minutes is null ? null : DateTime.UtcNow;
        SaveSessions();
        return entry.Info;
    }

    // Заглушить/включить уведомления по чату (браузерные «нужно решение» / «ход завершён»).
    // UpdatedAt не трогаем по той же причине, что в SetExpiry: это настройка, а не активность.
    public Session? SetNotificationsMuted(string sessionId, bool muted)
    {
        if (!_sessions.TryGetValue(sessionId, out var entry)) return null;
        entry.Info.NotificationsMuted = muted;
        SaveSessions();
        return entry.Info;
    }

    // Включить/выключить голосовой режим чата и/или сменить стиль озвучки.
    // Один сеттер на оба поля намеренно: каждый дёргает SaveSessions(), то есть перезапись
    // всего списка — два раздельных дали бы две записи файла на один PUT.
    // null-аргумент = поле не трогаем: стиль приезжает и БЕЗ флага (устройство выправляет
    // чужой стиль у чата с уже включённой озвучкой), а такой запрос не должен её гасить.
    // UpdatedAt не трогаем по той же причине, что в SetExpiry: это настройка, а не активность.
    public Session? SetVoiceMode(string sessionId, bool? on, string? style = null)
    {
        if (!_sessions.TryGetValue(sessionId, out var entry)) return null;
        if (on is bool value) entry.Info.VoiceMode = value;
        if (style is not null) entry.Info.VoiceStyle = VoiceStyles.Normalize(style);
        SaveSessions();
        return entry.Info;
    }

    // Отметить чат прочитанным (синк непрочитанности между устройствами).
    // UpdatedAt намеренно не трогаем (как в SetExpiry): прочтение — не активность чата.
    // false — сессии нет или она не принадлежит owner'у (контроллер отдаёт 404).
    // Идемпотентность: LastReadAt уже >= UpdatedAt → true без перезаписи файла
    // (SaveSessions пишет весь список — незачем гонять диск на повторных отметках).
    public bool MarkRead(string sessionId, string ownerId)
    {
        if (GetOwned(sessionId, ownerId) is not { } s) return false;
        if (s.LastReadAt >= s.UpdatedAt) return true;
        s.LastReadAt = DateTime.UtcNow;
        SaveSessions();
        return true;
    }

    // Opt-out «Истории решений» (ADR-004 §6): тумблер «Не сохранять решения из этого чата».
    // Персистится в sessions.json; DossierCaptureService проверяет его при захвате коммита.
    public Session? SetExcludeFromDossiers(string sessionId, bool value)
    {
        if (!_sessions.TryGetValue(sessionId, out var entry)) return null;
        entry.Info.ExcludeFromDossiers = value;
        entry.Info.UpdatedAt = DateTime.UtcNow;
        SaveSessions();
        return entry.Info;
    }

    // Все сессии (для планировщика авто-удаления временных чатов)
    public IReadOnlyCollection<Session> GetAll() =>
        _sessions.Values.Select(e => e.Info).ToList();

    // Разовая переадресация закреплённых моделей (миграция каталога провайдера): id из карты
    // заменяется, всё остальное — включая незнакомые модели и «preset:{id}» — остаётся как есть.
    // Возвращает число изменённых чатов; 0 — стор на диск не переписывается.
    // Идёт через живой реестр, а не файл: иначе первый же SaveSessions вернул бы старые id.
    public int RemapModels(IReadOnlyDictionary<string, string> map)
    {
        var changed = 0;
        foreach (var info in _sessions.Values.Select(e => e.Info))
        {
            if (info.Model is null || !map.TryGetValue(info.Model.Trim(), out var next)) continue;
            info.Model = next;
            changed++;
        }
        if (changed > 0) SaveSessions();
        return changed;
    }

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

    // Дозапись состава инструментов в снимок: приходит из system/init, уже после его записи.
    // Тем же приёмником — единственная точка записи статуса MCP-серверов: CLI перечисляет в
    // init все поднятые серверы (и встроенные продуктовые, и записи личного реестра), так что
    // наблюдение достаётся бесплатно, без фонового поллинга и правок в ClaudeSession.
    private Action<string, IReadOnlyList<string>, IReadOnlyList<McpServerInfo>>? PromptToolsSinkFor(string sessionId) =>
        _promptSnapshots is null && _mcpStatus is null
            ? null
            : (snapshotId, tools, servers) =>
            {
                _promptSnapshots?.AttachCliLayer(sessionId, snapshotId, tools, servers);
                if (_mcpStatus is null || servers.Count == 0) return;
                // Владелец — по сессии (у проектной это владелец проекта): статусы per-user,
                // как и сам реестр. Сессии уже нет / владелец не резолвится — наблюдение некуда класть
                if (_sessions.TryGetValue(sessionId, out var entry)
                    && ResolveOwnerId(entry.Info) is { } ownerId)
                    _mcpStatus.RecordFromInit(ownerId, sessionId, servers);
            };

    // Приёмник паспортов прогонов сабагентов: пишет диагностику и, если агент оборвался на
    // середине (последнее его сообщение — tool_use, отчёта нет), взводит отметку на сессии —
    // добивание уходит по концу хода (см. NudgeTruncatedSubagentAsync).
    // null — стор не подключён (тесты): ходы идут как раньше, просто без паспортов.
    private Action<Llm.Claude.SubagentRunPassport>? SubagentRunSinkFor(string sessionId) =>
        _subagentRuns is null ? null : passport =>
        {
            _subagentRuns.Record(passport);
            if (!_sessions.TryGetValue(sessionId, out var entry)) return;
            if (passport.Truncated)
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
                // в транскрипт, и пометка могла взвестись по хвосту tool_use агента, который
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
        };

    // Рабочая папка сессии: отдельное worktree чата приоритетнее корня проекта.
    // Единая точка подмены cwd — через неё идут обе funnel-точки LlmSessionContext.
    // internal — той же формулой DifyToolset резолвит дефолтный датасет проекта чата
    // (волна 4): расхождение формул означало бы разный состав tools/list и shape.
    internal static string EffectiveRoot(Session session, string fallbackRoot) =>
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

    // «Переезжать некуда»: по СЫРЫМ полям чата (Info.Model → Info.Provider) цель совпала с
    // текущим провайдером. Для эндпоинта migrate-provider это отказ (просили перенести — не
    // перенесли, 400), для UpdateAsync — штатный случай: он сравнивает провайдеров по
    // ЭФФЕКТИВНЫМ моделям, и после переставленного назначения места две картины расходятся.
    // Отдельный ТИП, а не сравнение текста исключения: текст пишется человеку и меняется, а
    // ловля по нему молча пропускала бы наружу любую другую форму «переезжать не нужно»
    // ложным 400 на весь PATCH.
    private sealed class ProviderUnchangedException()
        : InvalidOperationException("Чат уже на этом провайдере");

    // Единственная точка смены провайдера у чата: и кнопка «Продолжить на …» (исчерпан
    // лимит), и обычная смена модели в настройках (UpdateAsync). Транскрипт CLI локальный —
    // переносим его в профиль целевого провайдера и продолжаем разговор через --resume без
    // потери контекста.
    // subscriptionKey — явный выбор аккаунта ТОГО ЖЕ пула подписок (кнопка карточки с
    // Kind="subscription"): вместо автовыбора Pick пользователь указывает конкретный ключ.
    // model = null — «По умолчанию» из настроек чата, когда назначение места модель не даёт:
    // переезжаем на родной Claude, ничего не закрепляя. Эндпоинт migrate-provider пустую
    // модель по-прежнему не принимает — проверка живёт у него.
    public async Task<Session> MigrateProviderAsync(string sessionId, string ownerId, string? model,
        string? subscriptionKey = null)
    {
        if (GetOwned(sessionId, ownerId) is null || !_sessions.TryGetValue(sessionId, out var entry))
            throw new KeyNotFoundException("Чат не найден");

        var newModel = string.IsNullOrWhiteSpace(model) ? null : model.Trim();

        var target = _llmProviders.ResolveByModel(newModel);
        // Десктопный чат стороннему вендору не отдаём (ADR-008): в его транскрипте оседают
        // кадры рабочего стола (desktop_screen пишет base64 в .jsonl), а миграция — это копия
        // файла в чужой профиль плюс --resume с чужим ANTHROPIC_BASE_URL. Автоматический
        // фолбэк то же правило держит обрезкой цепочки (TrimChainForDesktop); здесь — второй
        // шлюз, на единственной точке РУЧНОЙ смены провайдера: и настройки чата (UpdateAsync),
        // и кнопка «Продолжить на …» карточки provider_limit. Ротация внутри пула подписок
        // Claude (target is null) правилом не затронута — эндпоинт и владелец данных те же.
        if (entry.Info.DesktopChat && target is not null)
            throw new InvalidOperationException(
                "Десктопный чат нельзя перевести на стороннего провайдера: в его истории есть "
                + "кадры рабочего стола. Останьтесь на Claude или заведите обычный чат");
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
            // Цель: сторонний провайдер — его ключ; родной Claude — доступный аккаунт пула.
            if (target is null)
            {
                // null от ResolveByModel = родной Claude (подписка, без env-оверрайдов). Но тот же
                // null получается и для неизвестной модели: её id не нашёлся ни в Models, ни по
                // префиксу ни одного провайдера — фронт и реестр рассинхронизированы (каталог
                // /api/models отдал id, которого текущий LlmProviderRegistry не знает). Молчаливый
                // фолбэк на Pick давал ложное «Чат уже на этом провайдере» (Pick выбирал тот же
                // аккаунт пула, что текущий), поэтому неизвестную модель называем вслух.
                // Два случая различает ФОРМА id (IsNativeClaudeModel), а не сравнение с
                // Info.Model: у чата на «По умолчанию» она null, и по ней opus при живом пуле
                // выглядел как неизвестная модель — PATCH настроек падал с ложным «модель не
                // найдена» (дефект 3-й итерации). Проверка безусловная: форма id от наличия
                // пула подписок не зависит, а на коробке БЕЗ ClaudeSubscriptions мусорный id
                // иначе доезжал бы до Pick → PrimaryKey → «уже на этом провайдере», молча
                // ложился в Info.Model и валил каждый следующий ход.
                // Родная модель идёт дальше в Pick: тот вернёт либо текущий аккаунт (переезд
                // вырождается в «просто закрепить модель» — ProviderUnchangedException ниже),
                // либо здоровый другой — это штатная ротация пула кнопкой «Продолжить на …».
                if (!LlmProviderRegistry.IsNativeClaudeModel(newModel))
                    throw new InvalidOperationException(
                        $"Модель «{newModel}» не найдена среди настроенных провайдеров");
                targetKey = _subscriptionPool.Pick(newModel);
            }
            else
            {
                targetKey = target.Key;
            }
        }

        if (string.Equals(targetKey, currentKey, StringComparison.OrdinalIgnoreCase))
            throw new ProviderUnchangedException();

        // Разделитель «Продолжено на …» ставим только по факту переноса: в чате, где
        // переносить было нечего, продолжать тоже нечего — карточка врала бы.
        var transcriptMoved = false;
        if (entry.Info.ClaudeSessionId is not null)
        {
            var hostCwd = TryResolveCwd(entry.Info)
                ?? throw new InvalidOperationException("Не удалось определить рабочую папку чата");
            // У container-пользователя и корни (песочные профили), и cwd (контейнерный путь)
            // другие. Исключение ToRuntime (путь вне монтирований) намеренно не глушим:
            // операция явная, пользователь должен увидеть причину отказа (400)
            var cwd = CwdForOwner(ownerId, hostCwd);
            var srcRoot = ConfigRootFor(ownerId, currentKey);
            // Различаем «переносить нечего» и «перенос сорвался» — TryMigrate отдаёт false в
            // обоих случаях, а последствия разные. Транскрипта может не быть вовсе:
            // ClaudeSessionId выставляется на ЛЮБОМ первом ходе (включая служебный kickoff
            // онбординга), а файл к этому моменту мог не появиться, чат мог быть создан из
            // чужого resumeSessionId, либо его убрала плановая уборка CLI. Терять нечего —
            // меняем провайдера и пишем строку в лог. Сорвавшийся же перенос НАЙДЕННОГО файла
            // остаётся жёстким отказом: TryMigrate возвращает false и по таймауту копирования
            // живого файла (CopyFileShared, дедлайн 8 с), а это молчаливая потеря контекста.
            if (TranscriptMigrator.FindTranscript(srcRoot, cwd, entry.Info.ClaudeSessionId) is null)
                Console.Error.WriteLine(
                    $"[SessionManager] Чат {sessionId}: транскрипт {entry.Info.ClaudeSessionId} "
                    + $"не найден в {srcRoot} — переносить нечего, меняем провайдера");
            else if (!TranscriptMigrator.TryMigrate(srcRoot, ConfigRootFor(ownerId, targetKey),
                         cwd, entry.Info.ClaudeSessionId, out var error))
                throw new InvalidOperationException($"Не удалось перенести транскрипт: {error}");
            else
                transcriptMoved = true;
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

        // Явно выбранный аккаунт пула — подпись «на подписке», а не безликое «на AI».
        // Label = null — сообщение уходит без разделителя: висящую карточку «Продолжить на …»
        // оно всё равно гасит (chatReducer), а ленту не засоряет. Разделителя нет в двух
        // случаях: переносить было нечего (продолжать нечего — карточка врала бы) и автовыбор
        // ДРУГОГО аккаунта того же пула Claude (ротация подписок по договорённости тихая: тип
        // поставщика не менялся, а «Продолжено на AI» читается как уход к другому вендору).
        // Явный тык в карточку подписки (pickedSub) подпись сохраняет — это выбор человека.
        string? switchLabel = null;
        if (transcriptMoved)
        {
            if (pickedSub is not null)
                switchLabel = "Продолжено на подписке "
                    + $"«{(string.IsNullOrWhiteSpace(pickedSub.DisplayName) ? pickedSub.Key : pickedSub.DisplayName)}»";
            else if (target is not null)
                switchLabel = "Продолжено на "
                    + (string.IsNullOrWhiteSpace(target.DisplayName) ? target.Key : target.DisplayName);
            // Возврат СО стороннего провайдера на родной Claude — смена типа поставщика, подпись нужна
            else if (_llmProviders.GetByKey(currentKey) is not null)
                switchLabel = "Продолжено на AI";
        }
        await BroadcastAsync(sessionId, new ProviderSwitchedMessage(targetKey, newModel, switchLabel));
        Console.WriteLine($"[SessionManager] Чат {sessionId} мигрирован: {currentKey} → {targetKey} "
            + $"({newModel ?? "по умолчанию"}, транскрипт: {(transcriptMoved ? "перенесён" : "нечего переносить")})");
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

    // Кэш сводки карточки архива (ChatDigestService, место chat-digest): текст и момент
    // сборки — при UpdatedAt > ArchiveSummaryAt сводка не актуальна (см. Session).
    // UpdatedAt намеренно не двигается: сборка сводки — не активность чата, иначе она
    // сама снимала бы архив и поднимала чат в списке. null — сбросить кэш.
    public void SetArchiveSummary(string sessionId, string? summary)
    {
        if (!_sessions.TryGetValue(sessionId, out var entry)) return;
        entry.Info.ArchiveSummary = summary;
        entry.Info.ArchiveSummaryAt = summary is null ? null : DateTime.UtcNow;
        SaveSessions();
    }

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
    public void MarkFilesCommitted(
        IReadOnlyList<(string SessionId, IReadOnlyCollection<string> Paths, IReadOnlyCollection<string> RawPaths)> batch)
    {
        // Общий лок с UnmarkFileCommitted: пометка (поток статуса) и снятие (петля сообщений
        // хода) делают read-modify-write одного списка — без лока коммит ровно в момент
        // правки того же файла терял бы обновление. _saveLock (System.Threading.Lock)
        // реентерабелен (подсчёт рекурсии): вложенный SaveSessions берёт его повторно без клинча.
        lock (_saveLock)
        {
            var changed = false;
            foreach (var (sessionId, paths, rawPaths) in batch)
            {
                if (!_sessions.TryGetValue(sessionId, out var entry)) continue;
                var current = new HashSet<string>(entry.Info.CommittedFilePaths, StringComparer.Ordinal);
                var next = new HashSet<string>(current, StringComparer.Ordinal);
                next.UnionWith(paths);
                next.IntersectWith(rawPaths is HashSet<string> h ? h : [.. rawPaths]);
                if (next.SetEquals(current)) continue;
                entry.Info.CommittedFilePaths = [.. next.Order()];
                changed = true;
            }
            if (changed) SaveSessions();
        }
    }

    // Вернуть путь в учёт атрибуции: чат снова правит файл после фиксации. Сидит на
    // ГОРЯЧЕМ пути (каждое write-сообщение хода) — ранний выход без записи, когда
    // пометки нет. path — уже нормализованный (SessionChangedPaths.Normalize).
    // UpdatedAt не двигаем (см. MarkFilesCommitted).
    public void UnmarkFileCommitted(string sessionId, string path)
    {
        if (!_sessions.TryGetValue(sessionId, out var entry)) return;
        // Быстрая проверка ДО лока — горячий путь (каждое write-сообщение хода) не должен
        // толкаться с пометкой коммита; под локом перепроверяется (см. MarkFilesCommitted)
        if (!entry.Info.CommittedFilePaths.Contains(path, StringComparer.Ordinal)) return;
        lock (_saveLock)
        {
            if (!entry.Info.CommittedFilePaths.Contains(path, StringComparer.Ordinal)) return;
            entry.Info.CommittedFilePaths = [.. entry.Info.CommittedFilePaths.Where(p => p != path)];
            SaveSessions();
        }
    }

    // Ветка tool_use снятия пометки: file_changed НЕ хватает — событие не приходит, когда
    // содержимое не изменилось, путь отсечён TurnFileWatcher.ShouldIgnore или файл удалён;
    // такой файл после коммита выпал бы из атрибуции навсегда. Гейт worktree-хода зеркалит
    // SessionChangedPaths.Extract (TurnFileWatcher отдаёт пути относительно СВОЕГО корня —
    // правка в worktree не должна воскрешать атрибуцию одноимённого файла в корне).
    // Известное ограничение (принято): снятие идёт по ЗАЯВКЕ tool_use, а не по успешному
    // tool_result — отклонённый permission или упавший Edit вернут атрибуцию файлу,
    // которого чат фактически не менял (то же множество источников, что у Extract).
    private void TryUnmarkCommittedOnToolUse(string sessionId, SessionEntry? entry, string toolName, object? input)
    {
        if (entry is null || entry.TurnInWorktree) return;
        if (!SessionChangedPaths.IsWriteTool(toolName)) return;
        // Ранний выход до резолва проекта: у чата без пометок здесь горячий no-op
        if (entry.Info.CommittedFilePaths.Count == 0) return;
        var root = entry.Info.ProjectId is { } pid ? _projects.GetById(pid)?.RootPath : null;
        if (root is null) return;
        if (SessionChangedPaths.NormalizedToolPath(input, root) is { } rel)
            UnmarkFileCommitted(sessionId, rel);
    }

    public async Task<Session> CreateAsync(string projectId, ClaudeMode mode,
        string? resumeSessionId = null, string? name = null, string? model = null, string? agentName = null,
        string? effort = null, string? personaId = null, bool taskExecution = false, string? taskId = null,
        string? onboardingKind = null, bool desktopChat = false)
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
            // Тип чата «Десктопный» (ADR-008): задаётся при СОЗДАНИИ и дальше не меняется —
            // состав грани фиксируется на момент запуска CLI
            DesktopChat = desktopChat,
            // Онбординг-сессия: задаётся ДО старта — BuildPersonaLayer читает поле при сборке слоя
            OnboardingKind = onboardingKind,
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

        // Персона без своей модели идёт своим уровнем, без уровня — назначением места «чат с персоной».
        // Дефолт места (Strong) передаётся в резолв, чтобы ячейка персоны без явного уровня сработала.
        var personaModel = ResolveDefaultModel(Llm.LocalActionCatalog.ChatPersona,
            _assignments.PersonaModel(persona, ownerId,
                Llm.LocalActionCatalog.DefaultTierOf(Llm.LocalActionCatalog.ChatPersona)),
            resumeSessionId, ownerId);

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
        // (дефолт места передаётся в резолв, чтобы ячейка ведущей без уровня сработала).
        var leaderModel = ResolveDefaultModel(Llm.LocalActionCatalog.ChatPersona,
            _assignments.PersonaModel(leader, ownerId,
                Llm.LocalActionCatalog.DefaultTierOf(Llm.LocalActionCatalog.ChatPersona)),
            resumeSessionId: null, ownerId);

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

    // Живая персона чата для цепочки фолбэка (ClaudeSession.EffectiveTurnChain →
    // ResolveChain с матрицами персоны): перечитывается на каждый ход — правка матриц
    // персоны/специальности применяется со следующего хода без пересоздания адаптера.
    // null — у чата нет персоны (или владельца).
    private Func<Persona?> BuildPersonaProvider(Session session, string? ownerId) =>
        () => session.PersonaId is { } pid && ownerId is not null ? _personas.Get(pid, ownerId) : null;

    // Персона-слой сессии (промпт характера + контекст памяти + auto-recall + сама персона
    // для гейтов возможностей). Строится одинаково при первом старте и при восстановлении процесса.
    // Промпт — замыкание: адаптер зовёт его на каждый ход, поэтому правки персоны
    // (контракт/характер), смена модели сессии и флаг PersonaSwitched применяются сразу.
    private (Func<string?>? Prompt, MemoryMcpContext? Memory, Func<string, Task<RecallBlock?>>? Recall, Persona? Persona)
        BuildPersonaLayer(Session session, string? ownerId)
    {
        // Онбординг пользователя (знакомство): персоны у сессии ещё
        // нет — слой ведёт системный «Мастер настройки» тем же каналом PersonaPromptProvider.
        // После назначения дефолта персона садится в эту же сессию (SetPersona → AdapterStale),
        // слой пересобирается и становится обычным персонным.
        if (session.OnboardingKind == OnboardingKinds.User && session.PersonaId is null)
        {
            if (ownerId is null) return (null, null, null, null);
            return (() =>
            {
                var owner = _users.GetById(ownerId);
                // Резолв заготовки: id и имя подставляем ТОЛЬКО когда AssistantPersonaId резолвится
                // в ЖИВУЮ персону. Мёртвый id (заготовку удалили) → промпт деградирует к «создай
                // персону», и серверный предохранитель в этом состоянии create разрешает — план 2.9.
                var assistantId = owner?.AssistantPersonaId;
                if (assistantId is { } aid && _personas.Get(aid, ownerId) is { } draft)
                    return Prompts.OnboardingPrompts.UserMaster(owner?.DisplayName ?? owner?.Username, draft.Id, draft.Name);
                return Prompts.OnboardingPrompts.UserMaster(owner?.DisplayName ?? owner?.Username);
            }, null, null, null);
        }

        if (session.PersonaId is null || ownerId is null) return (null, null, null, null);
        var persona = _personas.Get(session.PersonaId, ownerId);
        if (persona is null) return (null, null, null, null);
        Func<string?> prompt = () =>
        {
            var p = session.PersonaId is { } pid ? _personas.Get(pid, ownerId) : null;
            if (p is null) return null;
            var built = _promptBuilder.Build(p, session.Model, session.PersonaSwitched,
                greeted: !string.IsNullOrWhiteSpace(p.Greeting),
                teamMechanicsBlock: BuildTeamMechanicsBlock(session, p),
                // Стиль digest — только там, где секция формата тоже поедет (ClaudeSession,
                // гейт «есть живой слушатель»). Иначе персона получила бы «пиши блок <voice>
                // в конце» без самого формата и без того, кому это слушать: маркер засорил бы
                // транскрипт исполнителя задачи ровно тем, что гейт и должен предотвращать.
                // Делегированный ход (глубина агента) виден только внутри ClaudeSession —
                // здесь отсекаем два признака из трёх, третий добирает сама секция
                voiceMode: session.VoiceMode,
                voiceStyle: session.TaskExecution || session.AutomationRuleId is not null
                    ? VoiceStyles.Talk
                    : session.VoiceStyle);
            // Групповой чат: надстройка со списком участников и правилом «говори только за себя»
            if (session.Participants is { Count: > 1 } memberIds)
            {
                var members = memberIds.Select(id => _personas.Get(id, ownerId))
                    .OfType<Persona>().ToList();
                if (members.Count > 1) built += "\n\n" + BuildGroupChatHint(p, members);
            }
            // Онбординг проекта: надстройка наставника поверх слоя личной дефолт-персоны.
            // Живёт, пока нет руководителя ИЛИ пока каркас не развёрнут (PresetKey == "pending"):
            // назначение руководителя в первом же ходе не должно гасить остаток сценария
            // (знакомство v2, п.5) — иначе шаги каркаса и команды исчезали бы до их прохождения.
            // Исчезает сама после применения/отказа каркаса — промпт пересобирается каждый ход.
            if (session.OnboardingKind == OnboardingKinds.Project && session.ProjectId is { } prjId
                && _projects.GetById(prjId) is { } prj
                && Prompts.OnboardingPrompts.ProjectOverlayActive(prj))
                built += "\n\n" + Prompts.OnboardingPrompts.ProjectOnboardingOverlay(
                    prj.Name, prj.PresetKey, PersonasEnabled(ownerId, session, persona));
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
                BuildPersonaRecallProvider(ownerId, session, persona.Id), persona);
        }
        return (prompt, null, null, persona);
    }

    // Блок «Командные механики» для руководителя проекта (мост в механики): добавляется,
    // только когда персона чата — дефолт-персона его проекта (Project.DefaultPersonaId).
    // Состав — по установленным скиллам
    // (TeamMechanicsPromptCatalog); без SkillsService (тесты) остаются механики без скилла.
    // Только промпт: состав MCP-инструментов не меняется, зависимость от хода отсутствует.
    private string? BuildTeamMechanicsBlock(Session session, Persona persona)
    {
        if (session.ProjectId is not { } projectId) return null;
        var project = _projects.GetById(projectId);
        if (project is null || project.DefaultPersonaId != persona.Id) return null;
        return Prompts.TeamMechanicsPromptCatalog.BuildPromptBlock(InstalledSkillNames());
    }

    // Имена установленных скиллов (глобальные + workflow-скрипты + плагинные) для фильтра
    // каталога механик. Источник обязан совпадать с тем, по которому доступность механик
    // считает фронт (GET /api/skills = скиллы + workflows + плагины): без workflow-скриптов
    // руководитель проекта НИКОГДА не предлагал четыре механики на них — панель экспертов,
    // командный спринт, ревью-консилиум и красную команду, — хотя в раскрывашке композера
    // они доступны и запускаются руками.
    // Ошибки чтения — пустой набор (блок сузится до механик без скилла, ход не падает).
    private IReadOnlySet<string> InstalledSkillNames()
    {
        if (_skills is null) return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            return _skills.GetGlobalSkills().Select(s => s.Name)
                .Concat(_skills.GetGlobalWorkflows().Select(s => s.Name))
                .Concat(_skills.GetPluginSkills().Select(s => s.Name))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }
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

    // Секции промпта специальности персоны (план «Секции промптов» этап 3, флаг
    // specialty-prompt-sections): сценарные инструкции «когда и как» по роли (история, граф
    // кода, процессы, правила роли) — резолвер EffectivePromptSections (SpecialtySettingsStore,
    // этап 2). Текст хода игнорируется (секции статичны для owner+специальности). null —
    // провайдер не injecting (тесты), нет владельца/персоны, специальность none или групповой
    // чат (несколько собеседников — контракт плана: секции только у персонных сессий).
    // Гейт по флагу — ВНУТРИ, на каждый ход (переключение действует сразу, как у dossier).
    private Func<string?, Task<string?>>? BuildPromptSectionsProvider(
        string? ownerId, Session session, Persona? persona)
    {
        if (ownerId is null || _specialtySettings is null || persona is null) return null;
        if (persona.Specialty == PersonaSpecialty.None) return null;
        if (session.Participants is { Count: > 1 }) return null;
        return _ =>
        {
            if (!_flags.IsEnabled(ownerId, FeatureFlagKeys.SpecialtyPromptSections))
                return Task.FromResult<string?>(null);
            var sections = _specialtySettings.EffectivePromptSections(ownerId, persona.Specialty);
            var text = sections.Count == 0 ? null : string.Join("\n\n", sections.Select(s => s.Text));
            return Task.FromResult(text);
        };
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
        if (fileScopes is { Count: > 0 } || chatScopes is { Count: > 0 })
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

    // Серверы личного реестра владельца в ход. Решение принимается ТОЛЬКО по
    // owner/project/persona — состав tools/list не смеет зависеть от свойств хода
    // (иначе сигнатура запуска мерцает и процесс CLI перезапускается со всеми MCP).
    // Провайдер, а не готовое значение: правка реестра применяется со следующего хода
    // без пересоздания адаптера. Секреты разворачиваются здесь и живут только во
    // временном конфиге хода.
    private Func<ExternalMcpContext?>? BuildExternalMcpProvider(string? ownerId, string? projectId, Persona? persona)
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
                foreach (var record in registry.GetByOwner(ownerId))
                {
                    if (!record.Enabled) continue;
                    // allow-модель: сервер едет, если включён «здесь» (проект этого чата
                    // по McpServersOn либо, вне проектов, AllowOutsideProjects записи) ИЛИ
                    // выдан персоне (McpServerGranted). Чистое условие — McpDelivery.ShouldDeliver,
                    // его OR-матрицу гоняем юнитами без SessionManager. Все входы — свойства
                    // owner/project/persona/записи, ни один не смотрит на ход.
                    var granted = _bindings.McpServerGranted(persona, "mcp:" + record.Key);
                    if (!Mcp.McpDelivery.ShouldDeliver(record, onInProject, isProjectChat, granted, readOnly))
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
                    var env = ResolveValues(fresh.Env);
                    var headers = ResolveValues(fresh.Headers);
                    if (!stdio && !ApplyAuthHeaders(fresh, headers)) continue;
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

        Dictionary<string, string> ResolveValues(Dictionary<string, string>? map)
        {
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var (name, value) in map ?? [])
                result[name] = _mcpSecrets!.Resolve(ownerId, value) ?? "";
            return result;
        }

        // Заголовок авторизации http/sse-сервера (общая точка с пробой — Mcp.McpAuthHeaders).
        // Потерянный секрет (запись ссылается в пустоту) — не повод отдавать серверу заведомо
        // анонимный запрос: пропускаем сервер с предупреждением, иначе инструменты молча
        // отвечали бы 401.
        bool ApplyAuthHeaders(McpServerRecord record, Dictionary<string, string> headers)
        {
            if (Mcp.McpAuthHeaders.TryApply(record, headers, r => _mcpSecrets!.Resolve(ownerId, r))) return true;
            _log.LogWarning("MCP-сервер «{Key}» снят с хода: не найдено значение авторизации", record.Key);
            return false;
        }
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


    // Назначить/сменить собеседника чату (единый селектор): персону (personaId) ИЛИ
    // стандартного .md-агента Claude (agentName) — взаимоисключающе. Оба пустые = снять.
    // Разрешено и ПО ХОДУ разговора: персона-слой строится на каждый ход, транскрипт
    // продолжается через --resume с новым системным слоем. Модель/усилие подтягиваются
    // из персоны; у начатой сессии — только при том же провайдере: смена собеседника
    // транскрипт не перевозит (в отличие от явной смены модели), а уводить ход в чужой
    // профиль CLI молча нельзя — модель персоны просто не применяется.
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

    // Запомнить персону, созданную в ходе онбординг-сессии (через personas_create из чата
    // мастера/наставника). Финализация (FinalizeOnboardingAsync) досевает профиль дефолта
    // только ей: выбранная существующая персона прав не получает.
    public void SetOnboardingCreatedPersona(string sessionId, string ownerId, string personaId)
    {
        if (!_sessions.TryGetValue(sessionId, out var entry)) return;
        if (ResolveOwnerId(entry.Info) != ownerId) return;
        if (entry.Info.OnboardingCreatedPersonaId == personaId) return;
        entry.Info.OnboardingCreatedPersonaId = personaId;
        SaveSessions();
    }

    // Пометить онбординг-сессию финализированной (PersonasController.FinalizeOnboardingAsync):
    // повторный make-default из живой сессии после этого — no-op без второго события в ленте.
    public void SetOnboardingFinalized(string sessionId, string ownerId)
    {
        if (!_sessions.TryGetValue(sessionId, out var entry)) return;
        if (ResolveOwnerId(entry.Info) != ownerId) return;
        if (entry.Info.OnboardingFinalized) return;
        entry.Info.OnboardingFinalized = true;
        SaveSessions();
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
            var personaModel = _assignments.PersonaModel(persona, ResolveOwnerId(entry.Info),
                Llm.LocalActionCatalog.DefaultTierOf(Llm.LocalActionCatalog.ChatPersona));
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

        var widgetsMcp = BuildWidgetsContext(ownerId, persona.Persona);
        var memoryMcp = persona.Memory ?? BuildTeamMemoryContext(ownerId, session.ProjectId);
        var tasksMcp = TasksMcpEnabled(ownerId, session, persona.Persona)
            ? BuildTasksContext(ownerId, session.ProjectId, persona.Persona) : null;
        var notesMcp = _bindings.EffectiveToolEnabled(ownerId, persona.Persona, "notes")
            ? BuildNotesContext(ownerId, session.ProjectId, persona.Persona) : null;
        var personasMcp = BuildPersonasContext(ownerId, session.ProjectId, session, persona.Persona);
        var notificationsMcp = BuildNotificationsContext(ownerId, session.PersonaId, persona.Persona);
        var codeGraphMcp = BuildCodeGraphContext(ownerId, session.ProjectId, session.Id, rootPath, persona.Persona);
        var difyMcp = BuildDifyContext(ownerId);
        var adapter = _adapters.Create(session, new LlmSessionContext(rootPath,
            msg => OnMessageAsync(session.Id, accumulator, msg, runId),
            rawSystemPrompt, permissionRules,
            TasksMcp: tasksMcp,
            NotesMcp: notesMcp,
            RecallProvider: BuildRecallProvider(ownerId),
            PersonaPromptProvider: persona.Prompt,
            PersonaProvider: BuildPersonaProvider(session, ownerId),
            MemoryMcp: memoryMcp,
            PersonaRecallProvider: persona.Recall,
            ExtraDisallowedTools: BuildExtraDisallowed(ownerId, persona.Persona, session),
            PersonasMcp: personasMcp,
            NotificationsMcp: notificationsMcp,
            WorkspaceMcp: workspace,
            BindingsProvider: BuildBindingsProvider(ownerId, session.PersonaId, workspace?.Sections),
            CodeGraphProvider: BuildCodeGraphProvider(ownerId, persona.Persona, rootPath, projectRoot),
            PromptSectionsProvider: BuildPromptSectionsProvider(ownerId, session, persona.Persona),
            PersonaAgentsProvider: BuildPersonaAgentsProvider(ownerId, session, persona.Persona),
            Launcher: _launchers.ForOwner(ownerId),
            ModulesMcp: BuildModulesContext(ownerId),
            WidgetsMcp: widgetsMcp,
            CodeGraphMcp: codeGraphMcp,
            DifyMcp: difyMcp,
            DesktopMcp: BuildDesktopContext(ownerId, session, persona.Persona),
            BrowserEnabled: BrowserEnabled(ownerId, persona.Persona),
            PromptSnapshotSink: PromptSinkFor(session.Id),
            PromptSnapshotToolsSink: PromptToolsSinkFor(session.Id),
            SubagentRunSink: SubagentRunSinkFor(session.Id),
            CliConfigRoot: ConfigRootFor(ownerId, session.Provider),
            ExternalMcpProvider: BuildExternalMcpProvider(ownerId, session.ProjectId, persona.Persona),
            DossierTrailerHint: BuildDossierTrailerHint(ownerId, session),
            PersistSessions: SaveSessions,
            EnqueueBypass: BuildEnqueueBypass(session.Id),
            OrchestrationDone: BuildOrchestrationDone(session.Id),
            HttpMcpActive: HttpMcpActive(widgetsMcp, memoryMcp, tasksMcp, notesMcp, personasMcp,
                workspace, notificationsMcp, codeGraphMcp, difyMcp),
            HttpMcpEnabledProvider: HttpMcpEnabled));
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
    public async Task<SendUserOutcome> SendMessageAsync(string sessionId, string text, IReadOnlyList<string> attachedPaths, string? mode = null, bool systemDirective = false, bool auto = false, string? senderPersonaId = null, bool suppressTasksExecute = false, string? senderOrigin = null, string? staffNote = null, DeliveryCause cause = DeliveryCause.Unknown)
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
            await ResumeTeamFromDecisionOnUserInput(sessionId, entry);

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
                    if (entry.Pending.Count >= MaxPendingPerSession)
                        throw new InvalidOperationException(
                            $"В очереди чата уже {MaxPendingPerSession} сообщений — дождитесь, пока она разберётся");
                }

                // Пользователь возобновил разговор — заморозка «Стоп» снимается ДО постановки,
                // иначе форсаж dispatchNow и разбор по концу хода упёрлись бы в QueueFrozen
                entry.QueueFrozen = false;
                var enqueued = await EnqueuePendingAsync(sessionId, entry, text, senderPersonaId, senderOrigin,
                    agentDepth: 0, kind: PendingKind.User, attachedPaths: attachedPaths, mode: mode);
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
                    PreemptTurnForQueue(sessionId, entry, "user-message (preempt хода пользователя)");
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
            senderPersonaId, suppressTasksExecute, senderOrigin, staffNote: staffNote, cause: cause);
        return SendUserOutcome.Started;
    }

    // Непосредственный запуск хода в процесс (гейты очереди уже пройдены либо не требуются).
    // fromQueue — доставка пользовательского сообщения из очереди: клиент рисовал его
    // призраком, поэтому live-баллон бродкастим так же, как для сервер-инициированных отправок.
    private async Task SendDirectAsync(string sessionId, SessionEntry entry, string text,
        IReadOnlyList<string> attachedPaths, string? mode, bool systemDirective, bool auto,
        string? senderPersonaId, bool suppressTasksExecute, string? senderOrigin, bool fromQueue = false,
        string? staffNote = null, DeliveryCause cause = DeliveryCause.Unknown)
    {
        // ДИАГНОСТИКА повторных доставок (инцидент 2026-08-10): каждая доставка хода в
        // процесс проходит через эту точку. src различает источник — hub (пользователь
        // через SignalR), auto (серверный ход: цикл/автоматизация/доклад исполнителя),
        // fromQueue (доставка пользовательского сообщения из серверной очереди pending).
        // cause — атрибуция callsite внутри auto/fromQueue (drain/WorkLoop/обход байпаса/…):
        // pinpoint'ит источник повторных доставок, прежде неразличимых при пустом origin.
        // Дубли видны как повторные строки с одинаковым/похожим text — pinpoint'ят источник.
        var deliverySrc = fromQueue ? "fromQueue" : auto ? "auto" : "hub";
        var effectiveCause = cause != DeliveryCause.Unknown ? cause : !auto ? DeliveryCause.User : DeliveryCause.Unknown;
        _log.LogInformation("Доставка хода {Session}: src={Src} cause={Cause} origin={Origin} mode={Mode} text=\"{Text}\"",
            sessionId, deliverySrc, effectiveCause, senderOrigin ?? "-", mode ?? "-",
            (text.Length > 60 ? text[..60] : text).Replace('\n', ' '));

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

        // Авто-отправка (командный ход, автоматизация, задача) и доставка пользовательского
        // из очереди (fromQueue — клиент рисовал призрак, а не баллон): клиент не добавлял
        // её оптимистично — показываем сразу, до тяжёлого старта CLI-процесса. ТОЛЬКО в
        // session-группу: клиент открытого чата состоит и в user_/project_-группе, широкая
        // рассылка дублировала сообщение в ленте (см. комментарий у внутриходовых событий).
        if ((auto || fromQueue) && !systemDirective)
            await BroadcastAsync(sessionId,
                new UserMessageMessage(text, attachedPaths.Count > 0 ? attachedPaths : null, senderPersonaId, auto, senderOrigin, StaffNote: staffNote));

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

        entry.Accumulator?.OnUserMessage(text, attachedPaths, systemDirective: systemDirective, auto: auto, senderPersonaId: senderPersonaId, senderOrigin: senderOrigin, staffNote: staffNote);
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
        // Диспетчеризация: локальный голосовой ход идёт мимо CLI (fire-and-forget — как
        // CLI-ветка, где SendMessageAsync лишь ставит ход в процесс; ответ приходит
        // событиями через OnMessageAsync). Реплика уже в аккумуляторе (OnUserMessage выше).
        if (localVoice)
        {
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
            await entry.Process!.SendMessageAsync(BuildCliTurnText(entry, text), attachedPaths,
                suppressTasksExecute: suppressTasksExecute);
        }
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

        // Обрыв фонового агента (см. NoteTruncatedBgAgent): CLI объявил его прогон
        // завершённым и отдал координатору последнюю реплику как результат. Пометка едет
        // префиксом ближайшего хода — своего хода на неё не тратим, а координатор обязан
        // знать, что итог по обрывку подводить нельзя. Одноразовая: уехала — снята.
        if (entry.TruncatedBgNote is { } cutBgAgent)
        {
            entry.TruncatedBgNote = null;
            result = SubagentPrompts.TruncatedBgAgent(cutBgAgent) + "\n\n" + result;
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
    private List<Llm.OllamaClient.ChatMsg> BuildVoiceMessages(SessionEntry entry, TurnAccumulator acc)
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
                StoredUserMessage u => new Llm.OllamaClient.ChatMsg("user", TrimVoiceMessage(u.Text!)),
                StoredTextMessage t => new Llm.OllamaClient.ChatMsg("assistant", TrimVoiceMessage(t.Text!)),
                _ => null,
            })
            .Where(m => m is not null)
            .Cast<Llm.OllamaClient.ChatMsg>()
            .ToList();

        var result = new List<Llm.OllamaClient.ChatMsg>(history.Count + 1) { new("system", system) };
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
        await OnMessageAsync(sessionId, acc, new SessionStartedMessage(
            entry.Info.ClaudeSessionId ?? sessionId, IsResume: false,
            Model: _router!.LocalModel, Mode: entry.Info.Mode.ToString().ToLowerInvariant()), runId);

        var spec = _router.ProfileSpec(Llm.CheapProfile.Text);
        var messages = BuildVoiceMessages(entry, acc);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        using var cts = new CancellationTokenSource();
        entry.LocalVoiceCts = cts;
        Llm.OllamaClient.ChatTurnResult? turn = null;
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
                    await OnMessageAsync(sessionId, acc, new TextDeltaMessage(chunk), runId);
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
            await OnMessageAsync(sessionId, acc, new ErrorMessage($"Локальная модель: {ex.Message}"), runId);
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
                await OnMessageAsync(sessionId, acc, new TextDeltaMessage(answer), runId);
            await OnMessageAsync(sessionId, acc, new ResultMessage(
                Subtype: "success", DurationMs: sw.ElapsedMilliseconds, NumTurns: 1,
                Usage: turn.Usage, TotalCostUsd: 0), runId);
        }
        else if (!cancelled)
        {
            // Пустой ответ/сбой HTTP — честная ошибка без фолбэка на CLI
            // (ErrorMessage исключения уже ушёл выше — второй не дублируем).
            if (turn is not null)
                await OnMessageAsync(sessionId, acc,
                    new ErrorMessage("Локальная модель не ответила"), runId);
        }

        await OnMessageAsync(sessionId, acc, new ExitedMessage(), runId);
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
                _log.LogInformation("Interrupt адаптера {Session}: callsite=agent-message (preempt хода агента, chats_send)", sessionId);
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
        entry.TurnInWorktree = false; // сообщение = новый ход в основном дереве (зеркало Extract)
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
            // ждать конца ВСЕГО цикла (пользовательское — наоборот, продолжается цикл как следующая
            // итерация, поэтому при живом цикле dispatchNow форсируется).
            dispatchNow = position == 1
                && !entry.QueueFrozen
                && (entry.Info.WorkLoop is null || kind == PendingKind.User)
                && entry.Info.Status is not (SessionStatus.Working or SessionStatus.Waiting)
                // Адаптер ведёт оркестрацию хода (фолбэк) — НЕ форсируем разбор очереди: ход,
                // вернувшийся из-под оркестрации через EnqueueBypass, должен дождаться её конца
                // (OrchestrationDone в finally адаптера), иначе drain↔requeue закрутит цикл на
                // рассинхроне «статус Active, но _turn ещё активен» (инцидент 2026-08-10 П3).
                && entry.Process?.OrchestrationActive != true;
        }
        await BroadcastPendingAsync(sessionId, entry);

        // ВНЕ лока: drain сам возьмёт PendingLock и достанет entry из реестра по sessionId.
        if (dispatchNow)
            _ = Task.Run(() => DrainNextPendingAsync(sessionId));

        // Dispatched говорит вызывающему, что доставка уже форсирована: прерывать ход после
        // такой постановки нельзя — это был бы собственный, только что запущенный ход
        return new SendAndWaitResult.Queued(position, Duplicate: false, Dispatched: dispatchNow);
    }

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
            suppressTasksExecute: suppressTasksExecute, senderOrigin: senderOrigin, staffNote: staffNote,
            cause: DeliveryCause.Direct);
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

    // Прервать идущий ход РАДИ очереди: убитый процесс result не пришлёт, поэтому доставку
    // разберёт exited того же прогона (DrainOnExitedRun). В отличие от «Стоп» очередь НЕ
    // морозится — прерывание здесь и есть требование доставить ждущее сообщение сейчас.
    private void PreemptTurnForQueue(string sessionId, SessionEntry entry, string callsite)
    {
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
        entry.SkipNextTeamTurnEnd = false;
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
        PreemptTurnForQueue(sessionId, entry, "pending-preempt (кнопка «прервать и отправить»)");
        return true;
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
                next = entry.Pending.FirstOrDefault(p => p.Kind == PendingKind.User);
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

    // Адаптер занятprevious-ходом: у него жив прогон CLI (HasLiveTurn — _turnLock держит,
    // транзрипт пишет) ИЛИ активна фолбэк-оркестрация (OrchestrationActive — между SettleAsync
    // и finally). В обоих случаях старт нового хода гоняется с предыдущим → сериализуем на
    // смерти. Чистая функция для прямого тестирования веток (Ф1, инцидент 2026-08-10).
    internal static bool Busy(ILlmSessionAdapter? p) =>
        p is { HasLiveTurn: true } or { OrchestrationActive: true };

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
        try
        {
            if (next.Kind == PendingKind.User)
                // fromQueue: true — клиент рисовал призраком, бродкастим live-баллон (как auto).
                // Режим уже применён при постановке, повторно не передаём, чтобы не сбросить
                // возможную смену режима после (SetMode) — Info.Mode источник правды.
                await SendDirectAsync(sessionId, entry, next.Text,
                    next.AttachedPaths ?? [], mode: next.Mode, systemDirective: false, auto: false,
                    senderPersonaId: next.SenderPersonaId, suppressTasksExecute: next.SuppressTasksExecute,
                    senderOrigin: next.SenderOrigin, fromQueue: true, cause: DeliveryCause.QueueUser);
            else
                await SendMessageAsync(sessionId, next.Text, [], auto: true,
                    senderPersonaId: next.SenderPersonaId, senderOrigin: next.SenderOrigin,
                    suppressTasksExecute: next.SuppressTasksExecute, staffNote: next.StaffNote,
                    cause: DeliveryCause.QueueAgent);
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

    // Есть ли в очереди пользовательское сообщение. При активном цикле именно оно продолжает
    // работу следующей итерацией — разбор очереди по концу хода опирается на эту проверку,
    // чтобы доставить такое сообщение (агентские при цикле по-прежнему ждут его конца).
    private static bool HasUserPending(SessionEntry entry)
    {
        lock (entry.PendingLock)
            return entry.Pending.Any(p => p.Kind == PendingKind.User);
    }

    private static bool HasPending(SessionEntry entry)
    {
        lock (entry.PendingLock)
            return entry.Pending.Count > 0;
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
        await _falPersistLock.WaitAsync();
        try
        {
            if (entry.Accumulator is not null) return;
            var key = entry.Info.ClaudeSessionId ?? entry.Info.Id.ToString();
            var existingHistory = await _history.LoadAsync(key);
            entry.Accumulator = new TurnAccumulator(existingHistory, entry.Info.ClaudeSessionId);
        }
        finally { _falPersistLock.Release(); }
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
            var tasksMcp = TasksMcpEnabled(entry.Info.OwnerId, entry.Info, persona.Persona)
                ? BuildTasksContext(entry.Info.OwnerId, null, persona.Persona) : null;
            var notesMcp = _bindings.EffectiveToolEnabled(entry.Info.OwnerId, persona.Persona, "notes")
                ? BuildNotesContext(entry.Info.OwnerId, null, persona.Persona) : null;
            var personasMcp = BuildPersonasContext(entry.Info.OwnerId, null, entry.Info, persona.Persona);
            var notificationsMcp = BuildNotificationsContext(entry.Info.OwnerId, entry.Info.PersonaId, persona.Persona);
            var difyMcp = BuildDifyContext(entry.Info.OwnerId);
            context = new LlmSessionContext(rootPath,
                msg => OnMessageAsync(sessionId, accumulator, msg, runId),
                RawSystemPrompt: null, PermissionRules: null,
                TasksMcp: tasksMcp,
                NotesMcp: notesMcp,
                RecallProvider: BuildRecallProvider(entry.Info.OwnerId),
                PersonaPromptProvider: persona.Prompt,
                PersonaProvider: BuildPersonaProvider(entry.Info, entry.Info.OwnerId),
                MemoryMcp: persona.Memory,
                PersonaRecallProvider: persona.Recall,
                ExtraDisallowedTools: BuildExtraDisallowed(entry.Info.OwnerId, persona.Persona, entry.Info),
                PersonasMcp: personasMcp,
                NotificationsMcp: notificationsMcp,
                WorkspaceMcp: workspace,
                BindingsProvider: BuildBindingsProvider(entry.Info.OwnerId, entry.Info.PersonaId, workspace?.Sections),
                CodeGraphProvider: BuildCodeGraphProvider(entry.Info.OwnerId, persona.Persona, rootPath),
                PromptSectionsProvider: BuildPromptSectionsProvider(entry.Info.OwnerId, entry.Info, persona.Persona),
                PersonaAgentsProvider: BuildPersonaAgentsProvider(entry.Info.OwnerId, entry.Info, persona.Persona),
                Launcher: _launchers.ForOwner(entry.Info.OwnerId),
                ModulesMcp: BuildModulesContext(entry.Info.OwnerId),
                WidgetsMcp: widgetsMcp,
                // Чат вне проекта — графа кода нет (он ключуется проектом)
                CodeGraphMcp: null,
                DifyMcp: difyMcp,
                BrowserEnabled: BrowserEnabled(entry.Info.OwnerId, persona.Persona),
                PromptSnapshotSink: PromptSinkFor(entry.Info.Id),
                PromptSnapshotToolsSink: PromptToolsSinkFor(entry.Info.Id),
                CliConfigRoot: ConfigRootFor(entry.Info.OwnerId, entry.Info.Provider),
                ExternalMcpProvider: BuildExternalMcpProvider(entry.Info.OwnerId, null, persona.Persona),
                PersistSessions: SaveSessions,
                EnqueueBypass: BuildEnqueueBypass(sessionId),
                OrchestrationDone: BuildOrchestrationDone(sessionId),
                SubagentRunSink: SubagentRunSinkFor(entry.Info.Id),
                HttpMcpActive: HttpMcpActive(widgetsMcp, persona.Memory, tasksMcp, notesMcp, personasMcp,
                    workspace, notificationsMcp, dify: difyMcp),
                HttpMcpEnabledProvider: HttpMcpEnabled);
                // Чат вне проекта: session.ProjectId==null → BuildDossierTrailerHint всегда null
        }
        else
        {
            var project = _projects.GetById(entry.Info.ProjectId)
                ?? throw new InvalidOperationException("Проект не найден");
            var persona = BuildPersonaLayer(entry.Info, project.OwnerId);
            var workspace = BuildWorkspaceContext(project.OwnerId, project.Id, entry.Info.Id, persona.Persona);
            var rootPath = EffectiveRoot(entry.Info, project.RootPath);
            var widgetsMcp = BuildWidgetsContext(project.OwnerId, persona.Persona);
            var memoryMcp = persona.Memory ?? BuildTeamMemoryContext(project.OwnerId, project.Id);
            var tasksMcp = TasksMcpEnabled(project.OwnerId, entry.Info, persona.Persona)
                ? BuildTasksContext(project.OwnerId, project.Id, persona.Persona) : null;
            var notesMcp = _bindings.EffectiveToolEnabled(project.OwnerId, persona.Persona, "notes")
                ? BuildNotesContext(project.OwnerId, project.Id, persona.Persona) : null;
            var personasMcp = BuildPersonasContext(project.OwnerId, project.Id, entry.Info, persona.Persona);
            var notificationsMcp = BuildNotificationsContext(project.OwnerId, entry.Info.PersonaId, persona.Persona);
            var codeGraphMcp = BuildCodeGraphContext(project.OwnerId, project.Id, entry.Info.Id, rootPath, persona.Persona);
            var difyMcp = BuildDifyContext(project.OwnerId);
            context = new LlmSessionContext(rootPath,
                msg => OnMessageAsync(sessionId, accumulator, msg, runId),
                project.SystemPrompt,
                () => _projects.GetById(entry.Info.ProjectId!)?.PermissionRules ?? (IReadOnlyList<PermissionRule>)Array.Empty<PermissionRule>(),
                TasksMcp: tasksMcp,
                NotesMcp: notesMcp,
                RecallProvider: BuildRecallProvider(project.OwnerId),
                PersonaPromptProvider: persona.Prompt,
                PersonaProvider: BuildPersonaProvider(entry.Info, project.OwnerId),
                MemoryMcp: memoryMcp,
                PersonaRecallProvider: persona.Recall,
                ExtraDisallowedTools: BuildExtraDisallowed(project.OwnerId, persona.Persona, entry.Info),
                PersonasMcp: personasMcp,
                NotificationsMcp: notificationsMcp,
                WorkspaceMcp: workspace,
                BindingsProvider: BuildBindingsProvider(project.OwnerId, entry.Info.PersonaId, workspace?.Sections),
                CodeGraphProvider: BuildCodeGraphProvider(project.OwnerId, persona.Persona, rootPath, project.RootPath),
                PromptSectionsProvider: BuildPromptSectionsProvider(project.OwnerId, entry.Info, persona.Persona),
                PersonaAgentsProvider: BuildPersonaAgentsProvider(project.OwnerId, entry.Info, persona.Persona),
                Launcher: _launchers.ForOwner(project.OwnerId),
                ModulesMcp: BuildModulesContext(project.OwnerId),
                WidgetsMcp: widgetsMcp,
                CodeGraphMcp: codeGraphMcp,
                DifyMcp: difyMcp,
                DesktopMcp: BuildDesktopContext(project.OwnerId, entry.Info, persona.Persona),
                BrowserEnabled: BrowserEnabled(project.OwnerId, persona.Persona),
                PromptSnapshotSink: PromptSinkFor(entry.Info.Id),
                PromptSnapshotToolsSink: PromptToolsSinkFor(entry.Info.Id),
                CliConfigRoot: ConfigRootFor(project.OwnerId, entry.Info.Provider),
                ExternalMcpProvider: BuildExternalMcpProvider(project.OwnerId, project.Id, persona.Persona),
                DossierTrailerHint: BuildDossierTrailerHint(project.OwnerId, entry.Info),
                PersistSessions: SaveSessions,
                EnqueueBypass: BuildEnqueueBypass(sessionId),
                OrchestrationDone: BuildOrchestrationDone(sessionId),
                SubagentRunSink: SubagentRunSinkFor(entry.Info.Id),
                HttpMcpActive: HttpMcpActive(widgetsMcp, memoryMcp, tasksMcp, notesMcp, personasMcp,
                    workspace, notificationsMcp, codeGraphMcp, difyMcp),
                HttpMcpEnabledProvider: HttpMcpEnabled);
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
                "по первому сообщению пользователя. " + Llm.TitleExtraction.JsonHintWithIcon + "\n\n" +
                (firstMessage.Length > 1500 ? firstMessage[..1500] : firstMessage);
            var raw = await _cheap.RunAsync(Llm.LocalActionCatalog.ChatTitle, prompt,
                ownerId: ownerId, jsonFormat: Llm.TitleExtraction.SchemaWithIcon);
            var line = Llm.TitleExtraction.Extract(raw);
            if (line is null || line.Length > 80) return;
            // Значок темы — имя lucide-компонента (PascalCase): имя остаётся чистым текстом,
            // иконку рисует фронт по icons[iconName]
            var iconName = Llm.TitleExtraction.ExtractIconName(raw);

            if (!_sessions.TryGetValue(sessionId, out var entry)) return;
            // Пользователь мог переименовать вручную, пока модель думала — тогда не трогаем
            if (entry.Info.Name != expectedTitle) return;
            entry.Info.Name = line;
            if (iconName is not null) entry.Info.Topic = iconName;
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
        // Архивный чат из архива не выводим: «Обновить название» доступен и в разделе
        // «Архив», а правка названия — не активность разговора (признак архива производный)
        if (!entry.Info.IsArchived) entry.Info.UpdatedAt = DateTime.UtcNow;
        SaveSessions();
        await BroadcastChatRenamedAsync(sessionId, entry.Info, line);
        return entry.Info;
    }

    // Подобрать значок-иконку ОДНОМУ чату по переписке. Имя не трогает и NameLocked не ставит —
    // в отличие от RetitleAsync (тот переименовывает). null — значок поставить не вышло
    // (нет переписки, модель не дала имя, чат исчез). Зовётся и пакетным прогоном.
    public async Task<Session?> SetChatIconAsync(string userId, string sessionId, CancellationToken ct)
    {
        var session = GetOwned(sessionId, userId);
        if (session is null) return null;
        if (_cheap is null) throw new InvalidOperationException("ИИ недоступен");

        // Транскрипт короткий: для темы разговора сути хватает, лишние токены ни к чему
        var history = await GetHistoryAsync(sessionId);
        var transcript = SessionSummaryService.BuildTranscript(history, 1500);
        if (string.IsNullOrWhiteSpace(transcript)) return null;

        var prompt = Llm.TitleExtraction.IconHint + "\n\n" + transcript;
        var raw = await _cheap.RunAsync(Llm.LocalActionCatalog.ChatTitle, prompt,
            ownerId: userId, jsonFormat: Llm.TitleExtraction.SchemaIcon, ct: ct);
        var iconName = Llm.TitleExtraction.ExtractIconName(raw);
        if (iconName is null)
        {
            // Диагностика: модель не дала валидное PascalCase-имя. Логируем сырой ответ,
            // чтобы понять — пустой {} (отступила), не-PascalCase (опечатка/пробел) или мусор.
            // Обрезаем: модели иногда шлют длинные рассуждения вслух
            _log.LogInformation("Значок чата {Session} («{Name}»): модель не дала имя. Ответ: {Raw}",
                sessionId, session.Name, raw is null ? "<null>" : (raw.Length > 300 ? raw[..300] + "…" : raw));
            return null;
        }

        if (!_sessions.TryGetValue(sessionId, out var entry)) return null;
        // Повторная проверка: пока модель думала, значок мог поставить авто-заголовок нового
        // чата — не перезаписываем то, что уже стоит
        if (!string.IsNullOrEmpty(entry.Info.Topic)) return entry.Info;
        entry.Info.Topic = iconName;
        entry.Info.UpdatedAt = DateTime.UtcNow;
        SaveSessions();
        // Имя не менялось — шлём его же: событие переносит и значок, отдельного не заводим
        await BroadcastChatRenamedAsync(sessionId, entry.Info, entry.Info.Name ?? "");
        return entry.Info;
    }

    // Результат пакетного прогона значков: сколько чатов получили иконку и сколько пропущено
    // (значок уже стоит, нет переписки, модель не дала имя). Идёт в тост палитры.
    public sealed record IconBatchResult(int Processed, int Skipped);

    // Подобрать значки чатам без него в рамках проекта (projectId) или, если проект не задан,
    // по всем чатам владельца. Действие AI-палитры «Проставить значки тем» в разделе проекта —
    // разовый проход. Чаты со значком отсеиваются ДО вызова модели (экономия вызовов), поэтому
    // в processed попадают только реально размеченные этим прогоном. Каждый оставшийся чат —
    // отдельный вызов модели: на десятках чатов это десятки секунд.
    public async Task<IconBatchResult> SetChatIconsAsync(string userId, CancellationToken ct, string? projectId = null)
    {
        var all = _sessions.Values
            .Where(e => ResolveOwnerId(e.Info) == userId && (projectId is null || e.Info.ProjectId == projectId))
            .Select(e => e.Info)
            .ToList();
        // Предфильтр: у кого значок уже есть — сразу в пропущенные, модель не зовём.
        // Архивные — туда же: у старых чатов Topic как раз пуст (значки появились позже),
        // и один клик «Проставить значки» выводил бы из архива ВСЕ старые чаты (UpdatedAt
        // двигается в SetChatIconAsync) и заказывал сотни вызовов модели
        var pending = all
            .Where(s => string.IsNullOrEmpty(s.Topic) && !s.IsArchived)
            .Select(s => s.Id)
            .ToList();
        var processed = 0;
        var skipped = all.Count - pending.Count;
        foreach (var id in pending)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var updated = await SetChatIconAsync(userId, id, ct);
                if (updated is not null) processed++; else skipped++;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { _log.LogDebug(ex, "Определение значка чата {Session}", id); skipped++; }
        }
        return new IconBatchResult(processed, skipped);
    }

    // Уведомить клиентов об авто-переименовании чата (адресация как у BroadcastChatDeletedAsync)
    private async Task BroadcastChatRenamedAsync(string sessionId, Session info, string name)
    {
        var msg = new ChatRenamedMessage(name, info.Topic) with { SessionId = sessionId };
        var tasks = new List<Task> { _hub.Clients.Group(sessionId).SendAsync("message", msg) };
        if (info.ProjectId is string pid)
            tasks.Add(_hub.Clients.Group("project_" + pid).SendAsync("message", msg));
        else if (info.OwnerId is string oid)
            tasks.Add(_hub.Clients.Group("user_" + oid).SendAsync("message", msg));
        await Task.WhenAll(tasks);
    }

    // Занят ли чат ходом ПРЯМО СЕЙЧАС. Status для этого не годится: у свежего чата он ещё
    // Starting, а между стартом хода и сменой статуса есть реальное окно. Занятость адаптера
    // спрашиваем каноническим Busy (живой прогон ИЛИ фолбэк-оркестрация): в паузе между
    // попытками цепочки прогона CLI нет, но ход идёт — и оркестратор в этот момент сам
    // копирует транскрипт, так что смена провайдера под ним разошлась бы с его restore.
    // Плюс признаки, которых Busy не знает: ход, принятый адаптером но ещё не поднявший
    // процесс, awaited-ход агента (TurnWaiter) и непустая серверная очередь — её сообщения
    // уедут в тот же чат, и провайдера менять под ними нельзя.
    private static bool HasTurnInFlight(SessionEntry entry)
    {
        if (Busy(entry.Process) || entry.Process is { HasQueuedTurn: true }) return true;
        if (entry.TurnWaiter is not null) return true;
        lock (entry.PendingLock) return entry.Pending.Count > 0;
    }

    // Публичная обёртка HasTurnInFlight для API-слоя (архивация чата, шаг 2 плана
    // «Архив чатов»): нужен тот же гейт, что у смены провайдера, — Status для этого не
    // годится (у свежего чата Starting, между стартом хода и сменой статуса есть окно).
    // false и для отсутствующего чата — гейт по чужому id не должен падать, caller
    // всё равно ответит 404 по GetOwned.
    public bool HasTurnInFlight(string sessionId) =>
        _sessions.TryGetValue(sessionId, out var entry) && HasTurnInFlight(entry);

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
    public async Task<Session?> UpdateAsync(string sessionId, string ownerId, string? name, string? model,
        string? effort, List<string>? tags = null)
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
            // и «переехавшая» сессия молча потеряла бы его. Прежний запрет «нельзя у начатой
            // сессии» держался ровно на этом, но с тех пор в продукте завелась миграция
            // транскрипта — та же, что кнопка «Продолжить на …» даёт живому разговору.
            // Значит вопрос не в том, начат ли чат, а в том, есть ли что переносить: этим
            // занимается MigrateProviderAsync.
            var newProvKey = _llmProviders.ResolveByModel(effectiveNew)?.Key;
            var curProvKey = _llmProviders.ResolveByModel(effectiveCur)?.Key;
            var migrated = false;
            if (newProvKey != curProvKey)
            {
                // Единственная причина отказа — ход прямо сейчас в полёте: смена провайдера
                // на лету увела бы ход в другой профиль CLI посреди прогона. Проверка стоит
                // ЗДЕСЬ, а не внутри MigrateProviderAsync: кнопка «Продолжить на …» аварийная
                // (приходит при исчерпании лимита, когда ход ещё формально жив), и такая
                // проверка сломала бы её.
                if (HasTurnInFlight(entry))
                    throw new InvalidOperationException(
                        "Идёт ответ ассистента — дождитесь его завершения, затем смените модель");

                try
                {
                    // effectiveNew = null — выбрали «По умолчанию», а назначение места модель не
                    // даёт: это переезд на родной Claude без закреплённой модели, а не «модель не
                    // указана». MigrateProviderAsync такой вызов понимает.
                    await MigrateProviderAsync(sessionId, ownerId, effectiveNew);
                    // Сброс на «По умолчанию» не должен закреплять модель: MigrateProviderAsync
                    // кладёт в Info.Model то, что ему передали (эффективную), и чат переставал бы
                    // следовать назначению места. Provider внутри уже выставлен.
                    if (newModel is null) entry.Info.Model = null;
                    migrated = true;
                }
                catch (ProviderUnchangedException)
                {
                    // Здесь провайдеров сравнивали по ЭФФЕКТИВНЫМ моделям, а миграция — по сырым
                    // (Info.Model ?? Info.Provider), и после переставленного назначения места эти
                    // две картины расходятся: чат с Model = null числится на claude (или на своём
                    // аккаунте пула), а по назначению эффективно уезжает на glm; выбор opus
                    // миграция видит как claude → claude. По сырым полям переносить нечего, так
                    // что это не повод валить 400 весь PATCH (вместе с name/effort/tags) —
                    // просто закрепляем модель обычной веткой ниже. Настоящая неизвестная
                    // модель сюда не попадает: у неё свой тип (InvalidOperationException) и
                    // честный 400 наружу.
                }
            }
            if (!migrated)
            {
                // В Info.Model кладём именно то, что выбрали: null = «следовать настройке»
                // (резолвится на каждом ходу, смена настройки подхватывается сама)
                entry.Info.Model = newModel;
                // Провайдер ВСЕГДА выводится из модели (комментарий LlmProviderRegistry: модель —
                // единственный источник правды, Provider не персистится как самостоятельное значение).
                // Инцидент 14.08.2026: пересчёт был только «в стороннего», и смена glm→opus[1m]
                // (пилюля модели в NewChatSetup до первого хода) оставляла пару (Claude-модель, ключ
                // glm) — CLI стартовал в профиле glm с моделью Anthropic → мгновенный 401.
                if (effectiveNew is not null && _llmProviders.ResolveByModel(effectiveNew) is { } newProv)
                    entry.Info.Provider = newProv.Key;
                // Родной Claude (ResolveByModel == null, в т.ч. пул подписок): ключ — из пула
                // (Pick сам отфильтрует исчерпанные/auth-dead, при пустом пуле — PrimaryKey),
                // модель меняем на лету у живого хода — применится к его последующим round-trip'ам.
                else if (effectiveNew is not null)
                {
                    // Чат уже сидит на аккаунте пула, который эту модель тянет — Provider НЕ
                    // трогаем: транскрипт лежит в профиле именно этого аккаунта, а Pick при
                    // равных тарифах тай-брейкает случайно (LeastLoaded, deterministic: false).
                    // Переезд без переноса .jsonl и без AdapterStale не виден сразу (живой
                    // адаптер держит старый корень), но после рестарта --resume идёт в чужой
                    // профиль — «No conversation found» и разговор с нуля. Pick нужен ровно
                    // там, где текущий ключ модель не тянет (пин Opus на плане без Opus) либо
                    // это возврат со стороннего провайдера/чат вообще без аккаунта пула.
                    var cur = entry.Info.Provider;
                    if (cur is null || !_subscriptionPool.All.Any(s => s.Key == cur)
                        || !_subscriptionPool.SupportsModel(cur, effectiveNew))
                        entry.Info.Provider = _subscriptionPool.Pick(effectiveNew);
                    entry.Process?.TrySetModelLive(effectiveNew);
                }
            }
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

        // Отметка времени — только когда что-то реально правили. PUT приходит и с одними
        // настройками (срок хранения, тумблер уведомлений — их применяет контроллер до этого
        // вызова, сюда все поля доезжают как null): безусловный UpdatedAt поднимал бы чат
        // в списке и метил непрочитанным просто за смену настройки.
        // Архивный чат из архива не выводим: правка имени/модели/тегов доступна и в разделе
        // «Архив», а признак архива производный — UpdatedAt снимает его сам.
        if (name is not null || model is not null || effort is not null || tags is not null)
            if (!entry.Info.IsArchived)
                entry.Info.UpdatedAt = DateTime.UtcNow;
        SaveSessions();
        return entry.Info;
    }

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
        FireAndForget(ApplyStatusAsync(sessionId, entry, SessionStatus.Working),
            $"смена статуса после permission ({sessionId})");
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

    public void Interrupt(string sessionId)
    {
        if (_sessions.TryGetValue(sessionId, out var entry))
        {
            // Живой локальный голосовой ход: отменяем и выходим ДО stuck-детекта и
            // заморозки очереди — exited придёт из самой ветки (RunLocalVoiceTurnAsync),
            // вернёт статус Active и разберёт очередь (drainOnDeadRun). Ход короткий
            // (1-3 с), composer_restore для него не нужен.
            if (entry.LocalVoiceCts is { } localCts)
            {
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
            entry.SkipNextTeamTurnEnd = false;
            if (stuck)
                ReviveStuckSession(sessionId, entry);
            else
            {
                _log.LogInformation("Interrupt адаптера {Session}: callsite=stop (Interrupt(sessionId), stuck={Stuck})", sessionId, stuck);
                entry.Process?.Interrupt();
            }
        }
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
            entry.SkipNextTeamTurnEnd = false;
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

    // Рабочая папка хода для поиска транскрипта: дерево чата, иначе корень проекта.
    // Точность тут не критична — TranscriptProbe при промахе по конвенции сканирует
    // все проекты зарегистрированных корней, cwd лишь сужает первый поиск.
    private string ResolveTurnCwd(Session info) =>
        info.WorktreePath
        ?? (info.ProjectId is { } pid ? _projects.GetById(pid)?.RootPath : null)
        ?? "";

    // Транскрипт resume-якоря чата: null — якоря нет или файл не найден (проверку
    // целостности тогда пропускаем). Обе точки RestartStuckTurnAsync — гейт
    // спасательной ветки и валидация перед resume — ищут файл одним путём.
    private string? FindResumeTranscript(SessionEntry entry) =>
        entry.Info.ClaudeSessionId is { } csid
            ? Llm.Claude.TranscriptProbe.FindMainTranscript(ResolveTurnCwd(entry.Info), csid)
            : null;

    // Дефолт лимитов цикла «до готово». Невалидное значение конфига (не число или ≤ 0)
    // сваливается в дефолт, а не молча отрубает цикл: MaxTaskExecutions=0 иначе даёт
    // Exhausted с первой попытки, MaxIterations=0 — немедленную остановку по лимиту.
    // internal — тестируется напрямую (SessionManagerTests).
    internal static int LoopLimitOrDefault(string? raw, int defaultValue = 20) =>
        int.TryParse(raw, out var v) && v > 0 ? v : defaultValue;

    // Включение/выключение цикла «до готово» (флаг work-loop). Включение сбрасывает
    // счётчик итераций и счётчик запусков задач; лимиты — из конфига Loop:MaxIterations
    // и Loop:MaxTaskExecutions (дефолт 20, при ≤0 — тоже дефолт, см. LoopLimitOrDefault).
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

        // ADR-008 («Два уровня, которые нельзя смешивать»): автопродолжение work-loop
        // в десктопном чате запрещено. Цикл ведёт агента по итерациям без человека, а вся
        // модель грани держится на том, что человек подтверждает каждое действие на
        // устройстве. Выключение не запрещаем — вернуть false всегда можно.
        if (enabled && entry.Info.DesktopChat)
            throw new SessionModeConflictException(
                "Цикл «до готово» недоступен в десктопном чате: агент не должен действовать на " +
                "вашем компьютере без подтверждения каждого действия.");

        var wasEnabled = entry.Info.WorkLoop is not null;
        // Присвоение WorkLoop и очистку буфера хода держим под одним локом: иначе обнуление
        // поля состязается с чтением в TryConsumeWorkLoopRun/RefundWorkLoopRun/ContinueWorkLoopAsync,
        // и потребитель может инкрементировать уже выключенный объект (мусорный Allowed).
        var newLoop = enabled
            ? new SessionWorkLoop
            {
                MaxIterations = LoopLimitOrDefault(_config["Loop:MaxIterations"]),
                MaxExecutions = LoopLimitOrDefault(_config["Loop:MaxTaskExecutions"]),
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
    // feedback — правка человека к текущему плану («Изменить план»): уходит планировщику
    // вместе с предыдущей версией (см. TeamPlanningService.BuildPlannerPrompt).
    public async Task<(TeamImplementPlan? Plan, string? Reason)> CreateTeamPlanAsync(
        string sessionId, string request, string? userId = null, CancellationToken ct = default,
        bool fromHuman = true, string? feedback = null)
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

        var candidates = _teamPlanning.ResolveCandidates(entry.Info, ownerId);
        if (candidates.Count == 0)
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
        // Планировщика резолвим тут же: фронту нужна его персона для карточки «Готовит план…»
        // в ленте. ResolvePlanner без побочных эффектов (тот же пул кандидатов, что уйдёт
        // в CreatePlanAsync ниже), так что лишнего запроса не добавляем
        var plannerPersonaId = _teamPlanning.ResolvePlanner(entry.Info, ownerId, candidates)?.Id;
        // Событие «планировщик запущен» сразу после резолва кандидатов: фронт рисует
        // «Штаб планирует…», а сам факт не путается с долгим молчанием (контракт для Киры).
        var empty = new TeamPlanningService.Result(null, TeamPlanningService.Failure.Failed, null, 0, 0, TimeSpan.Zero);
        await BroadcastTeamPlanningStartedAsync(sessionId, empty, plannerPersonaId);

        var planning = await _teamPlanning.CreatePlanAsync(entry.Info, ownerId, request, projectHint, ct, previous, feedback);
        if (planning.Plan is null)
        {
            // Событие «планировщик закончил» с отказом — фронт снимет спиннер и покажет
            // причину в плашке рядом с карточкой отказа (контракт для Киры, см. docs).
            await BroadcastTeamPlanningFinishedAsync(sessionId, planning, plannerPersonaId);
            return (null, PlannerFailureReason(planning.Failure));
        }

        // План построен — сохранённая вводная и правка отказа отработаны (повтор по кнопке
        // «Повторить планирование» после успеха не нужен)
        WithTeamState(sessionId, t => { t.LastPlanRequest = null; t.LastPlanFeedback = null; return true; });
        await BroadcastTeamPlanningFinishedAsync(sessionId, planning, plannerPersonaId);
        await PublishTeamPlanAsync(sessionId, entry, planning.Plan, fromHuman);
        return (planning.Plan, null);
    }

    // Текст причины отказа для карточки (по Failure): разные советы под разные корни —
    // обрыв по токенам не то же, что «уточните задачу», и таймаут не вина человека.
    private static string PlannerFailureReason(TeamPlanningService.Failure f) => f switch
    {
        TeamPlanningService.Failure.TimedOut => TeamPlanningService.PlannerTimeoutReason,
        TeamPlanningService.Failure.Truncated => TeamPlanningService.PlannerTruncatedReason,
        TeamPlanningService.Failure.InvalidJson => TeamPlanningService.PlannerInvalidJsonReason,
        _ => "Планировщик не смог построить план — уточните задачу",
    };

    // Событие жизненного цикла планировщика для ленты. Контракт (для Киры):
    //  • start=true  — планировщик запущен, фронт рисует «Штаб планирует…» и блокирует
    //                   кнопки повтора. Остальные поля диагностические (для логов).
    //  • start=false — планировщик закончил: Success=true → SubtaskCount/WaveCount/Route;
    //                   Success=false → Failure (тот же текст, что в карточке отказа).
    // Событие ТРАНЗИТНОЕ: в историю не пишется (карточка плана или карточка отказа уже там,
    // дублировать не надо), и при рестарте сервера не восстанавливается — спиннер просто
    // не показывается, карточка подтянется через /api/.../history.
    private Task BroadcastTeamPlanningStartedAsync(string sessionId, TeamPlanningService.Result r, string? plannerPersonaId) =>
        BroadcastAsync(sessionId, new TeamPlanningMessage(
            Start: true,
            Success: false,
            SubtaskCount: 0,
            WaveCount: 0,
            ElapsedMs: 0,
            Route: r.Route?.Model,
            Failure: null,
            PersonaId: plannerPersonaId,
            PromptChars: r.PromptChars,
            ResponseChars: 0));

    private Task BroadcastTeamPlanningFinishedAsync(string sessionId, TeamPlanningService.Result r, string? plannerPersonaId) =>
        BroadcastAsync(sessionId, new TeamPlanningMessage(
            Start: false,
            Success: r.Plan is not null,
            SubtaskCount: r.Plan?.Subtasks.Count ?? 0,
            WaveCount: r.Plan?.WaveCount ?? 0,
            ElapsedMs: (long)r.Elapsed.TotalMilliseconds,
            Route: r.Route?.Model,
            Failure: r.Plan is null ? PlannerFailureReason(r.Failure) : null,
            PersonaId: plannerPersonaId,
            PromptChars: r.PromptChars,
            ResponseChars: r.ResponseChars));

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

        // Страховка инварианта «перепланирование ⇒ старая карточка погашена»: обычно её
        // гасит вход в перепланирование (правка человека, clarify), но легаси-состояние могло
        // дойти до публикации и без него — у устаревшей версии не должно оставаться кнопок.
        if (replanning)
            await SupersedeCurrentPlanCardAsync(sessionId, entry, plan.Version);

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

        // Раздача — тем же путём, что «Запустить» и авто-волна: план у TeamWaveService.
        // Повод UserCommand: добавочная волна разворачивается вводной человека — точки
        // контроля уже пройдены, гейт авто-волн ей не нужен.
        if (TeamWaveStarter is { } starter)
        {
            try { await starter(entry.Info, plan, TeamWaveTrigger.UserCommand); }
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
    // Cancel — план отклонён, режим возвращается к планированию;
    // Edit — правка плана текстом feedback: сервер сам пересобирает план (см. ветку ниже).
    public async Task<TeamImplementPlan?> RespondTeamPlanAsync(string sessionId, string planId,
        TeamPlanDecision decision, string? subtaskId = null, string? executorPersonaId = null,
        string? userId = null, string? feedback = null)
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

        // Edit («Изменить план», прод 2026-08-04): серверное перепланирование. Правка —
        // решение по карточке, а не сообщение в чат: ход координатору не выдаётся, сервер
        // сам гасит текущую карточку как заменённую и запускает планировщик с правкой.
        // Итог детерминирован: либо карточка версии vN+1 на подтверждении, либо карточка
        // с причиной сбоя и кнопкой повтора — молчаливого тупика нет ни в каком исходе.
        if (decision == TeamPlanDecision.Edit)
        {
            var team = entry.Info.TeamImplement;
            if (team is null || string.IsNullOrWhiteSpace(feedback)) return null;
            // Правка жива только для плана на подтверждении: запущенный план уже раздаёт
            // волны (остаток меняется карточкой «Изменить остаток плана»), а отменённый
            // нечему править. Отклоняем тихо: кнопка в этих стадиях не рендерится.
            if (team.Stage is not (TeamImplementStage.Confirming or TeamImplementStage.Planning))
            {
                _log.LogInformation("Правка плана {PlanId} в чате {SessionId} пропущена: стадия {Stage}",
                    planId, sessionId, team.Stage);
                return null;
            }

            // Правка видна в ленте и остаётся в истории: при серверном перехвате хода
            // координатору не выдаётся, и без записи текст человека исчез бы из чата
            // (раньше кнопка слала его обычным сообщением).
            var editTs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            await AppendStoredAsync(sessionId,
                new StoredUserMessage(feedback.Trim(), timestamp: editTs),
                new UserMessageMessage(feedback.Trim(), null, null, false, Timestamp: editTs));

            var nextVersion = team.PlanVersion + 1;
            WithTeamState(sessionId, t =>
            {
                t.Stage = TeamImplementStage.Planning;
                // Тот же контур, что у clarify (Э8): следующий план — версия vN+1,
                // подтверждение обязательно даже при включённых авто-волнах.
                t.Replanning = true;
                return true;
            });
            await SupersedeCurrentPlanCardAsync(sessionId, entry, nextVersion);
            entry.Info.UpdatedAt = DateTime.UtcNow;
            SaveSessions();
            await BroadcastTeamImplementAsync(sessionId, entry);

            // Планировщик зовётся напрямую: вводная — Request самой карточки (последняя
            // накопленная постановка итерации), правка уходит отдельным блоком промпта.
            await RunTeamPlanningAsync(sessionId, plan.Request, feedback, fromHuman: true);
            return plan;
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
        // разорван хуком, как OnSessionMessage у TaskExecutionService). Повод UserCommand:
        // «Запустить» — явное решение человека, гейт авто-волн не нужен.
        if (decision == TeamPlanDecision.Run && TeamWaveStarter is { } starter)
        {
            try { await starter(entry.Info, plan, TeamWaveTrigger.UserCommand); }
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

    // Погасить ТЕКУЩУЮ карточку плана как заменённую версией nextVersion. Зовётся при
    // входе в перепланирование (правка человека кнопкой или маркер работы в подтверждении)
    // и страховочно при публикации новой версии: у устаревшей карточки не должно оставаться
    // живых кнопок вовсе — гард M8 ловит клик, но человек не должен его делать.
    // Идемпотентна: уже разрешённую карточку (запуск/отмена/повторный вход) не трогает.
    private async Task SupersedeCurrentPlanCardAsync(string sessionId, SessionEntry entry, int nextVersion)
    {
        if (entry.Info.TeamImplement is not { PlanCardId: { } oldId }) return;

        var plan = entry.Accumulator?.FindTeamPlanAny(oldId)
            ?? (entry.Accumulator is null ? await GetTeamPlanAsync(sessionId, oldId) : null);

        bool changed;
        if (entry.Accumulator is { } acc)
        {
            changed = acc.OnTeamPlanSuperseded(oldId, nextVersion);
            if (changed)
                FireAndForget(acc.SaveSnapshotAsync(_history),
                    $"сохранение истории после гашения заменённой карточки плана ({sessionId})");
        }
        else
        {
            changed = await MutateStoredAsync<StoredTeamPlanMessage>(entry, sessionId,
                m => m.PlanId == oldId && !m.Resolved,
                m => { m.Resolved = true; m.Approved = false; m.SupersededBy = nextVersion; });
        }
        if (!changed || plan is null) return;

        await BroadcastAsync(sessionId, new TeamPlanMessage(oldId, plan, true, false, nextVersion));
    }

    // Хук раздачи волны (Э3): назначается TeamWaveService при старте — так разрывается
    // цикл зависимостей (TaskExecutionService → SessionManager). null — раздача недоступна
    // (юнит-тесты без полного DI, инспекционный режим): режим тогда лишь меняет стадию.
    // Повод вызова (D1, ревью 2026-08-17) решает судьбу гейта авто-волн в TeamWaveService:
    // SessionManager лишь честно говорит, кнопка это была или докрут по состоянию.
    public Func<Session, TeamImplementPlan, TeamWaveTrigger, Task>? TeamWaveStarter { get; set; }

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
        // Причина именно из бюджета (а не «остановлено»/«ждёт решения»/«план не подтверждён») —
        // по ней ниже поднимается карточка с кнопкой «Добавить бюджет»
        string? budgetReason = null;
        lock (entry.TeamLock)
        {
            // Стадия волны — вторая проверка после «Остановлено»: без неё квота честно
            // считала расход, но разрешала запуск ДО публикации и подтверждения плана —
            // единственное согласование (карточка плана) обходилось целиком (Э7-фикс).
            if (team.Stopped)
                reason = "практика остановлена человеком — новые запуски не идут, пока он не продолжит";
            // M3: причина отказа обязана быть честной. Из «ждёт решения» ссылаться на
            // неподтверждённый план — враньё: план как раз подтверждён, а ждём мы ответа
            // человека по карточке остановки (кнопкой или обычным сообщением в чат).
            else if (team.Stage == TeamImplementStage.AwaitingDecision)
                reason = "практика ждёт решения человека по карточке остановки — запуск исполнителей " +
                         "возобновится, когда он ответит (кнопкой карточки или сообщением в чат)";
            else if (team.Stage != TeamImplementStage.Wave)
                reason = "план ещё не подтверждён человеком — запуск исполнителей доступен только " +
                         "в стадии волны, единственное согласование — карточка плана";
            else
                reason = budgetReason = team.Budget.ExceededReason();
            if (reason is null)
            {
                team.Budget.RunsUsed++;
                // Задача, запущенная руками координатора, — такая же задача итерации, как
                // розданная волной: без этого счётчика потолок задач обходился ручной раздачей
                team.Budget.TasksUsed++;
            }
        }
        if (reason is not null)
        {
            // Исчерпанный бюджет — единственный отказ, о котором человек ещё НЕ знает:
            // «остановлено» и «ждёт решения» уже висят карточкой, неподтверждённый план —
            // карточкой плана. Без этой публикации выхода из тупика не было вовсе: потолки
            // поднимает только кнопка «Добавить бюджет» карточки BudgetExhausted, а её
            // публиковала раздача волны — не гейт ручного запуска; попросить карточку
            // координатор тоже не мог (в протоколе лишь deviation/check/clarify), и штаб
            // бесконечно упирался в отказ, пока человек жал «Разрешить» на чужой карточке
            // расхождения с планом — та бюджет не трогает (прод 2026-08-08).
            if (budgetReason is not null)
                FireAndForget(RaiseTeamBudgetExhaustedAsync(stabId, budgetReason),
                    $"карточка исчерпанного бюджета итерации ({stabId})");
            return (TeamRunQuota.Exhausted, reason);
        }

        entry.Info.UpdatedAt = DateTime.UtcNow;
        SaveSessions();
        FireAndForget(BroadcastTeamImplementAsync(stabId, entry),
            $"рассылка состояния режима после расхода квоты ({stabId})");
        return (TeamRunQuota.Allowed, null);
    }

    // Карточка «Бюджет итерации израсходован» из точки отказа квоты: у человека появляется
    // кнопка «Добавить бюджет» — единственный способ поднять потолки (агенту он недоступен).
    // Публикация переводит практику в «ждёт решения», поэтому следующий отказ придёт уже с
    // другой причиной и второй карточки не даст. Через TeamEscalationRaiser, когда он есть:
    // хук вдобавок шлёт уведомление и push, иначе остановка осталась бы только в ленте.
    private async Task RaiseTeamBudgetExhaustedAsync(string stabId, string reason)
    {
        if (GetById(stabId) is not { TeamImplement: { } team } stab) return;
        if (team.Stage == TeamImplementStage.AwaitingDecision) return;

        var card = new TeamEscalation
        {
            Kind = TeamEscalationKind.BudgetExhausted,
            Title = TeamImplementPrompts.EscalationTitle(TeamEscalationKind.BudgetExhausted, reason),
            Details = $"Запуск исполнителя отклонён: {reason}.\n\n"
                      + TeamImplementPrompts.BudgetLine(team.Budget),
            Wave = team.WaveNumber,
            Actions = TeamEscalationActions.For(TeamEscalationKind.BudgetExhausted),
        };
        if (TeamEscalationRaiser is { } raise) await raise(stab, card);
        else await PublishTeamEscalationAsync(stabId, card);
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

    // Вердикт квоты запуска задач в цикле «до готово» (work-loop-аналог командной Э4).
    // NotInLoop — чат не в цикле: работает прежний запрет DenyOnDelegatedTurn.
    public enum WorkLoopRunQuota { NotInLoop, Allowed, Exhausted }

    // Гейт лавины запусков: на ходу доклада исполнителя (SuppressTasksExecute) чату
    // с включённым циклом запуск разрешён, ПОКА цел лимит — запрет заменён квотой, а не
    // снят, иначе «доклад → запуск → доклад» уходит в бесконечный платный круг.
    // Разрешение сразу расходует единицу: счёт ведёт бэкенд в точке запуска. Квота
    // принадлежит самой сессии цикла — в отличие от командной, вверх по родителям не
    // поднимаемся: чат исполнения под циклом не живёт (Guard B4).
    public (WorkLoopRunQuota Verdict, string? Reason) TryConsumeWorkLoopRun(string sessionId, string ownerId)
    {
        // Владельческую проверку (сессия существует + владелец тот) держим до лока: она не
        // зависит от WorkLoop. Но сам loop достаём под локом — иначе обнуление поля в
        // SetWorkLoopAsync оставит нас со ссылкой на уже выключенный объект, инкремент уйдёт
        // в мусор, а вердикт будет Allowed у чата, где цикл уже погашен.
        if (GetOwned(sessionId, ownerId) is null) return (WorkLoopRunQuota.NotInLoop, null);
        if (!_sessions.TryGetValue(sessionId, out var entry)) return (WorkLoopRunQuota.NotInLoop, null);

        lock (entry.LoopTurnLock)
        {
            if (entry.Info.WorkLoop is not { } loop) return (WorkLoopRunQuota.NotInLoop, null);
            if (loop.ExecutionsStarted >= loop.MaxExecutions)
                return (WorkLoopRunQuota.Exhausted,
                    $"запуски задач в цикле исчерпаны: {loop.ExecutionsStarted} из {loop.MaxExecutions}");
            loop.ExecutionsStarted++;
        }
        entry.Info.UpdatedAt = DateTime.UtcNow;
        SaveSessions();
        return (WorkLoopRunQuota.Allowed, null);
    }

    // Компенсация квоты запуска цикла: TryConsumeWorkLoopRun списывает единицу авансом,
    // ДО реальной попытки запуска. Если запуск не состоялся (404/400, исключение), платить
    // не за что — фильтр DenyOnDelegatedTurn.OnActionExecuted возвращает единицу сюда.
    public void RefundWorkLoopRun(string sessionId, string ownerId)
    {
        // Симметрично TryConsumeWorkLoopRun: loop достаём под локом, чтобы возврат не ушёл
        // в обнулённый SetWorkLoopAsync'ом объект.
        if (GetOwned(sessionId, ownerId) is null) return;
        if (!_sessions.TryGetValue(sessionId, out var entry)) return;

        lock (entry.LoopTurnLock)
        {
            if (entry.Info.WorkLoop is not { } loop) return;
            if (loop.ExecutionsStarted > 0) loop.ExecutionsStarted--;
        }
        entry.Info.UpdatedAt = DateTime.UtcNow;
        SaveSessions();
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

    // Открытые (не resolved) карточки остановки чата — сторожу повторных напоминаний
    // (TeamWaveService.CheckAwaitingEscalationsAsync). Активный чат — живые объекты
    // аккумулятора; неактивный (после рестарта) — копии с диска. У активного чата
    // возвращённые объекты — те же, что в истории: их правки фиксируются снимком.
    public async Task<IReadOnlyList<TeamEscalation>> GetOpenTeamEscalationsAsync(string sessionId)
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

    // Отметить отправленное повторное напоминание по карточке остановки: счётчик и момент
    // последнего оклика пишутся на карточку в истории — переживают рестарт сервера, чтобы
    // после перезапуска не начать оклик заново. false — карточка уже закрыта либо её нет.
    public async Task<bool> MarkTeamEscalationRemindedAsync(string sessionId, string escalationId)
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
        return await MutateStoredAsync<StoredTeamEscalationMessage>(entry, sessionId,
            m => m.EscalationId == escalationId && !m.Escalation.Resolved,
            m =>
            {
                m.Escalation.RemindersSent++;
                m.Escalation.LastReminderAt = DateTime.UtcNow;
            });
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
                // P23 (прод 2026-08-12): но если все плановые волны уже закрыты — возвращать в Wave
                // некуда, это вечная «волна N из N» без работы. В Idle: итерация завершена, режим
                // ждёт новой вводной. Хода координатору здесь нет, поэтому не Checking (оно зависло
                // бы без хода проверки), а сразу Idle — итог уже подведён в ходе работы волн.
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
                        : AllPlannedWavesClosed(team)
                            ? TeamImplementStage.Idle
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
        // карточке, раздаёт TeamWaveService (хук разрывает цикл DI). Явных кнопок четыре:
        // «Запустить», «Добавить бюджет», «Продолжить» и «Перезапустить» — после них практика
        // обязана поехать сама. Без раздачи волна не стартовала, WaveStartedAt оставался пустым
        // и сторож молчал: человек нажал кнопку, а работа встала навсегда без единого сигнала.
        // Мёртвая зона (прод 2026-08-17): тот же вызов ещё и по СОСТОЯНИЮ, а не только по
        // кнопке из белого списка — карточка могла висеть ПОСЛЕ закрытия волны (allow/
        // keepPlan/answer…), авто-раздача следующей была уже подавлена, и кроме этого
        // вызова позвать её было некому. Действия с иной стадией (finish/stop/editRest/
        // retryPlan) сюда не попадают: их стадия не Wave.
        // D1 (ревью 2026-08-17): повод вызова различает два случая — кнопки «Запустить»/
        // «Добавить бюджет»/«Продолжить»/«Перезапустить» это явное решение запускать (гейт
        // не нужен), а докрут по состоянию при снятых авто-волнах обязан показать гейт-карточку
        // вместо молчаливой раздачи. Что именно делать, решает TeamWaveService по поводу
        // вызова — второй точки истины здесь не заводим.
        // «Перезапустить» в списке с круга 3 (приёмка круга 2): до него клик по карточке
        // мёртвой зоны при снятых авто-волнах поднимал гейт, и работа ехала со второго
        // клика — подпись обещала больше, чем делала. Для обычной зависшей волны добавление
        // ничего не меняет: там ClosedWave < WaveNumber, предикат раздачи ложен.
        // Раздавать нечего (волна уже идёт) — StartWave вернёт пустой список и не навредит.
        if (entry.Info.TeamImplement is { } teamNow
            && teamNow.PlanCardId is { } planId
            && TeamWaveStarter is { } starter)
        {
            var plan = await GetTeamPlanAsync(sessionId, planId);
            var trigger = actionId is "runNext" or "addBudget" or "resume" or "restart"
                ? TeamWaveTrigger.UserCommand
                : TeamWaveTrigger.StateCatchUp;
            if (plan is not null && (trigger == TeamWaveTrigger.UserCommand
                    || WaveStartPendingAfterDecision(teamNow, plan)))
            {
                try { await starter(entry.Info, plan, trigger); }
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
        // Правка плана («Изменить план») повторяется С НЕЙ ЖЕ — иначе повтор вернул бы
        // прежний план, и правка человека потерялась бы. Стадию уже поставили Planning выше,
        // гарды StartTeamWorkAsync повтору не нужны. Не получится снова — RunTeamPlanningAsync
        // опубликует новую карточку с той же кнопкой.
        if (actionId == "retryPlan")
        {
            var teamState = entry.Info.TeamImplement;
            if (!string.IsNullOrWhiteSpace(teamState?.LastPlanRequest))
                await RunTeamPlanningAsync(sessionId, teamState.LastPlanRequest,
                    teamState.LastPlanFeedback, entry.TeamTurnFromHuman);
            else
                _log.LogWarning("Повтор планирования в чате {SessionId}: сохранённая вводная пуста", sessionId);
            return true;
        }

        // Координатор узнаёт решение обычным ходом — как если бы человек написал его текстом.
        // В ленте — плашка механики, а не пузырь «Автоматически» с сырым текстом директивы.
        await SendOrEnqueueAsync(sessionId,
            TeamImplementPrompts.EscalationResolvedTurn(escalation, label, comment),
            senderPersonaId: null, silent: true, suppressTasksExecute: true,
            staffNote: TeamStaffNotes.EscalationResolved);
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
    // `decision` в протоколе координатора больше нет — вопрос в живом ходу задаётся ASK;
    // парсер терпит маркер как фолбэк (старые транскрипты, привычка модели) — карточка с полем
    // лучше молчаливого зависания.
    internal static (TeamEscalationKind Kind, string Text)? ParseEscalationMarker(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        // Теги маркера ищем ВНЕ код-блоков (модель любит цитировать протокол примером), а
        // содержимое между ними берём из ОРИГИНАЛЬНОГО текста — со всем вложенным кодом.
        // Закрытие по имени (</escalate:check>) терпит close-регэксп: строгое сравнение роняло
        // маркер в молчаливый тупик (модель по XML-привычке закрывает тег по имени при генерации).
        var found = FindPairedMarkerOutsideCode(text, EscalateOpenTagRegex, EscalateCloseTagRegex);
        if (found is null) return null;
        var (openEnd, closeStart, _, openMatch) = found.Value;
        var kind = openMatch.Groups[1].Value switch
        {
            "deviation" => TeamEscalationKind.PlanDeviation,
            "check" => TeamEscalationKind.CheckFailed,
            // Тупик в волне (Э8): не остановка «жду решения», а возврат в интервью
            "clarify" => TeamEscalationKind.NeedsClarification,
            _ => TeamEscalationKind.ProductDecision,
        };
        return (kind, text[openEnd..closeStart].Trim());
    }

    // Маркер работы в ответе координатора (Э5): `<team:work>постановка</team>`. Им координатор
    // говорит, что вводная человека требует правки файлов — бэкенд разложит её планировщиком
    // и развернёт волну. Разговорный ответ маркера не несёт и не стоит ничего.
    // Разбор — как у эскалации: вне код-блоков, потому что протокол модель любит цитировать.
    internal static string? ParseWorkMarker(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        // Теги маркера ищем ВНЕ код-блоков (модель любит цитировать протокол примером), а
        // содержимое между ними берём из ОРИГИНАЛЬНОГО текста — со всем вложенным кодом. Без
        // этого код-блок внутри <team:work> (дамп компонента в разведке) вырезался до извлечения,
        // и планировщик получал постановку без кода (P19). Закрытие по имени (</team:work>)
        // терпит close-регэксп — фикс инцидента 2026-07-31 сохранён.
        var found = FindPairedMarkerOutsideCode(text, WorkOpenTagRegex, WorkCloseTagRegex);
        if (found is null) return null;
        var (openEnd, closeStart, _, _) = found.Value;
        var request = text[openEnd..closeStart].Trim();
        return request.Length == 0 ? null : request;
    }

    // Маркер разговора (M6): `<team:talk/>` — координатор честно разобрал сообщение человека:
    // работы нет, файлы менять не нужно. Легальный выход из интервью без плана — по голому
    // тексту бэкенд не отличит такой ответ от молчаливого тупика (stall-гард). Разбор — как
    // у прочих маркеров: вне код-блоков, потому что протокол модель любит цитировать.
    internal static bool HasTalkMarker(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        // Самодостаточный тег — хотя бы один match целиком вне код-блоков (как у парных маркеров:
        // процитированный в ```-примере маркер не считается активным вызовом).
        var ranges = GetCodeBlockRanges(text);
        foreach (System.Text.RegularExpressions.Match m in TalkMarkerRegex.Matches(text))
            if (IsRangeOutsideCode(ranges, m.Index, m.Index + m.Length)) return true;
        return false;
    }

    // Маркер молчания (B4 «Доклада о завершении задачи»): `<no-reply/>` — ходу нечего сказать
    // человеку. Ответ ровно этим маркером не должен оставить в ленте ни реплики, ни следа
    // пустого хода: стрижка ниже вырезает маркер, а «после стрижки пусто» нигде не создаёт
    // запись (ни в живой трансляции, ни в истории — TurnAccumulator.FlushBuffers).
    // В отличие от маркеров штаба живёт в ЛЮБОМ чате: им отвечает обычная персона постановщика.
    internal const string NoReplyMarker = "<no-reply/>";

    internal static bool HasNoReplyMarker(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        // Самодостаточный тег — как <team:talk/>: процитированный в ```-примере не считается
        var ranges = GetCodeBlockRanges(text);
        foreach (System.Text.RegularExpressions.Match m in NoReplyMarkerRegex.Matches(text))
            if (IsRangeOutsideCode(ranges, m.Index, m.Index + m.Length)) return true;
        return false;
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
    private static readonly System.Text.RegularExpressions.Regex TalkMarkerRegex =
        new(@"<team:talk\s*/>", System.Text.RegularExpressions.RegexOptions.Compiled);
    private static readonly System.Text.RegularExpressions.Regex NoReplyMarkerRegex =
        new(@"<no-reply\s*/>", System.Text.RegularExpressions.RegexOptions.Compiled);

    // Открывающие/закрывающие теги маркеров — и для РАЗБОРА (Parse*/Has* выше), и для
    // зачистки ленты (RemovePairedMarkers ниже) один и тот же позиционный поиск пары:
    // зачистка и разбор находят границы маркера одним способом и не расходятся. Найти
    // закрывающий тег ВНЕ кода одним lazy-регэкспом нельзя — он свернётся на закрывающем
    // теге, процитированном внутри код-блока, и настоящий маркер с вложенным кодом (P19)
    // не соберётся. Поэтому ищем теги по отдельности и проверяем, что оба лежат вне
    // код-блоков, а содержимое между ними берём из оригинала.
    private static readonly System.Text.RegularExpressions.Regex WorkOpenTagRegex =
        new("<team:work>", System.Text.RegularExpressions.RegexOptions.Compiled);
    private static readonly System.Text.RegularExpressions.Regex WorkCloseTagRegex =
        new(@"</team(?::work)?>", System.Text.RegularExpressions.RegexOptions.Compiled);
    private static readonly System.Text.RegularExpressions.Regex EscalateOpenTagRegex =
        new(@"<escalate:(deviation|check|decision|clarify)>", System.Text.RegularExpressions.RegexOptions.Compiled);
    private static readonly System.Text.RegularExpressions.Regex EscalateCloseTagRegex =
        new(@"</escalate(?::\w+)?>", System.Text.RegularExpressions.RegexOptions.Compiled);

    // Осиротевший закрывающий тег без пары (прод 2026-08-02, находка Веры): в длинном
    // структурированном ответе модель иногда закрывает маркер повторно или цитирует закрытие
    // отдельно от открытия, которое уже вырезано парным поиском выше (например тем же именем
    // маркера двумя абзацами раньше). Такой закрывающий тег — всегда служебный синтаксис
    // нашего протокола (`</team>`/`</team:work>`, `</escalate>`/`</escalate:kind>`), человеку
    // он не нужен ни в какой форме — вырезаем и его.
    private static readonly System.Text.RegularExpressions.Regex OrphanCloserRegex =
        new(@"</escalate(?::\w+)?>|</team(?::work)?>", System.Text.RegularExpressions.RegexOptions.Compiled);

    internal static string StripTeamProtocolMarkers(string text)
    {
        if (string.IsNullOrEmpty(text) || !text.Contains('<')) return text;
        // Парные маркеры (эскалация/работа) вырезаем из ИСХОДНОГО текста позиционно — тем же
        // поиском пары тегов вне код-блоков, что и разбор. Рез по код-блокам здесь не годится:
        // маркер с fenced-блоком внутри (хвост P19) разрезался на сегменты, сегмент до фенса
        // оставался в ленте с буквальным <team:work> и всей постановкой, а закрывающий тег
        // съедался как осиротевший. Диапазон [openStart, closeEnd) уносит и вложенный код.
        text = RemovePairedMarkers(text, EscalateOpenTagRegex, EscalateCloseTagRegex);
        text = RemovePairedMarkers(text, WorkOpenTagRegex, WorkCloseTagRegex);
        // Остальное (самозакрывающиеся маркеры, осиротевшие закрывающие теги) — по-прежнему
        // посегментно: только вне код-блоков, процитированный в примере протокол не трогаем.
        var sb = new System.Text.StringBuilder(text.Length);
        var pos = 0;
        foreach (System.Text.RegularExpressions.Match code in CodeSpanOrFenceRegex.Matches(text))
        {
            sb.Append(StripUnpairedMarkers(text[pos..code.Index]));
            sb.Append(text, code.Index, code.Length);
            pos = code.Index + code.Length;
        }
        sb.Append(StripUnpairedMarkers(text[pos..]));
        return sb.ToString();
    }

    // Вырезает из текста каждый парный маркер openTag...closeTag, у которого ОБА тега лежат
    // вне код-блоков. После каждого удаления поиск начинается заново — позиции сдвинулись.
    private static string RemovePairedMarkers(string text,
        System.Text.RegularExpressions.Regex openTagRegex, System.Text.RegularExpressions.Regex closeTagRegex)
    {
        while (FindPairedMarkerOutsideCode(text, openTagRegex, closeTagRegex) is { } found)
        {
            var openStart = found.OpenMatch.Index;
            text = text.Remove(openStart, found.CloseEnd - openStart);
        }
        return text;
    }

    private static string StripUnpairedMarkers(string text)
    {
        if (text.Length == 0 || !text.Contains('<')) return text;
        text = TalkMarkerRegex.Replace(text, "");
        text = NoReplyMarkerRegex.Replace(text, "");
        text = OrphanCloserRegex.Replace(text, "");
        return text;
    }

    // Диапазоны fenced- (```...```) и инлайн- (`...`) код-блоков в порядке появления. В отличие
    // от прежнего вырезания кода перед разбором маркера, позиции позволяют найти теги ВНЕ кода
    // и вернуть содержимое маркера из оригинала — со всем вложенным кодом (фикс P19: раньше
    // код-блок внутри <team:work> вырезался до извлечения, и планировщик получал пустую постановку).
    private static List<(int Start, int End)> GetCodeBlockRanges(string text)
    {
        var ranges = new List<(int Start, int End)>();
        foreach (System.Text.RegularExpressions.Match code in CodeSpanOrFenceRegex.Matches(text))
            ranges.Add((code.Index, code.Index + code.Length));
        return ranges;
    }

    // Целиком ли диапазон [start, end) лежит вне код-блоков (не пересекается ни с одним).
    private static bool IsRangeOutsideCode(List<(int Start, int End)> ranges, int start, int end)
    {
        foreach (var (s, e) in ranges)
            if (start < e && end > s) return false;   // пересечение с код-блоком
        return true;
    }

    // Первый парный маркер (openTag...closeTag), у которого ОБА тега целиком лежат вне код-блоков.
    // Возвращает границы в оригинальном тексте (включая конец закрывающего тега — зачистка ленты
    // вырезает диапазон [openStart, closeEnd) целиком) и match открывающего тега (для групп —
    // напр. тип эскалации). Содержимое между тегами (включая вложенный код) вызывающий берёт из
    // оригинала через text[openEnd..closeStart]. Так процитированный в ```-примере маркер не
    // сработает (тег внутри код-блока), а код внутри настоящей постановки не потеряется.
    private static (int OpenEnd, int CloseStart, int CloseEnd, System.Text.RegularExpressions.Match OpenMatch)?
        FindPairedMarkerOutsideCode(
            string text,
            System.Text.RegularExpressions.Regex openTagRegex,
            System.Text.RegularExpressions.Regex closeTagRegex)
    {
        var ranges = GetCodeBlockRanges(text);
        for (var om = openTagRegex.Match(text); om.Success; om = om.NextMatch())
        {
            var openEnd = om.Index + om.Length;
            if (!IsRangeOutsideCode(ranges, om.Index, openEnd)) continue;   // открывающий в коде — цитата
            for (var cm = closeTagRegex.Match(text, openEnd); cm.Success; cm = cm.NextMatch())
            {
                if (IsRangeOutsideCode(ranges, cm.Index, cm.Index + cm.Length))
                    return (openEnd, cm.Index, cm.Index + cm.Length, om);   // закрывающий вне кода — настоящий маркер
            }
        }
        return null;
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
        // Самозакрывающийся маркер молчания целиком: любой его префикс («<n», «<no-repl»,
        // «<no-reply/») ещё может дорасти до маркера — до этого показывать хвост нельзя
        NoReplyMarker,
    ];

    internal static bool IsAmbiguousMarkerTail(string tail)
    {
        if (tail.Length == 0 || tail[0] != '<') return false;
        foreach (var open in MarkerOpenTags)
            if (open.Length > tail.Length && open.StartsWith(tail, StringComparison.Ordinal))
                return true;
        // У `<team:talk/>` и `<no-reply/>` пробелы перед `/>` не фиксированы регэкспом разбора —
        // сюда попадает только незавершённый префикс (полный маркер уже вырезан
        // StripTeamProtocolMarkers)
        return System.Text.RegularExpressions.Regex.IsMatch(tail, @"^<(?:team:talk|no-reply)\s*/?$");
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
        "<team:work>", "<team:talk", "<no-reply",
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

    // Отсечки сторожа волн, погашенные вопросом ASK (OnStabAskQuestionAsync), возвращаются
    // по завершении хода — ответ получен, либо ход прерван (прерывание без result приходит
    // сюда же, а чистый ExitedMessage обрабатывает зовущий). Без возврата волна осталась бы
    // без надзора: настоящий stall никто бы не поймал, а «молчаливых пауз не бывает».
    // Волна должна быть живой: закрытая (ClosedWave == WaveNumber) или нулевая — не в счёт.
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

        // Вопрос ASK в волне гасил отсечки сторожа (OnStabAskQuestionAsync): ход завершился —
        // ответ получен или ход прерван, волна снова под надзором. Стадию не трогаем: если
        // дальше по ходу маркер эскалации, публикация карточки сама переведёт практику в
        // ожидание и снова обнулит отсечки.
        RestoreWaveWatchdogIfPaused(sessionId, entry);

        // P23 (прод 2026-08-12): практика в «ждёт решения» по карточке блокера, но координатор
        // в этом ходе снял предмет блокера сам — продолжил работу маркером team:work или подвёл
        // итог при закрытых волнах плана. Карточку не держать: иначе она висит по решённому
        // вопросу, стадия сто́ит в AwaitingDecision, а человек отвечает кнопкой на уже ненужную
        // эскалацию (прогон Веры P23: координатор сам закрыл задачу и подвёл итог, карточка
        // осталась висеть). Если погасили — перечитываем состояние команды: стадия сменилась,
        // и дальше HandleTeamTurnEndAsync идёт по ней (team:work разберёт StartTeamWorkAsync,
        // Checking доводится до Idle ниже).
        if (team.Stage == TeamImplementStage.AwaitingDecision
            && await TryAutoResolveTeamBlockerAsync(sessionId, entry, turnText)
            && entry.Info.TeamImplement is { } teamAfterResolve)
        {
            team = teamAfterResolve;
        }

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
        // Прод 2026-08-12 (P16): тот же аргумент — для async-субагента координатора. Ход,
        // что закончился текстом «запустил разведку», но в фоновом Tool-вызове оставил живого
        // агента (entry.Process.HasPendingBg), — НЕ тупик: координатор ждёт собственного
        // результата, и следующий ход (пробуждение по task-notification) почти наверняка
        // принесёт маркер team:work. Без исключения гард поднимал «Координатор не понял
        // вводную» на живой работе и уводил стадию в AwaitingDecision — тогда штатный
        // team:work уже не потреблялся (StartTeamWorkAsync её не принимает), и человеку
        // приходилось отвечать на ложную карточку (прогон Веры P16).
        var stalledStage = team.Stage == TeamImplementStage.Interview
            || (team.Stage == TeamImplementStage.Planning && team.WaveNumber == 0);
        if (stalledStage && !asked && !entry.TeamPlanningInFlight && !AsyncAgentInFlight(entry))
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

    // Все плановые волны итерации закрыты — работа по плану закончена. P23: это сигнал того,
    // что карточка блокера стала неактуальна финалом итерации (координатор подвёл итог, а
    // стадия зависла в AwaitingDecision), и критерий терминального состояния при возврате из
    // «ждёт решения» (RespondTeamEscalation / ResumeTeamFromDecisionOnUserInput) — иначе
    // практика формально «в волне N из N» после полностью закрытых волн. PlannedWaves == 0
    // (план не запускался) никогда не считаем закрытым набором.
    private static bool AllPlannedWavesClosed(SessionTeamImplement team) =>
        team.PlannedWaves > 0 && team.ClosedWave >= team.PlannedWaves;

    // Мёртвая зона конвейера (прод 2026-08-17): карточка остановки висела ПОСЛЕ закрытия
    // волны — авто-раздача следующей уже была подавлена («практика ждёт человека»), а белый
    // список actionId ответа её не покрывал: конвейер замолкал до ручного «Остановить →
    // Продолжить». Признак «раздачу нужно позвать» после решения человека: практика вернулась
    // в Wave, все РОЗДАННЫЕ волны закрыты (ClosedWave == WaveNumber), а в плане есть
    // нерозданные под-задачи. Решение «раздать или поднять карточку» остаётся за
    // TeamWaveService (бюджет, версии плана, «Остановить») — здесь только «пора ли звать».
    private static bool WaveStartPendingAfterDecision(SessionTeamImplement team, TeamImplementPlan plan) =>
        team.Stage == TeamImplementStage.Wave
        && team.WaveNumber > 0
        && team.ClosedWave == team.WaveNumber
        && plan.Subtasks.Any(s => s.TaskId is null);

    // У координатора есть живый фоновый субагент (Tool Agent и т.п.) — ход, завершённый
    // текстом без маркера, не тупик: координатор ждёт собственного результата. P16: по этому
    // признаку гард молчаливого тупика молчит (аналог TeamPlanningInFlight). entry.Process —
    // адаптер текущего прогона CLI; HasPendingBg истинно, пока прогон доживает фоновых агентов.
    private static bool AsyncAgentInFlight(SessionEntry entry) => entry.Process?.HasPendingBg ?? false;

    // P23: авто-гашение карточки блокера, когда координатор сам снял её предмет. Разбудившийся
    // по докладу-блокеру координатор (BlockerReactionTurn) отвечает ходом — и если этот ход
    // продолжает работу (маркер team:work) либо закрывает итерацию (все волны плана закрыты),
    // карточку более не держать: звать человека по решённому вопросу не нужно (прогон P23:
    // координатор сам закрыл задачу и подвёл итог, карточка висела в AwaitingDecision). true —
    // погасил и сдвинул стадию; false — оснований для авто-резолва нет (ждём человека).
    private async Task<bool> TryAutoResolveTeamBlockerAsync(string sessionId, SessionEntry entry, string turnText)
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
        // team:work, либо итерация финиширована (все плановые волны закрыты). Иначе карточка
        // уместна — координатор реально ждёт решения человека, оставляем как есть.
        var hasWork = ParseWorkMarker(turnText) is not null;
        var allWavesClosed = AllPlannedWavesClosed(team);
        if (!hasWork && !allWavesClosed) return false;

        // Гасим карточку тем же путём, что кнопка человека (RespondTeamEscalationAsync):
        // помечаем Resolved, пишем снимок истории, рассылаем WS с resolved=true (иначе на F5
        // карточка вновь подсветилась бы как ждущая ответа).
        var card = openBlocker.Escalation;
        if (entry.Accumulator is { } acc)
        {
            acc.OnTeamEscalationResolved(openBlocker.EscalationId, "answer");
            FireAndForget(acc.SaveSnapshotAsync(_history),
                $"сохранение истории после авто-гашения карточки блокера ({sessionId})");
        }
        await BroadcastAsync(sessionId, new TeamEscalationMessage(openBlocker.EscalationId,
            TeamEscalationKind.Blocker.ToWireToken(), card.Title, card.Details, card.Actions,
            card.TaskId, card.Wave, Resolved: true, ChosenActionId: "answer", card.PersonaId));

        // Стадию возвращаем так, чтобы практика поехала дальше без призрака ожидания:
        // 1) team:work запускает перепланирование: при закрытых волнах это новая итерация (Idle
        //    — StartTeamWorkAsync её сбросит), иначе Planning (RunTeamPlanningAsync в той же итерации).
        // 2) без team:work, но с закрытыми волнами — финальная проверка (Checking); её в этом же
        //    вызове HandleTeamTurnEndAsync доведёт до Idle (терминал), координатор итог уже подвёл.
        // Возвращать StageBeforeDecision (там обычно Wave) нельзя — работы в старой волне больше нет.
        WithTeamState(sessionId, t =>
        {
            t.Stage = hasWork
                ? (allWavesClosed ? TeamImplementStage.Idle : TeamImplementStage.Planning)
                : TeamImplementStage.Checking;
            t.StageBeforeDecision = null;
            t.WaveStartedAt = null;
            t.WaveActivityAt = null;
            return true;
        });
        entry.Info.UpdatedAt = DateTime.UtcNow;
        SaveSessions();
        await BroadcastTeamImplementAsync(sessionId, entry);
        _log.LogInformation("Карточка блокера {CardId} чата-штаба {SessionId} погашена автоматически: " +
            "координатор снял блокер сам ({Reason})", openBlocker.EscalationId, sessionId,
            hasWork ? "team:work" : "все волны плана закрыты");
        return true;
    }

    // Новая вводная разложена планировщиком и уходит в волну (Э5). Гард по стадии: работу
    // разворачиваем только когда итерация не идёт — иначе маркер посреди волны запустил бы
    // вторую поверх первой. «Остановить» удерживает маркеры в стадиях идущей итерации, но
    // не новую вводную в ожидании: классифицированная как работа — она и есть решение
    // человека продолжить (спека «Бюджет»: «Остановить» относится к прошлой итерации).
    // feedback — правка человека к текущему плану («Изменить план»): планировщик
    // пересобирает план под неё (см. TeamPlanningService.BuildPlannerPrompt).
    private async Task StartTeamWorkAsync(string sessionId, string request, string? feedback = null)
    {
        if (!_sessions.TryGetValue(sessionId, out var entry)) return;
        if (entry.Info.TeamImplement is not { } team) return;
        // Э8: интервью — легальная точка выхода в план (маркером его и закрывает координатор).
        // Confirming (правка плана текстом, прод 2026-08-04): координатор получил правку и
        // обязан пересобрать план маркером работы (PlanEditProtocol) — до фикса маркер здесь
        // молча проглатывался, и человек оставался со старой карточкой.
        if ((team.Stopped && team.Stage != TeamImplementStage.Idle)
            || team.Stage is not (TeamImplementStage.Interview
                or TeamImplementStage.Planning or TeamImplementStage.Idle
                or TeamImplementStage.Confirming))
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

        // Правка плана на подтверждении: тот же контур, что у clarify (Э8) — старая карточка
        // гаснет как заменённая, новый план публикуется версией vN+1 с обязательным
        // подтверждением. План-режим уже навязан (Confirming живёт в нём со стадии интервью).
        if (team.Stage == TeamImplementStage.Confirming)
        {
            var nextVersion = team.PlanVersion + 1;
            WithTeamState(sessionId, t =>
            {
                t.Stage = TeamImplementStage.Planning;
                t.Replanning = true;
                t.WaveStartedAt = null;
                t.WaveActivityAt = null;
                return true;
            });
            await SupersedeCurrentPlanCardAsync(sessionId, entry, nextVersion);
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

        await RunTeamPlanningAsync(sessionId, request, feedback, entry.TeamTurnFromHuman);
    }

    // Собственно планирование: вводная (и правка к плану) сохраняются для повтора, зовётся
    // планировщик, а при отказе публикуется карточка с причиной и кнопкой повтора. Гардов
    // по стадии нет — состояние готовит вызывающий (StartTeamWorkAsync для вводной,
    // RespondTeamPlanAsync для правки «Изменить план», retryPlan для повтора после сбоя).
    // Молчаливых тупиков не бывает ни в одном исходе: успех даёт карточку плана, сбой и
    // таймаут — карточку отказа.
    private async Task RunTeamPlanningAsync(string sessionId, string request, string? feedback,
        bool fromHuman)
    {
        if (!_sessions.TryGetValue(sessionId, out var entry)) return;
        if (entry.Info.TeamImplement is not { } team) return;

        // Вводная и правка сохраняются на состоянии ДО планировщика: при его отказе человек
        // сможет повторить планирование кнопкой карточки, не проходя интервью заново и не
        // теряя правку (повтор обязан пересобирать план по той же правке).
        WithTeamState(sessionId, t =>
        {
            t.LastPlanRequest = request;
            t.LastPlanFeedback = feedback;
            return true;
        });

        var isEdit = !string.IsNullOrWhiteSpace(feedback);
        // Флаг живого планирования: гард молчаливого тупика по концу хода (см.
        // HandleTeamTurnEndAsync) не поднимает тревогу, пока планировщик реально строит план.
        entry.TeamPlanningInFlight = true;
        try
        {
            var (plan, reason) = await CreateTeamPlanAsync(sessionId, request,
                fromHuman: fromHuman, feedback: feedback);
            if (plan is not null) return;

            // Молчаливых тупиков в режиме не бывает: человек ждёт план — значит про
            // несостоявшийся план он должен узнать карточкой, а не по тишине. Таймаут
            // планировщика — отдельный случай: причина не в постановке человека, и текст
            // карточки называет её как есть. У правки текст свой: старая карточка уже
            // погашена как заменённая, и без карточки отказа человек остался бы вообще без плана.
            // Обрыв по токенам и невалидный JSON — третья и четвёртая ветки (прод 2026-08-05):
            // совет другой, текст другой, без подмены «таймаут».
            var (title, details) = isEdit
                ? EditFailureText(feedback!, reason)
                : FreshFailureText(request, reason);
            var failed = new TeamEscalation
            {
                Kind = TeamEscalationKind.ProductDecision,
                Title = title,
                Details = details,
                Wave = team.WaveNumber,
                // Кнопка повторяет планирование по сохранённой вводной и правке (retryPlan в
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

    // Заголовок и тело карточки отказа планировщика: для СВЕЖЕЙ вводной (не правки).
    // reason — строковый ключ причины (PlannerTimeoutReason / PlannerTruncatedReason /
    // PlannerInvalidJsonReason / fallback «Планировщик не смог…»). У каждой причины —
    // своё название и свой совет: таймаут «не ваша вина, повторите», обрыв «план не
    // уместился в лимит, попробуйте короче», невалидный JSON «повторите».
    private static (string Title, string Details) FreshFailureText(string request, string? reason) =>
        reason switch
        {
            TeamPlanningService.PlannerTimeoutReason => (
                "План не построился: планировщик не уложился во время",
                TeamImplementPrompts.PlanTimeoutDetails(request)),
            TeamPlanningService.PlannerTruncatedReason => (
                "План не построился: планировщик не уместил план в лимит вывода",
                TeamImplementPrompts.PlanTruncatedDetails(request)),
            TeamPlanningService.PlannerInvalidJsonReason => (
                "План не построился: планировщик вернул неразборчивый план",
                TeamImplementPrompts.PlanInvalidJsonDetails(request)),
            _ => (
                "План по вашей вводной не построился",
                TeamImplementPrompts.PlanFailedDetails(request, reason)),
        };

    // Заголовок и тело карточки отказа для ПРАВКИ: «Изменить план» отдельно от
    // первоначальной вводной, потому что старая карточка уже погашена.
    private static (string Title, string Details) EditFailureText(string feedback, string? reason) =>
        reason switch
        {
            TeamPlanningService.PlannerTimeoutReason => (
                "План не пересобрался: планировщик не уложился во время",
                TeamImplementPrompts.PlanEditTimeoutDetails(feedback)),
            TeamPlanningService.PlannerTruncatedReason => (
                "План не пересобрался: планировщик не уместил правку в лимит вывода",
                TeamImplementPrompts.PlanEditTruncatedDetails(feedback)),
            _ => (
                "Правка не привела к новой версии плана",
                TeamImplementPrompts.PlanEditFailedDetails(feedback, reason)),
        };

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

        // Третья дверь в мёртвую зону (Major, ревью 2026-08-17): интервью могло закончиться
        // ПОСЛЕ закрытия волны (clarify посреди волны → CloseWaveIfDoneAsync стоит в
        // waitsHuman, ставит ClosedWave и конвейер не двигает). Тогда выше стадия вернулась
        // в Wave, но отсечки не заводятся (ClosedWave == WaveNumber) и раздачу следующей
        // никто не позвал — тот же стоящий конвейер, который сторож ловил бы только через
        // таймаут простоя. Тот же предикат и тот же вызов раздачи, что у двух других точек
        // выхода из ожидания; повод StateCatchUp — гейт при снятых авто-волнах решает
        // TeamWaveService, как и везде.
        if (entry.Info.TeamImplement is { } teamNow
            && teamNow.PlanCardId is { } planId
            && TeamWaveStarter is { } starter)
        {
            var plan = await GetTeamPlanAsync(sessionId, planId);
            if (plan is not null && WaveStartPendingAfterDecision(teamNow, plan))
            {
                try { await starter(entry.Info, plan, TeamWaveTrigger.StateCatchUp); }
                catch (Exception ex)
                {
                    _log.LogError(ex, "Раздача волны после выхода из интервью (чат {SessionId}) не удалась", sessionId);
                }
            }
        }
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
    // P23: если все плановые волны уже закрыты — возвращать в Wave некуда (вечная «волна N из
    // N»), идём в Idle: итерация завершена, режим ждёт новой вводной.
    // Карточку в ленте не гасим: она остаётся историей, а повторное решение по ней приведёт
    // практику в то же состояние (путь идемпотентен по стадии).
    private async Task ResumeTeamFromDecisionOnUserInput(string sessionId, SessionEntry entry)
    {
        if (entry.Info.TeamImplement is not { Stage: TeamImplementStage.AwaitingDecision }) return;

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
            && TeamWaveStarter is { } starter)
        {
            var plan = await GetTeamPlanAsync(sessionId, planId);
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
        if (hadPlan) await SupersedeCurrentPlanCardAsync(sessionId, entry, nextVersion);
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
                staffNote: TeamStaffNotes.InterviewReturn);
    }

    // Координатор задал вопрос ASK-карточкой (Э8). В интервью это очередной раунд (их не
    // больше двух на вводную — счёт ведёт бэкенд, модель своих раундов не помнит).
    // Вне интервью вопрос живёт внутри хода и практику НЕ останавливает (решение по запросу
    // владельца 2026-08-04 — единый канал вопросов): продуктовая развилка или уточнение
    // задаётся ASK, ответ приходит в тот же ход и работа продолжается с того же места.
    // Возврат в интервью с паузой волн и перепланированием остался только за явным маркером
    // <escalate:clarify> («требования неясны и действовать нельзя») — прежний вход сюда из
    // ASK делал из любого вопроса пересборку плана.
    internal async Task OnStabAskQuestionAsync(string sessionId)
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
    // проверка; продуктовый вопрос ушёл в ASK — маркер decision здесь только фолбэк).
    // Заголовки — из таблицы «Эскалация и остановки».
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

    // Потолок добиваний подряд. Две попытки — сознательный предел: бесконечный цикл добивания
    // хуже честного отказа, после него судьбу работы решает человек.
    internal const int MaxSubagentNudges = 2;

    /// <summary>
    /// Слать ли добивание оборванного сабагента. Чистая функция — вся политика в одном месте
    /// (под тестом). Уступаем всем, у кого свой протокол продолжения: цикл «до готово» и штаб
    /// продолжат ход сами, непустая очередь продолжит его сообщением, а параллельный ход цикла
    /// (LoopTurnInFlight) уже в пути — второй systemDirective ушёл бы в тот же процесс.
    /// </summary>
    internal static bool ShouldNudgeSubagent(int nudgesSent, bool workLoopActive, bool teamActive,
        bool hasPending, bool loopTurnInFlight) =>
        nudgesSent < MaxSubagentNudges && !workLoopActive && !teamActive && !hasPending && !loopTurnInFlight;

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
        Llm.Claude.SubagentRunPassport run)
    {
        // Пометка уедет префиксом ближайшего хода — чем бы он ни был поднят (человеком,
        // очередью, добиванием): координатор обязан узнать, что обрывок не итог, даже когда
        // добивать мы не стали.
        entry.TruncatedBgNote = run;

        if (entry.Process is null || TurnInFlight(entry.Info.Status)) return;
        // Оборвался другой агент — у него своя серия попыток (счётчик per-agentId)
        if (StartsNudgeSeries(entry.NudgeAgentId, run.AgentId)) entry.SubagentNudges = 0;
        if (!ShouldNudgeSubagent(entry.SubagentNudges, entry.Info.WorkLoop is not null,
                entry.Info.TeamImplement is not null, HasPending(entry), entry.LoopTurnInFlight)) return;

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
    // потолком попыток — см. SubagentPrompts.ResumeTruncated.
    private async Task NudgeTruncatedSubagentAsync(string sessionId,
        Llm.Claude.SubagentRunPassport run, int attempt)
    {
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
            "и {Seconds} с (контекст {Context} токенов) — добивание {Attempt}/{Max}, чат {SessionId}",
            run.AgentId, run.AgentType, run.LastTool, run.ToolUses, run.DurationSeconds,
            run.ContextTokens, attempt, MaxSubagentNudges, sessionId);
        await SendMessageAsync(sessionId, Prompts.SubagentPrompts.ResumeTruncated(run, attempt, MaxSubagentNudges),
            [], systemDirective: true, cause: DeliveryCause.SubagentNudge);
    }

    // Автопродолжение цикла «до готово»: вызывается по result хода, нёсшего протокол цикла.
    // Маркер найден → верификационный ход, затем стоп; нет → продолжение до лимита итераций.
    private async Task ContinueWorkLoopAsync(string sessionId)
    {
        if (!_sessions.TryGetValue(sessionId, out var entry)) return;
        if (entry.Info.WorkLoop is not { } loop) return;

        // Без двойной отправки (гонка «result → директива продолжения» vs «очередь доставляет
        // сообщение пользователя»): если пользователь успел прислать сообщение в этом ходе или
        // между итерациями, оно само продолжит цикл как следующая итерация (доставку выполнит
        // drain). Системную директиву продолжения/верификации в этом случае не шлём — иначе
        // два хода подряд ушли бы в один процесс. Проверка атомарна с извлечением drain'а:
        // LoopTurnInFlight=true означает, что drain уже вытащил пользовательское сообщение и
        // запуска итерацию. Маркер взводим ПОД ТЕМ ЖЕ PendingLock, что и гейт (Major 3): иначе
        // в окне до BuildCliTurnText (SaveSessions/Broadcast/EnsureProcess, сотни мс) параллельный
        // drain успевал вытащить пользовательское сообщение и пустить второй ход в тот же процесс.
        // До всей логики цикла (phase/iteration) — чтобы не оставлять изменённое состояние при уступке.
        lock (entry.PendingLock)
        {
            if (entry.Pending.Any(p => p.Kind == PendingKind.User)) return;
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
        if (IsStaleInteractionAnswer(sessionId, entry, $"вопрос {toolUseId}")) return;
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
                // Архивная копия транскрипта уходит вместе с чатом — иначе переписка
                // переживёт его и в data, и в бэкапе. Гейт тот же (общий csid у чата-двойника)
                _archivedTranscripts.Delete(csid);
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
            var removed = Llm.TranscriptMigrator.DeleteEverywhere(
                TranscriptSearchRoots(info), TryResolveCwd(info), claudeSessionId);
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
                    if (entry is not null) entry.TurnInWorktree = m.TurnWorktree != null;
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
                                var safe = TrimAmbiguousMarkerTail(
                                    TrimUnresolvedMarkerOpen(StripTeamProtocolMarkers(entry.TeamTurnText.ToString())));
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
                    acc.OnToolUse(m.Id, m.Name, m.Input, m.ParentToolUseId);
                    TryUnmarkCommittedOnToolUse(sessionId, entry, m.Name, m.Input);
                    break;
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
                // Присутствие фона — сигнал для СПИСКА чатов, а не для ленты: в историю не
                // пишем (состояние живёт ровно столько, сколько процесс) и статус сессии не
                // трогаем — ApplyStatusAsync двигал бы UpdatedAt, а по нему идут сортировка,
                // секции дерева и непрочитанность. Рассылка — в session + project/user-группы
                case BgAgentsPresenceMessage m:
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
                    _usage.Record(m.LimitType, m.Utilization, m.Status, m.IsUsingOverage, m.ResetsAt, m.OverageStatus, m.OverageResetsAt, subscriptionKey: entry?.Info.Provider, source: "turn");
                    _activity?.Touch(entry?.Info.Provider);
                    // P31: rate_limit_event от подписки — доказательство аутентификации (до лимитов
                    // запрос не дошёл бы). Снимаем auth-dead независимо от окна и исчерпания: иначе
                    // транзитный 401 + перевход (claude setup-token) выключали подписку до рестарта
                    // процесса, хотя токен уже починили (блокер ревью P29). По любому окну, не только
                    // exhaustion-окну: заголовки лимитов приходят с каждым ответом авторизованного API.
                    if (entry is not null && _subscriptionPool.IsAuthDead(entry.Info.Provider))
                    {
                        _subscriptionPool.ClearAuthDead(entry.Info.Provider);
                        Console.WriteLine($"[SessionManager] Подписка «{entry.Info.Provider}» отвечает (ход {sessionId}) — снята пометка auth-dead");
                    }
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
                            // M1: под фолбэк-оркестрацией ротацией владеет адаптер —
                            // помечать провайдер исчерпанным и переключать пул тут
                            // нельзя. Не только потому, что будет дубль provider_switched:
                            // поздний rate_limit от УЖЕ прерванной попытки придёт после
                            // ApplyTarget и Info.Provider уже сменён на здоровый — пометив
                            // его, мы загубим только что выбранную подписку. Провайдер
                            // этой попытки адаптер сам отметит в ResolveNextTarget.
                            if (entry.Process is FallbackLlmSessionAdapter fb && fb.FallbackTurnActive)
                                return;
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
                    // Сюда доезжают только настоящие провалы: промежуточную ошибку попытки,
                    // за которой пошла подмена, адаптер наружу не выпускает (её текст едет
                    // в ErrorDetails маркера) — значит и LoopTurnFailed на ней не взводится.
                    // Details — сырой техтекст под «Подробностями» карточки.
                    await acc.OnErrorAsync(m.Text, _history, m.Details);
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

        // Ход оборвался без result (прерывание, смерть процесса посреди ASK): HandleTeamTurnEndAsync
        // по такому ходу не зовётся, поэтому отсечки сторожа, погашенные вопросом, возвращаем здесь.
        // Для штатного хода это no-op: result уже отдал восстановление ему, повтор идемпотентен.
        if (entry is not null && msg is ExitedMessage && entry.Info.TeamImplement is not null)
            RestoreWaveWatchdogIfPaused(sessionId, entry);

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
            {
                // P12/P15: отметим момент, когда статус стал Active — sweep в SaveSessions по этой
                // метке доведёт до Finished, если exited прогона не придёт (доживающий прогон при
                // живых фоновых агентах до BgLingerTimeout, подавленный SuppressExited, висящий без
                // работы процесс). Любой иной статус (Working/Waiting — идёт ход/карточка; Finished/
                // Error — терминальный) — «завершённого Active» нет, метку гасим. Под PendingLock:
                // struct DateTimeOffset? читает sweep под тем же локом; lock короткий, без await.
                lock (entry.PendingLock)
                    entry.LastTurnEndedAt = newStatus == SessionStatus.Active ? DateTimeOffset.UtcNow : null;
                await ApplyStatusAsync(sessionId, entry, newStatus.Value);
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
                    var finalSafe = StripTeamProtocolMarkers(turnText);
                    // Тот же гард, что в живой трансляции: ход, ответивший ровно маркером,
                    // не должен догнать ленту пробелами вокруг вырезанного маркера.
                    catchUpDelta = finalSafe.Length > entry.TeamTurnShownLength && finalSafe.Trim().Length > 0
                        ? finalSafe[entry.TeamTurnShownLength..] : null;
                    entry.TeamTurnShownLength = 0;
                    entry.TeamTurnText.Clear();
                    entry.TurnSawAngleBracket = false;
                    turnAsked = entry.TeamTurnAsked;
                    entry.TeamTurnAsked = false;
                }
                if (!string.IsNullOrEmpty(catchUpDelta))
                    await BroadcastAsync(sessionId, new TextDeltaMessage(catchUpDelta));
            }

            if (msg is ResultMessage or ErrorMessage && entry.Info.TeamImplement is not null)
            {
                // Пара ErrorMessage(ExpectResultFollows)+ResultMessage одного хода (см. комментарий
                // у SessionEntry.SkipNextTeamTurnEnd) — обработали по ErrorMessage, спаренный
                // ResultMessage только гасит флаг и второй раз ход не разбирает. Между ними может
                // приехать ещё одна ошибка — итоговый текст исчерпания цепочки (FailExhaustedAsync
                // шлёт «причина попытки» → «вердикт» → result): её тоже глушим, конец хода у него
                // один. Флаг снимает только терминальный result, иначе он утёк бы в следующий ход.
                if (entry.SkipNextTeamTurnEnd)
                {
                    if (msg is ResultMessage) entry.SkipNextTeamTurnEnd = false;
                }
                else
                {
                    if (msg is ErrorMessage { ExpectResultFollows: true }) entry.SkipNextTeamTurnEnd = true;
                    var teamTurnText = turnText;
                    var teamTurnAsked = turnAsked;
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
                        HasPending(entry), entry.LoopTurnInFlight && entry.Info.WorkLoop is not null))
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
                    && (entry.Info.WorkLoop is null || HasUserPending(entry))))
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
