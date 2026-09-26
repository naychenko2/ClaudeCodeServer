namespace ClaudeHomeServer.Services.ImageEditor;

// Поставщик «Локальные модели» (ADR-018, раздел «Локальные модели»): Qwen-Image 2.1 и
// FaceDetailer в ComfyUI на своей видеокарте через Core-шов ILocalImageMedia — вертикаль Images
// модулю не видна. Денег нет: котировка — ноль в единицах free плюс время по таблице замеров и
// длина очереди GPU. Доступен всем; нет шва (Images выключена), тумблера LocalMedia:Enabled или
// живого ComfyUI — поставщик скрыт в каталоге.
//
// Канала маски у Qwen-Image нет: маска и размеченная копия уходят образцами с ролями в запросе
// (MaskSupport.AsReference), порядок картинок задаёт EditRequestComposer. Исключение — «удали»
// кистью: маску-образец модель не понимает (живой прогон 2026-09-26: стёрла фон, а отмеченное
// оставила), поэтому область маски закрашивается на холсте серым, и модель заменяет серое фоном.
// Стирание идёт одним холстом, без образцов и размеченной копии — на копии виден стираемый
// предмет. Граф правки отдаёт
// один вариант за прогон, поэтому варианты правки идут прогонами по очереди — общая очередь
// ComfyUI не забивается чужими ожиданиями одного человека.
public sealed class LocalImageEditor(ILocalImageMedia? media) : IImageEditor, IImageEditQuoter
{
    public const string ProviderKey = "local";
    public const string QwenImage = "qwen-image-2.1";
    public const string FaceDetailer = "face-detailer";

    // Потолок одного запуска с ожиданием очереди: четыре варианта правки ≈4 мин, плюс чужие прогоны
    private static readonly TimeSpan JobCeiling = TimeSpan.FromMinutes(20);

    internal TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(2);

    public string Key => ProviderKey;
    public string Label => "Локальные модели";
    public string PriceUnit => ImageEditPriceUnits.Free;
    public bool Enabled => media?.Available == true;
    public IReadOnlyList<ImageEditModelInfo> Models => Catalog;

    // Генерация — до 4 вариантов одним прогоном; правка — до 16 картинок всего (холст и 15 образцов)
    private static readonly IReadOnlyList<ImageEditModelInfo> Catalog =
    [
        new(QwenImage, "Qwen-Image 2.1",
            new ImageEditCaps([ImageEditOp.Generate, ImageEditOp.Edit, ImageEditOp.Inpaint], MaskSupport.AsReference,
                MaxReferences: 15, MaxCount: 4, FaceByReferences: true),
            new ImageEditPriceHint(0, ImageEditPriceUnits.Free, "image")),
        new(FaceDetailer, "Улучшить лица",
            new ImageEditCaps([ImageEditOp.EnhanceFaces], MaskSupport.None, MaxReferences: 0, MaxCount: 1, FaceByReferences: false),
            new ImageEditPriceHint(0, ImageEditPriceUnits.Free, "image")),
    ];

    public ImageEditModelInfo? PickModel(ImageEditOp op, EditMode mode, EditTraits traits) => op switch
    {
        ImageEditOp.EnhanceFaces => Catalog[1],
        ImageEditOp.Generate or ImageEditOp.Edit or ImageEditOp.Inpaint => Catalog[0],
        _ => null,
    };

    // ── Котировка ────────────────────────────────────────────────────────────────

    public int? ExpectedSeconds(ImageEditModelInfo model) =>
        media?.EtaSeconds(model.Id == FaceDetailer ? LocalImageOp.FaceDetail : LocalImageOp.Edit, 1, 1);

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

    // Время всех вариантов: генерация идёт одним прогоном, правка — прогоном на вариант
    private int? Eta(ImageEditModelInfo model, ImageEditQuoteRequest request)
    {
        var count = Math.Max(1, request.Count);
        if (model.Id == FaceDetailer) return media!.EtaSeconds(LocalImageOp.FaceDetail, 1, 1);
        // Картинок в запросе: холст, размеченная копия, маска и образцы
        var images = request.References + (request.HasAnnotations ? 1 : 0) + (request.HasMask ? 1 : 0);
        if (request.Op == ImageEditOp.Generate)
            return images == 0
                ? media!.EtaSeconds(LocalImageOp.Generate, count, 0)
                : media!.EtaSeconds(LocalImageOp.Generate, count, images) * count;
        return media!.EtaSeconds(LocalImageOp.Edit, 1, images + 1) * count;
    }

    // ── Запуск ───────────────────────────────────────────────────────────────────

    public async Task<ImageEditResult> RunAsync(ImageEditRequest req, IProgress<EditProgress> progress, CancellationToken ct)
    {
        if (media is null) return Fail(EditOutcome.Unavailable, "Локальные модели недоступны на этом сервере");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(JobCeiling);
        var token = timeout.Token;

        var references = req.References.Select(r => r.Bytes).ToList();
        LocalImageRequest run;
        int runs;
        switch (req.Op)
        {
            case ImageEditOp.EnhanceFaces:
                if (req.Source is null) return Fail(EditOutcome.Failed, "Нет исходной картинки");
                run = new LocalImageRequest(LocalImageOp.FaceDetail, "", [req.Source.Bytes], null, 1);
                runs = 1;
                break;
            case ImageEditOp.Generate:
                // Холст генерации по тексту не нужен: берутся только образцы человека
                run = new LocalImageRequest(LocalImageOp.Generate, req.Prompt, references, req.AspectRatio, req.Count);
                runs = references.Count == 0 ? 1 : Math.Clamp(req.Count, 1, 4);
                break;
            case ImageEditOp.Edit or ImageEditOp.Inpaint
                when req.Source is not null && EraseMaskOf(req) is { } eraseMask:
                run = new LocalImageRequest(LocalImageOp.Edit, ErasePrompt(req.Instruction), [req.Source.Bytes], null, 1,
                    EraseMask: eraseMask);
                runs = Math.Clamp(req.Count, 1, 4);
                break;
            case ImageEditOp.Edit or ImageEditOp.Inpaint:
                if (req.Source is null) return Fail(EditOutcome.Failed, "Нет исходной картинки");
                // Отдельного канала маски нет: маска уже лежит среди образцов (MaskSupport.AsReference)
                run = new LocalImageRequest(LocalImageOp.Edit, req.Prompt, [req.Source.Bytes, .. references], null, 1);
                runs = Math.Clamp(req.Count, 1, 4);
                break;
            default:
                return Fail(EditOutcome.Failed, "Локальные модели так не умеют");
        }
        // Генерация по тексту без образцов отдаёт все варианты одним прогоном, остальное — по одному
        if (runs > 1) run = run with { Count = 1 };

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
                progress.Report(new EditProgress(EditStage.Queued, submitted.QueuePosition));

                var done = await WaitAsync(ticket, progress, token);
                current = null;
                if (done.State == LocalImageState.Failed)
                {
                    if (images.Count > 0) break;
                    return new ImageEditResult(EditOutcome.Failed, [], Free, false, RemoteId(tickets),
                        "Локальная модель не справилась: " + (done.Error ?? "ComfyUI завершил задачу ошибкой"));
                }
                progress.Report(new EditProgress(EditStage.Downloading));
                images.AddRange(done.Files.Select(f => new EditedImage(f.Bytes, f.ContentType)));
            }
        }
        catch (OperationCanceledException)
        {
            // Задачу, ещё ждущую в очереди, снимаем: чужое время GPU она не займёт
            if (current is not null) await media.CancelAsync(current, CancellationToken.None);
            if (ct.IsCancellationRequested) throw;
            if (images.Count == 0)
                return new ImageEditResult(EditOutcome.Failed, [], Free, false, RemoteId(tickets),
                    $"Локальная видеокарта не успела за {JobCeiling.TotalMinutes:0} минут — очередь занята");
        }

        return images.Count == 0
            ? new ImageEditResult(EditOutcome.Failed, [], Free, false, RemoteId(tickets), "Локальная модель не вернула картинок")
            : new ImageEditResult(EditOutcome.Ok, images, Free, false, RemoteId(tickets), null);
    }

    private async Task<LocalImagePoll> WaitAsync(string ticket, IProgress<EditProgress> progress, CancellationToken ct)
    {
        var stage = EditStage.Queued;
        int? position = null;
        while (true)
        {
            var poll = await media!.PollAsync(ticket, ct);
            if (poll.State is LocalImageState.Completed or LocalImageState.Failed) return poll;
            var next = poll.State == LocalImageState.Running ? EditStage.Running : EditStage.Queued;
            if (poll.Warning is null && (next != stage || poll.QueuePosition != position))
            {
                stage = next;
                position = poll.QueuePosition;
                progress.Report(new EditProgress(stage, stage == EditStage.Queued ? position : null));
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

    // Маска стирания: маска среди образцов и просьба — стереть отмеченное
    private static byte[]? EraseMaskOf(ImageEditRequest req) =>
        EditIntent.IsRemoval(req.Instruction)
            ? req.References.FirstOrDefault(r => r.Label == EditRequestComposer.MaskLabel)?.Bytes
            : null;

    internal const string EraseNote =
        "Серая заливка на картинке закрывает то, что нужно удалить. Замени серую область продолжением фона " +
        "вокруг неё, чтобы не было видно, что там что-то было. Ничего нового в эту область не добавляй. " +
        "Всё вне серой области оставь без изменений.";

    private static string ErasePrompt(string? instruction) =>
        string.IsNullOrWhiteSpace(instruction) ? EraseNote : $"{instruction.Trim()}\n\n{EraseNote}";

    private static readonly EditCost Free = new(0, ImageEditPriceUnits.Free);

    private static string? RemoteId(List<string> tickets) => tickets.Count == 0 ? null : string.Join(",", tickets);

    // Ничего не списывается никогда: Charged = false и при отказе
    private static ImageEditResult Fail(EditOutcome outcome, string error) => new(outcome, [], null, false, null, error);
}
