using System.Collections.Concurrent;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services.Composition;
using ClaudeHomeServer.Services.ImageEditor;
using ClaudeHomeServer.Services.Images.Editing.Raster;
using ClaudeHomeServer.Services.Spend;

namespace ClaudeHomeServer.Services.ImageEditor;

// Исполнитель задач редактора (ADR-017, разделы 4 и 7): котировка → 202 с jobId → события
// прогресса и готовности вариантов в группу владельца → отмена. Реестр — в памяти.
//
// Инварианты:
// - запуск только по котировке и ровно на её паре «поставщик + модель». Поставщик отказал
//   или пропал — отказ и, если есть сосед, НОВАЯ котировка соседа (retryQuote); сам
//   исполнитель на соседа не переходит никогда;
// - трата пишется в общий учёт (ISpendCollector → SpendStore) на того, кто запустил
//   (ownerId задачи), в момент принятия задачи поставщиком — ВСЕГДА, даже с неизвестной
//   суммой: сумма догоняется отдельной записью. Отмена и сбой после принятия запись не
//   отменяют. Поставщик честно сказал «не списано» — компенсирующая запись с минусом,
//   журнал только дописывается;
// - котировка без суммы запуск НЕ запрещает (C1 пересмотрен 2026-09-26, решение Андрея:
//   Higgsfield доступен всем без запретов). Такая трата ложится на запустившего сразу при
//   принятии с подписью SpendLabelPendingAmount («сумма уточняется»), а фактическая сумма
//   из ответа поставщика (ActualCost) догоняет её отдельной записью;
// - потолки одновременных задач не распространяются на Higgsfield: работа идёт у
//   поставщика, а не на нашем железе;
// - чужая задача и чужая котировка неотличимы от несуществующих.
// - вход перед драйвером ужимается под лимиты модели (InputFitter), а варианты после
//   скачивания приводятся к размеру исходника (MatchSourceSize, ADR-018 §9). Оригинал в
//   проекте не трогается: сервис его не видит, работает с байтами запроса.
public sealed class ImageEditJobService : IImageEditJobs, IDisposable
{
    public const int MaxJobsPerOwner = 2;
    public const int MaxJobsPerInstance = 4;
    private static readonly TimeSpan QuoteTtl = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan CancelWait = TimeSpan.FromSeconds(10);

    private readonly IEnumerable<IImageEditor> _editors;
    private readonly ISessionBroadcaster? _broadcaster;
    private readonly ISpendCollector? _spend;
    private readonly ImageEditWorkspace _workspace;
    private readonly ILogger<ImageEditJobService> _log;
    private readonly TimeProvider _time;
    private readonly InputFitter? _fitter;
    private readonly ConcurrentDictionary<string, Quote> _quotes = new();
    private readonly ConcurrentDictionary<string, Job> _jobs = new();
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Lock _startLock = new();

    public ImageEditJobService(
        IEnumerable<IImageEditor> editors,
        ImageEditWorkspace workspace,
        ILogger<ImageEditJobService> log,
        ISpendCollector? spend = null,
        ISessionBroadcaster? broadcaster = null,
        TimeProvider? time = null,
        IImageRaster? raster = null)
    {
        _fitter = raster is null ? null : new InputFitter(raster);
        _editors = editors;
        _spend = spend;
        _workspace = workspace;
        _log = log;
        _broadcaster = broadcaster;
        _time = time ?? TimeProvider.System;
    }

    // Задача кончилась (готова, сбой, отмена): владелец и итог. Слушатель — нити картинок: варианты
    // становятся версиями до события image_edit_completed, чтобы фронт, перечитав нити, их застал.
    // Сбой слушателя задачу не роняет
    public event Func<string, ImageEditJobDto, Task>? Finished;

    // Вариант готов, а задача ещё идёт (драйвер отдал EditProgress.Ready): вариант уже лежит в рабочей
    // папке и в job.Variants. Слушатель — нити картинок: вариант становится версией сразу. Для
    // последнего варианта приходит раньше Finished. Сбой слушателя задачу не роняет
    public event Func<string, ImageEditJobDto, Task>? VariantReady;

    private sealed record Quote(
        string Id, string OwnerId, string ProjectId, string Provider, ImageEditModelInfo Model,
        ImageEditOp Op, int Count, ImageEditEstimateDto Estimate, DateTime ExpiresAt, int? ExpectedSeconds);

    private sealed class Job(string id, string ownerId, string projectId, Quote quote, CancellationTokenSource cts)
    {
        // Размер исходника до ужатия: к нему приводятся варианты; null — приводить не к чему
        public (int Width, int Height)? MatchSize { get; init; }
        public string? BaseStepId { get; init; }
        // Чат картинки и кто запустил (ADR-018 §2): едут в DTO, события и запись траты
        public string? ChatSessionId { get; init; }
        // Нить картинки чата ChatSessionId (ADR-019): «Взять» принимает варианты только своей задачи
        public string? ThreadId { get; init; }
        public ImageEditInitiator Initiator { get; init; }
        public string? SizeNote { get; set; }
        public readonly Lock Gate = new();
        public string Id { get; } = id;
        public string OwnerId { get; } = ownerId;
        public string ProjectId { get; } = projectId;
        public Quote Quote { get; } = quote;
        public CancellationTokenSource Cts { get; } = cts;
        public DateTime CreatedAt { get; init; }
        public ImageEditJobStatus Status { get; set; } = ImageEditJobStatus.Queued;
        public int? QueuePosition { get; set; }
        // Текущий прогон драйвера (номер, число, ETA) и момент его перехода в Running по часам
        // бэкенда: наружу уходит уже посчитанное «прошло», часы браузера с нашими не сверены
        public int? Run { get; set; }
        public int? Runs { get; set; }
        public int? EtaSeconds { get; set; }
        public DateTime? RunStartedAt { get; set; }
        public List<int> Variants { get; } = [];
        public EditCost? Cost { get; set; }
        public EditOutcome? Outcome { get; set; }
        public bool? Charged { get; set; }
        public string? Error { get; set; }
        // Поставщик принял задачу: с этого момента возможна трата
        public bool Accepted { get; set; }
        // Запись траты уже лежит в учёте; RecordedAmount — её сумма, null — неизвестна
        public bool SpendWritten { get; set; }
        public double? RecordedAmount { get; set; }
        public Task Completion { get; set; } = Task.CompletedTask;
        // Очередь разбора готовых по ходу вариантов (EditProgress.Ready): отчёт синхронный, а
        // приведение размера и событие — асинхронные, и финал обязан их дождаться. Правится под Gate
        public Task Pipeline { get; set; } = Task.CompletedTask;
        // Сколько вариантов драйвер уже отдал по ходу: финал пишет только те, что после них
        public int ReadyCount { get; set; }

        public bool IsActive => Status is ImageEditJobStatus.Queued or ImageEditJobStatus.Running or ImageEditJobStatus.Downloading;
    }

    // ── Котировка ────────────────────────────────────────────────────────────────

    public async Task<ImageEditCallResult<ImageEditQuoteDto>> QuoteAsync(
        string ownerId, string projectId, ImageEditQuoteRequest request, CancellationToken ct)
    {
        var editor = ImageEditCatalog.FindAvailable(_editors, request.Provider);
        if (editor is null)
            return Fail<ImageEditQuoteDto>(ImageEditErrorCodes.ProviderUnavailable,
                $"Поставщик «{request.Provider}» не настроен или отключён администратором");

        var traits = new EditTraits(request.HasMask, request.References, request.HasCharacter, request.HasAnnotations, request.Removal);
        var op = request.HasMask && request.Op == ImageEditOp.Edit ? ImageEditOp.Inpaint : request.Op;
        var model = string.Equals(request.Model?.Trim(), ImageEditCatalog.AutoModelId, StringComparison.OrdinalIgnoreCase)
            ? editor.PickModel(op, request.Mode, traits)
            : editor.Models.FirstOrDefault(m => string.Equals(m.Id, request.Model?.Trim(), StringComparison.OrdinalIgnoreCase));
        if (model is null)
            return Fail<ImageEditQuoteDto>(ImageEditErrorCodes.InvalidRequest,
                $"У поставщика {editor.Label} нет модели для этой операции");
        if (!model.Caps.Ops.Contains(op))
            return Fail<ImageEditQuoteDto>(ImageEditErrorCodes.InvalidRequest,
                op == ImageEditOp.Inpaint ? $"Модель {model.Label} не принимает маску" : $"Модель {model.Label} так не умеет");
        if (request.Count < 1 || request.Count > model.Caps.MaxCount)
            return Fail<ImageEditQuoteDto>(ImageEditErrorCodes.InvalidRequest,
                $"Модель {model.Label}: вариантов от 1 до {model.Caps.MaxCount}");

        ImageEditEstimateDto estimate;
        try
        {
            estimate = editor is IImageEditQuoter quoter
                ? await quoter.EstimateAsync(model, request, ct)
                : ImageEditEstimates.FromHint(model, request, editor.PriceUnit);
        }
        catch (ImageEditProviderUnavailableException ex)
        {
            return Fail<ImageEditQuoteDto>(ImageEditErrorCodes.ProviderUnavailable, ex.Message);
        }

        var quote = new Quote(Guid.NewGuid().ToString("N"), ownerId, projectId, editor.Key, model, op, request.Count,
            estimate, Now() + QuoteTtl, estimate.EtaSeconds ?? (editor as IImageEditQuoter)?.ExpectedSeconds(model));
        PruneQuotes();
        _quotes[quote.Id] = quote;
        return ImageEditCallResult<ImageEditQuoteDto>.Ok(ToDto(quote));
    }

    // ── Запуск ───────────────────────────────────────────────────────────────────

    public async Task<ImageEditCallResult<ImageEditJobCreatedDto>> StartAsync(
        string ownerId, string projectId, ImageEditJobInput input, CancellationToken ct)
    {
        if (!_quotes.TryGetValue(input.QuoteId, out var quote)
            || quote.OwnerId != ownerId || quote.ProjectId != projectId || quote.ExpiresAt < Now())
            return Fail<ImageEditJobCreatedDto>(ImageEditErrorCodes.QuoteNotFound,
                "Котировка устарела — цена пересчитается автоматически");

        // Ровно поставщик котировки: пропал — отказ, а не сосед
        var editor = ImageEditCatalog.FindAvailable(_editors, quote.Provider);
        if (editor is null)
            return Fail<ImageEditJobCreatedDto>(ImageEditErrorCodes.ProviderUnavailable,
                $"Поставщик «{quote.Provider}» больше недоступен");

        // Ужатие до Compose: сверка размеров маски и исходника идёт уже по ужатым
        (int Width, int Height)? matchSize = null;
        if (_fitter is not null)
        {
            var fitted = await _fitter.FitAsync(input, ImageEditCatalog.WithInputLimits(quote.Model).Caps, ct);
            if (fitted.Value is not { } fit)
                return Fail<ImageEditJobCreatedDto>(fitted.ErrorCode ?? ImageEditErrorCodes.InvalidRequest,
                    fitted.Error ?? "Картинку не удалось подготовить");
            input = fit.Input;
            // «Дорисовать за края» и «Улучшить качество» меняют размер по смыслу — возврата нет
            if (input.MatchSourceSize && quote.Op is not (ImageEditOp.Outpaint or ImageEditOp.Upscale)
                && fit.SourceWidth is { } w && fit.SourceHeight is { } h)
                matchSize = (w, h);
        }

        var composed = EditRequestComposer.Compose(input, quote.Op, quote.Model, quote.Count, input.AspectRatio);
        if (composed.Value is not { } request)
            return Fail<ImageEditJobCreatedDto>(composed.ErrorCode ?? ImageEditErrorCodes.InvalidRequest,
                composed.Error ?? "Неверный запрос");

        Job job;
        lock (_startLock)
        {
            var active = _jobs.Values.Where(j => j.IsActive && IsCapped(j.Quote.Provider)).ToList();
            if (IsCapped(editor.Key)
                && (active.Count(j => j.OwnerId == ownerId) >= MaxJobsPerOwner || active.Count >= MaxJobsPerInstance))
                return Fail<ImageEditJobCreatedDto>(ImageEditErrorCodes.TooManyJobs,
                    "Уже идёт слишком много генераций — дождитесь окончания текущих");
            if (!_quotes.TryRemove(quote.Id, out _))
                return Fail<ImageEditJobCreatedDto>(ImageEditErrorCodes.QuoteNotFound, "Котировка уже использована");

            job = new Job(Guid.NewGuid().ToString("N"), ownerId, projectId, quote,
                CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token))
            {
                CreatedAt = Now(),
                MatchSize = matchSize,
                BaseStepId = string.IsNullOrWhiteSpace(input.BaseStepId) ? null : input.BaseStepId.Trim(),
                ChatSessionId = string.IsNullOrWhiteSpace(input.ChatSessionId) ? null : input.ChatSessionId.Trim(),
                ThreadId = string.IsNullOrWhiteSpace(input.ThreadId) ? null : input.ThreadId.Trim(),
                Initiator = input.Initiator,
            };
            _jobs[job.Id] = job;
            job.Completion = Task.Run(() => RunAsync(job, editor, request));
        }

        _workspace.Sweep(Now());
        return ImageEditCallResult<ImageEditJobCreatedDto>.Ok(new ImageEditJobCreatedDto(job.Id));
    }

    private async Task RunAsync(Job job, IImageEditor editor, ImageEditRequest request)
    {
        ImageEditResult result;
        var cancelled = false;
        try
        {
            result = await editor.RunAsync(request, new JobProgress(this, job), job.Cts.Token);
        }
        catch (OperationCanceledException) when (job.Cts.IsCancellationRequested)
        {
            cancelled = true;
            result = new ImageEditResult(EditOutcome.Cancelled, [], null, null, null, "Остановлено по запросу");
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Редактор картинок: драйвер {Provider} упал", editor.Key);
            result = new ImageEditResult(EditOutcome.Failed, [], null, null, null, "Сбой поставщика: " + ex.Message);
        }

        // Варианты, отданные по ходу, разбираются до финала при любом исходе: иначе Finished обогнал
        // бы VariantReady последнего варианта, а отмена потеряла бы уже готовые
        Task pipeline;
        lock (job.Gate) pipeline = job.Pipeline;
        await pipeline;

        try
        {
            if (!cancelled && result.Outcome == EditOutcome.Ok && result.Images.Count > 0)
                await CompleteAsync(job, result);
            else
                await FailAsync(job, editor, cancelled ? result with { Outcome = EditOutcome.Cancelled } : result);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Редактор картинок: не удалось завершить задачу {JobId}", job.Id);
            lock (job.Gate)
            {
                job.Status = ImageEditJobStatus.Failed;
                job.Outcome = EditOutcome.Failed;
                job.Error = "Не удалось сохранить результат";
            }
            await NotifyFinishedAsync(job);
        }
    }

    private Task NotifyFinishedAsync(Job job) => NotifyAsync(Finished, job, "завершения");

    private async Task NotifyAsync(Func<string, ImageEditJobDto, Task>? handlers, Job job, string what)
    {
        if (handlers is null) return;
        var dto = ToDto(job);
        foreach (var listener in handlers.GetInvocationList().Cast<Func<string, ImageEditJobDto, Task>>())
        {
            try
            {
                await listener(job.OwnerId, dto);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Редактор картинок: слушатель {What} задачи {JobId} упал", what, job.Id);
            }
        }
    }

    // Вариант к размеру исходника и в рабочую папку под номером n; true — пропорции не совпали
    private async Task<bool> SaveVariantAsync(Job job, int n, EditedImage image)
    {
        var mismatch = false;
        if (job.MatchSize is { } size && _fitter is not null)
        {
            var match = await _fitter.MatchSizeAsync(image, size.Width, size.Height, CancellationToken.None);
            image = match.Image;
            mismatch = match.Mismatch;
        }
        _workspace.SaveVariant(job.OwnerId, job.Id, n, image);
        return mismatch;
    }

    // Звено очереди Pipeline: варианты first.. сохраняются, попадают в job.Variants и уходят
    // слушателям VariantReady. Не бросает — иначе следующее звено и финал не дождались бы очереди
    private async Task DeliverReadyAsync(Task previous, Job job, int first, IReadOnlyList<EditedImage> ready)
    {
        // Звено заводится под job.Gate в потоке драйвера: разбор уходит из него сразу
        await Task.Yield();
        await previous;
        try
        {
            for (var i = 0; i < ready.Count; i++)
            {
                var n = first + i;
                var mismatch = await SaveVariantAsync(job, n, ready[i]);
                lock (job.Gate)
                {
                    job.Variants.Add(n);
                    if (mismatch) job.SizeNote = ImageEditSizeNotes.AspectMismatch;
                }
            }
            await NotifyAsync(VariantReady, job, "готового варианта");
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Редактор картинок: готовые варианты задачи {JobId} не разобраны", job.Id);
        }
    }

    // Финал пишет только хвост: варианты, отданные по ходу (ReadyCount), уже лежат в job.Variants
    private async Task CompleteAsync(Job job, ImageEditResult result)
    {
        int delivered;
        lock (job.Gate) delivered = job.ReadyCount;
        string? sizeNote = null;
        List<int> tail = [];
        for (var i = delivered; i < result.Images.Count; i++)
        {
            if (await SaveVariantAsync(job, i + 1, result.Images[i])) sizeNote = ImageEditSizeNotes.AspectMismatch;
            tail.Add(i + 1);
        }

        var cost = result.ActualCost ?? EstimateCost(job.Quote);
        // Поставщик вернул результат, не сообщив о принятии, — трата всё равно была
        RecordSpend(job, cost?.Amount);
        lock (job.Gate)
        {
            job.Variants.AddRange(tail);
            job.Cost = cost;
            job.Outcome = EditOutcome.Ok;
            job.Charged = result.Charged ?? true;
            job.SizeNote = sizeNote ?? job.SizeNote;
            job.Status = ImageEditJobStatus.Completed;
            sizeNote = job.SizeNote;
        }
        await NotifyFinishedAsync(job);
        await Broadcast(job.OwnerId, new ImageEditCompletedMessage(job.Id, job.ProjectId, [.. job.Variants], cost,
            job.ChatSessionId, job.Initiator, sizeNote, job.ThreadId));
    }

    private async Task FailAsync(Job job, IImageEditor editor, ImageEditResult result)
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

        var retry = result.Outcome is EditOutcome.Cancelled or EditOutcome.Rejected ? null : RetryQuote(job, editor);
        lock (job.Gate)
        {
            job.Status = result.Outcome == EditOutcome.Cancelled ? ImageEditJobStatus.Cancelled : ImageEditJobStatus.Failed;
            job.Outcome = result.Outcome;
            job.Charged = charged;
            job.Error = result.Error;
            if (charged != false && job.RecordedAmount is { } amount)
                job.Cost = new EditCost(amount, editor.PriceUnit);
        }
        await NotifyFinishedAsync(job);
        await Broadcast(job.OwnerId,
            new ImageEditFailedMessage(job.Id, job.ProjectId, result.Outcome, charged, result.Error, retry,
                job.ChatSessionId, job.Initiator, job.ThreadId));
    }

    // Сосед с подходящей моделью — только предложение: UI покажет «Повторить через …»
    private ImageEditQuoteDto? RetryQuote(Job job, IImageEditor failed)
    {
        var q = job.Quote;
        foreach (var other in ImageEditCatalog.Available(_editors).Where(e => e.Key != failed.Key))
        {
            var model = other.PickModel(q.Op, EditMode.Auto, new EditTraits(q.Op == ImageEditOp.Inpaint, 0, false));
            if (model is null || !model.Caps.Ops.Contains(q.Op)) continue;
            var count = Math.Min(q.Count, model.Caps.MaxCount);
            var request = new ImageEditQuoteRequest(other.Key, model.Id, EditMode.Auto, q.Op, count,
                q.Op == ImageEditOp.Inpaint, 0, false, null, null);
            var estimate = ImageEditEstimates.FromHint(model, request, other.PriceUnit);
            var retry = new Quote(Guid.NewGuid().ToString("N"), job.OwnerId, job.ProjectId, other.Key, model, q.Op, count,
                estimate, Now() + QuoteTtl, (other as IImageEditQuoter)?.ExpectedSeconds(model));
            _quotes[retry.Id] = retry;
            return ToDto(retry);
        }
        return null;
    }

    // Приняли у поставщика — пишем трату на запустившего по котировке, один раз
    private void OnAccepted(Job job)
    {
        lock (job.Gate)
        {
            if (job.Accepted) return;
            job.Accepted = true;
        }
        RecordSpend(job, job.Quote.Estimate.Amount);
    }

    // Первая запись — всегда, с числом вариантов и суммой, если она известна. Сумма, ставшая
    // известной позже, догоняет её отдельной записью без генераций.
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
            _log.LogWarning("Редактор картинок: учёт расхода выключен, трата задачи {JobId} не записана", job.Id);
            return;
        }
        var credits = job.Quote.Estimate.Unit == ImageEditPriceUnits.Credits;
        try
        {
            _spend.Record(new SpendRecord
            {
                Timestamp = Now(),
                OwnerId = job.OwnerId,
                ProjectId = job.ProjectId,
                SessionId = job.ChatSessionId,
                Initiator = job.Initiator == ImageEditInitiator.Agent ? SpendInitiators.Agent : SpendInitiators.Human,
                Provider = job.Quote.Provider,
                Model = job.Quote.Model.Id,
                Source = job.Quote.Provider,
                CostUsd = credits ? null : amount,
                CostCredits = credits ? amount : null,
                Generations = generations,
                Label = amount is null && generations > 0 ? SpendLabelPendingAmount : SpendLabel,
            });
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Редактор картинок: не удалось записать трату задачи {JobId}", job.Id);
        }
    }

    // ── Чтение и отмена ──────────────────────────────────────────────────────────

    public ImageEditJobDto? Get(string ownerId, string projectId, string jobId) =>
        Find(ownerId, projectId, jobId) is { } job ? ToDto(job) : null;

    public async Task<ImageEditJobDto?> CancelAsync(string ownerId, string projectId, string jobId, CancellationToken ct)
    {
        if (Find(ownerId, projectId, jobId) is not { } job) return null;
        if (job.IsActive)
        {
            await job.Cts.CancelAsync();
            await Task.WhenAny(job.Completion, Task.Delay(CancelWait, ct));
        }
        return ToDto(job);
    }

    public EditedImage? OpenVariant(string ownerId, string projectId, string jobId, int variant)
    {
        if (Find(ownerId, projectId, jobId) is not { } job) return null;
        lock (job.Gate)
            if (!job.Variants.Contains(variant)) return null;
        return _workspace.OpenVariant(ownerId, jobId, variant);
    }

    private Job? Find(string ownerId, string projectId, string jobId) =>
        _jobs.TryGetValue(jobId, out var job) && job.OwnerId == ownerId && job.ProjectId == projectId ? job : null;

    // Идемпотентно: контейнер зовёт Dispose дважды — сервис зарегистрирован под своим
    // типом и форвардером под IImageEditJobs. Токен остановки не освобождаем: связанные
    // токены задач, доживающих остановку, ещё держат на него ссылку.
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
        _shutdown.Cancel();
    }

    private int _disposed;

    // ── Прогресс и события ───────────────────────────────────────────────────────

    // Синхронный IProgress: Progress<T> увёл бы отчёт в пул и перемешал бы стадии. Поэтому и готовые
    // варианты (Ready) здесь не ждутся, а только встают звеном в job.Pipeline — по порядку отчётов
    private sealed class JobProgress(ImageEditJobService owner, Job job) : IProgress<EditProgress>
    {
        public void Report(EditProgress value)
        {
            owner.OnAccepted(job);
            int? elapsed;
            lock (job.Gate)
            {
                if (value.Ready is { Count: > 0 } ready)
                {
                    job.Pipeline = owner.DeliverReadyAsync(job.Pipeline, job, job.ReadyCount + 1, ready);
                    job.ReadyCount += ready.Count;
                }
                if (!job.IsActive) return;
                var status = value.Stage switch
                {
                    EditStage.Running => ImageEditJobStatus.Running,
                    EditStage.Downloading => ImageEditJobStatus.Downloading,
                    _ => ImageEditJobStatus.Queued,
                };
                // Отсчёт — с перехода в Running, в том числе нового прогона после предыдущего
                if (status == ImageEditJobStatus.Running
                    && (job.Status != ImageEditJobStatus.Running || job.Run != value.Run))
                    job.RunStartedAt = owner.Now();
                job.Status = status;
                job.QueuePosition = value.QueuePosition;
                job.Run = value.Run;
                job.Runs = value.Runs;
                job.EtaSeconds = value.EtaSeconds;
                elapsed = owner.RunElapsedSeconds(job);
            }
            _ = owner.Broadcast(job.OwnerId,
                new ImageEditProgressMessage(job.Id, job.ProjectId, value.Stage, value.QueuePosition,
                    job.ChatSessionId, job.Initiator, job.ThreadId, value.Run, value.Runs, value.EtaSeconds,
                    elapsed));
        }
    }

    private async Task Broadcast(string ownerId, ServerMessage message)
    {
        if (_broadcaster is null) return;
        try
        {
            await _broadcaster.ToOwner(ownerId, message);
        }
        catch (Exception ex)
        {
            // Потерянное событие фронт догоняет GET …/jobs/{jobId}
            _log.LogDebug(ex, "Редактор картинок: событие {Type} не доставлено", message.Type);
        }
    }

    // Подпись записи в общем учёте: по ней трата редактора отличима от генерации из чата
    public const string SpendLabel = "image-editor";
    // Трата принята, а сумма пока неизвестна: её догонит запись с SpendLabel без генераций
    public const string SpendLabelPendingAmount = "image-editor: сумма уточняется";

    // Потолки одновременных задач — только для поставщиков, которые их требуют
    private static bool IsCapped(string provider) => provider != HiggsfieldImageEditor.ProviderKey;

    private static EditCost? EstimateCost(Quote q) =>
        q.Estimate.Amount is { } a ? new EditCost(a, q.Estimate.Unit) : null;

    private ImageEditQuoteDto ToDto(Quote q) =>
        new(q.Id, q.Provider, q.Model.Id, q.Estimate, q.ExpiresAt, q.ExpectedSeconds);

    private ImageEditJobDto ToDto(Job j)
    {
        lock (j.Gate)
            return new ImageEditJobDto(j.Id, j.ProjectId, j.Status, j.Quote.Provider, j.Quote.Model.Id,
                [.. j.Variants], j.Cost, j.Outcome, j.Charged, j.Error, j.QueuePosition, j.CreatedAt,
                j.ChatSessionId, j.Initiator, j.BaseStepId, j.SizeNote, j.Quote.Count, j.Quote.Estimate, j.ThreadId,
                j.Run, j.Runs, j.EtaSeconds, RunElapsedSeconds(j));
    }

    // Сколько идёт текущий прогон по часам бэкенда; вне Running — null. Звать под j.Gate
    private int? RunElapsedSeconds(Job j) =>
        j.Status == ImageEditJobStatus.Running && j.RunStartedAt is { } started
            ? Math.Max(0, (int)(Now() - started).TotalSeconds)
            : null;

    private void PruneQuotes()
    {
        var now = Now();
        foreach (var (id, q) in _quotes)
            if (q.ExpiresAt < now) _quotes.TryRemove(id, out _);
    }

    private DateTime Now() => _time.GetUtcNow().UtcDateTime;

    private static ImageEditCallResult<T> Fail<T>(string code, string error) => ImageEditCallResult<T>.Fail(code, error);
}
