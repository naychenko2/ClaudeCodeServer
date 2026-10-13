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
public sealed class LocalImageMediaAdapter : ILocalImageMedia
{
    private const string OutputStem = "ie_";
    // Живость ComfyUI для каталога: свежий ответ держим 30 с, опрос — не дольше 2 с
    private static readonly TimeSpan ProbeTtl = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(2);
    // Вуаль (ADR-028, решение 12): серая заливка места правки на копии холста, α — прозрачность
    private const double VeilAlpha = 0.6;

    private readonly ComfyClient comfy;
    private readonly IConfiguration config;
    private readonly ILogger<LocalImageMediaAdapter> log;
    private readonly IImageRaster? raster;
    private readonly LocalMediaJobStore? store;

    private readonly Lock _probeGate = new();
    private DateTime _probedAt = DateTime.MinValue;
    private bool _alive;
    private Task? _probing;

    public LocalImageMediaAdapter(ComfyClient comfy, IConfiguration config, ILogger<LocalImageMediaAdapter> log,
        IImageRaster? raster = null, LocalMediaJobStore? store = null)
    {
        this.store = store;
        this.comfy = comfy;
        this.config = config;
        this.log = log;
        this.raster = raster;
        // Прогрев: к первому вопросу каталога ответ ComfyUI обычно уже есть, и поставщик
        // не мигает «скрыт» на старте
        if (LocalMediaOptions.IsEnabled(config)) StartProbe();
    }

    // Не блокирует никогда: отдаёт последний известный ответ, а устаревший освежает в фоне.
    // Синхронное ожидание опроса здесь держало бы поток пула на каждом запросе каталога
    public bool Available
    {
        get
        {
            if (!LocalMediaOptions.IsEnabled(config)) return false;
            lock (_probeGate)
            {
                if (DateTime.UtcNow - _probedAt >= ProbeTtl) StartProbe();
                return _alive;
            }
        }
    }

    public bool Configured => LocalMediaOptions.IsEnabled(config);

    // Задача опроса в процессе
    internal Task? Probing
    {
        get { lock (_probeGate) return _probing; }
    }

    private void StartProbe()
    {
        lock (_probeGate) _probing ??= ProbeAsync();
    }

    private async Task ProbeAsync()
    {
        // Опрос уходит с потока вызывающего: тот держит блокировку и ждать не должен
        await Task.Yield();
        try
        {
            await QueueLengthAsync(CancellationToken.None);
        }
        finally
        {
            lock (_probeGate) _probing = null;
        }
    }

    // Любой ответ очереди (опрос, котировка, запуск) — свежее знание о живости ComfyUI
    private void Remember(bool alive)
    {
        lock (_probeGate)
        {
            _alive = alive;
            _probedAt = DateTime.UtcNow;
        }
    }

    public async Task<int?> QueueLengthAsync(CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(ProbeTimeout);
        try
        {
            var length = (await comfy.GetQueueAsync(timeout.Token)).Length;
            Remember(alive: true);
            return length;
        }
        catch (ComfyException)
        {
            Remember(alive: false);
            return null;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            Remember(alive: false);
            return null;
        }
    }

    public async Task<IReadOnlyList<string>?> ListLorasAsync(CancellationToken ct)
    {
        if (!LocalMediaOptions.Read(config).Enabled) return null;
        try
        {
            var loras = await comfy.ListLorasAsync(ct);
            Remember(alive: true);
            return loras;
        }
        catch (ComfyException)
        {
            Remember(alive: false);
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

        // Дорисовка за края: поля заливаются серым на холсте, размер результата — по холсту
        (int Width, int Height)? canvas = null;
        if (request.Pad is { } pad)
        {
            if (request.Op != LocalImageOp.Edit || raster is null || request.EraseMask is not null)
                return LocalImageSubmitted.Fail("Дорисовка за края здесь недоступна.");
            if (pad.Left < 0 || pad.Top < 0 || pad.Right < 0 || pad.Bottom < 0
                || pad.Left + pad.Top + pad.Right + pad.Bottom == 0)
                return LocalImageSubmitted.Fail("Поля дорисовки заданы неверно.");
            var probe = raster.Probe(images[0]);
            if (probe is null) return LocalImageSubmitted.Fail("Картинка 1 — не PNG, JPEG или WebP.");
            var padded = raster.Apply(images[0], [new PadOp(probe.DisplayWidth + pad.Left + pad.Right,
                probe.DisplayHeight + pad.Top + pad.Bottom, OutpaintFill, pad.Left, pad.Top)],
                new ImageEncodeSpec(ImageEncodeFormat.Png));
            if (!padded.Ok)
                return LocalImageSubmitted.Fail("Холст не удалось расширить: " + padded.Message,
                    busy: padded.Error == RasterError.Busy);
            images = [padded.Image!.Bytes, .. images.Skip(1)];
            canvas = OutpaintSize(padded.Image.Width, padded.Image.Height);
        }

        // Вне маски вернуть исходник: маска — PNG того же размера, что холст; только у Edit,
        // несовместима со стиранием кистью и дорисовкой; пустой массив — «маски нет»
        if (request.KeepMask is { Length: > 0 } keepMask)
        {
            if (request.Op != LocalImageOp.Edit || request.EraseMask is not null || request.Pad is not null)
                return LocalImageSubmitted.Fail("Возврат исходника вне маски — только для правки без стирания и дорисовки.");
            if (keepMask.Length > LocalMediaService.MaxInputBytes)
                return LocalImageSubmitted.Fail($"Маска больше {LocalMediaService.MaxInputBytes / 1024 / 1024} МБ.");
            if (!ImageDimensions.IsPng(keepMask))
                return LocalImageSubmitted.Fail("Маска — не PNG.");
            var maskSize = ImageDimensions.Read(keepMask);
            var canvasSize = ImageDimensions.Read(images[0]);
            if (maskSize is null || canvasSize is null || maskSize != canvasSize)
                return LocalImageSubmitted.Fail("Маска не совпадает размером с первой картинкой.");
        }

        // Вуаль (ADR-028, решение 12): место правки закрашивается серым на КОПИИ холста, а не
        // образцом. Только у Edit, несовместима со стиранием, дорисовкой и кропом (у кропа модель
        // видит кроп, а склейка «вне маски — исходник» идёт по KeepMask)
        byte[]? veiledBytes = null;
        if (request.VeilMask is { Length: > 0 } veilMask)
        {
            if (request.Op != LocalImageOp.Edit || request.EraseMask is not null || request.Pad is not null || request.Crop is not null)
                return LocalImageSubmitted.Fail("Вуаль — только для правки без стирания, дорисовки и подстановки на лицо.");
            if (raster is null)
                return LocalImageSubmitted.Fail("Вуаль здесь недоступна.");
            var veiled = raster.VeilMasked(images[0], veilMask, VeilAlpha);
            if (!veiled.Ok)
                return LocalImageSubmitted.Fail("Вуаль не удалось наложить: " + veiled.Message,
                    busy: veiled.Error == RasterError.Busy);
            veiledBytes = veiled.Image!.Bytes;
        }

        // Кроп вокруг цели (ADR-028, решение 12): модель видит кроп, а не маску-образец.
        // Только у Edit, вместе с KeepMask; прямоугольник — внутри холста, X/Y/Width/Height
        // кратны 8, размер увеличения — кратно 16 (латент VAE). Кроп, прижатый к правому/нижнему
        // краю холста, может быть некратен 8 по ширине/высоте: округление вниз срезало бы маску
        // у края, а узлам ImageCrop/CropMask кратность не нужна (step: 1)
        if (request.Crop is { } crop)
        {
            if (request.Op != LocalImageOp.Edit || request.KeepMask is not { Length: > 0 })
                return LocalImageSubmitted.Fail("Кроп вокруг цели — только для правки с возвратом исходника вне маски.");
            if (crop.Width <= 0 || crop.Height <= 0)
                return LocalImageSubmitted.Fail("Размер кропа должен быть положительным.");
            var canvasSize = ImageDimensions.Read(images[0]);
            if (canvasSize is null)
                return LocalImageSubmitted.Fail("Кроп выходит за пределы холста.");
            var (canvasWidth, canvasHeight) = canvasSize.Value;
            if (crop.X % 8 != 0 || crop.Y % 8 != 0
                || (crop.Width % 8 != 0 && crop.X + crop.Width != canvasWidth)
                || (crop.Height % 8 != 0 && crop.Y + crop.Height != canvasHeight))
                return LocalImageSubmitted.Fail("Координаты и размер кропа должны быть кратны 8.");
            if (crop.ScaledWidth % 16 != 0 || crop.ScaledHeight % 16 != 0)
                return LocalImageSubmitted.Fail("Размер увеличения кропа должен быть кратно 16.");
            if (crop.X < 0 || crop.Y < 0
                || crop.X + crop.Width > canvasWidth || crop.Y + crop.Height > canvasHeight)
                return LocalImageSubmitted.Fail("Кроп выходит за пределы холста.");
        }

        if (request.Lora is { } requested)
        {
            var name = requested.Name ?? "";
            if (name.Length == 0 || name.Contains("..") || name.StartsWith('/') || name.StartsWith('\\') || name.Contains(':'))
                return LocalImageSubmitted.Fail($"Имя LoRA «{name}» недопустимо.");
            if (requested.Strength is not (>= 0 and <= 2))
                return LocalImageSubmitted.Fail("Сила LoRA должна быть от 0 до 2.");
            IReadOnlyList<string> known;
            try
            {
                known = await comfy.ListLorasAsync(ct);
            }
            catch (ComfyException ex)
            {
                Remember(alive: false);
                return LocalImageSubmitted.Fail($"{ex.Message}. Не удалось получить список LoRA, попробуйте позже.", busy: true);
            }
            if (!known.Contains(name, StringComparer.Ordinal))
                return LocalImageSubmitted.Fail($"LoRA «{name}» не найдена в ComfyUI");
        }

        ComfyQueueState queue;
        try
        {
            queue = await comfy.GetQueueAsync(ct);
            Remember(alive: true);
        }
        catch (ComfyException ex)
        {
            Remember(alive: false);
            return LocalImageSubmitted.Fail($"{ex.Message}. Попробуйте позже или выберите другого поставщика.", busy: true);
        }
        if (queue.Length >= options.MaxComfyQueue)
            return LocalImageSubmitted.Fail($"Очередь локальной видеокарты занята ({queue.Length} задач). "
                + "Попробуйте позже или выберите другого поставщика.", busy: true);

        var id = Guid.NewGuid().ToString("N");
        var jobId = LocalMediaService.JobIdPrefix + id;
        var prefix = $"{ComfyWorkflows.OutputFolder}/{OutputStem}{id}";
        var seed = request.Seed is >= 0 ? request.Seed.Value : Random.Shared.NextInt64(1, 1L << 50);
        var count = Math.Clamp(request.Count, 1, ComfyWorkflows.MaxCount);

        // Всё, что ушло в input ComfyUI, — лица людей: записываем в стор, чтобы убрала общая чистка
        var uploaded = new List<string>();
        string? promptId = null;
        try
        {
            var names = new List<string>();
            for (var k = 0; k < images.Count; k++)
            {
                var ext = ImageFormatSniffer.DetectExtension(images[k]);
                if (ext is null) return LocalImageSubmitted.Fail($"Картинка {k + 1} — не PNG, JPEG или WebP.");
                // Холст (images[0]) с кропом лица не ужимается JPEG до потолка загрузки узла:
                // его лимит поднимается до MaxMediaInputBytes (90 МБ); остальные входы — по-прежнему 30 МБ
                var limit = k == 0 && request.Crop is not null
                    ? LocalMediaService.MaxMediaInputBytes
                    : LocalMediaService.MaxInputBytes;
                if (images[k].Length > limit)
                    return LocalImageSubmitted.Fail($"Картинка {k + 1} больше {limit / 1024 / 1024} МБ.");
                names.Add(await UploadTrackedAsync(uploaded, images[k], $"{jobId}-in{k + 1}{ext}", ct));
            }

            // Фото эталона — только узлу сходства; тот же префикс, что у входов, чтобы забрала чистка
            var faceRefs = new List<string>();
            if (request.Op is LocalImageOp.Edit or LocalImageOp.Generate && names.Count > 0)
                for (var k = 0; k < request.FaceReferences?.Count; k++)
                {
                    var photo = request.FaceReferences[k];
                    var ext = ImageFormatSniffer.DetectExtension(photo);
                    if (ext is null) return LocalImageSubmitted.Fail($"Фото эталона {k + 1} — не PNG, JPEG или WebP.");
                    if (photo.Length > LocalMediaService.MaxInputBytes)
                        return LocalImageSubmitted.Fail($"Фото эталона {k + 1} больше {LocalMediaService.MaxInputBytes / 1024 / 1024} МБ.");
                    faceRefs.Add(await UploadTrackedAsync(uploaded, photo, $"{jobId}-ref{k + 1}{ext}", ct));
                }

            // Маска «вне маски вернуть исходник»: тот же префикс, что у входов, чтобы забрала чистка
            string? maskName = null;
            if (request.KeepMask is { Length: > 0 } maskBytes)
                maskName = await UploadTrackedAsync(uploaded, maskBytes, $"{jobId}-mask.png", ct);

            // Вуаль: копия холста с серой заливкой — тем же префиксом, чтобы забрала чистка
            string? modelCanvas = null;
            if (veiledBytes is not null)
                modelCanvas = await UploadTrackedAsync(uploaded, veiledBytes, $"{jobId}-veil.png", ct);

            var lora = request.Lora is { } l ? (l.Name, l.Strength) : ((string, double)?)null;
            var graph = request.Op switch
            {
                LocalImageOp.Generate when names.Count == 0 => GenerateGraph(prompt, request, seed, count, prefix),
                // Генерация по образцам — граф правки на пустом холсте нужного размера
                LocalImageOp.Generate => ComfyWorkflows.EditImage(prompt, names, CanvasFor(request), seed, prefix, lora, faceRefs),
                LocalImageOp.Edit => ComfyWorkflows.EditImage(prompt, names, canvas, seed, prefix, lora, faceRefs, maskName, request.Crop, modelCanvas),
                _ => ComfyWorkflows.FaceDetail(names[0], seed, prefix),
            };
            var queued = await comfy.QueuePromptAsync(graph, ct);
            promptId = queued.PromptId;
            return new LocalImageSubmitted(queued.PromptId, queue.Length, EtaSeconds(request.Op, count, names.Count), null);
        }
        catch (ComfyException ex)
        {
            log.LogWarning("ComfyUI не принял задачу редактора {Op}: {Error}", request.Op, ex.Message);
            return LocalImageSubmitted.Fail(ex.Message, busy: true);
        }
        finally
        {
            // Принятая задача ещё стоит в очереди и читает входы — чистка ждёт; непринятая — убирается сразу
            RegisterInputs(jobId, uploaded, promptId is null ? null : SubmitCleanupDelay);
        }
    }

    // Входы принятой задачи не трогаем, пока она могла стоять в очереди и считаться
    private static readonly TimeSpan SubmitCleanupDelay = TimeSpan.FromHours(2);
    private static readonly TimeSpan FacePoseCleanupDelay = TimeSpan.FromMinutes(10);
    // Владелец записей редактора в сторе: чужим задачам local-media они неотличимы от несуществующих
    private const string StoreOwner = "image-editor";

    private async Task<string> UploadTrackedAsync(List<string> uploaded, byte[] bytes, string fileName, CancellationToken ct)
    {
        var name = await comfy.UploadImageAsync(bytes, fileName, ct);
        uploaded.Add(name);
        return name;
    }

    // Запись без результата: задача уже «завершена», а CleanAfter откладывает чистку её входов.
    // LocalMediaCleanup подбирает её по обычным правилам; своего чистильщика здесь нет
    private void RegisterInputs(string jobId, IReadOnlyList<string> inputs, TimeSpan? cleanupDelay)
    {
        if (store is null || inputs.Count == 0) return;
        var now = DateTime.UtcNow;
        store.Add(new LocalMediaJob
        {
            Id = jobId,
            OwnerId = StoreOwner,
            Op = LocalMediaOps.EditorInputs,
            Status = LocalMediaStatuses.Completed,
            CreatedAt = now,
            FinishedAt = now,
            ComfyInputs = [.. inputs],
            CleanAfter = cleanupDelay is { } delay ? now + delay : null,
        });
    }

    private void ReleaseCleanup(string jobId) => store?.Update(jobId, StoreOwner, j => j.CleanAfter = null);

    // Холст генерации: точный размер формата, иначе по пропорции из таблицы Qwen-Image
    internal static (int Width, int Height) CanvasFor(LocalImageRequest request) =>
        request.Width is > 0 && request.Height is > 0 ? (request.Width.Value, request.Height.Value) : SizeFor(request.Aspect);

    private static System.Text.Json.Nodes.JsonObject GenerateGraph(string prompt, LocalImageRequest request, long seed, int count,
        string prefix)
    {
        var size = CanvasFor(request);
        return ComfyWorkflows.GenerateImage(prompt, "", size.Width, size.Height, seed, ComfyWorkflows.DefaultSteps,
            count, prefix);
    }

    // Заливка полей дорисовки — тот же нейтральный серый, что у стирания кистью: «замени серое»
    // Qwen-Image понимает
    internal const string OutpaintFill = "#808080";

    // Площадь результата дорисовки — как у квадрата таблицы Qwen-Image (1328²)
    private const double OutpaintArea = 1328d * 1328d;

    // Размер результата дорисовки: пропорция расширенного холста, площадь как у таблицы Qwen-Image,
    // стороны кратны 16 (латент VAE 8× и патчи 2×2)
    internal static (int Width, int Height) OutpaintSize(int width, int height)
    {
        var scale = Math.Sqrt(OutpaintArea / ((double)width * height));
        return (Snap(width * scale), Snap(height * scale));

        static int Snap(double side) => Math.Max(16, (int)Math.Round(side / 16, MidpointRounding.AwayFromZero) * 16);
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

            var similarity = ParseSimilarity(history.Texts);
            var files = new List<LocalImageFile>();
            foreach (var file in history.Files)
            {
                var type = LocalMediaService.ContentTypeOf(Path.GetExtension(file.FileName));
                if (type is null || !type.StartsWith("image/", StringComparison.Ordinal)) continue;
                files.Add(new LocalImageFile(await comfy.DownloadAsync(file, ct), type, similarity));
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

    private const string SimilarityKey = "ccs_face_similarity=";

    // Граф даёт одну картинку на прогон, поэтому значение относится к ней; none, мусор и
    // отсутствие строки — не ошибка, а «сходство не посчитано»
    internal static double? ParseSimilarity(IReadOnlyList<string> texts)
    {
        foreach (var text in texts)
        {
            var line = text.Trim();
            if (!line.StartsWith(SimilarityKey, StringComparison.Ordinal)) continue;
            return double.TryParse(line[SimilarityKey.Length..].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
                && double.IsFinite(value)
                ? value
                : null;
        }
        return null;
    }

    // Потолок ожидания расчёта — как у воркера сходства
    private static readonly TimeSpan FacePoseTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan FacePosePollInterval = TimeSpan.FromMilliseconds(500);
    internal TimeSpan FacePoseTimeoutOverride { get; set; } = FacePoseTimeout;

    public async Task<LocalFacePoseResult> FacePoseAsync(IReadOnlyList<byte[]> images, CancellationToken ct)
    {
        var options = LocalMediaOptions.Read(config);
        var uploaded = new List<string>();
        if (!options.Enabled) return LocalFacePoseResult.Fail("Локальные модели выключены на этом сервере.");
        if (images.Count is < 1 or > ComfyWorkflows.MaxFacePoseImages)
            return LocalFacePoseResult.Fail($"Для ракурса нужно от 1 до {ComfyWorkflows.MaxFacePoseImages} фото.");

        // Фоновая разметка не задерживает генерации человека: подаём только в пустую очередь
        var length = await QueueLengthAsync(ct);
        if (length is null) return LocalFacePoseResult.Fail("ComfyUI не ответил. Попробуйте позже.", busy: true);
        if (length > 0) return LocalFacePoseResult.Fail("Очередь локальной видеокарты занята. Попробуйте позже.", busy: true);

        var jobId = LocalMediaService.JobIdPrefix + Guid.NewGuid().ToString("N");
        string ticket;
        var names = new List<string>();
        var queuedOk = false;
        try
        {
            for (var k = 0; k < images.Count; k++)
            {
                var ext = ImageFormatSniffer.DetectExtension(images[k]);
                if (ext is null) return LocalFacePoseResult.Fail($"Фото {k + 1} — не PNG, JPEG или WebP.");
                if (images[k].Length > LocalMediaService.MaxInputBytes)
                    return LocalFacePoseResult.Fail($"Фото {k + 1} больше {LocalMediaService.MaxInputBytes / 1024 / 1024} МБ.");
                names.Add(await UploadTrackedAsync(uploaded, images[k], $"{jobId}-pose{k + 1}{ext}", ct));
            }
            ticket = (await comfy.QueuePromptAsync(ComfyWorkflows.FacePose(names), ct)).PromptId;
            queuedOk = true;
        }
        catch (ComfyException ex)
        {
            Remember(alive: false);
            log.LogWarning("ComfyUI не принял задачу ракурса: {Error}", ex.Message);
            return LocalFacePoseResult.Fail(ex.Message, busy: true);
        }
        finally
        {
            RegisterInputs(jobId, uploaded, queuedOk ? FacePoseCleanupDelay : null);
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(FacePoseTimeoutOverride);
        try
        {
            while (true)
            {
                var history = await comfy.GetHistoryAsync(ticket, timeout.Token);
                if (history is { Failed: true })
                {
                    ReleaseCleanup(jobId);
                    return LocalFacePoseResult.Fail(history.Error ?? "ComfyUI завершил задачу ошибкой.");
                }
                if (history is { Completed: true })
                {
                    // Разметка короткая: расчёт кончился — входы не нужны, чистка заберёт их в свой проход
                    ReleaseCleanup(jobId);
                    return ParseFacePoses(history.Texts, names) is { } poses
                        ? new LocalFacePoseResult(poses, null)
                        : LocalFacePoseResult.Fail("Узел ракурса вернул неожиданный ответ.");
                }
                await Task.Delay(FacePosePollInterval, timeout.Token);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return LocalFacePoseResult.Fail("Расчёт ракурса не уложился в отведённое время.", busy: true);
        }
        catch (ComfyException ex)
        {
            Remember(alive: false);
            return LocalFacePoseResult.Fail(ex.Message, busy: true);
        }
    }

    // Потолок ожидания детекции — как у воркера сходства: человек ждёт результат, но видеокарта
    // может быть занята долгой задачей — по таймауту Busy, а не исключение
    private static readonly TimeSpan FaceDetectTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan FaceDetectPollInterval = TimeSpan.FromMilliseconds(500);
    internal TimeSpan FaceDetectTimeoutOverride { get; set; } = FaceDetectTimeout;
    private static readonly TimeSpan FaceDetectCleanupDelay = TimeSpan.FromMinutes(10);

    public async Task<LocalFaceDetectResult> DetectFacesAsync(byte[] image, CancellationToken ct)
    {
        var options = LocalMediaOptions.Read(config);
        if (!options.Enabled) return new LocalFaceDetectResult(0, 0, [], "Локальные модели выключены на этом сервере.");
        if (image.Length > LocalMediaService.MaxInputBytes)
            return new LocalFaceDetectResult(0, 0, [], $"Картинка больше {LocalMediaService.MaxInputBytes / 1024 / 1024} МБ.");
        var ext = ImageFormatSniffer.DetectExtension(image);
        if (ext is null) return new LocalFaceDetectResult(0, 0, [], "Картинка — не PNG, JPEG, WebP или GIF.");

        // Нормализация EXIF: применяем EXIF-поворот и обнуляем тег на стороне бэкенда, чтобы размеры
        // и рамки не зависели от того, как загрузчик узла/воркера трактует EXIF (воркер cv2 применяет
        // поворот, а ImageDimensions — нет). Orientation 1 (или нет EXIF) — байты не трогаем.
        var normalized = NormalizeExif(image);

        var jobId = LocalMediaService.JobIdPrefix + Guid.NewGuid().ToString("N");
        var uploaded = new List<string>();
        string ticket;
        var queuedOk = false;
        try
        {
            // Лицо = биометрия: загружаем как входы и записываем на общую чистку
            var name = await UploadTrackedAsync(uploaded, normalized, $"{jobId}-detect{ext}", ct);
            // Детекцию запускает человек — в голову очереди
            ticket = (await comfy.QueuePromptAsync(ComfyWorkflows.FaceDetect(name), ct, front: true)).PromptId;
            queuedOk = true;
        }
        catch (ComfyException ex)
        {
            Remember(alive: false);
            log.LogWarning("ComfyUI не принял задачу детекции лиц: {Error}", ex.Message);
            return new LocalFaceDetectResult(0, 0, [], ex.Message, true);
        }
        finally
        {
            RegisterInputs(jobId, uploaded, queuedOk ? FaceDetectCleanupDelay : null);
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(FaceDetectTimeoutOverride);
        try
        {
            while (true)
            {
                var history = await comfy.GetHistoryAsync(ticket, timeout.Token);
                if (history is { Failed: true })
                {
                    ReleaseCleanup(jobId);
                    return new LocalFaceDetectResult(0, 0, [], history.Error ?? "ComfyUI завершил задачу ошибкой.");
                }
                if (history is { Completed: true })
                {
                    ReleaseCleanup(jobId);
                    return ParseFaces(history.Texts, normalized) is { } result
                        ? result
                        : new LocalFaceDetectResult(0, 0, [], "Узел детекции вернул неожиданный ответ.");
                }
                await Task.Delay(FaceDetectPollInterval, timeout.Token);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new LocalFaceDetectResult(0, 0, [], "Детекция лиц не уложилась в отведённое время.", true);
        }
        catch (ComfyException ex)
        {
            Remember(alive: false);
            return new LocalFaceDetectResult(0, 0, [], ex.Message, true);
        }
    }

    // Нормализация EXIF: применяем EXIF-поворот и обнуляем тег на стороне бэкенда, чтобы размеры
    // и рамки не зависели от того, как загрузчик узла/воркера трактует EXIF. Orientation 1 (или нет EXIF) —
    // байты не трогаем (перекодировка впустую). raster = null (подсистема images выключена) — как раньше.
    // Сбой Apply (потолок мегапикселей, битый файл) — исходные байты: детекция по ним откажет сама.
    private byte[] NormalizeExif(byte[] image)
    {
        if (raster is null) return image;
        var probe = raster.Probe(image);
        if (probe is null || probe.Orientation <= 1) return image;
        var outcome = raster.Apply(image, [], null);
        return outcome.Image is { } img ? img.Bytes : image;
    }

    private const string FaceDetectKey = "ccs_face_detect=";

    // Строка узла: ccs_face_detect={"width":W,"height":H,"faces":[{"bbox":[x1,y1,x2,y2],"score":0.87,"small":false}, ...]}
    // либо ccs_face_detect=error. Лиц нет — faces пуст (нормальный результат, без Error). Битый JSON,
    // нечисловой bbox, нулевой/отрицательный размер, x2<=x1 — «неожиданный ответ» (null), а не «лиц нет».
    // Сверка W/H: воркер (cv2) может повернуть JPEG по EXIF, а ImageDimensions — нет; не совпало и
    // реальный размер известен → null; реальный размер неизвестен (null) — доверяем воркеру.
    internal static LocalFaceDetectResult? ParseFaces(IReadOnlyList<string> texts, byte[] image)
    {
        foreach (var text in texts)
        {
            var line = text.Trim();
            if (!line.StartsWith(FaceDetectKey, StringComparison.Ordinal)) continue;
            var rest = line[FaceDetectKey.Length..].Trim();
            if (rest == "error") return new LocalFaceDetectResult(0, 0, [], "Узел детекции вернул ошибку.");
            try
            {
                if (System.Text.Json.Nodes.JsonNode.Parse(rest) is not System.Text.Json.Nodes.JsonObject answer)
                    return null;
                var width = Int(answer["width"]);
                var height = Int(answer["height"]);
                if (width is null || height is null || width <= 0 || height <= 0) return null;
                var faces = new List<LocalDetectedFace>();
                if (answer["faces"] is System.Text.Json.Nodes.JsonArray list)
                {
                    for (var i = 0; i < list.Count; i++)
                    {
                        if (list[i] is not System.Text.Json.Nodes.JsonObject face) return null;
                        if (face["bbox"] is not System.Text.Json.Nodes.JsonArray bbox || bbox.Count != 4) return null;
                        var x1 = Int(bbox[0]); var y1 = Int(bbox[1]); var x2 = Int(bbox[2]); var y2 = Int(bbox[3]);
                        if (x1 is null || y1 is null || x2 is null || y2 is null) return null;
                        if (x2 <= x1 || y2 <= y1) return null;
                        faces.Add(new LocalDetectedFace(i + 1, x1.Value, y1.Value, x2.Value, y2.Value,
                            Score(face), Small(face)));
                    }
                }
                else return null;

                // Сверка системы координат: реальный размер байт против ответа воркера
                var real = ImageDimensions.Read(image);
                if (real is { } size && (size.Width != width || size.Height != height))
                    return new LocalFaceDetectResult(width.Value, height.Value, [], "Не удалось сопоставить лица с картинкой.");
                return new LocalFaceDetectResult(width.Value, height.Value, faces);
            }
            catch (System.Text.Json.JsonException)
            {
                return null;
            }
        }
        return null;

        static int? Int(System.Text.Json.Nodes.JsonNode? node) =>
            node is System.Text.Json.Nodes.JsonValue v && v.TryGetValue<int>(out var i) ? i : null;

        static double Score(System.Text.Json.Nodes.JsonObject face) =>
            face["score"] is System.Text.Json.Nodes.JsonValue v && v.TryGetValue<double>(out var d) && double.IsFinite(d) ? d : 0;

        static bool Small(System.Text.Json.Nodes.JsonObject face) =>
            face["small"] is System.Text.Json.Nodes.JsonValue v && v.TryGetValue<bool>(out var b) && b;
    }

    private const string FacePoseKey = "ccs_face_pose=";

    // Строка узла: ccs_face_pose={"файл.png": {"yaw":..,"pitch":..,"roll":..}, ...}. Лицо не найдено —
    // все три поля null; ccs_face_pose=error — сбой воркера. Оси читаются по именам полей, порядок
    // ключей и лишние поля значения не имеют. Ключ файла — имя из graph («подпапка/файл») либо путь к нему: сверяем по имени файла.
    // Ответ без записи для фото, с полуполным или нечисловым углом негоден (null) — это не «лица нет»
    internal static IReadOnlyList<LocalFacePose?>? ParseFacePoses(IReadOnlyList<string> texts, IReadOnlyList<string> names)
    {
        foreach (var text in texts)
        {
            var line = text.Trim();
            if (!line.StartsWith(FacePoseKey, StringComparison.Ordinal)) continue;
            try
            {
                if (System.Text.Json.Nodes.JsonNode.Parse(line[FacePoseKey.Length..]) is not System.Text.Json.Nodes.JsonObject answer)
                    return null;
                var poses = new List<LocalFacePose?>(names.Count);
                foreach (var name in names)
                {
                    var entry = answer.FirstOrDefault(kv => kv.Key == name || Path.GetFileName(kv.Key) == Path.GetFileName(name)).Value;
                    if (entry is not System.Text.Json.Nodes.JsonObject o) return null;
                    var yaw = Angle(o, "yaw");
                    var pitch = Angle(o, "pitch");
                    var roll = Angle(o, "roll");
                    if (yaw is not null && pitch is not null && roll is not null) poses.Add(new LocalFacePose(yaw.Value, pitch.Value, roll.Value));
                    else if (o["yaw"] is null && o["pitch"] is null && o["roll"] is null) poses.Add(null);
                    else return null;
                }
                return poses;
            }
            catch (System.Text.Json.JsonException)
            {
                return null;
            }
        }
        return null;

        static double? Angle(System.Text.Json.Nodes.JsonObject o, string name) =>
            o[name] is System.Text.Json.Nodes.JsonValue v && v.TryGetValue<double>(out var d) && double.IsFinite(d) ? d : null;
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
