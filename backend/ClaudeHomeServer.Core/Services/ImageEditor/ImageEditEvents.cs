using ClaudeHomeServer.Protocol;

namespace ClaudeHomeServer.Services.ImageEditor;

// SignalR-события задачи редактора (ADR-016, раздел 7). Уходят в группу владельца через
// ISessionBroadcaster.ToOwner. Потерянное событие фронт догоняет GET …/jobs/{jobId}
// на onReconnected и при монтировании.
public static class ImageEditEventNames
{
    public const string Progress = "image_edit_progress";
    public const string Completed = "image_edit_completed";
    public const string Failed = "image_edit_failed";
}

public record ImageEditProgressMessage(string JobId, string ProjectId, EditStage Stage, int? QueuePosition)
    : ServerMessage(ImageEditEventNames.Progress);

public record ImageEditCompletedMessage(string JobId, string ProjectId, IReadOnlyList<int> Variants, EditCost? Cost)
    : ServerMessage(ImageEditEventNames.Completed);

// RetryQuote — котировка соседнего доступного поставщика: UI предлагает «Повторить через …»
// одним кликом, сам сервер на соседа не переходит
public record ImageEditFailedMessage(
    string JobId,
    string ProjectId,
    EditOutcome Outcome,
    bool? Charged,
    string? Error,
    ImageEditQuoteDto? RetryQuote)
    : ServerMessage(ImageEditEventNames.Failed);
