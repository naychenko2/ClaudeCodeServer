using ClaudeHomeServer.Services.ImageEditor;
using ClaudeHomeServer.Services.Media;

namespace ClaudeHomeServer.Services.Images.LocalMedia;

// Адаптер шва ILocalVideoMedia для модуля «Видео» (ADR-022, §3). Прямо на ComfyClient с шаблоном
// ComfyWorkflows.ImageToVideo (узел last_frame), а не через LocalMediaService: тот ведёт задачу в сторе
// и пишет результат в .cc-attachments проекта (LocalMediaCollector собрал бы его второй раз), а модуль
// кладёт клип в свою рабочую папку. Белый список размеров — ComfyWorkflows.VideoSizes, потолок длины —
// LocalMediaOptions.MaxVideoSeconds, проверка кадров и ETA — статика LocalMediaService. Живость ComfyUI
// общая с картинками: её кеширует адаптер ILocalImageMedia.
public sealed class LocalVideoMediaAdapter(
    ComfyClient comfy,
    IConfiguration config,
    ILocalImageMedia images,
    ILogger<LocalVideoMediaAdapter> log) : ILocalVideoMedia
{
    private const string IdPrefix = "lv_";
    private const string FirstSlot = "первый кадр";
    private const string LastSlot = "последний кадр";

    public bool Configured => LocalMediaOptions.IsEnabled(config);

    public bool Available => Configured && images.Available;

    public Task<int?> QueueLengthAsync(CancellationToken ct) =>
        Configured ? images.QueueLengthAsync(ct) : Task.FromResult<int?>(null);

    public int? EtaSeconds(LocalVideoRequest request)
    {
        try
        {
            var plan = Plan(request, LocalMediaOptions.Read(config));
            return LocalMediaService.ImageToVideoEta(plan.Size, plan.Seconds, plan.Fast);
        }
        catch (LocalMediaInputException)
        {
            return null;
        }
    }

    public async Task<LocalVideoSubmitted> SubmitAsync(LocalVideoRequest request, CancellationToken ct)
    {
        var options = LocalMediaOptions.Read(config);
        if (!options.Enabled) return LocalVideoSubmitted.Fail("Локальные модели выключены на этом сервере.");

        Run plan;
        try
        {
            plan = Plan(request, options);
        }
        catch (LocalMediaInputException ex)
        {
            return LocalVideoSubmitted.Fail(ex.Message);
        }

        var queue = await images.QueueLengthAsync(ct);
        if (queue is null)
            return LocalVideoSubmitted.Fail("ComfyUI не отвечает. Попробуйте позже или выберите другого поставщика.", busy: true);
        if (queue >= options.MaxComfyQueue)
            return LocalVideoSubmitted.Fail($"Очередь локальной видеокарты занята ({queue} задач). "
                + "Попробуйте позже или выберите другого поставщика.", busy: true);

        var id = IdPrefix + Guid.NewGuid().ToString("N");
        var seed = request.Seed is >= 0 ? request.Seed.Value : Random.Shared.NextInt64(1, 1L << 50);
        try
        {
            var firstName = await comfy.UploadImageAsync(plan.First.Bytes, $"{id}-in1{plan.First.Extension}", ct);
            string? lastName = null;
            if (plan.Last is { } last)
                lastName = await comfy.UploadImageAsync(last.Bytes, $"{id}-last{last.Extension}", ct);

            var graph = ComfyWorkflows.ImageToVideo(firstName, lastName, plan.Prompt, plan.Size.Width, plan.Size.Height,
                ComfyWorkflows.FramesFor(plan.Seconds), seed, plan.Fast, $"{ComfyWorkflows.OutputFolder}/{id}",
                $"{ComfyWorkflows.OutputFolder}/latents/{id}");
            var queued = await comfy.QueuePromptAsync(graph, ct);
            return new LocalVideoSubmitted(queued.PromptId, queue,
                LocalMediaService.ImageToVideoEta(plan.Size, plan.Seconds, plan.Fast), null);
        }
        catch (ComfyException ex)
        {
            log.LogWarning("ComfyUI не принял видеозадачу: {Error}", ex.Message);
            return LocalVideoSubmitted.Fail(ex.Message, busy: true);
        }
    }

    public async Task<LocalVideoPoll> PollAsync(string ticket, CancellationToken ct)
    {
        try
        {
            var history = await comfy.GetHistoryAsync(ticket, ct);
            if (history is null)
            {
                var position = (await comfy.GetQueueAsync(ct)).PositionOf(ticket);
                if (position is not null)
                    return new LocalVideoPoll(position == 0 ? LocalVideoState.Running : LocalVideoState.Queued, position, null, null);
                // Могла закончиться между двумя запросами — история решает
                history = await comfy.GetHistoryAsync(ticket, ct);
                if (history is null)
                    return Failed("Задача пропала из очереди ComfyUI (перезапуск или отмена на стенде).");
            }
            if (history.Failed) return Failed(history.Error ?? "ComfyUI завершил задачу ошибкой.");
            if (!history.Completed) return new LocalVideoPoll(LocalVideoState.Running, 0, null, null);

            foreach (var file in history.Files)
            {
                var ext = Path.GetExtension(file.FileName).ToLowerInvariant();
                if (LocalMediaService.ContentTypeOf(ext) is not { } type || !type.StartsWith("video/", StringComparison.Ordinal))
                    continue;
                // Звук H3 генерирует сам, CreateVideo склеивает его с кадрами: дорожка есть всегда
                var bytes = await comfy.DownloadAsync(file, ct);
                return new LocalVideoPoll(LocalVideoState.Completed, null, new LocalVideoFile(bytes, type, ext, HasSound: true), null);
            }
            return Failed("ComfyUI не вернул видео.");
        }
        catch (ComfyException ex)
        {
            return new LocalVideoPoll(LocalVideoState.Running, null, null, null, ex.Message);
        }
        catch (Exception ex) when (ex is InvalidOperationException or FormatException)
        {
            log.LogWarning(ex, "Неожиданный ответ ComfyUI по видеозадаче {Ticket}", ticket);
            return new LocalVideoPoll(LocalVideoState.Running, null, null, null, "ComfyUI вернул неожиданный ответ.");
        }

        static LocalVideoPoll Failed(string error) => new(LocalVideoState.Failed, null, null, error);
    }

    public async Task<bool> CancelAsync(string ticket, CancellationToken ct)
    {
        try
        {
            var queue = await comfy.GetQueueAsync(ct);
            switch (queue.PositionOf(ticket))
            {
                case null:
                    return false;
                // Идёт — прерываем адресно: иначе граф доигрывает и держит GPU
                case 0:
                    await comfy.InterruptAsync(ticket, ct);
                    return true;
                default:
                    await comfy.DeletePendingAsync(ticket, ct);
                    return true;
            }
        }
        catch (ComfyException)
        {
            return false;
        }
    }

    private sealed record Run(string Prompt, LocalMediaService.InputFile First, LocalMediaService.InputFile? Last,
        (int Width, int Height) Size, int Seconds, bool Fast);

    // Проверка запроса и размеры кадра — общие с local_image_to_video: портретный первый кадр даёт
    // портретное видео (стороны переставляются, кадр не режется)
    private static Run Plan(LocalVideoRequest request, LocalMediaOptions options)
    {
        var prompt = (request.Prompt ?? "").Trim();
        if (prompt.Length == 0) throw new LocalMediaInputException("Нужен запрос — что происходит в кадре.");
        if (prompt.Length > ComfyWorkflows.MaxPromptLength)
            throw new LocalMediaInputException($"Запрос длиннее {ComfyWorkflows.MaxPromptLength} символов.");
        if (request.Seconds < 1 || request.Seconds > options.MaxVideoSeconds)
            throw new LocalMediaInputException($"Длительность — от 1 до {options.MaxVideoSeconds} секунд.");
        var sizeKey = string.IsNullOrWhiteSpace(request.Size) ? "full" : request.Size.Trim();
        if (!ComfyWorkflows.VideoSizes.TryGetValue(sizeKey, out var size))
            throw new LocalMediaInputException("size — full (1344×768) или half (864×480).");
        if (request.FirstFrame is not { Length: > 0 })
            throw new LocalMediaInputException("Нужен первый кадр.");

        var first = LocalMediaService.CheckInput(request.FirstFrame, FirstSlot, FirstSlot, LocalMediaService.MediaKind.Image);
        var last = request.LastFrame is { } lastBytes
            ? LocalMediaService.CheckInput(lastBytes, LastSlot, LastSlot, LocalMediaService.MediaKind.Image)
            : null;
        if (ImageDimensions.Read(first.Bytes) is { } dims && dims.Height > dims.Width)
            size = (size.Height, size.Width);
        return new Run(prompt, first, last, size, request.Seconds, request.Fast ?? options.VideoFastDefault);
    }
}
