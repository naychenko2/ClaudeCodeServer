using System.Text.Json.Serialization;

namespace ClaudeCodeServer.Protocol;

[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(StoredUserMessage), "user_message")]
[JsonDerivedType(typeof(StoredSessionStartedMessage), "session_started")]
[JsonDerivedType(typeof(StoredTextMessage), "text")]
[JsonDerivedType(typeof(StoredThinkingMessage), "thinking")]
[JsonDerivedType(typeof(StoredToolUseMessage), "tool_use")]
[JsonDerivedType(typeof(StoredFileChangedMessage), "file_changed")]
[JsonDerivedType(typeof(StoredResultMessage), "result")]
[JsonDerivedType(typeof(StoredErrorMessage), "error")]
public abstract class StoredMessage { }

public class StoredUserMessage(string text, string[]? attachedPaths = null) : StoredMessage
{
    public string Text { get; init; } = text;
    public string[]? AttachedPaths { get; init; } = attachedPaths;
}

public class StoredSessionStartedMessage(string model, string mode) : StoredMessage
{
    public string Model { get; init; } = model;
    public string Mode { get; init; } = mode;
}

public class StoredTextMessage(string text) : StoredMessage
{
    public string Text { get; init; } = text;
}

public class StoredThinkingMessage(string text) : StoredMessage
{
    public string Text { get; init; } = text;
}

public class StoredFileChangedMessage(string path, int added, int removed) : StoredMessage
{
    public string Path { get; init; } = path;
    public int Added { get; init; } = added;
    public int Removed { get; init; } = removed;
}

public class StoredResultMessage(string subtype, long durationMs, int numTurns,
    UsageInfo? usage = null, double? totalCostUsd = null, string? apiErrorStatus = null) : StoredMessage
{
    public string Subtype { get; init; } = subtype;
    public long DurationMs { get; init; } = durationMs;
    public int NumTurns { get; init; } = numTurns;
    public UsageInfo? Usage { get; init; } = usage;
    public double? TotalCostUsd { get; init; } = totalCostUsd;
    public string? ApiErrorStatus { get; init; } = apiErrorStatus;
}

public class StoredErrorMessage(string text) : StoredMessage
{
    public string Text { get; init; } = text;
}

// Result/IsError заполняются позже — при получении tool_result от Claude
public class StoredToolUseMessage : StoredMessage
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public object? Input { get; init; }
    public string? Result { get; set; }
    public bool IsError { get; set; }
    public string? ParentToolUseId { get; init; }
}
