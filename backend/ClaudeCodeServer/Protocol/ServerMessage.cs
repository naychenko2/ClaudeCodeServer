using System.Text.Json.Serialization;

namespace ClaudeCodeServer.Protocol;

// Сообщения от сервера к клиенту (через SignalR)
public abstract record ServerMessage(string Type)
{
    // Заполняется при броадкасте в SessionManager — позволяет клиенту роутить по сессии
    public string SessionId { get; init; } = "";
}

public record SessionStartedMessage(string ClaudeSessionId, bool IsResume, string Model, string Mode)
    : ServerMessage("session_started");

public record TextDeltaMessage(string Text)
    : ServerMessage("text_delta");

public record ThinkingDeltaMessage(string Text)
    : ServerMessage("thinking_delta");

public record ToolUseMessage(string Id, string Name, object Input)
    : ServerMessage("tool_use");

public record ToolResultMessage(string ToolUseId, string Content, bool IsError)
    : ServerMessage("tool_result");

public record PermissionRequestMessage(string RequestId, string ToolName, object ToolInput)
    : ServerMessage("permission_request");

public record FileChangedMessage(string Path, int Added, int Removed)
    : ServerMessage("file_changed");

public record ResultMessage(string Subtype, long DurationMs, int NumTurns, UsageInfo? Usage)
    : ServerMessage("result");

public record ErrorMessage(string Text)
    : ServerMessage("error");

public record ExitedMessage()
    : ServerMessage("exited");

public record UsageInfo(int InputTokens, int OutputTokens, int CacheReadTokens, int CacheCreationTokens);

// Сообщения от клиента к серверу
public record ClientMessage([property: JsonPropertyName("type")] string Type);

public record SendMessageRequest(string Text, string[]? AttachedPaths = null) : ClientMessage("send_message");

public record PermissionDecisionRequest(string RequestId, string Behavior) : ClientMessage("permission_decision");

public record InterruptRequest() : ClientMessage("interrupt");
