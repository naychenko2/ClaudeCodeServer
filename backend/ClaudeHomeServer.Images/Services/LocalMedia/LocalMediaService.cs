using System.Text.Json.Nodes;
using ClaudeHomeServer.Services.ImageEditor;
using ClaudeHomeServer.Services.ImageEditor.Versioning;
using ClaudeHomeServer.Services.Images.Editing;

namespace ClaudeHomeServer.Services.Images.LocalMedia;

// Заявка на генерацию. Images — пути файлов проекта или job_id прошлых задач
public sealed record LocalMediaRequest(
    string OwnerId,
    string ProjectId,
    string? SessionId,
    string Op,
    string? Prompt = null,
    string? NegativePrompt = null,
    string? Aspect = null,
    int? Steps = null,
    int? Count = null,
    long? Seed = null,
    IReadOnlyList<string>? Images = null,
    string? VideoSize = null,
    int? DurationSeconds = null);

// Задача и её живое положение: Position — сколько задач ComfyUI впереди (0 — идёт),
// Warning — временный сбой опроса (ComfyUI недоступен), задача при этом жива
public sealed record LocalMediaJobView(LocalMediaJob Job, int? Position = null, int? EtaSeconds = null,
    string? Warning = null);

public sealed record LocalMediaCallResult(LocalMediaJobView? View, string? Error)
{
    public static LocalMediaCallResult Fail(string error) => new(null, error);
}

// Фасад локальной генерации: проверки, загрузка входов, постановка графа, опрос и
// сбор результата в папку проекта. Граф — только из ComfyWorkflows
public sealed class LocalMediaService(
    ComfyClient comfy,
    LocalMediaJobStore store,
    ILocalMediaProjectAccess projects,
    IConfiguration config,
    ILogger<LocalMediaService> log)
{
    // Папка результатов в проекте: скрыта из дерева, из синка знаний и из git
    public const string ResultsFolder = ".cc-attachments/local-media";
    public const int MaxInputBytes = 30 * 1024 * 1024;
    public const int MaxWaitSeconds = 15;
    public const int MaxWaitJobs = 12;
    private const string JobIdPrefix = "lm_";

    // Проверка лимита и постановка — атомарно: две параллельные заявки не обходят потолок
    private readonly SemaphoreSlim _submitGate = new(1, 1);
    // Сбор результата: опрос агента и коллектор не пишут один файл дважды
    private readonly SemaphoreSlim _collectGate = new(1, 1);

    public LocalMediaOptions Options => LocalMediaOptions.Read(config);

    public bool Enabled => Options.Enabled;

    public static bool LooksLikeJobId(string value) =>
        value.StartsWith(JobIdPrefix, StringComparison.Ordinal) && value.Length == JobIdPrefix.Length + 32
        && value[JobIdPrefix.Length..].All(char.IsAsciiHexDigitLower);

    public async Task<LocalMediaCallResult> SubmitAsync(LocalMediaRequest request, CancellationToken ct)
    {
        var options = Options;
        if (!options.Enabled) return LocalMediaCallResult.Fail("Локальная генерация выключена на этом сервере.");
        if (!LocalMediaOps.IsKnown(request.Op)) return LocalMediaCallResult.Fail("Неизвестная операция.");

        var prompt = (request.Prompt ?? "").Trim();
        if (request.Op != LocalMediaOps.FaceDetail && prompt.Length == 0)
            return LocalMediaCallResult.Fail("Нужен prompt — описание того, что сгенерировать.");
        if (prompt.Length > ComfyWorkflows.MaxPromptLength
            || (request.NegativePrompt?.Length ?? 0) > ComfyWorkflows.MaxPromptLength)
            return LocalMediaCallResult.Fail($"Промпт длиннее {ComfyWorkflows.MaxPromptLength} символов.");

        var root = projects.ResolveRoot(request.OwnerId, request.ProjectId);
        if (root is null)
            return LocalMediaCallResult.Fail("Проект чата недоступен: локальная генерация пишет результат в папку проекта на сервере.");

        var jobId = JobIdPrefix + Guid.NewGuid().ToString("N");
        var seed = request.Seed is >= 0 ? request.Seed.Value : Random.Shared.NextInt64(1, 1L << 50);

        await _submitGate.WaitAsync(ct);
        try
        {
            if (store.ActiveCount(request.OwnerId) >= options.MaxQueuedPerOwner)
                return LocalMediaCallResult.Fail($"У тебя уже {options.MaxQueuedPerOwner} незавершённые локальные задачи — "
                    + "дождись их (local_jobs_wait) и повтори.");

            ComfyQueueState queue;
            try
            {
                queue = await comfy.GetQueueAsync(ct);
            }
            catch (ComfyException ex)
            {
                return LocalMediaCallResult.Fail($"{ex.Message}. Попробуй позже или сгенерируй в облаке.");
            }
            if (queue.Length >= options.MaxComfyQueue)
                return LocalMediaCallResult.Fail($"Очередь локальной GPU занята ({queue.Length} задач). "
                    + "Попробуй позже или сгенерируй в облаке.");

            var job = new LocalMediaJob
            {
                Id = jobId,
                OwnerId = request.OwnerId,
                ProjectId = request.ProjectId,
                SessionId = request.SessionId,
                Op = request.Op,
                Seed = seed,
            };
            var prefix = $"{ComfyWorkflows.OutputFolder}/{jobId}";

            JsonObject graph;
            int eta;
            try
            {
                (graph, eta) = await BuildGraphAsync(request, job, root, prompt, seed, prefix, options, ct);
            }
            catch (LocalMediaInputException ex)
            {
                return LocalMediaCallResult.Fail(ex.Message);
            }
            catch (ComfyException ex)
            {
                return LocalMediaCallResult.Fail($"{ex.Message}. Попробуй позже или сгенерируй в облаке.");
            }

            try
            {
                job.PromptId = (await comfy.QueuePromptAsync(graph, ct)).PromptId;
            }
            catch (ComfyException ex)
            {
                log.LogWarning("ComfyUI не принял задачу {Op}: {Error}", request.Op, ex.Message);
                return LocalMediaCallResult.Fail(ex.Message);
            }

            store.Add(job);
            return new LocalMediaCallResult(new LocalMediaJobView(job, queue.Length, eta), null);
        }
        finally
        {
            _submitGate.Release();
        }
    }

    private async Task<(JsonObject Graph, int EtaSeconds)> BuildGraphAsync(LocalMediaRequest request,
        LocalMediaJob job, string root, string prompt, long seed, string prefix, LocalMediaOptions options,
        CancellationToken ct)
    {
        var images = request.Images ?? [];
        switch (request.Op)
        {
            case LocalMediaOps.GenerateImage:
            {
                var size = AspectSize(request.Aspect ?? "1:1");
                var steps = Math.Clamp(request.Steps ?? ComfyWorkflows.DefaultSteps, ComfyWorkflows.MinSteps, ComfyWorkflows.MaxSteps);
                var count = Math.Clamp(request.Count ?? 1, 1, ComfyWorkflows.MaxCount);
                var graph = ComfyWorkflows.GenerateImage(prompt, request.NegativePrompt?.Trim() ?? "",
                    size.Width, size.Height, seed, steps, count, prefix);
                return (graph, 15 + (int)Math.Ceiling(steps * 1.2 * count));
            }
            case LocalMediaOps.EditImage:
            {
                if (images.Count is < 1 or > ComfyWorkflows.MaxEditImages)
                    throw new LocalMediaInputException("Для правки нужно от 1 до 16 картинок (images): первая — основная.");
                var size = string.IsNullOrWhiteSpace(request.Aspect) ? ((int, int)?)null : AspectSize(request.Aspect);
                var names = new List<string>();
                for (var k = 0; k < images.Count; k++)
                    names.Add(await UploadInputAsync(request, root, images[k], $"{job.Id}-in{k + 1}", ct));
                return (ComfyWorkflows.EditImage(prompt, names, size, seed, prefix), 60 + 10 * (images.Count - 1));
            }
            case LocalMediaOps.FaceDetail:
            {
                if (images.Count != 1) throw new LocalMediaInputException("Нужна ровно одна картинка (image).");
                var name = await UploadInputAsync(request, root, images[0], $"{job.Id}-in1", ct);
                return (ComfyWorkflows.FaceDetail(name, seed, prefix), 25);
            }
            case LocalMediaOps.ImageToVideo:
            {
                if (images.Count != 1) throw new LocalMediaInputException("Нужна ровно одна картинка — первый кадр (image).");
                var seconds = request.DurationSeconds ?? 5;
                if (seconds < 1 || seconds > options.MaxVideoSeconds)
                    throw new LocalMediaInputException($"Длительность — от 1 до {options.MaxVideoSeconds} секунд.");
                var sizeKey = string.IsNullOrWhiteSpace(request.VideoSize) ? "full" : request.VideoSize.Trim();
                if (!ComfyWorkflows.VideoSizes.TryGetValue(sizeKey, out var size))
                    throw new LocalMediaInputException("size — full (1344×768) или half (864×480).");

                var (bytes, _) = ReadInput(request, root, images[0]);
                // Портретный кадр — портретное видео: стороны переставляются, а не кадр режется
                if (ImageDimensions.Read(bytes) is { } dims && dims.Height > dims.Width)
                    size = (size.Height, size.Width);
                var name = await comfy.UploadImageAsync(bytes, $"{job.Id}-in1{Extension(bytes)}", ct);

                job.Width = size.Width;
                job.Height = size.Height;
                job.DurationSeconds = seconds;
                var eta = seconds <= 5 ? 110 : 110 + (seconds - 5) * 60;
                return (ComfyWorkflows.ImageToVideo(name, prompt, size.Width, size.Height,
                    ComfyWorkflows.FramesFor(seconds), seed, prefix), eta);
            }
            default:
                throw new LocalMediaInputException("Неизвестная операция.");
        }
    }

    private static (int Width, int Height) AspectSize(string aspect) =>
        ComfyWorkflows.ImageSizes.TryGetValue(aspect.Trim(), out var size)
            ? size
            : throw new LocalMediaInputException("aspect — одно из: " + string.Join(", ", ComfyWorkflows.ImageSizes.Keys) + ".");

    private async Task<string> UploadInputAsync(LocalMediaRequest request, string root, string reference,
        string stem, CancellationToken ct)
    {
        var (bytes, _) = ReadInput(request, root, reference);
        return await comfy.UploadImageAsync(bytes, stem + Extension(bytes), ct);
    }

    private static string Extension(byte[] bytes) => ImageFormatSniffer.DetectExtension(bytes) ?? ".png";

    // Вход — путь файла проекта или job_id прошлой задачи этого же владельца и проекта.
    // Только из проекта чата: SafePath + запрет символических ссылок (ProjectLinkGuard)
    private (byte[] Bytes, string Path) ReadInput(LocalMediaRequest request, string root, string reference)
    {
        var value = (reference ?? "").Trim();
        if (value.Length == 0) throw new LocalMediaInputException("Пустая ссылка на картинку.");

        string relative;
        if (LooksLikeJobId(value))
        {
            var source = store.Get(value, request.OwnerId);
            if (source is null || source.ProjectId != request.ProjectId)
                throw new LocalMediaInputException($"Задача {value} не найдена.");
            var output = source.Outputs.FirstOrDefault(o => o.ContentType.StartsWith("image/", StringComparison.Ordinal));
            if (source.Status != LocalMediaStatuses.Completed || output is null)
                throw new LocalMediaInputException($"У задачи {value} нет готовой картинки.");
            relative = output.Path;
        }
        else
        {
            relative = value.Replace('\\', '/');
            while (relative.StartsWith("./", StringComparison.Ordinal)) relative = relative[2..];
        }

        var full = ProjectLinkGuard.ResolveInside(root, relative)
            ?? throw new LocalMediaInputException($"Путь «{value}» вне папки проекта.");
        var info = new FileInfo(full);
        if (!info.Exists) throw new LocalMediaInputException($"Файл «{value}» не найден в проекте.");
        if (info.Length > MaxInputBytes) throw new LocalMediaInputException($"Файл «{value}» больше 30 МБ.");
        var bytes = File.ReadAllBytes(full);
        if (ImageFormatSniffer.DetectExtension(bytes) is null)
            throw new LocalMediaInputException($"Файл «{value}» — не картинка (нужен PNG, JPEG или WebP).");
        return (bytes, relative);
    }

    // Длина общей очереди ComfyUI; null — ComfyUI недоступен
    public async Task<int?> QueueLengthAsync(CancellationToken ct)
    {
        try
        {
            return (await comfy.GetQueueAsync(ct)).Length;
        }
        catch (ComfyException)
        {
            return null;
        }
    }

    // Статус с опросом ComfyUI; null — задачи нет или она чужая
    public async Task<LocalMediaJobView?> GetAsync(string ownerId, string jobId, CancellationToken ct)
    {
        var job = store.Get(jobId, ownerId);
        return job is null ? null : await RefreshAsync(job, ct);
    }

    // Ожидание нескольких задач: до готовности всех или до таймаута (не больше 15 с —
    // вызов инструмента не должен висеть). Чужие id возвращаются отдельно как «не найдены»
    public async Task<(IReadOnlyList<LocalMediaJobView> Jobs, IReadOnlyList<string> Missing)> WaitAsync(
        string ownerId, IReadOnlyList<string> jobIds, int timeoutSeconds, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow.AddSeconds(Math.Clamp(timeoutSeconds, 0, MaxWaitSeconds));
        var ids = jobIds.Distinct(StringComparer.Ordinal).Take(MaxWaitJobs).ToList();
        var missing = ids.Where(id => store.Get(id, ownerId) is null).ToList();
        var known = ids.Except(missing).ToList();
        var interval = TimeSpan.FromMilliseconds(Math.Clamp(Options.PollIntervalMs, 500, 5000));

        while (true)
        {
            var views = new List<LocalMediaJobView>();
            foreach (var id in known)
                if (store.Get(id, ownerId) is { } job)
                    views.Add(await RefreshAsync(job, ct));

            var left = deadline - DateTime.UtcNow;
            if (views.All(v => LocalMediaStatuses.IsTerminal(v.Job.Status)) || left <= TimeSpan.Zero)
                return (views, missing);
            await Task.Delay(left < interval ? left : interval, ct);
        }
    }

    // Один шаг опроса задачи: очередь → история → сбор результата в проект
    public async Task<LocalMediaJobView> RefreshAsync(LocalMediaJob job, CancellationToken ct)
    {
        if (LocalMediaStatuses.IsTerminal(job.Status)) return new LocalMediaJobView(job);
        try
        {
            var history = await comfy.GetHistoryAsync(job.PromptId, ct);
            if (history is null)
            {
                var queue = await comfy.GetQueueAsync(ct);
                var position = queue.PositionOf(job.PromptId);
                if (position is null)
                {
                    // Могла закончиться между двумя запросами — история решает
                    history = await comfy.GetHistoryAsync(job.PromptId, ct);
                    if (history is null)
                        return new LocalMediaJobView(Fail(job, "Задача пропала из очереди ComfyUI (перезапуск или отмена на стенде)."));
                }
                else
                {
                    var status = position == 0 ? LocalMediaStatuses.Running : LocalMediaStatuses.Queued;
                    var current = status == job.Status ? job : store.Update(job.Id, j => j.Status = status) ?? job;
                    return new LocalMediaJobView(current, position);
                }
            }

            if (history.Failed)
                return new LocalMediaJobView(Fail(job, history.Error ?? "ComfyUI завершил задачу ошибкой."));
            if (!history.Completed)
                return new LocalMediaJobView(job, 0);
            return new LocalMediaJobView(await CollectAsync(job, history.Files, ct));
        }
        catch (ComfyException ex)
        {
            return new LocalMediaJobView(job, Warning: ex.Message);
        }
        catch (Exception ex) when (ex is InvalidOperationException or FormatException)
        {
            // Неожиданная форма ответа ComfyUI — задачу не валим, опрос повторится
            log.LogWarning(ex, "Неожиданный ответ ComfyUI по задаче {JobId}", job.Id);
            return new LocalMediaJobView(job, Warning: "ComfyUI вернул неожиданный ответ — повтори позже.");
        }
    }

    private async Task<LocalMediaJob> CollectAsync(LocalMediaJob job, IReadOnlyList<ComfyOutputFile> files,
        CancellationToken ct)
    {
        await _collectGate.WaitAsync(ct);
        try
        {
            // Пока ждали ворот, результат мог собрать соседний опрос
            var current = store.Get(job.Id, job.OwnerId) ?? job;
            if (LocalMediaStatuses.IsTerminal(current.Status)) return current;

            var root = projects.ResolveRoot(current.OwnerId, current.ProjectId);
            if (root is null) return Fail(current, "Проект задачи недоступен — результат некуда сохранить.");

            var wanted = files.Where(f => ContentTypeOf(Path.GetExtension(f.FileName)) is not null).ToList();
            if (wanted.Count == 0) return Fail(current, "ComfyUI не вернул файлов результата.");

            var folder = $"{ResultsFolder}/{current.CreatedAt:yyyy-MM-dd}";
            var outputs = new List<LocalMediaOutput>();
            for (var n = 0; n < wanted.Count; n++)
            {
                var file = wanted[n];
                var ext = Path.GetExtension(file.FileName).ToLowerInvariant();
                var bytes = await comfy.DownloadAsync(file, ct);
                var relative = $"{folder}/{current.Id}-{n + 1}{ext}";
                var full = SafePath.Join(root, relative);
                ProjectLinkGuard.EnsureNoLink(root, full);
                Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                ProjectLinkGuard.EnsureNoLink(root, full);
                await File.WriteAllBytesAsync(full, bytes, ct);
                projects.NotifyWritten(root, relative);

                var contentType = ContentTypeOf(ext)!;
                var dims = contentType.StartsWith("image/", StringComparison.Ordinal)
                    ? ImageDimensions.Read(bytes)
                    : current.Width is { } w && current.Height is { } h ? ((int Width, int Height)?)(w, h) : null;
                outputs.Add(new LocalMediaOutput
                {
                    Path = relative,
                    ContentType = contentType,
                    Width = dims?.Width,
                    Height = dims?.Height,
                });
            }

            return store.Update(current.Id, j =>
            {
                j.Status = LocalMediaStatuses.Completed;
                j.Outputs = outputs;
                j.FinishedAt = DateTime.UtcNow;
            }) ?? current;
        }
        catch (UnauthorizedAccessException)
        {
            return Fail(job, "Папка результатов идёт через символическую ссылку или вне проекта — результат не сохранён.");
        }
        catch (IOException ex)
        {
            log.LogWarning(ex, "Не удалось записать результат задачи {JobId}", job.Id);
            return Fail(job, "Не удалось записать результат в папку проекта.");
        }
        finally
        {
            _collectGate.Release();
        }
    }

    public static string? ContentTypeOf(string extension) => extension.ToLowerInvariant() switch
    {
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".webp" => "image/webp",
        ".mp4" => "video/mp4",
        ".webm" => "video/webm",
        _ => null,
    };

    private LocalMediaJob Fail(LocalMediaJob job, string error) =>
        store.Update(job.Id, j =>
        {
            j.Status = LocalMediaStatuses.Failed;
            j.Error = error;
            j.FinishedAt = DateTime.UtcNow;
        }) ?? job;

    // Сбор висящих задач фоном: результат попадает в проект, даже если агент не спрашивает.
    // Возвращает число задач, дошедших до конца
    public async Task<int> CollectPendingAsync(CancellationToken ct)
    {
        var done = 0;
        foreach (var job in store.Active())
        {
            ct.ThrowIfCancellationRequested();
            var view = await RefreshAsync(job, ct);
            if (LocalMediaStatuses.IsTerminal(view.Job.Status)) done++;
        }
        var options = Options;
        store.Prune(TimeSpan.FromDays(Math.Max(1, options.JobRetentionDays)), DateTime.UtcNow);
        return done;
    }
}

// Отказ по входным данным заявки: текст уходит модели как есть
public sealed class LocalMediaInputException(string message) : Exception(message);
