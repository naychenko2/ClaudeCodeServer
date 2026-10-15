using System.Text.Json.Nodes;
using ClaudeHomeServer.Services.Media;

namespace ClaudeHomeServer.Services.MediaHistory;

// Запись истории: один прогон ComfyUI по prompt_id. События журнала (enqueued/started/finished)
// склеиваются в одну запись. Origin — метка происхождения (Core): вид, операция, владелец,
// проект, источник, job_id. Model/Prompt/Params — из enqueued; Outcome/RunSeconds/Error — из
// finished. Времена — по часам бэкенда (ts событий).
public sealed class MediaHistoryRecord
{
    public string PromptId { get; }
    public LocalJobOrigin? Origin { get; internal set; }
    public string? Model { get; internal set; }
    public string? Prompt { get; internal set; }
    public JsonObject? Params { get; internal set; }
    public string? Outcome { get; internal set; }
    public int? RunSeconds { get; internal set; }
    public string? Error { get; internal set; }
    public DateTimeOffset? EnqueuedAt { get; internal set; }
    public DateTimeOffset? StartedAt { get; internal set; }
    public DateTimeOffset? FinishedAt { get; internal set; }

    internal MediaHistoryRecord(string promptId) => PromptId = promptId;

    // Вид прогона (image|video|audio|train|other) — из метки происхождения
    public string? Kind => Origin?.Kind;

    // Операция (generate_image, text_to_video, …) — из метки происхождения
    public string? Op => Origin?.Op;

    // Статус для ленты: завершённые — по исходу, в процессе — «идёт»/«ждёт»
    public string Status =>
        FinishedAt is not null ? (Outcome ?? "failed")
        : StartedAt is not null ? "running"
        : EnqueuedAt is not null ? "queued"
        : "unknown";
}
