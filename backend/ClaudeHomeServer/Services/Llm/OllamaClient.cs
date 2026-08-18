using System.Text.Json;
using ClaudeHomeServer.Protocol;

namespace ClaudeHomeServer.Services.Llm;

// Тонкая обёртка над локальным Ollama (POST /api/chat). Используется AI-хабом для
// БЕСПЛАТНОГО ранжирования действий по контексту (см. OllamaActionRankService).
// Прямой HTTP мимо claude CLI: Ollama не Anthropic-совместим, а старт CLI (~15с)
// убил бы смысл «быстро и часто». Без непустых BaseUrl/Model — Enabled=false
// (фича молча уходит в rule-based фолбэк).
public sealed class OllamaClient
{
    // Именованный клиент, а не безымянный: под этим именем в Program.cs зарегистрирован
    // тихий логгер (Services/Http/QuietHttpLogger). Иначе непогашенная Ollama печатала
    // портянку Error со стектрейсом на каждый вызов — при том что здесь недоступность
    // штатно ловится и уходит в Debug с фолбэком.
    public const string HttpClientName = "ollama";

    private readonly IHttpClientFactory _http;
    private readonly ILogger<OllamaClient> _logger;
    // Сбор расхода бесплатных вызовов (null — в тестах: аналитика выключена)
    private readonly Spend.ISpendCollector? _spend;

    public string BaseUrl { get; }
    public string Model { get; }
    // Отдельная модель для текстовых действий (Ollama:TextModel); пусто → та же Model.
    // Позволяет держать одну модель на ранжир и на генерацию, либо развести при желании.
    public string TextModel { get; }
    // keep_alive для Ollama: число (секунды; -1 = держать вечно) ЛИБО duration-строка ("5m").
    // Строку "-1" API отвергает ("missing unit in duration") — поэтому целое отдаём числом.
    private readonly object _keepAlive;
    public int TimeoutMs { get; }

    public bool Enabled => !string.IsNullOrWhiteSpace(BaseUrl) && !string.IsNullOrWhiteSpace(Model);

    public OllamaClient(IHttpClientFactory http, IConfiguration config, ILogger<OllamaClient> logger,
        Spend.ISpendCollector? spend = null)
    {
        _http = http;
        _logger = logger;
        _spend = spend;
        BaseUrl = (config["Ollama:BaseUrl"] ?? "http://localhost:11434").TrimEnd('/');
        Model = config["Ollama:Model"] ?? "";
        TextModel = config["Ollama:TextModel"] is { Length: > 0 } tm ? tm : Model;
        var keepAlive = config["Ollama:KeepAlive"] ?? "-1"; // держим модель в памяти между вызовами
        _keepAlive = int.TryParse(keepAlive, out var ka) ? ka : keepAlive;
        TimeoutMs = int.TryParse(config["Ollama:TimeoutMs"], out var t) ? t : 4000;
    }

    // Один синхронный чат-ход со структурированным JSON-выводом. Возвращает строку
    // message.content (валидный JSON по schema) либо null при любой ошибке/таймауте.
    // think:false обязателен — иначе qwen3 тратит вывод на размышления и тупит.
    //
    // Параметры профиля (model/timeoutMs/numPredict/numCtx) опциональны: без них работает
    // прежнее поведение ранжира AI-хаба. numCtx особенно важен для длинных промптов —
    // дефолт Ollama (~4k) МОЛЧА срезает хвост входа, и модель отвечает по обрубку.
    public async Task<string?> ChatJsonAsync(
        string systemPrompt, string userPrompt, object formatSchema, CancellationToken ct = default,
        string? model = null, int? timeoutMs = null, int? numPredict = null, int? numCtx = null,
        string? ownerId = null, string? label = null)
    {
        var used = string.IsNullOrWhiteSpace(model) ? Model : model!;
        if (string.IsNullOrWhiteSpace(BaseUrl) || string.IsNullOrWhiteSpace(used)) return null;
        try
        {
            var client = _http.CreateClient(HttpClientName);
            client.Timeout = TimeSpan.FromMilliseconds(timeoutMs ?? TimeoutMs);

            // Пустой system не шлём: часть моделей на пустой роли ведёт себя хуже, чем без него.
            var messages = string.IsNullOrWhiteSpace(systemPrompt)
                ? new[] { new { role = "user", content = userPrompt } }
                : new[] { new { role = "system", content = systemPrompt },
                          new { role = "user", content = userPrompt } };

            using var resp = await client.PostAsJsonAsync($"{BaseUrl}/api/chat", new
            {
                model = used,
                stream = false,
                think = false,
                keep_alive = _keepAlive,
                format = formatSchema,
                options = numCtx is { } nc
                    ? new { temperature = 0, num_predict = numPredict ?? 120, num_ctx = nc }
                    : (object)new { temperature = 0, num_predict = numPredict ?? 120 },
                messages,
            }, ct);

            if (!resp.IsSuccessStatusCode)
            {
                _logger.LogDebug("Ollama /api/chat вернул {Status}", resp.StatusCode);
                return null;
            }

            var json = await resp.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ct);
            var answer = json.TryGetProperty("message", out var msg) && msg.TryGetProperty("content", out var content)
                ? content.GetString()
                : null;
            if (!string.IsNullOrEmpty(answer)) RecordSpend(used, json, ownerId, label);
            return answer;
        }
        catch (Exception ex)
        {
            // Недоступность/таймаут — штатный сценарий (фолбэк на правила), не шумим ошибкой
            _logger.LogDebug(ex, "Ollama недоступен ({BaseUrl}), фолбэк на правила", BaseUrl);
            return null;
        }
    }

    // Расход локального вызова в аналитику: токены из счётчиков ответа Ollama
    // (prompt_eval_count/eval_count), стоимость 0 — источник free. Владелец и подпись
    // действия приходят от вызывающего (CheapTextRunner); без них запись системная.
    // Ошибка записи вызов не роняет.
    private void RecordSpend(string model, JsonElement json, string? ownerId, string? label)
    {
        if (_spend is null) return;
        try
        {
            _spend.Record(new Models.SpendRecord
            {
                OwnerId = ownerId ?? "",
                Provider = "ollama",
                Model = model,
                Source = Models.SpendSources.Free,
                Label = label,
                InputTokens = json.TryGetProperty("prompt_eval_count", out var p)
                    && p.ValueKind == JsonValueKind.Number ? p.GetInt64() : 0,
                OutputTokens = json.TryGetProperty("eval_count", out var e)
                    && e.ValueKind == JsonValueKind.Number ? e.GetInt64() : 0,
                CostUsd = 0,
            });
        }
        catch { /* аналитика не должна ронять вызов */ }
    }

    // Свободнотекстовая генерация (без format-schema): единый prompt → текст ответа.
    // Для фоновых one-shot действий, которые сами разбирают ответ модели своими устойчивыми
    // парсерами (как раньше разбирали ответ claude --print). think:false — иначе qwen3 тратит
    // вывод на размышления. numCtx задаётся явно: дефолт Ollama (~4k) молча режет большой вход.
    // Возвращает null при любой ошибке/таймауте/пустом ответе — вызывающий откатывается на claude.
    public async Task<string?> GenerateTextAsync(
        string prompt, string? model, TimeSpan timeout, int numPredict, int numCtx,
        string? ownerId = null, string? label = null, CancellationToken ct = default)
    {
        var used = string.IsNullOrWhiteSpace(model) ? TextModel : model!;
        if (string.IsNullOrWhiteSpace(BaseUrl) || string.IsNullOrWhiteSpace(used)) return null;
        try
        {
            var client = _http.CreateClient(HttpClientName);
            client.Timeout = timeout;

            using var resp = await client.PostAsJsonAsync($"{BaseUrl}/api/chat", new
            {
                model = used,
                stream = false,
                think = false,
                keep_alive = _keepAlive,
                options = new { temperature = 0, num_predict = numPredict, num_ctx = numCtx },
                messages = new[] { new { role = "user", content = prompt } },
            }, ct);

            if (!resp.IsSuccessStatusCode)
            {
                _logger.LogDebug("Ollama /api/chat (text) вернул {Status}", resp.StatusCode);
                return null;
            }

            var json = await resp.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ct);
            var content = json.TryGetProperty("message", out var msg) && msg.TryGetProperty("content", out var c)
                ? c.GetString()
                : null;
            if (string.IsNullOrWhiteSpace(content)) return null;
            RecordSpend(used, json, ownerId, label);
            return content;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Ollama (text) недоступен ({BaseUrl}), фолбэк на claude", BaseUrl);
            return null;
        }
    }

    // Реплика диалога для метода ChatTurnAsync: role = system|user|assistant.
    public sealed record ChatMsg(string Role, string Content);

    // Результат разговорного хода: текст ответа (null — пустой/сбойный вызов) и usage
    // для ResultMessage ленты (токены из счётчиков Ollama; может быть null при их отсутствии).
    public sealed record ChatTurnResult(string? Text, Protocol.UsageInfo? Usage);

    // Один разговорный ход голосового режима (Session.VoiceMode + место chat-voice на
    // «Локальная»): полный messages[] (system + история + реплика) → короткий ответ.
    // Отличия от GenerateTextAsync: диалоговая история, temperature 0.7 (разговор, а не
    // классификация) и НЕ-null-протокол: вызов знает об отказе сам (фолбэка на claude нет —
    // тихий 15-секундный старт CLI в разговоре хуже видимой ошибки в ленте).
    public async Task<ChatTurnResult> ChatTurnAsync(
        IReadOnlyList<ChatMsg> messages, string? model, TimeSpan timeout,
        int numPredict, int numCtx, string? ownerId, CancellationToken ct = default)
    {
        var used = string.IsNullOrWhiteSpace(model) ? TextModel : model!;
        if (string.IsNullOrWhiteSpace(BaseUrl) || string.IsNullOrWhiteSpace(used))
            return new ChatTurnResult(null, null);
        try
        {
            var client = _http.CreateClient(HttpClientName);
            client.Timeout = timeout;

            using var resp = await client.PostAsJsonAsync($"{BaseUrl}/api/chat", new
            {
                model = used,
                stream = false,
                think = false,
                keep_alive = _keepAlive,
                options = new { temperature = 0.7, num_predict = numPredict, num_ctx = numCtx },
                messages = messages.Select(m => new { role = m.Role, content = m.Content }).ToArray(),
            }, ct);

            if (!resp.IsSuccessStatusCode)
            {
                _logger.LogDebug("Ollama /api/chat (voice) вернул {Status}", resp.StatusCode);
                return new ChatTurnResult(null, null);
            }

            var json = await resp.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ct);
            var answer = json.TryGetProperty("message", out var msg) && msg.TryGetProperty("content", out var c)
                ? c.GetString()
                : null;
            if (string.IsNullOrWhiteSpace(answer)) return new ChatTurnResult(null, null);
            RecordSpend(used, json, ownerId, "voice-turn");
            // Токены для result ленты: input = prompt_eval_count, output = eval_count.
            // Кеша у локального вызова нет — обе метрики нули.
            Protocol.UsageInfo? usage = null;
            if (json.TryGetProperty("prompt_eval_count", out var p) && p.ValueKind == JsonValueKind.Number
                && json.TryGetProperty("eval_count", out var e) && e.ValueKind == JsonValueKind.Number)
                usage = new Protocol.UsageInfo(p.GetInt32(), e.GetInt32(), 0, 0);
            return new ChatTurnResult(answer, usage);
        }
        catch (OperationCanceledException)
        {
            // «Стоп» пользователя / отмена хода — не ошибка, состояние закроет exited ветки
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Ollama (voice) недоступен ({BaseUrl})", BaseUrl);
            return new ChatTurnResult(null, null);
        }
    }

    // Прогрев: холостой вызов, чтобы модель загрузилась в память заранее (keep_alive из конфига).
    // Best-effort — ошибки глушим.
    public async Task WarmUpAsync(CancellationToken ct = default)
    {
        if (!Enabled) return;
        try
        {
            var client = _http.CreateClient(HttpClientName);
            client.Timeout = TimeSpan.FromSeconds(90); // холодный старт грузит веса
            await client.PostAsJsonAsync($"{BaseUrl}/api/chat", new
            {
                model = Model,
                stream = false,
                think = false,
                keep_alive = _keepAlive,
                options = new { num_predict = 1 },
                messages = new[] { new { role = "user", content = "ok" } },
            }, ct);
            _logger.LogInformation("Ollama прогрет: модель {Model}", Model);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Прогрев Ollama не удался (не критично)");
        }
    }
}
