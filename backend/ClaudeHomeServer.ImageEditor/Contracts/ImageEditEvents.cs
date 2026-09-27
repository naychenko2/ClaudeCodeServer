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
    // Нити картинок чата сменились (ADR-019): любая запись в ImageThreadStore
    public const string ThreadChanged = "image_thread_changed";
    // Выбор человека в полосе «Картинки» проекта сменился (PUT …/image-editor/prefs)
    public const string PrefsChanged = "image_prefs_changed";
}

// ChatSessionId и Initiator в событиях задачи — те же, что в ImageEditJobDto: карточка запуска
// агентом в ленте находит свою задачу. Хвостовые поля, старый фронт их не видит.

public record ImageEditProgressMessage(string JobId, string ProjectId, EditStage Stage, int? QueuePosition,
    string? ChatSessionId = null, ImageEditInitiator Initiator = ImageEditInitiator.Human, string? ThreadId = null)
    : ServerMessage(ImageEditEventNames.Progress);

// SizeNote — как в ImageEditJobDto: варианты не приведены к размеру исходника
public record ImageEditCompletedMessage(string JobId, string ProjectId, IReadOnlyList<int> Variants, EditCost? Cost,
    string? ChatSessionId = null, ImageEditInitiator Initiator = ImageEditInitiator.Human, string? SizeNote = null,
    string? ThreadId = null)
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
    ImageEditInitiator Initiator = ImageEditInitiator.Human,
    string? ThreadId = null)
    : ServerMessage(ImageEditEventNames.Failed);

// Нити чата после записи (ADR-019 §3): карточки-стопки в ленте перерисовываются из State.
// Базовый SessionId — чат, чьи нити сменились (new ImageThreadChangedMessage(…) { SessionId = chatId }).
// Уходит в группу владельца на КАЖДУЮ запись: фокус, шаг, задача, сохранение, откат
public record ImageThreadChangedMessage(string ProjectId, long Revision, Threads.ImageThreadsState State)
    : ServerMessage(ImageEditEventNames.ThreadChanged);

// Настройки проекта после записи — в группу владельца: другие вкладки перерисовывают полосу
public record ImageProjectPrefsChangedMessage(string ProjectId, Prefs.ImageProjectPrefs Prefs)
    : ServerMessage(ImageEditEventNames.PrefsChanged);
