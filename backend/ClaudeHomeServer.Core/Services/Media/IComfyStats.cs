using System.Text.Json.Serialization;

namespace ClaudeHomeServer.Services.Media;

// Шов для Main: контроллер Main читает данные медиа-стенда ComfyUI, не ссылаясь на Images
// напрямую (вертикаль Main не тянет Images). GetMediaAsync — снимок состояния стенда; null —
// стенд не настроен или недоступен.
public interface IComfyStats
{
    Task<ComfyMediaStatsSnapshot?> GetMediaAsync(CancellationToken ct);
}

// Снимок состояния медиа-стенда ComfyUI. Status — working | busy | down | disabled.
// OwnerId в строках (Running/Queue/LastHour) внутренний: контроллер Main обезличит чужих,
// наружу REST его не отдаёт. Gpus/LoadedModels — данные узла (маршрут /ccs/stats, шаг 31):
// Gpus — карты узла (роль media), LoadedModels — имена загруженных моделей; оба пустые,
// если узел не сообщает (маршрут не установлен) — тогда Unsupported несёт пометку.
public sealed record ComfyMediaStatsSnapshot(
    bool Configured,
    string Status,
    DateTimeOffset? DownSince,
    string? Version,
    IReadOnlyList<ComfyGpuInfo> Gpus,
    IReadOnlyList<string> LoadedModels,
    int SlotsUsed,
    int SlotsLimit,
    IReadOnlyList<ComfyJobRow> Running,
    IReadOnlyList<ComfyJobRow> Queue,
    IReadOnlyList<ComfyDoneRow> LastHour,
    IReadOnlyList<string> Unsupported);

// GPU-карта узла ComfyUI (маршрут /ccs/stats): индекс, имя, утилизация, память, температура,
// мощность. Uuid — пустой: узел отдаёт index, не uuid (одна карта на машине 192.168.7.107).
public sealed record ComfyGpuInfo(
    int Index,
    string Uuid,
    string Name,
    int? UtilPerc,
    int? MemUsedMb,
    int? MemTotalMb,
    int? TempC,
    double? PowerW,
    double? PowerLimitW);

// Строка прогона ComfyUI (идущий или в очереди). Foreign — чужой источник
// (instance | transcription | external). OwnerId внутренний, обезличивается Main'ом.
public sealed record ComfyJobRow(
    string Kind,
    string Op,
    bool Heavy,
    [property: JsonIgnore] string OwnerId,
    string? SessionId,
    string? ProjectId,
    string? Foreign,
    LocalGpuSteps? Steps,
    int? Percent,
    bool PercentExact,
    int? EtaSeconds,
    int? RemainingSeconds,
    int? StartInSeconds,
    DateTimeOffset? StartedAt);

// Завершённый за последний час прогон: Outcome — completed | failed | cancelled.
public sealed record ComfyDoneRow(
    string Kind,
    string Op,
    [property: JsonIgnore] string OwnerId,
    string Outcome,
    int? RunSeconds,
    int? EtaSeconds,
    DateTimeOffset FinishedAt);
