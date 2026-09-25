using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using ClaudeHomeServer.Services.ImageEditor;

namespace ClaudeHomeServer.Services.Images.Editing;

// Драйвер правки Higgsfield (ADR-016, раздел 2а) поверх HiggsfieldMcpClient. Платит
// инстансный аккаунт админа, учёт трат per-user ведёт исполнитель задач: сюда владелец не
// доходит вовсе, AdminOwnerId шов IHiggsfieldAccess не отдаёт по построению.
//
// Цепочка: media_upload → PUT → media_confirm на каждый вход → generate_image с ЯВНЫМ
// use_unlim: false (без него сервер при бесплатных генерациях ничего не запускает и
// возвращает вопрос unlim_choice) → опрос jobs_wait до терминального статуса → скачивание.
// Инструмента отмены у Higgsfield нет: отмена просто прекращает ожидание.
public sealed partial class HiggsfieldImageEditor(HiggsfieldMcpClient client) : IImageEditor, IImageEditQuoter
{
    public const string ProviderKey = "higgsfield";
    private static readonly TimeSpan JobCeiling = TimeSpan.FromMinutes(6);
    private const int MaxParallelUploads = 4;

    // Пауза между пустыми ответами jobs_wait; сам jobs_wait ждёт до 15 с на сервере
    internal TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(1);

    public string Key => ProviderKey;
    public string Label => "Higgsfield";
    public string PriceUnit => ImageEditPriceUnits.Credits;
    public bool Enabled => client.Available;
    public IReadOnlyList<ImageEditModelInfo> Models => Catalog;

    public const string NanoBanana2 = "nano_banana_2";
    public const string NanoBanana2Lite = "nano_banana_2_lite";
    public const string NanoBananaPro = "nano_banana_pro";
    public const string GptImage = "gpt_image_2_5";
    public const string FluxKontext = "flux_kontext";
    public const string Seedream = "seedream_v5_pro";
    public const string Outpaint = "flux_2_pro_outpaint";
    public const string BackgroundRemover = "image_background_remover";
    public const string Topaz = "topaz_image";

    // Курируемый список волны 1 (сверено с models_explore 2026-09-25). Цены — замер get_cost
    // за один запуск: сумма от count не зависит, вариант = отдельный запуск.
    private static readonly IReadOnlyList<ImageEditModelInfo> Catalog =
    [
        Model(NanoBanana2, "Nano Banana 2", [ImageEditOp.Generate, ImageEditOp.Edit, ImageEditOp.Inpaint], MaskSupport.Native, 4, 4, true, 1.5),
        Model(NanoBanana2Lite, "Nano Banana 2 Lite", [ImageEditOp.Edit, ImageEditOp.Inpaint], MaskSupport.Native, 4, 4, true, null),
        Model(NanoBananaPro, "Nano Banana Pro", [ImageEditOp.Generate, ImageEditOp.Edit, ImageEditOp.Inpaint], MaskSupport.AsReference, 4, 4, true, null),
        Model(GptImage, "GPT Image 2.5", [ImageEditOp.Generate, ImageEditOp.Edit, ImageEditOp.Inpaint], MaskSupport.AsReference, 4, 4, true, 0.25),
        Model(FluxKontext, "FLUX Kontext", [ImageEditOp.Edit], MaskSupport.None, 2, 4, false, null),
        Model(Seedream, "Seedream 5 Pro", [ImageEditOp.Edit, ImageEditOp.Inpaint], MaskSupport.AsReference, 4, 4, false, null),
        Model(Outpaint, "FLUX.2 Pro Outpaint", [ImageEditOp.Outpaint], MaskSupport.None, 0, 1, false, null),
        Model(BackgroundRemover, "Убрать фон", [ImageEditOp.RemoveBackground], MaskSupport.None, 0, 1, false, null),
        Model(Topaz, "Topaz: улучшить качество", [ImageEditOp.Upscale], MaskSupport.None, 0, 1, false, null),
    ];

    private static ImageEditModelInfo Model(string id, string label, ImageEditOp[] ops, MaskSupport mask,
        int maxRefs, int maxCount, bool face, double? credits) =>
        new(id, label, new ImageEditCaps(ops, mask, maxRefs, maxCount, face),
            credits is { } c ? new ImageEditPriceHint(c, ImageEditPriceUnits.Credits, "image") : null);

    public ImageEditModelInfo? PickModel(ImageEditOp op, EditMode mode, EditTraits traits)
    {
        var id = op switch
        {
            ImageEditOp.Outpaint => Outpaint,
            ImageEditOp.RemoveBackground => BackgroundRemover,
            ImageEditOp.Upscale => Topaz,
            // Маска (и маска с персонажем) — настоящий канал маски у nano_banana_2
            _ when traits.HasMask => NanoBanana2,
            ImageEditOp.Generate => NanoBanana2,
            _ => mode switch
            {
                EditMode.Precise => GptImage,
                EditMode.Photoreal => NanoBananaPro,
                _ => NanoBanana2,
            },
        };
        return Catalog.First(m => m.Id == id);
    }

    public int? ExpectedSeconds(ImageEditModelInfo model) => 25;

    // ── Котировка ────────────────────────────────────────────────────────────────

    // get_cost: true — препроверка без запуска, точно в кредитах. Замер 2026-09-25: сумма
    // одна и та же при count 1 и 3, то есть это цена одного запуска, — поэтому итог
    // умножается на число вариантов здесь.
    public async Task<ImageEditEstimateDto> EstimateAsync(
        ImageEditModelInfo model, ImageEditQuoteRequest request, CancellationToken ct)
    {
        var args = new JsonObject
        {
            ["model"] = model.Id,
            ["prompt"] = "cost preflight",
            ["count"] = 1,
            ["get_cost"] = true,
            ["use_unlim"] = false,
        };
        if (model.Id == Topaz && request.Width is > 0 && request.Height is > 0)
        {
            args["output_width"] = request.Width * 2;
            args["output_height"] = request.Height * 2;
        }

        var call = await client.CallToolAsync("generate_image", args, ct);
        if (call.Unavailable) throw new ImageEditProviderUnavailableException(call.Text);
        if (ParseCredits(call) is not { } perRun) return ImageEditEstimates.Unknown(PriceUnit);
        return new ImageEditEstimateDto(Math.Round(perRun * Math.Max(1, request.Count), 4),
            PriceUnit, false, ImageEditEstimateSources.Provider);
    }

    internal static double? ParseCredits(HiggsfieldCall call)
    {
        if (call.Structured is JsonObject s)
            foreach (var key in new[] { "credits", "cost", "total_cost" })
                if (s[key] is JsonValue v && v.TryGetValue<double>(out var d))
                    return d;
        var m = CreditsPattern().Match(call.Text);
        return m.Success && double.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var credits)
            ? credits
            : null;
    }

    [GeneratedRegex(@"(\d+(?:\.\d+)?)\s*credits?", RegexOptions.IgnoreCase)]
    private static partial Regex CreditsPattern();

    // ── Запуск ───────────────────────────────────────────────────────────────────

    public async Task<ImageEditResult> RunAsync(ImageEditRequest req, IProgress<EditProgress> progress, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(JobCeiling);
        var token = timeout.Token;

        try
        {
            var medias = new JsonArray();
            var inputs = new List<(ImageBytes Image, string Role)>();
            if (req.Source is not null) inputs.Add((req.Source, "image_references"));
            inputs.AddRange(req.References.Select(r => (new ImageBytes(r.Bytes, r.ContentType), "image_references")));
            if (req.Mask is not null) inputs.Add((req.Mask, "mask"));

            var uploaded = await UploadAllAsync(inputs, token);
            if (uploaded.Failure is { } failure) return failure;
            foreach (var (id, role) in uploaded.Ids)
                medias.Add(new JsonObject { ["role"] = role, ["value"] = id });

            var args = new JsonObject
            {
                ["model"] = req.Model,
                ["prompt"] = req.Prompt,
                ["count"] = Math.Clamp(req.Count, 1, 4),
                // Бесплатные генерации админа редактор не тратит и вопрос unlim_choice не задаёт
                ["use_unlim"] = false,
            };
            if (medias.Count > 0) args["medias"] = medias;
            if (req.AspectRatio is { Length: > 0 } ar) args["aspect_ratio"] = ar;
            if (req.Mask is not null) args["is_inpaint"] = true;
            if (req.Model == Outpaint && req.Outpaint is { } o)
            {
                args["expand_left"] = o.Left;
                args["expand_top"] = o.Top;
                args["expand_right"] = o.Right;
                args["expand_bottom"] = o.Bottom;
            }
            if (req.Model == Topaz)
            {
                var size = ImageDimensions.Read(req.Source?.Bytes);
                if (size is null) return Fail(EditOutcome.Failed, false, "Не удалось прочитать размер исходной картинки");
                args["output_width"] = size.Value.Width * 2;
                args["output_height"] = size.Value.Height * 2;
            }

            var started = await client.CallToolAsync("generate_image", args, token);
            if (started.Unavailable) return Fail(EditOutcome.Unavailable, false, started.Text);
            if (started.Text.Contains("unlim_choice", StringComparison.OrdinalIgnoreCase))
                return Fail(EditOutcome.Failed, false, "Higgsfield запросил выбор бесплатных генераций — запуск не выполнен");
            if (!started.Ok) return Fail(Classify(started.Text), false, started.Text);

            var jobIds = JobIds(started.Json());
            if (jobIds.Count == 0) return Fail(EditOutcome.Failed, false, "Higgsfield не вернул id заданий");

            // Задания приняты — кредиты админа ушли
            var remoteId = string.Join(",", jobIds);
            progress.Report(new EditProgress(EditStage.Queued));

            var urls = new Dictionary<int, string>();
            var errors = new List<string>();
            while (true)
            {
                var waitArgs = new JsonObject
                {
                    ["jobs"] = new JsonArray(jobIds.Select((id, i) => (JsonNode)new JsonObject { ["index"] = i, ["job_id"] = id }).ToArray()),
                    ["timeout_seconds"] = 15,
                };
                var wait = await client.CallToolAsync("jobs_wait", waitArgs, token);
                if (wait.Unavailable)
                    return new ImageEditResult(EditOutcome.Unavailable, [], null, null, remoteId, wait.Text);

                var json = wait.Json();
                var allTerminal = json?["all_terminal"] is JsonValue at && at.TryGetValue<bool>(out var t) && t;
                var anyActive = false;
                errors.Clear();
                if (json?["jobs"] is JsonArray jobs)
                {
                    foreach (var job in jobs.OfType<JsonObject>())
                    {
                        var index = job["index"] is JsonValue iv && iv.TryGetValue<int>(out var idx) ? idx : urls.Count;
                        var status = job["status"]?.ToString()?.ToLowerInvariant() ?? "";
                        if (status == "completed" && job["result_url"]?.ToString() is { Length: > 0 } url)
                            urls[index] = url;
                        else if (status is "failed" or "error" or "nsfw" or "cancelled" or "canceled")
                            errors.Add(job["error"]?.ToString() ?? status);
                        else
                            anyActive = true;
                    }
                }
                if (allTerminal || (json?["jobs"] is JsonArray && !anyActive)) break;
                progress.Report(new EditProgress(EditStage.Running));
                await Task.Delay(PollInterval, token);
            }

            if (urls.Count == 0)
            {
                var text = errors.Count > 0 ? string.Join("; ", errors) : "Higgsfield не вернул картинок";
                return new ImageEditResult(Classify(text), [], null, null, remoteId, text);
            }

            progress.Report(new EditProgress(EditStage.Downloading));
            var images = new List<EditedImage>();
            foreach (var url in urls.OrderBy(p => p.Key).Select(p => p.Value))
                if (await client.DownloadAsync(url, token) is { } img)
                    images.Add(img);
            return images.Count == 0
                ? new ImageEditResult(EditOutcome.Failed, [], null, null, remoteId, "Не удалось скачать результат Higgsfield")
                : new ImageEditResult(EditOutcome.Ok, images, null, true, remoteId, null);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return Fail(EditOutcome.Failed, null, "Higgsfield не ответил за 6 минут");
        }
    }

    // Отмены у Higgsfield нет: работа у поставщика идёт дальше, кредиты, скорее всего, списаны
    public Task<bool> CancelRemoteAsync(string remoteId, CancellationToken ct) => Task.FromResult(false);

    private async Task<(List<(string Id, string Role)> Ids, ImageEditResult? Failure)> UploadAllAsync(
        List<(ImageBytes Image, string Role)> inputs, CancellationToken ct)
    {
        using var gate = new SemaphoreSlim(MaxParallelUploads);
        var tasks = inputs.Select(async (input, i) =>
        {
            await gate.WaitAsync(ct);
            try
            {
                return await UploadAsync(input.Image, $"input-{i}{Extension(input.Image.ContentType)}", ct);
            }
            finally
            {
                gate.Release();
            }
        }).ToList();
        var results = await Task.WhenAll(tasks);

        var ids = new List<(string, string)>();
        for (var i = 0; i < results.Length; i++)
        {
            var (id, failure) = results[i];
            if (failure is not null) return ([], failure);
            ids.Add((id!, inputs[i].Role));
        }
        return (ids, null);
    }

    private async Task<(string? Id, ImageEditResult? Failure)> UploadAsync(ImageBytes image, string fileName, CancellationToken ct)
    {
        var contentType = string.IsNullOrWhiteSpace(image.ContentType) ? "image/png" : image.ContentType;
        var upload = await client.CallToolAsync("media_upload",
            new JsonObject { ["filename"] = fileName, ["content_type"] = contentType }, ct);
        if (upload.Unavailable) return (null, Fail(EditOutcome.Unavailable, false, upload.Text));
        if (!upload.Ok) return (null, Fail(Classify(upload.Text), false, upload.Text));

        var slot = ParseUploadSlot(upload);
        if (slot is null) return (null, Fail(EditOutcome.Failed, false, "Higgsfield не выдал адрес загрузки"));
        if (!await client.PutAsync(slot.Value.Url, image.Bytes, contentType, ct))
            return (null, Fail(EditOutcome.Unavailable, false, "Не удалось загрузить картинку в Higgsfield"));

        var confirm = await client.CallToolAsync("media_confirm",
            new JsonObject { ["type"] = "image", ["media_id"] = slot.Value.MediaId }, ct);
        if (confirm.Unavailable) return (null, Fail(EditOutcome.Unavailable, false, confirm.Text));
        if (!confirm.Ok) return (null, Fail(Classify(confirm.Text), false, confirm.Text));
        return (slot.Value.MediaId, null);
    }

    // media_upload отвечает текстом «- {media_id}: … curl … '{url}'» (замер 2026-09-25);
    // structuredContent с upload_url/media_id тоже понимаем
    internal static (string MediaId, string Url)? ParseUploadSlot(HiggsfieldCall call)
    {
        if (call.Json() is JsonObject json)
        {
            var node = json["uploads"] is JsonArray arr && arr.Count > 0 ? arr[0] as JsonObject : json;
            if (node?["media_id"]?.ToString() is { Length: > 0 } id && node["upload_url"]?.ToString() is { Length: > 0 } url)
                return (id, url);
        }
        var m = UploadLinePattern().Match(call.Text);
        return m.Success ? (m.Groups[1].Value, m.Groups[2].Value) : null;
    }

    [GeneratedRegex(@"-\s*([0-9a-fA-F-]{36}):.*?'(https://[^']+)'", RegexOptions.Singleline)]
    private static partial Regex UploadLinePattern();

    // generate_image отдаёт { results: [{ id, status: "pending" }] }; jobs — на всякий случай
    internal static List<string> JobIds(JsonNode? json)
    {
        var ids = new List<string>();
        foreach (var key in new[] { "results", "jobs" })
            if (json?[key] is JsonArray arr)
                foreach (var item in arr.OfType<JsonObject>())
                    if ((item["id"] ?? item["job_id"])?.ToString() is { Length: > 0 } id)
                        ids.Add(id);
        return ids;
    }

    // Тексты у Higgsfield свободные: классификатор держится на образцах ответов в тестах
    internal static EditOutcome Classify(string text)
    {
        var lower = text.ToLowerInvariant();
        if (lower.Contains("insufficient") || lower.Contains("not enough credit") || lower.Contains("credits")
            && (lower.Contains("balance") || lower.Contains("top up") || lower.Contains("out of")))
            return EditOutcome.InsufficientCredits;
        if (lower.Contains("nsfw") || lower.Contains("moderation") || lower.Contains("content policy")
            || lower.Contains("safety") || lower.Contains("prohibited"))
            return EditOutcome.Rejected;
        if (lower.Contains("unauthorized") || lower.Contains("отключён"))
            return EditOutcome.Unavailable;
        return EditOutcome.Failed;
    }

    private static ImageEditResult Fail(EditOutcome outcome, bool? charged, string error) =>
        new(outcome, [], null, charged, null, error);

    private static string Extension(string contentType) => contentType switch
    {
        "image/jpeg" => ".jpg",
        "image/webp" => ".webp",
        "image/gif" => ".gif",
        _ => ".png",
    };
}
