using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services.AudioEditor.Prefs;
using ClaudeHomeServer.Services.AudioEditor.Threads;

namespace ClaudeHomeServer.Services.AudioEditor.Jobs;

// SignalR-события модуля «Звук» (ADR-021 §2): уходят в группу владельца через Core-шов
// ISessionBroadcaster.ToOwner. Потерянное событие фронт догоняет чтением задачи и нитей
public static class AudioEditEventNames
{
    public const string Progress = "audio_edit_progress";
    public const string Completed = "audio_edit_completed";
    public const string Failed = "audio_edit_failed";
    // Нити звука чата сменились: любая запись исполнителя или ручек в AudioThreadStore
    public const string ThreadChanged = "audio_thread_changed";
    // Выбор человека для режима области сменился
    public const string PrefsChanged = "audio_prefs_changed";
}

// Variant — номер варианта, который сейчас идёт (варианты — отдельные прогоны драйвера)
public record AudioEditProgressMessage(string JobId, string ScopeKey, AudioStage Stage, int? QueuePosition, int? EtaSeconds,
    int Variant, int Count, string? ChatSessionId, string? ThreadId, AudioEditInitiator Initiator)
    : ServerMessage(AudioEditEventNames.Progress);

// Error — у частичного успеха: часть вариантов готова, следующий не получился
public record AudioEditCompletedMessage(string JobId, string ScopeKey, IReadOnlyList<int> Variants, AudioCost? Cost,
    string? Error, string? ChatSessionId, string? ThreadId, AudioEditInitiator Initiator)
    : ServerMessage(AudioEditEventNames.Completed);

// RetryQuote — котировка соседа с той же операцией и тем же видом голоса: UI предлагает «Повторить
// через …» одним кликом, сам сервер на соседа не переходит никогда
public record AudioEditFailedMessage(string JobId, string ScopeKey, AudioOutcome Outcome, bool? Charged, string? Error,
    AudioQuoteDto? RetryQuote, string? ChatSessionId, string? ThreadId, AudioEditInitiator Initiator)
    : ServerMessage(AudioEditEventNames.Failed);

// Нити чата после записи; базовый SessionId — чат, чьи нити сменились
public record AudioThreadChangedMessage(string ScopeKey, long Revision, AudioThreadsState State)
    : ServerMessage(AudioEditEventNames.ThreadChanged);

public record AudioPrefsChangedMessage(string ScopeKey, string Mode, AudioModePrefs Prefs)
    : ServerMessage(AudioEditEventNames.PrefsChanged);
