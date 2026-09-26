using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using ClaudeHomeServer.Services.ImageEditor;

namespace ClaudeHomeServer.Services.ImageEditor;

// Драйвер правки fal.ai (ADR-017, разделы 2 и 4). Транспорт — очередь queue.fal.run, а не
// синхронный fal.run: только она даёт стадии «в очереди / рисуем» и отмену. Входные
// картинки уходят data: URI. Цена до запуска — прайс API (/v1/models/pricing) с кешем на
// сутки; нет ответа — ориентир каталога. Тот же ключ Fal:ApiKey и тот же тихий
// HTTP-клиент "fal", что у FalImageService.
public sealed class FalImageEditor : IImageEditor, IImageEditQuoter
{
    public const string ProviderKey = "fal";
    private const string HttpClientName = "fal";
    private static readonly TimeSpan PriceCacheTtl = TimeSpan.FromHours(24);
    private static readonly TimeSpan JobCeiling = TimeSpan.FromMinutes(6);

    private readonly IHttpClientFactory _http;
    private readonly string? _apiKey;
    private readonly string _queueBase;
    private readonly string _apiBase;
    private readonly ILogger<FalImageEditor> _log;
    private readonly ConcurrentDictionary<string, (double Price, string Unit, DateTime At)> _prices = new();
    // cancel_url задач в работе: отмена у fal идёт по нему, а не по request_id
    private readonly ConcurrentDictionary<string, string> _cancelUrls = new();

    // Пауза опроса статуса; тесты ставят ноль
    internal TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(1);

    public FalImageEditor(IHttpClientFactory http, IConfiguration config, ILogger<FalImageEditor> log)
    {
        _http = http;
        _apiKey = config["Fal:ApiKey"] ?? Environment.GetEnvironmentVariable("FAL_KEY");
        _queueBase = (config["Fal:QueueBase"] ?? "https://queue.fal.run").TrimEnd('/');
        _apiBase = (config["Fal:ApiBase"] ?? "https://api.fal.ai/v1").TrimEnd('/');
        _log = log;
    }

    public string Key => ProviderKey;
    public string Label => "fal.ai";
    public string PriceUnit => ImageEditPriceUnits.Usd;
    public bool Enabled => !string.IsNullOrWhiteSpace(_apiKey);
    public IReadOnlyList<ImageEditModelInfo> Models => Catalog;

    public const string NanoBananaEdit = "fal-ai/nano-banana-2/edit";
    public const string NanoBananaProEdit = "fal-ai/nano-banana-pro/edit";
    public const string KontextPro = "fal-ai/flux-pro/kontext";
    public const string KontextMaxMulti = "fal-ai/flux-pro/kontext/max/multi";
    public const string FluxFill = "fal-ai/flux-pro/v1/fill";
    public const string BriaExpand = "fal-ai/bria/expand";
    public const string BriaRemoveBackground = "fal-ai/bria/background/remove";
    public const string NanoBananaGenerate = "fal-ai/nano-banana-2";

    // Курируемый список: только модели, которые драйвер умеет вызвать с нашими образцами
    // и маской (сверено с живым каталогом fal 2026-09-25, цены — get_pricing)
    private static readonly IReadOnlyList<ImageEditModelInfo> Catalog =
    [
        Model(NanoBananaEdit, "Nano Banana 2", [ImageEditOp.Edit, ImageEditOp.Inpaint], MaskSupport.AsReference, 3, 4, true, 0.08, "image", maskPass: true),
        Model(NanoBananaProEdit, "Nano Banana Pro", [ImageEditOp.Edit, ImageEditOp.Inpaint], MaskSupport.AsReference, 3, 4, true, 0.15, "image", maskPass: true),
        Model(KontextPro, "FLUX Kontext Pro", [ImageEditOp.Edit], MaskSupport.None, 0, 4, false, 0.04, "image"),
        Model(KontextMaxMulti, "FLUX Kontext Max Multi", [ImageEditOp.Edit, ImageEditOp.Inpaint], MaskSupport.AsReference, 3, 4, false, 0.08, "image"),
        Model(FluxFill, "FLUX Pro Fill (инпейнт)", [ImageEditOp.Inpaint], MaskSupport.Native, 0, 4, false, 0.05, "megapixel"),
        Model(BriaExpand, "Bria Expand", [ImageEditOp.Outpaint], MaskSupport.None, 0, 1, false, 0.04, "generation"),
        Model(BriaRemoveBackground, "Bria: убрать фон", [ImageEditOp.RemoveBackground], MaskSupport.None, 0, 1, false, 0.018, "generation"),
        Model(NanoBananaGenerate, "Nano Banana 2 (новая картинка)", [ImageEditOp.Generate], MaskSupport.None, 3, 4, false, 0.08, "image"),
    ];

    private static ImageEditModelInfo Model(string id, string label, ImageEditOp[] ops, MaskSupport mask,
        int maxRefs, int maxCount, bool face, double price, string per, bool maskPass = false) =>
        new(id, label, new ImageEditCaps(ops, mask, maxRefs, maxCount, face, maskPass),
            new ImageEditPriceHint(price, ImageEditPriceUnits.Usd, per));

    public ImageEditModelInfo? PickModel(ImageEditOp op, EditMode mode, EditTraits traits)
    {
        var id = op switch
        {
            ImageEditOp.Generate => NanoBananaGenerate,
            ImageEditOp.Outpaint => BriaExpand,
            ImageEditOp.RemoveBackground => BriaRemoveBackground,
            ImageEditOp.Upscale => null,
            // Стрелки, рамки и подписи видны только на размеченной копии — нужна модель с
            // каналом образцов; у FLUX Fill его нет, и копия выпала бы из запроса
            _ when traits.HasAnnotations => mode == EditMode.Photoreal ? NanoBananaProEdit : NanoBananaEdit,
            // Стереть отмеченное: FLUX Fill дорисовывает в маске новый предмет вместо фона (живой
            // прогон 2026-09-26 заменил кружку другой кружкой), nano-banana по маске-образцу
            // стирает чисто и, в отличие от bria/eraser, умеет несколько вариантов
            _ when traits.HasMask && traits.Removal => mode == EditMode.Photoreal ? NanoBananaProEdit : NanoBananaEdit,
            // Одна кисть без образцов и персонажа — настоящий инпейнт; иначе маска едет образцом
            _ when traits.HasMask && traits.References == 0 && !traits.HasCharacter && mode != EditMode.Photoreal => FluxFill,
            _ when traits.HasCharacter || traits.HasMask => mode == EditMode.Photoreal ? NanoBananaProEdit : NanoBananaEdit,
            _ => mode switch
            {
                EditMode.Precise => traits.References > 0 ? KontextMaxMulti : KontextPro,
                EditMode.Photoreal => NanoBananaProEdit,
                _ => NanoBananaEdit,
            },
        };
        return id is null ? null : Catalog.First(m => m.Id == id);
    }

    public int? ExpectedSeconds(ImageEditModelInfo model) => model.Id switch
    {
        KontextPro => 12,
        BriaRemoveBackground => 8,
        _ => 20,
    };

    // ── Котировка ────────────────────────────────────────────────────────────────

    public async Task<ImageEditEstimateDto> EstimateAsync(
        ImageEditModelInfo model, ImageEditQuoteRequest request, CancellationToken ct)
    {
        // Кисть вместе с пометками — два запроса: первый проход по маске даёт ещё одну картинку
        if (model.Caps.SeparateMaskPass && request.HasMask && request.HasAnnotations)
            request = request with { Count = request.Count + 1 };

        var price = await PriceAsync(model.Id, ct);
        if (price is null) return ImageEditEstimates.FromHint(model, request, PriceUnit);

        var amount = ImageEditEstimates.PerUnit(price.Value.Unit, price.Value.Price, request);
        if (amount is null) return ImageEditEstimates.Unknown(PriceUnit);
        // За картинку и за запуск цена точная; мегапиксели — по размеру исходника, результат может отличаться
        var approx = price.Value.Unit.StartsWith("megapixel", StringComparison.OrdinalIgnoreCase);
        return new ImageEditEstimateDto(amount, PriceUnit, approx, ImageEditEstimateSources.Catalog);
    }

    private async Task<(double Price, string Unit)?> PriceAsync(string endpoint, CancellationToken ct)
    {
        if (_prices.TryGetValue(endpoint, out var cached) && DateTime.UtcNow - cached.At < PriceCacheTtl)
            return (cached.Price, cached.Unit);
        if (!Enabled) return null;
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get,
                $"{_apiBase}/models/pricing?endpoint_id={Uri.EscapeDataString(endpoint)}");
            Authorize(req);
            using var resp = await Client().SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode) return null;
            var json = await resp.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ct);
            if (!json.TryGetProperty("prices", out var prices) || prices.ValueKind != JsonValueKind.Array) return null;
            foreach (var p in prices.EnumerateArray())
            {
                if (Str(p, "endpoint_id") is { } id && !string.Equals(id, endpoint, StringComparison.OrdinalIgnoreCase)) continue;
                if (!p.TryGetProperty("unit_price", out var up) || !up.TryGetDouble(out var unitPrice)) continue;
                var unit = Str(p, "unit") ?? "";
                _prices[endpoint] = (unitPrice, unit, DateTime.UtcNow);
                return (unitPrice, unit);
            }
            return null;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            _log.LogDebug(ex, "fal: прайс {Endpoint} недоступен, беру ориентир каталога", endpoint);
            return null;
        }
    }

    // ── Запуск ───────────────────────────────────────────────────────────────────

    public async Task<ImageEditResult> RunAsync(ImageEditRequest req, IProgress<EditProgress> progress, CancellationToken ct)
    {
        if (!Enabled) return Fail(EditOutcome.Unavailable, false, null, "fal.ai не настроен: нет ключа");
        if (req.MaskPass is not { } pass) return await RunQueuedAsync(req, progress, ct);

        // Два прохода: сначала по маске, её первый вариант — исходник правки по пометкам.
        // «Скачиваем» первого прохода для человека ещё работа, а не финиш
        var first = await RunQueuedAsync(pass with { MaskPass = null }, new MaskPassProgress(progress), ct);
        if (first.Outcome != EditOutcome.Ok || first.Images.Count == 0) return first;
        var cleaned = first.Images[0];
        var second = await RunQueuedAsync(
            req with { Source = new ImageBytes(cleaned.Bytes, cleaned.ContentType), MaskPass = null }, progress, ct);
        // Первый проход уже тарифицирован, что бы ни случилось со вторым
        return second.Outcome == EditOutcome.Ok ? second : second with { Charged = true };
    }

    private sealed class MaskPassProgress(IProgress<EditProgress> inner) : IProgress<EditProgress>
    {
        public void Report(EditProgress value) =>
            inner.Report(value.Stage == EditStage.Downloading ? new EditProgress(EditStage.Running) : value);
    }

    private async Task<ImageEditResult> RunQueuedAsync(ImageEditRequest req, IProgress<EditProgress> progress, CancellationToken ct)
    {
        Dictionary<string, object?> body;
        try
        {
            body = BuildBody(req);
        }
        catch (ArgumentException ex)
        {
            return Fail(EditOutcome.Failed, false, null, ex.Message);
        }

        var client = Client();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(JobCeiling);
        var token = timeout.Token;

        QueueTicket ticket;
        try
        {
            using var submit = new HttpRequestMessage(HttpMethod.Post, $"{_queueBase}/{req.Model.Trim('/')}")
            {
                Content = JsonContent.Create(body),
            };
            Authorize(submit);
            using var resp = await client.SendAsync(submit, token);
            var text = await resp.Content.ReadAsStringAsync(token);
            if (!resp.IsSuccessStatusCode)
                return Fail(ClassifyHttp(resp.StatusCode, text), false, null, ErrorText(text, resp.StatusCode));
            ticket = ParseTicket(text) ?? throw new JsonException("нет request_id в ответе очереди");
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException || ex is TaskCanceledException && !ct.IsCancellationRequested)
        {
            return Fail(EditOutcome.Unavailable, false, null, "fal.ai не ответил: " + ex.Message);
        }

        // Задачу приняли — дальше списание возможно
        _cancelUrls[ticket.RequestId] = ticket.CancelUrl;
        progress.Report(new EditProgress(EditStage.Queued));
        try
        {
            while (true)
            {
                using var status = new HttpRequestMessage(HttpMethod.Get, ticket.StatusUrl);
                Authorize(status);
                using var sresp = await client.SendAsync(status, token);
                if (sresp.IsSuccessStatusCode)
                {
                    var s = await sresp.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: token);
                    var state = Str(s, "status");
                    if (state == "COMPLETED") break;
                    if (state == "IN_QUEUE")
                        progress.Report(new EditProgress(EditStage.Queued,
                            s.TryGetProperty("queue_position", out var qp) && qp.TryGetInt32(out var pos) ? pos : null));
                    else if (state == "IN_PROGRESS")
                        progress.Report(new EditProgress(EditStage.Running));
                }
                await Task.Delay(PollInterval, token);
            }

            using var result = new HttpRequestMessage(HttpMethod.Get, ticket.ResponseUrl);
            Authorize(result);
            using var rresp = await client.SendAsync(result, token);
            var rtext = await rresp.Content.ReadAsStringAsync(token);
            // Неуспешный запрос fal не тарифицирует
            if (!rresp.IsSuccessStatusCode)
                return Fail(ClassifyHttp(rresp.StatusCode, rtext), false, ticket.RequestId, ErrorText(rtext, rresp.StatusCode));

            progress.Report(new EditProgress(EditStage.Downloading));
            var images = await DownloadAllAsync(client, rtext, token);
            if (images.Count == 0)
                return Fail(EditOutcome.Failed, null, ticket.RequestId, "fal.ai не вернул картинок");
            return new ImageEditResult(EditOutcome.Ok, images, null, true, ticket.RequestId, null);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            await CancelRemoteAsync(ticket.RequestId, CancellationToken.None);
            return Fail(EditOutcome.Failed, null, ticket.RequestId, "fal.ai не ответил за 6 минут");
        }
        catch (OperationCanceledException)
        {
            // Отмена снаружи: отзываем задачу у поставщика и пробрасываем отмену дальше
            await CancelRemoteAsync(ticket.RequestId, CancellationToken.None);
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException)
        {
            return Fail(EditOutcome.Failed, null, ticket.RequestId, "Сбой связи с fal.ai: " + ex.Message);
        }
        finally
        {
            _cancelUrls.TryRemove(ticket.RequestId, out _);
        }
    }

    public async Task<bool> CancelRemoteAsync(string remoteId, CancellationToken ct)
    {
        if (!_cancelUrls.TryGetValue(remoteId, out var url) || string.IsNullOrEmpty(url)) return false;
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(10));
            using var req = new HttpRequestMessage(HttpMethod.Put, url);
            Authorize(req);
            using var resp = await Client().SendAsync(req, cts.Token);
            return resp.IsSuccessStatusCode;
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException)
        {
            _log.LogDebug(ex, "fal: отмена {RequestId} не прошла", remoteId);
            return false;
        }
    }

    // Тело запроса по модели: схемы входа у семейств fal разные, лишний параметр даёт 422
    internal static Dictionary<string, object?> BuildBody(ImageEditRequest req)
    {
        var id = req.Model.Trim().Trim('/');
        var count = Math.Clamp(req.Count, 1, 4);
        var source = req.Source is null ? null : DataUri(req.Source.Bytes, req.Source.ContentType);
        var refs = req.References.Select(r => DataUri(r.Bytes, r.ContentType)).ToList();

        string RequireSource() => source ?? throw new ArgumentException("Модели нужна исходная картинка");

        var body = new Dictionary<string, object?>();
        switch (id)
        {
            case FluxFill:
                if (req.Mask is null) throw new ArgumentException("Модели FLUX Fill нужна маска: выделите место кистью");
                body["prompt"] = req.Prompt;
                body["image_url"] = RequireSource();
                body["mask_url"] = DataUri(req.Mask.Bytes, req.Mask.ContentType);
                body["num_images"] = count;
                break;
            case BriaExpand:
            {
                var size = ImageDimensions.Read(req.Source?.Bytes)
                           ?? throw new ArgumentException("Не удалось прочитать размер исходной картинки");
                var o = req.Outpaint ?? throw new ArgumentException("Не заданы поля дорисовки");
                body["image_url"] = RequireSource();
                body["canvas_size"] = new[] { size.Width + o.Left + o.Right, size.Height + o.Top + o.Bottom };
                body["original_image_size"] = new[] { size.Width, size.Height };
                body["original_image_location"] = new[] { o.Left, o.Top };
                body["prompt"] = req.Prompt;
                break;
            }
            case BriaRemoveBackground:
                body["image_url"] = RequireSource();
                break;
            case KontextPro:
                body["prompt"] = req.Prompt;
                body["image_url"] = RequireSource();
                body["num_images"] = count;
                if (req.AspectRatio is { Length: > 0 } ar) body["aspect_ratio"] = ar;
                break;
            case NanoBananaGenerate:
                body["prompt"] = req.Prompt;
                body["num_images"] = count;
                body["aspect_ratio"] = req.AspectRatio is { Length: > 0 } a ? a : "1:1";
                break;
            default:
                // nano-banana */edit и kontext/max/multi: исходник первым, образцы следом
                body["prompt"] = req.Prompt;
                body["image_urls"] = (source is null ? refs : refs.Prepend(source)).ToList();
                body["num_images"] = count;
                if (req.AspectRatio is { Length: > 0 } r) body["aspect_ratio"] = r;
                break;
        }
        return body;
    }

    private sealed record QueueTicket(string RequestId, string StatusUrl, string ResponseUrl, string CancelUrl);

    private QueueTicket? ParseTicket(string text)
    {
        var json = JsonDocument.Parse(text).RootElement;
        var id = Str(json, "request_id");
        if (string.IsNullOrEmpty(id)) return null;
        var baseUrl = $"{_queueBase}/requests/{id}";
        return new QueueTicket(id,
            Str(json, "status_url") ?? baseUrl + "/status",
            Str(json, "response_url") ?? baseUrl,
            Str(json, "cancel_url") ?? baseUrl + "/cancel");
    }

    private static async Task<IReadOnlyList<EditedImage>> DownloadAllAsync(HttpClient client, string text, CancellationToken ct)
    {
        var json = JsonDocument.Parse(text).RootElement;
        var items = new List<JsonElement>();
        if (json.TryGetProperty("images", out var arr) && arr.ValueKind == JsonValueKind.Array)
            items.AddRange(arr.EnumerateArray());
        else if (json.TryGetProperty("image", out var one) && one.ValueKind == JsonValueKind.Object)
            items.Add(one);

        var result = new List<EditedImage>();
        foreach (var item in items)
        {
            if (Str(item, "url") is not { Length: > 0 } url) continue;
            if (await ImageDownload.FetchAsync(client, url, Str(item, "content_type"), ct) is { } img)
                result.Add(img);
        }
        return result;
    }

    private static EditOutcome ClassifyHttp(HttpStatusCode code, string body)
    {
        var lower = body.ToLowerInvariant();
        if (lower.Contains("balance") || lower.Contains("insufficient") || lower.Contains("credit"))
            return EditOutcome.InsufficientCredits;
        if (lower.Contains("nsfw") || lower.Contains("content policy") || lower.Contains("moderation")
            || lower.Contains("safety"))
            return EditOutcome.Rejected;
        return code is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.ServiceUnavailable
            or HttpStatusCode.BadGateway or HttpStatusCode.GatewayTimeout
            ? EditOutcome.Unavailable
            : EditOutcome.Failed;
    }

    private static string ErrorText(string body, HttpStatusCode code)
    {
        try
        {
            var json = JsonDocument.Parse(body).RootElement;
            if (Str(json, "detail") is { Length: > 0 } d) return d;
            if (json.TryGetProperty("detail", out var arr) && arr.ValueKind == JsonValueKind.Array
                && arr.EnumerateArray().FirstOrDefault() is { ValueKind: JsonValueKind.Object } first
                && Str(first, "msg") is { Length: > 0 } msg)
                return msg;
        }
        catch (JsonException) { }
        return $"fal.ai ответил {(int)code}";
    }

    private static ImageEditResult Fail(EditOutcome outcome, bool? charged, string? remoteId, string error) =>
        new(outcome, [], null, charged, remoteId, error);

    private HttpClient Client()
    {
        var client = _http.CreateClient(HttpClientName);
        client.Timeout = TimeSpan.FromSeconds(60);
        return client;
    }

    private void Authorize(HttpRequestMessage req) =>
        req.Headers.Authorization = new AuthenticationHeaderValue("Key", _apiKey);

    private static string DataUri(byte[] bytes, string contentType) =>
        $"data:{(string.IsNullOrWhiteSpace(contentType) ? "image/png" : contentType)};base64,{Convert.ToBase64String(bytes)}";

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;
}
