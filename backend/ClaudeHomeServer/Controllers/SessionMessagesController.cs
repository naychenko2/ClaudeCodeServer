using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using ClaudeHomeServer.Filters;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ClaudeHomeServer.Controllers;

// Единый доступ к сообщениям сессии по её id — и чаты вне проекта, и проектные сессии
// (в отличие от ChatsController/SessionsController, привязанных к типу сессии).
[ApiController]
[Authorize]
[Route("api/sessions/{sessionId}")]
public class SessionMessagesController(SessionManager sessions, ProjectManager projects) : ControllerBase
{
    // DefaultMapInboundClaims = false → sub не ремапится в NameIdentifier, читаем напрямую
    private string UserId => User.FindFirstValue(JwtRegisteredClaimNames.Sub)!;

    // Максимальная длина текста одного сообщения в выдаче
    private const int MaxTextLength = 2000;

    // Сессия текущего пользователя: у проектной — владелец проекта, у чата — сама сессия.
    // Чужая/несуществующая — как отсутствующая (404). Владелец резолвится в SessionManager.
    private Models.Session? OwnedSession(string sessionId) => sessions.GetOwned(sessionId, UserId);

    // GET /api/sessions/{sid}/history?limit= — последние N сообщений (компактно, тексты усечены)
    [HttpGet("history")]
    public async Task<IActionResult> GetHistory(string sessionId, [FromQuery] int limit = 20)
    {
        var session = OwnedSession(sessionId);
        if (session is null) return NotFound();

        var all = await sessions.GetHistoryAsync(sessionId);
        var items = all
            .Select(ToItem)
            .Where(i => i is not null)
            .ToList();
        var take = Math.Clamp(limit, 1, 200);

        return Ok(new
        {
            sessionId = session.Id,
            name = session.Name,
            projectId = session.ProjectId,
            status = session.Status.ToString().ToLower(),
            total = items.Count,
            items = items.Skip(Math.Max(0, items.Count - take)),
        });
    }

    // POST /api/sessions/{sid}/messages — отправка сообщения в сессию (REST-канал агентов,
    // chats_send). wait="turn" — ждать завершения хода до timeoutSec (clamp 5..240, дефолт 90);
    // wait="none" — не ждать. Истёкший таймаут НЕ отменяет ход целевой сессии.
    [HttpPost("messages")]
    // Анти-рекурсия: делегированный ход не пишет в третьи чаты (раньше — срезанием секции
    // chats из состава инструментов wsp, что перезапускало процесс CLI со всеми MCP)
    [DenyOnDelegatedTurn("Отправка сообщения в другой чат")]
    public async Task<IActionResult> PostMessage(string sessionId, [FromBody] SendSessionMessageRequest req)
    {
        var session = OwnedSession(sessionId);
        if (session is null) return NotFound();

        var text = req.Text?.Trim();
        if (string.IsNullOrEmpty(text))
            return BadRequest(new { error = "Текст сообщения пуст" });

        // Глубина делегирования целевого хода = глубина хода отправителя + 1. Считаем её ЗДЕСЬ,
        // по живой сессии отправителя (X-Caller-Session-Id от chats_send): в env MCP-сервера она
        // протухала при переиспользовании прогона, и делегированный ход мог отправить сообщение
        // с глубиной прошлого хода. Заголовок X-Agent-Depth остаётся фолбэком для прямых вызовов
        // REST-канала; своей глубине отправителя он уступает.
        var agentDepth = Request.Headers.TryGetValue("X-Agent-Depth", out var dh)
            && int.TryParse(dh.FirstOrDefault(), out var d) ? Math.Max(0, d) : 0;
        if (Request.Headers.TryGetValue(DenyOnDelegatedTurnAttribute.CallerHeader, out var ch)
            && ch.FirstOrDefault() is { Length: > 0 } callerSessionId)
            agentDepth = sessions.GetActiveTurnDelegation(callerSessionId, UserId).AgentDepth + 1;

        // Персона-отправитель: chats_send передаёт id своей сессии — берём её PersonaId, чтобы
        // получатель отрисовал входящую реплику лицом персоны. Только сессия того же владельца.
        string? senderPersonaId = null;
        string? senderOrigin = null;
        string? senderChatName = null;
        if (Request.Headers.TryGetValue("X-Sender-Session-Id", out var sh)
            && sh.FirstOrDefault() is { Length: > 0 } senderId)
        {
            var sender = OwnedSession(senderId);
            senderPersonaId = sender?.PersonaId;
            senderOrigin = ResolveSenderOrigin(sender, session);
            // Подпись карточки, когда персоны у отправителя нет: имя его чата отвечает
            // на вопрос «кто пишет» лучше, чем безликое «Входящее сообщение»
            senderChatName = sender?.Name;
        }

        var waitTurn = !string.Equals(req.Wait, "none", StringComparison.OrdinalIgnoreCase);
        var timeout = waitTurn
            ? TimeSpan.FromSeconds(Math.Clamp(req.TimeoutSec ?? 90, 5, 240))
            : TimeSpan.Zero;

        SendAndWaitResult result;
        try
        {
            result = await sessions.SendMessageAndWaitAsync(sessionId, text, timeout, agentDepth, senderPersonaId, senderOrigin, senderChatName);
        }
        catch (InvalidOperationException) { return NotFound(); }

        return result switch
        {
            SendAndWaitResult.Busy b => Conflict(new
            {
                status = "busy",
                currentStatus = b.CurrentStatus.ToString().ToLower(),
                hint = b.CurrentStatus == Models.SessionStatus.Waiting
                    ? "сессия ждёт подтверждения человека — не вклинивайся; не ретраить чаще раза в 30 секунд и не более 2 раз"
                    : "сессия сейчас выполняет ход — попробуй позже; не ретраить чаще раза в 30 секунд и не более 2 раз",
            }),
            // Сессия была занята — сообщение принято и уйдёт после текущего хода. Ответа
            // ждать не на чем: ход чужой, поэтому даже при wait=turn возвращаем 202.
            SendAndWaitResult.Queued q => Accepted(new
            {
                status = "queued",
                position = q.Position,
                duplicate = q.Duplicate,
                hint = q.Duplicate
                    ? "такое же сообщение уже стоит в очереди — повторно не отправляй"
                    : "чат занят: сообщение доставлено в очередь и уйдёт после текущего хода. Не ретрай — ответ смотри через chats_history",
            }),
            SendAndWaitResult.QueueFull f => StatusCode(429, new
            {
                status = "queue_full",
                limit = f.Limit,
                hint = $"в очереди чата уже {f.Limit} сообщений — дождись, пока он их разберёт",
            }),
            SendAndWaitResult.Completed c => Ok(new
            {
                status = "completed",
                reply = c.Result.Reply,
                durationMs = c.Result.DurationMs,
                costUsd = c.Result.CostUsd,
            }),
            // Running: wait=none либо истёк таймаут — ход продолжается
            _ => Accepted(new
            {
                status = "running",
                hint = "ход продолжается — результат позже через chats_history",
            }),
        };
    }

    // Откуда прилетело сообщение — чип-источник у карточки получателя. null, когда
    // отправитель в том же месте: чип показывал бы очевидное и только шумел.
    private string? ResolveSenderOrigin(Models.Session? sender, Models.Session receiver)
    {
        if (sender is null || sender.ProjectId == receiver.ProjectId) return null;
        if (sender.ProjectId is null) return "Вне проектов";
        // Проект мог быть удалён, а сессия ещё жива — источник всё равно чужой
        return projects.GetById(sender.ProjectId)?.Name ?? "Другой проект";
    }

    // POST /api/sessions/{sid}/report — отчёт «наверх», в родительский чат (chats_report_up).
    // Промежуточный отчёт по ходу работы: карточка ложится в ленту родителя бесплатно, ход
    // ему НЕ запускается — он увидит отчёт на своём следующем ходу. Финальный доклад по
    // завершении задачи отправляет сам сервер (TaskExecutionService), и он уже с ходом.
    [HttpPost("report")]
    public async Task<IActionResult> ReportUp(string sessionId, [FromBody] ReportUpRequest req)
    {
        var text = req.Text?.Trim();
        if (string.IsNullOrEmpty(text))
            return BadRequest(new { error = "Текст отчёта пуст" });

        var result = await sessions.ReportUpAsync(sessionId, text, UserId, withTurn: false);
        return result switch
        {
            SessionManager.ReportUpResult.NotFound => NotFound(),
            SessionManager.ReportUpResult.NoParent => BadRequest(new
            {
                status = "no_parent",
                hint = "у этого чата нет родительского — отчитываться некуда",
            }),
            SessionManager.ReportUpResult.TooDeep => BadRequest(new
            {
                status = "too_deep",
                hint = "цепочка автоматических отчётов слишком длинная — доложи человеку в своём чате",
            }),
            _ => Ok(new { status = "delivered", hint = "отчёт лёг в ленту родительского чата" }),
        };
    }

    // GET /api/sessions/{sid}/pending — сообщения, ждущие конца текущего хода
    [HttpGet("pending")]
    public IActionResult GetPending(string sessionId)
    {
        if (OwnedSession(sessionId) is null) return NotFound();
        return Ok(sessions.GetPending(sessionId).Select(p => new
        {
            id = p.Id, text = p.Text, senderPersonaId = p.SenderPersonaId,
            senderOrigin = p.SenderOrigin, enqueuedAt = p.EnqueuedAt,
            senderChatName = p.SenderChatName,
        }));
    }

    // DELETE /api/sessions/{sid}/pending/{messageId} — снять сообщение из очереди
    // (крестик на карточке-призраке). 404 — уже доставлено или отменено.
    [HttpDelete("pending/{messageId}")]
    public async Task<IActionResult> CancelPending(string sessionId, string messageId)
    {
        if (OwnedSession(sessionId) is null) return NotFound();
        return await sessions.CancelPendingAsync(sessionId, messageId) ? NoContent() : NotFound();
    }

    // Компактное представление сообщения истории; служебные записи (thinking, file_changed,
    // стоимость, границы компакции) в выдачу не попадают
    private static object? ToItem(StoredMessage m) => m switch
    {
        StoredUserMessage u => new { kind = "user", text = Truncate(u.Text), viaAgent = u.ViaAgent, senderPersonaId = u.SenderPersonaId, auto = u.Auto },
        // Текст сабагента (ParentToolUseId != null) — внутренность его карточки, в дайджест не идёт
        StoredTextMessage { ParentToolUseId: null } t => new { kind = "assistant", text = Truncate(t.Text), personaId = t.PersonaId },
        StoredToolUseMessage t => new { kind = "tool", name = t.Name, isError = t.IsError } as object,
        StoredResultMessage r => new { kind = "result", subtype = r.Subtype, numTurns = r.NumTurns },
        StoredErrorMessage e => new { kind = "error", text = Truncate(e.Text) },
        _ => null,
    };

    private static string Truncate(string text) =>
        text.Length <= MaxTextLength ? text : text[..MaxTextLength] + "…";
}

// Wait: "turn" (дефолт) — ждать завершения хода, "none" — вернуть 202 сразу.
// TimeoutSec клампится в 5..240 секунд.
public record SendSessionMessageRequest(string? Text, string? Wait = "turn", int? TimeoutSec = 90);

// Отчёт в родительский чат: текст ложится карточкой, ход родителя не запускается
public record ReportUpRequest(string? Text);
