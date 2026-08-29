using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using ClaudeHomeServer.Protocol;

namespace ClaudeHomeServer.Services.Llm;

// Локальный движок llama-server (https://github.com/ggerganov/llama.cpp/tree/master/examples/server).
// Раскладка «один порт — много моделей»: один процесс llama-server слушает HTTP и
// держит загруженные веса в памяти, поле model в запросе выбирает, какие именно.
//
// Диалект — OpenAI-совместимый /v1/chat/completions. Поведенческий контракт совпадает
// с OllamaClient: null/пусто при любой беде → вызывающий идёт дальше по цепочке; логи
// в Debug, не Error; регистрация через AddQuietHttpClient (Program.cs).
//
// numCtx НЕ передаётся: у llama-server контекст фиксируется ключом -c при старте.
// Ollama:Profiles:*:NumCtx продолжает читаться маршрутизатором, но игнорируется —
// предупреждение печатается один раз при создании (см. ctor).
public sealed class LlamaServerClient : ILocalLlmClient
{
    // Именованный клиент — под этим именем в Program.cs зарегистрирован тихий
    // HTTP-логгер (QuietHttpLogger).
    public const string HttpClientName = "llama-server";

    private readonly IHttpClientFactory _http;
    private readonly ILogger<LlamaServerClient> _logger;
    private readonly Spend.ISpendCollector? _spend;
    private readonly LocalLlmOptions _options;

    public string BaseUrl => _options.BaseUrl;
    public string Model => _options.Model;
    public string TextModel => _options.TextModel;
    public int TimeoutMs => _options.TimeoutMs;
    public bool Enabled => !string.IsNullOrWhiteSpace(BaseUrl) && !string.IsNullOrWhiteSpace(Model);
    public string ProviderKey => LocalLlmOptions.LlamaServer;

    public LlamaServerClient(IHttpClientFactory http, IConfiguration config,
        ILogger<LlamaServerClient> logger, Spend.ISpendCollector? spend = null)
    {
        _http = http;
        _logger = logger;
        _spend = spend;
        _options = LocalLlmOptions.Read(config);

        // Расхождение конфига и реальности не должно быть тихим: Ollama:Profiles:*:NumCtx
        // задаёт num_ctx для Ollama, у llama-server контекст фиксируется ключом -c при
        // старте сервера и в запросе не передаётся. Печатаем ОДИН раз, чтобы оператор
        // не искал, почему длинный промпт обрезается (если контекст сервера короче).
        if (Enabled && config["Ollama:Profiles:small:NumCtx"] is not null
            || config["Ollama:Profiles:text:NumCtx"] is not null
            || config["Ollama:Profiles:large:NumCtx"] is not null)
        {
            _logger.LogWarning(
                "llama-server: Ollama:Profiles:*:NumCtx игнорируется, контекст фиксируется ключом -c при старте сервера");
        }
    }

    // Свободнотекстовая генерация: единый prompt → строка ответа. Для фоновых one-shot
    // действий, которые сами разбирают ответ своими устойчивыми парсерами.
    // numCtx принимается в сигнатуре для совместимости с ILocalLlmClient, но в запрос
    // не уходит — см. комментарий к классу.
    public async Task<string?> GenerateTextAsync(
        string prompt, string? model, TimeSpan timeout, int numPredict, int numCtx,
        string? ownerId = null, string? label = null, CancellationToken ct = default)
    {
        _ = numCtx; // у llama-server контекст фиксируется ключом -c при старте
        var used = string.IsNullOrWhiteSpace(model) ? TextModel : model!;
        if (string.IsNullOrWhiteSpace(BaseUrl) || string.IsNullOrWhiteSpace(used)) return null;
        try
        {
            var client = _http.CreateClient(HttpClientName);
            client.Timeout = timeout;

            using var resp = await client.PostAsJsonAsync($"{BaseUrl}/v1/chat/completions",
                BuildRequestBody(used, BuildMessages(null, prompt), numPredict, jsonFormat: null), ct);

            if (!resp.IsSuccessStatusCode)
            {
                _logger.LogDebug("llama-server /v1/chat/completions (text) вернул {Status}", resp.StatusCode);
                return null;
            }

            var json = await resp.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ct);
            var content = ExtractContent(json);
            if (string.IsNullOrWhiteSpace(content)) return null;
            content = ThinkingStripper.Strip(content);
            RecordSpend(used, json, ownerId, label);
            return content;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "llama-server (text) недоступен ({BaseUrl}), фолбэк на claude", BaseUrl);
            return null;
        }
    }

    // Один синхронный чат-ход со структурированным JSON-выводом. jsonFormat:
    // - строка "json" → response_format {type:"json_object"} (просто JSON-object),
    // - иначе трактуем как JSON-схему → response_format {type:"json_schema", …}.
    //
    // schema-json в llama-server: {type:"json_schema", json_schema:{name, schema, strict:true}}.
    // Возвращает null при любой ошибке/таймауте — вызывающий откатывается дальше по цепочке.
    public async Task<string?> ChatJsonAsync(
        string systemPrompt, string userPrompt, object jsonFormat, CancellationToken ct = default,
        string? model = null, int? timeoutMs = null, int? numPredict = null, int? numCtx = null,
        string? ownerId = null, string? label = null)
    {
        _ = numCtx; // у llama-server контекст фиксируется ключом -c при старте
        var used = string.IsNullOrWhiteSpace(model) ? Model : model!;
        if (string.IsNullOrWhiteSpace(BaseUrl) || string.IsNullOrWhiteSpace(used)) return null;
        try
        {
            var client = _http.CreateClient(HttpClientName);
            client.Timeout = TimeSpan.FromMilliseconds(timeoutMs ?? TimeoutMs);

            // jsonFormat — либо полноценная JSON-схема (объект с type/properties/…), либо
            // строка "json" (просто JSON-object). Схему заворачиваем в json_schema-обёртку.
            object? responseFormat = null;
            // Грамматика строится только у json_schema — от этого зависит reasoning_format
            // (см. BuildRequestBody: с грамматикой ключ роняет запрос в 400).
            var schemaGrammar = false;
            if (jsonFormat is string s && s == "json")
                responseFormat = new { type = "json_object" };
            else if (jsonFormat is not null)
            {
                responseFormat = new { type = "json_schema", json_schema = new { name = "structured", schema = jsonFormat, strict = true } };
                schemaGrammar = true;
            }

            using var resp = await client.PostAsJsonAsync($"{BaseUrl}/v1/chat/completions",
                BuildRequestBody(used, BuildMessages(systemPrompt, userPrompt), numPredict ?? 120, responseFormat, schemaGrammar), ct);

            if (!resp.IsSuccessStatusCode)
            {
                _logger.LogDebug("llama-server /v1/chat/completions вернул {Status}", resp.StatusCode);
                return null;
            }

            var json = await resp.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ct);
            var answer = ExtractContent(json);
            if (string.IsNullOrWhiteSpace(answer)) return null;
            answer = ThinkingStripper.Strip(answer);
            if (!string.IsNullOrEmpty(answer)) RecordSpend(used, json, ownerId, label);
            return answer;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "llama-server недоступен ({BaseUrl}), фолбэк на claude", BaseUrl);
            return null;
        }
    }

    // Один разговорный ход голосового режима (Session.VoiceMode + место chat-voice на
    // «Локальная»): полный messages[] → короткий ответ. Без fallback на claude CLI: тихий
    // 15-секундный старт подпроцесса в разговоре хуже видимой ошибки в ленте.
    //
    // onDelta != null — потоковый режим (SSE, content-type: text/event-stream): куски
    // текста уходят вызывающему по границе предложения через общий StreamSentenceBuffer,
    // озвучка стартует, не дожидаясь конца ответа. Без onDelta поведение прежнее: один
    // ответ целиком.
    public async Task<ChatTurnResult> ChatTurnAsync(
        IReadOnlyList<ChatMsg> messages, string? model, TimeSpan timeout,
        int numPredict, int numCtx, string? ownerId,
        Func<string, Task>? onDelta = null, CancellationToken ct = default)
    {
        _ = numCtx; // у llama-server контекст фиксируется ключом -c при старте
        var used = string.IsNullOrWhiteSpace(model) ? TextModel : model!;
        if (string.IsNullOrWhiteSpace(BaseUrl) || string.IsNullOrWhiteSpace(used))
            return new ChatTurnResult(null, null);
        var streaming = onDelta is not null;
        try
        {
            var client = _http.CreateClient(HttpClientName);
            // В потоковом режиме HttpClient.Timeout покрывает только заголовки ответа —
            // общий потолок хода держит связанный CTS ниже (иначе стрим висел бы вечно)
            client.Timeout = streaming ? Timeout.InfiniteTimeSpan : timeout;
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            if (streaming) cts.CancelAfter(timeout);
            var token = streaming ? cts.Token : ct;

            // Собираем тело запроса руками: stream + stream_options нужны вместе,
            // BuildRequestBody их не включает (он для не-потоковых методов).
            var body = new Dictionary<string, object?>
            {
                ["model"] = used,
                ["messages"] = messages.Select(m => new { role = m.Role, content = m.Content }).ToArray(),
                ["max_tokens"] = numPredict,
                ["temperature"] = 0.7,
            };
            if (_options.DisableThinking)
            {
                body["chat_template_kwargs"] = new { enable_thinking = false };
                // Здесь reasoning_format безопасен: разговорный ход идёт без response_format,
                // а конфликт с грамматикой возникает только в паре со схемой (см. BuildRequestBody).
                body["reasoning_format"] = "none";
            }
            if (streaming)
            {
                body["stream"] = true;
                body["stream_options"] = new { include_usage = true };
            }

            using var req = new HttpRequestMessage(HttpMethod.Post, $"{BaseUrl}/v1/chat/completions")
            {
                Content = JsonContent.Create(body),
            };
            // llama-server с --api-key: типичный запуск без TLS, поэтому Bearer как для
            // OpenAI-совместимых источников. Заголовок шлём через DefaultRequestHeaders,
            // потому что HttpRequestMessage пересоздаётся фабрикой.
            if (!string.IsNullOrWhiteSpace(_options.ApiKey))
            {
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.ApiKey);
            }

            using var resp = await client.SendAsync(req,
                streaming ? HttpCompletionOption.ResponseHeadersRead : HttpCompletionOption.ResponseContentRead,
                token);

            if (!resp.IsSuccessStatusCode)
            {
                _logger.LogDebug("llama-server /v1/chat/completions (voice) вернул {Status}", resp.StatusCode);
                return new ChatTurnResult(null, null);
            }

            if (streaming)
                return await ReadChatStreamAsync(resp, used, ownerId, onDelta!, ct, token);

            var json = await resp.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: token);
            var answer = ExtractContent(json);
            if (string.IsNullOrWhiteSpace(answer)) return new ChatTurnResult(null, null);
            answer = ThinkingStripper.Strip(answer);
            RecordSpend(used, json, ownerId, "voice-turn");
            return new ChatTurnResult(answer, ReadUsage(json));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // «Стоп» пользователя / отмена хода — не ошибка, состояние закроет exited ветки
            throw;
        }
        catch (OperationCanceledException ex)
        {
            _logger.LogDebug(ex, "llama-server (voice) не уложился в {Timeout}", timeout);
            return new ChatTurnResult(null, null);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "llama-server (voice) недоступен ({BaseUrl})", BaseUrl);
            return new ChatTurnResult(null, null);
        }
    }

    // Чтение SSE-потока: события вида «data: {…}\n\n», терминатор «data: [DONE]».
    // Текст — choices[0].delta.content; usage — финальный чанк с пустым choices и
    // usage.prompt_tokens/completion_tokens (нужен stream_options.include_usage=true).
    // hardCt — отмена пользователя («Стоп»), её пробрасываем наружу; token — она же
    // плюс потолок времени: по нему отдаём уже накопленный текст, а не теряем ход целиком.
    private async Task<ChatTurnResult> ReadChatStreamAsync(
        HttpResponseMessage resp, string model, string? ownerId,
        Func<string, Task> onDelta, CancellationToken hardCt, CancellationToken token)
    {
        var buffer = new StreamSentenceBuffer();
        Protocol.UsageInfo? usage = null;
        var rawFull = new StringBuilder();

        async Task FlushAsync()
        {
            var chunk = buffer.Flush();
            if (chunk.Length > 0) await onDelta(chunk);
        }

        try
        {
            await using var stream = await resp.Content.ReadAsStreamAsync(token);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            string? line;
            while ((line = await reader.ReadLineAsync(token)) is not null)
            {
                if (string.IsNullOrEmpty(line)) continue;
                if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;
                var payload = line["data:".Length..].TrimStart();
                if (payload == "[DONE]") break;

                using var doc = SafeParse(payload);
                if (doc is null) continue;
                var root = doc.RootElement;

                if (root.TryGetProperty("choices", out var choices)
                    && choices.ValueKind == JsonValueKind.Array
                    && choices.GetArrayLength() > 0)
                {
                    var first = choices[0];
                    if (first.TryGetProperty("delta", out var delta)
                        && delta.TryGetProperty("content", out var c)
                        && c.ValueKind == JsonValueKind.String
                        && c.GetString() is { Length: > 0 } piece)
                    {
                        rawFull.Append(piece);
                        if (buffer.Append(piece)) await FlushAsync();
                    }
                }

                // usage приходит в финальном чанке с пустым choices; достаём его там же.
                if (root.TryGetProperty("usage", out var u) && u.ValueKind == JsonValueKind.Object)
                    usage = ReadUsageFromOpenAi(u);
            }

            RecordSpend(model, BuildUsageJsonFromOpenAi(usage), ownerId, "voice-turn");
        }
        catch (OperationCanceledException) when (hardCt.IsCancellationRequested)
        {
            throw; // «Стоп»: накопленное не отдаём, ход отменён целиком
        }
        catch (OperationCanceledException ex)
        {
            _logger.LogDebug(ex, "llama-server (voice): поток оборван по таймауту, отдаём накопленное");
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "llama-server (voice): сбой чтения потока, отдаём накопленное");
        }

        await FlushAsync();
        var text = ThinkingStripper.Strip(buffer.FullText);
        return new ChatTurnResult(string.IsNullOrWhiteSpace(text) ? null : text, usage);
    }

    // Битый чанк потока пропускаем молча: терять из-за него весь ход незачем
    private static JsonDocument? SafeParse(string line)
    {
        try { return JsonDocument.Parse(line); }
        catch (JsonException) { return null; }
    }

    // Тело запроса для не-потоковых методов (GenerateTextAsync/ChatJsonAsync).
    // DisableThinking прокидывается через chat_template_kwargs.enable_thinking; страховка
    // от забывчивого движка — ThinkingStripper на выходе.
    //
    // reasoning_format:"none" НЕ шлём вместе с ГРАММАТИКОЙ json_schema: на живом
    // llama-server (b10666, Qwen3 14B) эта пара роняет запрос ещё до генерации —
    // 400 «Failed to initialize samplers: Unexpected empty grammar stack after accepting
    // piece: <think>». Грамматика схемы не допускает токен <think>, который модель всё
    // равно эмитит при reasoning_format:none. Без этого ключа схема работает и отдаёт
    // чистый JSON — enable_thinking:false справляется сам (проверено 29.08).
    // json_object грамматики не строит и с reasoning_format уживается — там ключ нужен,
    // иначе ответ приезжает с <think> и разбирается только страховкой ThinkingStripper.
    private Dictionary<string, object?> BuildRequestBody(
        string used, object[] messages, int maxTokens, object? jsonFormat,
        bool schemaGrammar = false)
    {
        var body = new Dictionary<string, object?>
        {
            ["model"] = used,
            ["messages"] = messages,
            ["max_tokens"] = maxTokens,
            ["temperature"] = 0,
        };
        if (jsonFormat is not null) body["response_format"] = jsonFormat;
        if (_options.DisableThinking)
        {
            body["chat_template_kwargs"] = new { enable_thinking = false };
            // Только когда грамматики схемы нет — см. комментарий выше.
            if (!schemaGrammar) body["reasoning_format"] = "none";
        }
        return body;
    }

    private static object[] BuildMessages(string? systemPrompt, string userPrompt)
    {
        if (string.IsNullOrWhiteSpace(systemPrompt))
            return [new { role = "user", content = userPrompt }];
        return
        [
            new { role = "system", content = systemPrompt },
            new { role = "user", content = userPrompt },
        ];
    }

    // Достать текст ответа: choices[0].message.content.
    private static string? ExtractContent(JsonElement json) =>
        json.TryGetProperty("choices", out var choices)
        && choices.ValueKind == JsonValueKind.Array
        && choices.GetArrayLength() > 0
        && choices[0].TryGetProperty("message", out var msg)
        && msg.TryGetProperty("content", out var c)
            ? c.GetString()
            : null;

    // usage ответа OpenAI-диалекта: prompt_tokens / completion_tokens.
    private static Protocol.UsageInfo? ReadUsage(JsonElement json) =>
        json.TryGetProperty("usage", out var u) && u.ValueKind == JsonValueKind.Object
            ? ReadUsageFromOpenAi(u) : null;

    private static Protocol.UsageInfo? ReadUsageFromOpenAi(JsonElement u) =>
        u.TryGetProperty("prompt_tokens", out var p) && p.ValueKind == JsonValueKind.Number
        && u.TryGetProperty("completion_tokens", out var e) && e.ValueKind == JsonValueKind.Number
            ? new Protocol.UsageInfo(p.GetInt32(), e.GetInt32(), 0, 0)
            : null;

    // Учёт локального вызова: токены из usage, стоимость 0 (free-источник).
    // ProviderKey="llama-server" — SpendSources.IsFree примет эту строку по новой
    // записи в SpendRecord. usage в OpenAI-диалекте — вложенный объект, ищем сначала
    // его: prompt_tokens/completion_tokens живут внутри, а не на верхнем уровне.
    private void RecordSpend(string model, JsonElement json, string? ownerId, string? label)
    {
        if (_spend is null) return;
        try
        {
            var usage = json.TryGetProperty("usage", out var u) && u.ValueKind == JsonValueKind.Object
                ? u : default;
            _spend.Record(new Models.SpendRecord
            {
                OwnerId = ownerId ?? "",
                Provider = ProviderKey,
                Model = model,
                Source = Models.SpendSources.Free,
                Label = label,
                InputTokens = usage.ValueKind == JsonValueKind.Object
                    && usage.TryGetProperty("prompt_tokens", out var p)
                    && p.ValueKind == JsonValueKind.Number ? p.GetInt64() : 0,
                OutputTokens = usage.ValueKind == JsonValueKind.Object
                    && usage.TryGetProperty("completion_tokens", out var e)
                    && e.ValueKind == JsonValueKind.Number ? e.GetInt64() : 0,
                CostUsd = 0,
            });
        }
        catch { /* аналитика не должна ронять вызов */ }
    }

    // Обёртка usage-only JsonElement для учёта в стриме: у потокового варианта usage
    // приходит в финальном чанке без content, а RecordSpend ждёт json-форму. Собираем
    // минимальный объект {prompt_tokens, completion_tokens} на лету.
    private static JsonElement BuildUsageJsonFromOpenAi(Protocol.UsageInfo? usage)
    {
        using var ms = new MemoryStream();
        using (var writer = new Utf8JsonWriter(ms))
        {
            writer.WriteStartObject();
            if (usage is not null)
            {
                writer.WriteNumber("prompt_tokens", usage.InputTokens);
                writer.WriteNumber("completion_tokens", usage.OutputTokens);
            }
            writer.WriteEndObject();
        }
        ms.Position = 0;
        return JsonDocument.Parse(ms.ToArray()).RootElement.Clone();
    }

    // Прогрев: холостой вызов с max_tokens:1, чтобы модель загрузилась в память.
    // Best-effort — ошибки глушим.
    public async Task WarmUpAsync(CancellationToken ct = default)
    {
        if (!Enabled) return;
        try
        {
            var client = _http.CreateClient(HttpClientName);
            client.Timeout = TimeSpan.FromSeconds(90);
            await client.PostAsJsonAsync($"{BaseUrl}/v1/chat/completions", new
            {
                model = Model,
                messages = new[] { new { role = "user", content = "ok" } },
                max_tokens = 1,
            }, ct);
            _logger.LogInformation("llama-server прогрет: модель {Model}", Model);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Прогрев llama-server не удался (не критично)");
        }
    }
}
