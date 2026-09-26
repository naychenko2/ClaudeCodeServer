using System.Globalization;
using ClaudeHomeServer.Services.ImageEditor;
using ClaudeHomeServer.Services.ImageEditor.Versioning;
using ClaudeHomeServer.Services.Images.Editing.Raster;

namespace ClaudeHomeServer.Services.Images.LocalMedia;

// Адаптер шва ILocalImageMedia для редактора картинок (ADR-018, раздел «Локальные модели»).
// Тот же ComfyUI и те же шаблоны ComfyWorkflows, что у MCP-сервера local-media, но без стора
// задач и без записи в проект: задачу ведёт исполнитель редактора, а варианты он кладёт в свою
// рабочую папку. Общее с local-media — тумблер LocalMedia:Enabled, потолок общей очереди
// MaxComfyQueue и таблица замеров времени.
public sealed class LocalImageMediaAdapter(ComfyClient comfy, IConfiguration config, ILogger<LocalImageMediaAdapter> log,
    IImageRaster? raster = null) : ILocalImageMedia
{
    private const string OutputStem = "ie_";
    // Живость ComfyUI для каталога: свежий ответ держим 30 с, первая проверка ждёт не дольше 2 с
    private static readonly TimeSpan ProbeTtl = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(2);

    private readonly Lock _probeGate = new();
    private DateTime _probedAt = DateTime.MinValue;
    private bool _alive;
    private Task? _probing;

    public bool Available
    {
        get
        {
            if (!LocalMediaOptions.IsEnabled(config)) return false;
            Task? first = null;
            lock (_probeGate)
            {
                if (DateTime.UtcNow - _probedAt < ProbeTtl) return _alive;
                _probing ??= ProbeAsync();
                if (_probedAt == DateTime.MinValue) first = _probing;
            }
            // Самый первый вопрос ждёт ответа: иначе поставщик мигнул бы «скрыт» на старте
            first?.Wait(ProbeTimeout);
            lock (_probeGate) return _alive;
        }
    }

    private async Task ProbeAsync()
    {
        var alive = await QueueLengthAsync(CancellationToken.None) is not null;
        lock (_probeGate)
        {
            _alive = alive;
            _probedAt = DateTime.UtcNow;
            _probing = null;
        }
    }

    private void MarkDead()
    {
        lock (_probeGate)
        {
            _alive = false;
            _probedAt = DateTime.UtcNow;
        }
    }

    public async Task<int?> QueueLengthAsync(CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(ProbeTimeout);
        try
        {
            return (await comfy.GetQueueAsync(timeout.Token)).Length;
        }
        catch (ComfyException)
        {
            return null;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return null;
        }
    }

    public int? EtaSeconds(LocalImageOp op, int count, int images) => op switch
    {
        LocalImageOp.Generate when images == 0 => LocalMediaService.GenerateImageEta(ComfyWorkflows.DefaultSteps, Math.Max(1, count)),
        LocalImageOp.Generate or LocalImageOp.Edit => LocalMediaService.EditImageEta(images),
        LocalImageOp.FaceDetail => LocalMediaService.FaceDetailEta,
        _ => null,
    };

    public async Task<LocalImageSubmitted> SubmitAsync(LocalImageRequest request, CancellationToken ct)
    {
        var options = LocalMediaOptions.Read(config);
        if (!options.Enabled) return LocalImageSubmitted.Fail("Локальные модели выключены на этом сервере.");

        var prompt = (request.Prompt ?? "").Trim();
        if (prompt.Length > ComfyWorkflows.MaxPromptLength)
            return LocalImageSubmitted.Fail($"Запрос длиннее {ComfyWorkflows.MaxPromptLength} символов.");
        var images = request.Images ?? [];
        switch (request.Op)
        {
            case LocalImageOp.Generate or LocalImageOp.Edit when prompt.Length == 0:
                return LocalImageSubmitted.Fail("Нужен запрос — что нарисовать или изменить.");
            case LocalImageOp.Edit when images.Count is < 1 or > ComfyWorkflows.MaxEditImages:
                return LocalImageSubmitted.Fail($"Для правки нужно от 1 до {ComfyWorkflows.MaxEditImages} картинок: первая — холст.");
            case LocalImageOp.Generate when images.Count > ComfyWorkflows.MaxEditImages:
                return LocalImageSubmitted.Fail($"Образцов — не больше {ComfyWorkflows.MaxEditImages}.");
            case LocalImageOp.FaceDetail when images.Count != 1:
                return LocalImageSubmitted.Fail("Для доводки лиц нужна ровно одна картинка.");
        }

        // Стирание кистью: область маски закрашивается на холсте до модели
        if (request.EraseMask is { Length: > 0 } eraseMask)
        {
            if (request.Op != LocalImageOp.Edit || raster is null)
                return LocalImageSubmitted.Fail("Стирание по маске здесь недоступно.");
            var erased = raster.EraseMasked(images[0], eraseMask);
            if (!erased.Ok) return LocalImageSubmitted.Fail("Маску не удалось наложить: " + erased.Message);
            images = [erased.Image!.Bytes, .. images.Skip(1)];
        }

        ComfyQueueState queue;
        try
        {
            queue = await comfy.GetQueueAsync(ct);
        }
        catch (ComfyException ex)
        {
            MarkDead();
            return LocalImageSubmitted.Fail($"{ex.Message}. Попробуйте позже или выберите другого поставщика.", busy: true);
        }
        if (queue.Length >= options.MaxComfyQueue)
            return LocalImageSubmitted.Fail($"Очередь локальной видеокарты занята ({queue.Length} задач). "
                + "Попробуйте позже или выберите другого поставщика.", busy: true);

        var id = Guid.NewGuid().ToString("N");
        var prefix = $"{ComfyWorkflows.OutputFolder}/{OutputStem}{id}";
        var seed = request.Seed is >= 0 ? request.Seed.Value : Random.Shared.NextInt64(1, 1L << 50);
        var count = Math.Clamp(request.Count, 1, ComfyWorkflows.MaxCount);

        try
        {
            var names = new List<string>();
            for (var k = 0; k < images.Count; k++)
            {
                var ext = ImageFormatSniffer.DetectExtension(images[k]);
                if (ext is null) return LocalImageSubmitted.Fail($"Картинка {k + 1} — не PNG, JPEG или WebP.");
                if (images[k].Length > LocalMediaService.MaxInputBytes)
                    return LocalImageSubmitted.Fail($"Картинка {k + 1} больше {LocalMediaService.MaxInputBytes / 1024 / 1024} МБ.");
                names.Add(await comfy.UploadImageAsync(images[k], $"{OutputStem}{id}-in{k + 1}{ext}", ct));
            }

            var graph = request.Op switch
            {
                LocalImageOp.Generate when names.Count == 0 => GenerateGraph(prompt, request.Aspect, seed, count, prefix),
                // Генерация по образцам — граф правки на пустом холсте нужного размера
                LocalImageOp.Generate => ComfyWorkflows.EditImage(prompt, names, SizeFor(request.Aspect), seed, prefix),
                LocalImageOp.Edit => ComfyWorkflows.EditImage(prompt, names, null, seed, prefix),
                _ => ComfyWorkflows.FaceDetail(names[0], seed, prefix),
            };
            var queued = await comfy.QueuePromptAsync(graph, ct);
            return new LocalImageSubmitted(queued.PromptId, queue.Length, EtaSeconds(request.Op, count, names.Count), null);
        }
        catch (ComfyException ex)
        {
            log.LogWarning("ComfyUI не принял задачу редактора {Op}: {Error}", request.Op, ex.Message);
            return LocalImageSubmitted.Fail(ex.Message, busy: true);
        }
    }

    private static System.Text.Json.Nodes.JsonObject GenerateGraph(string prompt, string? aspect, long seed, int count,
        string prefix)
    {
        var size = SizeFor(aspect);
        return ComfyWorkflows.GenerateImage(prompt, "", size.Width, size.Height, seed, ComfyWorkflows.DefaultSteps,
            count, prefix);
    }

    // Размер из таблицы Qwen-Image; соотношение не из таблицы — ближайшее по пропорции
    internal static (int Width, int Height) SizeFor(string? aspect)
    {
        var key = aspect?.Trim() ?? "";
        if (ComfyWorkflows.ImageSizes.TryGetValue(key, out var exact)) return exact;
        var parts = key.Split(':');
        if (parts.Length != 2
            || !double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var w)
            || !double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var h)
            || w <= 0 || h <= 0)
            return ComfyWorkflows.ImageSizes["1:1"];
        var ratio = Math.Log(w / h);
        return ComfyWorkflows.ImageSizes.Values
            .OrderBy(s => Math.Abs(Math.Log((double)s.Width / s.Height) - ratio))
            .First();
    }

    public async Task<LocalImagePoll> PollAsync(string ticket, CancellationToken ct)
    {
        try
        {
            var history = await comfy.GetHistoryAsync(ticket, ct);
            if (history is null)
            {
                var position = (await comfy.GetQueueAsync(ct)).PositionOf(ticket);
                if (position is not null)
                    return new LocalImagePoll(position == 0 ? LocalImageState.Running : LocalImageState.Queued, position, [], null);
                // Могла закончиться между двумя запросами — история решает
                history = await comfy.GetHistoryAsync(ticket, ct);
                if (history is null)
                    return Failed("Задача пропала из очереди ComfyUI (перезапуск или отмена на стенде).");
            }
            if (history.Failed) return Failed(history.Error ?? "ComfyUI завершил задачу ошибкой.");
            if (!history.Completed) return new LocalImagePoll(LocalImageState.Running, 0, [], null);

            var files = new List<LocalImageFile>();
            foreach (var file in history.Files)
            {
                var type = LocalMediaService.ContentTypeOf(Path.GetExtension(file.FileName));
                if (type is null || !type.StartsWith("image/", StringComparison.Ordinal)) continue;
                files.Add(new LocalImageFile(await comfy.DownloadAsync(file, ct), type));
            }
            return files.Count == 0
                ? Failed("ComfyUI не вернул картинок.")
                : new LocalImagePoll(LocalImageState.Completed, null, files, null);
        }
        catch (ComfyException ex)
        {
            return new LocalImagePoll(LocalImageState.Running, null, [], null, ex.Message);
        }
        catch (Exception ex) when (ex is InvalidOperationException or FormatException)
        {
            log.LogWarning(ex, "Неожиданный ответ ComfyUI по задаче редактора {Ticket}", ticket);
            return new LocalImagePoll(LocalImageState.Running, null, [], null, "ComfyUI вернул неожиданный ответ.");
        }

        static LocalImagePoll Failed(string error) => new(LocalImageState.Failed, null, [], error);
    }

    public async Task<bool> CancelAsync(string ticket, CancellationToken ct)
    {
        try
        {
            var queue = await comfy.GetQueueAsync(ct);
            if (queue.PositionOf(ticket) is not > 0) return false;
            await comfy.DeletePendingAsync(ticket, ct);
            return true;
        }
        catch (ComfyException)
        {
            return false;
        }
    }
}
