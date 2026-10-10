using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using ClaudeHomeServer.Services.ImageEditor;

namespace ClaudeHomeServer.Services.Images.LocalMedia;

public sealed partial class LocalMediaService
{
    // Шаги и скорость обучения пресета standard (ADR-026 §3): 2000 шагов, ≈2,35 с на шаг
    public const int LoraTrainSteps = 2000;
    public const double LoraTrainSecondsPerStep = 2.35;

    // Полное время обучения по пресету: шаги × время шага + старт (LocalMediaOptions.LoraTrainStartSeconds);
    // неизвестный пресет — null. Пробные картинки ai-toolkit в продукте выключены (ADR-026)
    public static int? LoraTrainEta(string preset, int startSeconds) =>
        preset == LoraTrainPresets.Standard ? LoraTrainRemaining(0, LoraTrainSteps) + startSeconds : null;

    // Остаток по известному шагу
    public static int LoraTrainRemaining(int step, int totalSteps) =>
        (int)Math.Ceiling(Math.Max(0, totalSteps - step) * LoraTrainSecondsPerStep);

    public const int LoraMinPhotos = 20;
    public const int LoraMaxPhotos = 50;

    // Прогресс шагов (ADR-026 §4): не больше одного слушателя WebSocket на задачу; шаг живёт в памяти и в стор не пишется
    private readonly Lock _loraProgressLock = new();
    private readonly Dictionary<string, CancellationTokenSource> _loraListeners = [];
    private readonly Dictionary<string, int> _loraSteps = [];

    // Слушатель прогресса; тесты подменяют сокет ComfyUI
    internal Func<string, Action<double>, CancellationToken, Task>? LoraProgressListener { get; set; }

    public bool LoraTrainEnabled => Options is { Enabled: true, LoraTrainEnabled: true };

    // Идущее на инстансе обучение (любого владельца)
    public LoraTrainBusy? LoraBusy(string ownerId) =>
        store.ListByOp(LocalMediaOps.LoraTrain).FirstOrDefault(j => !LocalMediaStatuses.IsTerminal(j.Status)) is { } job
            ? BusyOf(job, ownerId)
            : null;

    private static LoraTrainBusy BusyOf(LocalMediaJob job, string ownerId) =>
        new(job.EtaSeconds is { } eta ? (job.StartedAt ?? job.CreatedAt).AddSeconds(eta) : null, job.OwnerId == ownerId, job.Id);

    public async Task<LoraTrainStartResult> StartLoraTrainAsync(LoraTrainStart request, CancellationToken ct)
    {
        var options = Options;
        if (!options.Enabled || !options.LoraTrainEnabled)
            return LoraRefusal(LoraTrainErrorCode.Disabled, "Обучение LoRA выключено на этом сервере.");
        if (!LoraTrainPresets.All.Contains(request.Preset))
            return LoraRefusal(LoraTrainErrorCode.BadPreset, "Неизвестный пресет обучения.");
        if (!LoraTrigger.IsValid(request.Trigger))
            return LoraRefusal(LoraTrainErrorCode.BadTrigger, "Недопустимый триггер.");
        if (!Guid.TryParse(request.OwnerId, out _) || string.IsNullOrWhiteSpace(request.ProjectId)
            || !LoraNamespace.IsValidSlug(request.Slug) || !LoraNamespace.IsValidRunId(request.RunId))
            return LoraRefusal(LoraTrainErrorCode.BadRequest, "Недопустимые владелец, персонаж или идентификатор прогона.");
        if (request.Photos.Count < LoraMinPhotos)
            return LoraRefusal(LoraTrainErrorCode.TooFewPhotos, $"Для обучения нужно не меньше {LoraMinPhotos} фото.");
        if (request.Photos.Count > LoraMaxPhotos
            || request.Photos.Any(p => !ComfyWorkflows.IsLoraPhotoName(p.Name))
            || request.Photos.Select(p => p.Name).Distinct(StringComparer.Ordinal).Count() != request.Photos.Count)
            return LoraRefusal(LoraTrainErrorCode.BadRequest, $"Фото: от {LoraMinPhotos} до {LoraMaxPhotos}, имена — хеши без повторов.");

        await _submitGate.WaitAsync(ct);
        try
        {
            if (store.ActiveLoraTrainCount(request.OwnerId) > 0)
                return LoraRefusal(LoraTrainErrorCode.OwnerBusy, "У тебя уже идёт обучение LoRA.", LoraBusy(request.OwnerId));
            if (store.ActiveLoraTrainCount() > 0)
                return LoraRefusal(LoraTrainErrorCode.GpuBusy, "Видеокарта занята обучением другого пользователя.", LoraBusy(request.OwnerId));
            if (store.ActiveCount(request.OwnerId) >= options.MaxQueuedPerOwner)
                return LoraRefusal(LoraTrainErrorCode.QueueFull, $"У тебя уже {options.MaxQueuedPerOwner} незавершённые локальные задачи.");

            ComfyQueueState queue;
            try
            {
                queue = await comfy.GetQueueAsync(ct);
            }
            catch (ComfyException ex)
            {
                return LoraRefusal(LoraTrainErrorCode.Unavailable, ex.Message);
            }
            if (queue.Length >= options.MaxComfyQueue)
                return LoraRefusal(LoraTrainErrorCode.QueueFull, $"Очередь локальной GPU занята ({queue.Length} задач).");

            var jobId = JobIdPrefix + Guid.NewGuid().ToString("N");
            var folder = LoraNamespace.RunInputFolder(request.OwnerId, request.RunId);
            var uploaded = new List<string>();
            try
            {
                foreach (var photo in request.Photos)
                    uploaded.Add(await comfy.UploadInputAsync(await File.ReadAllBytesAsync(photo.HostPath, ct), photo.Name, folder, ct));
            }
            catch (ComfyException ex)
            {
                await CleanupPartialUploadAsync(request, uploaded.Count, ct);
                return LoraRefusal(LoraTrainErrorCode.Unavailable, ex.Message);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                log.LogError(ex, "Обучающее фото прогона {Run} не прочиталось с диска", request.RunId);
                await CleanupPartialUploadAsync(request, uploaded.Count, ct);
                return LoraRefusal(LoraTrainErrorCode.Unavailable, "Не удалось прочитать обучающее фото на сервере.");
            }

            var eta = LoraTrainEta(request.Preset, options.LoraTrainStartSeconds);
            var job = new LocalMediaJob
            {
                Id = jobId,
                OwnerId = request.OwnerId,
                ProjectId = request.ProjectId,
                Op = LocalMediaOps.LoraTrain,
                Heavy = true,
                EtaSeconds = eta,
                LoraSlug = request.Slug,
                LoraRunId = request.RunId,
                LoraPreset = request.Preset,
            };
            try
            {
                job.PromptId = (await comfy.QueuePromptAsync(ComfyWorkflows.LoraTrain(request.OwnerId, request.Slug,
                    request.RunId, request.Trigger, request.Preset, uploaded, jobId), ct)).PromptId;
            }
            catch (ComfyException ex)
            {
                log.LogWarning("ComfyUI не принял обучение LoRA: {Error}", ex.Message);
                return LoraRefusal(LoraTrainErrorCode.Unavailable, ex.Message);
            }

            store.Add(job);
            return new LoraTrainStartResult(jobId, queue.Length, eta);
        }
        finally
        {
            _submitGate.Release();
        }
    }

    // Часть фото уже на GPU, а записи версии ещё нет: lora_delete чистит и in_dir прогона (ADR-026 §6.4),
    // так что ставим его сами. Не вышло (ComfyUI лежит) — остаток подберёт только ручная уборка
    private async Task CleanupPartialUploadAsync(LoraTrainStart request, int uploadedCount, CancellationToken ct)
    {
        if (uploadedCount == 0) return;
        if (await QueueLoraDeleteAsync(request.OwnerId, request.ProjectId, request.Slug, request.RunId, ct) is null)
            log.LogWarning("Не удалось поставить уборку {Count} загруженных фото прогона {Run}", uploadedCount, request.RunId);
    }

    // Под _submitGate вызывающего. Удаление — лёгкая задача воркера (без GPU), в LoraBusy не учитывается
    private async Task<LocalMediaJob?> QueueLoraDeleteAsync(string ownerId, string projectId, string slug, string runId, CancellationToken ct)
    {
        var job = new LocalMediaJob
        {
            Id = JobIdPrefix + Guid.NewGuid().ToString("N"),
            OwnerId = ownerId,
            ProjectId = projectId,
            Op = LocalMediaOps.LoraDelete,
            LoraSlug = slug,
            LoraRunId = runId,
        };
        try
        {
            job.PromptId = (await comfy.QueuePromptAsync(ComfyWorkflows.LoraDelete(ownerId, slug, runId, job.Id), ct)).PromptId;
        }
        catch (ComfyException ex)
        {
            log.LogWarning("ComfyUI не принял удаление прогона LoRA: {Error}", ex.Message);
            return null;
        }
        store.Add(job);
        return job;
    }

    private static LoraTrainStartResult LoraRefusal(LoraTrainErrorCode code, string message, LoraTrainBusy? busy = null) =>
        new(null, null, null, code, message, busy);

    // Чужая задача неотличима от несуществующей
    public async Task<LoraTrainState?> GetLoraTrainAsync(string ownerId, string jobId, CancellationToken ct)
    {
        var job = store.Get(jobId, ownerId);
        if (job is null || job.Op != LocalMediaOps.LoraTrain) return null;
        var view = await RefreshAsync(job, ct);
        return LoraState(view.Job, view.Position);
    }

    public IReadOnlyList<LoraTrainState> ListLoraTrain(string? ownerId) =>
        [.. store.ListByOp(LocalMediaOps.LoraTrain, ownerId).Select(j => LoraState(j, null))];

    public async Task<LoraCancelResult> CancelLoraTrainAsync(string ownerId, string jobId, bool asAdmin, CancellationToken ct)
    {
        var job = store.ListByOp(LocalMediaOps.LoraTrain, asAdmin ? null : ownerId).FirstOrDefault(j => j.Id == jobId);
        if (job is null) return LoraCancelResult.NotFound;
        if (LocalMediaStatuses.IsTerminal(job.Status)) return LoraCancelResult.AlreadyFinished;

        // Статус могли не обновить с прошлого опроса, а ждущая задача могла стать идущей между опросом и снятием.
        // Поэтому всегда оба шага по СВОЕМУ prompt_id: снятие из очереди, затем прерывание (для не идущей — no-op).
        // Так гонка Queued→Running закрыта без _submitGate: постановку отмена не затрагивает
        var view = await RefreshAsync(job, ct);
        if (LocalMediaStatuses.IsTerminal(view.Job.Status)) return LoraCancelResult.AlreadyFinished;
        if (!await TryStopLoraPromptAsync(job, ct))
        {
            // Прогон на карте мог продолжиться: терминальный статус освободил бы карту для второго обучения.
            // Остаёмся активными, отмену довершит сторож; вызывающему это тот же «принято»
            store.Update(job.Id, job.OwnerId, j =>
            {
                j.CancelRequested = true;
                j.CancelRequestedAt ??= DateTime.UtcNow;
            });
            return LoraCancelResult.Cancelled;
        }

        MarkLoraCancelled(job);
        return LoraCancelResult.Cancelled;
    }

    // true — ComfyUI принял оба запроса (для отсутствующего prompt_id они no-op, это и есть «его там нет»)
    private async Task<bool> TryStopLoraPromptAsync(LocalMediaJob job, CancellationToken ct)
    {
        try
        {
            await comfy.DeletePendingAsync(job.PromptId, ct);
            await comfy.InterruptAsync(job.PromptId, ct);
            return true;
        }
        catch (ComfyException ex)
        {
            log.LogWarning("ComfyUI не подтвердил отмену обучения LoRA {Job}: {Error}", job.Id, ex.Message);
            return false;
        }
    }

    private void MarkLoraCancelled(LocalMediaJob job)
    {
        store.Update(job.Id, job.OwnerId, j =>
        {
            j.Status = LocalMediaStatuses.Failed;
            j.Cancelled = true;
            j.FinishedAt = DateTime.UtcNow;
        });
        StopLoraProgress(job.Id);
    }

    // Сторож зависшего обучения: потолок узла 240 мин + запас (LoraTrainTimeoutMinutes); иначе Running держит карту вечно
    public async Task<int> SweepLoraTimeoutsAsync(CancellationToken ct)
    {
        var now = (time ?? TimeProvider.System).GetUtcNow().UtcDateTime;
        var limit = TimeSpan.FromMinutes(Options.LoraTrainTimeoutMinutes);
        var swept = 0;
        foreach (var job in store.ListByOp(LocalMediaOps.LoraTrain))
        {
            if (LocalMediaStatuses.IsTerminal(job.Status)) continue;
            if (job.CancelRequested)
            {
                // Повтор отмены, которую ComfyUI не подтвердил при запросе
                if (await TryStopLoraPromptAsync(job, ct) && store.Get(job.Id, job.OwnerId) is { } pending
                    && !LocalMediaStatuses.IsTerminal(pending.Status))
                {
                    MarkLoraCancelled(pending);
                    swept++;
                }
                continue;
            }
            // Считаем от старта выполнения: задача могла долго стоять в очереди за чужими прогонами
            if (job.StartedAt is not { } startedAt || now - startedAt <= limit) continue;
            await _collectGate.WaitAsync(ct);
            try
            {
                // Успели завершить, пока ждали гейт, — не трогаем
                var current = store.Get(job.Id, job.OwnerId) ?? job;
                if (LocalMediaStatuses.IsTerminal(current.Status)) continue;
                try
                {
                    await comfy.DeletePendingAsync(current.PromptId, ct);
                    await comfy.InterruptAsync(current.PromptId, ct);
                }
                catch (ComfyException ex)
                {
                    log.LogWarning("ComfyUI не подтвердил прерывание зависшего обучения LoRA {Job}: {Error}", current.Id, ex.Message);
                }
                Fail(current, $"Обучение превысило потолок {Options.LoraTrainTimeoutMinutes} мин (timeout).", code: LoraErrorCodes.Timeout);
                StopLoraProgress(current.Id);
                swept++;
            }
            finally
            {
                _collectGate.Release();
            }
        }
        return swept;
    }

    // Идемпотентно: первый вызов ставит lora_delete, следующие отдают исход этой задачи
    public async Task<LoraDeleteState> DeleteLoraRunAsync(string ownerId, string projectId, string slug, string runId, CancellationToken ct)
    {
        if (!Guid.TryParse(ownerId, out _) || !LoraNamespace.IsValidSlug(slug) || !LoraNamespace.IsValidRunId(runId))
            return LoraDeleteState.Failed;
        if (!Options.Enabled) return LoraDeleteState.Failed;

        await _submitGate.WaitAsync(ct);
        try
        {
            // Последняя задача этого прогона; упавшая не терминальна для уборки: ставим новую (ADR-026 §6.4)
            var existing = store.ListByOp(LocalMediaOps.LoraDelete, ownerId)
                .Where(j => j.LoraSlug == slug && j.LoraRunId == runId)
                .OrderBy(j => j.CreatedAt).LastOrDefault();
            if (existing is not null)
            {
                var current = (await RefreshAsync(existing, ct)).Job;
                if (current.Status != LocalMediaStatuses.Failed)
                    return current.Status == LocalMediaStatuses.Completed ? LoraDeleteState.Deleted : LoraDeleteState.Pending;
            }

            return await QueueLoraDeleteAsync(ownerId, projectId, slug, runId, ct) is null
                ? LoraDeleteState.Failed
                : LoraDeleteState.Pending;
        }
        finally
        {
            _submitGate.Release();
        }
    }

    // Сбор lora_train/lora_delete: проект не резолвится, файлов не качаем, усыновителей не зовём
    private async Task<LocalMediaJob> CollectLoraAsync(LocalMediaJob job, ComfyHistoryEntry history, CancellationToken ct)
    {
        await _collectGate.WaitAsync(ct);
        try
        {
            var current = store.Get(job.Id, job.OwnerId) ?? job;
            if (LocalMediaStatuses.IsTerminal(current.Status)) return current;

            if (current.Op == LocalMediaOps.LoraDelete)
                return Complete(current, history, null, null, null);

            var expected = LoraNamespace.RunLoraFile(current.OwnerId, current.LoraSlug ?? "", current.LoraRunId ?? "");
            var name = history.CcsLora is { Count: > 0 } list ? (list[0] as JsonObject)?["name"]?.GetValue<string>() : null;
            if (!string.Equals(name, expected, StringComparison.Ordinal))
                return Fail(current, "Воркер вернул неожиданное имя весов LoRA.", history, LoraErrorCodes.BadResult);

            var stats = history.CcsStats is { Count: > 0 } s ? s[0] as JsonObject : null;
            var kept = stats?["photos_kept"] is JsonValue k && k.TryGetValue<int>(out var n) ? n : (int?)null;
            var rejected = (stats?["rejected"] as JsonArray)?.OfType<JsonObject>()
                .Select(r => new LoraPhotoRejectionDto
                {
                    Name = r["name"]?.GetValue<string>() ?? "",
                    Reason = r["reason"]?.GetValue<string>() ?? "",
                }).ToList();
            return Complete(current, history, expected, kept, rejected);
        }
        finally
        {
            _collectGate.Release();
        }
    }

    private LocalMediaJob Complete(LocalMediaJob job, ComfyHistoryEntry history, string? file, int? kept,
        List<LoraPhotoRejectionDto>? rejected)
    {
        var done = store.Update(job.Id, job.OwnerId, j =>
        {
            j.Status = LocalMediaStatuses.Completed;
            j.FinishedAt = DateTime.UtcNow;
            j.LoraFile = file;
            j.LoraPhotosKept = kept;
            j.LoraRejected = rejected;
            ApplyRunStats(j, history);
        }) ?? job;
        StopLoraProgress(job.Id);
        return done;
    }

    private LoraTrainState LoraState(LocalMediaJob job, int? position)
    {
        var status = job.Cancelled ? LoraTrainStatus.Cancelled : job.Status switch
        {
            LocalMediaStatuses.Running => LoraTrainStatus.Running,
            LocalMediaStatuses.Completed => LoraTrainStatus.Completed,
            LocalMediaStatuses.Failed => LoraTrainStatus.Failed,
            _ => LoraTrainStatus.Queued,
        };

        int? step = null;
        if (status == LoraTrainStatus.Running) step = EnsureLoraProgress(job);
        else StopLoraProgress(job.Id);

        int? eta = status switch
        {
            LoraTrainStatus.Running when step is { } s => LoraTrainRemaining(s, LoraTrainSteps),
            LoraTrainStatus.Running when job.StartedAt is { } started && job.EtaSeconds is { } total =>
                Math.Max(60, (int)(started.AddSeconds(total) - DateTime.UtcNow).TotalSeconds),
            LoraTrainStatus.Running or LoraTrainStatus.Queued => job.EtaSeconds,
            _ => null,
        };

        return new LoraTrainState(job.Id, job.OwnerId, job.ProjectId, job.LoraSlug ?? "", job.LoraRunId ?? "",
            job.LoraPreset ?? LoraTrainPresets.Standard, status, status == LoraTrainStatus.Queued ? position : null,
            step, LoraTrainSteps, eta, job.CreatedAt, job.StartedAt, job.FinishedAt,
            status == LoraTrainStatus.Completed ? job.LoraFile : null,
            status == LoraTrainStatus.Failed ? new LoraTrainFailure(job.ErrorCode ?? LoraErrorCodes.WorkerError, job.Error ?? "") : null,
            job.LoraPhotosKept,
            job.LoraRejected?.Select(r => new LoraPhotoRejection(r.Name, r.Reason)).ToList());
    }

    // Запускает слушателя, если его нет; возвращает последний известный шаг
    private int? EnsureLoraProgress(LocalMediaJob job)
    {
        lock (_loraProgressLock)
        {
            if (!_loraListeners.ContainsKey(job.Id) && job.PromptId.Length > 0)
            {
                var cts = new CancellationTokenSource();
                _loraListeners[job.Id] = cts;
                var listen = LoraProgressListener ?? comfy.ListenProgressAsync;
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await listen(job.PromptId, f =>
                        {
                            lock (_loraProgressLock) _loraSteps[job.Id] = (int)Math.Round(f * LoraTrainSteps);
                        }, cts.Token);
                    }
                    catch (Exception ex) when (ex is not OutOfMemoryException)
                    {
                        // Прогресс необязателен: сорвавшегося слушателя поднимет следующий опрос
                    }
                    finally
                    {
                        lock (_loraProgressLock)
                            if (_loraListeners.TryGetValue(job.Id, out var own) && own == cts) _loraListeners.Remove(job.Id);
                    }
                });
            }
            return _loraSteps.TryGetValue(job.Id, out var step) ? step : null;
        }
    }

    private void StopLoraProgress(string jobId)
    {
        lock (_loraProgressLock)
        {
            if (_loraListeners.Remove(jobId, out var cts)) cts.Cancel();
            _loraSteps.Remove(jobId);
        }
    }
}

// Коды ошибок прогона (ADR-026 §6.3). Отмена кодом не считается
public static class LoraErrorCodes
{
    public const string TooFewFaces = "too_few_faces";
    public const string Oom = "oom";
    public const string Timeout = "timeout";
    public const string ComfyRestart = "comfy_restart";
    public const string BadResult = "bad_result";
    public const string WorkerError = "worker_error";

    private static readonly Regex Marker = new(@"ccs_error=([a-z_]+)", RegexOptions.Compiled);

    public static string Classify(string? error)
    {
        if (error is null) return WorkerError;
        if (Marker.Match(error) is { Success: true } m && m.Groups[1].Value == TooFewFaces) return TooFewFaces;
        if (error.Contains("out of memory", StringComparison.OrdinalIgnoreCase)) return Oom;
        if (error.Contains("timeout", StringComparison.OrdinalIgnoreCase)
            || error.Contains("timed out", StringComparison.OrdinalIgnoreCase)) return Timeout;
        return WorkerError;
    }
}
