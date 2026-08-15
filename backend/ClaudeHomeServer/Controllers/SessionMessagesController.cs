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
public class SessionMessagesController(SessionManager sessions, ProjectManager projects,
    PromptSnapshotStore promptSnapshots, PromptAuditService promptAudit, TaskManager tasks) : ControllerBase
{
    // DefaultMapInboundClaims = false → sub не ремапится в NameIdentifier, читаем напрямую
    private string UserId => User.FindFirstValue(JwtRegisteredClaimNames.Sub)!;

    // Максимальная длина текста одного сообщения в выдаче
    private const int MaxTextLength = 2000;

    // Сессия текущего пользователя: у проектной — владелец проекта, у чата — сама сессия.
    // Чужая/несуществующая — как отсутствующая (404). Владелец резолвится в SessionManager.
    private Models.Session? OwnedSession(string sessionId) => sessions.GetOwned(sessionId, UserId);

    // GET /api/sessions/{sid}/prompt/{snapshotId} — что ушло модели на этом ходу
    // (секции системного промпта, текст хода, аргументы запуска, доступная часть слоя CLI).
    // Тексты файлов слоя CLI по умолчанию НЕ отдаются — один CLAUDE.md с раскрытыми
    // импортами весит десятки КБ, а разворачивают его редко: вместо текста идёт size,
    // а сам текст берётся ниже по требованию. withFiles=true — отдать всё разом.
    // 404 — снимка нет: штатный случай, его вытеснил ретеншн последних 50 ходов чата.
    [HttpGet("prompt/{snapshotId}")]
    public IActionResult GetPromptSnapshot(string sessionId, string snapshotId,
        [FromQuery] bool withFiles = false)
    {
        var session = OwnedSession(sessionId);
        if (session is null) return NotFound();

        // Путь строим от id РЕЗОЛВНУТОЙ сессии, а не от строки из URL: та становится
        // сегментом пути к файлу (стор её тоже валидирует, но полагаться на это не станем)
        var snapshot = promptSnapshots.Load(session.Id, snapshotId);
        if (snapshot is null) return NotFound();

        if (!withFiles && snapshot.CliLayer?.Files is { Count: > 0 } files)
            snapshot = snapshot with
            {
                CliLayer = snapshot.CliLayer with
                {
                    Files = [.. files.Select(f => f with { Text = "", Size = f.Text.Length })],
                },
            };

        return Ok(snapshot);
    }

    // GET /api/sessions/{sid}/prompt/{snapshotId}/file?key= — текст одного файла слоя CLI.
    // Ленивый догруз для строк, которые человек раскрыл: ключ — путь файла из снимка.
    [HttpGet("prompt/{snapshotId}/file")]
    public IActionResult GetPromptSnapshotFile(string sessionId, string snapshotId,
        [FromQuery] string? key)
    {
        var session = OwnedSession(sessionId);
        if (session is null) return NotFound();
        if (string.IsNullOrWhiteSpace(key)) return BadRequest(new { error = "Не указан файл" });

        // Ключ сверяется со списком ИЗ СНИМКА, а не читается с диска: наружу отдаётся
        // только то, что мы сами туда положили при сборке хода
        var file = promptSnapshots.Load(session.Id, snapshotId)?.CliLayer?.Files
            ?.FirstOrDefault(f => f.Key == key);

        return file is null ? NotFound() : Ok(file);
    }

    // POST /api/sessions/{sid}/prompt/{snapshotId}/analyze — разбор промпта хода моделью
    // («что лишнее и как сократить»). По умолчанию наружу уходят только метаданные секций;
    // includeText=true — человек осознанно разрешил приложить фрагменты их текста.
    [HttpPost("prompt/{snapshotId}/analyze")]
    public async Task<IActionResult> AnalyzePromptSnapshot(string sessionId, string snapshotId,
        [FromBody] AnalyzePromptRequest? req, CancellationToken ct)
    {
        var session = OwnedSession(sessionId);
        if (session is null) return NotFound();

        var snapshot = promptSnapshots.Load(session.Id, snapshotId);
        if (snapshot is null) return NotFound();

        string? result;
        try
        {
            result = await promptAudit.AnalyzeAsync(
                session.Id, snapshot, req?.IncludeText ?? false, UserId, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Исполнитель места prompt-audit не настроен либо вход в claude протух —
            // отдаём причину текстом: по «не удалось разобрать» человек ничего не починит
            return BadRequest(new { error = $"Разбор не удался: {ex.Message}" });
        }

        // null — по этой сессии разбор уже идёт: второй вызов был бы вторым платным
        if (result is null) return Conflict(new { error = "Разбор этого чата уже идёт" });

        return string.IsNullOrWhiteSpace(result)
            ? BadRequest(new { error = "Модель для разбора вернула пустой ответ — проверьте, кто назначен на место «Разбор промпта хода» в «Поставщиках моделей»" })
            : Ok(new { analysis = result });
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

        // Режим «Командная реализация» (Э4): агент, пишущий в чат-штаб, поднимает ему платный
        // ход — значит расходует ту же квоту пробуждения, что и доклад-блокер. Без этого
        // бюджет обходился бы соседним инструментом: chats_send вместо chats_report_up.
        // Ход человека и фронта сюда не попадает — только вызовы MCP (есть CallerHeader).
        var wakeSpent = false;
        if (Request.Headers.ContainsKey(DenyOnDelegatedTurnAttribute.CallerHeader))
        {
            var wake = sessions.TryConsumeTeamWakeup(sessionId);
            if (wake.TeamMode && !wake.Allowed)
                return StatusCode(StatusCodes.Status403Forbidden, new
                {
                    error = $"Сообщение в чат-штаб недоступно: {wake.Reason}. Доложи результат "
                        + "в своей задаче — координатор увидит его, когда человек разрешит продолжить.",
                });
            wakeSpent = wake.TeamMode;
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

        // Квота списана авансом, а сообщение не дошло (дубль, переполнение очереди, занято
        // ожиданием человека) — возвращаем единицу, как ReportBlockerAsync на TooDeep:
        // иначе наивные ретраи агента выжигали бы MaxWakeups без единого реального хода.
        // Принятое в очередь (не дубль) не рефандим — доставка по концу хода ход поднимет.
        if (wakeSpent && result is SendAndWaitResult.Queued { Duplicate: true }
            or SendAndWaitResult.QueueFull or SendAndWaitResult.Busy)
            sessions.RefundTeamWakeup(sessionId);

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
    // Как и chats_send: делегированный ход не пишет в чужие чаты. Главный сценарий это не
    // задевает — ход чата-исполнителя запускает сервер (глубина 0), а не chats_send; гейт
    // закрывает лишь эскалацию с уже делегированного хода, поверх лимита цепочки.
    [DenyOnDelegatedTurn("Отчёт в вышестоящий чат")]
    public async Task<IActionResult> ReportUp(string sessionId, [FromBody] ReportUpRequest req)
    {
        var text = req.Text?.Trim();
        if (string.IsNullOrEmpty(text))
            return BadRequest(new { error = "Текст отчёта пуст" });

        // Финальный доклад по задаче постановщику уже доставил сервер (TaskExecutionService):
        // второе сообщение об одном факте гасим — именно оно и делало ленту дублирующей.
        // Ownership проверяем сами: ниже её делает SessionManager, а гейт стоит перед ним.
        if (OwnedSession(sessionId) is not null
            && IsCompletionAlreadyReported(tasks.GetBySession(sessionId), req.Blocker))
            return Ok(new
            {
                status = "already_reported",
                hint = "доклад о завершении этой задачи постановщик уже получил — повторять не нужно",
            });

        // Блокер будит постановщика ходом (Э4): в режиме «Командная реализация» доклад
        // «я застрял» иначе пролежал бы в ленте штаба до конца волны, а координатор ждал
        // бы докладов о завершении, которых не будет. Обычный промежуточный отчёт по-прежнему
        // бесплатный — ход не запускает.
        var result = req.Blocker
            ? await sessions.ReportBlockerAsync(sessionId, text, UserId)
            : await sessions.ReportUpAsync(sessionId, text, UserId, withTurn: false);
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

    // Гейт B5: сессия — чат-исполнитель задачи (TaskManager.GetBySession), по которой доклад
    // о завершении уже доставлен (CAS-флаг CompletionDelivered). null — сессия не привязана
    // к задаче, отчёт идёт обычным путём. Вынесен статикой, чтобы проверяться юнит-тестом
    // без поднятия приложения.
    //
    // Гейт закрывает ТОЛЬКО финальный доклад (спека B5). CompletionDelivered необратим, и на
    // блокеры он не распространяется: человек продолжает работу в том же чате-исполнителе,
    // персона упирается в блокер и зовёт chats_report_up(blocker: true) — молча съеденный
    // вызов оставил бы координатора без единственного сигнала «встал», а модель получила бы Ok
    // и считала, что доложила.
    internal static bool IsCompletionAlreadyReported(Models.TaskItem? task, bool blocker) =>
        !blocker && task is { CompletionDelivered: true };

    // GET /api/sessions/{sid}/pending — сообщения, ждущие конца текущего хода
    [HttpGet("pending")]
    public IActionResult GetPending(string sessionId)
    {
        if (OwnedSession(sessionId) is null) return NotFound();
        return Ok(sessions.GetPending(sessionId).Select(p => new
        {
            id = p.Id,
            text = p.Text,
            senderPersonaId = p.SenderPersonaId,
            senderOrigin = p.SenderOrigin,
            enqueuedAt = p.EnqueuedAt,
            senderChatName = p.SenderChatName,
            kind = p.Kind == ClaudeHomeServer.Services.SessionManager.PendingKind.User ? "user" : "agent",
            attachedPaths = p.AttachedPaths,
            mode = p.Kind == ClaudeHomeServer.Services.SessionManager.PendingKind.User ? p.Mode : null,
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

    // POST /api/sessions/{sid}/pending/preempt — прервать идущий ход и доставить ждущее
    // сообщение сейчас (кнопка на карточке очереди). Обычная отправка ход не прерывает —
    // это осознанный перебой. 409 — прерывать нечего (ход уже кончился либо очередь пуста).
    [HttpPost("pending/preempt")]
    public IActionResult PreemptForPending(string sessionId)
    {
        if (OwnedSession(sessionId) is null) return NotFound();
        return sessions.PreemptForPending(sessionId)
            ? NoContent()
            : Conflict(new { error = "Прерывать нечего: ход уже завершился или очередь пуста" });
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

// Разбор промпта хода. IncludeText=true — человек разрешил приложить фрагменты текста
// секций (там recall личных заметок и память персоны); по умолчанию уходят метаданные.
public record AnalyzePromptRequest(bool IncludeText = false);

// Отчёт в родительский чат: текст ложится карточкой, ход родителя не запускается.
// Blocker=true — доклад о блокере: постановщику запускается ход, а в режиме «Командная
// реализация» человек дополнительно получает карточку остановки с кнопками (Э4).
public record ReportUpRequest(string? Text, bool Blocker = false);
