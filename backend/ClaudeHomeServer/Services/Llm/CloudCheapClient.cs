using System.Text.Json;

namespace ClaudeHomeServer.Services.Llm;

// Прямой HTTP-адаптер к OpenAI-совместимому эндпоинту агрегатора (OpenRouter) для
// БЕСПЛАТНОГО выполнения фоновых one-shot действий на моделях ":free". Второй транспорт
// рядом с провайдером (CLI): тот же OpenRouter, но вызов идёт напрямую (POST /chat/completions),
// а не через claude CLI — старт CLI ~15с на вызов убил бы смысл «быстро и часто», прямой
// HTTP отвечает за ~3.5с. Ответ разбирают те же парсеры потребителей, что и ответ claude.
//
// Конкретную модель выбирает админ в пикере фоновых действий (приходит в GenerateTextAsync);
// список доступных моделей — курируемый OpenRouter:DirectModels (см. ModelCatalogService).
// Эндпоинт и ключ — из настроенного CLI-провайдера (LlmProviders:{Provider}), здесь не дублируются.
// Провайдер не настроен → Enabled=false (маршрут молча уходит на локаль/claude).
public sealed class CloudCheapClient
{
    // Префикс id модели в маршруте действия, помечающий прямой транспорт: "direct:<modelId>".
    // Так один и тот же OpenRouter различается в сторе и пикере — модель без префикса идёт
    // через провайдер (claude CLI), с префиксом — через этот адаптер (прямой HTTP).
    public const string RoutePrefix = "direct:";

    // Виртуальный ключ провайдера прямого адаптера для группировки в каталоге/пикере
    public const string DirectProviderKey = "openrouter-direct";

    public static bool IsDirectRoute(string? route) =>
        route is not null && route.StartsWith(RoutePrefix, StringComparison.Ordinal);

    public static string StripPrefix(string route) =>
        route.StartsWith(RoutePrefix, StringComparison.Ordinal) ? route[RoutePrefix.Length..] : route;

    private readonly IHttpClientFactory _http;
    private readonly ILogger<CloudCheapClient> _logger;
    private readonly LlmProviderConfigView _provider;

    // Ключ провайдера-источника эндпоинта/ключа (дефолт openrouter)
    public string ProviderKey { get; }

    public bool Enabled => _provider.Configured;

    // Адрес эндпоинта для UI использования (без ключа)
    public string? BaseUrl => _provider.Configured ? _provider.ApiBaseUrl : null;

    public CloudCheapClient(IHttpClientFactory http, IConfiguration config,
        LlmProviderRegistry providers, ILogger<CloudCheapClient> logger)
    {
        _http = http;
        _logger = logger;
        ProviderKey = config["OpenRouter:Provider"] is { Length: > 0 } p ? p : "openrouter";

        var cfg = providers.GetByKey(ProviderKey);
        _provider = new LlmProviderConfigView(
            Configured: cfg is { Enabled: true } && !string.IsNullOrWhiteSpace(cfg.ApiBaseUrl),
            ApiBaseUrl: cfg?.ApiBaseUrl?.TrimEnd('/') ?? "",
            ApiKey: cfg?.ApiKey ?? "");
    }

    private sealed record LlmProviderConfigView(bool Configured, string ApiBaseUrl, string ApiKey);

    // Свободнотекстовая генерация выбранной моделью (контракт совпадает с
    // OllamaClient.GenerateTextAsync). maxTokens — лимит вывода профиля. Возвращает null при
    // любой ошибке/таймауте/пустом ответе — вызывающий откатывается на следующий маршрут.
    public async Task<string?> GenerateTextAsync(
        string model, string prompt, TimeSpan timeout, int maxTokens, CancellationToken ct = default)
    {
        if (!_provider.Configured || string.IsNullOrWhiteSpace(model)) return null;
        try
        {
            var client = _http.CreateClient("llm-provider");
            client.Timeout = timeout;

            using var req = new HttpRequestMessage(HttpMethod.Post, $"{_provider.ApiBaseUrl}/chat/completions")
            {
                Content = JsonContent.Create(new
                {
                    model,
                    stream = false,
                    temperature = 0,
                    max_tokens = maxTokens,
                    messages = new[] { new { role = "user", content = prompt } },
                }),
            };
            req.Headers.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _provider.ApiKey);

            using var resp = await client.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode)
            {
                // 429 — исчерпан суточный/минутный лимит бесплатных моделей: штатный сценарий,
                // не шумим ошибкой, вызывающий уходит на следующий маршрут
                _logger.LogDebug("{Provider} /chat/completions вернул {Status} для {Model}",
                    ProviderKey, resp.StatusCode, model);
                return null;
            }

            var json = await resp.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ct);
            // Рассуждающие модели кладут ход мысли в message.reasoning отдельно от content —
            // берём именно content, парсеры потребителей ждут чистый ответ
            var content = json.TryGetProperty("choices", out var choices)
                && choices.ValueKind == JsonValueKind.Array && choices.GetArrayLength() > 0
                && choices[0].TryGetProperty("message", out var msg)
                && msg.TryGetProperty("content", out var c)
                    ? c.GetString()
                    : null;
            return string.IsNullOrWhiteSpace(content) ? null : content;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "{Provider} недоступен, фолбэк на следующий маршрут", ProviderKey);
            return null;
        }
    }
}
