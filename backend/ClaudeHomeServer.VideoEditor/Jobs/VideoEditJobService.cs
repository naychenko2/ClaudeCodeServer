using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json.Nodes;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services.ChatContext;
using ClaudeHomeServer.Services.Composition;
using ClaudeHomeServer.Services.Media;
using ClaudeHomeServer.Services.Spend;
using ClaudeHomeServer.Services.VideoEditor.Catalog;
using ClaudeHomeServer.Services.VideoEditor.ChatContext;
using ClaudeHomeServer.Services.VideoEditor.Contracts;
using ClaudeHomeServer.Services.VideoEditor.Prefs;
using ClaudeHomeServer.Services.VideoEditor.Scenes;

namespace ClaudeHomeServer.Services.VideoEditor.Jobs;

// Исполнитель съёмки (ADR-022, образец — AudioEditJobService): котировка → запуск строго по quoteId → события
// video_edit_* в группу владельца → итог версиями сцены. Реестр — в памяти; запуски, оборванные
// перезапуском, снимает VideoThreadRecovery.
//
// Инварианты:
// - запуск только по котировке и ровно на её паре «поставщик + модель». Поставщик отказал или пропал —
//   отказ и, если есть сосед, его котировка (RetryQuote). Сам исполнитель на соседа не переходит никогда;
// - потолки: MaxJobsPerOwner на владельца, MaxJobsPerInstance на инстанс, у local — одна тяжёлая съёмка
//   за раз (GPU). Отказ — текст, не исключение;
// - трата пишется в общий учёт (ISpendCollector) на запустившего в момент принятия задачи поставщиком —
//   всегда, у local с нулём; в своей валюте (доллары fal, кредиты Higgsfield не складываются); с Initiator.
//   Поставщик честно сказал «не списано» — компенсирующая запись с минусом; остановленное ДО принятия
//   не списывается вовсе;
// - лицензия модели фиксируется в котировке и переходит в запуск и версии: каталог может поменяться;
// - params проверяются по ParamNames драйвера и в котировке, и в запуске: неизвестный ключ — отказ
//   invalid_request с именем поля, до денег и до очереди;
// - запуск обязан прийти с тем же, от чего зависит цена котировки (длительность, число вариантов);
// - чужая задача и чужая котировка неотличимы от несуществующих.
public sealed class VideoEditJobService : IDisposable
{
    public const int MaxJobsPerOwner = 2;
    public const int MaxJobsPerInstance = 4;
    public const int DefaultDurationSec = 5;
    public static readonly TimeSpan QuoteTtl = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan CancelWait = TimeSpan.FromSeconds(10);

    public const string OwnerLimitText = "Уже идут две съёмки — дождитесь окончания одной из них";
    public const string InstanceLimitText = "Сервер занят: идут четыре съёмки — попробуйте через минуту";
    public const string HeavyBusyText =
        "На локальной видеокарте уже идёт съёмка — дождитесь её окончания или выберите облачного поставщика";
    public const string QuoteExpiredText = "Котировка устарела — запросите цену заново";
    public const string QuoteMismatchText = "Котировка не соответствует запросу — запросите цену заново";

    private readonly IEnumerable<IVideoEngine> _engines;
    private readonly VideoEditWorkspace _workspace;
    private readonly VideoJobThreads _threads;
    private readonly VideoPrefsService? _prefs;
    private readonly VideoFrameReader _frames;
    private readonly VideoContextLaunch? _context;
    private readonly ISpendCollector? _spend;
    private readonly ISessionBroadcaster? _broadcaster;
    private readonly IFeatureFlagGate? _flags;
    private readonly ILogger<VideoEditJobService> _log;
    private readonly TimeProvider _time;
    private readonly ConcurrentDictionary<string, Quote> _quotes = new();
    private readonly ConcurrentDictionary<string, Job> _jobs = new();
    // Контекст «Повторить увеличение»: (задача, вариант) версии с upscaleError → тикет съёмки у драйвера и исходная котировка.
    // Только в памяти и не дольше ILocalVideoMedia.ShotTtl — столько же живёт латент у адаптера; рестарт стирает и то, и другое
    private readonly ConcurrentDictionary<string, UpscaleRetryContext> _retryContexts = new();
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Lock _startLock = new();
    private int _disposed;

    public VideoEditJobService(
        IEnumerable<IVideoEngine> engines,
        VideoEditWorkspace workspace,
        VideoJobThreads threads,
        ILogger<VideoEditJobService> log,
        VideoPrefsService? prefs = null,
        ISpendCollector? spend = null,
        ISessionBroadcaster? broadcaster = null,
        TimeProvider? time = null,
        IFeatureFlagGate? flags = null,
        VideoFrameReader? frames = null,
        VideoContextLaunch? context = null)
    {
        _context = context;
        _engines = engines;
        _workspace = workspace;
        _threads = threads;
        _log = log;
        _prefs = prefs;
        _spend = spend;
        _broadcaster = broadcaster;
        _time = time ?? TimeProvider.System;
        _flags = flags;
        _frames = frames ?? new VideoFrameReader(null, workspace);
    }

    // «Авто» сначала пробует локальные модели — по флагу владельца local-media-default. Явно
    // выбранного поставщика флаг не трогает никогда
    public bool PrefersLocal(string ownerId) =>
        _flags?.IsEnabled(ownerId, FeatureFlagKeys.LocalMediaDefault) == true;

    public IEnumerable<IVideoEngine> Engines => _engines;

    private sealed record Quote(
        string Id, string OwnerId, string ScopeKey, string? SessionId, string? SceneId, string Provider, VideoModelInfo Model,
        int Count, int DurationSec, string? Aspect, bool Sound, VideoPriceDto Price, DateTime ExpiresAt,
        long? ContextRevision = null, ResolvedFormat? Format = null, MediaFormat? Wish = null,
        // Желание пришло из поля «Формат» (запрос, сцена, префы), а не собрано из прежнего поля aspect: только оно подрезает кадры
        bool FormatExplicit = false)
    {
        public bool Heavy => Model.Caps.Heavy;
        public string License => Model.Caps.License.Label;
    }

    private sealed record UpscaleRetryContext(string ShotTicket, Quote Quote, DateTime At);

    private static string RetryKey(string jobId, int variant) => $"{jobId}:{variant}";

    private sealed class Job(string id, string ownerId, VideoEditScope scope, Quote quote, CancellationTokenSource cts)
    {
        public readonly Lock Gate = new();
        public string Id { get; } = id;
        public string OwnerId { get; } = ownerId;
        public VideoEditScope Scope { get; } = scope;
        public Quote Quote { get; } = quote;
        public CancellationTokenSource Cts { get; } = cts;
        public DateTime CreatedAt { get; init; }
        public string? ChatSessionId { get; set; }
        public string? SceneId { get; set; }
        public string Initiator { get; init; } = VideoInitiators.Human;
        public string SpendLabel { get; init; } = "";
        public string SpendSource { get; init; } = "";
        // «Повторить увеличение»: вторая стадия уже снятой версии; денег и «соседнего поставщика» у неё нет
        public bool UpscaleRetry { get; init; }
        public VideoInputsSnapshotDto Inputs { get; set; } = new("", null, null);
        public VideoEditJobStatus Status { get; set; } = VideoEditJobStatus.Queued;
        public int Variant { get; set; } = 1;
        public int? QueuePosition { get; set; }
        public int? EtaSeconds { get; set; }
        // Время апскейла «Макс» (пока идёт съёмка) и тикет текущей задачи GPU: по нему отменяется только увеличение
        public int? UpscaleEtaSeconds { get; set; }
        public string? RemoteId { get; set; }
        public List<VideoVariantResult> Variants { get; } = [];
        public VideoCostDto? Cost { get; set; }
        public VideoOutcome? Outcome { get; set; }
        public bool? Charged { get; set; }
        public string? Error { get; set; }
        public bool Accepted { get; set; }
        public bool SpendWritten { get; set; }
        public double? RecordedAmount { get; set; }
        public Task Completion { get; set; } = Task.CompletedTask;

        public bool IsActive => Status is VideoEditJobStatus.Queued or VideoEditJobStatus.Running or VideoEditJobStatus.Upscaling
            or VideoEditJobStatus.Downloading;
    }

    // ── Котировка ────────────────────────────────────────────────────────────────

    public async Task<VideoEditCallResult<VideoQuoteResponse>> QuoteAsync(
        string ownerId, VideoEditScope scope, VideoQuoteRequest request, CancellationToken ct)
    {
        // Ревизия контекста: сцена и кадры берутся из стора, одноимённые поля запроса игнорируются
        VideoContextInputs? fromContext = null;
        if (request.ContextRevision is { } revision)
        {
            if (_context is null)
                return Fail<VideoQuoteResponse>(VideoEditorErrors.ProviderUnavailable, VideoContextLaunch.UnavailableText);
            var read = _context.Read(ownerId, scope, request.SessionId, revision);
            if (!read.Ok) return read.Fail<VideoQuoteResponse>();
            fromContext = VideoContextLaunch.Extract(read.State!);
            if (fromContext.SceneId is null)
                return Fail<VideoQuoteResponse>(VideoEditorErrors.InvalidRequest, VideoContextLaunch.NotSceneText);
            if (fromContext.Problem is { } problem)
                return Fail<VideoQuoteResponse>(VideoEditorErrors.InvalidRequest, problem);
            request = request with { SceneId = fromContext.SceneId };
        }
        if (!_threads.OwnScene(ownerId, scope.Key, request.SessionId, request.SceneId))
            return Fail<VideoQuoteResponse>(VideoEditorErrors.SceneNotFound, "Сцена не найдена");
        var settings = _threads.Store.Get(ownerId, request.SessionId).Scenes.First(s => s.SceneId == request.SceneId).Settings;
        if (fromContext is not null)
        {
            // Кадры сцены при ревизии — только референсы контекста (frame-a/frame-b), не поля сцены
            settings = settings with { FrameA = fromContext.FrameA, FrameB = fromContext.FrameB };
            if (VideoSceneService.SettingsProblem(scope, settings) is { } bad)
                return Fail<VideoQuoteResponse>(bad.ErrorCode!, bad.Error!);
        }

        // Цепочка: явное в запросе → настройки сцены → префы области → умолчание каталога
        var prefs = _prefs?.Get(ownerId, scope) ?? VideoPrefsStore.Empty;
        var count = request.Count ?? settings.Count ?? prefs.Count ?? 1;
        if (count < 1 || count > VideoThreadStore.MaxCount)
            return Fail<VideoQuoteResponse>(VideoEditorErrors.InvalidRequest, $"Вариантов — от 1 до {VideoThreadStore.MaxCount}");
        var duration = request.DurationSec ?? settings.DurationSec ?? prefs.DurationSec ?? DefaultDurationSec;
        var legacyAspect = request.Aspect ?? settings.Aspect ?? prefs.Aspect;
        var sound = request.Sound ?? settings.Sound ?? prefs.Sound ?? false;
        var providerKey = request.Provider ?? settings.Provider ?? prefs.Provider;
        var modelId = request.Model ?? settings.Model ?? prefs.Model;
        var need = new VideoCatalog.Need(settings.FrameA is not null, settings.FrameB is not null, duration);

        IVideoEngine? engine;
        VideoModelInfo? model;
        if (!VideoCatalog.IsAuto(providerKey))
        {
            // Явный выбор не подменяем: лежит — отказ с причиной, а не тихий переход к соседу
            engine = VideoCatalog.Registered(_engines)
                .FirstOrDefault(e => string.Equals(e.Key, providerKey!.Trim(), StringComparison.OrdinalIgnoreCase));
            if (engine is null)
                return Fail<VideoQuoteResponse>(VideoEditorErrors.ProviderUnavailable, $"Поставщик «{providerKey}» не заведён на этом сервере");
            if (!SafeEnabled(engine))
                return Fail<VideoQuoteResponse>(VideoEditorErrors.ProviderUnavailable, $"Поставщик «{engine.Label}» сейчас недоступен");
            if (engine.ScopeRefusal(scope) is { } refusal)
                return Fail<VideoQuoteResponse>(ScopeRefusalCode(scope), refusal);
            await RefreshModelsQuietly(engine, ct);
            model = VideoCatalog.Pick(engine, modelId, need);
            if (model is null)
                return Fail<VideoQuoteResponse>(VideoEditorErrors.InvalidRequest, NoModelText(engine, need));
        }
        else
        {
            var candidates = VideoCatalog.AutoCandidates(_engines, scope, PrefersLocal(ownerId));
            foreach (var candidate in candidates) await RefreshModelsQuietly(candidate, ct);
            (engine, model) = candidates
                .Select(e => (Engine: e, Model: VideoCatalog.Pick(e, modelId, need)))
                .FirstOrDefault(p => p.Model is not null);
            if (engine is null || model is null)
                return Fail<VideoQuoteResponse>(VideoEditorErrors.ProviderUnavailable, "Сейчас нет доступного поставщика для этой сцены");
        }

        // Формат (ADR-025): желание запроса → сцены → префов; у старого клиента — прежнее поле aspect. Хранится желание,
        // действующий формат считается здесь, под выбранную модель; подмена показывается в ответе, а не пишется в сцену
        var (wish, explicitFormat) = request.Format is { } given
            ? (given, true)
            : WishOf(request.Aspect, settings.Format, settings.Aspect, prefs.Format, prefs.Aspect);
        if (VideoSceneFormat.Validate(wish) is { } badFormat)
            return Fail<VideoQuoteResponse>(VideoEditorErrors.InvalidRequest, badFormat);
        var resolved = Resolve(engine, model, wish);
        var aspect = resolved?.Aspect ?? (wish is null ? legacyAspect : null);
        if (wish is null && aspect is not null && model.Caps.Aspects.Count > 0 && !model.Caps.Aspects.Contains(aspect))
            return Fail<VideoQuoteResponse>(VideoEditorErrors.InvalidRequest,
                $"Модель «{model.Label}» снимает в пропорциях {string.Join(", ", model.Caps.Aspects)}, а не {aspect}");
        sound = sound && model.Caps.Sound;

        VideoPriceDto price;
        try
        {
            price = await EstimateAsync(engine, model, ProbeRequest(scope, model, settings.Text, duration, aspect, sound, resolved), count, ct);
        }
        catch (VideoEngineUnavailableException ex)
        {
            return Fail<VideoQuoteResponse>(VideoEditorErrors.ProviderUnavailable, ex.Message);
        }

        var quote = new Quote(NewId(), ownerId, scope.Key, request.SessionId, request.SceneId, engine.Key, model, count,
            duration, aspect, sound, price, Now() + QuoteTtl, request.ContextRevision, resolved, wish, explicitFormat);
        PruneQuotes();
        PruneRetryContexts();
        _quotes[quote.Id] = quote;
        return VideoEditCallResult<VideoQuoteResponse>.Ok(ToDto(quote));
    }

    private static VideoRequest ProbeRequest(VideoEditScope scope, VideoModelInfo model, string text, int duration,
        string? aspect, bool sound, ResolvedFormat? format) =>
        new(model.Id, scope, text, null, null, duration, aspect, sound, Format: format);

    // Желание по цепочке «запрос → сцена → префы»: Format главнее, прежнее поле aspect — только когда Format не задан
    // Второй элемент — желание явное (поле «Формат»): прежнее поле aspect желанием «Формата» не считается
    private (MediaFormat? Wish, bool Explicit) WishOf(string? requestAspect, MediaFormat? sceneFormat, string? sceneAspect,
        MediaFormat? prefsFormat, string? prefsAspect)
    {
        if (requestAspect is not null) return (AspectWish(requestAspect), false);
        if (sceneFormat is not null) return (sceneFormat, true);
        if (sceneAspect is not null) return (AspectWish(sceneAspect), false);
        if (prefsFormat is not null) return (prefsFormat, true);
        return prefsAspect is not null ? (AspectWish(prefsAspect), false) : (null, false);
    }

    private MediaFormat AspectWish(string aspect) =>
        new(FormatModes.Aspect, aspect, null, null, null, ContextActor.Human, Now());

    // Действующий формат под модель; без каталога формата («Как у модели») и без желания — null: поведение как раньше.
    // Ступень без названия — умолчание драйвера (DefaultTier): старый клиент пишет одно aspect
    private static ResolvedFormat? Resolve(IVideoEngine engine, VideoModelInfo model, MediaFormat? wish)
    {
        if (wish is null || model.Caps.Format is not { } caps) return null;
        if (wish.Mode == FormatModes.Aspect && wish.Tier is null)
            wish = wish with { Tier = VideoFormatCatalog.DefaultTier(engine.Key, model.Id) };
        var resolved = MediaFormatResolver.Resolve(caps, wish);
        return resolved.Mode == FormatModes.Model ? null : resolved;
    }

    // Котировка драйвера — на все варианты: каждый вариант — отдельный прогон
    private static async Task<VideoPriceDto> EstimateAsync(IVideoEngine engine, VideoModelInfo model, VideoRequest probe,
        int count, CancellationToken ct)
    {
        var estimate = engine is IVideoQuoter quoter
            ? await quoter.EstimateAsync(model, probe, ct)
            : FromHint(engine, model, probe);
        return new VideoPriceDto(estimate.Amount * count, estimate.Unit, estimate.Approx, estimate.Source,
            estimate.EtaSeconds * count, estimate.QueueLength, estimate.UpscaleEtaSeconds * count, estimate.PostStep, estimate.ShotEtaSeconds * count);
    }

    // Ориентир каталога: цена за секунду × длительность или цена за запуск; нет ориентира — суммы нет
    private static VideoEstimate FromHint(IVideoEngine engine, VideoModelInfo model, VideoRequest probe)
    {
        if (model.PriceHint is not { } hint)
            return new VideoEstimate(null, engine.PriceUnit, true, VideoEstimateSources.Unknown);
        double units = hint.Per == "sec" ? probe.DurationSec : 1;
        return new VideoEstimate(hint.Amount * units, hint.Unit, true, VideoEstimateSources.Catalog);
    }

    // ── Запуск ───────────────────────────────────────────────────────────────────

    public async Task<VideoEditCallResult<VideoJobCreated>> StartAsync(
        string ownerId, VideoEditScope scope, VideoLaunchRequest input, CancellationToken ct)
    {
        if (!_quotes.TryGetValue(input.QuoteId, out var quote)
            || quote.OwnerId != ownerId || quote.ScopeKey != scope.Key || quote.ExpiresAt < Now())
            return Fail<VideoJobCreated>(VideoEditorErrors.QuoteNotFound, QuoteExpiredText);
        // Ревизия контекста: совпасть и со стором, и с ревизией котировки (цена выписана на состав кадров);
        // котировка по ревизии без ревизии в запуске — тоже несовпадение
        VideoContextInputs? fromContext = null;
        if (input.ContextRevision is { } revision)
        {
            if (_context is null)
                return Fail<VideoJobCreated>(VideoEditorErrors.ProviderUnavailable, VideoContextLaunch.UnavailableText);
            var read = _context.Read(ownerId, scope, input.SessionId, revision);
            if (!read.Ok) return read.Fail<VideoJobCreated>();
            if (quote.ContextRevision != revision)
                return _context.Stale(ownerId, scope, input.SessionId).Fail<VideoJobCreated>();
            fromContext = VideoContextLaunch.Extract(read.State!);
            if (fromContext.SceneId is null)
                return Fail<VideoJobCreated>(VideoEditorErrors.InvalidRequest, VideoContextLaunch.NotSceneText);
            if (fromContext.Problem is { } problem)
                return Fail<VideoJobCreated>(VideoEditorErrors.InvalidRequest, problem);
            input = input with { SceneId = fromContext.SceneId };
        }
        else if (quote.ContextRevision is not null)
            return Fail<VideoJobCreated>(VideoEditorErrors.InvalidRequest, QuoteMismatchText);
        if (quote.SessionId != input.SessionId || quote.SceneId != input.SceneId)
            return Fail<VideoJobCreated>(VideoEditorErrors.InvalidRequest, QuoteMismatchText);
        if (!_threads.OwnScene(ownerId, scope.Key, input.SessionId, input.SceneId))
            return Fail<VideoJobCreated>(VideoEditorErrors.SceneNotFound, "Сцена не найдена");

        // Ровно поставщик котировки: пропал — отказ, а не сосед
        var engine = VideoCatalog.Available(_engines)
            .FirstOrDefault(e => string.Equals(e.Key, quote.Provider, StringComparison.OrdinalIgnoreCase));
        if (engine is null)
            return Fail<VideoJobCreated>(VideoEditorErrors.ProviderUnavailable, $"Поставщик «{quote.Provider}» больше недоступен");
        if (engine.ScopeRefusal(scope) is { } refusal)
            return Fail<VideoJobCreated>(ScopeRefusalCode(scope), refusal);

        var parameters = input.Params?.DeepClone().AsObject() ?? new JsonObject();
        // При ревизии текст поля ввода едет в params.request — просьба поверх текста сцены, не частный параметр модели
        string? ask = null;
        if (fromContext is not null)
        {
            if (parameters["request"] is JsonValue asked && asked.TryGetValue<string>(out var askedText)
                && !string.IsNullOrWhiteSpace(askedText))
                ask = askedText.Trim();
            parameters.Remove("request");
        }
        if (CheckParams(engine, quote.Model, parameters) is { } badParams)
            return Fail<VideoJobCreated>(VideoEditorErrors.InvalidRequest, badParams);

        var scene = _threads.Store.Get(ownerId, input.SessionId).Scenes.First(s => s.SceneId == input.SceneId);
        var settings = scene.Settings;
        if (fromContext is not null)
        {
            // Кадры при ревизии — референсы контекста; в сцену они ложатся запуском (OnLaunchedAsync пишет настройки)
            settings = settings with { FrameA = fromContext.FrameA, FrameB = fromContext.FrameB };
            if (VideoSceneService.SettingsProblem(scope, settings) is { } bad)
                return Fail<VideoJobCreated>(bad.ErrorCode!, bad.Error!);
        }
        var prompt = string.IsNullOrWhiteSpace(ask) ? settings.Text
            : string.IsNullOrWhiteSpace(settings.Text) ? ask : settings.Text.TrimEnd() + "\n\n" + ask;
        // Сцена могла поменять входы между котировкой и запуском: модель обязана по-прежнему подходить
        if (!VideoCatalog.Fits(quote.Model, new VideoCatalog.Need(settings.FrameA is not null, settings.FrameB is not null, quote.DurationSec)))
            return Fail<VideoJobCreated>(VideoEditorErrors.InvalidRequest, QuoteMismatchText);

        // Кадры читаются ДО слота и до денег: нет кадра — отказ без следа
        var frameA = await _frames.ReadAsync(ownerId, scope, settings.FrameA, ct);
        if (frameA.ErrorCode is not null) return Fail<VideoJobCreated>(frameA.ErrorCode, frameA.Error!);
        var frameB = await _frames.ReadAsync(ownerId, scope, settings.FrameB, ct);
        if (frameB.ErrorCode is not null) return Fail<VideoJobCreated>(frameB.ErrorCode, frameB.Error!);

        // Подрезка кадров под пропорции формата сцены — ДО модели, там же до слота и денег (ADR-025 §5): так local H3
        // получает 9:16 из 16:9-кадра, а превью (frames/preview) считает то же самое
        VideoFramesCroppedDto? framesCropped = null;
        if (quote.FormatExplicit && quote.Format?.Aspect is { } fitAspect && (frameA.Bytes is not null || frameB.Bytes is not null))
        {
            var anchor = settings.FrameFit?.Anchor ?? CropAnchors.Center;
            var fitA = frameA.Bytes is { } a ? _frames.Fit(a, fitAspect, anchor) : null;
            if (fitA is { ErrorCode: not null }) return Fail<VideoJobCreated>(fitA.ErrorCode, fitA.Error!);
            var fitB = frameB.Bytes is { } b ? _frames.Fit(b, fitAspect, anchor) : null;
            if (fitB is { ErrorCode: not null }) return Fail<VideoJobCreated>(fitB.ErrorCode, fitB.Error!);
            if (fitA?.SourceAspect is not null || fitB?.SourceAspect is not null)
                framesCropped = new VideoFramesCroppedDto(fitAspect, fitA?.SourceAspect, fitB?.SourceAspect, anchor);
            frameA = frameA with { Bytes = fitA?.Frame ?? frameA.Bytes };
            frameB = frameB with { Bytes = fitB?.Frame ?? frameB.Bytes };
        }

        var initiator = input.Initiator == VideoInitiators.Agent ? VideoInitiators.Agent : VideoInitiators.Human;
        Job job;
        lock (_startLock)
        {
            var active = _jobs.Values.Where(j => j.IsActive).ToList();
            if (active.Count(j => j.OwnerId == ownerId) >= MaxJobsPerOwner)
                return Fail<VideoJobCreated>(VideoEditorErrors.TooManyJobs, OwnerLimitText);
            if (active.Count >= MaxJobsPerInstance)
                return Fail<VideoJobCreated>(VideoEditorErrors.TooManyJobs, InstanceLimitText);
            if (IsLocalHeavy(quote) && active.Any(j => IsLocalHeavy(j.Quote)))
                return Fail<VideoJobCreated>(VideoEditorErrors.HeavyBusy, HeavyBusyText);
            if (!_quotes.TryRemove(quote.Id, out _))
                return Fail<VideoJobCreated>(VideoEditorErrors.QuoteNotFound, "Котировка уже использована");

            job = new Job(NewId(), ownerId, scope, quote, CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token))
            {
                CreatedAt = Now(),
                Initiator = initiator,
                SpendLabel = SpendLabelOf(quote),
                SpendSource = engine.SpendSource,
                ChatSessionId = input.SessionId,
                SceneId = input.SceneId,
                Inputs = VideoSignatures.Snapshot(settings),
            };
            _jobs[job.Id] = job;
        }

        // Запуск ложится в сцену до старта: итог задачи обязан застать идущий запуск
        // Желание формата сцены остаётся как есть (хранится желание, а не подмена под модель); Aspect — его зеркало, а у
        // сцены без желания — как раньше пропорции котировки
        var chosen = settings with
        {
            Provider = quote.Provider, Model = quote.Model.Id, DurationSec = quote.DurationSec,
            Aspect = settings.Format is null ? quote.Aspect : settings.Aspect,
            Sound = quote.Sound, Count = quote.Count,
        };
        await _threads.OnLaunchedAsync(ownerId, scope.Key, input.SessionId, input.SceneId, job.Id, ToDto(quote),
            prompt, initiator, chosen, ct, framesCropped);

        var request = new VideoRequest(quote.Model.Id, scope, prompt, frameA.Bytes, frameB.Bytes, quote.DurationSec,
            quote.Aspect, quote.Sound, parameters, input.Seed, quote.Format);
        job.Completion = Task.Run(() => RunAsync(job, engine, (variant, progress, token) =>
            engine.RunAsync(request.Seed is { } seed ? request with { Seed = seed + variant - 1 } : request, progress, token)));

        _workspace.Sweep(Now());
        return VideoEditCallResult<VideoJobCreated>.Ok(new VideoJobCreated(job.Id));
    }

    private delegate Task<VideoResult> VariantRunner(int variant, IProgress<VideoProgress> progress, CancellationToken ct);

    private async Task RunAsync(Job job, IVideoEngine engine, VariantRunner runner)
    {
        VideoResult? failure = null;
        var cancelled = false;
        var costs = new List<VideoCost?>();
        try
        {
            for (var variant = 1; variant <= job.Quote.Count; variant++)
            {
                lock (job.Gate) job.Variant = variant;
                VideoResult result;
                try
                {
                    result = await runner(variant, new JobProgress(this, job, variant), job.Cts.Token);
                }
                catch (OperationCanceledException) when (job.Cts.IsCancellationRequested)
                {
                    cancelled = true;
                    break;
                }
                catch (Exception ex)
                {
                    _log.LogWarning(ex, "Видео: драйвер {Provider} упал", engine.Key);
                    result = VideoResult.Fail(VideoOutcome.Failed, "Сбой поставщика: " + ex.Message, charged: null);
                }

                if (result.Outcome == VideoOutcome.Cancelled || job.Cts.IsCancellationRequested)
                {
                    cancelled = true;
                    if (result.RemoteId is { } remote) await CancelRemoteQuietly(engine, remote);
                    break;
                }
                if (result.Outcome != VideoOutcome.Ok || SaveVariant(job, variant, result) is not { } saved)
                {
                    failure = result.Outcome == VideoOutcome.Ok
                        ? result with { Outcome = VideoOutcome.Failed, Error = "Поставщик не вернул пригодного клипа" }
                        : result;
                    break;
                }
                if (result.File?.UpscaleError is not null && result.RemoteId is { } shotTicket)
                    _retryContexts[RetryKey(job.Id, variant)] = new UpscaleRetryContext(shotTicket, job.Quote, Now());
                OnAccepted(job);
                costs.Add(result.ActualCost);
                lock (job.Gate) job.Variants.Add(saved with { Cost = CostOf(job.Quote, result.ActualCost) });
            }

            int ready;
            lock (job.Gate) ready = job.Variants.Count;
            if (!cancelled && ready > 0)
                await CompleteAsync(job, costs, failure?.Error);
            else
                await FailAsync(job, engine,
                    cancelled ? VideoResult.Fail(VideoOutcome.Cancelled, "Остановлено по запросу", charged: null)
                        : failure ?? VideoResult.Fail(VideoOutcome.Failed, "Задача не дала результата"),
                    cancelled);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Видео: не удалось завершить задачу {JobId}", job.Id);
            lock (job.Gate)
            {
                job.Status = VideoEditJobStatus.Failed;
                job.Outcome = VideoOutcome.Failed;
                job.Error = "Не удалось сохранить результат";
            }
        }
    }

    // Клип варианта в рабочую папку; метаданные для версии. null — пустой клип
    private VideoVariantResult? SaveVariant(Job job, int variant, VideoResult result)
    {
        if (result.File is not { Bytes.Length: > 0 } file) return null;
        _workspace.SaveClip(job.OwnerId, job.Id, variant, file.Bytes, file.Extension);
        return new VideoVariantResult(variant, file.DurationSec ?? job.Quote.DurationSec, file.Bytes.LongLength,
            file.HasSound ?? job.Quote.Sound, null, file.Width, file.Height, file.UpscaleError, file.UpscaleCancelled);
    }

    private async Task CompleteAsync(Job job, IReadOnlyList<VideoCost?> costs, string? partialError)
    {
        // Фактическая сумма — только если её назвали все прогоны в одной единице; иначе котировка
        var cost = costs.Count > 0 && costs.All(c => c is not null && c.Unit == costs[0]!.Unit)
            ? new VideoCost(costs.Sum(c => c!.Amount), costs[0]!.Unit)
            : EstimateCost(job.Quote);
        RecordSpend(job, cost?.Amount);
        List<VideoVariantResult> variants;
        lock (job.Gate)
        {
            job.Cost = CostOf(job.Quote, cost);
            job.Outcome = VideoOutcome.Ok;
            job.Charged = true;
            job.Error = partialError;
            job.Status = VideoEditJobStatus.Completed;
            variants = [.. job.Variants];
        }
        // Версии ложатся в сцену до события: фронт, перечитав нити по completed, их застаёт
        if (job is { ChatSessionId: { } sessionId, SceneId: { } sceneId })
            await _threads.OnFinishedAsync(job.OwnerId, job.Scope.Key, sessionId, sceneId, job.Id,
                VideoEditJobStatus.Completed, variants, job.Inputs, partialError);
        await Broadcast(job.OwnerId, new VideoEditCompletedMessage(job.Id, job.Scope.Key, job.SceneId ?? "",
            [.. variants.Select(v => v.Variant)], job.Cost, partialError, job.Initiator) { SessionId = job.ChatSessionId ?? "" });
    }

    private async Task FailAsync(Job job, IVideoEngine engine, VideoResult result, bool cancelled)
    {
        bool? charged;
        bool written;
        double? recorded;
        lock (job.Gate)
        {
            charged = result.Charged ?? (job.Accepted ? null : false);
            written = job.SpendWritten;
            recorded = job.RecordedAmount;
        }
        if (charged == false && written)
            WriteSpend(job, -recorded, -job.Quote.Count);
        else if (charged == true)
            RecordSpend(job, (result.ActualCost ?? EstimateCost(job.Quote))?.Amount);

        var retry = cancelled || job.UpscaleRetry || result.Outcome == VideoOutcome.Rejected ? null : RetryQuote(job, engine);
        List<VideoVariantResult> variants;
        lock (job.Gate)
        {
            job.Status = cancelled ? VideoEditJobStatus.Cancelled : VideoEditJobStatus.Failed;
            job.Outcome = result.Outcome;
            job.Charged = charged;
            job.Error = result.Error;
            if (charged != false && job.RecordedAmount is { } amount)
                job.Cost = CostOf(job.Quote, new VideoCost(amount, job.Quote.Price.Unit));
            variants = [.. job.Variants];
        }
        // Отмена оставляет готовые варианты версиями
        if (job is { ChatSessionId: { } sessionId, SceneId: { } sceneId })
            await _threads.OnFinishedAsync(job.OwnerId, job.Scope.Key, sessionId, sceneId, job.Id,
                cancelled ? VideoEditJobStatus.Cancelled : VideoEditJobStatus.Failed, variants, job.Inputs, result.Error);
        await Broadcast(job.OwnerId, new VideoEditFailedMessage(job.Id, job.Scope.Key, job.SceneId ?? "", charged, result.Error,
            retry, job.Initiator) { SessionId = job.ChatSessionId ?? "" });
    }

    // Сосед — только предложение «Повторить через …»: другой доступный поставщик, работающий в этой
    // области, с моделью под ту же сцену. Не запускается
    private RetryQuote? RetryQuote(Job job, IVideoEngine failed)
    {
        var q = job.Quote;
        if (q.SessionId is null || q.SceneId is null) return null;
        var scene = _threads.Store.Get(job.OwnerId, q.SessionId).Scenes.FirstOrDefault(s => s.SceneId == q.SceneId);
        if (scene is null) return null;
        var need = new VideoCatalog.Need(scene.Settings.FrameA is not null, scene.Settings.FrameB is not null, q.DurationSec);
        foreach (var other in VideoCatalog.Available(_engines).Where(e => e.Key != failed.Key))
        {
            if (other.ScopeRefusal(job.Scope) is not null) continue;
            if (VideoCatalog.Pick(other, VideoCatalog.AutoModelId, need) is not { } model) continue;
            var estimate = FromHint(other, model, ProbeRequest(job.Scope, model, scene.Settings.Text, q.DurationSec, q.Aspect, q.Sound, null));
            var price = new VideoPriceDto(estimate.Amount * q.Count, estimate.Unit, estimate.Approx, estimate.Source, null, null);
            var retry = new Quote(NewId(), job.OwnerId, q.ScopeKey, q.SessionId, q.SceneId, other.Key, model, q.Count,
                q.DurationSec, q.Aspect, q.Sound && model.Caps.Sound, price, Now() + QuoteTtl);
            _quotes[retry.Id] = retry;
            return new RetryQuote(other.Key, model.Id, ToDto(retry), $"«{failed.Label}» не снял сцену");
        }
        return null;
    }

    private async Task CancelRemoteQuietly(IVideoEngine engine, string remoteId)
    {
        try { await engine.CancelRemoteAsync(remoteId, CancellationToken.None); }
        catch (Exception ex) { _log.LogDebug(ex, "Видео: отмена у поставщика {Provider} не прошла", engine.Key); }
    }

    // ── Трата ────────────────────────────────────────────────────────────────────

    // Приняли у поставщика — трата на запустившего по котировке, один раз на задачу
    private void OnAccepted(Job job)
    {
        lock (job.Gate)
        {
            if (job.Accepted) return;
            job.Accepted = true;
        }
        RecordSpend(job, job.Quote.Price.Amount);
    }

    // Первая запись — всегда, с числом запросов и суммой, если она известна; сумма, ставшая известной
    // позже, догоняет её отдельной записью без генераций
    private void RecordSpend(Job job, double? amount)
    {
        if (job.UpscaleRetry) return;
        int generations;
        lock (job.Gate)
        {
            job.Accepted = true;
            if (!job.SpendWritten)
            {
                job.SpendWritten = true;
                job.RecordedAmount = amount;
                generations = job.Quote.Count;
            }
            else if (job.RecordedAmount is null && amount is not null)
            {
                job.RecordedAmount = amount;
                generations = 0;
            }
            else return;
        }
        WriteSpend(job, amount, generations);
    }

    private void WriteSpend(Job job, double? amount, int generations)
    {
        if (_spend is null)
        {
            _log.LogWarning("Видео: учёт расхода выключен, трата задачи {JobId} не записана", job.Id);
            return;
        }
        // Валюты не складываются: кредиты — в своём поле, остальное (и бесплатное — нулём) — доллары
        var unit = job.Quote.Price.Unit;
        try
        {
            _spend.Record(new SpendRecord
            {
                Timestamp = Now(),
                OwnerId = job.OwnerId,
                ProjectId = VideoEditScope.ProjectIdOf(job.Scope.Key),
                SessionId = job.ChatSessionId,
                Initiator = job.Initiator == VideoInitiators.Agent ? SpendInitiators.Agent : SpendInitiators.Human,
                Provider = job.Quote.Provider,
                Model = job.Quote.Model.Id,
                Source = job.SpendSource,
                CostUsd = unit == VideoPriceUnits.Credits ? null : amount,
                CostCredits = unit == VideoPriceUnits.Credits ? amount : null,
                Generations = generations,
                Label = job.SpendLabel,
            });
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Видео: не удалось записать трату задачи {JobId}", job.Id);
        }
    }

    // «veo-3.1 · 8 с × 2»: модель, длительность и число вариантов — единица тарификации видео секунда
    private static string SpendLabelOf(Quote q) =>
        $"{q.Model.Id} · {q.DurationSec.ToString(CultureInfo.InvariantCulture)} с" + (q.Count > 1 ? $" × {q.Count}" : "");

    // Валюта цены версии: доллары, кредиты или local (бесплатно, 0)
    private static VideoCostDto CostOf(Quote q, VideoCost? cost)
    {
        var amount = cost?.Amount ?? q.Price.Amount ?? 0;
        var perVariant = cost is null ? amount / Math.Max(1, q.Count) : amount;
        return q.Price.Unit switch
        {
            VideoPriceUnits.Credits => new VideoCostDto("credits", perVariant),
            VideoPriceUnits.Free => new VideoCostDto("local", 0),
            _ => new VideoCostDto("usd", perVariant),
        };
    }

    // ── Чтение и отмена ──────────────────────────────────────────────────────────

    public VideoQuoteResponse? FindQuote(string ownerId, string scopeKey, string quoteId) =>
        _quotes.TryGetValue(quoteId, out var q) && q.OwnerId == ownerId && q.ScopeKey == scopeKey && q.ExpiresAt >= Now()
            ? ToDto(q)
            : null;

    public VideoJobDto? Get(string ownerId, string scopeKey, string jobId) =>
        Find(ownerId, scopeKey, jobId) is { } job ? ToDto(job) : null;

    // Отмена: токен задачи гасит прогон драйвера, а id у поставщика из результата отменяется
    // CancelRemoteAsync. Готовые варианты остаются версиями
    public async Task<VideoJobDto?> CancelAsync(string ownerId, string scopeKey, string jobId, CancellationToken ct)
    {
        if (Find(ownerId, scopeKey, jobId) is not { } job) return null;
        if (job.IsActive)
        {
            await job.Cts.CancelAsync();
            await Task.WhenAny(job.Completion, Task.Delay(CancelWait, ct));
        }
        return ToDto(job);
    }

    // dryRun подрезки кадров A/B (ADR-025 §5): тот же расчёт, что у съёмки, ничего не пишет и не тратит. Пропорции —
    // из запроса, иначе из желания сцены или формата проекта; без них подрезать нечего — отказ с подсказкой
    public async Task<VideoEditCallResult<VideoFramesPreviewDto>> PreviewFramesAsync(string ownerId, VideoEditScope scope,
        VideoFramesPreviewRequest request, CancellationToken ct)
    {
        if (!_threads.OwnScene(ownerId, scope.Key, request.SessionId, request.SceneId))
            return Fail<VideoFramesPreviewDto>(VideoEditorErrors.SceneNotFound, "Сцена не найдена");
        var settings = _threads.Store.Get(ownerId, request.SessionId).Scenes.First(s => s.SceneId == request.SceneId).Settings;
        var prefs = _prefs?.Get(ownerId, scope) ?? VideoPrefsStore.Empty;
        var aspect = request.Aspect ?? VideoSceneFormat.AspectOf(settings) ?? VideoSceneFormat.AspectOf(
            new VideoSceneSettingsDto(null, null, "", null, null, null, prefs.Aspect, null, null, prefs.Format));
        if (aspect is null)
            return Fail<VideoFramesPreviewDto>(VideoEditorErrors.InvalidRequest,
                "Пропорции сцены не заданы: выберите их в «Формате», тогда видно, как подрежутся кадры");
        var anchor = request.Anchor ?? settings.FrameFit?.Anchor ?? CropAnchors.Center;
        if (!CropAnchors.All.Contains(anchor))
            return Fail<VideoFramesPreviewDto>(VideoEditorErrors.InvalidRequest, $"Недопустимый якорь рамки: {anchor}");

        var previews = new List<VideoFramePreviewDto?>();
        foreach (var frame in new[] { settings.FrameA, settings.FrameB })
        {
            var read = await _frames.ReadAsync(ownerId, scope, frame, ct);
            if (read.ErrorCode is not null) return Fail<VideoFramesPreviewDto>(read.ErrorCode, read.Error!);
            if (read.Bytes is not { } bytes) { previews.Add(null); continue; }
            var (preview, code, error) = _frames.Preview(bytes, aspect, anchor);
            if (code is not null) return Fail<VideoFramesPreviewDto>(code, error!);
            previews.Add(preview);
        }
        return VideoEditCallResult<VideoFramesPreviewDto>.Ok(new VideoFramesPreviewDto(aspect, anchor, previews[0], previews[1]));
    }

    // «Отменить увеличение»: снимает только тикет апскейла «Макс» local H3; запуск при этом не отменяется, съёмка 768p
    // останется версией с пометкой «увеличение отменено» (пока включена KeepShotWhenUpscaleFails). Не на стадии upscaling — отказ
    public async Task<VideoEditCallResult<VideoJobDto>> CancelUpscaleAsync(string ownerId, string scopeKey, string jobId,
        CancellationToken ct)
    {
        if (Find(ownerId, scopeKey, jobId) is not { } job) return Fail<VideoJobDto>(VideoEditorErrors.JobNotFound, "Задача не найдена");
        string? remote;
        lock (job.Gate) remote = job.Status == VideoEditJobStatus.Upscaling ? job.RemoteId : null;
        if (remote is null) return Fail<VideoJobDto>(VideoEditorErrors.InvalidRequest, "Увеличение сейчас не идёт");
        var engine = VideoCatalog.Registered(_engines)
            .FirstOrDefault(e => string.Equals(e.Key, job.Quote.Provider, StringComparison.OrdinalIgnoreCase));
        if (engine is not null) await CancelRemoteQuietly(engine, remote);
        return VideoEditCallResult<VideoJobDto>.Ok(ToDto(job));
    }

    // «Повторить увеличение»: у версии «Макс» local H3 с upscaleError запускает ТОЛЬКО вторую стадию по живому тикету
    // съёмки — новой задачей, которую можно отменить как обычное увеличение. Удача — новая версия 1440p, версия 768p
    // остаётся как была (файлы не перезаписываются); сбой и отмена версий не добавляют. Тяжёлая задача GPU: тот же потолок
    // «одна на карту», что у съёмки, второго семафора нет. Денег и записи траты у повтора нет
    public async Task<VideoEditCallResult<VideoUpscaleRetryResult>> RetryUpscaleAsync(string ownerId, VideoEditScope scope,
        string sessionId, string sceneId, string versionId, string initiator, CancellationToken ct)
    {
        if (!_threads.OwnScene(ownerId, scope.Key, sessionId, sceneId))
            return Fail<VideoUpscaleRetryResult>(VideoEditorErrors.SceneNotFound, "Сцена не найдена");
        var scene = _threads.Store.Get(ownerId, sessionId).Scenes.First(s => s.SceneId == sceneId);
        if (scene.Versions.FirstOrDefault(v => v.VersionId == versionId) is not { } version)
            return Fail<VideoUpscaleRetryResult>(VideoEditorErrors.VersionNotFound, "Версия не найдена");
        if (version.UpscaleError is null)
            return Fail<VideoUpscaleRetryResult>(VideoEditorErrors.InvalidRequest, VideoUpscaleRetryTexts.NotFailed);

        var engine = VideoCatalog.Available(_engines)
            .FirstOrDefault(e => string.Equals(e.Key, version.Provider, StringComparison.OrdinalIgnoreCase));
        if (engine is null)
            return Fail<VideoUpscaleRetryResult>(VideoEditorErrors.ProviderUnavailable, $"Поставщик «{version.Provider}» больше недоступен");
        if (engine.ScopeRefusal(scope) is { } refusal)
            return Fail<VideoUpscaleRetryResult>(ScopeRefusalCode(scope), refusal);

        PruneRetryContexts();
        _retryContexts.TryGetValue(RetryKey(version.JobId, version.Variant), out var context);
        if (engine.UpscaleRetryRefusal(context?.ShotTicket ?? "") is { } gone)
            return Fail<VideoUpscaleRetryResult>(
                gone == VideoUpscaleRetryTexts.NoUpscale ? VideoEditorErrors.InvalidRequest : VideoEditorErrors.UpscaleRetryUnavailable, gone);
        if (context is null)
            return Fail<VideoUpscaleRetryResult>(VideoEditorErrors.UpscaleRetryUnavailable, VideoUpscaleRetryTexts.Gone);

        // Котировки у повтора нет: «котировка» только несёт модель, формат и лицензию исходного запуска
        var quote = context.Quote with
        {
            Id = NewId(), Count = 1, Price = context.Quote.Price with { Eta = null, UpscaleEta = null, ShotEta = null, PostStep = null },
            ExpiresAt = Now(),
        };
        Job job;
        lock (_startLock)
        {
            var active = _jobs.Values.Where(j => j.IsActive).ToList();
            if (active.Count(j => j.OwnerId == ownerId) >= MaxJobsPerOwner)
                return Fail<VideoUpscaleRetryResult>(VideoEditorErrors.TooManyJobs, OwnerLimitText);
            if (active.Count >= MaxJobsPerInstance)
                return Fail<VideoUpscaleRetryResult>(VideoEditorErrors.TooManyJobs, InstanceLimitText);
            if (IsLocalHeavy(quote) && active.Any(j => IsLocalHeavy(j.Quote)))
                return Fail<VideoUpscaleRetryResult>(VideoEditorErrors.HeavyBusy, HeavyBusyText);
            job = new Job(NewId(), ownerId, scope, quote, CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token))
            {
                CreatedAt = Now(),
                Initiator = initiator == VideoInitiators.Agent ? VideoInitiators.Agent : VideoInitiators.Human,
                SpendLabel = SpendLabelOf(quote),
                SpendSource = engine.SpendSource,
                ChatSessionId = sessionId,
                SceneId = sceneId,
                Inputs = version.Inputs,
                UpscaleRetry = true,
            };
            _jobs[job.Id] = job;
        }

        var current = _threads.Store.Get(ownerId, sessionId).Scenes.First(s => s.SceneId == sceneId);
        await _threads.OnLaunchedAsync(ownerId, scope.Key, sessionId, sceneId, job.Id, ToDto(quote),
            "Повтор увеличения до 1440p", job.Initiator, current.Settings, ct, current.FramesCropped);

        var request = new UpscaleRetryRequest(context.ShotTicket, quote.Format);
        job.Completion = Task.Run(() => RunAsync(job, engine, (_, progress, token) => engine.RetryUpscaleAsync(request, progress, token)));
        return VideoEditCallResult<VideoUpscaleRetryResult>.Ok(new VideoUpscaleRetryResult(job.Id, versionId));
    }

    private void PruneRetryContexts()
    {
        var now = Now();
        foreach (var (key, context) in _retryContexts)
            if (now - context.At > ILocalVideoMedia.ShotTtl) _retryContexts.TryRemove(key, out _);
    }

    // Ожидание конца задачи — для тестов и выключения
    internal Task WhenDone(string jobId) => _jobs.TryGetValue(jobId, out var job) ? job.Completion : Task.CompletedTask;

    private Job? Find(string ownerId, string scopeKey, string jobId) =>
        _jobs.TryGetValue(jobId, out var job) && job.OwnerId == ownerId && job.Scope.Key == scopeKey ? job : null;

    // Идемпотентно; токен остановки не освобождаем — связанные токены доживающих задач держат ссылку
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
        _shutdown.Cancel();
    }

    // ── Прогресс и события ───────────────────────────────────────────────────────

    // Синхронный IProgress: Progress<T> увёл бы отчёт в пул и перемешал бы стадии. Трата пишется на первом
    // отчёте с Accepted — поставщик принял задачу; отчёты до принятия (очередь локальной видеокарты)
    // деньгами не считаются, а остановленное до принятия не списывается
    private sealed class JobProgress(VideoEditJobService owner, Job job, int variant) : IProgress<VideoProgress>
    {
        public void Report(VideoProgress value)
        {
            if (value.Accepted) owner.OnAccepted(job);
            lock (job.Gate)
            {
                if (!job.IsActive) return;
                job.Status = value.Stage switch
                {
                    VideoStage.Running => VideoEditJobStatus.Running,
                    VideoStage.Upscaling => VideoEditJobStatus.Upscaling,
                    VideoStage.Downloading => VideoEditJobStatus.Downloading,
                    _ => VideoEditJobStatus.Queued,
                };
                job.QueuePosition = value.QueuePosition;
                job.EtaSeconds = value.EtaSeconds;
                if (value.UpscaleEtaSeconds is { } upscaleEta) job.UpscaleEtaSeconds = upscaleEta;
                else if (value.Stage != VideoStage.Queued && value.Stage != VideoStage.Running) job.UpscaleEtaSeconds = null;
                if (value.RemoteId is { } remote) job.RemoteId = remote;
            }
            _ = owner.Broadcast(job.OwnerId, new VideoEditProgressMessage(job.Id, job.Scope.Key, job.SceneId ?? "",
                StageName(value.Stage), value.QueuePosition, value.EtaSeconds, variant, job.Quote.Count, job.Initiator, value.Percent,
                value.UpscaleEtaSeconds)
            { SessionId = job.ChatSessionId ?? "" });
        }
    }

    private static string StageName(VideoStage stage) => stage switch
    {
        VideoStage.Running => VideoProgressStages.Running,
        VideoStage.Upscaling => VideoProgressStages.Upscaling,
        VideoStage.Downloading => VideoProgressStages.Downloading,
        _ => VideoProgressStages.Queued,
    };

    private async Task Broadcast(string ownerId, ServerMessage message)
    {
        if (_broadcaster is null) return;
        try
        {
            await _broadcaster.ToOwner(ownerId, message);
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Видео: событие {Type} не доставлено", message.Type);
        }
    }

    // ── Помощники ────────────────────────────────────────────────────────────────

    // Проверка params по ParamNames драйвера: неизвестный ключ иначе молча выпал бы в драйвере
    private static string? CheckParams(IVideoEngine engine, VideoModelInfo model, JsonObject parameters)
    {
        if (parameters.Count == 0) return null;
        // Размер теперь задаёт формат: частный параметр ступени (resolution у fal и Higgsfield, size у local) не принимается
        var tierParam = model.Caps.Format?.WireParam ?? (engine.Key == VideoCatalog.LocalKey ? "size" : null);
        if (tierParam is not null && parameters.ContainsKey(tierParam))
            return $"Размер задаётся форматом: передайте quality (standard, high, max), а не «{tierParam}»";
        var names = engine.ParamNames(model) ?? new HashSet<string>();
        return parameters.Select(p => p.Key).FirstOrDefault(k => !names.Contains(k)) is { } unknown
            ? $"Неизвестный параметр «{unknown}» у модели {model.Id}"
              + (names.Count == 0 ? ". Частных параметров у неё нет." : ". Допустимые: " + string.Join(", ", names.Order()) + ".")
            : null;
    }

    private static string NoModelText(IVideoEngine engine, VideoCatalog.Need need) =>
        $"У поставщика «{engine.Label}» нет модели для этой сцены ({(need.FrameA ? "кадр A" : "без кадров")}"
        + (need.FrameB ? " → кадр B" : "") + $", {need.DurationSec} с)";

    // Отказ области (личный чат, локальный проект) — свой код; прочие отказы — «поставщик недоступен»
    private static string ScopeRefusalCode(VideoEditScope scope) =>
        scope.IsPersonal ? VideoEditorErrors.LocalUnavailablePersonal : VideoEditorErrors.ProjectLocalUnsupported;

    private async Task RefreshModelsQuietly(IVideoEngine engine, CancellationToken ct)
    {
        try { await engine.RefreshModelsAsync(ct); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogDebug(ex, "Видео: каталог поставщика {Provider} не обновился", engine.Key);
        }
    }

    private static bool SafeEnabled(IVideoEngine engine)
    {
        try { return engine.Enabled; }
        catch { return false; }
    }

    private static bool IsLocalHeavy(Quote q) =>
        q.Heavy || string.Equals(q.Provider, VideoCatalog.LocalKey, StringComparison.OrdinalIgnoreCase);

    private static VideoCost? EstimateCost(Quote q) => q.Price.Amount is { } a ? new VideoCost(a, q.Price.Unit) : null;

    private static VideoQuoteResponse ToDto(Quote q) =>
        new(q.Id, q.Provider, q.Model.Id, q.Count, q.DurationSec, q.Price, q.License, q.Heavy, q.ExpiresAt, q.Format);

    private static VideoJobDto ToDto(Job j)
    {
        lock (j.Gate)
            return new VideoJobDto(j.Id, j.Scope.Key, StatusName(j.Status), j.Quote.Provider, j.Quote.Model.Id, j.Quote.Count,
                [.. j.Variants.Select(v => v.Variant)], j.Cost, j.Outcome?.ToString().ToLowerInvariant(), j.Charged, j.Error,
                j.QueuePosition, j.EtaSeconds, j.CreatedAt, j.ChatSessionId, j.SceneId, j.Initiator, j.Quote.License,
                j.UpscaleEtaSeconds, j.Quote.Count > 1 ? j.Variant : null);
    }

    private static string StatusName(VideoEditJobStatus s) => s.ToString().ToLowerInvariant();

    private void PruneQuotes()
    {
        var now = Now();
        foreach (var (id, q) in _quotes)
            if (q.ExpiresAt < now) _quotes.TryRemove(id, out _);
    }

    private DateTime Now() => _time.GetUtcNow().UtcDateTime;

    private static string NewId() => Guid.NewGuid().ToString("N");

    private static VideoEditCallResult<T> Fail<T>(string code, string error) => VideoEditCallResult<T>.Fail(code, error);
}

public sealed record VideoJobCreated(string JobId);
