using System.Text.Json.Nodes;
using ClaudeHomeServer.Services.ImageEditor;
using ClaudeHomeServer.Services.ImageEditor.Versioning;
using ClaudeHomeServer.Services.Images.Editing;

namespace ClaudeHomeServer.Services.Images.LocalMedia;

// Заявка на генерацию. Images (у референсов — ref_images), LastFrame, Video, Mask, RefVideos,
// RefAudios — пути файлов проекта или job_id прошлых задач; SourceJobId — видео для апскейла
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
    int? DurationSeconds = null,
    string? LastFrame = null,
    bool? Fast = null,
    string? Orientation = null,
    string? SourceJobId = null,
    string? UpscaleTarget = null,
    string? Video = null,
    string? Mask = null,
    IReadOnlyList<string>? RefVideos = null,
    IReadOnlyList<string>? RefAudios = null,
    string? Identity = null);

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
    // Видео и звук: потолок загрузки ComfyUI — 100 МБ вместе с обёрткой multipart
    public const int MaxMediaInputBytes = 90 * 1024 * 1024;
    public const double MinRefVideoSeconds = 2;
    public const double MaxRefVideoSeconds = 15;
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
        if (request.Op is not (LocalMediaOps.FaceDetail or LocalMediaOps.VideoUpscale) && prompt.Length == 0)
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

            // Тяжёлые операции держат GPU десятки минут: у владельца одновременно одна
            var heavy = IsHeavy(request);
            if (heavy && store.ActiveHeavyCount(request.OwnerId) > 0)
                return LocalMediaCallResult.Fail("У тебя уже идёт тяжёлая локальная задача (апскейл, инпейнт или "
                    + "референсы с identity=max) — дождись её (local_jobs_wait) и повтори.");

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
                Heavy = heavy,
            };
            var prefix = $"{ComfyWorkflows.OutputFolder}/{jobId}";

            JsonObject graph;
            int? eta;
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

    private async Task<(JsonObject Graph, int? EtaSeconds)> BuildGraphAsync(LocalMediaRequest request,
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
                return (graph, GenerateImageEta(steps, count));
            }
            case LocalMediaOps.EditImage:
            {
                if (images.Count is < 1 or > ComfyWorkflows.MaxEditImages)
                    throw new LocalMediaInputException("Для правки нужно от 1 до 16 картинок (images): первая — основная.");
                var size = string.IsNullOrWhiteSpace(request.Aspect) ? ((int, int)?)null : AspectSize(request.Aspect);
                var names = new List<string>();
                for (var k = 0; k < images.Count; k++)
                    names.Add(await UploadInputAsync(request, root, images[k], MediaKind.Image, $"{job.Id}-in{k + 1}", ct));
                return (ComfyWorkflows.EditImage(prompt, names, size, seed, prefix), EditImageEta(images.Count));
            }
            case LocalMediaOps.FaceDetail:
            {
                if (images.Count != 1) throw new LocalMediaInputException("Нужна ровно одна картинка (image).");
                var name = await UploadInputAsync(request, root, images[0], MediaKind.Image, $"{job.Id}-in1", ct);
                return (ComfyWorkflows.FaceDetail(name, seed, prefix), FaceDetailEta);
            }
            case LocalMediaOps.TextToVideo:
            {
                var seconds = VideoSeconds(request, options);
                var size = VideoSize(request.VideoSize, request.Orientation);
                var fast = request.Fast ?? options.VideoFastDefault;
                var frames = ComfyWorkflows.FramesFor(seconds);
                RememberVideo(job, prompt, size, seconds, frames, fast);
                return (ComfyWorkflows.TextToVideo(prompt, size.Width, size.Height, frames, seed, fast, prefix,
                    LatentPrefix(job)), TextToVideoEta(size, seconds));
            }
            case LocalMediaOps.ImageToVideo:
            {
                if (images.Count != 1) throw new LocalMediaInputException("Нужна ровно одна картинка — первый кадр (image).");
                var seconds = VideoSeconds(request, options);
                var size = VideoSize(request.VideoSize, null);
                var fast = request.Fast ?? options.VideoFastDefault;

                var first = ReadInput(request, root, images[0], MediaKind.Image);
                // Портретный кадр — портретное видео: стороны переставляются, а не кадр режется
                if (ImageDimensions.Read(first.Bytes) is { } dims && dims.Height > dims.Width)
                    size = (size.Height, size.Width);
                var firstName = await comfy.UploadImageAsync(first.Bytes, $"{job.Id}-in1{first.Extension}", ct);
                string? lastName = null;
                if (!string.IsNullOrWhiteSpace(request.LastFrame))
                    lastName = await UploadInputAsync(request, root, request.LastFrame, MediaKind.Image, $"{job.Id}-last", ct);

                var frames = ComfyWorkflows.FramesFor(seconds);
                RememberVideo(job, prompt, size, seconds, frames, fast);
                job.ComfyFirstFrame = firstName;
                return (ComfyWorkflows.ImageToVideo(firstName, lastName, prompt, size.Width, size.Height, frames, seed,
                    fast, prefix, LatentPrefix(job)), ImageToVideoEta(size, seconds, fast));
            }
            case LocalMediaOps.VideoUpscale:
                return await BuildUpscaleAsync(request, job, prefix, options, ct);
            case LocalMediaOps.VideoInpaint:
            {
                if (string.IsNullOrWhiteSpace(request.Video) || string.IsNullOrWhiteSpace(request.Mask))
                    throw new LocalMediaInputException("Для инпейнта нужны video (mp4 в проекте) и mask (PNG, белое — перерисовать).");
                var video = ReadInput(request, root, request.Video, MediaKind.Video);
                var info = video.Video!;
                if (!IsAllowedVideoSize(info.Width, info.Height))
                    throw new LocalMediaInputException($"Видео {info.Width}×{info.Height}: для инпейнта нужен размер "
                        + "1344×768 или 864×480 (либо портретный с переставленными сторонами).");
                var seconds = (int)Math.Round(info.Seconds);
                if (seconds < 1 || seconds > options.MaxVideoSeconds)
                    throw new LocalMediaInputException($"Видео для инпейнта — от 1 до {options.MaxVideoSeconds} секунд, "
                        + $"а это ≈{info.Seconds:0.#} с.");
                var mask = ReadInput(request, root, request.Mask, MediaKind.Image);
                if (ImageDimensions.Read(mask.Bytes) is not { } maskDims || maskDims != (info.Width, info.Height))
                    throw new LocalMediaInputException($"Маска должна быть того же размера, что видео ({info.Width}×{info.Height}).");

                var videoName = await comfy.UploadImageAsync(video.Bytes, $"{job.Id}-video.mp4", ct);
                var maskName = await comfy.UploadImageAsync(mask.Bytes, $"{job.Id}-mask{mask.Extension}", ct);
                var frames = ComfyWorkflows.FramesFor(seconds);
                job.Width = info.Width;
                job.Height = info.Height;
                job.DurationSeconds = seconds;
                return (ComfyWorkflows.InpaintVideo(videoName, maskName, prompt, info.Width, info.Height, frames, seed,
                    prefix), InpaintEta((info.Width, info.Height), seconds));
            }
            case LocalMediaOps.ReferenceToVideo:
            {
                var refVideos = request.RefVideos ?? [];
                var refAudios = request.RefAudios ?? [];
                if (images.Count is < 1 or > ComfyWorkflows.MaxRefImages)
                    throw new LocalMediaInputException("Нужно от 1 до 9 картинок-референсов (ref_images).");
                if (refVideos.Count > ComfyWorkflows.MaxRefVideos)
                    throw new LocalMediaInputException("Референс-видео — не больше трёх (ref_videos).");
                if (refAudios.Count > ComfyWorkflows.MaxRefAudios)
                    throw new LocalMediaInputException("Референсного звука — не больше трёх (ref_audios).");
                var maxIdentity = IsMaxIdentity(request.Identity);
                var seconds = VideoSeconds(request, options);
                var size = VideoSize(request.VideoSize, request.Orientation);

                var imageNames = new List<string>();
                for (var k = 0; k < images.Count; k++)
                    imageNames.Add(await UploadInputAsync(request, root, images[k], MediaKind.Image, $"{job.Id}-ref{k + 1}", ct));
                var videoNames = new List<string>();
                for (var k = 0; k < refVideos.Count; k++)
                {
                    var video = ReadInput(request, root, refVideos[k], MediaKind.Video);
                    if (video.Video!.Seconds is < MinRefVideoSeconds or > MaxRefVideoSeconds)
                        throw new LocalMediaInputException($"Референс-видео «{refVideos[k]}» — ≈{video.Video.Seconds:0.#} с, "
                            + "а нужно от 2 до 15 секунд.");
                    videoNames.Add(await comfy.UploadImageAsync(video.Bytes, $"{job.Id}-refvid{k + 1}.mp4", ct));
                }
                var audioNames = new List<string>();
                for (var k = 0; k < refAudios.Count; k++)
                    audioNames.Add(await UploadInputAsync(request, root, refAudios[k], MediaKind.Audio, $"{job.Id}-refaud{k + 1}", ct));

                job.Width = size.Width;
                job.Height = size.Height;
                job.DurationSeconds = seconds;
                return (ComfyWorkflows.ReferenceToVideo(prompt, imageNames, videoNames, audioNames, size.Width, size.Height,
                    ComfyWorkflows.FramesFor(seconds), maxIdentity, seed, prefix), null);
            }
            default:
                throw new LocalMediaInputException("Неизвестная операция.");
        }
    }

    // Апскейл по латенту прошлой видеозадачи ЭТОГО владельца и проекта. Произвольный mp4 не
    // принимается: латентный апскейлер работает только со своим же латентом. Латенты едут из
    // output ComfyUI в корень input под именем с jobId (LoadLatent видит только корень)
    private async Task<(JsonObject Graph, int? EtaSeconds)> BuildUpscaleAsync(LocalMediaRequest request, LocalMediaJob job, string prefix,
        LocalMediaOptions options, CancellationToken ct)
    {
        var sourceId = (request.SourceJobId ?? "").Trim();
        if (sourceId.Length == 0)
            throw new LocalMediaInputException("Для апскейла нужен job_id завершённой задачи local_text_to_video или local_image_to_video.");
        var source = LooksLikeJobId(sourceId) ? store.Get(sourceId, request.OwnerId) : null;
        if (source is null || source.ProjectId != request.ProjectId)
            throw new LocalMediaInputException($"Задача {sourceId} не найдена.");
        if (!LocalMediaOps.KeepsLatent(source.Op))
            throw new LocalMediaInputException("Апскейл — только для видео из local_text_to_video или local_image_to_video: "
                + "он работает по сохранённому латенту задачи, а не по готовому файлу.");
        if (source.Status != LocalMediaStatuses.Completed || source.LatentVideo is null || source.LatentAudio is null
            || source.Prompt is null || source.Frames is null)
            throw new LocalMediaInputException($"У задачи {sourceId} нет сохранённого латента: она не завершена "
                + "или поставлена до появления апскейла.");

        var landscape = (source.Width, source.Height) == ComfyWorkflows.VideoSizes["full"];
        var portrait = (source.Height, source.Width) == ComfyWorkflows.VideoSizes["full"];
        if (!landscape && !portrait)
            throw new LocalMediaInputException("Апскейл — только для видео размера full (1344×768 или портретного 768×1344).");

        var target = (request.UpscaleTarget ?? "1440p").Trim().ToLowerInvariant();
        (int Width, int Height) size = target switch
        {
            "1440p" or "1440" => ComfyWorkflows.Upscale1440,
            "2k" => ComfyWorkflows.Upscale2k,
            _ => throw new LocalMediaInputException("target — 1440p (2528×1440) или 2k (2688×1536)."),
        };
        if (portrait) size = (size.Height, size.Width);
        var tiled = target == "2k" || options.Upscale1440Tiled;

        var latentVideo = await CopyLatentAsync(source.LatentVideo, $"{ComfyClient.InputFolder}-{job.Id}-video.latent", ct);
        var latentAudio = await CopyLatentAsync(source.LatentAudio, $"{ComfyClient.InputFolder}-{job.Id}-audio.latent", ct);

        if (request.Seed is not >= 0) job.Seed = source.Seed;
        job.Width = size.Width;
        job.Height = size.Height;
        job.DurationSeconds = source.DurationSeconds;
        job.Frames = source.Frames;
        return (ComfyWorkflows.UpscaleVideo(latentVideo, latentAudio, source.ComfyFirstFrame, source.Prompt,
            size.Width, size.Height, source.Frames.Value, job.Seed, tiled, prefix),
            target == "2k" ? null : Upscale1440Eta(source.Frames.Value, tiled));
    }

    private async Task<string> CopyLatentAsync(string outputPath, string inputName, CancellationToken ct)
    {
        var slash = outputPath.LastIndexOf('/');
        var file = new ComfyOutputFile(outputPath[(slash + 1)..], slash < 0 ? "" : outputPath[..slash], "output");
        var bytes = await comfy.DownloadAsync(file, ct);
        return await comfy.UploadInputAsync(bytes, inputName, "", ct);
    }

    public static bool IsHeavy(LocalMediaRequest request) =>
        request.Op is LocalMediaOps.VideoUpscale or LocalMediaOps.VideoInpaint
        || (request.Op == LocalMediaOps.ReferenceToVideo
            && string.Equals(request.Identity?.Trim(), "max", StringComparison.OrdinalIgnoreCase));

    private static bool IsMaxIdentity(string? identity) => (identity?.Trim().ToLowerInvariant()) switch
    {
        null or "" or "match" => false,
        "max" => true,
        _ => throw new LocalMediaInputException("identity — match (быстрее) или max (точнее сходство, в разы медленнее)."),
    };

    private static int VideoSeconds(LocalMediaRequest request, LocalMediaOptions options)
    {
        var seconds = request.DurationSeconds ?? 5;
        if (seconds < 1 || seconds > options.MaxVideoSeconds)
            throw new LocalMediaInputException($"Длительность — от 1 до {options.MaxVideoSeconds} секунд.");
        return seconds;
    }

    private static (int Width, int Height) VideoSize(string? key, string? orientation)
    {
        var sizeKey = string.IsNullOrWhiteSpace(key) ? "full" : key.Trim();
        if (!ComfyWorkflows.VideoSizes.TryGetValue(sizeKey, out var size))
            throw new LocalMediaInputException("size — full (1344×768) или half (864×480).");
        return (orientation?.Trim().ToLowerInvariant()) switch
        {
            null or "" or "landscape" => size,
            "portrait" => (size.Height, size.Width),
            _ => throw new LocalMediaInputException("orientation — landscape или portrait."),
        };
    }

    private static bool IsAllowedVideoSize(int width, int height) =>
        ComfyWorkflows.VideoSizes.Values.Any(s => (s.Width, s.Height) == (width, height) || (s.Height, s.Width) == (width, height));

    private static void RememberVideo(LocalMediaJob job, string prompt, (int Width, int Height) size, int seconds,
        int frames, bool fast)
    {
        job.Width = size.Width;
        job.Height = size.Height;
        job.DurationSeconds = seconds;
        job.Frames = frames;
        job.Prompt = prompt;
        job.Fast = fast;
    }

    private static string LatentPrefix(LocalMediaJob job) => $"{ComfyWorkflows.OutputFolder}/latents/{job.Id}";

    // Картинки: ≈45 с на картинку при 25 шагах, правка ≈60 с плюс ≈10 с на каждый образец,
    // доводка лиц ≈25 с (docs/features/local-media.md). Общие с драйвером редактора
    public static int GenerateImageEta(int steps, int count) => 15 + (int)Math.Ceiling(steps * 1.2 * count);

    public static int EditImageEta(int images) => 60 + 10 * (Math.Max(1, images) - 1);

    public const int FaceDetailEta = 25;

    // Время — зачётные прогоны замера d653be60 (одна RTX 3090, модели уже загружены); первый
    // прогон после простоя идёт примерно столько же. Где замера нет — null: время не обещаем.
    // Короче 5 с — по 5-секундному замеру (потолок), между 5 и 10 с — по прямой
    public static int? ImageToVideoEta((int Width, int Height) size, int seconds, bool fast)
    {
        if (IsVideoSize(size, "half")) return seconds <= 5 ? 94 : null; // fast на half не мерен — время обычного
        if (!IsVideoSize(size, "full")) return null;
        var (at5, at10) = fast ? (234, 585) : (333, 943);
        return seconds <= 5 ? at5 : (int)Math.Round(at5 + (seconds - 5) * (at10 - at5) / 5.0);
    }

    // T2V замерен только на 5 с full и только в обычном режиме: fast не медленнее, берём его же
    public static int? TextToVideoEta((int Width, int Height) size, int seconds) =>
        IsVideoSize(size, "full") && seconds <= 5 ? 310 : null;

    public static int? InpaintEta((int Width, int Height) size, int seconds) =>
        IsVideoSize(size, "half") && seconds <= 5 ? 98 : null;

    // Апскейл 1440p по латенту 5-секундного full; 2K по тайлам не замерен
    public static int? Upscale1440Eta(int frames, bool tiled) =>
        frames <= ComfyWorkflows.FramesFor(5) ? (tiled ? 915 : 1065) : null;

    private static bool IsVideoSize((int Width, int Height) size, string key)
    {
        var known = ComfyWorkflows.VideoSizes[key];
        return size == known || size == (known.Height, known.Width);
    }

    private static (int Width, int Height) AspectSize(string aspect) =>
        ComfyWorkflows.ImageSizes.TryGetValue(aspect.Trim(), out var size)
            ? size
            : throw new LocalMediaInputException("aspect — одно из: " + string.Join(", ", ComfyWorkflows.ImageSizes.Keys) + ".");

    private async Task<string> UploadInputAsync(LocalMediaRequest request, string root, string reference,
        MediaKind kind, string stem, CancellationToken ct)
    {
        var input = ReadInput(request, root, reference, kind);
        return await comfy.UploadImageAsync(input.Bytes, stem + input.Extension, ct);
    }

    private enum MediaKind { Image, Video, Audio }

    private sealed record InputFile(byte[] Bytes, string Extension, MediaProbe.VideoInfo? Video);

    // Вход — путь файла проекта или job_id прошлой задачи этого же владельца и проекта.
    // Только из проекта чата: SafePath + запрет символических ссылок (ProjectLinkGuard)
    private InputFile ReadInput(LocalMediaRequest request, string root, string reference, MediaKind kind)
    {
        var value = (reference ?? "").Trim();
        var (what, result) = kind switch
        {
            MediaKind.Video => ("видео", "готового видео"),
            MediaKind.Audio => ("звук", "готового звука"),
            _ => ("картинку", "готовой картинки"),
        };
        if (value.Length == 0) throw new LocalMediaInputException($"Пустая ссылка на {what}.");

        string relative;
        if (LooksLikeJobId(value))
        {
            var source = store.Get(value, request.OwnerId);
            if (source is null || source.ProjectId != request.ProjectId)
                throw new LocalMediaInputException($"Задача {value} не найдена.");
            var type = kind == MediaKind.Video ? "video/" : "image/";
            var output = kind == MediaKind.Audio ? null
                : source.Outputs.FirstOrDefault(o => o.ContentType.StartsWith(type, StringComparison.Ordinal));
            if (source.Status != LocalMediaStatuses.Completed || output is null)
                throw new LocalMediaInputException($"У задачи {value} нет {result}.");
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
        var limit = kind == MediaKind.Image ? MaxInputBytes : MaxMediaInputBytes;
        if (info.Length > limit) throw new LocalMediaInputException($"Файл «{value}» больше {limit / 1024 / 1024} МБ.");
        var bytes = File.ReadAllBytes(full);

        switch (kind)
        {
            case MediaKind.Video:
                var video = MediaProbe.ReadMp4(bytes)
                    ?? throw new LocalMediaInputException($"Файл «{value}» — не видео mp4 (или в нём нет видеодорожки).");
                return new InputFile(bytes, ".mp4", video);
            case MediaKind.Audio:
                var audio = MediaProbe.DetectAudioExtension(bytes)
                    ?? throw new LocalMediaInputException($"Файл «{value}» — не звук (нужен WAV, MP3, FLAC или OGG).");
                return new InputFile(bytes, audio, null);
            default:
                var image = ImageFormatSniffer.DetectExtension(bytes)
                    ?? throw new LocalMediaInputException($"Файл «{value}» — не картинка (нужен PNG, JPEG или WebP).");
                return new InputFile(bytes, image, null);
        }
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
                    var current = status == job.Status ? job : store.Update(job.Id, job.OwnerId, j => j.Status = status) ?? job;
                    return new LocalMediaJobView(current, position);
                }
            }

            if (history.Failed)
                return new LocalMediaJobView(Fail(job, history.Error ?? "ComfyUI завершил задачу ошибкой."));
            if (!history.Completed)
                return new LocalMediaJobView(job, 0);
            return new LocalMediaJobView(await CollectAsync(job, history, ct));
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

    private async Task<LocalMediaJob> CollectAsync(LocalMediaJob job, ComfyHistoryEntry history, CancellationToken ct)
    {
        var files = history.Files;
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

            // Латент видео — вход будущего апскейла: остаётся в output ComfyUI, в сторе только путь
            var (latentVideo, latentAudio) = LocalMediaOps.KeepsLatent(current.Op)
                ? (LatentPath(history.Latents, "_video_"), LatentPath(history.Latents, "_audio_"))
                : (null, null);

            return store.Update(current.Id, current.OwnerId, j =>
            {
                j.Status = LocalMediaStatuses.Completed;
                j.Outputs = outputs;
                j.LatentVideo = latentVideo;
                j.LatentAudio = latentAudio;
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

    private static string? LatentPath(IReadOnlyList<ComfyOutputFile> latents, string marker) =>
        latents.FirstOrDefault(l => l.FileName.Contains(marker, StringComparison.Ordinal)) is { } file
            ? (file.Subfolder.Length == 0 ? file.FileName : $"{file.Subfolder}/{file.FileName}")
            : null;

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
        store.Update(job.Id, job.OwnerId, j =>
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
