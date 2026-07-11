using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
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
    // Чужая/несуществующая — как отсутствующая (404).
    private Models.Session? OwnedSession(string sessionId)
    {
        var s = sessions.GetById(sessionId);
        if (s is null) return null;
        var ownerId = s.ProjectId is not null
            ? projects.GetById(s.ProjectId)?.OwnerId
            : s.OwnerId;
        return ownerId is not null && ownerId == UserId ? s : null;
    }

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
    public async Task<IActionResult> PostMessage(string sessionId, [FromBody] SendSessionMessageRequest req)
    {
        var session = OwnedSession(sessionId);
        if (session is null) return NotFound();

        var text = req.Text?.Trim();
        if (string.IsNullOrEmpty(text))
            return BadRequest(new { error = "Текст сообщения пуст" });

        // Глубина делегирования: заголовок ставит MCP-сервер (chats_send = depth вызывающего + 1);
        // отсутствует = 0 (обычный клиент). Урезание инструментов по глубине — в адаптере.
        var agentDepth = Request.Headers.TryGetValue("X-Agent-Depth", out var dh)
            && int.TryParse(dh.FirstOrDefault(), out var d) ? Math.Max(0, d) : 0;

        // Персона-отправитель: chats_send передаёт id своей сессии — берём её PersonaId, чтобы
        // получатель отрисовал входящую реплику лицом персоны. Только сессия того же владельца.
        string? senderPersonaId = null;
        if (Request.Headers.TryGetValue("X-Sender-Session-Id", out var sh)
            && sh.FirstOrDefault() is { Length: > 0 } senderId)
        {
            var sender = OwnedSession(senderId);
            senderPersonaId = sender?.PersonaId;
        }

        var waitTurn = !string.Equals(req.Wait, "none", StringComparison.OrdinalIgnoreCase);
        var timeout = waitTurn
            ? TimeSpan.FromSeconds(Math.Clamp(req.TimeoutSec ?? 90, 5, 240))
            : TimeSpan.Zero;

        SendAndWaitResult result;
        try
        {
            result = await sessions.SendMessageAndWaitAsync(sessionId, text, timeout, agentDepth, senderPersonaId);
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

    // Компактное представление сообщения истории; служебные записи (thinking, file_changed,
    // стоимость, границы компакции) в выдачу не попадают
    private static object? ToItem(StoredMessage m) => m switch
    {
        StoredUserMessage u => new { kind = "user", text = Truncate(u.Text), viaAgent = u.ViaAgent, senderPersonaId = u.SenderPersonaId },
        StoredTextMessage t => new { kind = "assistant", text = Truncate(t.Text), personaId = t.PersonaId },
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
