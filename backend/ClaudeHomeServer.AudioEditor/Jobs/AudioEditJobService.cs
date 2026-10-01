using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services.AudioEditor.Catalog;
using ClaudeHomeServer.Services.AudioEditor.Prefs;
using ClaudeHomeServer.Services.AudioEditor.Threads;
using ClaudeHomeServer.Services.Composition;
using ClaudeHomeServer.Services.Spend;

namespace ClaudeHomeServer.Services.AudioEditor.Jobs;

// Исполнитель задач звука (ADR-021 §2, образец — ImageEditJobService): котировка → запуск строго по
// quoteId → события audio_edit_* в группу владельца → итог версиями нити. Реестр — в памяти; запуски,
// оборванные перезапуском, снимает AudioThreadRecovery.
//
// Инварианты:
// - запуск только по котировке и ровно на её паре «поставщик + модель». Поставщик отказал или пропал —
//   отказ и, если есть сосед с ТОЙ ЖЕ операцией и ТЕМ ЖЕ видом голоса, его новая котировка (RetryQuote).
//   Сам исполнитель на соседа не переходит никогда: клон у одного поставщика — не клон у другого;
// - потолки: MaxJobsPerOwner на владельца, MaxJobsPerInstance на инстанс, у local — не больше одной
//   тяжёлой задачи (AudioCaps.HeavyOps): тяжёлые держат GPU «одна за раз». Отказ — текст, не исключение;
// - трата пишется в общий учёт (ISpendCollector) на запустившего в момент принятия задачи поставщиком —
//   всегда, у local с нулём; в своей валюте (доллары, кредиты, рубли не складываются), в Label — модель и
//   единица тарификации. Поставщик честно сказал «не списано» — компенсирующая запись с минусом;
// - лицензия модели фиксируется в котировке и переходит в запуск и версии: каталог может поменяться;
// - чужая задача и чужая котировка неотличимы от несуществующих.
public sealed class AudioEditJobService : IDisposable
{
    public const int MaxJobsPerOwner = 2;
    public const int MaxJobsPerInstance = 4;
    public static readonly TimeSpan QuoteTtl = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan CancelWait = TimeSpan.FromSeconds(10);

    public const string OwnerLimitText = "Уже идут две задачи звука — дождитесь окончания одной из них";
    public const string InstanceLimitText = "Сервер занят: идут четыре задачи звука — попробуйте через минуту";
    public const string HeavyBusyText =
        "На локальной видеокарте уже идёт тяжёлая задача (обучение голоса или разбор на дорожки) — " +
        "дождитесь её окончания или выберите облачного поставщика";
    public const string QuoteExpiredText = "Котировка устарела — запросите цену заново";

    private readonly IEnumerable<IAudioEngine> _engines;
    private readonly AudioEditWorkspace _workspace;
    private readonly AudioJobThreads? _threads;
    private readonly AudioPrefsService? _prefs;
    private readonly ISpendCollector? _spend;
    private readonly ISessionBroadcaster? _broadcaster;
    private readonly ILogger<AudioEditJobService> _log;
    private readonly TimeProvider _time;
    private readonly ConcurrentDictionary<string, Quote> _quotes = new();
    private readonly ConcurrentDictionary<string, Job> _jobs = new();
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Lock _startLock = new();
    private int _disposed;

    public AudioEditJobService(
        IEnumerable<IAudioEngine> engines,
        AudioEditWorkspace workspace,
        ILogger<AudioEditJobService> log,
        AudioJobThreads? threads = null,
        AudioPrefsService? prefs = null,
        ISpendCollector? spend = null,
        ISessionBroadcaster? broadcaster = null,
        TimeProvider? time = null)
    {
        _engines = engines;
        _workspace = workspace;
        _log = log;
        _threads = threads;
        _prefs = prefs;
        _spend = spend;
        _broadcaster = broadcaster;
        _time = time ?? TimeProvider.System;
    }

    private sealed record Quote(
        string Id, string OwnerId, string ScopeKey, string Mode, AudioOp Op, string Provider, AudioModelInfo Model,
        int Count, AudioVoiceKind? VoiceKind, AudioPrice Price, JsonObject Fields, DateTime ExpiresAt)
    {
        public bool Heavy => Model.Caps.IsHeavy(Op);
        public string License => Model.Caps.License.Label;
    }

    private sealed class Job(string id, string ownerId, AudioEditScope scope, Quote quote, CancellationTokenSource cts)
    {
        public readonly Lock Gate = new();
        public string Id { get; } = id;
        public string OwnerId { get; } = ownerId;
        public AudioEditScope Scope { get; } = scope;
        public Quote Quote { get; } = quote;
        public CancellationTokenSource Cts { get; } = cts;
        public DateTime CreatedAt { get; init; }
        public string? ChatSessionId { get; set; }
        public string? ThreadId { get; set; }
        public AudioEditInitiator Initiator { get; init; }
        public string SpendLabel { get; init; } = "";
        public string SpendSource { get; init; } = "";
        public AudioEditJobStatus Status { get; set; } = AudioEditJobStatus.Queued;
        public int Variant { get; set; } = 1;
        public int? QueuePosition { get; set; }
        public int? EtaSeconds { get; set; }
        public List<AudioJobVariantDto> Variants { get; } = [];
        public AudioCost? Cost { get; set; }
        public AudioOutcome? Outcome { get; set; }
        public bool? Charged { get; set; }
        public string? Error { get; set; }
        public bool Accepted { get; set; }
        public bool SpendWritten { get; set; }
        public double? RecordedAmount { get; set; }
        public Task Completion { get; set; } = Task.CompletedTask;

        public bool IsActive => Status is AudioEditJobStatus.Queued or AudioEditJobStatus.Running or AudioEditJobStatus.Downloading;
    }

    // ── Котировка ────────────────────────────────────────────────────────────────

    public async Task<AudioEditCallResult<AudioQuoteDto>> QuoteAsync(
        string ownerId, AudioEditScope scope, AudioQuoteRequest request, CancellationToken ct)
    {
        if (!AudioModes.IsValid(request.Mode))
            return Fail<AudioQuoteDto>(AudioEditErrorCodes.InvalidRequest, $"Неизвестный режим: {request.Mode}");

        // Цепочка: явное в запросе → настройки нити → префы режима → умолчание каталога
        var thread = _threads is not null && _threads.OwnThread(ownerId, scope.Key, request.SessionId, request.ThreadId)
            ? _threads.Store.Get(ownerId, request.SessionId!).Threads.First(t => t.Id == request.ThreadId).Settings
            : null;
        var chain = _prefs is not null
            ? _prefs.Resolve(ownerId, scope, request.Mode, thread, CatalogDefault(request.Mode))
            : AudioPrefsResolver.Resolve(request.Mode, thread, null, CatalogDefault(request.Mode));

        if (!TryParseOp(request.Operation ?? chain.Operation, out var op) || AudioOps.IsNoAi(op))
            return Fail<AudioQuoteDto>(AudioEditErrorCodes.InvalidRequest,
                $"Неизвестная операция: {request.Operation ?? chain.Operation}");
        var count = request.Count ?? chain.Count;
        if (count < 1 || count > AudioModePrefs.MaxCount)
            return Fail<AudioQuoteDto>(AudioEditErrorCodes.InvalidRequest, $"Вариантов — от 1 до {AudioModePrefs.MaxCount}");
        var voiceKind = request.VoiceKind ?? DefaultVoiceKind(op);
        var modelId = request.Model ?? chain.Model;
        var providerKey = request.Provider ?? chain.Provider;

        IAudioEngine? engine;
        AudioModelInfo? model;
        if (!IsAuto(providerKey))
        {
            // Явный выбор не подменяем: лежит — отказ с причиной, а не тихий переход к соседу
            engine = AudioCatalog.Registered(_engines)
                .FirstOrDefault(e => string.Equals(e.Key, providerKey!.Trim(), StringComparison.OrdinalIgnoreCase));
            if (engine is null)
                return Fail<AudioQuoteDto>(AudioEditErrorCodes.ProviderUnavailable, $"Поставщик «{providerKey}» не заведён на этом сервере");
            if (!SafeEnabled(engine))
                return Fail<AudioQuoteDto>(AudioEditErrorCodes.ProviderUnavailable, $"Поставщик «{engine.Label}» сейчас недоступен");
            if (engine.ScopeRefusal(scope) is { } refusal)
                return Fail<AudioQuoteDto>(AudioEditErrorCodes.ProviderUnavailable, refusal);
            await RefreshModelsQuietly(engine, ct);
            model = Pick(engine, op, modelId, voiceKind);
            if (model is null)
                return Fail<AudioQuoteDto>(AudioEditErrorCodes.InvalidRequest,
                    $"У поставщика «{engine.Label}» нет такой модели для этой операции" + (voiceKind is null ? "" : " и вида голоса"));
        }
        else
        {
            var candidates = AudioCatalog.Available(_engines).Where(e => e.ScopeRefusal(scope) is null).ToList();
            foreach (var candidate in candidates) await RefreshModelsQuietly(candidate, ct);
            (engine, model) = candidates
                .Select(e => (Engine: e, Model: Pick(e, op, modelId, voiceKind)))
                .FirstOrDefault(p => p.Model is not null);
            if (engine is null || model is null)
                return Fail<AudioQuoteDto>(AudioEditErrorCodes.ProviderUnavailable, "Сейчас нет доступного поставщика для этой операции");
        }

        var fields = Merge(chain.Fields, request.Fields);
        AudioPrice price;
        try
        {
            price = await EstimateAsync(engine, model, new AudioRequest(op, model.Id, scope, Text: request.Text,
                DurationSec: request.DurationSec, Params: fields), count, ct);
        }
        catch (AudioEngineUnavailableException ex)
        {
            return Fail<AudioQuoteDto>(AudioEditErrorCodes.ProviderUnavailable, ex.Message);
        }

        var quote = new Quote(NewId(), ownerId, scope.Key, request.Mode, op, engine.Key, model, count, voiceKind, price,
            fields, Now() + QuoteTtl);
        PruneQuotes();
        _quotes[quote.Id] = quote;
        return AudioEditCallResult<AudioQuoteDto>.Ok(ToDto(quote));
    }

    // Котировка драйвера — на все варианты: каждый вариант — отдельный прогон
    private static async Task<AudioPrice> EstimateAsync(IAudioEngine engine, AudioModelInfo model, AudioRequest probe,
        int count, CancellationToken ct)
    {
        var estimate = engine is IAudioQuoter quoter
            ? await quoter.EstimateAsync(model, probe, ct)
            : FromHint(engine, model, probe);
        return new AudioPrice(estimate.Amount * count, estimate.Unit, estimate.Approx, estimate.Source,
            estimate.EtaSeconds * count, estimate.QueueLength);
    }

    // Ориентир каталога: цена за единицу × число единиц запроса; единиц не знаем — суммы нет
    private static AudioEstimate FromHint(IAudioEngine engine, AudioModelInfo model, AudioRequest probe)
    {
        if (model.PriceHint is not { } hint)
            return new AudioEstimate(null, engine.PriceUnit, true, AudioEstimateSources.Unknown);
        double? units = hint.Unit switch
        {
            AudioPriceUnits.Chars => probe.Text?.Length,
            AudioPriceUnits.Sec => probe.DurationSec,
            AudioPriceUnits.Min => probe.DurationSec / 60.0,
            _ => 1,
        };
        return new AudioEstimate(hint.Amount * units, hint.Unit, true, AudioEstimateSources.Catalog);
    }

    // ── Запуск ───────────────────────────────────────────────────────────────────

    public async Task<AudioEditCallResult<AudioJobCreatedDto>> StartAsync(
        string ownerId, AudioEditScope scope, AudioJobInput input, CancellationToken ct)
    {
        if (!_quotes.TryGetValue(input.QuoteId, out var quote)
            || quote.OwnerId != ownerId || quote.ScopeKey != scope.Key || quote.ExpiresAt < Now())
            return Fail<AudioJobCreatedDto>(AudioEditErrorCodes.QuoteNotFound, QuoteExpiredText);

        // Ровно поставщик котировки: пропал — отказ, а не сосед
        var engine = AudioCatalog.Available(_engines)
            .FirstOrDefault(e => string.Equals(e.Key, quote.Provider, StringComparison.OrdinalIgnoreCase));
        if (engine is null)
            return Fail<AudioJobCreatedDto>(AudioEditErrorCodes.ProviderUnavailable, $"Поставщик «{quote.Provider}» больше недоступен");
        if (engine.ScopeRefusal(scope) is { } refusal)
            return Fail<AudioJobCreatedDto>(AudioEditErrorCodes.ProviderUnavailable, refusal);

        Job job;
        lock (_startLock)
        {
            var active = _jobs.Values.Where(j => j.IsActive).ToList();
            if (active.Count(j => j.OwnerId == ownerId) >= MaxJobsPerOwner)
                return Fail<AudioJobCreatedDto>(AudioEditErrorCodes.TooManyJobs, OwnerLimitText);
            if (active.Count >= MaxJobsPerInstance)
                return Fail<AudioJobCreatedDto>(AudioEditErrorCodes.TooManyJobs, InstanceLimitText);
            if (IsLocalHeavy(quote) && active.Any(j => IsLocalHeavy(j.Quote)))
                return Fail<AudioJobCreatedDto>(AudioEditErrorCodes.HeavyBusy, HeavyBusyText);
            if (!_quotes.TryRemove(quote.Id, out _))
                return Fail<AudioJobCreatedDto>(AudioEditErrorCodes.QuoteNotFound, "Котировка уже использована");

            job = new Job(NewId(), ownerId, scope, quote, CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token))
            {
                CreatedAt = Now(),
                Initiator = input.Initiator,
                SpendLabel = SpendLabelOf(quote, input),
                SpendSource = engine.SpendSource,
            };
            _jobs[job.Id] = job;
        }

        // Запуск ложится в нить до старта: итог задачи обязан застать идущий запуск
        if (_threads is not null && _threads.OwnThread(ownerId, scope.Key, input.SessionId, input.ThreadId))
        {
            var sessionId = input.SessionId!.Trim();
            var threadId = input.ThreadId!.Trim();
            var thread = _threads.Store.Get(ownerId, sessionId).Threads.First(t => t.Id == threadId);
            var baseVersion = thread.Version(input.BaseVersionId)?.Id ?? thread.CurrentVersionId;
            job.ChatSessionId = sessionId;
            job.ThreadId = threadId;
            await _threads.OnLaunchedAsync(ownerId, scope.Key, sessionId, threadId, job.Id, ToDto(quote),
                input.Prompt ?? input.Text, baseVersion, input.Initiator,
                new AudioThreadSettings(quote.Mode, OpName(quote.Op), quote.Provider, quote.Model.Id,
                    quote.Fields.DeepClone().AsObject(), quote.Count), ct);
        }

        var request = new AudioRequest(quote.Op, quote.Model.Id, scope, input.Text, input.Prompt, input.Lyrics,
            input.Language, input.DurationSec, input.StartSec, input.EndSec, Merge(quote.Fields, input.Params),
            input.Source, input.Reference, input.Clips, input.VoiceModel, input.VoiceIndex, input.Seed);
        job.Completion = Task.Run(() => RunAsync(job, engine, request));

        _workspace.Sweep(Now());
        return AudioEditCallResult<AudioJobCreatedDto>.Ok(new AudioJobCreatedDto(job.Id));
    }

    private async Task RunAsync(Job job, IAudioEngine engine, AudioRequest request)
    {
        AudioResult? failure = null;
        var cancelled = false;
        var costs = new List<AudioCost?>();
        try
        {
            for (var variant = 1; variant <= job.Quote.Count; variant++)
            {
                lock (job.Gate) job.Variant = variant;
                var run = request.Seed is { } seed ? request with { Seed = seed + variant - 1 } : request;
                AudioResult result;
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
                    _log.LogWarning(ex, "Звук: драйвер {Provider} упал", engine.Key);
                    result = AudioResult.Fail(AudioOutcome.Failed, "Сбой поставщика: " + ex.Message, charged: null);
                }

                if (result.Outcome == AudioOutcome.Cancelled || job.Cts.IsCancellationRequested)
                {
                    cancelled = true;
                    if (result.RemoteId is { } remote) await CancelRemoteQuietly(engine, remote);
                    break;
                }
                if (result.Outcome != AudioOutcome.Ok || SaveVariant(job, variant, result) is not { } saved)
                {
                    failure = result.Outcome == AudioOutcome.Ok
                        ? result with { Outcome = AudioOutcome.Failed, Error = "Поставщик не вернул пригодных файлов" }
                        : result;
                    break;
                }
                OnAccepted(job);
                costs.Add(result.ActualCost);
                lock (job.Gate) job.Variants.Add(saved);
            }

            int ready;
            lock (job.Gate) ready = job.Variants.Count;
            if (!cancelled && ready > 0)
                await CompleteAsync(job, costs, failure?.Error);
            else
                await FailAsync(job, engine,
                    cancelled ? AudioResult.Fail(AudioOutcome.Cancelled, "Остановлено по запросу", charged: null)
                        : failure ?? AudioResult.Fail(AudioOutcome.Failed, "Задача не дала результата"),
                    cancelled);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Звук: не удалось завершить задачу {JobId}", job.Id);
            lock (job.Gate)
            {
                job.Status = AudioEditJobStatus.Failed;
                job.Outcome = AudioOutcome.Failed;
                job.Error = "Не удалось сохранить результат";
            }
        }
    }

    // Файлы варианта в рабочую папку; роль, которую нить не примет, отбрасывается. null — ничего годного
    private AudioJobVariantDto? SaveVariant(Job job, int variant, AudioResult result)
    {
        var files = new List<AudioVersionFile>();
        foreach (var file in result.Files)
        {
            if (!AudioFileRoles.IsValid(file.Role) || files.Any(f => f.Role == file.Role))
            {
                _log.LogWarning("Звук: файл с ролью {Role} задачи {JobId} отброшен", file.Role, job.Id);
                continue;
            }
            files.Add(new AudioVersionFile(file.Role,
                _workspace.SaveFile(job.OwnerId, job.Id, variant, file.Role, file.Bytes, file.Extension)));
        }
        return files.Count == 0 ? null : new AudioJobVariantDto(variant, files);
    }

    private async Task CompleteAsync(Job job, IReadOnlyList<AudioCost?> costs, string? partialError)
    {
        // Фактическая сумма — только если её назвали все прогоны в одной единице; иначе котировка
        var cost = costs.Count > 0 && costs.All(c => c is not null && c.Unit == costs[0]!.Unit)
            ? new AudioCost(costs.Sum(c => c!.Amount), costs[0]!.Unit)
            : EstimateCost(job.Quote);
        RecordSpend(job, cost?.Amount);
        List<AudioJobVariantDto> variants;
        lock (job.Gate)
        {
            job.Cost = cost;
            job.Outcome = AudioOutcome.Ok;
            job.Charged = true;
            job.Error = partialError;
            job.Status = AudioEditJobStatus.Completed;
            variants = [.. job.Variants];
        }
        // Версии ложатся в нить до события: фронт, перечитав нити по completed, их застаёт
        if (job is { ChatSessionId: { } sessionId, ThreadId: { } threadId })
            await _threads!.OnFinishedAsync(job.OwnerId, job.Scope.Key, sessionId, threadId, job.Id,
                AudioEditJobStatus.Completed, variants, partialError);
        await Broadcast(job.OwnerId, new AudioEditCompletedMessage(job.Id, job.Scope.Key, [.. variants.Select(v => v.Variant)],
            cost, partialError, job.ChatSessionId, job.ThreadId, job.Initiator));
    }

    private async Task FailAsync(Job job, IAudioEngine engine, AudioResult result, bool cancelled)
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

        var retry = cancelled || result.Outcome == AudioOutcome.Rejected ? null : RetryQuote(job, engine);
        List<AudioJobVariantDto> variants;
        lock (job.Gate)
        {
            job.Status = cancelled ? AudioEditJobStatus.Cancelled : AudioEditJobStatus.Failed;
            job.Outcome = result.Outcome;
            job.Charged = charged;
            job.Error = result.Error;
            if (charged != false && job.RecordedAmount is { } amount) job.Cost = new AudioCost(amount, job.Quote.Price.Unit);
            variants = [.. job.Variants];
        }
        // Отмена оставляет готовые варианты версиями
        if (job is { ChatSessionId: { } sessionId, ThreadId: { } threadId })
            await _threads!.OnFinishedAsync(job.OwnerId, job.Scope.Key, sessionId, threadId, job.Id,
                cancelled ? AudioEditJobStatus.Cancelled : AudioEditJobStatus.Failed, variants, result.Error);
        await Broadcast(job.OwnerId, new AudioEditFailedMessage(job.Id, job.Scope.Key, result.Outcome, charged, result.Error,
            retry, job.ChatSessionId, job.ThreadId, job.Initiator));
    }

    // Сосед — только предложение «Повторить через …»: другой доступный поставщик, работающий в этой
    // области, с моделью той же операции и того же вида голоса. Не запускается
    private AudioQuoteDto? RetryQuote(Job job, IAudioEngine failed)
    {
        var q = job.Quote;
        foreach (var other in AudioCatalog.Available(_engines).Where(e => e.Key != failed.Key))
        {
            if (other.ScopeRefusal(job.Scope) is not null) continue;
            if (Pick(other, q.Op, AudioCatalog.AutoModelId, q.VoiceKind) is not { } model) continue;
            var estimate = FromHint(other, model, new AudioRequest(q.Op, model.Id, job.Scope));
            var price = new AudioPrice(estimate.Amount * q.Count, estimate.Unit, estimate.Approx, estimate.Source, null, null);
            var retry = new Quote(NewId(), job.OwnerId, q.ScopeKey, q.Mode, q.Op, other.Key, model, q.Count, q.VoiceKind,
                price, q.Fields.DeepClone().AsObject(), Now() + QuoteTtl);
            _quotes[retry.Id] = retry;
            return ToDto(retry);
        }
        return null;
    }

    private async Task CancelRemoteQuietly(IAudioEngine engine, string remoteId)
    {
        try { await engine.CancelRemoteAsync(remoteId, CancellationToken.None); }
        catch (Exception ex) { _log.LogDebug(ex, "Звук: отмена у поставщика {Provider} не прошла", engine.Key); }
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
            _log.LogWarning("Звук: учёт расхода выключен, трата задачи {JobId} не записана", job.Id);
            return;
        }
        // Валюты не складываются: кредиты и рубли — в своих полях, остальное (и бесплатное — нулём) — доллары
        var unit = job.Quote.Price.Unit;
        try
        {
            _spend.Record(new SpendRecord
            {
                Timestamp = Now(),
                OwnerId = job.OwnerId,
                ProjectId = AudioEditScope.ProjectIdOf(job.Scope.Key),
                SessionId = job.ChatSessionId,
                Initiator = job.Initiator == AudioEditInitiator.Agent ? SpendInitiators.Agent : SpendInitiators.Human,
                Provider = job.Quote.Provider,
                Model = job.Quote.Model.Id,
                Source = job.SpendSource,
                CostUsd = unit is AudioPriceUnits.Credits or AudioPriceUnits.Rub ? null : amount,
                CostCredits = unit == AudioPriceUnits.Credits ? amount : null,
                CostRub = unit == AudioPriceUnits.Rub ? amount : null,
                Generations = generations,
                Label = job.SpendLabel,
            });
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Звук: не удалось записать трату задачи {JobId}", job.Id);
        }
    }

    // «qwen3-tts-1.7b · бесплатно», «fal-ai/minimax/speech-2.8-hd · 68 симв.»: модель и единица
    // тарификации. Новых полей SpendRecord не заводим — длина и единица живут в подписи
    private static string SpendLabelOf(Quote q, AudioJobInput input)
    {
        var chars = (input.Text ?? input.Lyrics)?.Length;
        var units = q.Price.Unit switch
        {
            AudioPriceUnits.Chars => chars is { } c ? $"{c} симв." : "симв.",
            AudioPriceUnits.Sec => input.DurationSec is { } s ? $"{s} с" : "с",
            AudioPriceUnits.Min => input.DurationSec is { } m ? $"{(m / 60.0).ToString("0.#", CultureInfo.InvariantCulture)} мин" : "мин",
            AudioPriceUnits.Run => $"запусков: {q.Count}",
            AudioPriceUnits.Credits => "кредиты",
            AudioPriceUnits.Rub => "руб.",
            AudioPriceUnits.Free => "бесплатно",
            var other => other,
        };
        return $"{q.Model.Id} · {units}";
    }

    // ── Чтение и отмена ──────────────────────────────────────────────────────────

    // Живая котировка владельца в области — ручке запуска нужна её операция, чтобы собрать входы
    // до запуска. Чужая, устаревшая и несуществующая неотличимы
    public AudioQuoteDto? FindQuote(string ownerId, string scopeKey, string quoteId) =>
        _quotes.TryGetValue(quoteId, out var q) && q.OwnerId == ownerId && q.ScopeKey == scopeKey && q.ExpiresAt >= Now()
            ? ToDto(q)
            : null;

    public AudioJobDto? Get(string ownerId, string scopeKey, string jobId) =>
        Find(ownerId, scopeKey, jobId) is { } job ? ToDto(job) : null;

    // Отмена: токен задачи гасит прогон драйвера (local снимает задачу из очереди сам), а id у
    // поставщика из результата отменяется CancelRemoteAsync. Готовые варианты остаются версиями
    public async Task<AudioJobDto?> CancelAsync(string ownerId, string scopeKey, string jobId, CancellationToken ct)
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

    // Синхронный IProgress: Progress<T> увёл бы отчёт в пул и перемешал бы стадии
    private sealed class JobProgress(AudioEditJobService owner, Job job, int variant) : IProgress<AudioProgress>
    {
        public void Report(AudioProgress value)
        {
            owner.OnAccepted(job);
            lock (job.Gate)
            {
                if (!job.IsActive) return;
                job.Status = value.Stage switch
                {
                    AudioStage.Running => AudioEditJobStatus.Running,
                    AudioStage.Downloading => AudioEditJobStatus.Downloading,
                    _ => AudioEditJobStatus.Queued,
                };
                job.QueuePosition = value.QueuePosition;
                job.EtaSeconds = value.EtaSeconds;
            }
            _ = owner.Broadcast(job.OwnerId, new AudioEditProgressMessage(job.Id, job.Scope.Key, value.Stage,
                value.QueuePosition, value.EtaSeconds, variant, job.Quote.Count, job.ChatSessionId, job.ThreadId, job.Initiator));
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
            _log.LogDebug(ex, "Звук: событие {Type} не доставлено", message.Type);
        }
    }

    // ── Помощники ────────────────────────────────────────────────────────────────

    // Умолчание каталога по режиму: операция режима, «Авто» у поставщика и модели, один вариант
    public static AudioModePrefs CatalogDefault(string mode) => mode switch
    {
        AudioModes.Voice => new AudioModePrefs(OpName(AudioOp.Speak), null, AudioCatalog.AutoModelId, 1, null),
        AudioModes.Music => new AudioModePrefs(OpName(AudioOp.Song), null, AudioCatalog.AutoModelId, 1, null),
        _ => new AudioModePrefs(OpName(AudioOp.Separate), null, AudioCatalog.AutoModelId, 1, null),
    };

    // Вид голоса, который операция подразумевает сама
    private static AudioVoiceKind? DefaultVoiceKind(AudioOp op) => op switch
    {
        AudioOp.CloneVoice => AudioVoiceKind.Clone,
        AudioOp.DesignVoice => AudioVoiceKind.Description,
        AudioOp.TrainVoice => AudioVoiceKind.Rvc,
        _ => null,
    };

    // Модель поставщика под операцию и вид голоса: «Авто» — первая подходящая, явная — только она
    private static AudioModelInfo? Pick(IAudioEngine engine, AudioOp op, string? modelId, AudioVoiceKind? kind)
    {
        bool Fits(AudioModelInfo m) =>
            m.DisabledReason is null && m.Caps.Ops.Contains(op) && (kind is null || m.Caps.VoiceKinds.Contains(kind.Value));
        return AudioCatalog.IsAuto(modelId)
            ? engine.Models.FirstOrDefault(Fits)
            : engine.Models.FirstOrDefault(m => string.Equals(m.Id, modelId!.Trim(), StringComparison.OrdinalIgnoreCase) && Fits(m));
    }

    private static bool IsAuto(string? provider) =>
        string.IsNullOrWhiteSpace(provider) || string.Equals(provider.Trim(), AudioCatalog.AutoModelId, StringComparison.OrdinalIgnoreCase);

    // Живой каталог поставщика: сбой обновления — не отказ котировки, остаётся прежний список
    private async Task RefreshModelsQuietly(IAudioEngine engine, CancellationToken ct)
    {
        try { await engine.RefreshModelsAsync(ct); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogDebug(ex, "Звук: каталог поставщика {Provider} не обновился", engine.Key);
        }
    }

    private static bool SafeEnabled(IAudioEngine engine)
    {
        try { return engine.Enabled; }
        catch { return false; }
    }

    private static bool IsLocalHeavy(Quote q) => q.Heavy && q.Provider == Engines.LocalAudioEngine.ProviderKey;

    public static string OpName(AudioOp op) => JsonNamingPolicy.CamelCase.ConvertName(op.ToString());

    private static bool TryParseOp(string? name, out AudioOp op)
    {
        op = default;
        return !string.IsNullOrWhiteSpace(name) && !int.TryParse(name, out _)
            && Enum.TryParse(name.Trim(), ignoreCase: true, out op) && Enum.IsDefined(op);
    }

    private static JsonObject Merge(params JsonObject?[] layers)
    {
        var merged = new JsonObject();
        foreach (var layer in layers)
            if (layer is not null)
                foreach (var (key, value) in layer)
                    merged[key] = value?.DeepClone();
        return merged;
    }

    private static AudioCost? EstimateCost(Quote q) => q.Price.Amount is { } a ? new AudioCost(a, q.Price.Unit) : null;

    private static AudioQuoteDto ToDto(Quote q) =>
        new(q.Id, q.Mode, q.Op, q.Provider, q.Model.Id, q.Count, q.VoiceKind, q.Price, q.License, q.Heavy, q.ExpiresAt);

    private static AudioJobDto ToDto(Job j)
    {
        lock (j.Gate)
            return new AudioJobDto(j.Id, j.Scope.Key, j.Status, j.Quote.Provider, j.Quote.Model.Id, j.Quote.Op, j.Quote.Count,
                [.. j.Variants], j.Cost, j.Outcome, j.Charged, j.Error, j.QueuePosition, j.EtaSeconds, j.CreatedAt,
                j.ChatSessionId, j.ThreadId, j.Initiator, j.Quote.License);
    }

    private void PruneQuotes()
    {
        var now = Now();
        foreach (var (id, q) in _quotes)
            if (q.ExpiresAt < now) _quotes.TryRemove(id, out _);
    }

    private DateTime Now() => _time.GetUtcNow().UtcDateTime;

    private static string NewId() => Guid.NewGuid().ToString("N");

    private static AudioEditCallResult<T> Fail<T>(string code, string error) => AudioEditCallResult<T>.Fail(code, error);
}
