using System.Text.Json.Serialization;
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
    // Префы области записал агент (формат проекта): свежие префы нужны чипу «Формат», он читает их из стора
    public const string PrefsChanged = "video_prefs_changed";
    public const string Progress = "video_edit_progress";
    public const string Completed = "video_edit_completed";
    public const string Failed = "video_edit_failed";
}

public static class VideoProgressStages
{
    public const string Queued = "queued";
    public const string Running = "running";
    // Вторая тяжёлая задача GPU «Макс» local H3: латентный апскейл съёмки 768p до 1440p. Отменяется отдельно
    public const string Upscaling = "upscaling";
    public const string Downloading = "downloading";
}

public record VideoThreadChangedMessage(string ScopeKey, long Revision, VideoThreadsStateDto State)
    : ServerMessage(VideoEditorEventNames.ThreadChanged);

public record VideoFilmChangedMessage(string ScopeKey, string Path, FilmStateDto State)
    : ServerMessage(VideoEditorEventNames.FilmChanged);

public record VideoPrefsChangedMessage(string ScopeKey, VideoPrefsDto Prefs)
    : ServerMessage(VideoEditorEventNames.PrefsChanged);

// Stage — VideoProgressStages.* (queued | running | upscaling | downloading); Variant — номер варианта, который идёт;
// Percent — доля готовности 0..1 текущей стадии от поставщика (необязательно: нет данных — поля нет, клиент считает
// по ETA). EtaSeconds и Percent всегда относятся к ТЕКУЩЕЙ стадии: на upscaling они отдельные от съёмки.
// UpscaleEtaSeconds — сколько займёт апскейл «Макс» local H3 после съёмки («следом · ~15 мин»); у запуска без
// апскейла и у самого upscaling поля нет. Gpu — живой снимок очереди GPU ComfyUI (шаг 30): только
// отображение, null — поставщик не знает; null не пишется (camelCase как соседние поля)
public record VideoEditProgressMessage(string JobId, string ScopeKey, string SceneId, string Stage, int? QueuePosition,
    int? EtaSeconds, int Variant, int Count, string Initiator, double? Percent = null, int? UpscaleEtaSeconds = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    ClaudeHomeServer.Services.Media.LocalGpuQueueInfo? Gpu = null)
    : ServerMessage(VideoEditorEventNames.Progress);

// Error — у частичного успеха: часть вариантов готова, следующий не получился
public record VideoEditCompletedMessage(string JobId, string ScopeKey, string SceneId, IReadOnlyList<int> Variants,
    VideoCostDto? Cost, string? Error, string Initiator)
    : ServerMessage(VideoEditorEventNames.Completed);

public record VideoEditFailedMessage(string JobId, string ScopeKey, string SceneId, bool? Charged, string? Error,
    RetryQuote? RetryQuote, string Initiator)
    : ServerMessage(VideoEditorEventNames.Failed);
