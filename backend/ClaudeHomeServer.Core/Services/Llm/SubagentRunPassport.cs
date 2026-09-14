namespace ClaudeHomeServer.Services.Llm;

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
/// <param name="FinishedBy">
/// чем закрыт прогон (строкой, без enum — порядок значений исторический):
///  • <c>bg_done</c> — фоновый агент, продукт объявил результат готовым посреди хода координатора;
///  • <c>bg_aborted</c> — фон отменён (внешнее завершение отмены без ожидания дозаписи);
///  • <c>tool_result</c> — синхронный Task: координатор дождался результата по tool_result;
///  • <c>run_end</c> — обычное завершение: ватчер Dispose дренажировал хвост транскрипта;
///  • <c>interrupted</c> — прогон убит пользовательским прерыванием хода (Kill из Interrupt()),
///    транскрипт на диске цел, содержимое годится для продолжения после рестарта.
/// </param>
/// <param name="CliContextWindow">
/// окно контекста, объявленное CLI на запуске прогона (CLAUDE_CODE_MAX_CONTEXT_TOKENS);
/// 0 — не объявлялось. Без него разбор обрывов упирается в догадки: суффикс [1m] живёт
/// только во флаге --model и внутрь сабагента не передаётся, поэтому «контекст 198k при
/// обрыве» читается совсем по-разному в окне 200k и в окне 1M.
/// </param>
///
/// Этап 5, шаг 2: record переехал в Core из `Services/Llm/Claude/SubagentRunLog.cs`
/// (шаг 2 разрывает цикл Llm ⇄ Turn через шину `SubagentRunCompleted` в Services/Turn/TurnEvents.cs;
/// побочно — импорт `using ClaudeHomeServer.Services.Llm.Claude;` из TurnEvents больше
/// не нужен, что закрывает «одно место Llm.Claude в Turn» из задачи). Реализация record-а
/// в Llm.Claude больше не нужна — тип тонкий, конструктор без логики.
/// </summary>
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
    DateTime RecordedAt,
    int CliContextWindow = 0)
{
    /// <summary>
    /// Прогон закрыт как ФОНОВЫЙ агент (bg_done/bg_aborted): его результат продукт объявил
    /// готовым сам, посреди хода координатора — конец хода при этом не наступает, и разбор
    /// «по result» может не наступить вовсе. У tool_result (синхронный Task), run_end
    /// (обычная смерть прогона) и interrupted (убит прерыванием хода) конец хода либо рядом,
    /// либо явился причиной обрыва — там разбора по result достаточно, а класс «убит» идёт
    /// по отдельной ветке политики добиваний (см. ShouldNudgeSubagent с признаком
    /// isInterruptedRun и SubagentPrompts.ResumeInterrupted).
    /// </summary>
    public bool FinishedInBackground => FinishedBy is "bg_done" or "bg_aborted";
}
