using System.Text.Json.Serialization;

namespace ClaudeCodeServer.Protocol;

// Сообщения от сервера к клиенту (через SignalR)
public abstract record ServerMessage(string Type)
{
    // Заполняется при броадкасте в SessionManager — позволяет клиенту роутить по сессии
    public string SessionId { get; init; } = "";
}

public record McpServerInfo(string Name, string Status);

public record SessionStartedMessage(string ClaudeSessionId, bool IsResume, string Model, string Mode,
    string? Cwd = null, int ToolCount = 0, IReadOnlyList<McpServerInfo>? McpServers = null)
    : ServerMessage("session_started");

public record TextDeltaMessage(string Text)
    : ServerMessage("text_delta");

public record ThinkingDeltaMessage(string Text)
    : ServerMessage("thinking_delta");

public record ToolUseMessage(string Id, string Name, object Input, string? ParentToolUseId = null)
    : ServerMessage("tool_use");

// Стриминг аргументов инструмента (input_json_delta) — накопленный частичный JSON
public record ToolInputDeltaMessage(string ToolUseId, string PartialJson)
    : ServerMessage("tool_input_delta");

public record ToolResultMessage(string ToolUseId, string Content, bool IsError)
    : ServerMessage("tool_result");

public record PermissionRequestMessage(string RequestId, string ToolName, object ToolInput)
    : ServerMessage("permission_request");

// AskUserQuestion: в режиме stdio приходит как обычный tool_use, ответ — tool_result в stdin
public record AskQuestionMessage(string ToolUseId, object Input)
    : ServerMessage("ask_question");

public record FileChangedMessage(string Path, int Added, int Removed)
    : ServerMessage("file_changed");

public record ResultMessage(string Subtype, long DurationMs, int NumTurns, UsageInfo? Usage, double? TotalCostUsd, string? ApiErrorStatus = null, IReadOnlyList<string>? PermissionDenials = null)
    : ServerMessage("result");

// Ответ оборван по лимиту токенов (assistant stop_reason == max_tokens)
public record TruncatedMessage() : ServerMessage("truncated");

// Скрытое (зашифрованное) размышление — блок redacted_thinking
public record RedactedThinkingMessage() : ServerMessage("redacted_thinking");

public record ErrorMessage(string Text)
    : ServerMessage("error");

// Мягкий лимит API во время хода: claude приостанавливается до сброса (rate_limit_event)
public record RateLimitMessage(string LimitType, string? ResetsAt)
    : ServerMessage("rate_limit");

// Граница компакции контекста: Claude свернул часть истории (system/compact_boundary)
public record CompactBoundaryMessage(string Trigger, int? PreTokens)
    : ServerMessage("compact_boundary");

public record ExitedMessage()
    : ServerMessage("exited");

public record StatusChangedMessage(string Status, string? LastMessage = null, int MessageCount = 0)
    : ServerMessage("status_changed");

public record UsageInfo(int InputTokens, int OutputTokens, int CacheReadTokens, int CacheCreationTokens);

// Сообщения от клиента к серверу
public record ClientMessage([property: JsonPropertyName("type")] string Type);

public record SendMessageRequest(string Text, string[]? AttachedPaths = null) : ClientMessage("send_message");

public record PermissionDecisionRequest(string RequestId, string Behavior) : ClientMessage("permission_decision");

public record InterruptRequest() : ClientMessage("interrupt");
