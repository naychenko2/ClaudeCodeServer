using System.Text.Json;

namespace ClaudeHomeServer.Services.Llm;

// Тонкая обёртка над локальным Ollama (POST /api/chat). Используется AI-хабом для
// БЕСПЛАТНОГО ранжирования действий по контексту (см. OllamaActionRankService).
// Прямой HTTP мимо claude CLI: Ollama не Anthropic-совместим, а старт CLI (~15с)
// убил бы смысл «быстро и часто». Без непустых BaseUrl/Model — Enabled=false
// (фича молча уходит в rule-based фолбэк).
public sealed class OllamaClient
{
    private readonly IHttpClientFactory _http;
    private readonly ILogger<OllamaClient> _logger;

    public string BaseUrl { get; }
    public string Model { get; }
    // keep_alive для Ollama: число (секунды; -1 = держать вечно) ЛИБО duration-строка ("5m").
    // Строку "-1" API отвергает ("missing unit in duration") — поэтому целое отдаём числом.
    private readonly object _keepAlive;
    public int TimeoutMs { get; }

    public bool Enabled => !string.IsNullOrWhiteSpace(BaseUrl) && !string.IsNullOrWhiteSpace(Model);

    public OllamaClient(IHttpClientFactory http, IConfiguration config, ILogger<OllamaClient> logger)
    {
        _http = http;
        _logger = logger;
        BaseUrl = (config["Ollama:BaseUrl"] ?? "http://localhost:11434").TrimEnd('/');
        Model = config["Ollama:Model"] ?? "";
        var keepAlive = config["Ollama:KeepAlive"] ?? "-1"; // держим модель в памяти между вызовами
        _keepAlive = int.TryParse(keepAlive, out var ka) ? ka : keepAlive;
        TimeoutMs = int.TryParse(config["Ollama:TimeoutMs"], out var t) ? t : 4000;
    }

    // Один синхронный чат-ход со структурированным JSON-выводом. Возвращает строку
    // message.content (валидный JSON по schema) либо null при любой ошибке/таймауте.
    // think:false обязателен — иначе qwen3 тратит вывод на размышления и тупит.
    public async Task<string?> ChatJsonAsync(
        string systemPrompt, string userPrompt, object formatSchema, CancellationToken ct = default)
    {
        if (!Enabled) return null;
        try
        {
            var client = _http.CreateClient();
            client.Timeout = TimeSpan.FromMilliseconds(TimeoutMs);

            using var resp = await client.PostAsJsonAsync($"{BaseUrl}/api/chat", new
            {
                model = Model,
                stream = false,
                think = false,
                keep_alive = _keepAlive,
                format = formatSchema,
                options = new { temperature = 0, num_predict = 120 },
                messages = new[]
                {
                    new { role = "system", content = systemPrompt },
                    new { role = "user", content = userPrompt },
                },
            }, ct);

            if (!resp.IsSuccessStatusCode)
            {
                _logger.LogDebug("Ollama /api/chat вернул {Status}", resp.StatusCode);
                return null;
            }

            var json = await resp.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ct);
            return json.TryGetProperty("message", out var msg) && msg.TryGetProperty("content", out var content)
                ? content.GetString()
                : null;
        }
        catch (Exception ex)
        {
            // Недоступность/таймаут — штатный сценарий (фолбэк на правила), не шумим ошибкой
            _logger.LogDebug(ex, "Ollama недоступен ({BaseUrl}), фолбэк на правила", BaseUrl);
            return null;
        }
    }

    // Прогрев: холостой вызов, чтобы модель загрузилась в память заранее (keep_alive из конфига).
    // Best-effort — ошибки глушим.
    public async Task WarmUpAsync(CancellationToken ct = default)
    {
        if (!Enabled) return;
        try
        {
            var client = _http.CreateClient();
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
