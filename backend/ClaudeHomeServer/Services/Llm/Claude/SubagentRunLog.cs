namespace ClaudeHomeServer.Services.Llm.Claude;

/// <summary>
/// Паспорт одного прогона сабагента (Task/Agent): чем кончился и какой ценой.
/// Считается из его транскрипта <c>agent-*.jsonl</c> по ходу чтения (SubagentStreamWatcher).
/// </summary>
/// <param name="AgentId">id из имени файла транскрипта (он же поле agentId внутри строк).</param>
/// <param name="AgentType">тип агента из agent-*.meta.json (какой .md-агент запущен).</param>
/// <param name="Description">описание вызова из меты — человекочитаемая подпись прогона.</param>
/// <param name="SessionId">чат, в ходе которого агент работал.</param>
/// <param name="ToolUseId">tool_use_id родительского вызова Task — связь с карточкой в ленте.</param>
/// <param name="ToolUses">сколько раз агент вызвал инструменты.</param>
/// <param name="AssistantMessages">сообщений модели в транскрипте (ходов агента).</param>
/// <param name="Prompts">сколько текстов ему прислали: постановка + добивания.</param>
/// <param name="ContextTokens">окно последнего запроса (input + cache_read + cache_creation).</param>
/// <param name="OutputTokens">суммарный выход по всем сообщениям.</param>
/// <param name="LastStopReason">stop_reason последнего сообщения модели.</param>
/// <param name="Truncated">
/// ОБРЫВ: последнее сообщение — tool_use (инструмент вызван, результат получен, продолжения
/// от модели нет), отчёта не было.
/// </param>
/// <param name="LastTool">имя последнего вызванного инструмента — на чём именно замолчал.</param>
/// <param name="NudgeAttempts">сколько раз продукт добивал агента автоматически (потолок 2).</param>
/// <param name="Partial">
/// ватчер подключился к уже начатому транскрипту (файл существовал на старте хода) —
/// счётчики неполны, паспорт годен только для факта завершения.
/// </param>
/// <param name="FinishedBy">чем закрыт прогон: bg_done | bg_aborted | tool_result | run_end.</param>
public sealed record SubagentRunPassport(
    string AgentId,
    string? AgentType,
    string? Description,
    string? SessionId,
    string? ToolUseId,
    DateTime StartedAt,
    DateTime LastActivityAt,
    int DurationSeconds,
    int ToolUses,
    int AssistantMessages,
    int Prompts,
    long ContextTokens,
    long OutputTokens,
    string? LastStopReason,
    bool Truncated,
    string? LastTool,
    string? Model,
    long TranscriptBytes,
    int NudgeAttempts,
    bool Partial,
    string FinishedBy,
    DateTime RecordedAt);

/// <summary>
/// Паспорта прогонов сабагентов: последние N штук в памяти, отдаются через GET /api/subagents/runs.
///
/// ЗАЧЕМ. Сабагенты систематически «отваливаются» посреди работы, а продукт принимает обрывок
/// за готовый результат. Разбор двух прогонов вручную (транскрипты agent-*.jsonl) дал факты,
/// которые здесь и требуется набирать автоматически, вместо гадания:
///  1. stop_reason за прогон: tool_use 70 и 130 раз, end_turn 1 и 2 раза. max_tokens — ни разу.
///  2. end_turn встречается ТОЛЬКО у финальных отчётов, пришедших после ручного добивания.
///     В момент обрыва последнее сообщение агента — tool_use: инструмент вызван, результат
///     получен, продолжения от модели нет.
///  3. Момент возврата результата родителю совпадает с последней активностью агента
///     (старт + длительность = таймстемп последней записи): дальше агент молчит, пока не толкнут.
///  4. Ошибок нет ни одной: ни overloaded, ни rate_limit, ни таймаутов, ни API Error.
///  5. Наш ватчдог не при чём: ClaudeSession.IdleTimeout = 60 мин, агенты жили 9 и 17 мин.
///  6. CLAUDE_CODE_PRINT_BG_WAIT_CEILING_MS=0 не при чём: CLI документирует «=0 to wait
///     indefinitely», наш код верен.
///  7. Порог остановки НЕ определён: не время (9 и 17 мин), не число вызовов инструментов
///     (59 и 51), не токены (133k и 162k). В нашем коде такого лимита нет — решение
///     принимается вне продукта.
///  8. Контекст агента при этом цел: добивание сообщением возобновляет работу с того же места.
/// Паспорта нужны, чтобы на десятке прогонов увидеть, есть ли общая граница (токены/вызовы/
/// время/тип агента) — поэтому в записи лежат все четыре измерения разом.
///
/// Хранение — только в памяти, как у McpCallLog: данные диагностические, переживать рестарт
/// им незачем, а стор в data/ потянул бы за собой бэкапы (см. «Новое хранилище → сверься
/// с бэкапом» в CLAUDE.md).
/// </summary>
public sealed class SubagentRunLog
{
    // Хватает на разбор серии прогонов и не растёт бесконечно на долгоживущем процессе
    private const int MaxRuns = 200;

    private readonly Lock _lock = new();
    // Свежие — в конце; ключ дедупликации — AgentId (добитый агент дописывает ТОТ ЖЕ транскрипт,
    // и его паспорт обновляется, а не задваивается)
    private readonly List<SubagentRunPassport> _runs = [];
    // Последний оборванный прогон по чату — сигнал для реакции «продолжить» (потребляется один раз)
    private readonly Dictionary<string, SubagentRunPassport> _truncatedBySession = [];

    /// <summary>Записать/обновить паспорт прогона.</summary>
    public void Record(SubagentRunPassport passport)
    {
        lock (_lock)
        {
            var i = _runs.FindIndex(r => r.AgentId == passport.AgentId);
            if (i >= 0)
            {
                // Счётчик добиваний ведёт не ватчер, а тот, кто добивал (NoteNudge) — при
                // обновлении паспорта его нельзя потерять
                passport = passport with { NudgeAttempts = Math.Max(passport.NudgeAttempts, _runs[i].NudgeAttempts) };
                _runs.RemoveAt(i);
            }
            _runs.Add(passport);
            while (_runs.Count > MaxRuns) _runs.RemoveAt(0);

            if (passport.SessionId is not { Length: > 0 } sid) return;
            if (passport.Truncated) _truncatedBySession[sid] = passport;
            else _truncatedBySession.Remove(sid);
        }
    }

    /// <summary>
    /// Забрать отметку об оборванном сабагенте чата (одноразово). Потребитель — исполнитель
    /// задач: обрыв в его ходе значит «работа не закончена», а не «задача выполнена».
    /// </summary>
    public SubagentRunPassport? TakeTruncated(string sessionId)
    {
        lock (_lock)
        {
            if (!_truncatedBySession.Remove(sessionId, out var passport)) return null;
            return passport;
        }
    }

    /// <summary>Отметить отправленное добивание агента (растёт до потолка попыток).</summary>
    public void NoteNudge(string agentId)
    {
        lock (_lock)
        {
            var i = _runs.FindIndex(r => r.AgentId == agentId);
            if (i >= 0) _runs[i] = _runs[i] with { NudgeAttempts = _runs[i].NudgeAttempts + 1 };
        }
    }

    /// <summary>Последние паспорта, свежие — первыми.</summary>
    public IReadOnlyList<SubagentRunPassport> Recent(int limit = 50)
    {
        lock (_lock)
            return [.. Enumerable.Reverse(_runs).Take(Math.Clamp(limit, 1, MaxRuns))];
    }

    /// <summary>Сводка: сколько прогонов и какая доля из них оборвана (по типам агентов).</summary>
    public IReadOnlyList<SubagentTypeStat> Stats()
    {
        lock (_lock)
            return [.. _runs
                .GroupBy(r => r.AgentType ?? "(без типа)")
                .Select(g => new SubagentTypeStat(
                    g.Key,
                    g.Count(),
                    g.Count(r => r.Truncated),
                    (int)g.Average(r => r.DurationSeconds),
                    (int)g.Average(r => r.ToolUses),
                    (long)g.Average(r => (double)r.ContextTokens)))
                .OrderByDescending(s => s.Truncated)
                .ThenByDescending(s => s.Runs)];
    }
}

public record SubagentTypeStat(
    string AgentType, int Runs, int Truncated, int AvgSeconds, int AvgToolUses, long AvgContextTokens);
