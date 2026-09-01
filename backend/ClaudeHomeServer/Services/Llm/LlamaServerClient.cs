using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace ClaudeHomeServer.Services.Llm;

// Локальный движок llama-server (https://github.com/ggerganov/llama.cpp/tree/master/examples/server).
// Раскладка «один порт — много моделей»: один процесс llama-server слушает HTTP и
// держит загруженные веса в памяти, поле model в запросе выбирает, какие именно.
//
// Диалект — OpenAI-совместимый /v1/chat/completions. Поведенческий контракт совпадает
// с OllamaClient: null/пусто при любой беде → вызывающий идёт дальше по цепочке; логи
// в Debug, не Error; регистрация через AddQuietHttpClient (Program.cs).
//
// numCtx в запрос НЕ передаётся (контекст фиксируется ключом -c при старте сервера),
// но ИСПОЛЬЗУЕТСЯ для обрезки длинного промпта по бюджету: иначе llama-server отвечает
// жёстким HTTP 400 «exceed_context_size_error», ход уходит в null → цепочка → платный
// claude. Ollama раньше обрезала хвост молча (num_ctx ехал в запрос); здесь —
// эквивалент руками до отправки. Факт обрезки логируется на уровне Information.
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
        if (Enabled
            && (config["Ollama:Profiles:small:NumCtx"] is not null
                || config["Ollama:Profiles:text:NumCtx"] is not null
                || config["Ollama:Profiles:large:NumCtx"] is not null))
        {
            _logger.LogWarning(
                "llama-server: Ollama:Profiles:*:NumCtx игнорируется, контекст фиксируется ключом -c при старте сервера");
        }
    }

    // Свободнотекстовая генерация: единый prompt → строка ответа. Для фоновых one-shot
    // действий, которые сами разбирают ответ своими устойчивыми парсерами.
    // numCtx используется для обрезки промпта под контекст llama-server (см. комментарий
    // к классу); в самом запросе не передаётся.
    // Оценка «символ → токены» для обрезки промпта под контекст: 3 символа ≈ 1 токен.
    // Замер на живом токенизаторе qwen35-9b (vocab 248320):
    //   кириллица 3.25–3.51 симв/токен, английский ~5.5, код C# ~4.2.
    // Все фоновые действия у нас русские, и при «средних» 4 симв/токен кириллический
    // промпт длиной в бюджет (numCtx - numPredict) × 4 всё равно лезет за numCtx →
    // llama-server отвечает 400 «exceed_context_size_error», ход уходит в цепочку на
    // платный claude. 3 держит запас на чистой кириллице; недобор на английском
    // дешевле отказа — обрезка и так срабатывает только на переполнении.
    // Точнее — через /tokenize llama-server, но +HTTP на каждый ход ради редкого случая
    // не оправдан.
    private const int CharsPerToken = 3;
    // Запас на chat template (роли + служебные теги): у распространённых шаблонов
    // (chatml/mistral/llama3) это 50–100 символов, берём с двойным запасом.
    private const int TemplateReserveChars = 256;

    // Бюджет символов под вход: (numCtx - numPredict) токенов под промпт, минус запас
    // на шаблон. numCtx<=0 — обрезка не заказана (вызывающий не знает контекст).
    private static int InputBudgetChars(int numCtx, int numPredict) =>
        numCtx > numPredict
            ? Math.Max(0, (numCtx - numPredict) * CharsPerToken - TemplateReserveChars)
            : 0;

    // Обрезать строку с хвоста до бюджета. Возвращает (текст, originalLen, newLen, truncated).
    private static string TrimToBudget(string text, int budget, out int originalLen, out int newLen)
    {
        originalLen = text.Length;
        if (budget <= 0 || text.Length <= budget)
        {
            newLen = text.Length;
            return text;
        }
        newLen = budget;
        return text[..budget];
    }

    // Обрезать массив сообщений до суммарного бюджета символов, сохраняя system (голову)
    // и ПОСЛЕДНЕЕ сообщение (текущая реплика пользователя в голосовом разговоре) —
    // середину выбрасываем целиком, при нехватке укорачиваем с хвоста только последнее.
    // Для голосового хода это критично: при обрезке «с головы» текущая реплика
    // превращалась в пустую строку, а старая история доезжала целиком — модель отвечала
    // на позапрошлый вопрос, и со стороны пользователя это выглядело как сбой без
    // диагностики.
    private static (IReadOnlyList<ChatMsg> messages, int origLen, int newLen) TrimMessagesToBudget(
        IReadOnlyList<ChatMsg> messages, int budget)
    {
        var origLen = 0;
        for (var i = 0; i < messages.Count; i++) origLen += messages[i].Content.Length;
        if (budget <= 0 || origLen <= budget || messages.Count == 0)
            return (messages, origLen, origLen);

        // Декомпозиция: system = messages[0] (если он реально system), хвост = messages[1..].
        var hasSystem = messages[0].Role == "system";
        var systemIdx = hasSystem ? 0 : -1;
        var tailStart = hasSystem ? 1 : 0;
        var tailCount = messages.Count - tailStart;

        var result = new ChatMsg[messages.Count];

        // Заголовок system режем с хвоста только в крайнем случае (не влезает даже
        // system+last): контракт ронять нельзя, поэтому берём сколько влезло.
        if (hasSystem)
        {
            var sysBudget = Math.Min(messages[0].Content.Length, budget);
            result[0] = new ChatMsg(messages[0].Role, messages[0].Content[..sysBudget]);
            budget -= sysBudget;
        }

        // Один хвостовой участник в разговоре — это и есть «текущая реплика».
        if (tailCount == 0) return (result, origLen, hasSystem ? result[0].Content.Length : 0);

        var lastIdx = messages.Count - 1;
        if (tailCount == 1)
        {
            // Только последнее сообщение — обрезаем его до остатка бюджета.
            var c = messages[lastIdx].Content;
            var keep = Math.Min(c.Length, Math.Max(0, budget));
            result[lastIdx] = new ChatMsg(messages[lastIdx].Role, c[..keep]);
            return (result, origLen, ComputeNewLen(result));
        }

        // Несколько сообщений: сначала резерв под последнее (оно должно дойти непустым),
        // затем середину наполняем от конца к началу, пока влезает.
        var lastContent = messages[lastIdx].Content;
        var lastKeep = Math.Min(lastContent.Length, Math.Max(0, budget));
        result[lastIdx] = new ChatMsg(messages[lastIdx].Role, lastContent[..lastKeep]);
        var middleBudget = Math.Max(0, budget - lastKeep);

        var used = 0;
        // Идём по messages[tailStart..lastIdx-1] от конца к началу, копим целиком, пока есть место.
        for (var i = lastIdx - 1; i >= tailStart; i--)
        {
            var len = messages[i].Content.Length;
            if (used + len > middleBudget) break;
            result[i] = messages[i];
            used += len;
        }
        // Остаток середины (старая история) — пустые сообщения с теми же ролями.
        for (var i = tailStart; i < lastIdx; i++)
        {
            if (result[i] is null)
                result[i] = new ChatMsg(messages[i].Role, "");
        }

        return (result, origLen, ComputeNewLen(result));
    }

    private static int ComputeNewLen(IReadOnlyList<ChatMsg> msgs)
    {
        var n = 0;
        for (var i = 0; i < msgs.Count; i++) n += msgs[i].Content.Length;
        return n;
    }

    public async Task<string?> GenerateTextAsync(
        string prompt, string? model, TimeSpan timeout, int numPredict, int numCtx,
        string? ownerId = null, string? label = null, CancellationToken ct = default)
    {
        var used = string.IsNullOrWhiteSpace(model) ? TextModel : model!;
        if (string.IsNullOrWhiteSpace(BaseUrl) || string.IsNullOrWhiteSpace(used)) return null;
        var budget = InputBudgetChars(numCtx, numPredict);
        var trimmed = TrimToBudget(prompt, budget, out var origLen, out var newLen);
        if (newLen < origLen)
            _logger.LogInformation(
                "llama-server (text): обрезка промпта под контекст {NumCtx}: {Orig}→{New} символов ({Label})",
                numCtx, origLen, newLen, label ?? "-");
        try
        {
            var client = _http.CreateClient(HttpClientName);
            client.Timeout = timeout;

            using var resp = await client.PostAsJsonAsync($"{BaseUrl}/v1/chat/completions",
                BuildRequestBody(used, BuildMessages(null, trimmed), numPredict, jsonFormat: null), ct);

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
        var used = string.IsNullOrWhiteSpace(model) ? Model : model!;
        if (string.IsNullOrWhiteSpace(BaseUrl) || string.IsNullOrWhiteSpace(used)) return null;
        var ctx = numCtx ?? 0;
        var pred = numPredict ?? 120;
        // Суммарный бюджет: при нехватке режем с хвоста user-промпта, системный не трогаем.
        var budget = InputBudgetChars(ctx, pred);
        var totalLen = (string.IsNullOrEmpty(systemPrompt) ? 0 : systemPrompt.Length) + userPrompt.Length;
        string trimmedSystem = systemPrompt;
        string trimmedUser = userPrompt;
        if (budget > 0 && totalLen > budget)
        {
            var sysLen = string.IsNullOrEmpty(systemPrompt) ? 0 : systemPrompt.Length;
            if (sysLen > budget)
            {
                // systemPrompt один длиннее бюджета: userBudget схлопнется в 0, user уйдёт
                // пустым, system всё равно переполнит контекст. У фоновых действий system
                // пустой, но если кто-то прислал — лучше увидеть в логе, чем гадать.
                _logger.LogWarning(
                    "llama-server (json): systemPrompt ({SysLen}) длиннее бюджета ({Budget}) для контекста {NumCtx}, метка {Label}",
                    sysLen, budget, ctx, label ?? "-");
            }
            var userBudget = Math.Max(0, budget - sysLen);
            // userBudget=0 здесь означает «явно обрезать до нуля» (systemPrompt один
            // длиннее бюджета). TrimToBudget трактует budget<=0 как «обрезка не
            // заказана» — здесь это другая семантика, делаем пустую строку руками.
            trimmedUser = userBudget == 0 ? "" : TrimToBudget(userPrompt, userBudget, out var _, out _);
            if (trimmedUser.Length < userPrompt.Length)
                _logger.LogInformation(
                    "llama-server (json): обрезка промпта под контекст {NumCtx}: {Orig}→{New} символов ({Label})",
                    ctx, totalLen, sysLen + trimmedUser.Length, label ?? "-");
        }
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
                BuildRequestBody(used, BuildMessages(trimmedSystem, trimmedUser), pred, responseFormat, schemaGrammar), ct);

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
        var used = string.IsNullOrWhiteSpace(model) ? TextModel : model!;
        if (string.IsNullOrWhiteSpace(BaseUrl) || string.IsNullOrWhiteSpace(used))
            return new ChatTurnResult(null, null);
        var budget = InputBudgetChars(numCtx, numPredict);
        var (trimmedMessages, origLen, newLen) = TrimMessagesToBudget(messages, budget);
        if (newLen < origLen)
            _logger.LogInformation(
                "llama-server (voice): обрезка промпта под контекст {NumCtx}: {Orig}→{New} символов (voice-turn)",
                numCtx, origLen, newLen);
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
                ["messages"] = trimmedMessages.Select(m => new { role = m.Role, content = m.Content }).ToArray(),
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
