using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services.Llm;

namespace ClaudeHomeServer.Services.Sessions;

// Локальный голосовой ход (S3-6, docs/research/session-manager-split-2026-11.md §1 «ход»):
// прямой вызов Ollama мимо claude CLI и его же синтез в ленту — тела BuildVoiceContextBlock,
// ShouldRunLocalVoice, BuildVoiceMessages, TrimVoiceMessage, RunLocalVoiceTurnAsync перенесены
// из SessionManager дословно, с комментариями. Ответ идёт синтетическими ServerMessage через
// OnMessageQueuedAsync конца хода (TurnCompletion) — тот же конвейер, что у CLI (аккумулятор,
// статусы Working→Active→Finished, разбор очереди, бродкаст). Фолбэка на CLI нет: тихий
// 15-секундный старт подпроцесса в разговоре хуже видимой ошибки в ленте.
// Зависимостей пять (план §2): router, ollama, store, completion и personas — последняя
// заменена в сравнении с планом: BuildVoiceMessages подмешивает характер персоны через
// PersonaManager.GetByIdInternal (в плане §2 вместо неё была history — тело конца хода
// снимки пишет через TurnCompletion, своей зависимости к ChatHistoryService здесь нет).
// Класс internal и полем конструктора SessionManager не становится: его единственный
// потребитель — фасад (accept хода решает ShouldRunLocalVoice и поднимает RunLocalVoiceTurnAsync),
// и он собирается в запасной сборке конструктора фасада (тот же приём, что с internal-фабрикой
// контекста в S3-5). Ограничение локального голоса (_router/_ollama могут не быть) — прежнее:
// без них ShouldRunLocalVoice даёт false, разговор идёт через CLI.
internal sealed class LocalVoiceTurn
{
    private readonly LocalActionRouter? _router;
    private readonly ILocalLlmClient? _ollama;
    private readonly PersonaManager _personas;
    private readonly SessionStore _store;
    private readonly TurnCompletion _completion;

    internal LocalVoiceTurn(LocalActionRouter? router, ILocalLlmClient? ollama,
        PersonaManager personas, SessionStore store, TurnCompletion completion)
    {
        _router = router;
        _ollama = ollama;
        _personas = personas;
        _store = store;
        _completion = completion;
    }

    // Модель места chat-voice по явному выбору админа (слот → UserModelTierResolver.ModelFor,
    // конкретная модель → как есть; null — ход идёт на модели чата). Ограничитель решения —
    // приём хода, резолвер — здесь: LocalActionRouter входит в зависимости этой роли (S3-7).
    internal string? VoiceModelFor(string? ownerId) =>
        _router?.VoiceModelFor(LocalActionCatalog.ChatVoice, ownerId);

    // Сводка локального разговора для CLI-хода: «Пользователь: … / Ассистент: …» из
    // хвоста истории. null/пусто — реплик не нашлось (тогда и блок не нужен).
    internal static string? BuildVoiceContextBlock(TurnAccumulator acc)
    {
        var lines = acc.GetAll()
            .Where(m => m is StoredUserMessage { SystemDirective: not true, Text: { Length: > 0 } }
                        or StoredTextMessage { Text: { Length: > 0 } })
            .TakeLast(VoiceHistoryMessages)
            .Select(m => m switch
            {
                StoredUserMessage u => $"Пользователь: {TrimVoiceMessage(u.Text!)}",
                StoredTextMessage t => $"Ассистент: {TrimVoiceMessage(t.Text!)}",
                _ => null,
            })
            .Where(l => l is not null)
            .ToList();
        if (lines.Count == 0) return null;
        return "[Контекст: до этого пользователь разговаривал голосом с локальной моделью " +
               "(краткие реплики, без инструментов). Последние реплики разговора:\n" +
               string.Join("\n", lines) + "\n]";
    }

    // Гейт локального голосового хода: разговор (Session.VoiceMode) на локальной модели
    // (место chat-voice маршрутизировано на «Локальная»). Только ходы человека без
    // вложений и без протоколов CLI (цикл «до готово»/штаб свои маркеры локаль не
    // воспроизведёт); авто-ходы и доклады автоматизаций — через CLI как раньше.
    // Персона не блокирует: её характер подмешивается в system-промпт разговора.
    internal bool ShouldRunLocalVoice(SessionEntry entry, bool auto, bool systemDirective,
        IReadOnlyList<string> attachedPaths) =>
        entry.Info.VoiceMode
        // Только стиль talk: digest — это полный агентный ответ с маркером <voice> в конце,
        // а локальная болталка (LocalCompanionSection) не умеет ни инструменты, ни маркер.
        // Забыть этот гейт — значит молча озвучить фолбэком всю реплику Ollama целиком.
        && !entry.Info.IsVoiceDigest
        && _router is not null && _ollama is not null
        && _router.UsesLocal(LocalActionCatalog.ChatVoice)
        && !auto && !systemDirective
        && entry.Info.WorkLoop is null
        && entry.Info.TeamImplement is null
        && attachedPaths.Count == 0;

    // messages[] для разговорного вызова Ollama: system-промпт собеседника (+ характер
    // персоны при чате с персоной — дёшево, без полного слоя персоны: инструменты, память
    // и привязки локальному ходу всё равно недоступны) + хвост истории. Текущая реплика
    // уже лежит в аккумуляторе (OnUserMessage общего хвоста SendDirectAsync) — отдельно
    // не добавляется, иначе продублировалась бы. GetAll() сам берёт лок аккумулятора.
    private List<ChatMsg> BuildVoiceMessages(SessionEntry entry, TurnAccumulator acc)
    {
        var system = Prompts.VoicePrompts.LocalCompanionSection;
        if (entry.Info.PersonaId is { } pid
            && _personas.GetByIdInternal(pid)?.Contract is { } contract)
        {
            var character = new List<string>(2);
            if (!string.IsNullOrWhiteSpace(contract.Character)) character.Add(contract.Character);
            if (!string.IsNullOrWhiteSpace(contract.Tone)) character.Add($"Тон: {contract.Tone}.");
            if (character.Count > 0)
                system += "\n\nТы говоришь от лица персонажа: " + string.Join(" ", character) +
                          "\nЭто сокращённый характер — полный образ и память недоступны в этом режиме.";
        }

        var history = acc.GetAll()
            .Where(m => m is StoredUserMessage { SystemDirective: not true, Text: { Length: > 0 } }
                        or StoredTextMessage { Text: { Length: > 0 } })
            .TakeLast(VoiceHistoryMessages)
            .Select(m => m switch
            {
                StoredUserMessage u => new ChatMsg("user", TrimVoiceMessage(u.Text!)),
                StoredTextMessage t => new ChatMsg("assistant", TrimVoiceMessage(t.Text!)),
                _ => null,
            })
            .Where(m => m is not null)
            .Cast<ChatMsg>()
            .ToList();

        var result = new List<ChatMsg>(history.Count + 1) { new("system", system) };
        result.AddRange(history);
        return result;
    }

    // Лимиты разговорного контекста: хвост истории и усечение одной реплики. Разговор
    // короткий, а num_ctx профиля Text — 8192 токенов: без усечения длинный старый ход
    // молча вытеснил бы свежие реплики.
    internal const int VoiceHistoryMessages = 16;
    internal static string TrimVoiceMessage(string text) =>
        text.Length <= 1500 ? text : text[..1500] + "…";

    // Локальный голосовой ход: прямой вызов Ollama мимо claude CLI. Ответ приходит
    // синтетическими ServerMessage через OnMessageQueuedAsync — тот же конвейер, что у CLI
    // (аккумулятор, статусы Working→Active→Finished, разбор очереди, бродкаст).
    // Фолбэка на CLI нет: тихий 15-секундный старт подпроцесса в разговоре хуже
    // видимой ошибки в ленте.
    internal async Task RunLocalVoiceTurnAsync(string sessionId, SessionEntry entry)
    {
        var runId = TurnProcessHost.NextRunId(); // сквозной счётчик прогонов живёт в хосте (S3-5)
        entry.RunId = runId;
        var acc = entry.Accumulator!;
        // Ключ истории: транскрипт CLI, если чат уже начат (лента едина), иначе id чата.
        // Info.ClaudeSessionId ветка НЕ ставит — его ставит CLI штатно при первом CLI-ходе.
        acc.SetSaveKey(entry.Info.ClaudeSessionId ?? sessionId);

        // Provider не заполняем (дефолт claude): фронт модель из каталога не знает и
        // деградирует в Claude-облик — бейдж не ломается, фронт не трогаем.
        await _completion.OnMessageQueuedAsync(sessionId, acc, new SessionStartedMessage(
            entry.Info.ClaudeSessionId ?? sessionId, IsResume: false,
            Model: _router!.LocalModel, Mode: entry.Info.Mode.ToString().ToLowerInvariant()), runId);

        // Профиль читаем по ключу места — раньше жёстко `CheapProfile.Text` и таймаут
        // по ключу места; пока значения совпадали — `chat-voice` объявлен `CheapProfile.Text`.
        // Одна строка правды устраняет рассинхрон при переводе места на другой профиль.
        var spec = _router.ProfileFor(LocalActionCatalog.ChatVoice);
        var messages = BuildVoiceMessages(entry, acc);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        using var cts = new CancellationTokenSource();
        entry.LocalVoiceCts = cts;
        ChatTurnResult? turn = null;
        var cancelled = false;
        // Сколько текста уже ушло в ленту потоком: по нему решается, слать ли ответ
        // целиком в конце (страховка на случай, если поток не дал ни куска)
        var streamed = 0;
        try
        {
            turn = await _ollama!.ChatTurnAsync(messages, _router.LocalModel,
                TimeSpan.FromMilliseconds(_router.TimeoutMsFor(LocalActionCatalog.ChatVoice)),
                spec.NumPredict, spec.NumCtx, _store.ResolveOwnerId(entry.Info),
                // Поток: куски ответа уходят в ленту (и в озвучку разговора) по мере
                // генерации — иначе первый звук ждал бы конца всего ответа
                onDelta: async chunk =>
                {
                    streamed += chunk.Length;
                    await _completion.OnMessageQueuedAsync(sessionId, acc, new TextDeltaMessage(chunk), runId);
                },
                ct: cts.Token);
        }
        catch (OperationCanceledException)
        {
            // «Стоп» пользователя: сообщения не шлём, exited ниже закроет ход (Working→Active)
            cancelled = true;
        }
        catch (Exception ex)
        {
            await _completion.OnMessageQueuedAsync(sessionId, acc, new ErrorMessage($"Локальная модель: {ex.Message}"), runId);
        }
        finally
        {
            entry.LocalVoiceCts = null;
            entry.LocalTurnsSinceCli++;
        }

        if (turn?.Text is { Length: > 0 } answer)
        {
            // Обычный путь: текст уже ушёл кусками из onDelta. Целиком шлём только если
            // поток не дал ничего (не-потоковый ответ, сбой до первого куска)
            if (streamed == 0)
                await _completion.OnMessageQueuedAsync(sessionId, acc, new TextDeltaMessage(answer), runId);
            await _completion.OnMessageQueuedAsync(sessionId, acc, new ResultMessage(
                Subtype: "success", DurationMs: sw.ElapsedMilliseconds, NumTurns: 1,
                Usage: turn.Usage, TotalCostUsd: 0), runId);
        }
        else if (!cancelled)
        {
            // Пустой ответ/сбой HTTP — честная ошибка без фолбэка на CLI
            // (ErrorMessage исключения уже ушёл выше — второй не дублируем).
            // Занятость общего счётчика — не отказ движка: человеку так и говорим
            if (turn is not null)
                await _completion.OnMessageQueuedAsync(sessionId, acc, new ErrorMessage(turn.Busy
                    ? "Локальный движок занят — ход не запущен, повторите чуть позже"
                    : "Локальная модель не ответила"), runId);
        }

        await _completion.OnMessageQueuedAsync(sessionId, acc, new ExitedMessage(), runId);
    }
}
