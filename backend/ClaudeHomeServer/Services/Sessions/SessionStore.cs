using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services.Composition;
using ClaudeHomeServer.Services.Llm;
using ClaudeHomeServer.Services.Team;

namespace ClaudeHomeServer.Services.Sessions;

// Результат загрузки sessions.json: id прерванных посреди хода транскриптов (маркер обрыва
// пишет SessionManager — у стора нет доступа к истории) и кандидаты на auto-resume.
internal readonly record struct SessionLoadResult(
    IReadOnlyList<string> AbortedMidTurn, IReadOnlyList<string> RestartResumeCandidates);

// Стор метаданных сессий (data/sessions.json): реестр записей в памяти, сохранение с дебаунсом
// и sweep зависших Active, выборки, владелец и видимость, актор хода, наблюдатели, рассылка
// статуса. Единственная точка правды владения и видимости (per-owner изоляция). Вынесен из
// SessionManager (S3-1, docs/research/session-manager-split-2026-11.md): тела методов перенесены
// дословно, SessionManager держит делегирующий фасад. Реализует ITeamRunState — рантайм-поля
// записи и транзакцию над состоянием режима штаба.
// Зависимостей четыре (config, projects, broadcaster, taskLookup): IServiceProvider и ленивых
// швов здесь нет, а проверка прав через IProjectAccess остаётся в фасаде SessionManager (методы
// чтения «с правом» и «с областью запроса» — композиция GetById и IProjectAccess).
// Класс public ТОЛЬКО потому, что он параметр публичного конструктора SessionManager (CS0051) и
// DI нужен публичный конструктор; обход фасада закрывает сторож формы ядра (правило 4:
// на типы Services.Sessions ссылаются лишь SessionManager, сами Services.Sessions и тесты).
public sealed class SessionStore : ITeamRunState, Access.ITurnFrameSource, Access.ITurnActorRegistry, IDisposable
{
    // Реестр сессий: одна точка правды на процесс (синглтон в DI). SessionManager держит ссылку
    // на этот же словарь в своём поле _sessions — тесты читают его рефлексией.
    internal readonly ConcurrentDictionary<string, SessionEntry> Sessions = new();

    private readonly ProjectManager _projects;
    // Шов Ф4 (Этап 5): заменяет _hub.Clients.Group(...).SendAsync — префиксы
    // собираются внутри SessionHubBroadcaster, а не в вызывающем коде.
    private readonly ISessionBroadcaster _broadcaster;
    private readonly ILogger<SessionStore> _log;
    private readonly string _sessionsFilePath;

    // Реестр задач (опционально, в тестах не передаётся): единственная точка вычисления
    // вычисляемых «связей» сессии-чата (SessionTaskLinks.ParentSessionId / IsTaskDone).
    // TaskManager не зависит от SessionManager (его ctor: config/лог/уведомления/персоны),
    // поэтому прямой ссылки DI-цикла не возникает. Храним Core-шов ITaskLookup (адаптер
    // строится один раз), чтобы сам SessionManager не тянул конкретный TaskManager.
    // null — задача не резолвится (та же семантика, что «резолвер не установлен»:
    // ParentSessionId=null, TaskDone=false).
    // Сеттер — тест-шов: TaskManager в конструктор SessionManager не пробрасывают
    // (DI-цикл), поэтому юниты, которым нужен резолв задачи, подменяют lookup явно.
    internal ITaskLookup? TaskLookup { get; set; }

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

    // Enum (в т.ч. ClaudeMode) сериализуем строками — устойчиво к изменению порядка значений.
    // При чтении конвертер принимает и старый числовой формат.
    private static readonly JsonSerializerOptions _jsonOpts = new()
    {
        Converters = { new JsonStringEnumConverter() },
    };

    // Лок сохранения sessions.json. Берут и вызывающие снаружи (пометки «зафиксировано в git» в
    // SessionManager делают read-modify-write под ним же), поэтому лок один на процесс.
    private readonly Lock _saveLock = new();
    internal Lock SaveLock => _saveLock;

    // Автосохранение сессий каждые 30с
    /// <summary>
    /// Период автосохранения (оно же — единственный фоновый триггер sweep-terminus, см. SaveSessions).
    /// Настраивается ключом <c>Session:AutoSaveSeconds</c>; 0 и меньше — таймер не заводится вовсе.
    ///
    /// Ноль нужен ТЕСТАМ, и не ради скорости: sweep живёт внутри SaveSessions, поэтому фоновый
    /// таймер выполняет его в произвольный момент — в том числе между двумя ассертами теста.
    /// Когда sweep ещё читал статический Session.TaskSourceSessionResolver (конструктор каждого
    /// нового TaskManager перезаписывал его под параллельными классами), это давало плавающее
    /// падение Sweep_ЖивойПотомокВГлубину: иерархия делегирования на миг переставала резолвиться,
    /// и sweep закрывал сессию, которую тест только что проверил живой. Поодиночке ни один из двух
    /// факторов не воспроизводился — падало только на полном прогоне и не каждый раз.
    /// </summary>
    private static readonly TimeSpan DefaultAutoSaveInterval = TimeSpan.FromSeconds(30);
    private readonly TimeSpan _autoSaveInterval;
    private Timer? _autoSaveTimer;
    // Дебаунс лёгких мутаций (MarkRead): не писать sessions.json синхронно в запросе, а
    // скопить изменения и записать один раз с короткой задержкой. Активен, когда включён
    // автосейв (Session:AutoSaveSeconds > 0) — тот же гейт, что у _autoSaveTimer: в тестах
    // (AutoSaveSeconds=0) отметка пишется синхронно, и фоновый таймер не гонит sweep-terminus
    // между ассертами. Задержка — Session:MarkReadDebounceSeconds (дефолт 1с).
    private readonly object _debounceLock = new();
    private Timer? _pendingSaveTimer;
    private readonly bool _debounceEnabled;
    private readonly TimeSpan _debounceDelay;

    /// <summary>Автосохранение включено (Session:AutoSaveSeconds &gt; 0); от него зависят и другие фоновые таймеры ядра.</summary>
    internal bool AutoSaveEnabled => _autoSaveInterval > TimeSpan.Zero;

    // Лок-словарь штаба по sessionId: единственный держатель блокировки для мутаций
    // SessionTeamImplement (WithTeamState). Инициализируется по требованию, GC подберёт объект,
    // когда режим выключат и ссылок не останется.
    private readonly ConcurrentDictionary<string, object> _teamLocks = new();

    public SessionStore(IConfiguration config, ProjectManager projects, ISessionBroadcaster broadcaster,
        ILogger<SessionStore> log, ITaskLookup? taskLookup = null)
    {
        _projects = projects;
        _broadcaster = broadcaster;
        _log = log;
        TaskLookup = taskLookup;

        // Корень берётся через GetFullPath (тот же приём, что у Program.cs и NotesService):
        // относительный DataPath отдал бы в deny-правило CLI относительный корень, а «//…» в
        // синтаксисе правил — абсолютный путь (ревью S1c, Н5).
        var dataDir = Path.GetDirectoryName(
            Path.GetFullPath(config["DataPath"] ?? Path.Combine(AppContext.BaseDirectory, "data", "projects.json")))
            ?? Path.Combine(AppContext.BaseDirectory, "data");
        _sessionsFilePath = Path.Combine(dataDir, "sessions.json");

        // Sweep-terminus grace (P12/P15): потолок ожидания exited после result до принудительного
        // Active→Finished. <=0 — выключить sweep (только для тестов/отладки).
        _stuckActiveGraceSeconds = int.TryParse(config["Session:StuckActiveGraceSeconds"], out var grace)
            && grace >= 0 ? grace : DefaultStuckActiveGraceSeconds;

        _autoSaveInterval = int.TryParse(config["Session:AutoSaveSeconds"], out var secs)
            ? TimeSpan.FromSeconds(secs)
            : DefaultAutoSaveInterval;
        // Дебаунс лёгких мутаций: тот же гейт autoSave > 0, что у автосейва (см. поле).
        // Задержка — Session:MarkReadDebounceSeconds (дефолт 1с).
        _debounceEnabled = _autoSaveInterval > TimeSpan.Zero;
        _debounceDelay = TimeSpan.FromSeconds(
            double.TryParse(config["Session:MarkReadDebounceSeconds"], out var debounceSecs) ? debounceSecs : 1);
    }

    // Автосохранение: периодический сброс in-memory данных на диск. Запускается ПОСЛЕ Load
    // (порядок старта не меняется: LoadSessions до таймеров).
    internal void StartAutoSave()
    {
        if (_autoSaveInterval > TimeSpan.Zero)
            _autoSaveTimer = new Timer(_ => SaveSessions(), null, _autoSaveInterval, _autoSaveInterval);
    }

    // Гашение автосейва идемпотентно: поле снимается атомарно, повторный вызов — no-op.
    // Поэтому его зовут и фасад (KillAllProcesses/Dispose), и Dispose стора ниже — дубль
    // безопасен, второй путь ничего не находит.
    internal void StopAutoSave() => Interlocked.Exchange(ref _autoSaveTimer, null)?.Dispose();

    // IDisposable: гасит оба фоновых таймера — автосейв (StopAutoSave) и one-shot дебаунса
    // MarkRead (FlushPendingSave, с синхронным сбросом висящей записи). Стор — синглтон DI:
    // контейнер диспозит его после ApplicationStopping, и без этого гашения таймеры жили бы
    // в процессе сами по себе (фасад гасил их только в своём cleanup-пути). Порядок тот же,
    // что у фасада: автосейв → дебаунс. Идемпотентен: оба метода повторны безопасны, поэтому
    // гашение фасада и диспоз контейнера не конфликтуют — второй путь no-op.
    public void Dispose()
    {
        StopAutoSave();
        FlushPendingSave();
        GC.SuppressFinalize(this);
    }

    // --- Зрители ---

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
        Sessions.TryGetValue(sessionId, out var entry)
        && entry.Process is { HasLiveTurn: true } or { HasQueuedTurn: true };

    // --- Загрузка и сохранение ---

    internal SessionLoadResult Load(Action<SessionEntry> onLoaded)
    {
        var list = JsonFileStore.Load<List<Session>>(_sessionsFilePath, _jsonOpts);
        if (list is null) return new SessionLoadResult([], []);
        List<string> abortedMidTurn = [];
        List<string> restartResumeCandidates = [];
        // Залипший Working (замечание Веры, ADR-033): живой статус (Working/Starting/Waiting)
        // переводим в Orphaned только в памяти; на диск это попадёт лишь при следующем
        // SaveSessions. Если активности по чату нет, файл держит Working, и при следующем
        // старте с включённым флагом этот давно оборванный ход снова станет кандидатом — ломает
        // правило «только ходы последнего рестарта». Помечаем, чтобы сохранить один раз.
        var anyLiveOrphaned = false;
        foreach (var session in list)
        {
            // Процесс умер при рестарте — "живые" статусы переводим в orphaned
            var originalStatus = session.Status;
            var wasLive = originalStatus is SessionStatus.Working or SessionStatus.Waiting;
            if (originalStatus is SessionStatus.Working or SessionStatus.Starting or SessionStatus.Waiting)
                anyLiveOrphaned = true;
            session.Status = originalStatus switch
            {
                SessionStatus.Working or SessionStatus.Starting or SessionStatus.Waiting
                    => SessionStatus.Orphaned,
                SessionStatus.Active => SessionStatus.Finished,
                _ => originalStatus,
            };
            if (wasLive && session.ClaudeSessionId is not null)
                abortedMidTurn.Add(session.ClaudeSessionId);
            // Кандидат на auto-resume — исходный Working из sessions.json: ход шёл,
            // result не пришёл. Waiting/Starting/Orphaned не кандидаты: Waiting — ход
            // ещё не шёл, Starting — CLI ещё не стартовал, Orphaned — уже был оборван
            // раньше («только последний рестарт»).
            if (originalStatus == SessionStatus.Working)
                restartResumeCandidates.Add(session.Id);
            var restoredEntry = new SessionEntry { Info = session };
            // Восстановление очереди отложенных сообщений — колбэк сборки (пока это SessionManager)
            onLoaded(restoredEntry);
            Sessions[session.Id] = restoredEntry;
        }
        // Значок темы переехал из имени в Session.Topic — переносим старые эмодзи-имена.
        // Идемпотентно: на уже перенесённых сессиях это no-op. Один SaveSessions покрывает
        // обе миграции: перенос темы + сохранение Orphaned (залипший Working, см. выше).
        var migratedTopics = Llm.ChatTopicMigration.Apply(list);
        var migratedModes = MigratePersonaChatMode(list);
        if (migratedModes > 0)
            Console.WriteLine($"[SessionManager] Миграция режима: {migratedModes} чатов персон переведены из Auto в AcceptEdits");
        if (migratedTopics || migratedModes > 0 || anyLiveOrphaned) SaveSessions();
        return new SessionLoadResult(abortedMidTurn, restartResumeCandidates);
    }

    // Чаты одной персоны в Auto гоняют классификатор разрешений на каждый вызов инструмента
    // (отдельный запрос к модели чата — до минуты на медленных прокси). Решение владельца:
    // персоны работают в AcceptEdits. Идемпотентно; UpdatedAt не двигаем (сортировка/архив).
    // Групповые чаты и чаты штаба (режим навязывает TeamImplement) не трогаем.
    internal static int MigratePersonaChatMode(IEnumerable<Session> sessions)
    {
        var migrated = 0;
        foreach (var s in sessions)
        {
            if (s.PersonaId is null || s.Mode != ClaudeMode.Auto) continue;
            if (s.Participants is { Count: > 1 } || s.TeamImplement is not null) continue;
            s.Mode = ClaudeMode.AcceptEdits;
            migrated++;
        }
        return migrated;
    }

    // Счётчик вызовов SaveSessions — для тестов «пачка пишет файл один раз» (ApplySubjects)
    private int _saveSessionsCalls;
    internal int SaveSessionsCalls => Volatile.Read(ref _saveSessionsCalls);

    // Дебаунс лёгких мутаций (MarkRead): не писать sessions.json синхронно в запросе.
    // Быстрые повторные отметки (открытие чата на нескольких устройствах) сливаются в одну
    // запись: первый вызов заводит one-shot таймер, следующие в окне задержки его не трогают.
    // Таймер живёт, пока включён автосейв; в тестах (AutoSaveSeconds=0) — синхронная запись,
    // как раньше. Запись сама идёт под _saveLock (SaveSessions), поэтому параллельность
    // с автосейвом/другими мутациями безопасна.
    internal void ScheduleDebouncedSave()
    {
        if (!_debounceEnabled) { SaveSessions(); return; }
        lock (_debounceLock)
        {
            if (_pendingSaveTimer is not null) return;
            Timer? timer = null;
            timer = new Timer(_ =>
            {
                // Гасим поле только если оно указывает на ЭТОТ таймер. В окне между
                // срабатыванием и взятием замка FlushPendingSave мог уже снять этот таймер
                // (и новый MarkRead завёл свежий): безусловная очистка затёрла бы чужой.
                lock (_debounceLock)
                    if (ReferenceEquals(_pendingSaveTimer, timer)) _pendingSaveTimer = null;
                // Сработавший one-shot таймер освобождает себя: без Dispose он держал бы
                // регистрацию в thread pool до конца процесса.
                timer?.Dispose();
                SaveSessions();
            }, null, _debounceDelay, Timeout.InfiniteTimeSpan);
            _pendingSaveTimer = timer;
        }
    }

    // Синхронный сброс висящей записи (MarkRead-дебаунс) при остановке: штатный shutdown
    // = перезапуск, и отложенная запись, если не сбросить её, потеряется (one-shot таймер
    // Dispose'ится, не успев сработать). Вызывается из KillAllProcesses/Dispose ДО убийства
    // процессов: SaveSessions() завершится на том же потоке, без гонки с диспозом адаптеров.
    // Idempotent: после первого сброса _pendingSaveTimer = null, повторный вызов — no-op.
    internal void FlushPendingSave()
    {
        Timer? pending;
        lock (_debounceLock)
        {
            pending = _pendingSaveTimer;
            _pendingSaveTimer = null;
            pending?.Dispose();
        }
        if (pending is not null) SaveSessions();
    }

    /// <summary>Всего зарегистрированных сессий (для OTel gauge). ConcurrentDictionary.Count — thread-safe, sub-ms.</summary>
    internal int ActiveCount => Sessions.Count;

    // Публичный (волна В): TeamPlanService сохраняет каталог сессий после правки PlanCardId/
    // PlanVersion/Replanning — через тот же канал, что и TeamWaveService.
    public void SaveSessions()
    {
        Interlocked.Increment(ref _saveSessionsCalls);
        lock (_saveLock)
        {
            try
            {
                var sessions = Sessions.Values.Select(e => e.Info).ToList();
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
            if (!Sessions.Values.Any(e => e.Info.Status == SessionStatus.Active && e.LastTurnEndedAt.HasValue))
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
            foreach (var e in Sessions.Values)
            {
                if (!HasLiveWork(e)) continue;
                var cur = ParentSourceSession(e.Info.Id);
                for (var steps = 0; cur is not null && steps < 256; steps++)
                {
                    if (!protectedByLiveSubtree.Add(cur)) break; // уже отмечен — цикл или общий предок
                    cur = ParentSourceSession(cur);
                }
            }

            foreach (var entry in Sessions.Values)
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
        // «Штаб занят» = идёт планирование: то же чтение, что у ITeamNotifier.IsSessionBusy
        // (стору шов штаба не нужен — признак лежит на самой записи).
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
            // Доводка: содержимого за ней нет — прочитанный чат не должен стать непрочитанным
            try { await ApplyStatusAsync(sid, entry, SessionStatus.Finished, touchUpdatedAt: false); }
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
        if (!Sessions.TryGetValue(sessionId, out var entry)) return null;
        return entry.Info.TaskId is { } tid
            ? TaskLookup?.GetById(tid)?.SourceSessionId
            : null;
    }

    // Эффективный родительский чат сессии (SessionTaskLinks): ручная группировка, иначе чат,
    // в котором была создана её задача. Единственный вход для вертикалей, у которых нет
    // собственного TaskManager (TeamTurnCompletionService/TeamBudgetService), а есть SessionManager.
    internal string? EffectiveParentSessionId(Session s) => SessionTaskLinks.ParentSessionId(s, TaskLookup);

    // --- Выборки ---

    public IReadOnlyCollection<Session> GetByProject(string projectId) =>
        Sessions.Values
            .Where(e => e.Info.ProjectId == projectId)
            .Select(e => e.Info)
            .OrderByDescending(s => s.UpdatedAt)
            .ToList();

    // Чаты проекта, видимые человеку (S2d-4a): GetByProject + VisibleTo — список человеческой
    // ручки GET /api/projects/{id}/sessions. Чужой приватный чат не отдаётся никому, кроме
    // автора (и автору он отдаётся как раньше: приватность — его поле, не чужой запрет).
    // GetByProject — сырая выборка проекта: нужен ли ей фильтр видимости, решает не тип
    // вызывающего, а то, кому уходит результат (вердикт R-S2d-4a). Человеку она уходит через
    // производный индекс файлов и там фильтруется на его границе: панель «Изменения»
    // (ProjectFileSessionsIndex.GetForProjectAsync → FilesController.ChangedBy, IsVisibleTo на
    // контроллере, доработка S2d-4a). Машинные потребители остаются на сырой выборке: сырые
    // пути индекса (GetSessionPathsAsync → CommitAttributionService), уборка worktree при
    // удалении проекта (ProjectsController), выкидывание групп хаба при отзыве участника
    // (ProjectMembershipService), резолв worktree-корня по пути (CodeGraphController), тулсеты
    // (WorkspaceToolset: chats_list, теги сессии), сторож тем (ChatSubjectSweeper — его выход
    // за видимость чата уже отдельный дефект).
    public IReadOnlyList<Session> GetVisibleByProject(string projectId, string userId) =>
        GetByProject(projectId).Where(s => VisibleTo(s, userId)).ToList();

    // Сессии владельца с ЖИВЫМИ фоновыми агентами — снимок для холодного старта списка чатов
    // (событие bg_agents_presence клиент мог пропустить, пока не вошёл в группы). Состояния
    // не держим: источник истины — сам прогон, HasPendingBg берёт свой лок на мгновение.
    // Сначала фильтр по фону (живой фон — редкость), потом резолв владельца и видимость
    // (S2d-4a): id чужого приватного чата в снимке человека нет — тот же фильтр, что у
    // GetVisibleByProject и GetAllOwnedVisibleBy.
    public IReadOnlyList<string> GetSessionsWithLiveAgents(string ownerId) =>
        Sessions.Values
            .Where(e => e.Process is { HasTrackedBg: true } && ResolveOwnerId(e.Info) == ownerId
                && VisibleTo(e.Info, ownerId))
            .Select(e => e.Info.Id)
            .ToList();

    // Та же выборка для фоновых КОМАНД (Bash с run_in_background): у них свой, тихий значок в
    // списке чатов. Отдельный метод, а не флаг в предыдущем: наборы пересекаются (в чате может
    // идти и агент, и дев-сервер), и клиенту нужны оба списка целиком.
    public IReadOnlyList<string> GetSessionsWithLiveBgCommands(string ownerId) =>
        Sessions.Values
            .Where(e => e.Process is { HasTrackedCommandBg: true } && ResolveOwnerId(e.Info) == ownerId
                && VisibleTo(e.Info, ownerId))
            .Select(e => e.Info.Id)
            .ToList();

    // Число сессий проекта — для карточки проекта (без аллокации списка)
    public int CountByProject(string projectId) =>
        Sessions.Values.Count(e => e.Info.ProjectId == projectId);

    // Чаты вне проекта, принадлежащие пользователю (для вкладки «Чаты»)
    public IReadOnlyCollection<Session> GetProjectlessChats(string ownerId) =>
        Sessions.Values
            .Where(e => e.Info.ProjectId == null && e.Info.OwnerId == ownerId)
            .Select(e => e.Info)
            .OrderByDescending(s => s.UpdatedAt)
            .ToList();

    // Все сессии (для планировщика авто-удаления временных чатов)
    public IReadOnlyCollection<Session> GetAll() =>
        Sessions.Values.Select(e => e.Info).ToList();

    public Session? GetById(string id) =>
        Sessions.TryGetValue(id, out var entry) ? entry.Info : null;

    // Владелец сессии: у чата — OwnerId, у проектной — владелец проекта
    // Единая точка резолва владельца сессии: у проектной — владелец проекта, у чата вне
    // проекта — сама сессия. Единственный источник истины для хаба и контроллеров тоже.
    public string? ResolveOwnerId(Session s) =>
        s.ProjectId is not null ? _projects.GetById(s.ProjectId)?.OwnerId : s.OwnerId;

    // Видимость сессии человеку — Core-точка SessionAccess.CanSee (та же, что у GetOwnedVisible
    // и шлюзов редакторов S2d-1d): приватный проектный чат не виден даже владельцу проекта
    // (решение 28.10). Проект резолвится по сессии: осиротевший проектный чат не виден
    // (fail-closed по контракту CanSee).
    internal bool VisibleTo(Session s, string userId) =>
        Access.SessionAccess.CanSee(s, s.ProjectId is not null ? _projects.GetById(s.ProjectId) : null, userId);

    // Та же видимость по id чата — для человеческих границ, которые держат список чатов не сами,
    // а через производный индекс (панель «Изменения»: FilesController.ChangedBy, доработка S2d-4a).
    // Нет такого чата или он осиротел (проект удалён) — нет (fail-closed по контракту CanSee).
    public bool IsVisibleTo(string sessionId, string? userId) =>
        Sessions.TryGetValue(sessionId, out var entry) && userId is not null && VisibleTo(entry.Info, userId);

    // Занят ли ключ транскрипта (ClaudeSessionId) чатом, не видимым человеку (FIN3-2, FINAL-3 п. 3):
    // такой чат есть, и ни один чат с этим ключом человеку не виден. Неизвестный ключ — нет:
    // продолжение чата (ветвление) подаёт свой новый csid до регистрации чата (CreateBranchAsync),
    // внешний транскрипт — законный resume. Проверка глобальная, не по проекту: ключ истории
    // data/sessions/{csid} глобален, приватный чат вне проекта — тот же чужой транскрипт.
    // Несколько чатов на одном транскрипте (старый resume-поток): достаточно одного видимого —
    // человек уже держит этот разговор.
    public bool IsTranscriptReservedForOthers(string csid, string userId) =>
        Sessions.Values.Any(e => e.Info.ClaudeSessionId == csid)
        && !Sessions.Values.Any(e => e.Info.ClaudeSessionId == csid && VisibleTo(e.Info, userId));

    // Есть ли у пользователя хоть одна сессия/чат — среда исполнения меняется только «начисто»
    // (корни проектов и профили сред различаются, resume привязан к путям старой среды)
    public bool HasSessionsOwnedBy(string ownerId) =>
        Sessions.Values.Any(e => ResolveOwnerId(e.Info) == ownerId);

    // Все сессии пользователя (проектные + чаты) — МАШИННАЯ точка: индекс фич и гард
    // (FeatureChatDoor), тулсеты. Чужой приватный чат здесь живёт законно: машинный потребитель
    // работает с ним через арендатора. Человеческим спискам нужен GetAllOwnedVisibleBy (S2d-4a).
    public IReadOnlyCollection<Session> GetAllOwnedBy(string ownerId) =>
        Sessions.Values
            .Select(e => e.Info)
            .Where(s => ResolveOwnerId(s) == ownerId)
            .ToList();

    // Все видимые человеку сессии (проектные + чаты) — список для человеческих ручек (S2d-4a):
    // GetAllOwnedBy + VisibleTo. Чужой приватный чат не отдаётся ни id, ни названием — он
    // неотличим от несуществующего, как на одиночных ручках S2d-3. Владение не меняется:
    // резолв тот же ResolveOwnerId, фильтр добавляет только приватность чата.
    public IReadOnlyCollection<Session> GetAllOwnedVisibleBy(string userId) =>
        Sessions.Values
            .Select(e => e.Info)
            .Where(s => ResolveOwnerId(s) == userId && VisibleTo(s, userId))
            .ToList();

    // Сессия, принадлежащая пользователю (и проектная, и чат вне проекта) — для
    // эндпоинтов, работающих с любым типом сессии (участники группы, совещания).
    public Session? GetOwned(string sessionId, string ownerId)
    {
        var s = GetById(sessionId);
        return s is not null && ResolveOwnerId(s) == ownerId ? s : null;
    }

    // Сессия, принадлежащая человеку И видимая ему (S2d-3a): GetOwned + SessionAccess.CanSee.
    // Приватный проектный чат не виден даже владельцу проекта (решение 28.10): на чужом
    // приватном чате владелец получает отказ, неотличимый от «нет такого чата». GetOwned
    // это не заменяет: машинные пути (тулсеты своей сессии-вызывателя, дайджест,
    // продолжение, сторожа) законно работают с приватным чатом через арендатора — без них
    // сломался бы сам приватный чат. CanSee зовётся напрямую, как у шлюзов редакторов
    // (S2d-1d): арендатор здесь уже установлен GetOwned, проверка добавляет только
    // приватность чата — это не решение о праве на проект (роль и рубильник неприменимы:
    // вызывающий и есть арендатор).
    public Session? GetOwnedVisible(string sessionId, string userId)
    {
        var s = GetOwned(sessionId, userId);
        if (s is null) return null;
        var project = s.ProjectId is not null ? _projects.GetById(s.ProjectId) : null;
        return Access.SessionAccess.CanSee(s, project, userId) ? s : null;
    }

    // Признак «чат приватный» (QA-D45): нужен контроллеру для ПОРЯДКА отказов — 409 режима
    // (private_chat_dossiers) идёт раньше авторского 403, ничего не записывая. Читается под тем
    // же VisibilityLock, что TrySetExcludeFromDossiers и SetChatVisibility: чтение до замка
    // прочитало бы «не приватный» в запись, уже сделанную тумблером из соседней вкладки.
    public bool IsPrivateChat(string sessionId)
    {
        if (!Sessions.TryGetValue(sessionId, out var entry)) return false;
        lock (entry.VisibilityLock) return entry.Info.PrivateToUserId is not null;
    }

    // Отметка «чат принял сообщение» (S2d-7a). Зовётся первой строкой всех входов, из которых
    // сообщение попадает в ленту чата и рассылается по ToChatAudience (SendMessageAsync,
    // SendMessageAndWaitAsync, EnqueuePendingAsync). Под тем же замком, что и проверка
    // «сообщений нет» в SetChatVisibilityAsync: либо отметка встала раньше — и тумблер видит
    // chat_has_messages, либо тумблер успел первым — и адресация сообщения (читается позже,
    // в момент рассылки) уже приватная. Третьего исхода нет.
    // Отметка хранится в Session (переживает рестарт: очередь отложенных и записи ленты — на диске),
    // поэтому первое занятие чата сохраняет sessions.json. Зовётся ещё и из SendDirectAsync и
    // AppendStoredAsync: доставка очереди после рестарта и карточки ленты идут мимо трёх входов выше.
    internal void ClaimFirstMessage(SessionEntry entry)
    {
        bool first;
        lock (entry.VisibilityLock)
        {
            first = entry.Info.MessageClaimed != true;
            entry.Info.MessageClaimed = true;
        }
        if (first) SaveSessions();
    }

    // --- Делегирование и актор хода ---

    // Состояние делегирования ИДУЩЕГО хода сессии. Спрашивает DenyOnDelegatedTurnAttribute по
    // заголовку MCP-сервера: единственный достоверный источник — живой адаптер, тогда как
    // заголовок/env запекаются при старте процесса и протухают при переиспользовании прогона.
    // Чужая сессия или отсутствие процесса — «обычный ход» (запрет не применяется).
    // Владение проверяем ТОЛЬКО через GetOwned (внутри — ResolveOwnerId): у проектной сессии
    // Session.OwnerId равен null, владелец живёт у проекта. Прямое сравнение с этим полем молча
    // отключало запрет, и делегированный ход спокойно запускал исполнителя — поймано live-тестом,
    // юнит-тесты такое не видят. Один способ резолва владельца на весь класс.
    // Признак хода складывается с делегированностью прогона (SessionEntry.RunDelegation):
    // фоновый агент делегированного хода после его result остаётся делегированным, пока он
    // жив и пока не подаст ход человек. RunOnly — делегированность держится только прогоном, а
    // текущий ход сам по себе глубины 0 (на этой «липкой» части разрешён отчёт наверх).
    public TurnDelegationState GetActiveTurnDelegation(string sessionId, string ownerId)
    {
        if (GetOwned(sessionId, ownerId) is null || !Sessions.TryGetValue(sessionId, out var entry))
            return new TurnDelegationState(0, false);
        var adapter = entry.Process;
        // Ход в полёте (подан и не дошёл до терминала): его собственный признак — от подачи
        var inFlight = entry.Info.Status is SessionStatus.Working or SessionStatus.Waiting;
        TurnDelegationState run, submit;
        lock (entry.PendingLock)
        {
            if (!inFlight && entry.RunDelegation != default && !RunAgentsAlive(adapter))
                entry.RunDelegation = entry.SubmitDelegation = default;
            run = entry.RunDelegation;
            submit = inFlight ? entry.SubmitDelegation : default;
        }
        var own = new TurnDelegationState(
            Math.Max(adapter?.CurrentTurnAgentDepth ?? 0, submit.AgentDepth),
            (adapter?.CurrentTurnSuppressTasksExecute ?? false) || submit.ExecutorSuppressed);
        return new TurnDelegationState(Math.Max(own.AgentDepth, run.AgentDepth),
            own.ExecutorSuppressed || run.ExecutorSuppressed,
            RunOnly: own.AgentDepth == 0 && run.AgentDepth >= 1);
    }

    // Живы ли фоновые агенты прогона. Адаптера нет — узнать нечем: считаем живыми (fail-closed)
    private static bool RunAgentsAlive(ILlmSessionAdapter? adapter) => adapter is null || adapter.HasLiveBgAgents;

    // Подача хода в процесс отмечает делегированность прогона ДО передачи адаптеру: фоновые
    // агенты этого хода не должны застать окно, где он ещё «человеческий». Подача человека
    // (не авто, не директива, глубина 0, не доклад) прогон сбрасывает — с неё начинается
    // новая ответственность. Любая другая подача добавляет свой признак к прогону, если у
    // того ещё живы фоновые агенты (авто-ход глубины 0 их делегированность не обнуляет), а
    // без живых агентов прошлое забывается — действует только признак этой подачи.
    internal static void NoteRunDelegation(SessionEntry entry, int agentDepth, bool suppressTasksExecute,
        bool fromHuman)
    {
        var human = fromHuman && agentDepth == 0 && !suppressTasksExecute;
        var keep = !human && RunAgentsAlive(entry.Process);
        lock (entry.PendingLock)
        {
            var prev = keep ? entry.RunDelegation : default;
            entry.SubmitDelegation = human ? default : new TurnDelegationState(agentDepth, suppressTasksExecute);
            entry.RunDelegation = human
                ? default
                : new TurnDelegationState(Math.Max(prev.AgentDepth, agentDepth),
                    prev.ExecutorSuppressed || suppressTasksExecute);
        }
    }

    // Актор хода (S2a-1). Пишется ТОЛЬКО в двух точках подачи (SendDirectAsync, SendMessageAndWaitAsync),
    // вызывающие передают его параметром senderUserId (по образцу senderPersonaId): запись в самих
    // вызывающих оставила бы устаревшего актора на каждом пропуске. Запись безусловная: каждая подача
    // перезаписывает оба поля, устаревший актор её не переживает; под PendingLock (читают потоки MCP-вызовов). Подача без инициатора
    // (null) у внутренних ходов (дренаж, WorkLoop, nudge, resume) наследует «липкого» актора прогона,
    // пока живы его фоновые агенты; у остальных пишется UnknownActor (fail-closed).
    internal const string UnknownActor = "";

    // Ранг сужения: неизвестный уже арендатора, арендатор — самый широкий. Тот, кто уже, выигрывает.
    private static int ActorRank(string? actor, string? tenantOwnerId) =>
        actor is null ? 3
        : actor == UnknownActor ? 0
        : actor == tenantOwnerId ? 2
        : 1;

    private static string? NarrowerActor(string? prev, string? next, string? tenantOwnerId) =>
        prev is null ? next
        : next is null ? prev
        : ActorRank(next, tenantOwnerId) < ActorRank(prev, tenantOwnerId) ? next : prev;

    // Человек-автор реплики для истории (StoredUserMessage.SenderUserId). Параметр подачи senderUserId —
    // инициатор хода (актор), а не автор: у агентской реплики (глубина ≥ 1 или задана персона-отправитель)
    // автора-человека нет, инициатор живёт в реестре актора (NoteTurnActor), в историю не пишется.
    internal static string? HumanAuthorOf(string? senderUserId, int agentDepth, string? senderPersonaId) =>
        agentDepth >= 1 || senderPersonaId is not null ? null : senderUserId;

    internal void NoteTurnActor(SessionEntry entry, string? actor, bool fromHuman)
    {
        var tenant = ResolveOwnerId(entry.Info);
        // Сужение побеждает и на человеческой подаче: пока живы фоновые агенты участника, актор
        // арендатора прогон не расширяет (1:1 с NoteRunDelegation, одинаково для хаба и REST)
        var keep = RunAgentsAlive(entry.Process);
        lock (entry.PendingLock)
        {
            var prev = keep ? entry.RunActor : null;
            var effective = !string.IsNullOrEmpty(actor) ? actor
                : !fromHuman && prev is not null ? prev
                : UnknownActor;
            entry.SubmitActor = effective;
            entry.RunActor = NarrowerActor(prev, effective, tenant);
        }
    }

    // Реестр актора хода (S2a-6b): с S3-6 запись расхода вызывает его через этот шов
    // (TurnCostLedger.RecordTurnSpend → _store как ITurnActorRegistry) — раньше реестр
    // передавался как сам SessionManager. Тело то же, что у фасадной реализации.
    string? Access.ITurnActorRegistry.Actor(string sessionId, string ownerId) => GetTurnActor(sessionId, ownerId);

    // Актор хода чата: владение — через GetOwned (как у GetActiveTurnDelegation). null — неизвестен.
    public string? GetTurnActor(string sessionId, string ownerId)
    {
        if (GetOwned(sessionId, ownerId) is null || !Sessions.TryGetValue(sessionId, out var entry))
            return null;
        var inFlight = entry.Info.Status is SessionStatus.Working or SessionStatus.Waiting;
        var tenant = ResolveOwnerId(entry.Info);
        string? merged;
        lock (entry.PendingLock)
        {
            if (!inFlight && entry.RunActor is not null && !RunAgentsAlive(entry.Process))
                entry.RunActor = entry.SubmitActor = null;
            merged = NarrowerActor(inFlight ? entry.SubmitActor : null, entry.RunActor, tenant);
        }
        return string.IsNullOrEmpty(merged) ? null : merged;
    }

    // Вердикт рамки хода по чату. Нет чата / чужой / проект исчез — Narrow (fail-closed), чат вне
    // проекта — Wide (личное пространство и так его).
    public Access.TurnFrameKind GetTurnFrame(string? sessionId, string ownerId)
    {
        if (string.IsNullOrEmpty(sessionId) || GetOwned(sessionId, ownerId) is not { } session)
            return Access.TurnFrameKind.Narrow;
        Project? project = null;
        if (session.ProjectId is not null && (project = _projects.GetById(session.ProjectId)) is null)
            return Access.TurnFrameKind.Narrow;
        return Access.TurnFrame.Resolve(project, session, ResolveOwnerId(session), GetTurnActor(sessionId, ownerId));
    }

    // Рамка хода для контекста запуска (S2a-4): с S3-5 вердикт в LlmSessionContext передаётся
    // сам стор, а не обходится через фасад (FrameSource: store в TurnLaunchContextFactory).
    // Регистрация ITurnFrameSource в Program.cs (её читают http-тулсеты) остаёт на фасаде —
    // здесь только явная реализация для прямой передачи стора в контекст.
    Access.TurnFrameKind Access.ITurnFrameSource.Frame(string? sessionId, string ownerId) =>
        GetTurnFrame(sessionId, ownerId);

    // Владелец чата (арендатор) либо автор идущего хода (S2a-10): им доступно «Стоп»/перебой/ответ на
    // вопрос агента. Неизвестный актор автором не считается (fail-closed).
    public bool IsOwnerOrTurnAuthor(string sessionId, string userId)
    {
        var s = GetById(sessionId);
        if (s is null) return false;
        var tenant = ResolveOwnerId(s);
        return tenant is not null && (tenant == userId || GetTurnActor(sessionId, tenant) == userId);
    }

    // Кто поставил сообщение в очередь: null — сообщения нет либо отправитель не записан (серверное).
    public string? PendingSentBy(string sessionId, string messageId)
    {
        if (!Sessions.TryGetValue(sessionId, out var entry)) return null;
        lock (entry.PendingLock)
            return entry.Pending.FirstOrDefault(p => p.Id == messageId)?.SenderUserId;
    }

    // Активен ли цикл «до готово» в чате-вызывателе: гейт анти-рекурсии (DelegatedTurnGate.Decide)
    // спрашивает это, чтобы разрешить ход-реакцию на доклад при активном цикле (единственная точка,
    // где координатор принимает результат и запускает следующего). Владение — тот же путь, что в
    // GetActiveTurnDelegation: GetOwned (внутри — ResolveOwnerId).
    public bool HasActiveWorkLoop(string sessionId, string ownerId) =>
        GetOwned(sessionId, ownerId)?.WorkLoop is not null;

    // --- Рантайм-чтения ---

    // Адаптер занятprevious-ходом: у него жив прогон CLI (HasLiveTurn — _turnLock держит,
    // транзрипт пишет) ИЛИ активна фолбэк-оркестрация (OrchestrationActive — между SettleAsync
    // и finally). В обоих случаях старт нового хода гоняется с предыдущим → сериализуем на
    // смерти. Чистая функция для прямого тестирования веток (Ф1, инцидент 2026-08-10).
    internal static bool Busy(ILlmSessionAdapter? p) =>
        p is { HasLiveTurn: true } or { OrchestrationActive: true };

    // Занят ли чат ходом ПРЯМО СЕЙЧАС. Status для этого не годится: у свежего чата он ещё
    // Starting, а между стартом хода и сменой статуса есть реальное окно. Занятость адаптера
    // спрашиваем каноническим Busy (живой прогон ИЛИ фолбэк-оркестрация): в паузе между
    // попытками цепочки прогона CLI нет, но ход идёт — и оркестратор в этот момент сам
    // копирует транскрипт, так что смена провайдера под ним разошлась бы с его restore.
    // Плюс признаки, которых Busy не знает: ход, принятый адаптером но ещё не поднявший
    // процесс, awaited-ход агента (TurnWaiter) и непустая серверная очередь — её сообщения
    // уедут в тот же чат, и провайдера менять под ними нельзя.
    internal static bool HasTurnInFlight(SessionEntry entry)
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
        Sessions.TryGetValue(sessionId, out var entry) && HasTurnInFlight(entry);

    // У координатора есть живой фоновый субагент (Tool Agent и т.п.) — ход, завершённый
    // текстом без маркера, не тупик: координатор ждёт собственного результата. P16: по этому
    // признаку гард молчаливого тупика молчит (аналог TeamPlanningInFlight). entry.Process —
    // адаптер текущего прогона CLI; HasPendingBg истинно, пока прогон доживает фоновых агентов.
    // Дубликат TeamDecisionService.AsyncAgentInFlight? Нет: вертикаль не ходит в entry.Process
    // (40-польная персистентная модель). Ядро пользуется HasPendingBg из Process напрямую —
    // приватное поле, и сюда шов не нужен.
    internal static bool AsyncAgentInFlight(SessionEntry entry) => entry.Process?.HasPendingBg ?? false;

    // --- Рассылка ---

    // Рассылка в session-группу. Внутренний канал штабных обновлений вертикали Services.Team:
    // TeamStateService.BroadcastTeamImplementAsync идёт через этот метод, чтобы не плодить
    // параллельный IHubContext<SessionHub> внутри вертикали (см. комментарий TeamCoordinator).
    // Снаружи (из контроллеров/хаба) используется публичный BroadcastSessionMessageAsync,
    // который дополнительно вещает в project_/user_-группу — для чат-карточек в списке.
    internal Task BroadcastAsync(string sessionId, ServerMessage msg) =>
        _broadcaster.ToSession(sessionId, msg with { SessionId = sessionId });

    // Рассылка в session-группу КРОМЕ соединения-отправителя — ручной ввод пользователя:
    // отправитель уже нарисовал баллон оптимистично (см. точку рассылки в SendDirectAsync),
    // эхо продублировало бы реплику в его ленте. Тот же проставщик SessionId.
    internal Task BroadcastExceptAsync(string sessionId, string exceptConnectionId, ServerMessage msg) =>
        _broadcaster.ToSessionExcept(sessionId, exceptConnectionId, msg with { SessionId = sessionId });

    // Публильный broadcast внеходового сообщения сессии: session-группа + project_/user_-группа
    // (по образцу BroadcastStatusChangeAsync). Используется роутингом группового чата и совещаниями.
    public async Task BroadcastSessionMessageAsync(string sessionId, ServerMessage msg)
    {
        if (!Sessions.TryGetValue(sessionId, out var entry)) return;
        var wired = msg with { SessionId = sessionId };
        var tasks = new List<Task> { _broadcaster.ToSession(sessionId, wired) };
        tasks.Add(_broadcaster.ToChatAudience(entry.Info, wired));
        await Task.WhenAll(tasks);
    }

    // Рассылаем в session-группу (сам чат) всегда, плюс в project-группу (все вкладки проекта)
    // для проектной сессии ЛИБО в user-группу (список чатов) для чата вне проекта —
    // чтобы клиент не пропустил обновление, если не успел войти в session-группу.
    internal async Task BroadcastStatusChangeAsync(string sessionId, Session info, SessionStatus status,
        string? lastMessage = null, int messageCount = 0)
    {
        var statusMsg = new StatusChangedMessage(status.ToString().ToLower(), lastMessage, messageCount)
            with { SessionId = sessionId };
        var tasks = new List<Task> { _broadcaster.ToSession(sessionId, statusMsg) };
        tasks.Add(_broadcaster.ToChatAudience(info, statusMsg));
        await Task.WhenAll(tasks);
    }

    // Уведомить клиентов об удалении чата (в т.ч. авто-удалении временного) — адресация как у
    // BroadcastStatusChangeAsync: проект или владелец чата. Тело переехало из фасада в S3-5:
    // удаляющему ролью нужен тот же адресный путь, а записи к этому моменту в реестре уже нет
    // (TryRemove раньше), поэтому info передаётся явно, а не читается из Sessions.
    public async Task BroadcastChatDeletedAsync(string sessionId, Session info)
    {
        var msg = new ChatDeletedMessage() with { SessionId = sessionId };
        var tasks = new List<Task> { _broadcaster.ToSession(sessionId, msg) };
        tasks.Add(_broadcaster.ToChatAudience(info, msg));
        await Task.WhenAll(tasks);
    }

    // Единая точка перехода статуса сессии: обновить Info → сохранить на диск → разослать клиентам.
    //
    // touchUpdatedAt = false у ДОВОДКИ статуса — перехода, за которым не стоит нового
    // содержимого: exited прогона и sweep-терминус Active→Finished приходят уже после ответа
    // (sweep — спустя grace, на ближайшем SaveSessions, то есть до минуты). UpdatedAt несёт
    // непрочитанность (updatedAt > lastReadAt), поэтому такая доводка метила прочитанный чат
    // непрочитанным заново: точка на кнопке проекта и стены гасла при открытии чата и через
    // полминуты возвращалась (инцидент 06.09.2026). Новое содержимое отмечают переходы, за
    // которыми оно есть: Working (ход пошёл), Active по result (ответ готов), Waiting
    // (карточка ждёт человека), Error.
    //
    // Архивный чат сменой статуса из архива не выводим вовсе (как RetitleAsync/UpdateAsync):
    // признак архива производный (UpdatedAt <= ArchivedAt), и любая отметка возвращала бы его.
    // Настоящая активность архива не теряет: отметку ставит приём сообщения (адаптер
    // SendMessageAsync, локальный голосовой ход — SendDirectAsync), и до смены статуса чат
    // уже не архивный.
    internal async Task ApplyStatusAsync(string sessionId, SessionEntry entry, SessionStatus status,
        bool touchUpdatedAt = true)
    {
        entry.Info.Status = status;
        if (touchUpdatedAt && !entry.Info.IsArchived) entry.Info.UpdatedAt = DateTime.UtcNow;
        SaveSessions();
        await BroadcastStatusChangeAsync(sessionId, entry.Info,
            status, entry.Info.LastMessage, entry.Info.MessageCount);
    }

    // --- Тела методов ITeamSessionDirectory (сам шов остаётся на SessionManager) ---

    internal TeamSessionInfo? DirectoryGet(string sessionId) =>
        Sessions.TryGetValue(sessionId, out var entry) ? Snapshot(entry.Info) : null;

    // Запись режима штаба под VisibilityLock с проверкой приватности (S2d-7b-3, доработка 1):
    // тот же замок, что у SetChatVisibility и SetParticipants. Приватный чат — write не вызывается,
    // PrivateChatModeException. Вертикаль штаба замок сама не берёт.
    internal void WriteUnlessPrivate(string sessionId, Action write)
    {
        if (!Sessions.TryGetValue(sessionId, out var entry)) return;
        lock (entry.VisibilityLock)
        {
            if (entry.Info.PrivateToUserId is not null)
                throw new PrivateChatModeException(Controllers.FeatureChatDoor.PrivateModeReason);
            write();
        }
    }

    internal IReadOnlyList<TeamSessionInfo> ListChildren(string parentSessionId) =>
        [.. Sessions.Values.Select(e => e.Info)
            .Where(s => SessionTaskLinks.ParentSessionId(s, TaskLookup) == parentSessionId)
            .Select(Snapshot)];

    internal ILookup<string, TeamSessionInfo> ChildrenByParent() =>
        Sessions.Values.Select(e => e.Info)
            .Where(s => SessionTaskLinks.ParentSessionId(s, TaskLookup) is not null)
            .ToLookup(s => SessionTaskLinks.ParentSessionId(s, TaskLookup)!, Snapshot);

    // Снимок под нужды штаба: узкий набор полей вместо 40-польной Session (см. TeamSessionInfo).
    // ParentSessionId — вычисляемый (SessionTaskLinks), поэтому снимок строит метод, а не статика.
    private TeamSessionInfo Snapshot(Session s) =>
        new(s.Id, SessionTaskLinks.ParentSessionId(s, TaskLookup), s.ProjectId, s.OwnerId, s.Status, s.UpdatedAt);

    // --- ITeamRunState: рантайм-поля записи и транзакция над состоянием режима штаба ---

    bool ITeamRunState.HasLiveTurn(string sessionId) => HasLiveTurnProcess(sessionId);

    bool ITeamRunState.HasAsyncAgent(string sessionId) =>
        Sessions.TryGetValue(sessionId, out var entry) && AsyncAgentInFlight(entry);

    bool ITeamRunState.TurnStartedByHuman(string sessionId) =>
        Sessions.TryGetValue(sessionId, out var entry) && entry.TeamTurnFromHuman;

    bool ITeamRunState.IsPlanningInFlight(string sessionId) =>
        Sessions.TryGetValue(sessionId, out var entry) && entry.TeamPlanningInFlight;

    void ITeamRunState.SetPlanningInFlight(string sessionId, bool inFlight)
    {
        if (Sessions.TryGetValue(sessionId, out var entry)) entry.TeamPlanningInFlight = inFlight;
    }

    bool ITeamRunState.ShouldSuppressAsyncAgentStallGuard(string sessionId)
    {
        // Баг b63fd8ea: голое HasAsyncAgent подавляло гард молчаливого тупика бессрочно,
        // пока async-агент писал хоть что-то в stdout (BgLingerTimeout — грейс тишины,
        // а не потолок длительности). Метка подавления AsyncAgentStallSince на SessionEntry
        // живёт под TeamTurnLock (дисциплина, что у TeamTurnText/TeamTurnAsked/TeamTurnFromHuman
        // рядом). entry.Process не лочим: он меняется в других точках ядра, и AsyncAgentInFlight
        // даёт согласованный снимок через HasPendingBg (lock в CliRun).
        if (!Sessions.TryGetValue(sessionId, out var entry)) return false;
        var hasAsync = AsyncAgentInFlight(entry);
        var now = DateTime.UtcNow;
        lock (entry.TeamTurnLock)
        {
            if (!hasAsync)
            {
                // Async-агент ушёл — метку обнуляем, чтобы следующий всплеск не унаследовал
                // длительность от НЕсвязанного прошлого агента.
                if (entry.AsyncAgentStallSince is not null) entry.AsyncAgentStallSince = null;
                return false;
            }
            if (entry.AsyncAgentStallSince is null) entry.AsyncAgentStallSince = now;
            return TeamAsyncAgentStallGuard.ShouldSuppress(true, entry.AsyncAgentStallSince, now,
                TeamAsyncAgentStallGuard.DefaultSuppressionTimeout);
        }
    }

    void ITeamRunState.TrySetPermissionModeLive(string sessionId, ClaudeMode mode)
    {
        // Шов «вертикаль → ядро» для смены режима живому CLI-прогону: реализация в ядре
        // потому, что именно ядро держит `entry.Process` (живой ILlmSessionAdapter).
        if (!Sessions.TryGetValue(sessionId, out var entry)) return;
        entry.Process?.TrySetPermissionModeLive(mode);
    }

    void ITeamRunState.TrySetEntryModeLiveAndStaleAdapter(string sessionId, ClaudeMode mode)
    {
        // Шов «вертикаль → ядро» (достройка ITeamRunState, волна Ж): тройная синхронизация
        // Mode/CLI/AdapterStale для SetTeamImplementAsync. Три поля лежат в SessionEntry
        // (Info.Mode, Process?, AdapterStale), и вертикаль без этого шова получала бы
        // доступ к 40-полейной персистентной модели. Гард «координатор не пишет код»
        // режет Bash/PowerShell на приёме permission, --disallowedTools — на создании
        // адаптера: без AdapterStale правка второго доехала бы только до следующего
        // пересоздания, а живой ход остался бы с прежним набором инструментов.
        if (!Sessions.TryGetValue(sessionId, out var entry)) return;
        entry.Info.Mode = mode;
        entry.Process?.TrySetPermissionModeLive(mode);
        if (entry.Process is not null) entry.AdapterStale = true;
    }

    // Транзакция над состоянием режима: ЕДИНСТВЕННЫЙ способ править счётчики бюджета и
    // попытки под-задач. Точки записи разнесены по потокам (раздача волны из колбэка
    // завершения задачи, перевыдача из колбэка провала хода, квота из HTTP-фильтра), а
    // `int++` не атомарен — частичный лок означал бы потерянные инкременты и нечестный счёт
    // ровно там, ради чего Э4 и делался. Внутри — только синхронная работа с моделью.
    // Лок-словарь переехал сюда из TeamStateService вместе с телом (S3-1): стор реализует
    // ITeamRunState, и второго словаря блокировок над тем же SessionTeamImplement быть не может.
    public T? WithTeamState<T>(string sessionId, Func<SessionTeamImplement, T> mutate)
    {
        if (!Sessions.ContainsKey(sessionId)) return default;
        var lockObj = _teamLocks.GetOrAdd(sessionId, _ => new object());
        lock (lockObj)
        {
            var session = GetById(sessionId);
            if (session?.TeamImplement is not { } team) return default;
            return mutate(team);
        }
    }

    // Шов «вертикаль → ядро» для плана вызова штабного разбора хода (волна Ж). Подписчик
    // turn/completed переехал в TeamTurnCompletionService и не должен видеть SessionEntry
    // (40-польная персистентная модель с пятью примитивами синхронизации — общая память двух
    // подсистем). Реализация делегирует в entry — это всё ещё в ядре, потому что OnMessageAsync
    // (ядро) пишет план по тому же ключу и без этого шва держал бы LastTeamTurnEnds приватным
    // полем класса. Без явной реализации вертикаль не нашла бы точку для записи.
    void ITeamRunState.RecordTeamTurnEnd(string sessionId, int turnSeq,
        string? text, bool failed, bool asked)
    {
        if (!Sessions.TryGetValue(sessionId, out var entry)) return;
        entry.RecordTeamTurnEnd(turnSeq, text, failed, asked);
    }

    bool ITeamRunState.TryTakeTeamTurnEnd(string sessionId, int turnSeq,
        out string? text, out bool failed, out bool asked)
    {
        if (!Sessions.TryGetValue(sessionId, out var entry))
        {
            text = null;
            failed = asked = false;
            return false;
        }
        // Делегируем в entry.TryTakeTeamTurnEnd: внутри — TeamTurnLock вокруг ContainsKey
        // + Remove, чтобы забор был атомарен с публикацией turn/completed в шине.
        if (entry.TryTakeTeamTurnEnd(turnSeq, out var t, out var call))
        {
            text = t;
            failed = call is not null && call.Value.Failed;
            asked = call is not null && call.Value.Asked;
            return true;
        }
        text = null;
        failed = asked = false;
        return false;
    }

    // Атомарный pre-claim публикации карточки молчаливого тупика (волна 3 задачи b63fd8ea).
    // Под WithTeamState (тот же лок, что в PublishTeamEscalationAsync,
    // TeamDecisionService.cs:428) проверяем stalledStage (Interview или Planning && WaveNumber==0);
    // если выполнен — переводим стадию в AwaitingDecision (побочный эффект) и отдаём СНИМОК
    // ПОЛНОГО состояния ДО мутации. Возвращаемый bool — единственный источник правды: либо
    // вызывающий код публикует карточку, либо уже опубликовал параллельный путь.
    // Снимок нужен BuildSilentStallEscalation (читает Stage/WaveNumber) для заголовка карточки,
    // а сам объект уже мутирован внутри лока — снапшок берётся ДО мутации и возвращается через
    // out. Полный набор полей (Stage/StageBeforeDecision/WaveStartedAt/WaveActivityAt) нужен
    // паре: этот метод + RollbackSilentStallClaim — откат клейма при сбое публикации карточки
    // (AppendAsync бросил исключение). Случай «чат удалён» здесь не покрыт — там ранний return
    // БЕЗ исключения, и клеймо остаётся до следующего гарда (отдельный нечастый кейс).
    // Без отката чат зависал бы в AwaitingDecision без карточки: stalledStage для этой стадии
    // больше не true, гард больше никогда не сработает.
    // out-параметры нельзя писать внутри лямбды (CS1628), поэтому захватываем через
    // локальный массив из одного элемента: внутри лямбды — присвоение по индексу,
    // снаружи — чтение после возврата.
    bool ITeamRunState.TryClaimSilentStall(string sessionId, out SilentStallClaim claim)
    {
        claim = default;
        var captured = new SessionTeamImplement?[] { null };
        var claimed = WithTeamState(sessionId, t =>
        {
            var stalledStage = t.Stage == TeamImplementStage.Interview
                || (t.Stage == TeamImplementStage.Planning && t.WaveNumber == 0);
            if (!stalledStage)
                return false;
            captured[0] = new SessionTeamImplement
            {
                Stage = t.Stage,
                StageBeforeDecision = t.StageBeforeDecision,
                WaveNumber = t.WaveNumber,
                WaveStartedAt = t.WaveStartedAt,
                WaveActivityAt = t.WaveActivityAt,
            };
            t.StageBeforeDecision = t.Stage;
            t.Stage = TeamImplementStage.AwaitingDecision;
            t.WaveStartedAt = null;
            t.WaveActivityAt = null;
            return true;
        });
        if (claimed && captured[0] is { } snap)
        {
            claim = new SilentStallClaim(
                Stage: snap.Stage,
                WaveNumber: snap.WaveNumber,
                StageBeforeDecision: snap.StageBeforeDecision,
                WaveStartedAt: snap.WaveStartedAt,
                WaveActivityAt: snap.WaveActivityAt);
        }
        return claimed;
    }

    // Откат успешного TryClaimSilentStall. CAS-подобная проверка: восстанавливаем снимок
    // ТОЛЬКО если состояние всё ещё соответствует тому, что оставил клейм (стадия AwaitingDecision
    // и StageBeforeDecision равен исходной стадии из снимка). Прод-сценарий «хвост публикации
    // упал после того, как карточка уже в ленте»: без CAS-гейта стадия бы откатилась в
    // Planning/Interview, и следующий заход гарда поднял бы ВТОРУЮ карточку поверх уже
    // видимой пользователю. С гейтом — false на выходе, состояние не трогается, дублирования
    // нет. Под тем же локом WithTeamState, что и сам claim: между откатом и параллельным
    // новым claim нет гонки — оба сериализуются. Сторона вызова не имеет дела с другими
    // полями SessionTeamImplement и не должна их трогать: меняются ровно те, что заявлены
    // в SilentStallClaim.
    bool ITeamRunState.RollbackSilentStallClaim(string sessionId, SilentStallClaim claim)
    {
        var rolled = false;
        WithTeamState(sessionId, t =>
        {
            // Клеймо «живое»: стадия ровно та, что мы поставили в TryClaimSilentStall,
            // и StageBeforeDecision хранит исходную стадию (Planning/Interview) из снимка.
            // Любое другое состояние означает, что либо карточка уже опубликована, либо
            // параллельный путь уже отменил клеймо — откатывать НЕЛЬЗЯ.
            if (t.Stage != TeamImplementStage.AwaitingDecision
                || t.StageBeforeDecision != claim.Stage)
            {
                return true;
            }
            t.Stage = claim.Stage;
            t.StageBeforeDecision = claim.StageBeforeDecision;
            t.WaveStartedAt = claim.WaveStartedAt;
            t.WaveActivityAt = claim.WaveActivityAt;
            rolled = true;
            return true;
        });
        return rolled;
    }
}
