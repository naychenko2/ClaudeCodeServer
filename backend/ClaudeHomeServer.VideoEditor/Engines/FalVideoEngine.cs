using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ClaudeHomeServer.Services.Http;
using ClaudeHomeServer.Services.VideoEditor.Catalog;
using static ClaudeHomeServer.Services.VideoEditor.Catalog.FalVideoCatalog;

namespace ClaudeHomeServer.Services.VideoEditor.Engines;

// Поставщик fal.ai для видео (ADR-022 §3) по образцу FalAudioEngine: очередь queue.fal.run — отправка,
// опрос, отмена по cancel_url, скачивание клипа общим безопасным загрузчиком. Тот же ключ Fal:ApiKey (или
// FAL_KEY) и тот же тихий HTTP-клиент "fal"; нет ключа — Enabled=false. Кадры уходят data: URI, поэтому
// драйвер работает и в личной области. Цена до запуска — прайс /v1/models/pricing с кешем на сутки:
// доллары за секунду ролика × длительность; нет ответа — ориентир каталога.
public sealed class FalVideoEngine : IVideoEngine, IVideoQuoter
{
    public const string ProviderKey = "fal";
    private const string HttpClientName = "fal";
    private static readonly TimeSpan PriceCacheTtl = TimeSpan.FromHours(24);
    // Ролик до 20 с плюс очередь: у Kling p90 исполнения уже ~4,5 минуты
    private static readonly TimeSpan JobCeiling = TimeSpan.FromMinutes(20);

    private readonly IHttpClientFactory _http;
    private readonly string? _apiKey;
    private readonly string _queueBase;
    private readonly string _apiBase;
    private readonly ILogger<FalVideoEngine> _log;
    private readonly ConcurrentDictionary<string, (double Price, string Unit, DateTime At)> _prices = new();
    // cancel_url задач в работе: отмена у fal идёт по нему, а не по request_id
    private readonly ConcurrentDictionary<string, string> _cancelUrls = new();

    // Пауза опроса статуса; тесты ставят ноль
    internal TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(2);
    internal TimeSpan Ceiling { get; set; } = JobCeiling;
    // Ссылка на клип из ответа fal — внешняя: качаем только общим безопасным загрузчиком (SSRF, редиректы,
    // потолок). 300 МБ за минуту на медленном канале не скачать — срок шире; общий срок держит Ceiling
    internal SafeMediaDownloader Downloader { get; init; } = SharedDownloader;
    internal long MaxDownloadBytes { get; init; } = SafeMediaDownloader.VideoMaxBytes;

    private static readonly SafeMediaDownloader SharedDownloader =
        new(SafeMediaDownloader.CreateHandler()) { Timeout = TimeSpan.FromMinutes(10) };

    private static readonly IReadOnlyList<VideoModelInfo> FalModels =
        [.. All.Select(m => m.Info with { Caps = m.Info.Caps with { LastFrameRequired = m.LastRequired } })];

    public FalVideoEngine(IHttpClientFactory http, IConfiguration config, ILogger<FalVideoEngine> log)
    {
        _http = http;
        _apiKey = config["Fal:ApiKey"] ?? Environment.GetEnvironmentVariable("FAL_KEY");
        _queueBase = (config["Fal:QueueBase"] ?? "https://queue.fal.run").TrimEnd('/');
        _apiBase = (config["Fal:ApiBase"] ?? "https://api.fal.ai/v1").TrimEnd('/');
        _log = log;
    }

    public string Key => ProviderKey;
    public string Label => "fal.ai";
    public string PriceUnit => VideoPriceUnits.Usd;
    public bool Enabled => !string.IsNullOrWhiteSpace(_apiKey);
    public IReadOnlyList<VideoModelInfo> Models => FalModels;

    public IReadOnlySet<string>? ParamNames(VideoModelInfo model) =>
        Find(model.Id) is { ParamNames.Count: > 0 } fal ? fal.ParamNames : null;

    // ── Котировка ────────────────────────────────────────────────────────────────

    public int? ExpectedSeconds(VideoModelInfo model, VideoRequest request) => Find(model.Id)?.EtaSeconds;

    public async Task<VideoEstimate> EstimateAsync(VideoModelInfo model, VideoRequest request, CancellationToken ct)
    {
        if (!Enabled) throw new VideoEngineUnavailableException("fal.ai не настроен: нет ключа");
        if (Find(model.Id) is not { } fal) throw new VideoEngineUnavailableException("У fal.ai нет такой модели");

        var eta = fal.EtaSeconds;
        if (await PriceAsync(fal.Info.Id, ct) is { } price)
        {
            var perRun = !IsPerSecond(price.Unit);
            return new VideoEstimate(perRun ? price.Price : price.Price * request.DurationSec, VideoPriceUnits.Usd,
                fal.PriceVaries, VideoEstimateSources.Provider, eta);
        }
        if (fal.Info.PriceHint is { } hint)
            return new VideoEstimate(hint.Per == "sec" ? hint.Amount * request.DurationSec : hint.Amount, hint.Unit, true,
                VideoEstimateSources.Catalog, eta);
        return new VideoEstimate(null, PriceUnit, true, VideoEstimateSources.Unknown, eta);
    }

    // seconds — за секунду ролика; videos, generations, units, requests — за запуск
    private static bool IsPerSecond(string unit) => unit.Trim().ToLowerInvariant() is "second" or "seconds" or "sec" or "s";

    private async Task<(double Price, string Unit)?> PriceAsync(string endpoint, CancellationToken ct)
    {
        if (_prices.TryGetValue(endpoint, out var cached) && DateTime.UtcNow - cached.At < PriceCacheTtl)
            return (cached.Price, cached.Unit);
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

    public async Task<VideoResult> RunAsync(VideoRequest req, IProgress<VideoProgress> progress, CancellationToken ct)
    {
        if (!Enabled) return VideoResult.Fail(VideoOutcome.Unavailable, "fal.ai не настроен: нет ключа");
        if (Find(req.Model) is not { } model) return VideoResult.Fail(VideoOutcome.Failed, "У fal.ai нет такой модели");

        JsonObject body;
        try
        {
            body = BuildBody(model, req);
        }
        catch (ArgumentException ex)
        {
            return VideoResult.Fail(VideoOutcome.Failed, ex.Message);
        }

        // fal не отдаёт ETA в статусе: в каждое событие кладём ориентир модели из каталога (фронт иначе берёт запасные 90 с)
        progress = new EtaProgress(progress, model.EtaSeconds);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(Ceiling);
        var run = await RunQueuedAsync(model.Info.Id, body, progress, ct, timeout.Token);
        if (run.Failure is { } failed) return failed;
        var requestId = run.RequestId!;

        progress.Report(new VideoProgress(VideoStage.Downloading, RemoteId: requestId));
        if (VideoUrl(run.Output!.Value) is not { } url)
            return new VideoResult(VideoOutcome.Failed, null, null, null, requestId, "fal.ai не вернул ролик");
        try
        {
            var download = await Downloader.DownloadAsync(url, MaxDownloadBytes, timeout.Token);
            if (!download.Ok)
                return new VideoResult(VideoOutcome.Failed, null, null, true, requestId,
                    "Не удалось скачать ролик fal.ai: " + Explain(download.Error));
            var hasSound = model.Sound is not null ? req.Sound : (bool?)null;
            var file = new VideoFile(download.Bytes!, ContentType(download.ContentType), Extension(download.FinalUri),
                req.DurationSec, hasSound);
            return new VideoResult(VideoOutcome.Ok, file, null, true, requestId, null);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new VideoResult(VideoOutcome.Failed, null, null, true, requestId, "fal.ai не отдал ролик вовремя");
        }
    }

    // Тело запроса: Params (имена fal, только разрешённые каталогом) → Defaults → наши общие поля поверх
    // всего. Нехватка или неверный вход — ArgumentException с текстом для человека, до отправки
    internal static JsonObject BuildBody(FalVideoModel model, VideoRequest req)
    {
        var body = new JsonObject();
        if (req.Params is { } extra)
            foreach (var (key, value) in extra)
            {
                if (!model.ParamNames.Contains(key))
                    throw new ArgumentException($"Модель «{model.Info.Label}» не знает параметр «{key}»");
                body[key] = value?.DeepClone();
            }
        if (model.Defaults is not null)
            foreach (var (key, value) in model.Defaults)
                if (!body.ContainsKey(key)) body[key] = value?.DeepClone();

        if (string.IsNullOrWhiteSpace(req.Text)) throw new ArgumentException("Модели нужно описание сцены");
        body["prompt"] = req.Text;

        var first = req.FrameA ?? throw new ArgumentException("Модели нужен первый кадр");
        CheckFrame(first, "Первый");
        body[model.First] = DataUri(first);
        if (req.FrameB is { } last)
        {
            if (model.Last is null) throw new ArgumentException($"Модель «{model.Info.Label}» не берёт последний кадр");
            CheckFrame(last, "Последний");
            body[model.Last] = DataUri(last);
        }
        else if (model.LastRequired)
            throw new ArgumentException($"Модели «{model.Info.Label}» нужен и последний кадр");

        if (!model.Info.Caps.Durations.Contains(req.DurationSec))
            throw new ArgumentException($"Модель «{model.Info.Label}» не берёт длительность {req.DurationSec} с");
        body[model.Duration] = model.DurationForm switch
        {
            FalDurationForm.Int => req.DurationSec,
            FalDurationForm.String => req.DurationSec.ToString(CultureInfo.InvariantCulture),
            _ => req.DurationSec.ToString(CultureInfo.InvariantCulture) + "s",
        };

        if (req.Aspect is { Length: > 0 } aspect)
        {
            if (model.Aspect is not null)
            {
                if (!model.Info.Caps.Aspects.Contains(aspect))
                    throw new ArgumentException($"Модель «{model.Info.Label}» не берёт пропорцию {aspect}");
                body[model.Aspect] = aspect;
            }
            // Модель без поля пропорции следует кадру — запрошенную пропорцию молча не навязываем
        }
        if (model.Sound is not null) body[model.Sound] = req.Sound;
        if (model.Seed is not null && req.Seed is { } seed) body[model.Seed] = seed;
        return body;
    }

    private static void CheckFrame(VideoFrameBytes frame, string which)
    {
        if (frame.Bytes.Length == 0) throw new ArgumentException($"{which} кадр пустой");
        if (frame.Bytes.Length > MaxFrameBytes)
            throw new ArgumentException($"{which} кадр больше {MaxFrameBytes / (1024 * 1024)} МБ — уменьшите его");
    }

    private static string DataUri(VideoFrameBytes frame) =>
        $"data:{(string.IsNullOrWhiteSpace(frame.ContentType) ? "image/png" : frame.ContentType)};base64,{Convert.ToBase64String(frame.Bytes)}";

    private static string? VideoUrl(JsonElement output) =>
        output.ValueKind == JsonValueKind.Object && output.TryGetProperty("video", out var v)
            && v.ValueKind == JsonValueKind.Object && Str(v, "url") is { Length: > 0 } url
            ? url
            : null;

    private static string ContentType(string? type) =>
        type is { Length: > 0 } t && t.StartsWith("video/", StringComparison.OrdinalIgnoreCase) ? t : "video/mp4";

    private static string Extension(Uri? uri)
    {
        var ext = uri is null ? "" : Path.GetExtension(uri.AbsolutePath).ToLowerInvariant();
        return ext is ".mp4" or ".webm" or ".mov" ? ext : ".mp4";
    }

    private string Explain(string? error) => error switch
    {
        "private-address" or "not-https" or "bad-url" => "внутренний или недопустимый адрес",
        "too-large" => $"ролик больше {MaxDownloadBytes / (1024 * 1024)} МБ",
        "timeout" => "истёк срок скачивания",
        "empty" => "пустой файл",
        null => "неизвестная ошибка",
        _ => error,
    };

    // Публичный статус очереди fal доли готовности не гарантирует: берём числовое progress (0..1) или
    // percent (0..100), если модель их отдаёт; иначе null — честно «нет данных»
    internal static double? StatusPercent(JsonElement status)
    {
        if (status.ValueKind != JsonValueKind.Object) return null;
        if (status.TryGetProperty("progress", out var p) && p.ValueKind == JsonValueKind.Number && p.TryGetDouble(out var f)
            && f is >= 0 and <= 1)
            return Math.Round(f, 2);
        if (status.TryGetProperty("percent", out var c) && c.ValueKind == JsonValueKind.Number && c.TryGetDouble(out var h)
            && h is >= 0 and <= 100)
            return Math.Round(h / 100, 2);
        return null;
    }

    private sealed class EtaProgress(IProgress<VideoProgress> inner, int eta) : IProgress<VideoProgress>
    {
        public void Report(VideoProgress value) => inner.Report(value.EtaSeconds is null ? value with { EtaSeconds = eta } : value);
    }

    private sealed record QueueRun(JsonElement? Output, string? RequestId, VideoResult? Failure);

    // Один прогон очереди: отправка, опрос до COMPLETED, чтение результата. Отмена снаружи отзывает задачу
    // у fal и пробрасывается; потолок времени — отказ значением
    private async Task<QueueRun> RunQueuedAsync(string endpoint, JsonObject body, IProgress<VideoProgress> progress,
        CancellationToken outer, CancellationToken token)
    {
        var client = Client();
        QueueTicket ticket;
        try
        {
            using var submit = new HttpRequestMessage(HttpMethod.Post, $"{_queueBase}/{endpoint.Trim('/')}")
            {
                Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
            };
            Authorize(submit);
            using var resp = await client.SendAsync(submit, token);
            var text = await resp.Content.ReadAsStringAsync(token);
            if (!resp.IsSuccessStatusCode)
                return new QueueRun(null, null, VideoResult.Fail(ClassifyHttp(resp.StatusCode, text), ErrorText(text, resp.StatusCode)));
            ticket = ParseTicket(text) ?? throw new JsonException("нет request_id в ответе очереди");
            // Ключ уходит и на эти адреса — чужой хост не опрашиваем
            if (!new[] { ticket.StatusUrl, ticket.ResponseUrl, ticket.CancelUrl }.All(u => FalQueueUrls.IsTrusted(u, _queueBase)))
                return new QueueRun(null, ticket.RequestId, VideoResult.Fail(VideoOutcome.Failed,
                    "fal.ai вернул адрес опроса вне своих хостов — задачу не опрашиваем."));
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException || ex is OperationCanceledException && !outer.IsCancellationRequested)
        {
            return new QueueRun(null, null, VideoResult.Fail(VideoOutcome.Unavailable, "fal.ai не ответил: " + ex.Message));
        }

        // Задачу приняли — с этого момента пишется трата
        _cancelUrls[ticket.RequestId] = ticket.CancelUrl;
        var id = ticket.RequestId;
        progress.Report(new VideoProgress(VideoStage.Queued, RemoteId: id, Accepted: true));
        double? lastPercent = null;
        var reportedRunning = false;
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
                        progress.Report(new VideoProgress(VideoStage.Queued,
                            s.TryGetProperty("queue_position", out var qp) && qp.TryGetInt32(out var pos) ? pos : null,
                            RemoteId: id, Accepted: true));
                    else if (state == "IN_PROGRESS")
                    {
                        var percent = StatusPercent(s);
                        // Без изменения доли не частим: опрос идёт чаще, чем она двигается
                        if (percent != lastPercent || !reportedRunning)
                            progress.Report(new VideoProgress(VideoStage.Running, RemoteId: id, Accepted: true, Percent: percent));
                        lastPercent = percent;
                        reportedRunning = true;
                    }
                }
                await Task.Delay(PollInterval, token);
            }

            using var result = new HttpRequestMessage(HttpMethod.Get, ticket.ResponseUrl);
            Authorize(result);
            using var rresp = await client.SendAsync(result, token);
            var rtext = await rresp.Content.ReadAsStringAsync(token);
            // Неуспешный запрос fal не тарифицирует
            if (!rresp.IsSuccessStatusCode)
                return new QueueRun(null, id, new VideoResult(ClassifyHttp(rresp.StatusCode, rtext), null, null, false, id,
                    ErrorText(rtext, rresp.StatusCode)));
            return new QueueRun(JsonDocument.Parse(rtext).RootElement.Clone(), id, null);
        }
        catch (OperationCanceledException) when (!outer.IsCancellationRequested)
        {
            await CancelRemoteAsync(id, CancellationToken.None);
            return new QueueRun(null, id, new VideoResult(VideoOutcome.Failed, null, null, null, id,
                $"fal.ai не ответил за {Ceiling.TotalMinutes:0} минут"));
        }
        catch (OperationCanceledException)
        {
            // Отмена снаружи: отзываем задачу у поставщика и пробрасываем отмену дальше
            await CancelRemoteAsync(id, CancellationToken.None);
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException)
        {
            return new QueueRun(null, id, new VideoResult(VideoOutcome.Failed, null, null, null, id,
                "Сбой связи с fal.ai: " + ex.Message));
        }
        finally
        {
            _cancelUrls.TryRemove(id, out _);
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

    private static VideoOutcome ClassifyHttp(HttpStatusCode code, string body)
    {
        var lower = body.ToLowerInvariant();
        if (lower.Contains("balance") || lower.Contains("insufficient") || lower.Contains("credit"))
            return VideoOutcome.InsufficientCredits;
        if (lower.Contains("nsfw") || lower.Contains("content policy") || lower.Contains("moderation")
            || lower.Contains("safety"))
            return VideoOutcome.Rejected;
        return code is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.ServiceUnavailable
            or HttpStatusCode.BadGateway or HttpStatusCode.GatewayTimeout
            ? VideoOutcome.Unavailable
            : VideoOutcome.Failed;
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

    private HttpClient Client()
    {
        var client = _http.CreateClient(HttpClientName);
        client.Timeout = TimeSpan.FromSeconds(120);
        return client;
    }

    private void Authorize(HttpRequestMessage req) =>
        req.Headers.Authorization = new AuthenticationHeaderValue("Key", _apiKey);

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;
}
