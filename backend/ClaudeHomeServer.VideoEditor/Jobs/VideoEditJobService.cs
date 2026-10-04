using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json.Nodes;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services.Composition;
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
        long? ContextRevision = null)
    {
        public bool Heavy => Model.Caps.Heavy;
        public string License => Model.Caps.License.Label;
    }

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
        public VideoInputsSnapshotDto Inputs { get; set; } = new("", null, null);
        public VideoEditJobStatus Status { get; set; } = VideoEditJobStatus.Queued;
        public int Variant { get; set; } = 1;
        public int? QueuePosition { get; set; }
        public int? EtaSeconds { get; set; }
        public List<VideoVariantResult> Variants { get; } = [];
        public VideoCostDto? Cost { get; set; }
        public VideoOutcome? Outcome { get; set; }
        public bool? Charged { get; set; }
        public string? Error { get; set; }
        public bool Accepted { get; set; }
        public bool SpendWritten { get; set; }
        public double? RecordedAmount { get; set; }
        public Task Completion { get; set; } = Task.CompletedTask;

        public bool IsActive => Status is VideoEditJobStatus.Queued or VideoEditJobStatus.Running or VideoEditJobStatus.Downloading;
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
        var aspect = request.Aspect ?? settings.Aspect ?? prefs.Aspect;
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

        if (aspect is not null && model.Caps.Aspects.Count > 0 && !model.Caps.Aspects.Contains(aspect))
            return Fail<VideoQuoteResponse>(VideoEditorErrors.InvalidRequest,
                $"Модель «{model.Label}» снимает в пропорциях {string.Join(", ", model.Caps.Aspects)}, а не {aspect}");
        sound = sound && model.Caps.Sound;

        VideoPriceDto price;
        try
        {
            price = await EstimateAsync(engine, model, ProbeRequest(scope, model, settings.Text, duration, aspect, sound), count, ct);
        }
        catch (VideoEngineUnavailableException ex)
        {
            return Fail<VideoQuoteResponse>(VideoEditorErrors.ProviderUnavailable, ex.Message);
        }

        var quote = new Quote(NewId(), ownerId, scope.Key, request.SessionId, request.SceneId, engine.Key, model, count,
            duration, aspect, sound, price, Now() + QuoteTtl, request.ContextRevision);
        PruneQuotes();
        _quotes[quote.Id] = quote;
        return VideoEditCallResult<VideoQuoteResponse>.Ok(ToDto(quote));
    }

    private static VideoRequest ProbeRequest(VideoEditScope scope, VideoModelInfo model, string text, int duration,
        string? aspect, bool sound) =>
        new(model.Id, scope, text, null, null, duration, aspect, sound);

    // Котировка драйвера — на все варианты: каждый вариант — отдельный прогон
    private static async Task<VideoPriceDto> EstimateAsync(IVideoEngine engine, VideoModelInfo model, VideoRequest probe,
        int count, CancellationToken ct)
    {
        var estimate = engine is IVideoQuoter quoter
            ? await quoter.EstimateAsync(model, probe, ct)
            : FromHint(engine, model, probe);
        return new VideoPriceDto(estimate.Amount * count, estimate.Unit, estimate.Approx, estimate.Source,
            estimate.EtaSeconds * count, estimate.QueueLength);
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
        var chosen = settings with
        {
            Provider = quote.Provider, Model = quote.Model.Id, DurationSec = quote.DurationSec, Aspect = quote.Aspect,
            Sound = quote.Sound, Count = quote.Count,
        };
        await _threads.OnLaunchedAsync(ownerId, scope.Key, input.SessionId, input.SceneId, job.Id, ToDto(quote),
            prompt, initiator, chosen, ct);

        var request = new VideoRequest(quote.Model.Id, scope, prompt, frameA.Bytes, frameB.Bytes, quote.DurationSec,
            quote.Aspect, quote.Sound, parameters, input.Seed);
        job.Completion = Task.Run(() => RunAsync(job, engine, request));

        _workspace.Sweep(Now());
        return VideoEditCallResult<VideoJobCreated>.Ok(new VideoJobCreated(job.Id));
    }

    private async Task RunAsync(Job job, IVideoEngine engine, VideoRequest request)
    {
        VideoResult? failure = null;
        var cancelled = false;
        var costs = new List<VideoCost?>();
        try
        {
            for (var variant = 1; variant <= job.Quote.Count; variant++)
            {
                lock (job.Gate) job.Variant = variant;
                var run = request.Seed is { } seed ? request with { Seed = seed + variant - 1 } : request;
                VideoResult result;
                try
                {
                    result = await engine.RunAsync(run, new JobProgress(this, job, variant), job.Cts.Token);
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
            file.HasSound ?? job.Quote.Sound, null);
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

        var retry = cancelled || result.Outcome == VideoOutcome.Rejected ? null : RetryQuote(job, engine);
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
            var estimate = FromHint(other, model, ProbeRequest(job.Scope, model, scene.Settings.Text, q.DurationSec, q.Aspect, q.Sound));
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
                    VideoStage.Downloading => VideoEditJobStatus.Downloading,
                    _ => VideoEditJobStatus.Queued,
                };
                job.QueuePosition = value.QueuePosition;
                job.EtaSeconds = value.EtaSeconds;
            }
            _ = owner.Broadcast(job.OwnerId, new VideoEditProgressMessage(job.Id, job.Scope.Key, job.SceneId ?? "",
                StageName(value.Stage), value.QueuePosition, value.EtaSeconds, variant, job.Quote.Count, job.Initiator)
            { SessionId = job.ChatSessionId ?? "" });
        }
    }

    private static string StageName(VideoStage stage) => stage switch
    {
        VideoStage.Running => "running",
        VideoStage.Downloading => "downloading",
        _ => "queued",
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
        new(q.Id, q.Provider, q.Model.Id, q.Count, q.DurationSec, q.Price, q.License, q.Heavy, q.ExpiresAt);

    private static VideoJobDto ToDto(Job j)
    {
        lock (j.Gate)
            return new VideoJobDto(j.Id, j.Scope.Key, StatusName(j.Status), j.Quote.Provider, j.Quote.Model.Id, j.Quote.Count,
                [.. j.Variants.Select(v => v.Variant)], j.Cost, j.Outcome?.ToString().ToLowerInvariant(), j.Charged, j.Error,
                j.QueuePosition, j.EtaSeconds, j.CreatedAt, j.ChatSessionId, j.SceneId, j.Initiator, j.Quote.License);
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
