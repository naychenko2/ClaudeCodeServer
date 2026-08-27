using System.Text.Json;

namespace ClaudeHomeServer.Services.Llm.Claude;

/// <summary>
/// Счётчик прогона сабагента: принимает строки его транскрипта (agent-*.jsonl) по мере чтения
/// и в любой момент умеет собрать паспорт (<see cref="SubagentRunPassport"/>).
///
/// Отделён от ватчера, чтобы разбор транскрипта был проверяемым без файловой системы.
/// Формат строк (CLI 2.x): у каждой — timestamp/agentId/type; у assistant внутри message
/// лежат model, stop_reason, usage и блоки content (text/thinking/tool_use); у user —
/// либо строковый content (текст, присланный агенту), либо массив с tool_result.
/// </summary>
internal sealed class SubagentRunTally(string agentId)
{
    public string AgentId { get; } = agentId;
    public string? AgentType { get; set; }
    public string? Description { get; set; }
    public string? ToolUseId { get; set; }
    // Ватчер подключился к уже начатому транскрипту: часть строк не прочитана никогда
    public bool Partial { get; set; }
    // В этом прогоне ватчера Feed вызывался (были новые строки в транскрипте)
    public bool FeedCalled { get; set; }

    public DateTime StartedAt { get; private set; }
    public DateTime LastActivityAt { get; private set; }
    public int ToolUses { get; private set; }
    public int AssistantMessages { get; private set; }
    public int Prompts { get; private set; }
    public long ContextTokens { get; private set; }
    public long OutputTokens { get; private set; }
    public string? LastStopReason { get; private set; }
    public string? LastTool { get; private set; }
    public string? Model { get; private set; }

    /// <summary>
    /// Обрыв на середине: последнее сообщение модели закончилось вызовом инструмента и
    /// продолжения не последовало (факт 2 разбора — см. <see cref="SubagentRunLog"/>).
    /// Отчёт агента всегда приходит с end_turn, поэтому признак однозначен.
    /// </summary>
    public bool Truncated => LastStopReason == "tool_use";

    public void Feed(JsonElement root)
    {
        FeedCalled = true;
        if (!root.TryGetProperty("type", out var t)) return;
        var type = t.GetString();

        if (root.TryGetProperty("timestamp", out var ts) && ts.ValueKind == JsonValueKind.String
            && DateTimeOffset.TryParse(ts.GetString(), out var at))
        {
            var utc = at.UtcDateTime;
            if (StartedAt == default) StartedAt = utc;
            if (utc > LastActivityAt) LastActivityAt = utc;
        }

        if (!root.TryGetProperty("message", out var msg) || msg.ValueKind != JsonValueKind.Object) return;

        if (type == "user")
        {
            // Строковый content — текст, присланный агенту: исходная постановка либо добивание
            // («The coordinator sent a message while you were working…»). Массив — tool_result.
            if (msg.TryGetProperty("content", out var uc) && uc.ValueKind == JsonValueKind.String) Prompts++;
            return;
        }
        if (type != "assistant") return;

        AssistantMessages++;
        if (msg.TryGetProperty("model", out var m) && m.ValueKind == JsonValueKind.String)
            Model = m.GetString();
        // stop_reason у части сообщений null (промежуточные записи стрима) — помним последний известный
        if (msg.TryGetProperty("stop_reason", out var sr) && sr.ValueKind == JsonValueKind.String)
            LastStopReason = sr.GetString();

        if (msg.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object)
        {
            // Окно последнего запроса: почти весь контекст сабагента идёт кэшем, поэтому
            // считать только input_tokens бессмысленно — по нему обрыв никогда не сойдётся
            ContextTokens = Num(usage, "input_tokens") + Num(usage, "cache_read_input_tokens")
                + Num(usage, "cache_creation_input_tokens");
            OutputTokens += Num(usage, "output_tokens");
        }

        if (!msg.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array) return;
        foreach (var block in content.EnumerateArray())
        {
            if (!block.TryGetProperty("type", out var bt) || bt.GetString() != "tool_use") continue;
            ToolUses++;
            if (block.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String)
                LastTool = n.GetString();
        }
    }

    public SubagentRunPassport Build(string? sessionId, string finishedBy, long transcriptBytes,
        int cliContextWindow = 0) => new(
        AgentId: AgentId,
        AgentType: AgentType,
        Description: Description,
        SessionId: sessionId,
        ToolUseId: ToolUseId,
        StartedAt: StartedAt,
        LastActivityAt: LastActivityAt,
        DurationSeconds: StartedAt == default || LastActivityAt <= StartedAt
            ? 0 : (int)(LastActivityAt - StartedAt).TotalSeconds,
        ToolUses: ToolUses,
        AssistantMessages: AssistantMessages,
        Prompts: Prompts,
        ContextTokens: ContextTokens,
        OutputTokens: OutputTokens,
        LastStopReason: LastStopReason,
        Truncated: Truncated,
        LastTool: LastTool,
        Model: Model,
        TranscriptBytes: transcriptBytes,
        NudgeAttempts: 0,
        Partial: Partial,
        FinishedBy: finishedBy,
        RecordedAt: DateTime.UtcNow,
        CliContextWindow: cliContextWindow);

    private static long Num(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n) ? n : 0;
}
