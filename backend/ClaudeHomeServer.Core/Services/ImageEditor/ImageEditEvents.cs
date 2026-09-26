using ClaudeHomeServer.Protocol;

namespace ClaudeHomeServer.Services.ImageEditor;

// SignalR-события задачи редактора (ADR-017, раздел 7). Уходят в группу владельца через
// ISessionBroadcaster.ToOwner. Потерянное событие фронт догоняет GET …/jobs/{jobId}
// на onReconnected и при монтировании.
public static class ImageEditEventNames
{
    public const string Progress = "image_edit_progress";
    public const string Completed = "image_edit_completed";
    public const string Failed = "image_edit_failed";
    // Состояние редактора чата картинки сменилось (ADR-018 §2): чаще всего его поменял агент
    public const string ChatState = "image_chat_state";
}

// ChatSessionId и Initiator в событиях задачи — те же, что в ImageEditJobDto (ADR-018 §2):
// карточка запуска агентом в ленте находит свою задачу. Хвостовые поля, старый фронт их не видит.

public record ImageEditProgressMessage(string JobId, string ProjectId, EditStage Stage, int? QueuePosition,
    string? ChatSessionId = null, ImageEditInitiator Initiator = ImageEditInitiator.Human)
    : ServerMessage(ImageEditEventNames.Progress);

public record ImageEditCompletedMessage(string JobId, string ProjectId, IReadOnlyList<int> Variants, EditCost? Cost,
    string? ChatSessionId = null, ImageEditInitiator Initiator = ImageEditInitiator.Human)
    : ServerMessage(ImageEditEventNames.Completed);

// RetryQuote — котировка соседнего доступного поставщика: UI предлагает «Повторить через …»
// одним кликом, сам сервер на соседа не переходит
public record ImageEditFailedMessage(
    string JobId,
    string ProjectId,
    EditOutcome Outcome,
    bool? Charged,
    string? Error,
    ImageEditQuoteDto? RetryQuote,
    string? ChatSessionId = null,
    ImageEditInitiator Initiator = ImageEditInitiator.Human)
    : ServerMessage(ImageEditEventNames.Failed);

// Одно изменённое поле состояния: Field — имя поля ImageChatState в camelCase
// ("prompt", "model", "count"…), From/To — значения для метки «✦ модель сменил Claude»
public record ImageChatStateChange(string Field, System.Text.Json.JsonElement? From, System.Text.Json.JsonElement? To);

// Уходит в группу владельца (ISessionBroadcaster.ToOwner). Чат — в базовом SessionId
// (заполняется при создании: new ImageChatStateMessage(…) { SessionId = chatId }), редактор
// применяет событие, только если открыт именно этот чат. ChangedBy — кто поменял.
public record ImageChatStateMessage(
    string ProjectId,
    long Revision,
    ImageChatState State,
    ImageEditInitiator ChangedBy,
    IReadOnlyList<ImageChatStateChange> Changes)
    : ServerMessage(ImageEditEventNames.ChatState);
