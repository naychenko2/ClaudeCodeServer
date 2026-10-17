using System.Text.RegularExpressions;
using ClaudeHomeServer.Services.Media;

namespace ClaudeHomeServer.Services.ImageEditor;

// Поставщик «Локальные модели» (ADR-018, раздел «Локальные модели»): Qwen-Image 2.1 и
// FaceDetailer в ComfyUI на своей видеокарте через Core-шов ILocalImageMedia — вертикаль Images
// модулю не видна. Денег нет: котировка — ноль в единицах free плюс время по таблице замеров и
// длина очереди GPU. Доступен всем; нет шва (Images выключена) или тумблера LocalMedia:Enabled —
// поставщик не заведён и скрыт в каталоге, а лежащий ComfyUI — лишь «недоступен» (IImageEditor.Registered).
//
// Канала маски у Qwen-Image нет: маска и размеченная копия уходят образцами с ролями в запросе
// (MaskSupport.AsReference), порядок картинок задаёт EditRequestComposer. Исключение — «удали»
// кистью: маску-образец модель не понимает (живой прогон 2026-09-26: стёрла фон, а отмеченное
// оставила), поэтому область маски закрашивается на холсте серым, и модель заменяет серое фоном.
// Стирание идёт одним холстом, без образцов и размеченной копии — на копии виден стираемый
// предмет. Варианты правки и генерации идут прогонами по очереди, по одному на вариант: общая
// очередь ComfyUI не забивается ожиданиями одного человека, а каждый готовый вариант сразу уходит
// отчётом EditProgress.Ready. Граф ComfyWorkflows при этом не трогается — он общий с MCP-инструментом
// local_generate_image, где batch_size остаётся.
public sealed class LocalImageEditor(ILocalImageMedia? media) : IImageEditor, IImageEditQuoter
{
    public const string ProviderKey = "local";
    // «Качественно» (25 шагов) и «быстро» (Turbo, 8 шагов); умолчание авто-выбора — «качественно»
    public const string QwenImage = LocalImageModels.Quality;
    public const string QwenImageTurbo = LocalImageModels.Turbo;
    public const string FaceDetailer = "face-detailer";

    // Потолок одного запуска с ожиданием очереди: четыре варианта правки ≈4 мин, плюс чужие прогоны
    private static readonly TimeSpan JobCeiling = TimeSpan.FromMinutes(20);

    // Потолок картинок одного прогона графа правки (ComfyWorkflows.MaxEditImages): холст и образцы
    public const int MaxImages = 16;

    internal TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(2);
    internal TimeSpan Ceiling { get; set; } = JobCeiling;

    public string Key => ProviderKey;
    public string Label => "Локальные модели";
    public string PriceUnit => ImageEditPriceUnits.Free;
    public bool Enabled => media?.Available == true;
    public bool Registered => media?.Configured == true;
    public IReadOnlyList<ImageEditModelInfo> Models => Catalog;

    // Формат (ADR-025): рекомендованные размеры Qwen-Image по пропорциям — зеркало
    // ComfyWorkflows.ImageSizes (Images; модулю он не виден, расхождение ловит MediaFormatDriversTests.Размеры_Qwen_в_каталоге_совпадают_с_ComfyWorkflows).
    // Размер один на пропорцию — «Высокое» и «Макс» серые. «Точно…» только у генерации (холст правки —
    // исходник): кратно 16 (латент VAE 8× и патчи 2×2), потолок — как у лимита входа каталога.
    public static readonly IReadOnlyDictionary<string, FormatSize> QwenSizes = new Dictionary<string, FormatSize>(StringComparer.Ordinal)
    {
        ["1:1"] = new(1328, 1328),
        ["16:9"] = new(1664, 928),
        ["9:16"] = new(928, 1664),
        ["4:3"] = new(1472, 1104),
        ["3:4"] = new(1104, 1472),
        ["3:2"] = new(1584, 1056),
        ["2:3"] = new(1056, 1584),
    };

    private static readonly MediaFormatCaps QwenFormat = new("Qwen-Image 2.1",
        [.. QwenSizes.Keys],
        [new FormatTierSpec(FormatTier.Standard, "~1,7 Мп", QwenSizes)],
        new ExactSizeSpec(Step: 16, MinSide: 256, MaxSide: 1664, MaxMegapixels: 1.8),
        null, "один размер ~1,7 Мп");

    // Генерация — до 4 вариантов, прогон на вариант; правка — до 16 картинок всего (холст и 15 образцов)
    private static readonly IReadOnlyList<ImageEditModelInfo> Catalog =
    [
        QwenModel(QwenImage, "Qwen-Image 2.1 — качественно"),
        QwenModel(QwenImageTurbo, "Qwen-Image 2.1 Turbo — быстро"),
        new(FaceDetailer, "Улучшить лица",
            new ImageEditCaps([ImageEditOp.EnhanceFaces], MaskSupport.None, MaxReferences: 0, MaxCount: 1, FaceByReferences: false),
            new ImageEditPriceHint(0, ImageEditPriceUnits.Free, "image")),
    ];

    private static ImageEditModelInfo QwenModel(string id, string label) =>
        new(id, label,
            new ImageEditCaps([ImageEditOp.Generate, ImageEditOp.Edit, ImageEditOp.Inpaint, ImageEditOp.Outpaint],
                MaskSupport.Veil,
                MaxReferences: 15, MaxCount: 4, FaceByReferences: true, MaxCharacterPhotos: 1, Format: QwenFormat),
            new ImageEditPriceHint(0, ImageEditPriceUnits.Free, "image"));

    // Умолчание авто-выбора — «качественно» (решение Андрея 2026-10-17); Turbo — по явному выбору человека
    public ImageEditModelInfo? PickModel(ImageEditOp op, EditMode mode, EditTraits traits) => op switch
    {
        ImageEditOp.EnhanceFaces => Catalog.First(m => m.Id == FaceDetailer),
        ImageEditOp.Generate or ImageEditOp.Edit or ImageEditOp.Inpaint or ImageEditOp.Outpaint => Catalog[0],
        _ => null,
    };

    // ── Котировка ────────────────────────────────────────────────────────────────

    public int? ExpectedSeconds(ImageEditModelInfo model) =>
        media?.EtaSeconds(model.Id == FaceDetailer ? LocalImageOp.FaceDetail : LocalImageOp.Edit, 1, 1, QwenVariant(model.Id));

    // Id модели каталога → вариант шва; у FaceDetailer варианта нет
    private static string? QwenVariant(string id) => id == FaceDetailer ? null : id;

    public async Task<ImageEditEstimateDto> EstimateAsync(
        ImageEditModelInfo model, ImageEditQuoteRequest request, CancellationToken ct)
    {
        if (media is null || !media.Available)
            throw new ImageEditProviderUnavailableException("Локальные модели сейчас недоступны");
        var queue = await media.QueueLengthAsync(ct)
            ?? throw new ImageEditProviderUnavailableException("Локальная видеокарта не отвечает (ComfyUI недоступен)");
        return new ImageEditEstimateDto(0, ImageEditPriceUnits.Free, false, ImageEditEstimateSources.Provider,
            EtaSeconds: Eta(model, request), QueueLength: queue);
    }

    // Время всех вариантов: и генерация, и правка идут прогоном на вариант
    private int? Eta(ImageEditModelInfo model, ImageEditQuoteRequest request)
    {
        var count = Math.Max(1, request.Count);
        if (model.Id == FaceDetailer) return media!.EtaSeconds(LocalImageOp.FaceDetail, 1, 1);
        // Картинок в запросе: холст, размеченная копия, маска, образцы и фото персонажа (одно,
        // MaxCharacterPhotos) — фронт считает в References только образцы, фото добавляет сервер
        var images = request.References + (request.HasAnnotations ? 1 : 0) + (request.HasMask ? 1 : 0)
                     + (request.HasCharacter ? 1 : 0);
        if (request.Op == ImageEditOp.Generate)
            return media!.EtaSeconds(LocalImageOp.Generate, 1, images, model.Id) * count;
        return media!.EtaSeconds(LocalImageOp.Edit, 1, images + 1, model.Id) * count;
    }

    // ── Запуск ───────────────────────────────────────────────────────────────────

    public async Task<ImageEditResult> RunAsync(ImageEditRequest req, IProgress<EditProgress> progress, CancellationToken ct)
    {
        if (media is null) return Fail(EditOutcome.Unavailable, "Локальные модели недоступны на этом сервере");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(Ceiling);
        var token = timeout.Token;

        var references = req.References.Select(r => r.Bytes).ToList();
        LocalImageRequest run;
        int runs;
        switch (req.Op)
        {
            case ImageEditOp.EnhanceFaces:
                if (req.Source is null) return Fail(EditOutcome.Failed, "Нет исходной картинки");
                // FaceDetailer образец не принимает: с персонажем в контексте лицо перерисуется «с нуля»
                // и сходство потеряется — честный отказ до постановки в очередь
                if (req.Character is not null)
                    return Fail(EditOutcome.Failed,
                        "Улучшить лица перерисует лицо без образца — сходство с персонажем пропадёт. " +
                        "Уберите персонажа из строки контекста, если всё равно нужно.");
                run = new LocalImageRequest(LocalImageOp.FaceDetail, "", [req.Source.Bytes], null, 1);
                runs = 1;
                break;
            case ImageEditOp.Generate:
                // Холст генерации по тексту не нужен: берутся только образцы человека. Прогон на
                // вариант и без образцов: первый вариант виден сразу, а не после всей пачки
                run = new LocalImageRequest(LocalImageOp.Generate, WithTrigger(req.Prompt, req), references, req.AspectRatio, 1,
                    Width: req.Format?.Width, Height: req.Format?.Height, Lora: LoraOf(req), FaceReferences: req.Character?.FacePhotos, Model: req.Model);
                runs = Math.Clamp(req.Count, 1, 4);
                break;
            case ImageEditOp.Edit or ImageEditOp.Inpaint
                when req.Source is not null && EraseMaskOf(req) is { } eraseMask:
                // Стирание клеится по маске (KeepMask): вне мазка — исходник, а не перерисованный
                // холст (дефект f0903cb5: без склейки модель меняла 99,8 % пикселей вне 30 px от мазка)
                run = new LocalImageRequest(LocalImageOp.Edit, ErasePrompt(req.Instruction), [req.Source.Bytes], null, 1,
                    EraseMask: eraseMask, KeepMask: req.KeepMask?.Bytes ?? KeepMaskOf(req), Model: req.Model);
                runs = Math.Clamp(req.Count, 1, 4);
                break;
            case ImageEditOp.Outpaint:
            {
                if (req.Source is null) return Fail(EditOutcome.Failed, "Нет исходной картинки");
                // Поля — явные или под пропорцию запроса; холст расширяет адаптер, граф — шаблон правки
                var size = ImageDimensions.Read(req.Source.Bytes);
                var spec = req.Outpaint ?? (size is { } s ? OutpaintSpec.ForAspect(s.Width, s.Height, req.AspectRatio) : null);
                if (spec is null) return Fail(EditOutcome.Failed, "Не удалось посчитать поля дорисовки");
                // Холст один, без образцов: образец с другой пропорцией сбил бы модель с полей
                run = new LocalImageRequest(LocalImageOp.Edit, OutpaintPrompt(req.Instruction), [req.Source.Bytes],
                    null, 1, Pad: new LocalImagePad(spec.Left, spec.Top, spec.Right, spec.Bottom), Model: req.Model);
                runs = Math.Clamp(req.Count, 1, 4);
                break;
            }
            case ImageEditOp.Edit or ImageEditOp.Inpaint:
                if (req.Source is null) return Fail(EditOutcome.Failed, "Нет исходной картинки");
                // Вуаль (ADR-028, решение 12): Qwen-Image перерисовывает белую маску-образец,
                // поэтому место правки уходит серой вуалью (VeilMask) на копии холста, а не образцом.
                // Исключения: подстановка на лицо (модель видит кроп, а не вуаль) и путь с
                // размеченной копией (вуаль там не проверена — вуаль не включаем, как раньше AsReference)
                run = new LocalImageRequest(LocalImageOp.Edit, WithTrigger(req.Prompt, req), [req.Source.Bytes, .. references], null, 1,
                    KeepMask: req.KeepMask?.Bytes ?? KeepMaskOf(req),
                    Crop: req.FaceCrop,
                    VeilMask: VeilMaskOf(req),
                    Lora: LoraOf(req), FaceReferences: req.Character?.FacePhotos, Model: req.Model);
                runs = Math.Clamp(req.Count, 1, 4);
                break;
            default:
                return Fail(EditOutcome.Failed, "Локальные модели так не умеют");
        }
        // Метка происхождения прогона (шаг 28): владелец, чат, вид=картинка, операция, тяжёлый ли.
        // Данные у движка есть из задачи (ImageEditJobService кладёт их в запрос); без задачи (тесты) — null.
        // Связь с источником (шаг 2): источник=image-editor, проект и job_id задачи
        if (req.OwnerId is { } owner)
            run = run with { Origin = new LocalJobOrigin(owner, req.SessionId, "image",
                ImageEditOpNames.OpName(req.Op), req.Heavy,
                ProjectId: req.ProjectId, Source: LocalJobOrigin.SourceImageEditor, SourceJobId: req.SourceJobId) };
        // Обрезать нельзя: молча пропал бы персонаж или образец — честный отказ до очереди
        if (run.Images.Count > MaxImages)
            return Fail(EditOutcome.Failed,
                $"Локальная модель берёт не больше {MaxImages} картинок за раз, а в запросе {run.Images.Count} " +
                "(исходник, пометки, маска, образцы и фото персонажа). Уберите лишние образцы");

        var images = new List<EditedImage>();
        var tickets = new List<string>();
        string? current = null;
        try
        {
            for (var i = 0; i < runs; i++)
            {
                var submitted = await media.SubmitAsync(run, token);
                if (submitted.Ticket is not { } ticket)
                {
                    var error = submitted.Error ?? "Локальная видеокарта не приняла задачу";
                    // Часть вариантов уже готова — отдаём их, а не выбрасываем
                    if (images.Count > 0) break;
                    return Fail(submitted.Busy ? EditOutcome.Unavailable : EditOutcome.Failed, error);
                }
                current = ticket;
                tickets.Add(ticket);
                var at = new EditProgress(EditStage.Queued, submitted.QueuePosition, i + 1, runs, submitted.EtaSeconds);
                progress.Report(at);

                var done = await WaitAsync(ticket, at, progress, token);
                current = null;
                if (done.State == LocalImageState.Failed)
                {
                    if (images.Count > 0) break;
                    return new ImageEditResult(EditOutcome.Failed, [], Free, false, RemoteId(tickets),
                        "Локальная модель не справилась: " + (done.Error ?? "ComfyUI завершил задачу ошибкой"));
                }
                // Готовый прогон уходит в отчёт сразу: вариант становится версией, не дожидаясь остальных
                List<EditedImage> ready = [.. done.Files.Select(f => new EditedImage(f.Bytes, f.ContentType, f.Similarity))];
                images.AddRange(ready);
                progress.Report(at with { Stage = EditStage.Downloading, QueuePosition = null, Ready = ready });
            }
        }
        catch (OperationCanceledException)
        {
            // Задачу, ещё ждущую в очереди, снимаем: чужое время GPU она не займёт
            if (current is not null) await media.CancelAsync(current, CancellationToken.None);
            if (ct.IsCancellationRequested) throw;
            if (images.Count == 0)
                return new ImageEditResult(EditOutcome.Failed, [], Free, false, RemoteId(tickets),
                    $"Локальная видеокарта не успела за {Ceiling.TotalMinutes:0} минут — очередь занята");
        }

        return images.Count == 0
            ? new ImageEditResult(EditOutcome.Failed, [], Free, false, RemoteId(tickets), "Локальная модель не вернула картинок")
            : new ImageEditResult(EditOutcome.Ok, images, Free, false, RemoteId(tickets), null);
    }

    // Режим B: LoRA персонажа плюс фото-образец. Только для Generate и обычной правки
    private static LocalImageLora? LoraOf(ImageEditRequest req) =>
        req.Character?.Lora is { } l ? new LocalImageLora(l.Name, l.Strength) : null;

    private static string WithTrigger(string prompt, ImageEditRequest req)
    {
        if (req.Character?.Lora is not { } l) return prompt;
        // Триггер уже есть в промпте отдельным словом (не обязательно в начале) — второй раз не добавляем
        var present = Regex.IsMatch(prompt, $@"(?<![\p{{L}}\p{{N}}_]){Regex.Escape(l.Trigger)}(?![\p{{L}}\p{{N}}_])", RegexOptions.IgnoreCase);
        return present ? prompt : $"photo of {l.Trigger}, {prompt}";
    }

    // at — отчёт о постановке прогона: его номер и ETA едут в каждой стадии ожидания
    private async Task<LocalImagePoll> WaitAsync(string ticket, EditProgress at, IProgress<EditProgress> progress,
        CancellationToken ct)
    {
        var stage = EditStage.Queued;
        int? position = null;
        // Снимок очереди GPU (шаг 28): живой, меняется каждый опрос — обновляем и вешаем на отчёт
        LocalGpuQueueInfo? gpu = null;
        while (true)
        {
            var poll = await media!.PollAsync(ticket, ct);
            if (poll.State is LocalImageState.Completed or LocalImageState.Failed) return poll;
            var next = poll.State == LocalImageState.Running ? EditStage.Running : EditStage.Queued;
            gpu = poll.Gpu;
            if (poll.Warning is null && (next != stage || poll.QueuePosition != position))
            {
                stage = next;
                position = poll.QueuePosition;
                progress.Report(at with { Stage = stage, QueuePosition = stage == EditStage.Queued ? position : null, Gpu = gpu });
            }
            await Task.Delay(PollInterval, ct);
        }
    }

    public async Task<bool> CancelRemoteAsync(string remoteId, CancellationToken ct)
    {
        if (media is null) return false;
        var any = false;
        foreach (var ticket in remoteId.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            any |= await media.CancelAsync(ticket, ct);
        return any;
    }

    // Маска стирания: просьба — стереть отмеченное; маска — каналом (вуаль/Native) или образцом
    // (AsReference). У вуали композитор кладёт маску в req.Mask, а не в References
    private static byte[]? EraseMaskOf(ImageEditRequest req) =>
        EditIntent.IsRemoval(req.Instruction)
            ? req.Mask?.Bytes ?? req.References.FirstOrDefault(r => r.Label == EditRequestComposer.MaskLabel)?.Bytes
            : null;

    // Маска «вне маски вернуть исходник»: маска есть (каналом или образцом), а размеченной копии
    // нет — иначе стрелки/рамки могут просить правку вне кисти, и склейка молча выбросила бы её
    private static byte[]? KeepMaskOf(ImageEditRequest req) =>
        req.References.Any(r => r.Label == EditRequestComposer.AnnotatedLabel)
            ? null
            : req.Mask?.Bytes ?? req.References.FirstOrDefault(r => r.Label == EditRequestComposer.MaskLabel)?.Bytes;

    // Вуаль (ADR-028, решение 12): серая заливка места правки на копии холста. Не включается при
    // подстановке на лицо (модель видит кроп) и на пути с размеченной копией (вуаль там не проверена)
    private static byte[]? VeilMaskOf(ImageEditRequest req) =>
        req.FaceCrop is not null || req.References.Any(r => r.Label == EditRequestComposer.AnnotatedLabel)
            ? null
            : req.Mask?.Bytes;

    internal const string EraseNote =
        "Серая заливка на картинке закрывает то, что нужно удалить. Замени серую область продолжением фона " +
        "вокруг неё, чтобы не было видно, что там что-то было. Ничего нового в эту область не добавляй. " +
        "Всё вне серой области оставь без изменений.";

    private static string ErasePrompt(string? instruction) =>
        string.IsNullOrWhiteSpace(instruction) ? EraseNote : $"{instruction.Trim()}\n\n{EraseNote}";

    internal const string OutpaintNote =
        "Серые поля по краям картинки — пустое место, которое нужно заполнить. Дострой в них сцену так, " +
        "чтобы она естественно продолжала картинку: тот же свет, перспектива, стиль и цвета, без рамок, " +
        "полос и видимых швов. Серого цвета в результате остаться не должно. Центральную часть картинки не меняй.";

    private static string OutpaintPrompt(string? instruction) =>
        string.IsNullOrWhiteSpace(instruction) ? OutpaintNote : $"{instruction.Trim()}\n\n{OutpaintNote}";

    private static readonly EditCost Free = new(0, ImageEditPriceUnits.Free);

    private static string? RemoteId(List<string> tickets) => tickets.Count == 0 ? null : string.Join(",", tickets);

    // Ничего не списывается никогда: Charged = false и при отказе
    private static ImageEditResult Fail(EditOutcome outcome, string error) => new(outcome, [], null, false, null, error);
}
