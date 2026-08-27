using ClaudeHomeServer.Models;

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
public sealed class SessionMessagingService(SessionManager sessions, ProjectManager projects,
    TaskManager tasks)
{
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
    /// </summary>
    public async Task<SendOutcome> SendAsync(string ownerId, string sessionId, string? text,
        string? callerSessionId, string? senderSessionId, int agentDepthFallback,
        string? wait, int? timeoutSec)
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

        var waitTurn = !string.Equals(wait, "none", StringComparison.OrdinalIgnoreCase);
        var timeout = waitTurn
            ? TimeSpan.FromSeconds(Math.Clamp(timeoutSec ?? 90, 5, 240))
            : TimeSpan.Zero;

        SendAndWaitResult result;
        try
        {
            result = await sessions.SendMessageAndWaitAsync(sessionId, trimmed, timeout,
                agentDepth, senderPersonaId, senderOrigin, senderChatName);
        }
        catch (InvalidOperationException) { return new SendOutcome.NotFound(); }

        // Квота списана авансом, а сообщение не дошло (дубль, переполнение очереди, занято
        // ожиданием человека) — возвращаем единицу: иначе наивные ретраи агента выжигали
        // бы MaxWakeups без единого реального хода. Принятое в очередь (не дубль) не
        // рефандим — доставка по концу хода ход поднимет.
        if (wakeSpent && result is SendAndWaitResult.Queued { Duplicate: true }
            or SendAndWaitResult.QueueFull or SendAndWaitResult.Busy)
            sessions.RefundTeamWakeup(sessionId);

        return result switch
        {
            SendAndWaitResult.Busy b => new SendOutcome.Busy(b.CurrentStatus),
            SendAndWaitResult.Queued q => new SendOutcome.Queued(q.Position, q.Duplicate),
            SendAndWaitResult.QueueFull f => new SendOutcome.QueueFull(f.Limit),
            SendAndWaitResult.Completed c => new SendOutcome.Completed(
                c.Result.Reply, c.Result.DurationMs, c.Result.CostUsd),
            _ => new SendOutcome.Running(),
        };
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
    /// ход родителю не запускается; blocker=true — постановщика будит ход (Э4).
    /// </summary>
    public async Task<ReportOutcome> ReportUpAsync(string ownerId, string sessionId,
        string? text, bool blocker)
    {
        var trimmed = text?.Trim();
        if (string.IsNullOrEmpty(trimmed)) return new ReportOutcome.EmptyText();

        // Финальный доклад по задаче постановщику уже доставил сервер (TaskExecutionService):
        // второе сообщение об одном факте гасим — именно оно и делало ленту дублирующей.
        // Ownership проверяем сами: ниже её делает SessionManager, а гейт стоит перед ним.
        if (sessions.GetOwned(sessionId, ownerId) is not null
            && IsCompletionAlreadyReported(tasks.GetBySession(sessionId), blocker))
            return new ReportOutcome.AlreadyReported();

        var result = blocker
            ? await sessions.ReportBlockerAsync(sessionId, trimmed, ownerId)
            : await sessions.ReportUpAsync(sessionId, trimmed, ownerId, withTurn: false);
        return result switch
        {
            SessionManager.ReportUpResult.NotFound => new ReportOutcome.NotFound(),
            SessionManager.ReportUpResult.NoParent => new ReportOutcome.NoParent(),
            SessionManager.ReportUpResult.TooDeep => new ReportOutcome.TooDeep(),
            _ => new ReportOutcome.Delivered(),
        };
    }

    // Гейт B5: сессия — чат-исполнитель задачи (TaskManager.GetBySession), по которой доклад
    // о завершении уже доставлен (CAS-флаг CompletionDelivered). null — сессия не привязана
    // к задаче, отчёт идёт обычным путём. Статикой — проверяется юнит-тестом без поднятия
    // приложения.
    //
    // Гейт закрывает ТОЛЬКО финальный доклад (спека B5). CompletionDelivered необратим, и на
    // блокеры он не распространяется: человек продолжает работу в том же чате-исполнителе,
    // персона упирается в блокер и зовёт chats_report_up(blocker: true) — молча съеденный
    // вызов оставил бы координатора без единственного сигнала «встал», а модель получила бы Ok
    // и считала, что доложила.
    internal static bool IsCompletionAlreadyReported(TaskItem? task, bool blocker) =>
        !blocker && task is { CompletionDelivered: true };
}
