using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ClaudeHomeServer.Services.AudioEditor.Catalog;
using ClaudeHomeServer.Services.Http;
using ClaudeHomeServer.Services.AudioEditor.Schema;

namespace ClaudeHomeServer.Services.AudioEditor.Engines;

// Поставщик fal.ai для звука (ADR-021 §2) по образцу FalImageEditor: очередь queue.fal.run — отправка,
// опрос, отмена по cancel_url, скачивание всех файлов результата (стемы, текст и субтитры). Тот же ключ
// Fal:ApiKey (или FAL_KEY) и тот же тихий HTTP-клиент "fal", что у картинок; нет ключа — Enabled=false.
// Отобранные эндпоинты и раскладка общих полей запроса на поля fal — каталог (AudioCatalog.Fal). Входной
// звук уходит data: URI. Цена до запуска — прайс /v1/models/pricing с кешем на сутки в единицах fal как
// есть (за 1000 символов, секунду, минуту, запуск); нет ответа — ориентир каталога. Схема входа для
// «Дополнительно» и проверки params — OpenAPI эндпоинта из /v1/models с кешем на инстанс (SchemaTtl);
// fal не ответил — последняя удачная схема с пометкой Stale, без неё — отказ значением. Работает в любой
// области: файлы проекта ему не нужны, результат возвращается байтами.
public sealed class FalAudioEngine : IAudioEngine, IAudioQuoter, IAudioParamSchemas
{
    public const string ProviderKey = "fal";
    private const string HttpClientName = "fal";
    private static readonly TimeSpan PriceCacheTtl = TimeSpan.FromHours(24);
    // Схемы fal меняются редко, но меняются: раз в шесть часов перечитываем
    internal static readonly TimeSpan SchemaTtl = TimeSpan.FromHours(6);
    // Песня ElevenLabs до 10 минут плюс очередь
    private static readonly TimeSpan JobCeiling = TimeSpan.FromMinutes(15);

    private readonly IHttpClientFactory _http;
    private readonly string? _apiKey;
    private readonly string _queueBase;
    private readonly string _apiBase;
    private readonly ILogger<FalAudioEngine> _log;
    private readonly ConcurrentDictionary<string, (double Price, string Unit, DateTime At)> _prices = new();
    // cancel_url задач в работе: отмена у fal идёт по нему, а не по request_id
    private readonly ConcurrentDictionary<string, string> _cancelUrls = new();
    // Поля входа эндпоинта и время чтения; запись остаётся и после истечения TTL — на случай, если fal не ответит
    private readonly ConcurrentDictionary<string, (IReadOnlyList<AudioParamField> Fields, DateTimeOffset At)> _schemas = new();

    // Пауза опроса статуса; тесты ставят ноль
    internal TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(1);
    internal TimeSpan Ceiling { get; set; } = JobCeiling;
    // Часы кеша схем; тесты двигают время
    internal TimeProvider Time { get; set; } = TimeProvider.System;
    // Скачивание файлов результата: ссылка из ответа fal — внешняя, качаем только общим безопасным
    // загрузчиком (SSRF, редиректы, потолок). Срок шире, чем у картинок: 200 МБ на медленном канале
    // за минуту не скачать, а общий срок задачи всё равно держит Ceiling. Тесты подставляют фейк
    internal SafeMediaDownloader Downloader { get; init; } = SharedDownloader;
    internal long MaxDownloadBytes { get; init; } = SafeMediaDownloader.AudioMaxBytes;

    private static readonly SafeMediaDownloader SharedDownloader =
        new(SafeMediaDownloader.CreateHandler()) { Timeout = TimeSpan.FromMinutes(5) };

    private static readonly IReadOnlyList<AudioModelInfo> FalModels = [.. AudioCatalog.Fal.Select(m => m.Info)];

    public FalAudioEngine(IHttpClientFactory http, IConfiguration config, ILogger<FalAudioEngine> log)
    {
        _http = http;
        _apiKey = config["Fal:ApiKey"] ?? Environment.GetEnvironmentVariable("FAL_KEY");
        _queueBase = (config["Fal:QueueBase"] ?? "https://queue.fal.run").TrimEnd('/');
        _apiBase = (config["Fal:ApiBase"] ?? "https://api.fal.ai/v1").TrimEnd('/');
        _log = log;
    }

    public string Key => ProviderKey;
    public string Label => "fal.ai";
    public string PriceUnit => AudioPriceUnits.Usd;
    public bool Enabled => !string.IsNullOrWhiteSpace(_apiKey);
    public IReadOnlyList<AudioModelInfo> Models => FalModels;

    // ── Котировка ────────────────────────────────────────────────────────────────

    public int? ExpectedSeconds(AudioModelInfo model, AudioRequest request) => request.Op switch
    {
        AudioOp.Speak or AudioOp.DesignVoice or AudioOp.CloneVoice or AudioOp.Sfx => 15,
        AudioOp.Song or AudioOp.Cover or AudioOp.Repaint or AudioOp.Outpaint => 90,
        _ => 40,
    };

    public async Task<AudioEstimate> EstimateAsync(AudioModelInfo model, AudioRequest request, CancellationToken ct)
    {
        if (!Enabled) throw new AudioEngineUnavailableException("fal.ai не настроен: нет ключа");
        if (AudioCatalog.FindFal(model.Id) is not { } fal) throw new AudioEngineUnavailableException("У fal.ai нет такой модели");

        var eta = ExpectedSeconds(model, request);
        var main = await EstimateEndpointAsync(fal.Info, request, ct);
        if (fal.Next is not { } next) return main with { EtaSeconds = eta };

        // Цепочка (клон Qwen → озвучка): сумма двух прогонов в единице второго — им тарифицируется текст
        var second = await EstimateEndpointAsync(next.Info, request, ct);
        var amount = main.Amount is { } a && second.Amount is { } b ? a + b : (double?)null;
        return new AudioEstimate(amount, second.Unit, true, second.Source, eta);
    }

    // Цена эндпоинта: прайс fal, без него — ориентир каталога; сумма — цена × число единиц запроса
    private async Task<AudioEstimate> EstimateEndpointAsync(AudioModelInfo info, AudioRequest request, CancellationToken ct)
    {
        if (await PriceAsync(info.Id, ct) is { } price && FalPriceUnits.Map(price.Unit) is { } unit)
            return new AudioEstimate(FalPriceUnits.Amount(unit, price.Price, request), unit.Unit, unit.Approx,
                AudioEstimateSources.Provider);
        if (info.PriceHint is { } hint)
            return new AudioEstimate(FalPriceUnits.Amount(new FalUnit(hint.Unit, 1, false), hint.Amount, request), hint.Unit,
                true, AudioEstimateSources.Catalog);
        return new AudioEstimate(null, PriceUnit, true, AudioEstimateSources.Unknown);
    }

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

    // ── Схема входа («Дополнительно») ──────────────────────────────────────────

    // У цепочки (клон Qwen) params идут во второй прогон — схема его эндпоинта; первый берёт только образец
    public async Task<AudioSchemaLookup> SchemaAsync(AudioModelInfo model, AudioOp op, CancellationToken ct)
    {
        if (!Enabled) return AudioSchemaLookup.Fail("fal.ai не настроен: нет ключа");
        if (AudioCatalog.FindFal(model.Id) is not { } fal || !fal.Info.Caps.Ops.Contains(op))
            return AudioSchemaLookup.Fail("У fal.ai нет такой модели для этой операции");

        var (endpoint, catalogFields, linkTo) = fal.Next is { } next
            ? (next.Info.Id, next.Fields, next.LinkTo)
            : (fal.Info.Id, fal.Fields, (string?)null);
        AudioParamSchema Schema(IReadOnlyList<AudioParamField> fields, bool stale)
        {
            var reserved = FalSchemaReader.Reserved(catalogFields, linkTo, fields);
            return new(ProviderKey, fal.Info.Id, AudioSchemaSources.FalOpenApi,
                [.. fields.Where(f => !reserved.Contains(f.Key))], reserved, stale);
        }

        var now = Time.GetUtcNow();
        (IReadOnlyList<AudioParamField> Fields, DateTimeOffset At)? cached = _schemas.TryGetValue(endpoint, out var c) ? c : null;
        if (cached is { } fresh && now - fresh.At < SchemaTtl) return AudioSchemaLookup.Ok(Schema(fresh.Fields, false));

        var (read, error) = await FetchSchemaAsync(endpoint, ct);
        if (read is not null)
        {
            _schemas[endpoint] = (read, now);
            return AudioSchemaLookup.Ok(Schema(read, false));
        }
        return cached is { } stale
            ? AudioSchemaLookup.Ok(Schema(stale.Fields, true))
            : AudioSchemaLookup.Fail("Схема модели fal.ai недоступна: " + error);
    }

    private async Task<(IReadOnlyList<AudioParamField>? Fields, string? Error)> FetchSchemaAsync(string endpoint, CancellationToken ct)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get,
                $"{_apiBase}/models?endpoint_id={Uri.EscapeDataString(endpoint)}&expand=openapi-3.0");
            Authorize(req);
            using var resp = await Client().SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode) return (null, $"fal.ai ответил {(int)resp.StatusCode}");
            var json = await resp.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ct);
            if (!json.TryGetProperty("models", out var models) || models.ValueKind != JsonValueKind.Array)
                return (null, "в ответе нет моделей");
            foreach (var m in models.EnumerateArray())
            {
                if (Str(m, "endpoint_id") is { } id && !string.Equals(id, endpoint, StringComparison.OrdinalIgnoreCase)) continue;
                if (!m.TryGetProperty("openapi", out var openapi) || openapi.ValueKind != JsonValueKind.Object) continue;
                if (FalSchemaReader.Read(openapi) is { } fields) return (fields, null);
            }
            return (null, "в OpenAPI нет схемы входа");
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            _log.LogDebug(ex, "fal: схема {Endpoint} недоступна", endpoint);
            return (null, ex.Message);
        }
    }

    // ── Голос из библиотеки ──────────────────────────────────────────────────────

    // Образец уходит ссылкой (data: URI) в поле образца модели: клон Qwen (эмбеддинг кешируется — второй раз
    // идёт сразу озвучка), Chatterbox, клон MiniMax. Озвучка MiniMax берёт custom_voice_id клона из кеша;
    // сам клон MiniMax создаётся только кнопкой пересоздания (StoredClone.Creates)
    public string? LibraryVoicesRefusal => null;

    private static readonly HashSet<string> MiniMaxSpeech =
        new([AudioCatalog.FalMiniMaxHd, AudioCatalog.FalMiniMaxTurbo], StringComparer.OrdinalIgnoreCase);

    private static readonly HashSet<string> SampleClones =
        new([AudioCatalog.FalQwenClone, AudioCatalog.FalChatterbox, AudioCatalog.FalMiniMaxClone], StringComparer.OrdinalIgnoreCase);

    public string? LibraryVoiceRefusal(AudioModelInfo model, AudioOp op, AudioVoiceUse voice)
    {
        if (voice.IsRvc) return "Голос-модель RVC работает только у локальных моделей";
        if (voice.Sample is null) return "У голоса нет записей";
        if (MiniMaxSpeech.Contains(model.Id) && op == AudioOp.Speak) return null;
        if (SampleClones.Contains(model.Id) && model.Caps.Ops.Contains(op)) return null;
        return $"Модель «{model.Label}» не берёт голос из библиотеки";
    }

    // Потолок образца, который едет data: URI внутри JSON. 20 МБ — предел файла клона у самого MiniMax
    // (запись 10 с – 5 мин); base64 раздувает его до ~27 МБ тела, и больше этого запрос не доезжает, а
    // висит до таймаута. Chatterbox и Qwen-клон берут короткий образец, им хватает того же потолка с
    // запасом: отдельного предела у них fal не объявляет, а упираются они в тот же размер тела запроса.
    // 60 секунд в подсказке — WAV 16 бит 44,1 кГц стерео (~10 МБ), с запасом до потолка
    public const int MaxDataUriSampleBytes = 20 * 1024 * 1024;
    public const string SampleTooLargeText =
        "Образец слишком большой для этой модели (больше 20 МБ) — укоротите его до 60 секунд";

    private static readonly HashSet<string> DataUriCapped =
        new([AudioCatalog.FalMiniMaxClone, AudioCatalog.FalChatterbox, AudioCatalog.FalQwenClone], StringComparer.OrdinalIgnoreCase);

    public (string Key, bool Creates)? StoredClone(AudioModelInfo model, AudioOp op) =>
        MiniMaxSpeech.Contains(model.Id) ? (Voices.VoiceProviders.MiniMax, false)
        : string.Equals(model.Id, AudioCatalog.FalMiniMaxClone, StringComparison.OrdinalIgnoreCase) ? (Voices.VoiceProviders.MiniMax, true)
        : null;

    // ── Запуск ───────────────────────────────────────────────────────────────────

    public async Task<AudioResult> RunAsync(AudioRequest req, IProgress<AudioProgress> progress, CancellationToken ct)
    {
        if (!Enabled) return AudioResult.Fail(AudioOutcome.Unavailable, "fal.ai не настроен: нет ключа");
        if (AudioCatalog.FindFal(req.Model) is not { } model || !model.Info.Caps.Ops.Contains(req.Op))
            return AudioResult.Fail(AudioOutcome.Failed, "У fal.ai нет такой модели для этой операции");

        var voice = req.Voice;
        if (voice?.Sample is { } sample && req.Reference is null) req = req with { Reference = sample };
        var miniMaxId = voice?.CachedId(Voices.VoiceProviders.MiniMax);
        if (voice is not null && MiniMaxSpeech.Contains(model.Info.Id) && miniMaxId is null)
            return AudioResult.Fail(AudioOutcome.Rejected, "У голоса нет клона MiniMax — создайте его кнопкой «Пересоздать»");
        // Эмбеддинг Qwen-клона из кеша: первый прогон цепочки не нужен
        var embedding = model.Next is not null ? voice?.CachedId(Voices.VoiceProviders.FalQwen) : null;
        // С эмбеддингом из кеша образец никуда не едет — и потолок ему не нужен
        if (embedding is null && req.Reference is { } reference && DataUriCapped.Contains(model.Info.Id)
            && reference.Bytes.Length > MaxDataUriSampleBytes)
            return AudioResult.Fail(AudioOutcome.Rejected, SampleTooLargeText);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(Ceiling);
        JsonElement output = default;
        string? requestId = null;
        List<AudioVoiceCacheEntry> created = [];
        if (embedding is null)
        {
            JsonObject body;
            try
            {
                body = FalRequestBuilder.Build(model.Fields, req, withParams: model.Next is null);
            }
            catch (ArgumentException ex)
            {
                return AudioResult.Fail(AudioOutcome.Failed, ex.Message);
            }
            if (miniMaxId is not null && MiniMaxSpeech.Contains(model.Info.Id))
            {
                // Клон MiniMax — voice_id в voice_setting; прочие настройки голоса из params сохраняются
                var setting = body["voice_setting"] as JsonObject ?? new JsonObject();
                setting["voice_id"] = miniMaxId;
                body["voice_setting"] = setting;
            }

            var first = await RunQueuedAsync(model.Info.Id, body, progress, ct, timeout.Token);
            if (first.Failure is { } failed) return failed;
            output = first.Output!.Value;
            requestId = first.RequestId!;
            if (voice is not null && string.Equals(model.Info.Id, AudioCatalog.FalMiniMaxClone, StringComparison.OrdinalIgnoreCase))
            {
                if (Str(output, "custom_voice_id") is not { Length: > 0 } customId)
                    return Charged(AudioResult.Fail(AudioOutcome.Failed, "fal.ai не вернул id клона MiniMax"), requestId);
                created.Add(new AudioVoiceCacheEntry(Voices.VoiceProviders.MiniMax, customId));
            }
        }
        if (model.Next is { } next)
        {
            // Второй прогон цепочки: ссылка на файл первого (эмбеддинг голоса) — полем второго запроса
            var link = embedding;
            if (link is null)
            {
                if (FalOutputs.FileUrl(output, next.LinkFrom) is not { } fresh)
                    return Charged(AudioResult.Fail(AudioOutcome.Failed, "fal.ai не вернул эмбеддинг голоса"), requestId!);
                // Ссылку скачивает уже fal, но и ей не доверяем: внутренний адрес — отказ до второго прогона
                if (await Downloader.CheckAsync(fresh, timeout.Token) is { } refused)
                    return Charged(AudioResult.Fail(AudioOutcome.Failed,
                        "fal.ai вернул недопустимую ссылку на эмбеддинг: " + FalOutputs.Explain(refused, MaxDownloadBytes)), requestId!);
                link = fresh;
                if (voice is not null) created.Add(new AudioVoiceCacheEntry(Voices.VoiceProviders.FalQwen, fresh));
            }
            JsonObject nextBody;
            try
            {
                nextBody = FalRequestBuilder.Build(next.Fields, req);
            }
            catch (ArgumentException ex)
            {
                return Cached(requestId is null ? AudioResult.Fail(AudioOutcome.Failed, ex.Message)
                    : Charged(AudioResult.Fail(AudioOutcome.Failed, ex.Message), requestId), created);
            }
            nextBody[next.LinkTo] = link;
            var second = await RunQueuedAsync(next.Info.Id, nextBody, progress, ct, timeout.Token);
            // Первый прогон уже тарифицирован, что бы ни случилось со вторым
            if (second.Failure is { } secondFailed)
                return Cached(requestId is null ? secondFailed : Charged(secondFailed, requestId), created);
            output = second.Output!.Value;
            requestId = second.RequestId!;
        }

        progress.Report(new AudioProgress(AudioStage.Downloading));
        try
        {
            var (files, error) = await FalOutputs.CollectAsync(Downloader, MaxDownloadBytes, model.Output, output, timeout.Token);
            if (error is not null)
                return Cached(new AudioResult(AudioOutcome.Failed, [], null, true, requestId, "Не удалось скачать результат fal.ai: " + error), created);
            return Cached(files.Count == 0
                ? new AudioResult(AudioOutcome.Failed, [], null, null, requestId, "fal.ai не вернул файлов")
                : new AudioResult(AudioOutcome.Ok, files, null, true, requestId, null), created);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return Cached(new AudioResult(AudioOutcome.Failed, [], null, true, requestId, "fal.ai не отдал файлы вовремя"), created);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException)
        {
            return Cached(new AudioResult(AudioOutcome.Failed, [], null, true, requestId, "Не удалось скачать результат fal.ai: " + ex.Message), created);
        }
    }

    private static AudioResult Cached(AudioResult result, List<AudioVoiceCacheEntry> created) =>
        created.Count == 0 ? result : result with { VoiceCache = created };

    private static AudioResult Charged(AudioResult result, string remoteId) =>
        result with { Charged = true, RemoteId = result.RemoteId ?? remoteId };

    private sealed record QueueRun(JsonElement? Output, string? RequestId, AudioResult? Failure);

    // Один прогон очереди: отправка, опрос до COMPLETED, чтение результата. Отмена снаружи отзывает задачу
    // у fal и пробрасывается; потолок времени — отказ значением
    private async Task<QueueRun> RunQueuedAsync(string endpoint, JsonObject body, IProgress<AudioProgress> progress,
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
                return new QueueRun(null, null, AudioResult.Fail(ClassifyHttp(resp.StatusCode, text), ErrorText(text, resp.StatusCode)));
            ticket = ParseTicket(text) ?? throw new JsonException("нет request_id в ответе очереди");
            // Ключ уходит и на эти адреса — чужой хост не опрашиваем
            if (!new[] { ticket.StatusUrl, ticket.ResponseUrl, ticket.CancelUrl }.All(u => FalQueueUrls.IsTrusted(u, _queueBase)))
                return new QueueRun(null, ticket.RequestId, AudioResult.Fail(AudioOutcome.Failed,
                    "fal.ai вернул адрес опроса вне своих хостов — задачу не опрашиваем."));
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException || ex is OperationCanceledException && !outer.IsCancellationRequested)
        {
            return new QueueRun(null, null, AudioResult.Fail(AudioOutcome.Unavailable, "fal.ai не ответил: " + ex.Message));
        }

        // Задачу приняли — дальше списание возможно
        _cancelUrls[ticket.RequestId] = ticket.CancelUrl;
        progress.Report(new AudioProgress(AudioStage.Queued));
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
                        progress.Report(new AudioProgress(AudioStage.Queued,
                            s.TryGetProperty("queue_position", out var qp) && qp.TryGetInt32(out var pos) ? pos : null));
                    else if (state == "IN_PROGRESS")
                        progress.Report(new AudioProgress(AudioStage.Running));
                }
                await Task.Delay(PollInterval, token);
            }

            using var result = new HttpRequestMessage(HttpMethod.Get, ticket.ResponseUrl);
            Authorize(result);
            using var rresp = await client.SendAsync(result, token);
            var rtext = await rresp.Content.ReadAsStringAsync(token);
            // Неуспешный запрос fal не тарифицирует
            if (!rresp.IsSuccessStatusCode)
                return new QueueRun(null, ticket.RequestId, new AudioResult(ClassifyHttp(rresp.StatusCode, rtext), [], null, false,
                    ticket.RequestId, ErrorText(rtext, rresp.StatusCode)));
            return new QueueRun(JsonDocument.Parse(rtext).RootElement.Clone(), ticket.RequestId, null);
        }
        catch (OperationCanceledException) when (!outer.IsCancellationRequested)
        {
            await CancelRemoteAsync(ticket.RequestId, CancellationToken.None);
            return new QueueRun(null, ticket.RequestId, new AudioResult(AudioOutcome.Failed, [], null, null, ticket.RequestId,
                $"fal.ai не ответил за {Ceiling.TotalMinutes:0} минут"));
        }
        catch (OperationCanceledException)
        {
            // Отмена снаружи: отзываем задачу у поставщика и пробрасываем отмену дальше
            await CancelRemoteAsync(ticket.RequestId, CancellationToken.None);
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException)
        {
            return new QueueRun(null, ticket.RequestId, new AudioResult(AudioOutcome.Failed, [], null, null, ticket.RequestId,
                "Сбой связи с fal.ai: " + ex.Message));
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

    private static AudioOutcome ClassifyHttp(HttpStatusCode code, string body)
    {
        var lower = body.ToLowerInvariant();
        if (lower.Contains("balance") || lower.Contains("insufficient") || lower.Contains("credit"))
            return AudioOutcome.InsufficientCredits;
        if (lower.Contains("nsfw") || lower.Contains("content policy") || lower.Contains("moderation")
            || lower.Contains("safety"))
            return AudioOutcome.Rejected;
        return code is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.ServiceUnavailable
            or HttpStatusCode.BadGateway or HttpStatusCode.GatewayTimeout
            ? AudioOutcome.Unavailable
            : AudioOutcome.Failed;
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

    internal static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;
}

// Единица прайса fal → наша: Divisor — «за 1000 символов», Approx — «compute seconds» (время машины, а не
// длина звука: считаем по длине входа, это верхняя оценка)
internal sealed record FalUnit(string Unit, double Divisor, bool Approx);

internal static class FalPriceUnits
{
    public static FalUnit? Map(string? falUnit)
    {
        if (string.IsNullOrWhiteSpace(falUnit)) return null;
        var unit = falUnit.Trim().ToLowerInvariant();
        var parts = unit.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        var divisor = parts.Length == 2 && double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && d > 0
            ? d
            : 1;
        if (unit.Contains("character") || unit.Contains("char")) return new FalUnit(AudioPriceUnits.Chars, divisor, false);
        if (unit.Contains("compute")) return new FalUnit(AudioPriceUnits.Sec, divisor, true);
        if (unit.Contains("minute")) return new FalUnit(AudioPriceUnits.Min, divisor, false);
        if (unit.Contains("second")) return new FalUnit(AudioPriceUnits.Sec, divisor, false);
        // units, generations, audios, requests — за запуск
        return new FalUnit(AudioPriceUnits.Run, 1, false);
    }

    // Сумма: символы — текст озвучки (иначе слова, иначе промпт); секунды и минуты — DurationSec запроса
    // (длина результата у генерации, длина входа у обработки). Единиц не знаем — суммы нет
    public static double? Amount(FalUnit unit, double price, AudioRequest request)
    {
        double? units = unit.Unit switch
        {
            AudioPriceUnits.Chars => (request.Text ?? request.Lyrics ?? request.Prompt)?.Length,
            AudioPriceUnits.Sec => request.DurationSec,
            AudioPriceUnits.Min => request.DurationSec / 60.0,
            _ => 1,
        };
        return units is { } u ? price * u / unit.Divisor : null;
    }
}
