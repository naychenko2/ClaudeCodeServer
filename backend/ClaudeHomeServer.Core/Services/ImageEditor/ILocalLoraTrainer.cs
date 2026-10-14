using System.Text.RegularExpressions;
using ClaudeHomeServer.Services.Media;

namespace ClaudeHomeServer.Services.ImageEditor;

// Шов обучения LoRA персонажа (ADR-026): реализация живёт в Images, потребители берут его
// необязательным параметром. Контракт — docs/adr/ADR-026-contracts.md, §3.
public interface ILocalLoraTrainer
{
    // LocalMedia:Enabled && LocalMedia:LoraTrainEnabled. ComfyUI при этом может лежать
    bool Enabled { get; }

    // Идущее на инстансе обучение (любого владельца) или null. Mine — оно этого владельца
    LoraTrainBusy? Busy(string ownerId);

    Task<LoraTrainStartResult> StartAsync(LoraTrainStart request, CancellationToken ct);

    // Чужая задача неотличима от несуществующей: null. Обновляет статус из истории ComfyUI
    Task<LoraTrainState?> GetAsync(string ownerId, string jobId, CancellationToken ct);

    // Задачи lora_train владельца, новые сверху, без опроса ComfyUI
    IReadOnlyList<LoraTrainState> ListFor(string ownerId);

    // Все задачи lora_train стора, любых владельцев. Только для фонового доводчика
    IReadOnlyList<LoraTrainState> ListAll();

    // asAdmin = true — отмена без проверки владельца; иначе чужая задача → NotFound
    Task<LoraCancelResult> CancelAsync(string ownerId, string jobId, bool asAdmin, CancellationToken ct);

    // Идемпотентно: первый вызов ставит op lora_delete, следующие отдают его исход.
    // Deleted — каталога прогона на GPU нет (в том числе «и не было»)
    Task<LoraDeleteState> DeleteRunAsync(string ownerId, string projectId, string slug, string runId, CancellationToken ct);
}

// Триггерное слово обученной версии (ADR-026-contracts §1); конец строки — `\z`, не `$`
public static class LoraTrigger
{
    private static readonly Regex Pattern = new(@"^[a-z][a-z0-9_]{2,23}\z", RegexOptions.Compiled);

    public static bool IsValid(string? trigger) => trigger is not null && Pattern.IsMatch(trigger);
}

public static class LoraTrainPresets
{
    public const string Standard = "standard";
    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal) { Standard };

    // Шаги и скорость обучения пресета standard (ADR-026 §3): 2000 шагов, ≈2,35 с на шаг
    public const int Steps = 2000;
    public const double SecondsPerStep = 2.35;

    // Полное время обучения по пресету: шаги × время шага + старт; неизвестный пресет — null
    public static int? Eta(string preset, int startSeconds) =>
        preset == Standard ? Remaining(0, Steps) + startSeconds : null;

    // Остаток по известному шагу
    public static int Remaining(int step, int totalSteps) =>
        (int)Math.Ceiling(Math.Max(0, totalSteps - step) * SecondsPerStep);
}

// HostPath — абсолютный путь файла в data/lora-training; Name — имя по регэкспу контракта,
// под ним фото уедет в input ComfyUI
public sealed record LoraTrainPhoto(string Name, string HostPath);

public sealed record LoraTrainStart(
    string OwnerId,
    string ProjectId,
    string Slug,
    string RunId,
    string Preset,
    string Trigger,
    IReadOnlyList<LoraTrainPhoto> Photos,
    // Метка происхождения прогона (шаг 23): null — вызывающая сторона ещё не передаёт (шаги 28–30).
    LocalJobOrigin? Origin = null);

public enum LoraTrainErrorCode
{
    Disabled, Unavailable, GpuBusy, OwnerBusy, QueueFull, TooFewPhotos, BadPreset, BadTrigger, BadRequest,
}

public sealed record LoraTrainBusy(DateTime? BusyUntil, bool Mine, string JobId);

public sealed record LoraTrainStartResult(
    string? JobId,
    int? QueuePosition,
    int? EtaSeconds,
    LoraTrainErrorCode? Error = null,
    string? Message = null,
    LoraTrainBusy? Busy = null);

public enum LoraTrainStatus { Queued, Running, Completed, Failed, Cancelled }

// Reason: no_face | many_faces | small_face | unreadable
public sealed record LoraPhotoRejection(string Name, string Reason);

public sealed record LoraTrainFailure(string Code, string Message);

public sealed record LoraTrainState(
    string JobId,
    string OwnerId,
    string ProjectId,
    string Slug,
    string RunId,
    string Preset,
    LoraTrainStatus Status,
    int? QueuePosition,
    int? Step,
    int? TotalSteps,
    int? EtaSeconds,
    DateTime CreatedAt,
    DateTime? StartedAt,
    DateTime? FinishedAt,
    string? LoraFile,
    LoraTrainFailure? Error,
    int? PhotosKept,
    IReadOnlyList<LoraPhotoRejection>? Rejected);

public enum LoraCancelResult { Cancelled, NotFound, AlreadyFinished }

public enum LoraDeleteState { Pending, Deleted, Failed }
