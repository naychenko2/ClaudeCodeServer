using System.Collections.Concurrent;
using System.Text;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services.Llm;

namespace ClaudeHomeServer.Services;

// Записка этого чата уже собирается (повторный клик) → 409 у контроллера
public sealed class HandoffInProgressException() : Exception("Перенос этого чата уже идёт");

// Тёплый ход не мог начаться: чат занят другим ходом (Busy/Queued/QueueFull) → 409 у
// контроллера. Это не отказ модели (502): перенос не удался, потому что чат занят.
// Текст различает «ничего не поставлено» (Busy/QueueFull) и «просьба о записке уже ушла в
// старый чат» (Queued): во втором случае человеку честно говорим, что она там выполнится
public sealed class HandoffBusyException(string message = "чат занят другим ходом, попробуйте позже")
    : Exception(message);

// Модель не собрала записку (упала/таймаут/пустой ответ) → 502 у контроллера
public sealed class HandoffGenerationException(string message) : Exception(message);

// Итог переноса: новый чат + черновик композера (передаточная записка, не автоотправка).
// Warm — записку написал сам чат (тёплый путь), а не дешёвый раннер
public sealed record ChatHandoffResult(Session Session, string Draft, bool Warm = false);

// Перенос в новый чат (фича «Индикатор prompt cache и перенос в новый чат», шаг B-4). Новый
// чат знает, о чём был разговор, и НЕ платит холодную
// перезапись всей истории в кэш промпта: он стартует с короткой передаточной записки, а не
// с транскрипта целиком. Поэтому преемник создаётся без resume (CreateHandoffChatAsync) —
// ветвление и --fork-session тащат историю целиком, ровно этой перезаписи и избегает.
//
// Путь выбирает сервер по статусу кэша последнего хода (PromptCacheState из result'а —
// контракт B-1): пока якорь с TTL не истёк, префикс лежит в кэше — тёплый путь просит
// записку у самого чата (его ход читает префикс из кэша, а не пишет заново); иначе —
// холодный путь: записку по транскрипту собирает дешёвый раннер, место chat-handoff.
// Предусловия тёплого хода (SessionManager.WarmHandoffBlocker) не выполнены — тихо идём
// холодным путём, без ошибки.
//
// Старый чат холодного пути только читается: ни UpdatedAt, ни архив, ни сводки не трогаем.
// Тёплый путь добавляет в него видимый ход передачи — это осознанно (ответ виден в ленте).
//
// Образец — ChatDigestService и ChatContinueService (тот же класс сервиса в спине):
// _inFlight против двойного клика, очередь «1 поток на владельца».
public class ChatHandoffService(
    SessionManager sessions, ICheapTextRunner cheap, ILogger<ChatHandoffService> logger)
{
    // Вдвое больше, чем у сводки: записке нужны решения и их причины, а не две строки итога.
    // При переполнении — голова (цель) + хвост (где остановились), см. SessionTranscript
    public const int TranscriptBudget = 60_000;

    public const string NothingToHandOff = "В чате ещё нет сообщений — переносить нечего";

    // Окно локали (16k) и её 1024 токена вывода записку по транскрипту не вывозят, а
    // обрезанная передача хуже честного отказа — модель не зовём вовсе
    public const string LocalRouteRefusal =
        "Перенос в новый чат назначен на локальную модель, а её окна не хватает на передачу разговора. "
        + "Назначьте месту «Перенос в новый чат» облачного исполнителя в «Поставщиках моделей».";

    // Явный потолок тёплого хода передачи. По таймауту ход НЕ отменяется (он видимый и
    // доработает сам), но новый чат не создаётся — записки на руках нет
    public static readonly TimeSpan WarmTurnTimeout = TimeSpan.FromMinutes(5);

    // Явный потолок холодного пути: фронт ждёт 6 минут, без потолка медленный раннер
    // обрывается на стороне клиента — человек видит оборванный запрос, а не честный 502.
    // 4 минуты — запас: отказ приходит раньше, чем клиент сдаётся. Свойство экземпляра,
    // а не константа: тесты укорачивают потолок (настоящие 4 минуты в тест не влезают).
    internal TimeSpan ColdPathTimeout { get; set; } = TimeSpan.FromMinutes(4);

    private readonly ConcurrentDictionary<string, byte> _inFlight = new();
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _ownerGates = new();

    // Шов для тестов: настоящая подача поднимает процесс CLI, а тестам нужен управляемый
    // исход хода (тот же приём, что SendOverride у ChatContinueService)
    internal Func<string, string, TimeSpan, Task<SendAndWaitResult>>? SendOverride { get; set; }

    /// <summary>
    /// Выполнить перенос: записка (тёплый или холодный путь) → чат-преемник без resume →
    /// черновик композера. Контракт отказов: KeyNotFoundException — 404 (чужой/несуществующий
    /// id), InvalidOperationException — 409 (группа/штаб/worktree, пустой чат, локаль места,
    /// повторный клик), HandoffBusyException — 409 (тёплый ход: чат занят другим ходом;
    /// если просьба о записке уже поставлена в старый чат — текст об этом говорит),
    /// HandoffGenerationException — 502 (модель не дала записку).
    /// </summary>
    public async Task<ChatHandoffResult> HandoffAsync(string userId, string sessionId, CancellationToken ct)
    {
        // Владение — GetOwned (проектные сессии тоже): чужой id — KeyNotFound, не 500
        var source = sessions.GetOwned(sessionId, userId)
            ?? throw new KeyNotFoundException("Чат не найден");

        // Всё, что можно отвергнуть без платного хода, отвергаем ДО него
        sessions.EnsureHandoffAllowed(source);

        if (!_inFlight.TryAdd(sessionId, 0))
            throw new HandoffInProgressException();

        try
        {
            var history = await sessions.GetHistoryAsync(sessionId);

            // Тёплый путь: кэш последнего хода ещё жив (якорь + TTL не истёк) и чат может
            // дать ход. Любое нарушение — тихий холодный путь, без ошибки. Причину тихого
            // отказа логируем: без неё в проде не видно, сколько переносов идёт тёплыми
            if (IsCacheWarm(history, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()))
            {
                var blocker = sessions.WarmHandoffBlocker(sessionId);
                if (blocker is null) return await WarmHandoffAsync(source, userId);
                logger.LogInformation(
                    "Тёплый путь для чата {SessionId} невозможен: {Blocker} — перенос пойдёт холодным",
                    sessionId, blocker);
            }

            if (cheap.UsesLocal(LocalActionCatalog.ChatHandoff))
                throw new InvalidOperationException(LocalRouteRefusal);

            // Гейт «1 поток на владельца» — только для холодного пути: записку пишет общий
            // дешёвый раннер, и несколько переносов одного владельца одновременно платили бы
            // ему лишние вызовы. Тёплый путь мимо этого гейта осознанно: там записку пишет
            // сам чат своим ходом (его очередь), а не конкурентный раннер (минор ревью B-4).
            var gate = _ownerGates.GetOrAdd(userId, _ => new SemaphoreSlim(1, 1));
            // Потолок холодного пути считается целиком: очередь в гейте и вызов раннера.
            // Не уложились — честный 502, а не оборванный на стороне клиента запрос
            using var coldDeadline = new CancellationTokenSource(ColdPathTimeout);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, coldDeadline.Token);
            var runCt = linked.Token;
            try
            {
                await gate.WaitAsync(runCt);
                try
                {
                    // Пока стояли в очереди, чат могли удалить
                    source = sessions.GetOwned(sessionId, userId)
                        ?? throw new KeyNotFoundException("Чат не найден");

                    var transcript = SessionTranscript.Build(history, TranscriptBudget);
                    if (string.IsNullOrWhiteSpace(transcript))
                        throw new InvalidOperationException(NothingToHandOff);

                    string note;
                    try
                    {
                        note = await cheap.RunAsync(LocalActionCatalog.ChatHandoff,
                            BuildPrompt(source.Name, transcript), ownerId: userId, ct: runCt);
                    }
                    // Отмену (клиент или наш потолок) не выдаём за отказ модели: раннер заворачивает её
                    // в InvalidOperationException (LlmTimeoutException), поэтому смотрим на токены
                    catch (InvalidOperationException ex)
                        when (!ct.IsCancellationRequested && !coldDeadline.IsCancellationRequested)
                    {
                        throw new HandoffGenerationException(ex.Message);
                    }
                    if (string.IsNullOrWhiteSpace(note))
                        throw new HandoffGenerationException("Модель вернула пустую передаточную записку");

                    var successor = await sessions.CreateHandoffChatAsync(source, userId,
                        SessionManager.ContinuationName(source.Name));
                    var draft = BuildDraft(source, note.Trim());
                    logger.LogInformation("Чат {SessionId} перенесён в {NewId} (записка {Length} символов)",
                        sessionId, successor.Id, draft.Length);
                    return new ChatHandoffResult(successor, draft);
                }
                finally
                {
                    // Гейт из словаря не вычищаем: Release → проверка → TryRemove неатомарны, и
                    // снятый «пустой» семафор пока в него входит другой перенос того же владельца
                    // порождал второй — «1 поток на владельца» переставал быть правдой. Запись на
                    // владельца — копейки
                    gate.Release();
                }
            }
            catch (Exception ex) when (ex is not HandoffGenerationException
                && (ct.IsCancellationRequested || coldDeadline.IsCancellationRequested))
            {
                // Клиент ушёл — просто прекращаем, преемника нет, отказа модели не изображаем
                ct.ThrowIfCancellationRequested();
                // Отказ по нашему потолку: текст говорит про время, чтобы человек понял,
                // что перенос можно повторить
                throw new HandoffGenerationException("перенос не успел завершиться, попробуйте ещё раз");
            }
        }
        finally
        {
            _inFlight.TryRemove(sessionId, out _);
        }
    }

    // Статус кэша по последнему result'у С ДАННЫМИ (данные B-1, тот же приём, что в F-1a на
    // фронте): result'ы без якоря (сторонний провайдер, старая история) пропускаются и
    // не гасят живой якорь более раннего хода — кэш привязан к разговору, не к провайдеру.
    // Якорь — время последнего касания кэша, TTL «1h»/«5m». Пока якорь + срок не истёк,
    // префикс лежит в кэше — ход самого чата прочитает его, а не перепишет. Порог «остывает»
    // здесь не нужен: даже остывающий кэш ещё читается. Нет ни одного result с данными —
    // холодный путь.
    internal static bool IsCacheWarm(IReadOnlyList<StoredMessage> history, long nowMs)
    {
        for (var i = history.Count - 1; i >= 0; i--)
        {
            if (history[i] is not StoredResultMessage { PromptCache: { } pc }) continue;
            // Без якоря это не сигнал состояния кэша — искать дальше, а не гасить им
            if (pc.AnchorAtMs is not long anchor) continue;
            var ttlMs = pc.Ttl switch { "1h" => 3_600_000L, "5m" => 300_000L, _ => -1L };
            // Якорь без валидного TTL — тоже не сигнал (как у фронта, promptCacheState):
            // искать дальше, а не гасить им более ранний живой якорь
            if (ttlMs <= 0) continue;
            return anchor + ttlMs > nowMs;
        }
        return false;
    }

    // Тёплый путь: видимый ход в самом чате (глубина 0, не «ввод человека»: мимо
    // PreemptTurnForQueue и штаба, режим чата не трогается). preempt: false — если чат
    // успели занять, не рубим чужой ход: запрос встанет в очередь, и мы честно отказываем
    // «чат занят». Просьба «только текстом» — в самом промпте: никаких запретов
    // инструментов и смены режима — состав tools/list не зависит от хода, иначе CLI
    // перезапустится со всеми MCP, и кэш, ради которого путь затевался, пропадёт.
    private async Task<ChatHandoffResult> WarmHandoffAsync(Session source, string ownerId)
    {
        var sent = await (SendOverride is { } send
            ? send(source.Id, BuildWarmPrompt(), WarmTurnTimeout)
            : sessions.SendMessageAndWaitAsync(source.Id, BuildWarmPrompt(), WarmTurnTimeout,
                preempt: false));

        var note = sent switch
        {
            SendAndWaitResult.Completed c when c.Result.Succeeded && !string.IsNullOrWhiteSpace(c.Result.Reply)
                => c.Result.Reply,
            SendAndWaitResult.Completed c when !c.Result.Succeeded
                => throw new HandoffGenerationException(string.IsNullOrWhiteSpace(c.Result.Reply)
                    ? "Ход передачи завершился ошибкой — новый чат не создан"
                    : $"Ход передачи завершился ошибкой: {c.Result.Reply}"),
            SendAndWaitResult.Completed =>
                throw new HandoffGenerationException("Чат вернул пустую передаточную записку"),
            SendAndWaitResult.Running =>
                throw new HandoffGenerationException(
                    $"Чат не написал передачу за {WarmTurnTimeout.TotalMinutes:0} мин — новый чат не создан, ход дорабатывает в старом"),
            // Просьба о записке УЖЕ в старом чате (в очереди или ушла ходом сразу): отменить
            // постановку нельзя, преемник не создаётся — честно говорим об этом в 409
            SendAndWaitResult.Queued { Dispatched: true } => throw new HandoffBusyException(
                "чат освободился в момент переноса: просьба о передаче ушла в старый чат "
                + "(ответ появится в его ленте), новый чат не создан — повторите перенос"),
            SendAndWaitResult.Queued => throw new HandoffBusyException(
                "чат занят другим ходом: просьба о передаче поставлена в очередь старого чата "
                + "и выполнится там, новый чат не создан — повторите перенос позже"),
            // Busy/QueueFull: ничего не поставлено — временное состояние, 409, клик можно повторить
            _ => throw new HandoffBusyException(),
        };

        var successor = await sessions.CreateHandoffChatAsync(source, ownerId,
            SessionManager.ContinuationName(source.Name));
        var draft = BuildDraft(source, note.Trim());
        logger.LogInformation("Чат {SessionId} перенесён тёплым путём в {NewId} (записка {Length} символов)",
            source.Id, successor.Id, draft.Length);
        return new ChatHandoffResult(successor, draft, Warm: true);
    }

    // Единственный промпт передачи — холодный путь (по транскрипту). Тёплый (BuildWarmPrompt)
    // берёт ту же спецификацию записки (AppendNoteSpec), меняется лишь вступление.
    // Ссылку на старый чат пишет код (BuildDraft), а не модель — id она бы исказила.
    public static string BuildPrompt(string? sessionName, string transcript)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Ниже — транскрипт чата с Claude Code. Разговор переносится в новый чат, который");
        sb.AppendLine("не увидит этой истории. Составь передаточную записку, по которой новый собеседник");
        sb.AppendLine("продолжит работу без потери контекста.");
        sb.AppendLine();
        AppendNoteSpec(sb, "транскрипте");
        if (!string.IsNullOrWhiteSpace(sessionName))
            sb.AppendLine($"Название чата: {sessionName}");
        sb.AppendLine();
        sb.AppendLine("Транскрипт:");
        sb.AppendLine(transcript);
        return sb.ToString();
    }

    // Тёплый путь: просьба к самому чату. Только текстом — никаких запретов инструментов
    // и смены режима на ход (см. комментарий WarmHandoffAsync)
    public static string BuildWarmPrompt()
    {
        var sb = new StringBuilder();
        sb.AppendLine("Этот разговор переносится в новый чат, который не увидит нашей истории. Составь");
        sb.AppendLine("передаточную записку, по которой новый собеседник продолжит работу без потери контекста.");
        sb.AppendLine("Не вызывай инструменты и ничего не делай в проекте — ответь только текстом записки.");
        sb.AppendLine();
        AppendNoteSpec(sb, "нашем разговоре");
        return sb.ToString().TrimEnd();
    }

    // Спецификация записки — общая для обоих путей, чтобы записки не разошлись по составу.
    // where — где искать факты («транскрипте» / «нашем разговоре», предложный падеж)
    private static void AppendNoteSpec(StringBuilder sb, string where)
    {
        sb.AppendLine("Разделы — markdown-заголовками третьего уровня, строго в этом порядке:");
        sb.AppendLine("### Цель");
        sb.AppendLine("### Решения и их причины");
        sb.AppendLine("### От чего отказались");
        sb.AppendLine("### Изменённые файлы");
        sb.AppendLine("### Где остановились");
        sb.AppendLine("### Следующий шаг");
        sb.AppendLine("### Открытые вопросы");
        sb.AppendLine();
        sb.AppendLine($"Правила: по-русски, кратко, списками. Не выдумывай того, чего нет в {where}: "
                     + "нечего сказать по разделу — напиши «нет». Пути файлов и идентификаторы — как в "
                     + $"{where}. Без вступлений и заключений — только записка.");
    }

    // Черновик нового чата: записка модели + ссылка на старый чат последней строкой. У
    // временного чата — предупреждение: ссылка умрёт вместе с ним (новый чат не временный).
    internal static string BuildDraft(Session source, string note)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Продолжаем разговор из чата «{(string.IsNullOrWhiteSpace(source.Name) ? "Чат" : source.Name)}». Передаточная записка:");
        sb.AppendLine();
        sb.AppendLine(note);
        sb.AppendLine();
        if (source.ExpiresAfterMinutes is not null)
            sb.AppendLine("Исходный чат временный: после его удаления ссылка ниже перестанет работать.");
        sb.Append($"Исходный чат: {source.Id} — детали через chats_history.");
        return sb.ToString();
    }
}
