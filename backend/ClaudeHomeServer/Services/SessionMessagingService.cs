using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services.Composition;
using ClaudeHomeServer.Services.Tasks;
using ClaudeHomeServer.Services.Sessions;
using Microsoft.Extensions.Logging;

namespace ClaudeHomeServer.Services;

/// <summary>
/// Оркестрация «сообщение в другую сессию» (chats_send) и «отчёт наверх» (chats_report_up) —
/// общая точка ДВУХ потребителей: SessionMessagesController (REST: UI и stdio-ветка MCP)
/// и WorkspaceToolset (http-ветка MCP, ADR-012 волна 3). Вынос по мотивам PersonasCrudService
/// (волна 2): дублировать эту логику в тулсете значило бы гарантированный рассинхрон веток.
/// Принципиальное отличие от PersonasCrudService — сервис НЕ возвращает ActionResult и не
/// собирает анонимные объекты под рефлексию: исход — типизированные record-ы, HTTP-коды
/// и тела мапит контроллер, MCP-тексты — тулсет (долг волны 2, здесь чисто сразу).
///
/// Сюда НЕ входят: гейт делегированного хода (MVC-фильтр [DenyOnDelegatedTurn] на
/// контроллере, DelegatedTurnGate в тулсете — решение единое, точка вызова своя) и состав
/// инструментов. Владение сессией проверяется здесь по ownerId вызывающего — у контроллера
/// это UserId из JWT, у тулсета — владелец сервисного токена.
/// </summary>
/// <param name="sessions">Оркестрация чатов: доставка отчёта и доклада-блокера.</param>
/// <param name="projects">Резолв чипа-источника «откуда прилетело сообщение».</param>
/// <param name="tasks">Резолв задачи по сессии (гейт B5) и постановка ожидания человека (FS-E2).</param>
/// <param name="log">Логирование отказа постановки ожидания; null — без логирования (тесты).</param>
/// <param name="broadcaster">Вещание task_changed после постановки ожидания (FS-E2 ревью, Major 1);
/// null — без вещания (прямые construction-вызовы тестов, не проверяющих событие). В DI —
/// синглтон ISessionBroadcaster (CoreSeamsGlue), как у остальных производителей события.</param>
public sealed class SessionMessagingService(SessionManager sessions, ProjectManager projects,
    TaskManager tasks, ILogger<SessionMessagingService>? log = null,
    ISessionBroadcaster? broadcaster = null)
{
    // FS-E2: логгер нужен одной ветке — отказ постановки ожидания логируется, а не роняет
    // доклад (контракт «отказ установки ожидания доклад не роняет»). Optional-параметр —
    // как в соседних сервисах (WatchdogNotifier): DI подаёт ILogger<T> сам, прямые
    // construction-вызовы тестов не затронуты. Тот же приём у broadcaster: в DI он
    // синглтон (CoreSeamsGlue), в тестах без проверки вещания — null.
    private readonly ILogger? _log = log;
    private readonly ISessionBroadcaster? _broadcaster = broadcaster;

    /// <summary>Исход отправки сообщения в сессию (chats_send).</summary>
    public abstract record SendOutcome
    {
        /// <summary>Сессия не найдена или чужая (как 404 контроллера).</summary>
        public sealed record NotFound : SendOutcome;
        /// <summary>Пустой текст после Trim (как 400 контроллера).</summary>
        public sealed record EmptyText : SendOutcome;
        /// <summary>Квота пробуждения штаба исчерпана (как 403 контроллера).</summary>
        public sealed record TeamWakeDenied(string? Reason) : SendOutcome;
        /// <summary>Сессия занята ходом или ждёт человека (как 409 busy).</summary>
        public sealed record Busy(SessionStatus CurrentStatus) : SendOutcome;
        /// <summary>Принято в очередь занятой сессии (как 202 queued).</summary>
        public sealed record Queued(int Position, bool Duplicate) : SendOutcome;
        /// <summary>Очередь переполнена (как 429 queue_full).</summary>
        public sealed record QueueFull(int Limit) : SendOutcome;
        /// <summary>Ход завершён до таймаута (как 200 completed).</summary>
        public sealed record Completed(string Reply, long DurationMs, double? CostUsd) : SendOutcome;
        /// <summary>wait=none или таймаут — ход продолжается (как 202 running).</summary>
        public sealed record Running : SendOutcome;
    }

    /// <summary>
    /// Отправка сообщения в чужую сессию владельца. Глубина делегирования целевого хода
    /// считается по ЖИВОЙ сессии отправителя (callerSessionId), а не по env/заголовку —
    /// env протухает при переиспользовании живого прогона. agentDepthFallback — значение
    /// заголовка X-Agent-Depth для прямых REST-вызовов без сессии-вызывателя.
    /// callerSessionId != null — признак MCP-вызова: только тогда расходуется квота
    /// пробуждения штаба («Командная реализация»), ход человека/фронта её не тратит.
    /// actorUserId — инициатор прямого вызова без сессии-вызывателя (REST человека); при наличии
    /// вызывателя актор берётся из реестра его хода (S2a-1).
    /// preempt=false — не прерывать рабочий ход получателя (сообщение в очередь, доставка
    /// по result); Waiting прерывается всегда, дефолт true — прежнее поведение.
    /// </summary>
    public async Task<SendOutcome> SendAsync(string ownerId, string sessionId, string? text,
        string? callerSessionId, string? senderSessionId, int agentDepthFallback,
        string? wait, int? timeoutSec, bool preempt = true, string? actorUserId = null)
    {
        var session = sessions.GetOwned(sessionId, ownerId);
        if (session is null) return new SendOutcome.NotFound();

        var trimmed = text?.Trim();
        if (string.IsNullOrEmpty(trimmed)) return new SendOutcome.EmptyText();

        // Глубина делегирования целевого хода = глубина хода отправителя + 1. Считаем по
        // живой сессии отправителя: в env MCP-сервера она протухала при переиспользовании
        // прогона, и делегированный ход мог отправить сообщение с глубиной прошлого хода.
        // Заголовок (fallback) — для прямых вызовов REST-канала; своей глубине он уступает.
        var agentDepth = agentDepthFallback;
        if (callerSessionId is { Length: > 0 })
            agentDepth = sessions.GetActiveTurnDelegation(callerSessionId, ownerId).AgentDepth + 1;

        // Персона-отправитель: получатель отрисует входящую реплику её лицом.
        // Только сессия того же владельца.
        string? senderPersonaId = null;
        string? senderOrigin = null;
        string? senderChatName = null;
        if (senderSessionId is { Length: > 0 } senderId)
        {
            var sender = sessions.GetOwned(senderId, ownerId);
            senderPersonaId = sender?.PersonaId;
            senderOrigin = ResolveSenderOrigin(sender, session);
            // Подпись карточки, когда персоны у отправителя нет: имя его чата отвечает
            // на вопрос «кто пишет» лучше, чем безликое «Входящее сообщение»
            senderChatName = sender?.Name;
        }

        // Режим «Командная реализация» (Э4): агент, пишущий в чат-штаб, поднимает ему платный
        // ход — значит расходует ту же квоту пробуждения, что и доклад-блокер. Без этого
        // бюджет обходился бы соседним инструментом: chats_send вместо chats_report_up.
        var wakeSpent = false;
        if (callerSessionId is not null)
        {
            var wake = sessions.TryConsumeTeamWakeup(sessionId);
            if (wake.TeamMode && !wake.Allowed)
                return new SendOutcome.TeamWakeDenied(wake.Reason);
            wakeSpent = wake.TeamMode;
        }

        // Актор нового хода: сообщение агента наследует актора вызывателя (его живой ход), прямой
        // REST-вызов человека — его же. Без обоих (будильник сторожа) актор неизвестен — fail-closed.
        var turnActor = callerSessionId is { Length: > 0 }
            ? sessions.GetTurnActor(callerSessionId, ownerId)
            : actorUserId;

        var waitTurn = !string.Equals(wait, "none", StringComparison.OrdinalIgnoreCase);
        var timeout = waitTurn
            ? TimeSpan.FromSeconds(Math.Clamp(timeoutSec ?? 90, 5, 240))
            : TimeSpan.Zero;

        // Момент, когда ядро приняло сообщение — «at» для сторожа ожидания (Ф-2.48, ревью
        // R-Ф-2.48): берётся ДО подачи, а не после SendMessageAndWaitAsync. При wait=… ход
        // получателя идёт внутри этого вызова (таймаут 5–240 с), и поднятый в нём новый блокер
        // (report_up → SetAwaitingHuman, Since позже ответа) — не то, что обязано снять данное
        // действие. Взятый после конца ожидания «at» прошёл бы сторож Since<=at и снял свежий
        // блокер — ровно та гонка, ради которой сторож и введён. Инвариант: ожидание, поставленное
        // после начала отправки, этим действием не снимается; поставленное до — снимается.
        var acceptedAt = DateTime.UtcNow;

        SendAndWaitResult result;
        try
        {
            result = await sessions.SendMessageAndWaitAsync(sessionId, trimmed, timeout,
                agentDepth, senderPersonaId, senderOrigin, senderChatName, preempt,
                senderUserId: turnActor);
        }
        catch (InvalidOperationException) { return new SendOutcome.NotFound(); }

        // Квота списана авансом, а сообщение не дошло (дубль, переполнение очереди, занято
        // ожиданием человека) — возвращаем единицу: иначе наивные ретраи агента выжигали
        // бы MaxWakeups без единого реального хода. Принятое в очередь (не дубль) не
        // рефандим — доставка по концу хода ход поднимет.
        if (wakeSpent && result is SendAndWaitResult.Queued { Duplicate: true }
            or SendAndWaitResult.QueueFull or SendAndWaitResult.Busy)
            sessions.RefundTeamWakeup(sessionId);

        // Явный тип: у switch-выражения без целевого типа общего типа веток нет (CS8506,
        // та же находка, что у ReportUpAsync ниже). Тип нужен и по делу: дальше по исходу
        // решаем, было ли сообщение ПРИНЯТО — только принятое есть действие адресата.
        SendOutcome outcome = result switch
        {
            SendAndWaitResult.Busy b => new SendOutcome.Busy(b.CurrentStatus),
            SendAndWaitResult.Queued q => new SendOutcome.Queued(q.Position, q.Duplicate),
            SendAndWaitResult.QueueFull f => new SendOutcome.QueueFull(f.Limit),
            SendAndWaitResult.Completed c => new SendOutcome.Completed(
                c.Result.Reply, c.Result.DurationMs, c.Result.CostUsd),
            _ => new SendOutcome.Running(),
        };

        // Ф-2.48: принятое сообщение (busy/queue_full — не принято, ожидания не трогаем).
        // Дубль — тоже не действие адресата (R-Ф-2.48, минор): тот же текст того же отправителя уже стоит
        // в очереди и будет доставлен, первое его принятие уже учлось как действие (то же чтение, что у
        // рефанда выше — «не дошло»). Дублем можно ошибочно снять новое ожидание, поднятое между первой
        // копией и ретраем: хронологически оно раньше дубля, и сторож по acceptedAt тут бессилен.
        // «at» для стора — принятый до подачи acceptedAt, а не время возврата ожидания хода.
        if (callerSessionId is { Length: > 0 } caller
            && outcome is SendOutcome.Queued { Duplicate: false } or SendOutcome.Completed or SendOutcome.Running)
            await TryClearAwaitingForAddresseeAsync(session, caller, acceptedAt);

        return outcome;
    }

    // Ф-2.48 (разбор Ф-2.47): ожидание человека (AwaitingHumanSince/Reason) — единственная
    // точка правды «жду тебя», и блокер адресован тому, кому ушёл доклад:
    // SessionTaskLinks.ParentSessionId(чата-исполнителя). Снятие блокера по факту было только
    // в режиме штаба (TeamDecisionService.TryResolveBlockerByFactAsync); в конвейере фич, где
    // координатор работает в обычном чате, действие адресата — сообщение в чат исполнителя —
    // ожидания не снимало вовсе (живой случай: плашка «ждёт вас» висела 1 ч 54 мин после
    // ответа координатора).
    //
    // Правила:
    // - Адресат — только родитель чата-получателя по SessionTaskLinks: та же связь, по которой
    //   ReportUpAsync носит доклад постановщику. Совпал с доверенным callerSessionId — это
    //   действие адресата. Заголовок X-Sender-Session-Id (senderSessionId) присылает клиент,
    //   он не доказательство: им можно выдать за адресата любой чат.
    // - Авто-сообщения сервера (довылка после рестарта, будильник сторожа) приходят без
    //   вызывателя — их отрезает первое правило, особого списка не нужно.
    // - Ожидание — у задачи, привязанной к чату-получателю (тот же резолв GetBySession, что у
    //   постановки FS-E2). Чат без задачи или задача без ожидания — ничего не делать.
    // - Сторож «Since <= acceptedAt» держит стор (ClearAwaitingHuman(id, at)): ожидание,
    //   поставленное ПОЗЖЕ принятия сообщения, этим действием не снимается — оно создано не
    //   им (та же гонка «ответ / новый блокер», что у AwaitingHumanAnswerSubscriber).
    // - task_changed — после успешного снятия, payload — обновлённая задача: тот же путь, что
    //   FS-E2 и AwaitingHumanAnswerSubscriber (доска и страница фичи живут только по нему).
    //
    // internal — ровно для теста сторожа: в бою at = момент начала отправки (acceptedAt до
    // вызова SendMessageAndWaitAsync, R-Ф-2.48), в тесте он задаётся явно (настоящей гонки в
    // тесте нет, и флейки тут быть не должно).
    internal async Task TryClearAwaitingForAddresseeAsync(Session target, string callerSessionId,
        DateTime acceptedAt)
    {
        if (SessionTaskLinks.ParentSessionId(target, new TaskLookupAdapter(tasks)) != callerSessionId)
            return;

        var task = tasks.GetBySession(target.Id);
        if (task is null || task.AwaitingHumanSince is null) return;

        try
        {
            var cleared = tasks.ClearAwaitingHuman(task.Id, acceptedAt);
            if (cleared is not null && _broadcaster is not null)
                await _broadcaster.ToTaskBoard(cleared.OwnerId ?? "", cleared.ProjectId,
                    new TaskChangedMessage("updated", cleared));
        }
        catch (InvalidOperationException e)
        {
            // Снятие не роняет саму отправку: сообщение адресат уже написал.
            _log?.LogWarning(e, "chats_send: ожидание человека задаче {TaskId} не снято", task.Id);
        }
    }

    // Откуда прилетело сообщение — чип-источник у карточки получателя. null, когда
    // отправитель в том же месте: чип показывал бы очевидное и только шумел.
    private string? ResolveSenderOrigin(Session? sender, Session receiver)
    {
        if (sender is null || sender.ProjectId == receiver.ProjectId) return null;
        if (sender.ProjectId is null) return "Вне проектов";
        // Проект мог быть удалён, а сессия ещё жива — источник всё равно чужой
        return projects.GetById(sender.ProjectId)?.Name ?? "Другой проект";
    }

    /// <summary>Исход отчёта «наверх», в родительский чат (chats_report_up).</summary>
    public abstract record ReportOutcome
    {
        /// <summary>Пустой текст после Trim (как 400 контроллера).</summary>
        public sealed record EmptyText : ReportOutcome;
        /// <summary>Финальный доклад по задаче уже доставлен сервером (гейт B5).</summary>
        public sealed record AlreadyReported : ReportOutcome;
        /// <summary>Сессия не найдена или чужая (как 404 контроллера).</summary>
        public sealed record NotFound : ReportOutcome;
        /// <summary>Нет родительского чата — отчитываться некуда.</summary>
        public sealed record NoParent : ReportOutcome;
        /// <summary>Цепочка автоматических отчётов слишком длинная.</summary>
        public sealed record TooDeep : ReportOutcome;
        /// <summary>Отчёт лёг в ленту родительского чата.</summary>
        public sealed record Delivered : ReportOutcome;
    }

    /// <summary>
    /// Отчёт в родительский чат. Промежуточный (blocker=false) — карточка в ленте бесплатно,
    /// ход родителю не запускается; blocker=true — постановщика будит ход (Э4) и, если это
    /// чат-исполнитель задачи, ставит её ожидание человека (FS-E2, §6.5 п.2): блокер штаба и
    /// тревога фичи — один факт.
    /// </summary>
    public async Task<ReportOutcome> ReportUpAsync(string ownerId, string sessionId,
        string? text, bool blocker)
    {
        var trimmed = text?.Trim();
        if (string.IsNullOrEmpty(trimmed)) return new ReportOutcome.EmptyText();

        // Финальный доклад по задаче постановщику уже доставил сервер (TaskExecutionService):
        // второе сообщение об одном факте гасим — именно оно и делало ленту дублирующей.
        // Ownership проверяем сами: ниже её делает SessionManager, а гейт стоит перед ним.
        // Задача резолвится один раз: тот же результат кормит гейт B5 и FS-E2-постановку.
        var linkedTask = sessions.GetOwned(sessionId, ownerId) is not null
            ? tasks.GetBySession(sessionId)
            : null;
        if (IsCompletionAlreadyReported(linkedTask, blocker))
            return new ReportOutcome.AlreadyReported();

        var result = blocker
            ? await sessions.ReportBlockerAsync(sessionId, trimmed, ownerId)
            : await sessions.ReportUpAsync(sessionId, trimmed, ownerId, withTurn: false);
        // явный тип: у switch-выражения без целевого типа общего типа веток нет (CS8506)
        ReportOutcome outcome = result switch
        {
            ReportUpResult.NotFound => new ReportOutcome.NotFound(),
            ReportUpResult.NoParent => new ReportOutcome.NoParent(),
            ReportUpResult.TooDeep => new ReportOutcome.TooDeep(),
            _ => new ReportOutcome.Delivered(),
        };

        // FS-E2 (docs/research/features-simplify-architecture-2026-11.md §6.5 п.2): порядок
        // прежний — сначала доклад постановщику, ожидание после него; дошёл только состоявшийся
        // доклад (Queued — тоже доставлен, в очередь родителя). Обычный доклад (blocker=false)
        // ожидания не ставит: промежуточная сводка по ходу работы — не «жду тебя».
        if (blocker && outcome is ReportOutcome.Delivered)
            await TrySetAwaitingHumanForBlocker(linkedTask, trimmed);

        return outcome;
    }

    // FS-E2: чат без задачи — ожидание не ставится (тот же null, что у гейта B5); Done-задача —
    // сигнал устарел: переход в Done уже снял ожидание (TaskManager.Update при переходе в Done
    // вызывает ClearAwaitingHuman), и ставить его после закрытия — «мёртвый» waiting_you на
    // выполненной карточке. Исключение SetAwaitingHuman (несогласованный ask — сегодня
    // единственный источник исключения) ловится и логируется: отказ установки ожидания не
    // роняет доклад — постановщик блокер уже получил.
    private async Task TrySetAwaitingHumanForBlocker(TaskItem? task, string reportText)
    {
        if (task is null || task.Status == TaskItemStatus.Done) return;
        try
        {
            var updated = tasks.SetAwaitingHuman(task.Id, FirstPhrase(reportText));
            // FS-E2 ревью (Major 1): стор фронта живёт только по task_changed (lib/tasks.ts,
            // lib/featurePage.ts) — молчаливая постановка ожидания оставляла доску устаревшей
            // (тот же класс дефекта, что линия паузы W4). Образец — TasksToolset: тот же
            // TaskChangedMessage("updated") через ToTaskBoard. После await: событие несёт уже
            // поставленное ожидание (payload = обновлённый TaskItem), а не состояние до ручки.
            if (updated is not null && _broadcaster is not null)
                await _broadcaster.ToTaskBoard(task.OwnerId ?? "", task.ProjectId,
                    new TaskChangedMessage("updated", updated));
        }
        catch (InvalidOperationException e)
        {
            _log?.LogWarning(e, "report_up(blocker): ожидание человека задаче {TaskId} не поставлено", task.Id);
        }
    }

    // Первая фраза доклада — до первого конца предложения (. ! ?) или конца строки; пустых
    // отрезков нет (текст начинается с разделителя — берётся следующий непустой). Длиннее
    // лимита TaskManager.AwaitingAskMaxLength — обрезка по границе слова: хвост отрезается
    // на последнем пробельном не дальше лимита; первое слово длиннее лимита — жёсткая граница
    // (лимит строгий: SetAwaitingHuman reject'ит ask длиннее). Многоточия нет: оно тоже
    // символ, а лимит — про текст причины.
    internal static string FirstPhrase(string text)
    {
        var start = 0;
        while (true)
        {
            var end = start;
            while (end < text.Length && !IsSentenceEndAt(text, end)) end++;
            var phrase = text[start..end].Trim();
            // Отрезок без буквы/цифры — не фраза («...» — пунктуация, а не причина): пропускаем.
            // Фраз нет вовсе (текст из одних разделителей) — весь текст, тоже под лимитом
            // (причина не пустеет).
            if (phrase.Length > 0 && HasWordChar(phrase)) return LimitToWordBoundary(phrase);
            if (end == text.Length) return LimitToWordBoundary(text.Trim());
            start = end + 1;
        }
    }

    // FS-E2 ревью (минор): '.' '!' '?' — конец предложения только за концом текста или за
    // пробелом: точка внутри URL (docs/adr/012.md) или домена — не граница, иначе причина
    // блокера резалась по первой точке ссылки. '\r' '\n' — безусловны: перевод строки —
    // граница строки независимо от следующего символа.
    private static bool IsSentenceEndAt(string text, int i) => text[i] switch
    {
        '\r' or '\n' => true,
        '.' or '!' or '?' => i + 1 == text.Length || char.IsWhiteSpace(text[i + 1]),
        _ => false,
    };

    // В отрезке есть буква или цифра — он несёт текст; нет — это пунктуация/пробелы.
    private static bool HasWordChar(string s)
    {
        foreach (var c in s)
            if (char.IsLetterOrDigit(c)) return true;
        return false;
    }

    private static string LimitToWordBoundary(string phrase)
    {
        const int limit = TaskManager.AwaitingAskMaxLength;
        if (phrase.Length <= limit) return phrase;
        // Последний пробельный в окне [1..limit]: cut на нём оставляет префикс ≤limit,
        // не разрезая слово (символ на позиции cut — пробел, он отрезан).
        for (var cut = limit; cut > 0; cut--)
            if (char.IsWhiteSpace(phrase[cut])) return phrase[..cut];
        return phrase[..limit];
    }

    // Гейт B5: сессия — чат-исполнитель задачи (TaskManager.GetBySession), по которой доклад
    // о завершении уже доставлен (CAS-флаг CompletionDelivered). null — сессия не привязана
    // к задаче, отчёт идёт обычным путём. Статикой — проверяется юнит-тестом без поднятия
    // приложения.
    //
    // Гейт закрывает ТОЛЬКО финальный доклад (спека B5). CompletionDelivered снимается только
    // штатным переоткрытием задачи (уход из Done через TaskManager.Update), и на блокеры он не
    // распространяется: человек продолжает работу в том же чате-исполнителе,
    // персона упирается в блокер и зовёт chats_report_up(blocker: true) — молча съеденный
    // вызов оставил бы координатора без единственного сигнала «встал», а модель получила бы Ok
    // и считала, что доложила.
    internal static bool IsCompletionAlreadyReported(TaskItem? task, bool blocker) =>
        !blocker && task is { CompletionDelivered: true };
}
