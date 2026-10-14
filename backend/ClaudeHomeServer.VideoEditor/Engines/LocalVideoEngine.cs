using System.Text.Json;
using System.Text.Json.Nodes;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.Media;
using ClaudeHomeServer.Services.VideoEditor.Catalog;
using ClaudeHomeServer.Services.VideoEditor.Contracts;

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

    // Частные параметры под именами local_image_to_video: fast — ускоренный режим. size (full | half) теперь задаёт
    // ступень формата (ADR-025): из Params он не принимается
    private static readonly IReadOnlySet<string> H3Params = new HashSet<string>(StringComparer.Ordinal) { "fast" };

    // Решение Андрея по Б2, ЖДЁТ ПОДТВЕРЖДЕНИЯ: увеличение «Макс» не удалось (или отменено) — съёмка 768p остаётся
    // отдельной версией с пометкой UpscaleError. false — запуск падает ошибкой и 768p не сохраняется.
    // Единственное место переключения — эта константа (тесты меняют свойство экземпляра)
    internal const bool DefaultKeepShotWhenUpscaleFails = true;

    internal bool KeepShotWhenUpscaleFails { get; set; } = DefaultKeepShotWhenUpscaleFails;

    public const string UpscaleTarget = "1440p";

    // Тикеты апскейла: по ним CancelRemoteAsync отличает «Отменить увеличение» от остановки всего запуска
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> _upscaleTickets = new();

    // Пропорция ролика H3 следует за первым кадром: портретный кадр — портретный ролик
    private static readonly IReadOnlyList<VideoModelInfo> LocalModels =
    [
        new(MiniMaxH3, "MiniMax H3",
            new VideoCaps([.. Enumerable.Range(1, MaxSeconds)], ["16:9", "9:16"], Sound: true, LastFrame: true,
                VideoLicense.NotStated, VideoPriceUnits.Free, FirstFrame: true, MaxFrameBytes: MaxFrameBytes,
                Format: VideoFormatCatalog.LocalH3(["16:9", "9:16"]))),
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
        media is null || ScopeRefusal(request.Scope) is not null ? null : TotalEta(request);

    public async Task<VideoEstimate> EstimateAsync(VideoModelInfo model, VideoRequest request, CancellationToken ct)
    {
        if (ScopeRefusal(request.Scope) is { } refusal) throw new VideoEngineUnavailableException(refusal);
        if (media is null || !media.Available) throw new VideoEngineUnavailableException("Локальные модели сейчас недоступны");
        var queue = await media.QueueLengthAsync(ct)
            ?? throw new VideoEngineUnavailableException("Локальная видеокарта не отвечает (ComfyUI недоступен)");
        var upscale = WantsUpscale(request);
        var shot = ShotEta(request);
        var up = upscale ? UpscaleEta(request) : null;
        // «Макс»: время = съёмка + апскейл; одной из частей нет в таблице замеров — числа не выдумываем
        var total = upscale ? (shot is { } a && up is { } b ? a + b : null) : shot;
        return new VideoEstimate(0, VideoPriceUnits.Free, false, VideoEstimateSources.Provider, total, queue,
            UpscaleEtaSeconds: up, PostStep: upscale ? FormatPostSteps.Upscale : null, ShotEtaSeconds: upscale ? shot : null);
    }

    private static bool WantsUpscale(VideoRequest req) => req.Format?.PostStep == FormatPostSteps.Upscale;

    // Запрос шва БЕЗ кадров: время считается по размеру, длине и режиму, кадры для него не нужны
    private static LocalVideoRequest Shape(VideoRequest req)
    {
        var fast = req.Params?["fast"] is { } f && (f.GetValueKind() is JsonValueKind.True or JsonValueKind.False) ? f.GetValue<bool>() : (bool?)null;
        return new LocalVideoRequest(req.Text, [], null, req.DurationSec, SizeOf(req), fast, req.Seed);
    }

    private int? ShotEta(VideoRequest req) => media?.EtaSeconds(Shape(req));
    private int? UpscaleEta(VideoRequest req) => media?.UpscaleEtaSeconds(Shape(req));
    private int? TotalEta(VideoRequest req) =>
        WantsUpscale(req) ? (ShotEta(req) is { } a && UpscaleEta(req) is { } b ? a + b : null) : ShotEta(req);

    // Размер съёмки — ступень формата (half | full, «Макс» снимается full); формата нет — full, как раньше
    private static string SizeOf(VideoRequest req) => req.Format?.WireValue is "half" or "full" ? req.Format.WireValue : "full";

    // ── Повтор увеличения ────────────────────────────────────────────────────────

    public string? UpscaleRetryRefusal(string shotTicket) =>
        media?.CanUpscale(shotTicket) == true ? null : VideoUpscaleRetryTexts.Gone;

    public async Task<VideoResult> RetryUpscaleAsync(UpscaleRetryRequest request, IProgress<VideoProgress> progress, CancellationToken ct)
    {
        if (media is null) return VideoResult.Fail(VideoOutcome.Unavailable, "Локальные модели недоступны на этом сервере");
        if (UpscaleRetryRefusal(request.ShotTicket) is { } refusal) return VideoResult.Fail(VideoOutcome.Rejected, refusal);
        return await UpscaleAsync(request.Format, request.ShotTicket, null, request.UpscaleEtaSeconds, progress, ct);
    }

    // ── Запуск ───────────────────────────────────────────────────────────────────

    public async Task<VideoResult> RunAsync(VideoRequest req, IProgress<VideoProgress> progress, CancellationToken ct)
    {
        if (ScopeRefusal(req.Scope) is { } refusal) return VideoResult.Fail(VideoOutcome.Rejected, refusal);
        if (media is null) return VideoResult.Fail(VideoOutcome.Unavailable, "Локальные модели недоступны на этом сервере");
        var (run, error) = Compose(req);
        if (run is null) return VideoResult.Fail(VideoOutcome.Rejected, error!);
        var upscale = WantsUpscale(req);
        var upscaleEta = upscale ? media.UpscaleEtaSeconds(run) : null;

        string? ticket = null;
        try
        {
            // Потолок — на каждую стадию отдельно: апскейл — вторая тяжёлая задача со своим ожиданием
            using var shotTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            shotTimeout.CancelAfter(Ceiling);
            var submitted = await media.SubmitAsync(run, shotTimeout.Token);
            if (submitted.Ticket is null)
                return VideoResult.Fail(submitted.Busy ? VideoOutcome.Unavailable : VideoOutcome.Failed,
                    submitted.Error ?? "Локальная видеокарта не приняла задачу");
            ticket = submitted.Ticket;
            progress.Report(new VideoProgress(VideoStage.Queued, submitted.QueuePosition, submitted.EtaSeconds,
                RemoteId: ticket, Accepted: true, UpscaleEtaSeconds: upscaleEta));

            var done = await WaitAsync(ticket, submitted.EtaSeconds, VideoStage.Running, upscaleEta, progress, shotTimeout.Token);
            ticket = null;
            if (done.State == LocalVideoState.Failed)
                return new VideoResult(VideoOutcome.Failed, null, Free, false, submitted.Ticket,
                    "Локальная модель не справилась: " + (done.Error ?? "ComfyUI завершил задачу ошибкой"));
            if (done.File is not { } file)
                return new VideoResult(VideoOutcome.Failed, null, Free, false, submitted.Ticket, "Локальная модель не вернула ролика");

            var shotFile = new VideoFile(file.Bytes, file.ContentType, file.Extension, HasSound: file.HasSound,
                Width: req.Format?.Width, Height: req.Format?.Height);
            if (!upscale)
            {
                progress.Report(new VideoProgress(VideoStage.Downloading, RemoteId: submitted.Ticket));
                return new VideoResult(VideoOutcome.Ok, shotFile, Free, false, submitted.Ticket, null);
            }
            // «Макс»: в карточке съёмка получает размер своей ступени, итог — после апскейла
            shotFile = shotFile with { Width = ShotWidth(req), Height = ShotHeight(req) };
            return await UpscaleAsync(req.Format, submitted.Ticket, shotFile, upscaleEta, progress, ct);
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

    // Размер съёмки «Макс» — ступень «Высокое» (768p, full): пропорции те же, что у формата
    private static int? ShotWidth(VideoRequest req) => ShotSize(req)?.W;
    private static int? ShotHeight(VideoRequest req) => ShotSize(req)?.H;

    private static FormatSize? ShotSize(VideoRequest req) =>
        LocalModels[0].Caps.Format?.Tiers.FirstOrDefault(t => t.Tier == FormatTier.High) is { } high
        && req.Format?.Aspect is { } aspect && high.Sizes.TryGetValue(aspect, out var size)
            ? size
            : null;

    // Вторая стадия «Макс»: латентный апскейл завершённой съёмки. Любой сбой или отмена САМОГО увеличения (не запуска)
    // оставляют съёмку 768p с пометкой UpscaleError, пока KeepShotWhenUpscaleFails; 768p вместо 1440p молча не подставляется
    // shot == null — повтор увеличения: съёмка уже сохранена версией, сбой и отмена версий не добавляют
    private async Task<VideoResult> UpscaleAsync(ResolvedFormat? format, string shotTicket, VideoFile? shot, int? upscaleEta,
        IProgress<VideoProgress> progress, CancellationToken ct)
    {
        string? upTicket = null;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(Ceiling);
            var submitted = await media!.SubmitUpscaleAsync(shotTicket, UpscaleTarget, timeout.Token);
            if (submitted.Ticket is null)
                return UpscaleFailed(shot, shotTicket, submitted.Error ?? "Локальная видеокарта не приняла увеличение");
            upTicket = submitted.Ticket;
            _upscaleTickets[upTicket] = false;
            progress.Report(new VideoProgress(VideoStage.Upscaling, submitted.QueuePosition, submitted.EtaSeconds ?? upscaleEta,
                RemoteId: upTicket));

            var done = await WaitAsync(upTicket, submitted.EtaSeconds ?? upscaleEta, VideoStage.Upscaling, null, progress, timeout.Token);
            var cancelledByUser = _upscaleTickets.TryGetValue(upTicket, out var flag) && flag;
            var finished = upTicket;
            upTicket = null;
            // Тикет снимается и при сбое, и при отмене человеком: иначе запись о нём остаётся в словаре навсегда
            _upscaleTickets.TryRemove(finished, out _);
            if (done.State == LocalVideoState.Failed || done.File is null)
                return UpscaleFailed(shot, shotTicket,
                    cancelledByUser ? "Увеличение отменено" : done.Error ?? "Локальная модель не вернула увеличенного ролика",
                    cancelled: cancelledByUser);
            progress.Report(new VideoProgress(VideoStage.Downloading, RemoteId: finished));
            var size = format is { Width: { } w, Height: { } h } ? (w, h) : ((int, int)?)null;
            return new VideoResult(VideoOutcome.Ok,
                new VideoFile(done.File.Bytes, done.File.ContentType, done.File.Extension, HasSound: done.File.HasSound,
                    Width: size?.Item1, Height: size?.Item2),
                Free, false, finished, null);
        }
        catch (OperationCanceledException)
        {
            // Отмена запуска целиком или потолок: снимаем тикет апскейла, как у съёмки
            if (upTicket is not null) await media!.CancelAsync(upTicket, CancellationToken.None);
            if (ct.IsCancellationRequested) throw;
            return UpscaleFailed(shot, shotTicket, $"Увеличение не уложилось в {Ceiling.TotalMinutes:0} минут");
        }
        finally
        {
            if (upTicket is not null) _upscaleTickets.TryRemove(upTicket, out _);
        }
    }

    private VideoResult UpscaleFailed(VideoFile? shot, string shotTicket, string reason, bool cancelled = false)
    {
        var text = cancelled ? reason : "Увеличение до 1440p не удалось: " + reason;
        if (shot is null)
            return VideoResult.Fail(cancelled ? VideoOutcome.Cancelled : VideoOutcome.Failed, text);
        return KeepShotWhenUpscaleFails
            ? new VideoResult(VideoOutcome.Ok, shot with { UpscaleError = text, UpscaleCancelled = cancelled ? true : null }, Free, false, shotTicket, null)
            : new VideoResult(VideoOutcome.Failed, null, Free, false, shotTicket, text + ". Съёмка 768p не сохранена");
    }

    private async Task<LocalVideoPoll> WaitAsync(string ticket, int? eta, VideoStage runningStage, int? upscaleEta,
        IProgress<VideoProgress> progress, CancellationToken ct)
    {
        var stage = VideoStage.Queued;
        int? position = null;
        double? percent = null;
        // Снимок очереди GPU (шаг 30): живой, меняется каждым опросом — обновляем и вешаем на отчёт
        LocalGpuQueueInfo? gpu = null;
        while (true)
        {
            var poll = await media!.PollAsync(ticket, ct);
            if (poll.State is LocalVideoState.Completed or LocalVideoState.Failed) return poll;
            var next = poll.State == LocalVideoState.Running ? runningStage : VideoStage.Queued;
            // Процент шлём, пока идёт прогон, и только при заметном сдвиге (1 %): опрос чаще, чем меняется шаг
            var nextPercent = poll.State == LocalVideoState.Running ? Percent(poll.Percent) : null;
            gpu = poll.Gpu;
            if (poll.Warning is null && (next != stage || poll.QueuePosition != position || nextPercent != percent))
            {
                stage = next;
                position = poll.QueuePosition;
                percent = nextPercent;
                // У апскейла в очереди стадия остаётся upscaling: «следом» человек уже прочёл, дальше — вторая задача
                var reported = runningStage == VideoStage.Upscaling && next == VideoStage.Queued ? VideoStage.Upscaling : next;
                progress.Report(new VideoProgress(reported, next == VideoStage.Queued ? position : null, eta, RemoteId: ticket,
                    Percent: percent, UpscaleEtaSeconds: next == VideoStage.Running ? null : upscaleEta, Gpu: gpu));
            }
            await Task.Delay(PollInterval, ct);
        }
    }

    private static double? Percent(double? raw) =>
        raw is { } v && double.IsFinite(v) ? Math.Round(Math.Clamp(v, 0, 1), 2) : null;

    public Task<bool> CancelRemoteAsync(string remoteId, CancellationToken ct)
    {
        // «Отменить увеличение» приходит сюда с тикетом апскейла: помечаем, чтобы запуск сохранил съёмку как «отменено»
        if (_upscaleTickets.ContainsKey(remoteId)) _upscaleTickets[remoteId] = true;
        return media is null ? Task.FromResult(false) : media.CancelAsync(remoteId, ct);
    }

    // Запрос шва из запроса драйвера: первый кадр обязателен (H3 без кадра — другой шаблон, шов его не
    // даёт), size и fast — из Params. Белый список размеров и потолок длины проверяет адаптер шва
    internal static (LocalVideoRequest? Run, string? Error) Compose(VideoRequest req)
    {
        if (!string.Equals(req.Model, MiniMaxH3, StringComparison.OrdinalIgnoreCase))
            return (null, "Локальные модели так не умеют");
        if (req.FrameA is not { } first) return (null, "Локальной модели нужен первый кадр");
        if (req.DurationSec is < 1 or > MaxSeconds) return (null, $"Длительность — от 1 до {MaxSeconds} секунд");

        // Размер задаёт ступень формата (half | full; «Макс» снимается full и увеличивается вторым прогоном)
        var size = SizeOf(req);
        bool? fast = null;
        if (req.Params is { } p)
        {
            if (p.FirstOrDefault(kv => !H3Params.Contains(kv.Key)) is { Key: { } unknown })
                return (null, unknown == "size" ? "Размер задаётся форматом (quality), а не параметром size" : $"Неизвестный параметр {unknown}");
            if (p["fast"] is { } f)
            {
                if (f.GetValueKind() is not (JsonValueKind.True or JsonValueKind.False)) return (null, "fast — true или false");
                fast = f.GetValue<bool>();
            }
        }
        var run = new LocalVideoRequest(req.Text, first.Bytes, req.FrameB?.Bytes, req.DurationSec, size, fast, req.Seed);
        // Метка происхождения прогона (шаг 30): владелец, чат/сцена, вид=видео, операция, тяжёлый ли.
        // «видео 1440p» — из операции и ступени задачи (PostStep=upscale), размер не пересчитываем;
        // данные у движка есть из задачи (VideoEditJobService кладёт их в запрос); без задачи (тесты) — null
        if (req.OwnerId is { } owner)
            run = run with { Origin = new LocalJobOrigin(owner, req.SessionId, "video", OpName(req), req.Heavy) };
        return (run, null);
    }

    // Операция для метки происхождения (шаг 30): «видео» для съёмки, «видео 1440p» для «Макс» (PostStep=upscale)
    private static string OpName(VideoRequest req) => WantsUpscale(req) ? "видео 1440p" : "видео";

    private static readonly VideoCost Free = new(0, VideoPriceUnits.Free);
}
