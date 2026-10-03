using ClaudeHomeServer.Protocol;

namespace ClaudeHomeServer.Services.VideoEditor.Contracts;

// SignalR-события модуля: уходят в группу владельца через Core-шов ISessionBroadcaster.ToOwner.
// Потерянное событие фронт догоняет чтением состояния. Базовый SessionId — чат события
public static class VideoEditorEventNames
{
    // Нити сцен чата сменились: любая запись исполнителя или ручек
    public const string ThreadChanged = "video_thread_changed";
    // Фильм изменён (правка, сборка, пометки): в состоянии — свежий FilmStateDto
    public const string FilmChanged = "video_film_changed";
    public const string Progress = "video_edit_progress";
    public const string Completed = "video_edit_completed";
    public const string Failed = "video_edit_failed";
}

public record VideoThreadChangedMessage(string ScopeKey, long Revision, VideoThreadsStateDto State)
    : ServerMessage(VideoEditorEventNames.ThreadChanged);

public record VideoFilmChangedMessage(string ScopeKey, string Path, FilmStateDto State)
    : ServerMessage(VideoEditorEventNames.FilmChanged);

// Stage — queued | running | downloading; Variant — номер варианта, который идёт
public record VideoEditProgressMessage(string JobId, string ScopeKey, string SceneId, string Stage, int? QueuePosition,
    int? EtaSeconds, int Variant, int Count, string Initiator)
    : ServerMessage(VideoEditorEventNames.Progress);

// Error — у частичного успеха: часть вариантов готова, следующий не получился
public record VideoEditCompletedMessage(string JobId, string ScopeKey, string SceneId, IReadOnlyList<int> Variants,
    VideoCostDto? Cost, string? Error, string Initiator)
    : ServerMessage(VideoEditorEventNames.Completed);

public record VideoEditFailedMessage(string JobId, string ScopeKey, string SceneId, bool? Charged, string? Error,
    RetryQuote? RetryQuote, string Initiator)
    : ServerMessage(VideoEditorEventNames.Failed);
