using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using ClaudeHomeServer.Services.Higgsfield;
using ClaudeHomeServer.Services.VideoEditor.Catalog;

namespace ClaudeHomeServer.Services.VideoEditor.Engines;

// Поставщик Higgsfield для видео (ADR-022 §3) поверх Core-клиента HiggsfieldMcpClient. Платит инстансный
// аккаунт админа, трату per-user пишет исполнитель — в кредитах. Нет доступа — Enabled=false.
//
// Каталог — живой (HiggsfieldVideoCatalog), кеш на ModelsTtl: сбой обновления оставляет прежний список, до
// первого успешного ответа он пуст. Цепочка: кадры — media_upload → PUT → media_confirm, цена — препроверкой
// get_cost, запуск generate_video с ЯВНЫМ use_unlim:false и аргументами ВНУТРИ params, кадры — medias с
// ролями start_image/end_image, опрос jobs_wait, скачивание с потолком VideoMaxBytes.
//
// Повтор — только до создания задания: загрузка кадров и препроверка повторяются, generate_video — лишь на
// явный отказ «не знаю такого кадра» (задание не создано). Обрыв связи на самом generate_video не
// повторяется: задание могло создаться, второй запуск списал бы кредиты дважды. После id задания повторяется
// только опрос. Инструмента отмены у Higgsfield нет.
//
// Разбор ответов (кредиты, id заданий, слот загрузки, классификатор отказов) повторяет звуковой драйвер:
// вертикаль на вертикаль не ссылается, а форма ответов одна
public sealed partial class HiggsfieldVideoEngine(HiggsfieldMcpClient client, TimeProvider? time = null,
    SafeMediaDownloader? downloader = null) : IVideoEngine, IVideoQuoter
{
    public const string ProviderKey = "higgsfield";
    public static readonly TimeSpan ModelsTtl = TimeSpan.FromMinutes(30);
    public static readonly TimeSpan DownloadTimeout = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan JobCeiling = TimeSpan.FromMinutes(20);
    private const int PollFailuresLimit = 5;

    // Клип весит сотни мегабайт: общий загрузчик с минутным потолком его не дотянет
    private static readonly SafeMediaDownloader VideoDownloader =
        new(SafeMediaDownloader.CreateHandler()) { Timeout = DownloadTimeout };

    // Пауза между пустыми ответами jobs_wait; сам jobs_wait ждёт до 15 с на сервере
    internal TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(2);

    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly SafeMediaDownloader _downloader = downloader ?? VideoDownloader;
    private readonly SemaphoreSlim _refresh = new(1, 1);
    private IReadOnlyList<HiggsfieldVideoCatalog.Model> _catalog = [];
    private IReadOnlyList<VideoModelInfo> _models = [];
    private DateTimeOffset? _loadedAt;

    public string Key => ProviderKey;
    public string Label => "Higgsfield";
    public string PriceUnit => VideoPriceUnits.Credits;
    public bool Enabled => client.Available;
    public IReadOnlyList<VideoModelInfo> Models => _models;
    public string SpendSource => ClaudeHomeServer.Models.SpendSources.Higgsfield;

    // ── Каталог ──────────────────────────────────────────────────────────────────

    public async ValueTask RefreshModelsAsync(CancellationToken ct)
    {
        if (Fresh() || !client.Available) return;
        await _refresh.WaitAsync(ct);
        try
        {
            if (Fresh()) return;
            var call = await client.CallToolAsync("models_explore",
                new JsonObject { ["action"] = "list", ["type"] = "video", ["limit"] = 100 }, ct);
            if (!call.Ok) return;
            var parsed = HiggsfieldVideoCatalog.Parse(call.Json());
            if (parsed.Count == 0) return;
            _catalog = parsed;
            _models = [.. parsed.Select(m => m.Info)];
            _loadedAt = _time.GetUtcNow();
        }
        finally
        {
            _refresh.Release();
        }
    }

    private bool Fresh() => _loadedAt is { } at && _time.GetUtcNow() - at < ModelsTtl;

    private HiggsfieldVideoCatalog.Model? Find(string id) =>
        _catalog.FirstOrDefault(m => string.Equals(m.Info.Id, id, StringComparison.OrdinalIgnoreCase));

    public IReadOnlySet<string>? ParamNames(VideoModelInfo model) =>
        Find(model.Id) is { } entry
            ? entry.Params.Keys.Where(k => !HiggsfieldVideoCatalog.Reserved.Contains(k)).ToHashSet(StringComparer.Ordinal)
            : null;

    // ── Котировка ────────────────────────────────────────────────────────────────

    public int? ExpectedSeconds(VideoModelInfo model, VideoRequest request) => 120;

    // get_cost: true — препроверка без запуска, кредиты за ОДИН запуск. Кадры для цены не грузим: загрузка —
    // уже след у поставщика. Цена неизвестна — не отказ: сумму допишет запуск
    public async Task<VideoEstimate> EstimateAsync(VideoModelInfo model, VideoRequest request, CancellationToken ct)
    {
        if (!client.Available) throw new VideoEngineUnavailableException("Higgsfield сейчас недоступен");
        if (Find(model.Id) is not { } entry) throw new VideoEngineUnavailableException("У Higgsfield нет такой модели видео");

        var composed = Compose(entry, request);
        if (composed.Args is not { } args) return Unknown;
        args["get_cost"] = true;
        var call = await CallBeforeJobAsync("generate_video", Params(args), ct);
        if (call.Unavailable) throw new VideoEngineUnavailableException(call.Text);
        if (!call.Ok && Classify(call.Text) == VideoOutcome.InsufficientCredits)
            throw new VideoEngineUnavailableException(Explain(VideoOutcome.InsufficientCredits, call.Text));
        return ParseCredits(call) is { } credits
            ? new VideoEstimate(credits, PriceUnit, false, VideoEstimateSources.Provider, ExpectedSeconds(model, request))
            : Unknown;
    }

    private static readonly VideoEstimate Unknown = new(null, VideoPriceUnits.Credits, true, VideoEstimateSources.Unknown);

    // ── Запуск ───────────────────────────────────────────────────────────────────

    public async Task<VideoResult> RunAsync(VideoRequest req, IProgress<VideoProgress> progress, CancellationToken ct)
    {
        if (!client.Available) return VideoResult.Fail(VideoOutcome.Unavailable, "Higgsfield сейчас недоступен");
        await RefreshModelsAsync(ct);
        if (Find(req.Model) is not { } entry) return VideoResult.Fail(VideoOutcome.Failed, "У Higgsfield нет такой модели видео");

        var composed = Compose(entry, req);
        if (composed.Args is not { } args) return VideoResult.Fail(VideoOutcome.Rejected, composed.Error!);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(JobCeiling);
        var token = timeout.Token;
        try
        {
            var (medias, failure) = await UploadFramesAsync(req, token);
            if (failure is not null) return failure;
            if (medias is not null) args["medias"] = medias;

            // Точная цена этого запуска — ею трата догоняет котировку без суммы; нехватка кредитов — до запуска
            var preflight = args.DeepClone().AsObject();
            preflight["get_cost"] = true;
            var cost = await CallBeforeJobAsync("generate_video", Params(preflight), token);
            if (!cost.Ok && !cost.Unavailable && Classify(cost.Text) == VideoOutcome.InsufficientCredits)
                return VideoResult.Fail(VideoOutcome.InsufficientCredits, Explain(VideoOutcome.InsufficientCredits, cost.Text));
            var credits = ParseCredits(cost);

            var started = await client.CallToolAsync("generate_video", Params(args), token);
            if (!started.Ok && !started.Unavailable && medias is not null && IsMediaMissing(started.Text))
            {
                // Higgsfield не узнал свежезагруженный кадр — задание не создано, списания не было: грузим
                // кадры заново и запускаем ровно ещё раз
                (medias, failure) = await UploadFramesAsync(req, token);
                if (failure is not null) return failure;
                args["medias"] = medias;
                started = await client.CallToolAsync("generate_video", Params(args), token);
            }
            if (started.Unavailable)
                return VideoResult.Fail(VideoOutcome.Unavailable, started.Text, charged: OutcomeUnknown(started.Text) ? null : false);
            if (IsUnlimChoice(started))
                return VideoResult.Fail(VideoOutcome.Failed,
                    "Higgsfield спросил, чем платить, хотя запуск идёт за кредиты — запуск не выполнен, кредиты не списаны");
            if (!started.Ok)
            {
                var outcome = Classify(started.Text);
                return VideoResult.Fail(outcome, Explain(outcome, started.Text));
            }

            var jobIds = JobIds(started.Json());
            if (jobIds.Count == 0) return VideoResult.Fail(VideoOutcome.Failed, "Higgsfield не вернул id задания");

            // Задание принято — кредиты админа ушли, с этой точки generate_video не повторяется
            var remoteId = string.Join(",", jobIds);
            var actual = credits is { } c ? new VideoCost(c, VideoPriceUnits.Credits) : null;
            progress.Report(new VideoProgress(VideoStage.Queued, RemoteId: remoteId, Accepted: true));

            var (url, error) = await WaitAsync(jobIds, remoteId, progress, token);
            if (url is null)
            {
                var outcome = error is null ? VideoOutcome.Failed : Classify(error);
                return new VideoResult(outcome, null, actual, true, remoteId,
                    error is null ? "Higgsfield не вернул ролика" : Explain(outcome, error));
            }

            progress.Report(new VideoProgress(VideoStage.Downloading, RemoteId: remoteId));
            var download = await DownloadAsync(url, token);
            if (download is null)
                return new VideoResult(VideoOutcome.Failed, null, actual, true, remoteId, "Не удалось скачать ролик Higgsfield");
            var (contentType, extension) = Format(download.ContentType, url);
            return new VideoResult(VideoOutcome.Ok, new VideoFile(download.Bytes, contentType, extension), actual, true,
                remoteId, null);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return VideoResult.Fail(VideoOutcome.Failed, $"Higgsfield не ответил за {JobCeiling.TotalMinutes:0} минут", charged: null);
        }
    }

    // Отмены у Higgsfield нет: работа у поставщика идёт дальше, кредиты, скорее всего, списаны
    public Task<bool> CancelRemoteAsync(string remoteId, CancellationToken ct) => Task.FromResult(false);

    // Обрыв связи или 5xx на запуске: задание могло создаться, поэтому «не списано» не утверждаем.
    // Токен не принят или интеграция отключена — до поставщика запуск не дошёл
    private static bool OutcomeUnknown(string text) =>
        !text.Contains("токен", StringComparison.OrdinalIgnoreCase) && !text.Contains("отключён", StringComparison.OrdinalIgnoreCase);

    // Вызов ДО создания задания (загрузка, препроверка): обрыв связи повторяем один раз
    private async Task<HiggsfieldCall> CallBeforeJobAsync(string tool, JsonObject args, CancellationToken ct)
    {
        var call = await client.CallToolAsync(tool, args.DeepClone().AsObject(), ct);
        return call.Unavailable && OutcomeUnknown(call.Text)
            ? await client.CallToolAsync(tool, args.DeepClone().AsObject(), ct)
            : call;
    }

    private async Task<HiggsfieldDownload?> DownloadAsync(string url, CancellationToken ct)
    {
        if (url.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            return await client.DownloadBytesAsync(url, SafeMediaDownloader.VideoMaxBytes, ct);
        var download = await _downloader.DownloadAsync(url, SafeMediaDownloader.VideoMaxBytes, ct);
        return download.Bytes is { } bytes ? new HiggsfieldDownload(bytes, download.ContentType) : null;
    }

    // Опрос до терминального статуса. Сбой самого опроса — не повод бросать оплаченное задание: терпим
    // PollFailuresLimit подряд, потом сдаёмся
    private async Task<(string? Url, string? Error)> WaitAsync(List<string> jobIds, string remoteId,
        IProgress<VideoProgress> progress, CancellationToken ct)
    {
        var failures = 0;
        while (true)
        {
            var waitArgs = new JsonObject
            {
                ["jobs"] = new JsonArray(jobIds.Select((id, i) => (JsonNode)new JsonObject { ["index"] = i, ["job_id"] = id }).ToArray()),
                ["timeout_seconds"] = 15,
            };
            var wait = await client.CallToolAsync("jobs_wait", waitArgs, ct);
            var json = wait.Ok ? wait.Json() : null;
            if (json?["jobs"] is not JsonArray jobs)
            {
                if (++failures >= PollFailuresLimit) return (null, wait.Text.Length > 0 ? wait.Text : null);
                await Task.Delay(PollInterval, ct);
                continue;
            }
            failures = 0;

            var allTerminal = json["all_terminal"] is JsonValue at && at.TryGetValue<bool>(out var t) && t;
            var anyActive = false;
            string? error = null;
            foreach (var job in jobs.OfType<JsonObject>())
            {
                var status = job["status"]?.ToString()?.ToLowerInvariant() ?? "";
                if (status == "completed" && (job["result_url"] ?? job["url"])?.ToString() is { Length: > 0 } url)
                    return (url, null);
                if (status == "completed")
                    error = job["error"]?.ToString() ?? "задание завершилось без ссылки на ролик";
                else if (status is "failed" or "error" or "nsfw" or "cancelled" or "canceled")
                    error = job["error"]?.ToString() ?? status;
                else
                    anyActive = true;
            }
            if (allTerminal || !anyActive) return (null, error);
            progress.Report(new VideoProgress(VideoStage.Running, RemoteId: remoteId));
            await Task.Delay(PollInterval, ct);
        }
    }

    // ── Состав запроса ───────────────────────────────────────────────────────────

    // Аргументы generate_video: общие поля — по возможностям модели, Params — только имена из схемы и
    // значения из её вариантов и пределов. Отказ — ДО траты, Error — текст для человека
    internal static (JsonObject? Args, string? Error) Compose(HiggsfieldVideoCatalog.Model model, VideoRequest req)
    {
        var caps = model.Info.Caps;
        if (string.IsNullOrWhiteSpace(req.Text)) return (null, "Нужно описание сцены");
        if (req.FrameA is not null && !caps.FirstFrame) return (null, $"Модель «{model.Info.Label}» не берёт кадр — только текст");
        if (req.FrameB is not null && !caps.LastFrame) return (null, $"Модель «{model.Info.Label}» не берёт последний кадр");
        if (req.FrameB is not null && req.FrameA is null) return (null, "Последний кадр задаётся только вместе с первым");
        if (!caps.Durations.Contains(req.DurationSec))
            return (null, $"Модель «{model.Info.Label}» не снимает {req.DurationSec} с: можно {string.Join(", ", caps.Durations)}");

        var args = new JsonObject
        {
            ["model"] = model.Info.Id,
            ["prompt"] = req.Text.Trim(),
            ["use_unlim"] = false,
            ["duration"] = req.DurationSec,
        };
        if (req.Aspect is { Length: > 0 } aspect && caps.Aspects.Count > 0)
        {
            if (!caps.Aspects.Contains(aspect)) return (null, $"Модель «{model.Info.Label}» не знает пропорции {aspect}");
            args["aspect_ratio"] = aspect;
        }
        // Звук ставим явно в обе стороны: у многих моделей он включён по умолчанию и стоит дороже
        if (model.SoundParam is { } sound)
            args[sound] = model.SoundAsSwitch ? (req.Sound ? "on" : "off") : req.Sound;
        if (req.Seed is { } seed && model.Params.ContainsKey("seed")) args["seed"] = seed;

        foreach (var (name, value) in req.Params ?? new JsonObject())
        {
            if (HiggsfieldVideoCatalog.Reserved.Contains(name) || !model.Params.TryGetValue(name, out var param) || value is null)
                continue;
            if (Check(param, value) is { } bad) return (null, bad);
            args[name] = value.DeepClone();
        }

        foreach (var param in model.Params.Values)
            if (param.Required && !args.ContainsKey(param.Name))
                return (null, $"Не задан обязательный параметр «{param.Name}»");
        return (args, null);
    }

    private static string? Check(HiggsfieldVideoCatalog.Param param, JsonNode value)
    {
        if (param.Options.Count > 0)
        {
            var text = HiggsfieldVideoCatalog.Text(value);
            if (text is null || !param.Options.Contains(text))
                return $"Недопустимое значение «{param.Name}»: {value.ToJsonString()}";
        }
        if (param.Type == "bool" && HiggsfieldVideoCatalog.Text(value) is not ("true" or "false"))
            return $"«{param.Name}» должен быть true или false";
        if (param.Type == "number")
        {
            if (HiggsfieldVideoCatalog.Number(value) is not { } d)
                return $"«{param.Name}» должен быть числом";
            if (d < param.Min || d > param.Max)
                return $"«{param.Name}» вне пределов {param.Min?.ToString(CultureInfo.InvariantCulture)}–{param.Max?.ToString(CultureInfo.InvariantCulture)}";
        }
        return null;
    }

    // Кадры запроса в medias; null — кадров нет (только текст). Загрузка — до создания задания, её
    // обрыв связи повторяем один раз
    private async Task<(JsonArray? Medias, VideoResult? Failure)> UploadFramesAsync(VideoRequest req, CancellationToken ct)
    {
        if (req.FrameA is null) return (null, null);
        var medias = new JsonArray();
        foreach (var (frame, role, name) in new[] { (req.FrameA, HiggsfieldVideoCatalog.StartImage, "start"), (req.FrameB, HiggsfieldVideoCatalog.EndImage, "end") })
        {
            if (frame is null) continue;
            var (id, failure) = await UploadAsync(frame, name, ct);
            if (failure?.Outcome == VideoOutcome.Unavailable) (id, failure) = await UploadAsync(frame, name, ct);
            if (failure is not null) return (null, failure);
            medias.Add(new JsonObject { ["role"] = role, ["value"] = id });
        }
        return (medias, null);
    }

    private async Task<(string? MediaId, VideoResult? Failure)> UploadAsync(VideoFrameBytes frame, string name, CancellationToken ct)
    {
        var contentType = string.IsNullOrWhiteSpace(frame.ContentType) ? "image/png" : frame.ContentType;
        var upload = await client.CallToolAsync("media_upload",
            new JsonObject { ["filename"] = name + ExtensionOf(contentType), ["content_type"] = contentType }, ct);
        if (upload.Unavailable) return (null, VideoResult.Fail(VideoOutcome.Unavailable, upload.Text));
        if (!upload.Ok) return (null, VideoResult.Fail(Classify(upload.Text), Explain(Classify(upload.Text), upload.Text)));

        if (ParseUploadSlot(upload) is not { } slot)
            return (null, VideoResult.Fail(VideoOutcome.Failed, "Higgsfield не выдал адрес загрузки"));
        if (!await client.PutAsync(slot.Url, frame.Bytes, contentType, ct))
            return (null, VideoResult.Fail(VideoOutcome.Unavailable, "Не удалось загрузить кадр в Higgsfield"));

        var confirm = await client.CallToolAsync("media_confirm",
            new JsonObject { ["type"] = "image", ["media_id"] = slot.MediaId }, ct);
        if (confirm.Unavailable) return (null, VideoResult.Fail(VideoOutcome.Unavailable, confirm.Text));
        if (!confirm.Ok) return (null, VideoResult.Fail(Classify(confirm.Text), Explain(Classify(confirm.Text), confirm.Text)));
        return (slot.MediaId, null);
    }

    // ── Разбор ответов (форма — как у звукового и картиночного драйверов) ─────────

    // «Cost preflight for kling3_0: 10 credits (10 exact). No job submitted.» (замер 2026-10-02) или structuredContent
    internal static double? ParseCredits(HiggsfieldCall call)
    {
        if (!call.Ok) return null;
        if (call.Structured is JsonObject s)
            foreach (var node in new[] { s, s["cost"] as JsonObject })
                foreach (var key in new[] { "credits", "credits_exact", "cost", "total_cost" })
                    if (node?[key] is JsonValue v && v.TryGetValue<double>(out var d))
                        return d;
        var m = CreditsPattern().Match(call.Text);
        return m.Success && double.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var credits)
            ? credits
            : null;
    }

    [GeneratedRegex(@"(\d+(?:\.\d+)?)\s*credits?", RegexOptions.IgnoreCase)]
    private static partial Regex CreditsPattern();

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

    internal static List<string> JobIds(JsonNode? json)
    {
        var ids = new List<string>();
        foreach (var key in new[] { "results", "jobs" })
            if (json?[key] is JsonArray arr)
                foreach (var item in arr.OfType<JsonObject>())
                    if ((item["id"] ?? item["job_id"])?.ToString() is { Length: > 0 } id)
                        ids.Add(id);
        if (ids.Count == 0 && json?["job_id"]?.ToString() is { Length: > 0 } single) ids.Add(single);
        return ids;
    }

    // «Не знаю такого кадра»: фраза якорится целиком, как у звука
    internal static bool IsMediaMissing(string text) =>
        MissingMediaPattern().IsMatch(text) || MissingStatusPattern().IsMatch(text);

    [GeneratedRegex(@"\bmedia\s+(not\s+found|(does\s+)?not\s+exist|(has\s+)?expired)\b", RegexOptions.IgnoreCase)]
    private static partial Regex MissingMediaPattern();

    [GeneratedRegex(@"\b(404|410)\b")]
    private static partial Regex MissingStatusPattern();

    internal static VideoOutcome Classify(string text)
    {
        var lower = text.ToLowerInvariant();
        if (lower.Contains("insufficient") || lower.Contains("not enough credit") || lower.Contains("credits")
            && (lower.Contains("balance") || lower.Contains("top up") || lower.Contains("out of")))
            return VideoOutcome.InsufficientCredits;
        if (lower.Contains("nsfw") || lower.Contains("moderation") || lower.Contains("content policy")
            || lower.Contains("safety") || lower.Contains("prohibited"))
            return VideoOutcome.Rejected;
        if (lower.Contains("unauthorized") || lower.Contains("отключён"))
            return VideoOutcome.Unavailable;
        return VideoOutcome.Failed;
    }

    internal static string Explain(VideoOutcome outcome, string raw) => outcome switch
    {
        VideoOutcome.InsufficientCredits =>
            $"На аккаунте Higgsfield закончились кредиты — пусть администратор пополнит баланс, или выберите другого поставщика. Ответ Higgsfield: {raw}",
        VideoOutcome.Rejected => $"Higgsfield отклонил запрос модерацией — переформулируйте описание или смените кадры. Ответ Higgsfield: {raw}",
        VideoOutcome.Unavailable => $"Higgsfield сейчас недоступен. Ответ Higgsfield: {raw}",
        _ => $"Higgsfield отказал в запуске. Ответ Higgsfield: {raw}",
    };

    // Копия: узел JSON не может жить у двух родителей, а args уходят повторно
    private static JsonObject Params(JsonObject args) => new() { ["params"] = args.DeepClone() };

    private static bool IsUnlimChoice(HiggsfieldCall call) =>
        call.Text.Contains("unlim_choice", StringComparison.OrdinalIgnoreCase)
        || call.Structured is JsonObject s && s.ContainsKey("unlim_choice");

    // Тип файла: заголовок ответа, затем расширение ссылки; иначе mp4
    internal static (string ContentType, string Extension) Format(string? contentType, string url)
    {
        var byType = contentType?.ToLowerInvariant() switch
        {
            "video/mp4" => ".mp4",
            "video/webm" => ".webm",
            "video/quicktime" => ".mov",
            _ => null,
        };
        if (byType is not null) return (contentType!.ToLowerInvariant(), byType);
        var path = Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.AbsolutePath : url;
        return Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".webm" => ("video/webm", ".webm"),
            ".mov" => ("video/quicktime", ".mov"),
            _ => ("video/mp4", ".mp4"),
        };
    }

    private static string ExtensionOf(string contentType) => contentType switch
    {
        "image/jpeg" or "image/jpg" => ".jpg",
        "image/webp" => ".webp",
        _ => ".png",
    };
}
