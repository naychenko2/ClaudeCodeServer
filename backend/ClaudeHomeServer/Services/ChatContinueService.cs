using System.Collections.Concurrent;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services.Composition;
using ClaudeHomeServer.Services.Notes;
using ClaudeHomeServer.Services.Turn;

namespace ClaudeHomeServer.Services;

// Сессия не поддерживает «Продолжить в новом чате» (нечего передавать или состояние чата не
// переносится запиской) → 400 у контроллера. Занятость и прочие временные отказы — это
// InvalidOperationException → 409.
public sealed class ChatContinueNotSupportedException(string message) : Exception(message);

public sealed record ChatContinueResult(string NewSessionId, string? HandoffNoteId);

// «Продолжить в новом чате»: агент пишет записку-передачу в СВОЁМ чате (у него полный контекст),
// платформа сохраняет её заметкой и открывает новый чат той же персоны, первым сообщением которого
// и идёт эта записка. Контекст падает с сотен тысяч токенов до десятков без сжатия с потерями.
// Не ветвление: ветвление копирует историю и транскрипт (SessionManager.BranchAsync), здесь
// копируется только смысл.
public sealed class ChatContinueService(
    SessionManager sessions,
    ISessionBroadcaster broadcaster,
    ILogger<ChatContinueService> logger,
    ITurnEventBus? events = null,
    INoteAccessor? notes = null,
    // Подсистема Notes отключаемая: null, если выключена (тогда записка не сохраняется заметкой)
    INoteSemanticIndex? kb = null) : IHostedService
{
    // Промпт записки-передачи. Слать его надо служебным сообщением в старый чат.
    internal const string HandoffPrompt =
        "Мы переносим работу в новый чат, этот будет закрыт. Напиши записку-передачу для себя же в новом чате — "
        + "там не будет этой истории. Строго по разделам: **Цель** (одна-две фразы); **Сделано** (с коммитами, "
        + "задачами, файлами — id и пути); **Текущее состояние** (что работает, что нет, где остановились); "
        + "**Принятые решения и почему** (включая отвергнутые варианты — чтобы не приносить их обратно); "
        + "**Открытые вопросы и риски**; **Следующий шаг** (конкретно); **Ключевые ссылки** (файлы, задачи, "
        + "заметки, чаты, команды проверки). Не пересказывай ход разговора, только то, что нужно, чтобы "
        + "продолжить без потерь. Без вступления.";

    internal const string ConfirmRequest =
        "Подтверди в двух-трёх строках, как понял состояние и следующий шаг, и жди указаний.";

    // Ход передачи — полноценный ход агента с чтением файлов/задач при необходимости; запас большой
    internal static readonly TimeSpan HandoffTurnTimeout = TimeSpan.FromMinutes(15);

    // Сколько ждём освобождения чата перед отложенным продолжением (ход, из которого вызван инструмент,
    // кончается чуть позже своего TurnCompleted: статус ещё Working, очередь разбирается)
    internal static readonly TimeSpan DeferredIdleTimeout = TimeSpan.FromSeconds(60);

    // Чаты, по которым продолжение уже идёт: двойной клик не должен порождать два новых чата
    private readonly ConcurrentDictionary<string, byte> _inFlight = new();

    // Продолжения, поставленные инструментом chats_continue ИЗ хода самого чата: стартуют, когда этот
    // ход кончится (дождаться конца собственного хода изнутри него нельзя). sessionId → (владелец, приписка)
    private readonly ConcurrentDictionary<string, DeferredContinue> _afterTurn = new();

    private sealed record DeferredContinue(string OwnerId, string? Note);

    // Шов для тестов: настоящая отправка поднимает процесс CLI, а тестам нужен управляемый исход хода
    internal Func<string, string, TimeSpan, bool, Task<SendAndWaitResult>>? SendOverride { get; set; }

    private Task<SendAndWaitResult> SendAsync(string sessionId, string text, TimeSpan timeout, bool systemDirective) =>
        SendOverride is { } send
            ? send(sessionId, text, timeout, systemDirective)
            : sessions.SendMessageAndWaitAsync(sessionId, text, timeout, systemDirective: systemDirective);

    private int _subscribed;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        // Отписки у шины нет: повторный старт хоста не должен удваивать подписчиков
        if (events is not null && Interlocked.Exchange(ref _subscribed, 1) == 0)
            events.OnNotification<TurnCompleted>(OnTurnCompleted, "ChatContinueService.TurnCompleted");
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>
    /// Выполнить продолжение синхронно: ход передачи в старом чате → заметка → новый чат →
    /// первое сообщение в нём. Контракт отказов: KeyNotFoundException — 404, ChatContinueNotSupported
    /// — 400, InvalidOperationException (занят, агент не написал записку) — 409.
    /// </summary>
    public async Task<ChatContinueResult> ContinueAsync(string ownerId, string sessionId, string? userNote)
    {
        var source = sessions.GetOwned(sessionId, ownerId)
            ?? throw new KeyNotFoundException("Чат не найден");

        // Всё, что можно отвергнуть без платного хода, отвергаем ДО него
        if (source.Participants is { Count: > 0 })
            throw new ChatContinueNotSupportedException(
                "Групповой чат нельзя продолжить в новом: состав спикеров живёт в чате");
        if (source.TeamImplement is not null)
            throw new ChatContinueNotSupportedException(
                "Чат в режиме «Командная реализация» нельзя продолжить в новом: состояние волн живёт в чате");
        var history = await sessions.GetHistoryAsync(sessionId);
        if (!history.Any(m => m is StoredTextMessage or StoredResultMessage))
            throw new ChatContinueNotSupportedException("В чате ещё нет ходов агента: передавать нечего");

        if (sessions.HasTurnInFlight(sessionId))
            throw new InvalidOperationException(
                "В чате идёт ход — дождитесь его завершения или прервите его, затем попробуйте снова");
        if (!_inFlight.TryAdd(sessionId, 0))
            throw new InvalidOperationException("Продолжение этого чата уже выполняется");
        try
        {
            var handoff = await RequestHandoffAsync(sessionId);

            var noteId = SaveHandoffNote(ownerId, source, handoff);

            var created = await sessions.CreateContinuationAsync(sessionId, ownerId);

            // Первое сообщение нового чата: ход дешёвый (свежий контекст). Не ждём его конца —
            // человек видит новый чат сразу. Сбой отправки не откатывает чат: он создан и пуст,
            // человек может написать сам
            try
            {
                await SendAsync(created.Id, BuildFirstMessage(source, handoff, userNote), TimeSpan.Zero,
                    systemDirective: false);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Первое сообщение чата-продолжения {New} (от {Old}) не отправлено", created.Id, sessionId);
            }

            logger.LogInformation("Чат {Old} продолжен в {New} (заметка {Note})", sessionId, created.Id, noteId ?? "—");
            return new ChatContinueResult(created.Id, noteId);
        }
        finally
        {
            _inFlight.TryRemove(sessionId, out _);
        }
    }

    /// <summary>
    /// Поставить продолжение в очередь (инструмент chats_continue). afterCurrentTurn — вызов сделан
    /// из хода самого чата: ждать собственный ход изнутри нельзя, поэтому продолжение стартует
    /// по окончании этого хода (TurnCompleted). Иначе стартует сразу, но фоном: ход передачи
    /// длится минуты, а вызов MCP столько не ждёт. false — такое продолжение уже стоит в очереди.
    /// </summary>
    public bool Schedule(string ownerId, string sessionId, string? userNote, bool afterCurrentTurn)
    {
        if (afterCurrentTurn)
            return _afterTurn.TryAdd(sessionId, new DeferredContinue(ownerId, userNote));

        if (_inFlight.ContainsKey(sessionId)) return false;
        _ = Task.Run(() => RunBackgroundAsync(ownerId, sessionId, userNote, waitIdle: false));
        return true;
    }

    private Task OnTurnCompleted(TurnCompleted e)
    {
        if (!_afterTurn.ContainsKey(e.Turn.SessionId)) return Task.CompletedTask;
        // Прерванный/упавший ход — человек остановил работу; поставленное продолжение не выполняем
        if (!_afterTurn.TryRemove(e.Turn.SessionId, out var deferred)) return Task.CompletedTask;
        if (e.Outcome != "success")
        {
            logger.LogInformation("Отложенное продолжение чата {Session} снято: ход завершился как {Outcome}", e.Turn.SessionId, e.Outcome);
            return Task.CompletedTask;
        }
        _ = Task.Run(() => RunBackgroundAsync(deferred.OwnerId, e.Turn.SessionId, deferred.Note, waitIdle: true));
        return Task.CompletedTask;
    }

    private async Task RunBackgroundAsync(string ownerId, string sessionId, string? userNote, bool waitIdle)
    {
        try
        {
            if (waitIdle)
            {
                var deadline = DateTime.UtcNow + DeferredIdleTimeout;
                while (sessions.HasTurnInFlight(sessionId) && DateTime.UtcNow < deadline)
                    await Task.Delay(250);
            }
            await ContinueAsync(ownerId, sessionId, userNote);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Фоновое продолжение чата {Session} не выполнено", sessionId);
        }
    }

    // Ход передачи в старом чате: служебное сообщение с промптом, ждём конца хода, берём ответ агента
    private async Task<string> RequestHandoffAsync(string sessionId)
    {
        var sent = await SendAsync(sessionId, HandoffPrompt, HandoffTurnTimeout, systemDirective: true);
        switch (sent)
        {
            case SendAndWaitResult.Completed c when !string.IsNullOrWhiteSpace(c.Result.Reply):
                return c.Result.Reply.Trim();
            case SendAndWaitResult.Completed:
                throw new InvalidOperationException("Агент вернул пустую записку-передачу — чат не продолжен");
            case SendAndWaitResult.Running:
                throw new InvalidOperationException(
                    "Агент не успел написать записку за отведённое время — чат не продолжен");
            default:
                // Busy/Queued/QueueFull: чат занялся между проверкой и отправкой
                throw new InvalidOperationException(
                    "В чате идёт ход — дождитесь его завершения или прервите его, затем попробуйте снова");
        }
    }

    // Записка заметкой в папке «Передачи» проекта (чат вне проекта — личный vault). Подсистема
    // заметок выключена или сбой записи — продолжаем без заметки: записка и так уедет первым
    // сообщением нового чата, заметка — лишь удобный архив
    private string? SaveHandoffNote(string ownerId, Session source, string handoff)
    {
        if (notes is null) return null;
        try
        {
            var name = string.IsNullOrWhiteSpace(source.Name) ? "Чат" : source.Name.Trim();
            var note = notes.Create(ownerId, new CreateNoteRequest(
                Title: $"{name} — передача {DateTime.Now:yyyy-MM-dd}",
                Content: $"{handoff}\n\n---\nПередача из чата: {ChatLink(source)}",
                Source: source.ProjectId,   // null → личный vault (чат вне проекта)
                Folder: "Передачи",
                SourceSessionId: source.Id));
            kb?.QueueSync(ownerId);
            _ = broadcaster.ToOwner(ownerId, new NotesChangedMessage("created", note.Id));
            return note.Id;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Записка-передача чата {Session} не сохранена заметкой", source.Id);
            return null;
        }
    }

    // Ссылка на чат, которую понимает фронт (диплинк #/chats/{id})
    internal static string ChatLink(Session chat)
    {
        var title = string.IsNullOrWhiteSpace(chat.Name) ? "Чат" : chat.Name.Trim().Replace("[", "(").Replace("]", ")");
        return $"[{title}](#/chats/{Uri.EscapeDataString(chat.Id)})";
    }

    // Записка + «Предыдущий чат» + приписка человека + просьба подтвердить понимание
    internal static string BuildFirstMessage(Session source, string handoff, string? userNote)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine(handoff);
        sb.AppendLine();
        sb.AppendLine($"Предыдущий чат: {ChatLink(source)}");
        if (!string.IsNullOrWhiteSpace(userNote))
        {
            sb.AppendLine();
            sb.AppendLine($"От человека: {userNote.Trim()}");
        }
        sb.AppendLine();
        sb.Append(ConfirmRequest);
        return sb.ToString();
    }
}
