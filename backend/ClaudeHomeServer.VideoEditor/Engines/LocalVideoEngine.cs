using System.Text.Json;
using System.Text.Json.Nodes;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.Media;

namespace ClaudeHomeServer.Services.VideoEditor.Engines;

// Поставщик «Локальные модели» для видео (ADR-022 §3) поверх Core-шва ILocalVideoMedia: MiniMax H3 на своей
// GPU, вертикаль Images модулю не видна. Денег нет — котировка ноль в единицах free плюс ETA по таблице
// замеров и длина общей очереди ComfyUI. Нет шва (Images выключена) или тумблеров — поставщик не заведён
// и Enabled=false, а не исключение.
//
// Только серверный проект: в личной области проекта нет, у локального проекта (ADR-016) файлы живут на
// устройстве. Отказ — ScopeRefusal до чтения входов и до обращения к шву; локальность — только через
// ProjectCapabilities.
public sealed class LocalVideoEngine(ILocalVideoMedia? media) : IVideoEngine, IVideoQuoter
{
    public const string ProviderKey = "local";
    public const string MiniMaxH3 = "minimax-h3";

    public const string PersonalScopeReason =
        "Локальные модели работают только в чате проекта: в личном чате им некуда положить ролик. " +
        "Откройте чат проекта или выберите облачного поставщика";

    public const string DeviceProjectReason =
        "Локальные модели работают только с проектом на сервере, а файлы этого проекта лежат на его устройстве. " +
        "Выберите облачного поставщика";

    // Потолок длины — значение по умолчанию LocalMedia:MaxVideoSeconds; машинный потолок ниже проверяет адаптер
    public const int MaxSeconds = 10;

    // Потолок входного кадра — LocalMediaService.MaxInputBytes
    private const long MaxFrameBytes = 30 * 1024 * 1024;

    // Потолок одного запуска с ожиданием очереди: 10 с full ≈ 16 мин, впереди может стоять ещё один ролик
    private static readonly TimeSpan JobCeiling = TimeSpan.FromMinutes(40);

    internal TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(3);
    internal TimeSpan Ceiling { get; set; } = JobCeiling;

    // Частные параметры под именами local_image_to_video: size — full | half, fast — ускоренный режим
    private static readonly IReadOnlySet<string> H3Params = new HashSet<string>(StringComparer.Ordinal) { "size", "fast" };

    // Пропорция ролика H3 следует за первым кадром: портретный кадр — портретный ролик
    private static readonly IReadOnlyList<VideoModelInfo> LocalModels =
    [
        new(MiniMaxH3, "MiniMax H3",
            new VideoCaps([.. Enumerable.Range(1, MaxSeconds)], ["16:9", "9:16"], Sound: true, LastFrame: true,
                VideoLicense.NotStated, VideoPriceUnits.Free, FirstFrame: true, MaxFrameBytes: MaxFrameBytes)),
    ];

    public string Key => ProviderKey;
    public string Label => "Локальные модели";
    public string PriceUnit => VideoPriceUnits.Free;
    public bool Enabled => media?.Available == true;
    public bool Registered => media?.Configured == true;
    public IReadOnlyList<VideoModelInfo> Models => LocalModels;

    public string? ScopeRefusal(VideoEditScope scope)
    {
        if (scope.IsPersonal || scope.Project is not { } project) return PersonalScopeReason;
        return ProjectCapabilities.FilesOnServer(project) ? null : DeviceProjectReason;
    }

    public IReadOnlySet<string>? ParamNames(VideoModelInfo model) =>
        string.Equals(model.Id, MiniMaxH3, StringComparison.OrdinalIgnoreCase) ? H3Params : null;

    // ── Котировка ────────────────────────────────────────────────────────────────

    public int? ExpectedSeconds(VideoModelInfo model, VideoRequest request) =>
        media is null || ScopeRefusal(request.Scope) is not null || Compose(request).Run is not { } run
            ? null
            : media.EtaSeconds(run);

    public async Task<VideoEstimate> EstimateAsync(VideoModelInfo model, VideoRequest request, CancellationToken ct)
    {
        if (ScopeRefusal(request.Scope) is { } refusal) throw new VideoEngineUnavailableException(refusal);
        if (media is null || !media.Available) throw new VideoEngineUnavailableException("Локальные модели сейчас недоступны");
        var queue = await media.QueueLengthAsync(ct)
            ?? throw new VideoEngineUnavailableException("Локальная видеокарта не отвечает (ComfyUI недоступен)");
        var eta = Compose(request).Run is { } run ? media.EtaSeconds(run) : null;
        return new VideoEstimate(0, VideoPriceUnits.Free, false, VideoEstimateSources.Provider, eta, queue);
    }

    // ── Запуск ───────────────────────────────────────────────────────────────────

    public async Task<VideoResult> RunAsync(VideoRequest req, IProgress<VideoProgress> progress, CancellationToken ct)
    {
        if (ScopeRefusal(req.Scope) is { } refusal) return VideoResult.Fail(VideoOutcome.Rejected, refusal);
        if (media is null) return VideoResult.Fail(VideoOutcome.Unavailable, "Локальные модели недоступны на этом сервере");
        var (run, error) = Compose(req);
        if (run is null) return VideoResult.Fail(VideoOutcome.Rejected, error!);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(Ceiling);
        var token = timeout.Token;

        string? ticket = null;
        try
        {
            var submitted = await media.SubmitAsync(run, token);
            if (submitted.Ticket is null)
                return VideoResult.Fail(submitted.Busy ? VideoOutcome.Unavailable : VideoOutcome.Failed,
                    submitted.Error ?? "Локальная видеокарта не приняла задачу");
            ticket = submitted.Ticket;
            progress.Report(new VideoProgress(VideoStage.Queued, submitted.QueuePosition, submitted.EtaSeconds,
                RemoteId: ticket, Accepted: true));

            var done = await WaitAsync(ticket, submitted.EtaSeconds, progress, token);
            ticket = null;
            if (done.State == LocalVideoState.Failed)
                return new VideoResult(VideoOutcome.Failed, null, Free, false, submitted.Ticket,
                    "Локальная модель не справилась: " + (done.Error ?? "ComfyUI завершил задачу ошибкой"));
            if (done.File is not { } file)
                return new VideoResult(VideoOutcome.Failed, null, Free, false, submitted.Ticket, "Локальная модель не вернула ролика");
            progress.Report(new VideoProgress(VideoStage.Downloading, RemoteId: submitted.Ticket));
            return new VideoResult(VideoOutcome.Ok,
                new VideoFile(file.Bytes, file.ContentType, file.Extension, HasSound: file.HasSound),
                Free, false, submitted.Ticket, null);
        }
        catch (OperationCanceledException)
        {
            // Задачу снимаем и с очереди, и с идущего прогона: чужое время GPU она не займёт
            if (ticket is not null) await media.CancelAsync(ticket, CancellationToken.None);
            if (ct.IsCancellationRequested) throw;
            return new VideoResult(VideoOutcome.Failed, null, Free, false, ticket,
                $"Локальная видеокарта не успела за {Ceiling.TotalMinutes:0} минут — очередь занята");
        }
    }

    private async Task<LocalVideoPoll> WaitAsync(string ticket, int? eta, IProgress<VideoProgress> progress, CancellationToken ct)
    {
        var stage = VideoStage.Queued;
        int? position = null;
        while (true)
        {
            var poll = await media!.PollAsync(ticket, ct);
            if (poll.State is LocalVideoState.Completed or LocalVideoState.Failed) return poll;
            var next = poll.State == LocalVideoState.Running ? VideoStage.Running : VideoStage.Queued;
            if (poll.Warning is null && (next != stage || poll.QueuePosition != position))
            {
                stage = next;
                position = poll.QueuePosition;
                progress.Report(new VideoProgress(stage, stage == VideoStage.Queued ? position : null, eta, RemoteId: ticket));
            }
            await Task.Delay(PollInterval, ct);
        }
    }

    public Task<bool> CancelRemoteAsync(string remoteId, CancellationToken ct) =>
        media is null ? Task.FromResult(false) : media.CancelAsync(remoteId, ct);

    // Запрос шва из запроса драйвера: первый кадр обязателен (H3 без кадра — другой шаблон, шов его не
    // даёт), size и fast — из Params. Белый список размеров и потолок длины проверяет адаптер шва
    internal static (LocalVideoRequest? Run, string? Error) Compose(VideoRequest req)
    {
        if (!string.Equals(req.Model, MiniMaxH3, StringComparison.OrdinalIgnoreCase))
            return (null, "Локальные модели так не умеют");
        if (req.FrameA is not { } first) return (null, "Локальной модели нужен первый кадр");
        if (req.DurationSec is < 1 or > MaxSeconds) return (null, $"Длительность — от 1 до {MaxSeconds} секунд");

        string size = "full";
        bool? fast = null;
        if (req.Params is { } p)
        {
            if (p.FirstOrDefault(kv => !H3Params.Contains(kv.Key)) is { Key: { } unknown })
                return (null, $"Неизвестный параметр {unknown}");
            if (p["size"] is { } s)
            {
                if (s.GetValueKind() != JsonValueKind.String) return (null, "size — full или half");
                size = s.GetValue<string>();
            }
            if (p["fast"] is { } f)
            {
                if (f.GetValueKind() is not (JsonValueKind.True or JsonValueKind.False)) return (null, "fast — true или false");
                fast = f.GetValue<bool>();
            }
        }
        return (new LocalVideoRequest(req.Text, first.Bytes, req.FrameB?.Bytes, req.DurationSec, size, fast, req.Seed), null);
    }

    private static readonly VideoCost Free = new(0, VideoPriceUnits.Free);
}
