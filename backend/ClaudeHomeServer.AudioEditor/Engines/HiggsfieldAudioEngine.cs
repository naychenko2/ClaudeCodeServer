using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using ClaudeHomeServer.Services.AudioEditor.Catalog;
using ClaudeHomeServer.Services.Higgsfield;

namespace ClaudeHomeServer.Services.AudioEditor.Engines;

// Поставщик Higgsfield для звука (ADR-021 §2) поверх Core-клиента HiggsfieldMcpClient: из Higgsfield в
// звуке только речь и клон голоса. Платит инстансный аккаунт админа, трату per-user пишет исполнитель —
// в кредитах. Нет доступа (интеграция не подключена или отключена) — Enabled=false.
//
// Каталог моделей — живой (HiggsfieldAudioCatalog), кеш на ModelsTtl: сбой обновления оставляет прежний
// список. Цепочка запуска как у картинок: образец голоса — media_upload → PUT → media_confirm, цена
// запуска — препроверкой get_cost, запуск generate_audio с ЯВНЫМ use_unlim:false и аргументами ВНУТРИ
// params (плоские Higgsfield отвергает), опрос jobs_wait, скачивание. Инструмента отмены у Higgsfield нет.
//
// Разбор ответов (кредиты, id заданий, слот загрузки, классификатор отказов) повторяет драйвер картинок
// HiggsfieldImageEditor: вертикаль на вертикаль не ссылается, а форма ответов одна
public sealed partial class HiggsfieldAudioEngine(HiggsfieldMcpClient client, TimeProvider? time = null) : IAudioEngine, IAudioQuoter
{
    public const string ProviderKey = "higgsfield";
    public static readonly TimeSpan ModelsTtl = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan JobCeiling = TimeSpan.FromMinutes(6);

    // Пауза между пустыми ответами jobs_wait; сам jobs_wait ждёт до 15 с на сервере
    internal TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(1);

    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly SemaphoreSlim _refresh = new(1, 1);
    private IReadOnlyList<HiggsfieldAudioCatalog.Model> _catalog = [];
    private IReadOnlyList<AudioModelInfo> _models = [];
    private DateTimeOffset? _loadedAt;

    public string Key => ProviderKey;
    public string Label => "Higgsfield";
    public string PriceUnit => AudioPriceUnits.Credits;
    public bool Enabled => client.Available;
    public IReadOnlyList<AudioModelInfo> Models => _models;
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
                new JsonObject { ["action"] = "list", ["type"] = "audio", ["limit"] = 100 }, ct);
            if (!call.Ok) return;
            var parsed = HiggsfieldAudioCatalog.Parse(call.Json());
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

    private HiggsfieldAudioCatalog.Model? Find(string id) =>
        _catalog.FirstOrDefault(m => string.Equals(m.Info.Id, id, StringComparison.OrdinalIgnoreCase));

    // Дикторы для выбора голоса (инструмент агента audio_voices). Форма ответа list_voices живьём не
    // сверена — разбор терпимый: массив voices/items/корень, id — voice_id или id. Сбой — пустой список
    public async Task<IReadOnlyList<HiggsfieldVoice>> ListVoicesAsync(string? model, CancellationToken ct)
    {
        var args = new JsonObject();
        if (!string.IsNullOrWhiteSpace(model)) args["model"] = model.Trim();
        var call = await client.CallToolAsync("list_voices", args, ct);
        if (!call.Ok) return [];
        var json = call.Json();
        var items = json?["voices"] as JsonArray ?? json?["items"] as JsonArray ?? json as JsonArray;
        var voices = new List<HiggsfieldVoice>();
        foreach (var v in items?.OfType<JsonObject>() ?? [])
            if ((v["voice_id"] ?? v["id"])?.ToString() is { Length: > 0 } id)
                voices.Add(new HiggsfieldVoice(id, (v["name"] ?? v["label"])?.ToString() ?? id,
                    v["language"]?.ToString(), v["gender"]?.ToString()));
        return voices;
    }

    // ── Котировка ────────────────────────────────────────────────────────────────

    public int? ExpectedSeconds(AudioModelInfo model, AudioRequest request) => 20;

    // get_cost: true — препроверка без запуска, точно в кредитах за ОДИН запуск (вариант = отдельный
    // запуск, итог умножает исполнитель). Цена неизвестна — не отказ: сумму допишет запуск
    public async Task<AudioEstimate> EstimateAsync(AudioModelInfo model, AudioRequest request, CancellationToken ct)
    {
        if (!client.Available) throw new AudioEngineUnavailableException("Higgsfield сейчас недоступен");
        if (Find(model.Id) is not { } entry) throw new AudioEngineUnavailableException("У Higgsfield нет такой модели звука");
        if (entry.Info.DisabledReason is { } reason) throw new AudioEngineUnavailableException(reason);

        var composed = Compose(entry, request);
        if (composed.Args is not { } args) return Unknown;
        args["get_cost"] = true;
        var call = await client.CallToolAsync("generate_audio", Params(args), ct);
        if (call.Unavailable) throw new AudioEngineUnavailableException(call.Text);
        if (!call.Ok && Classify(call.Text) == AudioOutcome.InsufficientCredits)
            throw new AudioEngineUnavailableException(Explain(AudioOutcome.InsufficientCredits, call.Text));
        return ParseCredits(call) is { } credits
            ? new AudioEstimate(credits, PriceUnit, false, AudioEstimateSources.Provider, ExpectedSeconds(model, request))
            : Unknown;
    }

    private static readonly AudioEstimate Unknown = new(null, AudioPriceUnits.Credits, true, AudioEstimateSources.Unknown);

    // ── Запуск ───────────────────────────────────────────────────────────────────

    public async Task<AudioResult> RunAsync(AudioRequest req, IProgress<AudioProgress> progress, CancellationToken ct)
    {
        if (!client.Available) return AudioResult.Fail(AudioOutcome.Unavailable, "Higgsfield сейчас недоступен");
        await RefreshModelsAsync(ct);
        if (Find(req.Model) is not { } entry) return AudioResult.Fail(AudioOutcome.Failed, "У Higgsfield нет такой модели звука");
        // Серая модель не запускается никогда, даже если запрос пришёл в обход каталога
        if (entry.Info.DisabledReason is { } reason) return AudioResult.Fail(AudioOutcome.Rejected, reason);
        if (!entry.Info.Caps.Ops.Contains(req.Op)) return AudioResult.Fail(AudioOutcome.Rejected, "Эта модель Higgsfield так не умеет");

        var composed = Compose(entry, req);
        if (composed.Args is not { } args) return AudioResult.Fail(AudioOutcome.Rejected, composed.Error!);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(JobCeiling);
        var token = timeout.Token;
        try
        {
            if (req.Op == AudioOp.CloneVoice)
            {
                var (mediaId, failure) = await UploadAsync(req.Reference!, token);
                if (failure is not null) return failure;
                args["medias"] = new JsonArray(new JsonObject { ["role"] = "audio_references", ["value"] = mediaId });
            }

            // Точная цена этого запуска — ею трата догоняет котировку без суммы
            var preflight = args.DeepClone().AsObject();
            preflight["get_cost"] = true;
            var cost = await client.CallToolAsync("generate_audio", Params(preflight), token);
            var credits = cost.Unavailable ? null : ParseCredits(cost);

            var started = await client.CallToolAsync("generate_audio", Params(args), token);
            if (started.Unavailable) return AudioResult.Fail(AudioOutcome.Unavailable, started.Text);
            if (IsUnlimChoice(started))
            {
                // Сервер спросил, чем платить: отвечаем «бесплатными» тем же запросом
                args["use_unlim"] = true;
                started = await client.CallToolAsync("generate_audio", Params(args), token);
                if (started.Unavailable) return AudioResult.Fail(AudioOutcome.Unavailable, started.Text);
                if (IsUnlimChoice(started))
                    return AudioResult.Fail(AudioOutcome.Failed,
                        "Higgsfield так и не принял выбор оплаты — запуск не выполнен, кредиты не списаны");
                credits = 0;
            }
            if (!started.Ok)
            {
                var outcome = Classify(started.Text);
                return AudioResult.Fail(outcome, Explain(outcome, started.Text));
            }

            var jobIds = JobIds(started.Json());
            if (jobIds.Count == 0) return AudioResult.Fail(AudioOutcome.Failed, "Higgsfield не вернул id задания");

            // Задание принято — кредиты админа ушли
            var remoteId = string.Join(",", jobIds);
            var actual = credits is { } c ? new AudioCost(c, AudioPriceUnits.Credits) : null;
            progress.Report(new AudioProgress(AudioStage.Queued));

            var (url, error) = await WaitAsync(jobIds, progress, token);
            if (url is null)
            {
                var outcome = error is null ? AudioOutcome.Failed : Classify(error);
                return new AudioResult(outcome, [], actual, true, remoteId,
                    error is null ? "Higgsfield не вернул звука" : Explain(outcome, error));
            }

            progress.Report(new AudioProgress(AudioStage.Downloading));
            var download = await client.DownloadBytesAsync(url, token);
            if (download is null)
                return new AudioResult(AudioOutcome.Failed, [], actual, true, remoteId, "Не удалось скачать результат Higgsfield");
            var (contentType, extension) = Format(download.ContentType, url, args["format"]?.ToString());
            return new AudioResult(AudioOutcome.Ok, [new AudioFile(AudioOutputs.Audio, download.Bytes, contentType, extension)],
                actual, true, remoteId, null);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return AudioResult.Fail(AudioOutcome.Failed, "Higgsfield не ответил за 6 минут", charged: null);
        }
    }

    // Отмены у Higgsfield нет: работа у поставщика идёт дальше, кредиты, скорее всего, списаны
    public Task<bool> CancelRemoteAsync(string remoteId, CancellationToken ct) => Task.FromResult(false);

    private async Task<(string? Url, string? Error)> WaitAsync(List<string> jobIds, IProgress<AudioProgress> progress,
        CancellationToken ct)
    {
        while (true)
        {
            var waitArgs = new JsonObject
            {
                ["jobs"] = new JsonArray(jobIds.Select((id, i) => (JsonNode)new JsonObject { ["index"] = i, ["job_id"] = id }).ToArray()),
                ["timeout_seconds"] = 15,
            };
            var wait = await client.CallToolAsync("jobs_wait", waitArgs, ct);
            if (wait.Unavailable) return (null, wait.Text);

            var json = wait.Json();
            var allTerminal = json?["all_terminal"] is JsonValue at && at.TryGetValue<bool>(out var t) && t;
            var anyActive = false;
            string? error = null;
            if (json?["jobs"] is JsonArray jobs)
            {
                foreach (var job in jobs.OfType<JsonObject>())
                {
                    var status = job["status"]?.ToString()?.ToLowerInvariant() ?? "";
                    if (status == "completed" && (job["result_url"] ?? job["url"])?.ToString() is { Length: > 0 } url)
                        return (url, null);
                    if (status is "failed" or "error" or "nsfw" or "cancelled" or "canceled")
                        error = job["error"]?.ToString() ?? status;
                    else
                        anyActive = true;
                }
            }
            if (allTerminal || (json?["jobs"] is JsonArray && !anyActive)) return (null, error);
            progress.Report(new AudioProgress(AudioStage.Running));
            await Task.Delay(PollInterval, ct);
        }
    }

    // ── Состав запроса ───────────────────────────────────────────────────────────

    // Аргументы generate_audio по схеме модели: Params — только имена из схемы и значения из её
    // вариантов и пределов; обязательное, которого нет, — отказ ДО траты. Error — текст для человека
    internal static (JsonObject? Args, string? Error) Compose(HiggsfieldAudioCatalog.Model model, AudioRequest req)
    {
        var text = req.Text ?? req.Prompt;
        if (string.IsNullOrWhiteSpace(text)) return (null, "Нужен текст для озвучки");
        if (model.Info.Caps.MaxTextChars is { } max && text.Length > max)
            return (null, $"Текст длиннее {max} символов ({text.Length})");
        if (req.Op == AudioOp.CloneVoice && (req.Reference is null || !model.AcceptsAudioReference))
            return (null, "Для клона голоса нужен образец голоса");

        var args = new JsonObject { ["model"] = model.Info.Id, ["prompt"] = text, ["use_unlim"] = false };
        var fields = req.Params ?? new JsonObject();
        foreach (var (name, value) in fields)
        {
            if (HiggsfieldAudioCatalog.Reserved.Contains(name) || !model.Params.TryGetValue(name, out var param) || value is null)
                continue;
            if (Check(param, value) is { } bad) return (null, bad);
            args[name] = value.DeepClone();
        }
        if (req.Language is { Length: > 0 } language && model.Params.TryGetValue("language", out var lp) && !args.ContainsKey("language"))
        {
            if (lp.Options.Count > 0 && !lp.Options.Contains(language)) return (null, $"Модель не знает язык «{language}»");
            args["language"] = language;
        }
        if (req.Seed is { } seed && model.Params.ContainsKey("seed") && !args.ContainsKey("seed")) args["seed"] = seed;

        if (model.IsDialogue)
        {
            // Реплики целиком из Params (агент собрал диалог сам) или одна реплика из текста и голоса
            if (args["dialogue"] is not JsonArray)
            {
                if (fields["voice_type"]?.ToString() is not { Length: > 0 } type || fields["voice_id"]?.ToString() is not { Length: > 0 } id)
                    return (null, "Выберите диктора: нужны voice_type и voice_id");
                args["dialogue"] = new JsonArray(new JsonObject { ["text"] = text, ["voice_type"] = type, ["voice_id"] = id });
            }
        }
        else if (args.ContainsKey("voice_type") != args.ContainsKey("voice_id")
                 && model.Params.ContainsKey("voice_type") && model.Params.ContainsKey("voice_id"))
        {
            return (null, "voice_type и voice_id задаются только вместе");
        }

        foreach (var param in model.Params.Values)
            if (param.Required && !args.ContainsKey(param.Name))
                return (null, param.Name is "voice_type" or "voice_id"
                    ? "Выберите диктора: нужны voice_type и voice_id"
                    : $"Не задан обязательный параметр «{param.Name}»");
        return (args, null);
    }

    private static string? Check(HiggsfieldAudioCatalog.Param param, JsonNode value)
    {
        if (param.Options.Count > 0)
        {
            var text = HiggsfieldAudioCatalog.Text(value);
            if (text is null || !param.Options.Contains(text))
                return $"Недопустимое значение «{param.Name}»: {value.ToJsonString()}";
        }
        if (param.Type == "number")
        {
            if (HiggsfieldAudioCatalog.Number(value) is not { } d)
                return $"«{param.Name}» должен быть числом";
            if (d < param.Min || d > param.Max)
                return $"«{param.Name}» вне пределов {param.Min?.ToString(CultureInfo.InvariantCulture)}–{param.Max?.ToString(CultureInfo.InvariantCulture)}";
        }
        return null;
    }

    private async Task<(string? MediaId, AudioResult? Failure)> UploadAsync(AudioBytes sample, CancellationToken ct)
    {
        var contentType = string.IsNullOrWhiteSpace(sample.ContentType) ? "audio/wav" : sample.ContentType;
        var upload = await client.CallToolAsync("media_upload",
            new JsonObject { ["filename"] = "reference" + ExtensionOf(contentType), ["content_type"] = contentType }, ct);
        if (upload.Unavailable) return (null, AudioResult.Fail(AudioOutcome.Unavailable, upload.Text));
        if (!upload.Ok) return (null, AudioResult.Fail(Classify(upload.Text), Explain(Classify(upload.Text), upload.Text)));

        if (ParseUploadSlot(upload) is not { } slot)
            return (null, AudioResult.Fail(AudioOutcome.Failed, "Higgsfield не выдал адрес загрузки"));
        if (!await client.PutAsync(slot.Url, sample.Bytes, contentType, ct))
            return (null, AudioResult.Fail(AudioOutcome.Unavailable, "Не удалось загрузить образец голоса в Higgsfield"));

        var confirm = await client.CallToolAsync("media_confirm",
            new JsonObject { ["type"] = "audio", ["media_id"] = slot.MediaId }, ct);
        if (confirm.Unavailable) return (null, AudioResult.Fail(AudioOutcome.Unavailable, confirm.Text));
        if (!confirm.Ok) return (null, AudioResult.Fail(Classify(confirm.Text), Explain(Classify(confirm.Text), confirm.Text)));
        return (slot.MediaId, null);
    }

    // ── Разбор ответов (форма — как у HiggsfieldImageEditor) ─────────────────────

    // «Cost preflight for seed_audio: 0.2 credits (0.2 exact)» (замер 2026-10-01) или structuredContent
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

    internal static AudioOutcome Classify(string text)
    {
        var lower = text.ToLowerInvariant();
        if (lower.Contains("insufficient") || lower.Contains("not enough credit") || lower.Contains("credits")
            && (lower.Contains("balance") || lower.Contains("top up") || lower.Contains("out of")))
            return AudioOutcome.InsufficientCredits;
        if (lower.Contains("nsfw") || lower.Contains("moderation") || lower.Contains("content policy")
            || lower.Contains("safety") || lower.Contains("prohibited"))
            return AudioOutcome.Rejected;
        if (lower.Contains("unauthorized") || lower.Contains("отключён"))
            return AudioOutcome.Unavailable;
        return AudioOutcome.Failed;
    }

    internal static string Explain(AudioOutcome outcome, string raw) => outcome switch
    {
        AudioOutcome.InsufficientCredits =>
            $"На аккаунте Higgsfield закончились кредиты — пусть администратор пополнит баланс, или выберите другого поставщика. Ответ Higgsfield: {raw}",
        AudioOutcome.Rejected => $"Higgsfield отклонил запрос модерацией — переформулируйте текст. Ответ Higgsfield: {raw}",
        AudioOutcome.Unavailable => $"Higgsfield сейчас недоступен. Ответ Higgsfield: {raw}",
        _ => $"Higgsfield отказал в запуске. Ответ Higgsfield: {raw}",
    };

    // Копия: узел JSON не может жить у двух родителей, а args уходят повторно
    private static JsonObject Params(JsonObject args) => new() { ["params"] = args.DeepClone() };

    private static bool IsUnlimChoice(HiggsfieldCall call) =>
        call.Text.Contains("unlim_choice", StringComparison.OrdinalIgnoreCase)
        || call.Structured is JsonObject s && s.ContainsKey("unlim_choice");

    // Тип файла: заголовок ответа, затем расширение ссылки, затем запрошенный format; иначе mp3
    internal static (string ContentType, string Extension) Format(string? contentType, string url, string? format)
    {
        var byType = contentType?.ToLowerInvariant() switch
        {
            "audio/mpeg" or "audio/mp3" => ".mp3",
            "audio/wav" or "audio/x-wav" or "audio/wave" => ".wav",
            "audio/ogg" or "audio/opus" => ".ogg",
            "audio/flac" => ".flac",
            _ => null,
        };
        if (byType is not null) return (contentType!.ToLowerInvariant(), byType);
        var path = Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.AbsolutePath : url;
        var ext = Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".mp3" => ".mp3",
            ".wav" => ".wav",
            ".ogg" or ".opus" => ".ogg",
            ".flac" => ".flac",
            _ => format switch
            {
                "wav" or "pcm" => ".wav",
                "ogg_opus" => ".ogg",
                _ => ".mp3",
            },
        };
        return (ContentTypeOf(ext), ext);
    }

    private static string ContentTypeOf(string ext) => ext switch
    {
        ".wav" => "audio/wav",
        ".ogg" => "audio/ogg",
        ".flac" => "audio/flac",
        _ => "audio/mpeg",
    };

    private static string ExtensionOf(string contentType) => contentType switch
    {
        "audio/mpeg" or "audio/mp3" => ".mp3",
        "audio/ogg" => ".ogg",
        "audio/flac" => ".flac",
        _ => ".wav",
    };
}

public sealed record HiggsfieldVoice(string Id, string Name, string? Language, string? Gender);
