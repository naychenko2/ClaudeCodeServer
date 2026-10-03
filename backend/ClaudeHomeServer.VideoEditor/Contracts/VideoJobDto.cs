namespace ClaudeHomeServer.Services.VideoEditor.Contracts;

// Состояние задачи для GET jobs/{jobId} (добавлено после КТ-1). Status — VideoJobStatuses.*: файлы вариантов в рабочей папке читаются ручкой версии сцены
public sealed record VideoJobDto(
    string JobId,
    string ScopeKey,
    string Status,
    string Provider,
    string Model,
    int Count,
    IReadOnlyList<int> Variants,
    VideoCostDto? Cost,
    string? Outcome,
    bool? Charged,
    string? Error,
    int? QueuePosition,
    int? EtaSeconds,
    DateTime CreatedAt,
    string? ChatSessionId,
    string? SceneId,
    string Initiator,
    string License);

public static class VideoJobStatuses
{
    public const string Queued = "queued";
    public const string Running = "running";
    public const string Downloading = "downloading";
    public const string Completed = "completed";
    public const string Failed = "failed";
    public const string Cancelled = "cancelled";
}
