using System.Collections.Concurrent;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services.Llm;
using ClaudeHomeServer.Services.Prompts;

namespace ClaudeHomeServer.Services.Sessions;

// Ядро приёма хода (S3-7, docs/research/session-manager-split-2026-11.md, аддендум Александра
// к §1/§2/§3): ход в сессии запускает только этот класс. Цикл «до готово» и добивание сабагента
// ход не стартуют сами — просят его у ядра через TrySubmitContinuationAsync; последняя проверка
// остановки перед сабмитом живёт здесь, а не у просящего: ранняя отсечка просящего слишком
// рання (Stop между ней и сабмитом уронил бы директиву в умирающий адаптер). Проверка сделана
// без блокировки: окно между ней и сабмитом не закрыто; практический риск ничтожен — писатель
// флага один (BeginShutdown, зовётся один раз при остановке приложения).
//
// Очередь pending, захват слота «сессия занята», пин разбора и флаг Stopping — одна атомарная
// операция этого класса: dequeue и пин под тем же замком entry.PendingLock; второй точки правды
// о том, что закреплено за ходом, не заводится. Пин из drain здесь же: вынести его в продолжение
// или в хелпер — значит открыть окно, где два drain'а закрепляют разное.
//
// Тела перенесены дословно из SessionManager (фасад переадресует прежними сигнатурами,
// переходный режим S3-0 §4). Зависимостей ровно десять (правило 2 сторожа формы ядра):
// store, personaLayer, queue, processHost, completion, settings, naming, voice,
// Lazy<ITeamNotifier>, config. Два уточнения против списка плана — зафиксированы в отчёте
// задачи S3-7:
// — «history» заменена «personaLayer»: телу BuildCliTurnText нужен BuildOmcRoleHint (блок Omc
//   вынесен в PersonaLayerBuilder, e4a4cf13b), а EnsureAccumulator просится хосту процесса
//   (он держит контекст запуска хода), прямой зависимости от роли истории у ядра нет;
// — вызов цикла (пустая очередь при активном цикле — «цикл продолжит работу директивой») — не
//   параметр конструктора, а переходный хук OnLoopContinueRequested: ядро не знает тело цикла
//   (оно на фасаде до шага 2), и лимит десяти не поднимается ради шва, живущего до выноса
//   TurnContinuation.
// Класс public только по той же причине, что и другие роли параметра публичного конструктора
// фасада (CS0051: менее доступный тип в параметре доступного конструктора); конструктор —
// internal, его зовут только AddSessionCore и запасная сборка фасада. Наружу ядро торчит
// швами ITurnStarter и Team.ITeamTurnIntake, свои члены (Stopping, TrySubmitContinuation,
// хук цикла) — только внутри сборки.
public sealed class TurnIntake : ITurnStarter, Team.ITeamTurnIntake
{
    private readonly SessionStore _store;
    private readonly PersonaLayerBuilder _persona;
    private readonly PendingQueueService _pending;
    private readonly TurnProcessHost _process;
    private readonly TurnCompletion _completion;
    private readonly ChatSettings _settings;
    private readonly ChatNaming _naming;
    private readonly LocalVoiceTurn _voice;
    private readonly Lazy<Team.ITeamNotifier> _teamNotifier;
    private readonly IConfiguration _config;
    private readonly ILogger<TurnIntake> _log;

    internal TurnIntake(SessionStore store, PersonaLayerBuilder personaLayer,
        PendingQueueService pending, TurnProcessHost process, TurnCompletion completion,
        ChatSettings settings, ChatNaming naming, LocalVoiceTurn voice,
        Lazy<Team.ITeamNotifier> teamNotifier, IConfiguration config, ILogger<TurnIntake> log)
    {
        _store = store;
        _persona = personaLayer;
        _pending = pending;
        _process = process;
        _completion = completion;
        _settings = settings;
        _naming = naming;
        _voice = voice;
        _teamNotifier = teamNotifier;
        _config = config;
        _log = log;
    }

    private ConcurrentDictionary<string, SessionEntry> _sessions => _store.Sessions;
    private static bool HasPending(SessionEntry entry) => PendingQueueService.HasPending(entry);

    // Признак штатной остановки (ADR-033): писатель ровно один — BeginShutdown (его зовёт
    // «Стоп» приложения SessionManager.KillAllProcesses ДО гашения адаптеров); писателя,
    // который бы его снимал, в коде нет — флаг живёт до конца процесса (семантика зафиксирована
    // тестом SessionManagerShutdownTests ДО переноса). Тела ядра читают своё поле;
    // TurnCompletion и цикл фасада — через IsStopping, только как раннюю отсечку:
    // решающая проверка — внутри TrySubmitContinuationAsync.
    internal volatile bool Stopping;

    internal void BeginShutdown() => Stopping = true;

    bool ITurnStarter.IsStopping => Stopping;

    // Старт нового хода в сессии (любой источник: человек, очередь, авто-хор). Вешает
    // TaskExecutionService через property фасада (тот переадресует в это поле). Сбой хука
    // ход не роняет. null — хук не задан (тесты без стора задач).
    internal Func<string, Task>? TurnStarting { get; set; }

    // Текст пользовательского сообщения (ввод в чат) — для push-источников автоматизаций
    // (детекция @упоминаний персон). Вызывается из SendDirectAsync после записи в Accumulator;
    // fire-and-forget, ошибки наблюдателя не роняют ход. Фасад переадресует подписку в это
    // событие (внешний подписчик PersonaAutomationService не меняется).
    internal event Func<Session, string, string?, Task>? OnUserMessage;

    // Переходный хук «очередь пуста, цикл активен и свободен от маркера — цикл продолжит
    // работу директивой» (S3-7): ядро регистрирует сам факт, тело цикла (SessionManager
    // до шага 2 выноса TurnContinuation) реагирует на него. Прямой зависимостью быть не может:
    // ядро не знает цикл (правило 1 сторожа), а держать его в конструкторе — поднять лимит
    // десяти ради шва, который снимается на шаге 2.
    internal Func<string, Task>? OnLoopContinueRequested;

    // Просьба продолжения о ходе (аддендум §2): единственный путь, которым цикл «до готово»
    // и добивание сабагента стартуют ход — тем же путём, что и обычный приём (системная
    // директива через SendDirectAsync), и с решающей проверкой остановки здесь. false —
    // хода нет (стоп); вызывающий обязан снять свой маркер итерации, иначе цикл зависнет
    // на мёртвой итерации.
    internal async Task<bool> TrySubmitContinuationAsync(string sessionId, string text,
        DeliveryCause cause = DeliveryCause.Unknown)
    {
        // Последняя проверка перед отправкой: ранняя отсечка вызывающего была вне ядра,
        // Stop мог прийти между ней и этим местом. Проверка без блокировки: окно между ней
        // и SendDirectAsync остаётся — риск ничтожен: писатель флага один (BeginShutdown).
        if (Stopping) return false;
        if (!_sessions.TryGetValue(sessionId, out var entry)) return false;
        await SendDirectAsync(sessionId, entry, text, [], mode: null, systemDirective: true, auto: false,
            senderPersonaId: null, suppressTasksExecute: false, senderOrigin: null, cause: cause);
        return true;
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
    public Task<SendUserOutcome> SendMessageAsync(string sessionId, string text, IReadOnlyList<string> attachedPaths, string? mode = null, bool systemDirective = false, bool auto = false, string? senderPersonaId = null, bool suppressTasksExecute = false, string? senderOrigin = null, string? senderConnectionId = null, string? staffNote = null, DeliveryCause cause = DeliveryCause.Unknown, int agentDepth = 0, string? senderUserId = null)
        => SendMessageCoreAsync(sessionId, text, attachedPaths, mode, systemDirective, auto, senderPersonaId, suppressTasksExecute,
            senderOrigin, senderConnectionId, staffNote, cause, agentDepth, senderUserId, queuedShown: false);

    // queuedShown — доставка из очереди уже показала «В очереди»: выходы без старта хода обязаны её закрыть
    private async Task<SendUserOutcome> SendMessageCoreAsync(string sessionId, string text, IReadOnlyList<string> attachedPaths, string? mode, bool systemDirective, bool auto, string? senderPersonaId, bool suppressTasksExecute, string? senderOrigin, string? senderConnectionId, string? staffNote, DeliveryCause cause, int agentDepth, string? senderUserId, bool queuedShown)
    {
        if (!_sessions.TryGetValue(sessionId, out var entry))
            throw new InvalidOperationException("Сессия не найдена");

        _store.ClaimFirstMessage(entry);

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
            _store.SaveSessions();

            // Режим «Командная реализация» (Э5): вводная человека начинает новую итерацию —
            // бюджет с нуля. Делаем это на приёме сообщения, ДО очереди: иначе вводная,
            // постоявшая в очереди, доехала бы до координатора уже с исчерпанным потолком.
            _teamNotifier.Value.OnHumanInput(sessionId);
            // Э4/M3: ответ на карточку остановки обычным сообщением — равноправная замена
            // её кнопок (спека: «написал, что делать, координатор учёл и пошёл дальше с того
            // же места»). Стадию возвращаем ДО очереди, по тем же правилам, что решение по
            // карточке, — иначе текст человека упирался бы в гейты стадии «ждёт решения».
            await _teamNotifier.Value.OnHumanInputAsync(sessionId);

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
                if (entry.QueueFrozen) { entry.QueueFrozen = false; _pending.PersistPending(entry); }
                var enqueued = await _pending.EnqueuePendingAsync(sessionId, entry, text, senderPersonaId, senderOrigin,
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
                    if (entry.QueueFrozen) { entry.QueueFrozen = false; _pending.PersistPending(entry); }
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
                await _pending.BroadcastPendingAsync(sessionId, entry);
                await DeliverPendingAsync(sessionId, entry, head);
                return SendUserOutcome.Queued;
            }
        }

        await SendDirectAsync(sessionId, entry, text, attachedPaths, mode, systemDirective, auto,
            senderPersonaId, suppressTasksExecute, senderOrigin, senderConnectionId: senderConnectionId, staffNote: staffNote, cause: cause, agentDepth: agentDepth,
            senderUserId: senderUserId, queuedShown: queuedShown);
        return SendUserOutcome.Started;
    }

    // Ход, не начатый по причине отказа: сообщение об ошибке в ленту, статус Error, процесс не трогаем
    private async Task RefuseTurnAsync(string sessionId, SessionEntry entry, string reason)
    {
        _log.LogWarning("Ход чата {Session} отклонён: {Reason}", sessionId, reason);
        var runId = TurnProcessHost.NextRunId(); // сквозной счётчик прогонов живёт в хосте (S3-5)
        entry.RunId = runId;
        var acc = entry.Accumulator!;
        await _completion.OnMessageQueuedAsync(sessionId, acc, new ErrorMessage(reason, ExpectResultFollows: true, Action: PersonaZone.ChangeCompanionAction), runId);
        await _completion.OnMessageQueuedAsync(sessionId, acc, new ResultMessage(
            Subtype: "error", DurationMs: 0, NumTurns: 0, Usage: null, TotalCostUsd: null), runId);
        await _completion.OnMessageQueuedAsync(sessionId, acc, new ExitedMessage(), runId);
    }

    // Непосредственный запуск хода в процесс (гейты очереди уже пройдены либо не требуются).
    // fromQueue — доставка пользовательского сообщения из очереди: клиент рисовал его
    // призраком, поэтому live-баллон бродкастим так же, как для сервер-инициированных отправок.
    private async Task SendDirectAsync(string sessionId, SessionEntry entry, string text,
        IReadOnlyList<string> attachedPaths, string? mode, bool systemDirective, bool auto,
        string? senderPersonaId, bool suppressTasksExecute, string? senderOrigin, string? senderConnectionId = null,
        bool fromQueue = false, string? staffNote = null, DeliveryCause cause = DeliveryCause.Unknown,
        int agentDepth = 0, string? senderUserId = null, bool queuedShown = false)
    {
        // Общая точка доставки хода: сюда идут и три входа с отметкой, и разбор очереди отложенных
        // после рестарта (DrainRestoredPendingAsync → DeliverPendingAsync), которая отметки не ставила
        _store.ClaimFirstMessage(entry);
        // Проект вышел из сферы персоны чата: ход не стартует, персону молча не подменяем
        // (S3-7: проверка опущена к владельцу зоны — ChatSettings.ZoneRefusalForTurn).
        if (_settings.ZoneRefusalForTurn(entry) is { } refusal)
        {
            await RefuseTurnAsync(sessionId, entry, refusal);
            // Ход не стартует — снять строку «В очереди», сама ClaudeSession фазу не закроет
            if (queuedShown) await _store.BroadcastAsync(sessionId, new TurnStartPhaseMessage("done"));
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
        if (!systemDirective && (auto || fromQueue) && _process.DeviceWaitGate(entry) is { } deviceGate)
        {
            await _process.ParkForDeviceAsync(sessionId, entry, deviceGate, new DeviceWaitMessage(
                Guid.NewGuid().ToString("N"), text,
                fromQueue && !auto ? DeviceWaitMessage.RouteUser : DeviceWaitMessage.RouteAuto,
                DateTime.UtcNow, senderPersonaId, senderOrigin, agentDepth,
                SuppressTasksExecute: suppressTasksExecute, StaffNote: staffNote,
                AttachedPaths: attachedPaths.Count > 0 ? attachedPaths : null, Mode: mode,
                SenderUserId: senderUserId));
            if (queuedShown) await _store.BroadcastAsync(sessionId, new TurnStartPhaseMessage("done"));
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
        if (mode is not null && Enum.TryParse<ClaudeMode>(mode, true, out var parsedMode)
            && (parsedMode != ClaudeMode.Plan || _settings.SupportsPlanMode(entry.Info.Model)))
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
                _store.SaveSessions();
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
        await _settings.RouteGroupSpeakerAsync(sessionId, entry, text);

        // Аккаунт чата могли исчерпать другие чаты, пока этот простаивал: перед ходом
        // тихо перевозим его на здоровую подписку пула (та же модель и эндпоинт)
        _process.TryPoolFailover(sessionId, entry);

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
                SenderUserId: SessionStore.HumanAuthorOf(senderUserId, agentDepth, senderPersonaId));
            if (!auto && !fromQueue && senderConnectionId is not null)
                await _store.BroadcastExceptAsync(sessionId, senderConnectionId, userMsg);
            else
                await _store.BroadcastAsync(sessionId, userMsg);
        }

        // Локальный голосовой ход (место chat-voice на «Локальная»): CLI-процесс не нужен,
        // достаточно аккумулятора истории — ответ принесёт RunLocalVoiceTurnAsync. Гейт
        // обязан быть детерминирован на протяжении всего SendDirectAsync (второй вызов
        // ниже, перед диспетчеризацией): все его входы — константы вызова плюс ручной
        // тумблер VoiceMode, между вызовами они не меняются.
        var localVoice = _voice.ShouldRunLocalVoice(entry, auto, systemDirective, attachedPaths);
        if (localVoice)
            await _process.EnsureAccumulatorAsync(entry);
        else
            await _process.EnsureProcessAsync(sessionId, entry);

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
            var title = ChatNaming.MakeChatTitle(text);
            if (!string.IsNullOrEmpty(title))
            {
                entry.Info.Name = title;
                _store.SaveSessions();
                // Фоново уточняем заголовок локальной моделью (best-effort, не блокирует ход)
                _ = _naming.RefineChatTitleAsync(sessionId, text, title, _store.ResolveOwnerId(entry.Info));
            }
        }

        await _store.ApplyStatusAsync(sessionId, entry, SessionStatus.Working);

        entry.Accumulator?.OnUserMessage(text, attachedPaths, viaAgent: agentDepth >= 1, systemDirective: systemDirective, auto: auto, senderPersonaId: senderPersonaId, senderOrigin: senderOrigin, staffNote: staffNote,
            senderUserId: SessionStore.HumanAuthorOf(senderUserId, agentDepth, senderPersonaId));
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
        SessionStore.NoteRunDelegation(entry, agentDepth, suppressTasksExecute, fromHuman: !auto && !systemDirective);
        var humanTurn = !auto && !systemDirective && agentDepth == 0 && !suppressTasksExecute;
        _store.NoteTurnActor(entry, senderUserId, humanTurn);
        Volatile.Write(ref entry.TurnFromHuman, humanTurn ? 1 : 0);
        // Факт «человек ответил» на шину (FS-E3a): по нему подписчик снимает ожидание человека
        // в задачах. Fire-and-forget по контракту Notification: наблюдатель не держит ход, его
        // падение гасит шина. Признак ровно тот же, что у актора и защёлки траты выше: агентская,
        // авто- и директивная подача — не ответ человека. Ключи — из хода (Owner — резолв сессии,
        // не «текущий пользователь»). Номер хода — ближайший будущий: SubmitTurn присвоит его
        // этой подаче (контракт события); у локального голосового хода CLI-прогона нет — 0.
        if (humanTurn)
            _ = _process.TurnEvents?.PublishAsync(new Turn.HumanMessageAccepted(
                new Turn.TurnContext(sessionId, _store.ResolveOwnerId(entry.Info),
                    localVoice ? 0 : (int)entry.Process!.SubmittedTurnSeq + 1, agentDepth,
                    entry.Info.ProjectId)));
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
                try { await _voice.RunLocalVoiceTurnAsync(sessionId, entry); }
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
                ? _voice.VoiceModelFor(_store.ResolveOwnerId(entry.Info))
                : null;
            // Блокер: override на модели ДРУГОГО провайдера ломает транскрипт — CLI стартует
            // в чужом профиле (CLAUDE_CONFIG_DIR) с --resume на транскрипт, которого там нет
            // («No conversation found» / ход без контекста). Штатный переезд провайдера идёт
            // только через MigrateProviderAsync с guard'ом, а override его обходит. Поэтому
            // override применяем, только если провайдер модели override совпадает с провайдером
            // модели чата (сравнение — через ProviderKey настроек, тот же свод, что у
            // guard'а и SetPersona; профиль локального CLI-провайдера тоже считается
            // другим). Миграцию на каждый ход НЕ
            // делаем: ход идёт на модели чата, как раньше.
            if (voiceModel is not null
                && !string.Equals(_settings.ProviderKey(voiceModel), _settings.ProviderKey(entry.Info.Model),
                    StringComparison.OrdinalIgnoreCase))
            {
                _log.LogWarning(
                    "Голосовой ход чата {SessionId}: модель места chat-voice «{VoiceModel}» (провайдер {VoiceKey}) " +
                    "не совпадает с провайдером модели чата {ChatKey} — ход идёт на модели чата, транскрипт не мигрируем",
                    sessionId, voiceModel, _settings.ProviderKey(voiceModel), _settings.ProviderKey(entry.Info.Model));
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
    internal string BuildCliTurnText(SessionEntry entry, string text, bool auto, bool systemDirective, string? senderPersonaId)
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
            // (S3-7d) Маркер взводится под PendingLock — тем же замком, что гейт разбора очереди
            // (DrainNextPendingAsync) и все остальные записи маркера: состояние маркера атомарно
            // относительно операций над очередью. На путях продолжения и drain он уже взведён
            // (:3282 / изъятие+подъём в drain) — здесь идемпотентен; на пути тика ожидания и
            // добивания сабагента это единственная точка подъёма.
            lock (entry.PendingLock) entry.LoopTurnInFlight = true;
            // Верификационный ход идёт со своей директивой — рабочий протокол не дописываем
            if (loop.Phase != "verifying")
                result += "\n\n" + OmoPrompts.WorkLoopTurn(loop.Promise);
        }

        // Режим «Командная реализация»: штаб на каждом ходу получает правило «любая работа —
        // через задачу» и свою стадию. Без напоминания координатор срезает углы и делает
        // работу сам — гард на инструменты правки его только останавливает, но не направляет.
        if (entry.Info.TeamImplement is { } team)
            result += "\n\n" + Team.TeamImplementPrompts.CoordinatorTurn(team);

        // Магслова oh-my-claudecode (ultrawork, ralph, autopilot…): хук keyword-detector
        // плагина отключён вместе со всеми хуками (disableAllHooks — иначе окна консоли
        // на хосте), поэтому активируем скилл сами — дописываем инструкцию его запуска.
        if (OmcKeywordRouting.BuildKeywordHint(text) is { } keywordHint)
            result += "\n\n" + keywordHint;

        // Процессы oh-my-claudecode: советнические роли плагина замещаются
        // персонами-сабагентами с подходящей специальностью (таблица соответствий).
        // Тело — в PersonaLayerBuilder (подготовка к S3-7): это его ResolveOtherPersonas/
        // SplitConsultants, вызов через слой персоны.
        if (_persona.BuildOmcRoleHint(text, entry.Info) is { } routingHint)
            result += "\n\n" + routingHint;

        // Возврат на CLI после локальных голосовых ходов: транскрипт CLI реплик разговора
        // не знает, без сводки модель отвечала бы так, будто разговора не было. Берём
        // хвост истории (лента едина) тем же лимитом, что и разговорный контекст.
        if (entry.LocalTurnsSinceCli > 0 && entry.Accumulator is { } voiceAcc)
        {
            var summary = LocalVoiceTurn.BuildVoiceContextBlock(voiceAcc);
            if (!string.IsNullOrEmpty(summary))
                result = summary + "\n\n" + result;
            entry.LocalTurnsSinceCli = 0;
        }

        return result;
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

        _store.ClaimFirstMessage(entry);

        // Занята только при реально идущем ходе (Working) или ожидании человека (Waiting).
        // Starting у живой сессии означает лишь «создан, ход ещё не запускался»: после первого
        // хода статус идёт Working→Active/Finished и назад в Starting не возвращается (обратно
        // Starting ставит только рестарт → Orphaned). Поэтому свежесозданный чат (у него Process
        // уже присвоен в StartNewSessionAsync, но ходов не было) НЕ занят — принимаем сообщение
        // и стартуем первый ход. Гонку двух одновременных ходов ловит TurnWaiter ниже.
        var status = entry.Info.Status;
        if (status is SessionStatus.Working or SessionStatus.Waiting)
        {
            var queued = await _pending.EnqueuePendingAsync(sessionId, entry, text, senderPersonaId, senderOrigin, agentDepth,
                senderChatName: senderChatName, senderUserId: senderUserId);
            // Ответ человека через REST принят в очередь — факт «человек ответил» уходит на шину
            // сразу, не дожидаясь доставки (дубль уже публиковался при первой постановке)
            if (queued is SendAndWaitResult.Queued { Duplicate: false })
                PublishRestHumanMessage(sessionId, entry, agentDepth, senderPersonaId, senderUserId, systemDirective);
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
        if (_process.DeviceWaitGate(entry) is { } deviceGate)
            return await _process.ParkForDeviceAsync(sessionId, entry, deviceGate, new DeviceWaitMessage(
                Guid.NewGuid().ToString("N"), text, DeviceWaitMessage.RouteAgent, DateTime.UtcNow,
                senderPersonaId, senderOrigin, agentDepth, SenderChatName: senderChatName,
                SenderUserId: senderUserId));

        await _process.EnsureProcessAsync(sessionId, entry);
        entry.Accumulator?.SetPersona(entry.Info.PersonaId);

        // Авто-имя по первому сообщению — как при отправке человеком
        if (string.IsNullOrWhiteSpace(entry.Info.Name) && !entry.Info.NameLocked && entry.Info.ClaudeSessionId is null)
        {
            var title = ChatNaming.MakeChatTitle(text);
            if (!string.IsNullOrEmpty(title))
            {
                entry.Info.Name = title;
                _store.SaveSessions();
                // Фоново уточняем заголовок локальной моделью (best-effort, не блокирует ход)
                _ = _naming.RefineChatTitleAsync(sessionId, text, title, _store.ResolveOwnerId(entry.Info));
            }
        }

        // Один ожидатель на ход: параллельная отправка проиграла гонку — в очередь БЕЗ
        // прерывания (чужой ход только стартует, рубить его чужим сообщением нельзя)
        var tcs = new TaskCompletionSource<TurnResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (Interlocked.CompareExchange(ref entry.TurnWaiter, tcs, null) is not null)
        {
            var raced = await _pending.EnqueuePendingAsync(sessionId, entry, text, senderPersonaId, senderOrigin, agentDepth,
                senderChatName: senderChatName, senderUserId: senderUserId);
            if (raced is SendAndWaitResult.Queued { Duplicate: false })
                PublishRestHumanMessage(sessionId, entry, agentDepth, senderPersonaId, senderUserId, systemDirective);
            return raced;
        }
        entry.TurnWaiterBaseline = entry.Accumulator?.GetAll().Count ?? 0;

        await _store.ApplyStatusAsync(sessionId, entry, SessionStatus.Working);
        entry.Accumulator?.OnUserMessage(text, [], viaAgent: agentDepth >= 1, senderPersonaId: senderPersonaId,
            systemDirective: systemDirective, senderOrigin: senderOrigin,
            senderUserId: SessionStore.HumanAuthorOf(senderUserId, agentDepth, senderPersonaId));
        entry.TurnInWorktree = false; // сообщение = новый ход в основном дереве (зеркало Extract)
        entry.CurrentTurnSnapshot = null; // ход агента — по «Стоп» в композер не возвращается
        entry.TeamTurnFromHuman = false; // ход поднят агентом (chats_send), не человеком (M7)
        // Превью чата — сообщением агента: адаптер его больше не выставляет (см. ChatPreview)
        entry.Info.LastMessage = ChatPreview(text);
        SessionStore.NoteRunDelegation(entry, agentDepth, suppressTasksExecute: false, fromHuman: false);
        _store.NoteTurnActor(entry, senderUserId, fromHuman: false);
        Volatile.Write(ref entry.TurnFromHuman, 0); // chats_send — всегда агентский ход, даже с id человека
        PublishRestHumanMessage(sessionId, entry, agentDepth, senderPersonaId, senderUserId, systemDirective);
        await entry.Process!.SendMessageAsync(text, null, agentDepth);

        if (timeout <= TimeSpan.Zero) return new SendAndWaitResult.Running();
        var completed = await Task.WhenAny(tcs.Task, Task.Delay(timeout));
        return completed == tcs.Task
            ? new SendAndWaitResult.Completed(await tcs.Task)
            : new SendAndWaitResult.Running(); // ход продолжается, ожидатель очистит OnMessageAsync
    }

    // REST-канал (POST /sessions/{id}/messages) принимает ответ человека мимо SendDirectAsync,
    // поэтому его факт на шину издаёт отдельно (FS-E3 доработка): без этого ответ в чате задачи
    // не снимал ожидание человека. Человек — тот же признак, что у автора реплики истории
    // (HumanAuthorOf): глубина 0, персоны-отправителя нет, id пользователя известен. Агентская
    // подача (caller-сессия → глубина ≥ 1), будильник сторожа (без пользователя) события не дают.
    private void PublishRestHumanMessage(string sessionId, SessionEntry entry, int agentDepth,
        string? senderPersonaId, string? senderUserId, bool systemDirective)
    {
        // Директивная подача (передача чата, тёплая просьба) — не ответ человека, как и у издателя E3a
        if (systemDirective) return;
        if (SessionStore.HumanAuthorOf(senderUserId, agentDepth, senderPersonaId) is null) return;
        _ = _process.TurnEvents?.PublishAsync(new Turn.HumanMessageAccepted(
            new Turn.TurnContext(sessionId, _store.ResolveOwnerId(entry.Info),
                (int)(entry.Process?.SubmittedTurnSeq ?? 0) + 1, agentDepth, entry.Info.ProjectId)));
    }

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
            await _pending.EnqueuePendingAsync(sessionId, entry, text, senderPersonaId, senderOrigin,
                agentDepth: 0, silent, suppressTasksExecute, senderChatName: null, kind: kind,
                staffNote: staffNote, senderUserId: senderUserId);
            return true;
        }

        await SendMessageAsync(sessionId, text, [], auto: true, senderPersonaId: senderPersonaId,
            suppressTasksExecute: suppressTasksExecute, senderOrigin: senderOrigin, staffNote: staffNote,
            cause: DeliveryCause.Direct, senderUserId: senderUserId);
        return false;
    }

    // ITeamTurnIntake (аддендум): ядро приёма — реализующий штабной шов. Фасад держит
    // однострочную переадресацию на эти тела (места new SessionManager и код Services/Team
    // не меняются).
    Task<bool> Team.ITeamTurnIntake.SendOrEnqueueAsync(string sessionId, string text,
        string? senderPersonaId, bool silent, bool suppressTasksExecute, string? staffNote, string? senderUserId)
        => SendOrEnqueueAsync(sessionId, text, senderPersonaId,
            silent: silent, suppressTasksExecute: suppressTasksExecute, staffNote: staffNote,
            senderUserId: senderUserId);

    // «Прервать ход» от штаба (карточка разрешения): тот же InterruptCore хоста процесса,
    // что и «Стоп» человека, но без отметки «остановлен пользователем».
    public void InterruptTurn(string sessionId) => _process.InterruptCore(sessionId, byUser: false);

    // Прервать идущий ход РАДИ очереди: убитый процесс result не пришлёт, поэтому доставку
    // разберёт exited того же прогона (DrainOnExitedRun). В отличие от «Стоп» очередь НЕ
    // морозится — прерывание здесь и есть требование доставить ждущее сообщение сейчас.
    // byUser — перебой затеял человек (кнопка на карточке очереди, его сообщение в ход, ждавший
    // его ответа): живая лента ставит на нём отметку «Ход остановлен пользователем», история
    // обязана её повторить, иначе после F5 разъедется с ней (и со сверкой длин на фронте).
    private void PreemptTurnForQueue(string sessionId, SessionEntry entry, string callsite, bool byUser)
    {
        if (byUser) _process.RecordUserInterrupt(sessionId, entry);
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
        // (S3-7d) Под PendingLock — тем же замком, что гейт drain'а: снятие атомарно относительно
        // последующего разбора, цель которого и есть доставить ждущее сообщение сейчас.
        lock (entry.PendingLock) entry.LoopTurnInFlight = false;
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
        if (removedWaiting) _store.SaveSessions();
        if (removed) await _pending.BroadcastPendingAsync(sessionId, entry);
        return removed;
    }

    // Достать следующее сообщение и отправить его обычным ходом. Вызывается по концу хода
    // (OnMessageAsync). Замороженная «Стоп» очередь не разбирается автоматически — только
    // возобновление (новое пользовательское сообщение при свободном чате) снимает заморозку.
    // Ошибка отправки не должна ронять разбор — сообщение уже снято с очереди, иначе оно
    // застряло бы навсегда и блокировало остальные.
    public async Task DrainNextPendingAsync(string sessionId)
    {
        // Окно штатной остановки: очередь осталась в памяти и новый ход не запускает
        if (Stopping) return;
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
                    // «активным» без движения. Продолжение пройдёт гейты само: если параллельный
                    // запуск (по result) уже взвёл маркер — оно сразу уступит. Тело цикла —
                    // у его владельца (фасад до шага 2), переходный хук ниже — единственная
                    // точка, где ядро знает о существовании цикла.
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
            var request = OnLoopContinueRequested;
            if (request is not null)
                _ = Task.Run(async () =>
                {
                    try { await request(sessionId); }
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
            await _pending.BroadcastPendingAsync(sessionId, entry);
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
    // Возвращает true, если разослали фазу «queued» (ждать пришлось): тогда выходы доставки
    // без старта хода обязаны снять строку фазой «done».
    private async Task<bool> AwaitPreviousTurnExitAsync(string sessionId, SessionEntry entry)
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
            return false;
        }
        // Предыдущий прогон уже мёртв — ждать нечего, строку «В очереди» не показываем.
        if (!SessionStore.Busy(entry.Process)) return false;

        // Карточка очереди к этому моменту уже снята (pending_messages), а баллон сообщения
        // появится только после ожидания: без фазы человек ~секунды видит пустоту.
        // Эфемерное событие в группу сессии — в history.json не пишется.
        // Исключение после рассылки queued (Interrupt, сам Broadcast) не должно оставить строку
        // «В очереди» висеть: гасим фазой done и пробрасываем дальше.
        try
        {
            await _store.BroadcastAsync(sessionId, new TurnStartPhaseMessage("queued"));

            var started = DateTime.UtcNow;
            var orchestration = entry.Process?.OrchestrationActive == true && entry.Process?.HasLiveTurn != true;
            var ceilingHit = false;
            var deadline = started + TimeSpan.FromSeconds(ceiling);
            while (SessionStore.Busy(entry.Process))
            {
                if (DateTime.UtcNow >= deadline)
                {
                    ceilingHit = true;
                    _log.LogInformation(
                        "Ожидание смерти прогона {Session} истекло ({Ceiling} с) — прерываю доживание ради отложенной доставки",
                        sessionId, ceiling);
                    entry.Process?.Interrupt();
                    // Interrupt асинхронен: прогон финализируется за секунды, даём короткий grace.
                    var grace = DateTime.UtcNow + TimeSpan.FromSeconds(2);
                    while (SessionStore.Busy(entry.Process) && DateTime.UtcNow < grace)
                        await Task.Delay(50);
                    break;
                }
                await Task.Delay(50);
            }
            // orchestration=true — прогон мёртв, держит только фолбэк-оркестрация (OrchestrationActive).
            _log.LogInformation(
                "Ожидание прогона {Session} перед отложенной доставкой: waitedMs={WaitedMs} ceilingHit={CeilingHit} orchestration={Orchestration}",
                sessionId, (long)(DateTime.UtcNow - started).TotalMilliseconds, ceilingHit, orchestration);
        }
        catch
        {
            await _store.BroadcastAsync(sessionId, new TurnStartPhaseMessage("done"));
            throw;
        }
        return true;
    }

    // Доставка конкретного сообщения из очереди обычным ходом (без повторных гейтов очереди).
    // Пользовательское идёт со своими вложениями и режимом; агентское — как серверная отправка.
    // Возвращает true, если доставка выполнена (ход стартовал — можно ждать result); false —
    // бросилась внутри, ход не поднялся и его повторная обработка по result не придёт (Minor 3).
    internal async Task<bool> DeliverPendingAsync(string sessionId, SessionEntry entry, QueuedMessage next)
    {
        // Сериализация отложенной доставки на смерти предыдущего хода (инцидент 2026-08-10, П2-Ф1):
        // статус Active ставится по result РАНЬШЕ, чем ClaudeSession отпустит _turnLock и финализирует
        // прогон. Старт нового хода в этом окне гоняется с доживающим процессом — same-process reuse
        // (Ч1), лок миграции (Ч2 — держатель .jsonl жив), interrupt свежего прогона. Ждём, пока прогон
        // умрёт (HasLiveTurn=false) И фолбэк-оркестрация снимется (OrchestrationActive=false) — тогда
        // _turnLock свободен, транзрипт закрыт, новый ход стартует свежим процессом. Только для
        // отложенной доставки (из Pending): свежий ход человека через SendDirectAsync сюда не попадает.
        var queuedShown = await AwaitPreviousTurnExitAsync(sessionId, entry);

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
            // catch ниже — тоже под PendingLock). После отзыва цикла уже нет: StopChatsOfRevokedMember
            // снимает его через InterruptCore, который гасит и маркер, — гард не срабатывает и ветка
            // доходит только до общего пути (зафиксировано тестом
            // ОтзывВОкнеДоставки_АктивныйЦиклИМаркерСнятыСтопом_ХодСиротаНеПоднят).
            if (entry.Info.WorkLoop is not null)
                lock (entry.PendingLock) entry.LoopTurnInFlight = false;
            if (queuedShown) await _store.BroadcastAsync(sessionId, new TurnStartPhaseMessage("done"));
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
                    senderUserId: next.SenderUserId, queuedShown: queuedShown);
            else
                // Глубина сообщения едет в ход: агентское из очереди так же делегированное,
                // как поданное сразу (иначе chats_send, отстоявший очередь, получил бы
                // возможности хода человека)
                await SendMessageCoreAsync(sessionId, next.Text, [], mode: null, systemDirective: false, auto: true,
                    senderPersonaId: next.SenderPersonaId, suppressTasksExecute: next.SuppressTasksExecute,
                    senderOrigin: next.SenderOrigin, senderConnectionId: null, staffNote: next.StaffNote,
                    cause: DeliveryCause.QueueAgent, agentDepth: next.AgentDepth,
                    senderUserId: next.SenderUserId, queuedShown: queuedShown);
            return true;
        }
        catch (Exception ex)
        {
            // При активном цикле drain выставил LoopTurnInFlight=true до отправки. Если ход
            // так и не стартовал — ход-то не пришлёт result, и маркер повис бы навсегда,
            // заблокировав разбор очереди. Сбрасываем, чтобы цикл не завис на мёртвой итерации.
            // (S3-7c) Снятие — под PendingLock, как подъём маркера в DrainNextPendingAsync:
            // гейт разбора очереди читает маркер под этим же замком, состояние маркера меняется
            // атомарно относительно операций над очередью. Сам этот catch очередь не трогает.
            if (entry.Info.WorkLoop is not null)
                lock (entry.PendingLock) entry.LoopTurnInFlight = false;
            Console.Error.WriteLine($"[SessionManager] Доставка отложенного сообщения ({sessionId}): {ex.Message}");
            if (queuedShown) await _store.BroadcastAsync(sessionId, new TurnStartPhaseMessage("done"));
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
        // Тот же признак видимости приватного чата, что у шлюзов стора (GetOwnedVisible,
        // VisibleTo): после вывода автора из проекта CanSee гаснет (ProjectAccess.Check = null),
        // и стартующий ход-сирота сливал бы события в user_{id} отозванного. Id автора входит
        // только в видимость его же чата; право уже реализовано RemoveAsync — здесь перечитывается
        // его следствие, нового права никто не получает. С S3-7 резолв проекта по сессии — внутри
        // стора (ProjectManager в зависимости ядра не входит): SessionStore.VisibleTo — ровно
        // SessionAccess.CanSee с тем же резолвом, осиротевший чат не виден (fail-closed).
        return !_store.VisibleTo(entry.Info, author);
    }

    // Режим прав, выбранный в Composer (селектор SetMode и сообщение с mode — одна точка
    // смены). Раньше он доезжал до сессии только вместе с сообщением (см. SendMessageAsync),
    // и выбор, сделанный до первого хода, терялся при уходе со страницы: UI перечитывал
    // Session.Mode и показывал прежний режим. На сам ход это не влияет — процесс claude всё
    // равно пересоздаётся в RunTurnAsync и читает --permission-mode из Info.Mode.
    // userId задан (вызов из API) — сверяется с владельцем; null — внутренний вызов.
    public Session? SetMode(string sessionId, string mode, string? userId = null)
    {
        if (!_sessions.TryGetValue(sessionId, out var entry)) return null;
        if (userId is not null && _store.ResolveOwnerId(entry.Info) != userId) return null;
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
        if (parsed == ClaudeMode.Plan && !_settings.SupportsPlanMode(entry.Info.Model))
            throw new InvalidOperationException("Провайдер не поддерживает режим «План»");
        // M1: гард «координатор не пишет код» держится на permission-запросах CLI, а в
        // acceptEdits/bypassPermissions CLI не спрашивает — такой режим штабу запрещён в
        // любой точке смены, не только при включении режима (аудит 2026-08-01: обход
        // селектором в стадии волны → координатор писал файлы мимо задач).
        if (entry.Info.TeamImplement is { } teamForGuard)
            parsed = PermissionModeGuard.GuardCompatibleMode(parsed, teamForGuard.CoordinatorNoCode);
        if (entry.Info.Mode == parsed) return entry.Info;
        entry.Info.Mode = parsed;
        _store.SaveSessions();
        // Живой ход перенастраиваем на лету (control-протокол set_permission_mode):
        // новый режим применяется уже к идущему ходу, а не только со следующего.
        entry.Process?.TrySetPermissionModeLive(parsed);
        return entry.Info;
    }

}
