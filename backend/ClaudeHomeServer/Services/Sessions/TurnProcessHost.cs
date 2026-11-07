using System.Collections.Concurrent;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;

using QueuedMessage = ClaudeHomeServer.Services.SessionManager.QueuedMessage;
using PendingKind = ClaudeHomeServer.Services.SessionManager.PendingKind;

namespace ClaudeHomeServer.Services.Sessions;

// Хост процесса CLI (S3-5, docs/research/session-manager-split-2026-11.md §1 «процесс», §2):
// старт процесса сессии и его переподключение, «Стоп» человека и реанимация зависшего чата,
// ответы на карточки взаимодействия (разрешение/вопрос/план), ручная компакция, ожидание
// устройства локального проекта и гашение всех процессов при остановке сервера. Тела
// перенесены из SessionManager дословно; фасад держит делегирующие обёртки прежних
// сигнатур — на них сидят точки входа (хаб, контроллеры, авто-возобновление).
//
// Зависимостей десять (сторож SessionCoreShapeGuardTests правило 2): фабрика адаптеров,
// сборщик контекста запуска, стор, история, маршрут, очередь, гейт устройства, проекты,
// ленивый шов ITurnStarter (разбор очереди при готовности устройства — приём хода живёт
// у фасада до S3-7) и шина событий хода (её передают контексту запуска).
//
// Отклонения от таблицы плана §2 (каждое — с комментарием у места; отдельным решением они не
// подтверждены — на проверке у Александра):
// — ReviveStuckSession(Async) и StopClosedExecutorProcess остаются здесь, а не в TurnRecovery:
//   их единственный вызывающий — InterruptCore и join-а TaskExecutionService; ребро
//   Host→Recovery из таблицы плана убрано совсем;
// — конфиг (IConfiguration) в конструктор не взят: ни одно тело этой роли его не читает
//   (DataDir и SystemPromptViaFile относятся к контексту и живут в фабрике); вместо него —
//   шина событий, которую требует StartNewSessionAsync;
// — EnqueueBypassTurn и BuildEnqueueBypass здесь (очередь — дело этой роли), фабрика
//   получает готовый колбэк параметром Build;
// — сквозной счётчик прогонов (_runSeq) переезжает сюда как static NextRunId(): оставшиеся
//   места фасада (конец хода, приём хода) зовут тот же метод — счётчик остаётся одним
//   на процесс, как задумано (RunId различает прогоны одной сессии).
// Класс public по той же причине, что и роли S3-2…S3-4: его принимает публичный конструктор
// фасада (internal-тип в публичном конструкторе — CS0051). Вовне он не назван ни одним типом:
// граница держит сторож правила 4.
public sealed class TurnProcessHost
{
    private readonly Llm.ILlmSessionAdapterFactory _adapters;
    private readonly TurnLaunchContextFactory _factory;
    private readonly SessionStore _store;
    private readonly SessionHistory _sessionHistory;
    private readonly CliRoute _route;
    private readonly PendingQueueService _queue;
    private readonly Execution.IProjectDeviceGate? _deviceGate;
    private readonly ProjectManager _projects;
    private readonly Lazy<ITurnStarter> _turnStarter;
    private readonly Turn.ITurnEventBus? _turnEvents;
    private readonly ILogger<TurnProcessHost> _log;

    // Конструктор internal: параметр TurnLaunchContextFactory — internal-тип (его сигнатура
    // держит internal-шов с internal TurnAccumulator, CS0051). Класс при этом public: его
    // принимает публичный конструктор фасада. DI собирает роль не type-activation, а лямбдой
    // AddSessionCore — internal-конструктор для неё доступен.
    internal TurnProcessHost(Llm.ILlmSessionAdapterFactory adapters, TurnLaunchContextFactory factory,
        SessionStore store, SessionHistory sessionHistory, CliRoute route, PendingQueueService queue,
        Execution.IProjectDeviceGate? deviceGate, ProjectManager projects,
        Lazy<ITurnStarter> turnStarter, Turn.ITurnEventBus? turnEvents, ILogger<TurnProcessHost> log)
    {
        _adapters = adapters;
        _factory = factory;
        _store = store;
        _sessionHistory = sessionHistory;
        _route = route;
        _queue = queue;
        _deviceGate = deviceGate;
        _projects = projects;
        _turnStarter = turnStarter;
        _turnEvents = turnEvents;
        _log = log;
    }

    private ConcurrentDictionary<string, SessionEntry> _sessions => _store.Sessions;

    // Сквозной счётчик прогонов адаптера (SessionEntry.RunId): один на процесс сервера —
    // сравниваются только прогоны одной сессии, глобальная уникальность лишь упрощает отладку.
    // (S3-5: поле переехало из фасада; оставшиеся места фасада — голосовой ход, приём хода —
    // зовут этот метод, поэтому счётчик остаётся единственным на процесс.)
    private static long _runSeq;
    internal static long NextRunId() => Interlocked.Increment(ref _runSeq);

    // --- Старт и переподключение процесса ---

    // Начало нового процесса сессии (тело переехало из StartNewSessionAsync).
    // ClaudeSessionId приходит не только от CLI: параметр resumeSessionId тела запроса
    // (POST /sessions, /chats, /personas/{id}/chats) садится в него как есть. А дальше он
    // становится ИМЕНЕМ ПАПКИ в data/sessions и именем файла транскрипта, которые при
    // удалении чата удаляются рекурсивно — «..» в значении увел бы удаление на всю папку
    // data (projects.json, users.json, история, сторы персон и задач). Единая точка старта
    // любой сессии, поэтому гейт стоит здесь; сообщение уходит клиенту как 400.
    internal async Task StartNewSessionAsync(Session session, string rootPath, string? rawSystemPrompt,
        Func<IReadOnlyList<PermissionRule>>? permissionRules)
    {
        if (session.ClaudeSessionId is { } resumeId && !Llm.TranscriptMigrator.IsSafeSessionId(resumeId))
            throw new InvalidOperationException(
                "Недопустимый resumeSessionId: разрешены только буквы, цифры, дефис и подчеркивание");

        // Ключ истории до первого ответа агента — id чата (HistoryKeyOf): иначе внеходовые
        // записи в свежий чат (карточки нитей картинок) жили бы только в памяти.
        // (S3-5: аккумулятор создаёт роль истории — её ключ и её лок; запись появляется
        // в реестре уже с аккумулятором, как и раньше.)
        var entry = new SessionEntry { Info = session };
        await _sessionHistory.EnsureAccumulatorAsync(entry);
        var accumulator = entry.Accumulator!;

        _queue.AttachPendingPersistence(entry);
        _sessions[session.Id] = entry;

        var ownerId = _store.ResolveOwnerId(session);

        // Чат в отдельном worktree: рабочая папка сессии — его дерево, не корень проекта
        // (корень запоминаем — он fallback для slice графа, пока свой граф дерева не построен)
        var projectRoot = rootPath;
        rootPath = CliRoute.EffectiveRoot(session, rootPath);

        // Идентификатор прогона несёт колбэк: по нему OnMessageAsync отличает exited этого
        // прогона от позднего exited доживающего (см. SessionEntry.DrainOnExitedRun)
        var runId = NextRunId();

        var context = _factory.BuildForNewSession(session, ownerId,
            session.ProjectId is { } launchProjectId ? _projects.GetById(launchProjectId) : null,
            rootPath, projectRoot, rawSystemPrompt, permissionRules, accumulator, runId,
            BuildEnqueueBypass(session.Id), _turnEvents,
            _route.ConfigRootFor(ownerId, session.Provider));
        var adapter = _adapters.Create(session, context);
        entry.Process = adapter;
        entry.RunId = runId;

        await adapter.StartAsync();
        _store.SaveSessions();
    }

    internal async Task EnsureProcessAsync(string sessionId, SessionEntry entry)
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
        await _sessionHistory.EnsureAccumulatorAsync(entry);
        var accumulator = entry.Accumulator!;

        // Чат вне проекта — рабочая папка Chats, без проектного промпта и правил;
        // проектная сессия — RootPath/SystemPrompt/PermissionRules из проекта.
        // Персона-слой восстанавливаем так же, как при первом старте (иначе после рестарта
        // сервера персонная сессия теряла бы характер и долгую память) — сборку контекста
        // держит TurnLaunchContextFactory (S3-5).
        // Идентификатор прогона — как в StartNewSessionAsync (см. SessionEntry.RunId)
        var runId = NextRunId();

        Llm.LlmSessionContext context;
        if (entry.Info.ProjectId is null)
        {
            var rootPath = _route.ResolveChatRoot(entry.Info.OwnerId
                ?? throw new InvalidOperationException("У чата не задан владелец"));
            context = _factory.BuildForReconnect(entry, null, rootPath, accumulator, runId,
                permissionRules: null, BuildEnqueueBypass(sessionId), _turnEvents,
                _route.ConfigRootFor(entry.Info.OwnerId, entry.Info.Provider));
        }
        else
        {
            var project = _projects.GetById(entry.Info.ProjectId)
                ?? throw new InvalidOperationException("Проект не найден");
            // Корень проекта запоминаем ДО EffectiveRoot: у worktree-чата это и есть fallback
            // для slice графа (ADR-003). Совпадение с rootPath — чат без worktree, fallback
            // сводится к no-op в CodeGraphPromptProvider.GetSliceAsync.
            var rootPath = CliRoute.EffectiveRoot(entry.Info, project.RootPath);
            context = _factory.BuildForReconnect(entry, project, rootPath, accumulator, runId,
                // Правила разрешения проекта читаются на каждый ход: их правка не должна
                // ждать пересборки адаптера
                () => _projects.GetById(entry.Info.ProjectId!)?.PermissionRules
                    ?? (IReadOnlyList<PermissionRule>)Array.Empty<PermissionRule>(),
                BuildEnqueueBypass(sessionId), _turnEvents,
                _route.ConfigRootFor(project.OwnerId, entry.Info.Provider));
        }
        var adapter = _adapters.Create(entry.Info, context);
        entry.Process = adapter;
        entry.RunId = runId;
        await adapter.StartAsync();
    }

    // Ручное сворачивание контекста: /compact в CLI, минуя счётчики и историю user-сообщений.
    // Гейт «провайдер умеет compact» остаётся у фасада (LlmProviderRegistry в хост не входит:
    // больше ни одному телу этой роли он не нужен, S3-5) — защита от рассинхрона UI там же.
    internal async Task CompactAsync(string sessionId, SessionEntry entry)
    {
        await EnsureProcessAsync(sessionId, entry);
        entry.Accumulator?.SetPersona(entry.Info.PersonaId);

        await _store.ApplyStatusAsync(sessionId, entry, SessionStatus.Working);

        // Метка «идёт компакция» — её читает PreemptForPending: прерывать компакцию нельзя,
        // её exited глотает обёртка фолбэка. Гасится на конце хода в OnMessageAsync — но
        // только терминалом ЭТОГО прогона, поэтому и метка хранит его идентификатор.
        // Ход не стартовал (адаптер отвалился на запуске compact) — снимаем сразу: терминала
        // не будет, а повисшая метка запретила бы перебой в этом чате навсегда.
        entry.CompactRun = entry.RunId;
        try { await entry.Process!.CompactAsync(); }
        catch { entry.CompactRun = 0; throw; }
    }

    // Колбэки оркестрационного обхода: EnqueueBypass замыкает sessionId сессии (адаптер
    // передаёт тот же Info.Id, но захват надёжнее — сессия уже известна при создании
    // контекста). Сам обход — очередь этой роли; OrchestrationDone строит фабрика.
    private Func<string, string, IReadOnlyList<string>?, int, bool, Task> BuildEnqueueBypass(string sessionId)
        => (_, text, paths, depth, suppress) => EnqueueBypassTurn(sessionId, text, paths, depth, suppress);

    private async Task EnqueueBypassTurn(string sessionId, string text,
        IReadOnlyList<string>? attachedPaths, int agentDepth, bool suppressTasksExecute)
    {
        if (!_sessions.TryGetValue(sessionId, out var entry)) return;
        await _queue.EnqueuePendingAsync(sessionId, entry, text,
            senderPersonaId: null, senderOrigin: "orchestration-bypass",
            agentDepth, silent: false, suppressTasksExecute,
            kind: PendingKind.Agent, attachedPaths: attachedPaths);
    }

    // === Ожидание устройства локального проекта (ADR-016, вариант А плана §5) ===

    // Вердикт «ждать устройство» для чата: только проектный чат локального проекта, чьё
    // устройство не готово. Устройства нет вовсе (отозвано) — не ждём: ход откажет честно.
    internal ProjectBackgroundGate? DeviceWaitGate(SessionEntry entry)
    {
        if (_deviceGate is null || entry.Info.ProjectId is not { } pid) return null;
        if (_projects.GetById(pid) is not { } project) return null;
        var gate = _deviceGate.Check(project);
        return gate.MustWait ? gate : null;
    }

    // Парковка на сессии: список заменяется целиком (копия с добавлением) — SaveSessions
    // сериализует Info без лока очереди и не должен застать список посреди правки. Дедуп
    // агентских — как у Pending: наивный ретрай агента не множит одинаковые ходы.
    internal async Task<SendAndWaitResult> ParkForDeviceAsync(string sessionId, SessionEntry entry,
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
        _store.SaveSessions();
        await _queue.BroadcastPendingAsync(sessionId, entry);
        _log.LogInformation("Чат {Session}: сообщение ждёт устройство {Device} ({Reason}), в ожидании {Count}",
            sessionId, gate.DeviceId, gate.Reason, position);
        return new SendAndWaitResult.Queued(position, Duplicate: false);
    }

    /// <summary>Чаты, у которых есть сообщения, ждущие устройство.</summary>
    internal IReadOnlyList<Session> GetDeviceWaitingSessions() =>
        [.. _sessions.Values.Select(e => e.Info).Where(i => i.DeviceWaitQueue is { Count: > 0 })];

    /// <summary>
    /// Устройство готово: ждавшие сообщения встают в голову обычной очереди (порядок прихода
    /// сохраняется) и разбираются штатным drain — первое сразу, если чат свободен, остальные
    /// по концу хода. Возвращает число выпущенных сообщений.
    /// </summary>
    internal async Task<int> ReleaseDeviceWaitAsync(string sessionId)
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
        _store.SaveSessions();
        await _queue.BroadcastPendingAsync(sessionId, entry);
        _log.LogInformation("Чат {Session}: устройство готово, в работу {Count} ждавших сообщений", sessionId, released.Count);
        // Разбор очереди — приём хода, он остаётся у фасада до S3-7: путь через шов
        // ITurnStarter (его реализация держит и проверку окна штатной остановки _stopping).
        if (dispatchNow) _ = Task.Run(() => _turnStarter.Value.DrainNextPendingAsync(sessionId));
        return released.Count;
    }

    /// <summary>
    /// Снять сообщения, ждущие устройство дольше потолка (поставлены раньше <paramref name="olderThanUtc"/>).
    /// Возвращает снятые — уведомление владельцу шлёт вызывающий.
    /// </summary>
    internal async Task<IReadOnlyList<DeviceWaitMessage>> ExpireDeviceWaitAsync(string sessionId, DateTime olderThanUtc)
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
        _store.SaveSessions();
        await _queue.BroadcastPendingAsync(sessionId, entry);
        return expired;
    }

    // --- «Стоп» человека, зависший чат, ответы на карточки ---

    // «Стоп» человека (хаб, доска агентов). Штаб останавливает исполнителя через
    // ITeamTurnIntake.InterruptTurn — та же механика, но без отметки в истории.
    internal void Interrupt(string sessionId) => InterruptCore(sessionId, byUser: true);

    internal void InterruptCore(string sessionId, bool byUser)
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
                _store.SaveSessions();
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
    /// некому начать, а цикл «до готово» жёг бы итераций, которых никто не увидит. В отличие от
    /// «Стопа»: очередь ЧИСТИТСЯ, а не морозится — заморозка ждёт возобновления, которого не
    /// будет; отметки человека и composer_restore нет: это не действие человека. Область — ровно
    /// приватные чаты отозванного (PrivateToUserId): ходы общих чатов принадлежат проекту и
    /// продолжаются для владельца.
    /// </summary>
    internal void StopChatsOfRevokedMember(string projectId, string memberUserId)
    {
        foreach (var session in _store.GetByProject(projectId))
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
            _queue.PersistPending(entry);
        }
    }

    // Отметка «Ход остановлен пользователем» в history.json. Пишется ТОЛЬКО на прерывании
    // человеком — в точке, где его намерение известно наверняка: ниже по стеку (адаптер,
    // паспорт хода с исходом interrupted) «Стоп» человека уже не отличить от остановки
    // исполнителя штабом, а падение процесса и внутренние перезапуски хода эту точку не
    // проходят вовсе. Живая лента ставит такую же отметку сама, оптимистично.
    // internal: к ней обращается фасад (PreemptTurnForQueue — прерывание ради очереди),
    // поэтому точка остаётся на фасаде, а тело здесь.
    internal void RecordUserInterrupt(string sessionId, SessionEntry entry)
    {
        if (entry.Accumulator is not { } acc || !acc.OnUserInterrupted()) return;
        _sessionHistory.SaveAccumulatorSnapshot(entry, $"отметка прерывания хода ({sessionId})");
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
            DeferredRun.FireAndForget(dead.DisposeAsync().AsTask(),
                $"уборка адаптера зависшего хода ({sessionId})");
        }
        DeferredRun.FireAndForget(ReviveStuckSessionAsync(sessionId, entry),
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
        DeferredRun.FireAndForget(proc.DisposeAsync().AsTask(), $"гашение процесса исполнителя закрытой задачи ({sessionId})");
        return true;
    }

    private async Task ReviveStuckSessionAsync(string sessionId, SessionEntry entry)
    {
        await _store.ApplyStatusAsync(sessionId, entry, SessionStatus.Active);
        await _sessionHistory.AddWorkLoopStoppedNoticeAsync(sessionId, "stuck_reset",
            "Зависший ход сброшен — чат снова доступен.");
    }

    // «Стоп» замораживает очередь (не чистит): сообщения остаются ждать возобновления,
    // а последнее пользовательское возвращается в композер (composer_restore).
    private async Task FreezePendingAsync(string sessionId, SessionEntry entry)
    {
        QueuedMessage? lastUser = null;
        bool hadAny;
        lock (entry.PendingLock)
        {
            entry.QueueFrozen = true;
            _queue.PersistPending(entry);
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
        if (hadAny) await _queue.BroadcastPendingAsync(sessionId, entry);

        // Что вернуть в композер: изъятое из очереди, иначе — снимок прерванного хода (если он
        // был пользовательским). Авто/агентский ход и пустая очередь → пустой restore.
        var restore = lastUser is not null
            ? new ComposerRestoreMessage(lastUser.Text,
                lastUser.AttachedPaths is { Count: > 0 } ? lastUser.AttachedPaths : null, lastUser.Mode)
            : entry.CurrentTurnSnapshot is { } snap
                ? new ComposerRestoreMessage(snap.Text,
                    snap.AttachedPaths is { Count: > 0 } ? snap.AttachedPaths : null, snap.Mode)
                : new ComposerRestoreMessage(null, null, null);
        await _store.BroadcastSessionMessageAsync(sessionId, restore);
        // Snapshot гасим после доставки restore — разовый, повторный FreezePending не должен
        // формировать restore из того же snapshot
        entry.CurrentTurnSnapshot = null;
    }

    // Снимок цикла «до готово» в ленту (тело было в фасаде; рассылка — напрямую стора,
    // как делают остальные роли ядра).
    private Task BroadcastWorkLoopAsync(string sessionId, SessionEntry entry)
    {
        var loop = entry.Info.WorkLoop;
        return _store.BroadcastAsync(sessionId, new WorkLoopMessage(
            loop is not null, loop?.Iteration ?? 0, loop?.MaxIterations ?? 0, loop?.Phase,
            loop?.WaitingReason, loop?.WaitingTicks ?? 0));
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
        DeferredRun.FireAndForget(_store.ApplyStatusAsync(sessionId, entry, status),
            $"смена статуса после {what} ({sessionId})");
    }

    internal void RespondPermission(string sessionId, string requestId, string behavior)
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
            _store.SaveSessions();
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
        DeferredRun.FireAndForget(_store.BroadcastAsync(sessionId,
                new InteractionResolvedMessage("permission", requestId, Decision: decision)),
            $"рассылка ответа на permission ({sessionId})");
        ResumeAfterInteraction(sessionId, entry, "permission");
    }

    // Снять инструмент с «Разрешать всегда» этого чата: следующий его вызов снова спросит.
    // Идемпотентно — инструмента в списке нет, отдаём сессию как есть (диск не трогаем).
    // Сравнение имён — OrdinalIgnoreCase, как при проверке в ClaudeSession.
    // null — сессии нет (владение проверяет контроллер, как у соседних эндпоинтов).
    // UpdatedAt не двигаем: настройка чата — не активность (см. соглашение о настройках).
    internal Session? RemoveAutoAllowTool(string sessionId, string tool)
    {
        if (!_sessions.TryGetValue(sessionId, out var entry)) return null;
        if (entry.Info.AutoAllowTools.RemoveAll(t => string.Equals(t, tool, StringComparison.OrdinalIgnoreCase)) > 0)
            _store.SaveSessions();
        return entry.Info;
    }

    internal void AnswerQuestion(string sessionId, string toolUseId, string answerText)
    {
        if (!_sessions.TryGetValue(sessionId, out var entry)) return;
        if (IsStaleInteractionAnswer(sessionId, entry, $"вопрос {toolUseId}")) return;
        entry.Process?.AnswerQuestion(toolUseId, answerText);
        entry.PendingInteraction = null;
        object? answers = null;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(answerText);
            if (doc.RootElement.TryGetProperty("answers", out var a))
                answers = System.Text.Json.JsonSerializer.Deserialize<object>(a.GetRawText());
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[SessionManager] Ответ на вопрос ({sessionId}) не распарсился: карточка погаснет без сводки выбора (и в историю, и в рассылку уйдёт без answers): {ex.Message}");
        }
        // Фиксируем ответ в истории, чтобы карточка вопроса пережила перезагрузку
        if (entry.Accumulator is not null)
        {
            entry.Accumulator.OnQuestionAnswered(toolUseId, answers);
            _sessionHistory.SaveAccumulatorSnapshot(entry,
                $"сохранение истории после ответа на вопрос ({sessionId})");
        }
        DeferredRun.FireAndForget(_store.BroadcastAsync(sessionId,
                new InteractionResolvedMessage("question", toolUseId, Answers: answers)),
            $"рассылка ответа на вопрос ({sessionId})");
        ResumeAfterInteraction(sessionId, entry, "ответа на вопрос");
    }

    internal void RespondPlan(string sessionId, string requestId, bool approve, string? feedback)
    {
        if (!_sessions.TryGetValue(sessionId, out var entry)) return;
        if (IsStaleInteractionAnswer(sessionId, entry, $"план {requestId}")) return;
        entry.Process?.RespondPlan(requestId, approve, feedback);
        entry.PendingInteraction = null;
        // Фиксируем решение по плану в истории, чтобы карточка пережила перезагрузку
        if (entry.Accumulator is not null)
        {
            entry.Accumulator.OnPlanResolved(requestId, approve, feedback);
            _sessionHistory.SaveAccumulatorSnapshot(entry,
                $"сохранение истории после решения по плану ({sessionId})");
        }
        DeferredRun.FireAndForget(_store.BroadcastAsync(sessionId,
                new InteractionResolvedMessage("plan", requestId, Approved: approve, Feedback: feedback)),
            $"рассылка решения по плану ({sessionId})");
        ResumeAfterInteraction(sessionId, entry, "решения по плану");
    }

    // --- Гашение всех процессов ---

    // Остановка всех живых адаптеров — вызывается при graceful shutdown приложения,
    // иначе после остановки сервера остаются зомби-процессы (claude + node MCP-серверов).
    // Флаг «штатная остановка» (_stopping — его смысл в разборе хода, S3-6) и таймер тика
    // ожидания (цикл «до готово», S3-7) остаются у фасада: он выставляет их ДО этого вызова,
    // порядок тот же, что был: _stopping → StopAutoSave → гашение таймера → FlushPendingSave
    // → убийство процессов.
    internal void KillProcesses()
    {
        // Дебаунс-таймер MarkRead фасад сбрасывает синхронно (FlushPendingSave) ДО убийства:
        // отложенная запись не теряется, а SaveSessions() завершается на этом же потоке,
        // без гонки с диспозом.
        _store.FlushPendingSave();

        var tasks = _sessions.Values
            .Select(e => e.Process)
            .OfType<Llm.ILlmSessionAdapter>()
            .Select(p => p.DisposeAsync().AsTask())
            .ToArray();
        if (tasks.Length == 0) return;
        try { Task.WaitAll(tasks, TimeSpan.FromSeconds(15)); }
        catch (AggregateException ex)
        {
            Console.Error.WriteLine($"[SessionManager] Остановка процессов при завершении: {ex.GetBaseException().Message}");
        }
    }
}
