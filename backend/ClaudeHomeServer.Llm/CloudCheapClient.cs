using System.Globalization;
using System.Text.Json;

namespace ClaudeHomeServer.Services.Llm;

// Прямой HTTP-адаптер к одному или нескольким OpenAI-совместимым источникам для фоновых
// one-shot действий. Источники задаются секцией CheapHttpSources:{key}:{ Provider, Models[] };
// пустая секция — фолбэк на legacy OpenRouter:Provider + OpenRouter:DirectModels. Provider —
// ключ из LlmProviders: и бесплатные (openrouter, freellmapi), и уже настроенные платные
// (deepseek/glm/kimi/minimax) — ApiKey тот же, что у чатового провайдера, выгода для платных —
// скорость (без старта CLI ~15с), а не деньги. Маршрут модели сохраняет префикс direct:<modelId>;
// источник внутри резолвится по id модели через курируемый список каждого источника. Коллизия id
// между источниками разрешается в пользу первого по порядку в конфиге.
public sealed class CloudCheapClient
{
    // Префикс id модели в маршруте действия, помечающий прямой транспорт: "direct:<modelId>".
    public const string RoutePrefix = "direct:";

    // Виртуальный ключ провайдера прямого адаптера для группировки в каталоге/пикере.
    // Legacy-источник openrouter сохраняет это имя для совместимости spend и пресетов.
    public const string DirectProviderKey = "openrouter-direct";

    public static bool IsDirectRoute(string? route) =>
        route is not null && route.StartsWith(RoutePrefix, StringComparison.Ordinal);

    public static string StripPrefix(string route) =>
        route.StartsWith(RoutePrefix, StringComparison.Ordinal) ? route[RoutePrefix.Length..] : route;

    private readonly IHttpClientFactory _http;
    private readonly ILogger<CloudCheapClient> _logger;
    private readonly List<Source> _sources = [];
    // Сбор расхода бесплатных вызовов (null — в тестах: аналитика выключена)
    private readonly Spend.ISpendCollector? _spend;

    // Ключ legacy-провайдера-источника (openrouter по умолчанию). Сохраняется для
    // обратной совместимости с кодом, который ожидал единственный источник.
    public string ProviderKey { get; }

    public bool Enabled => _sources.Any(s => s.Configured);

    // Адрес эндпоинта первого настроенного источника для UI использования (без ключа)
    public string? BaseUrl => _sources.FirstOrDefault(s => s.Configured)?.ApiBaseUrl;

    public IReadOnlyList<Source> Sources => _sources;

    // Ответ прямого адаптера: текст + флаг обрыва по лимиту вывода (см. GenerateDetailedAsync).
    // Text == null — шаг не удался (ошибка/429/таймаут/пусто), вызывающий идёт дальше по цепочке.
    public readonly record struct CloudTextResult(string? Text, bool Truncated);

    // Temperature источника для тела /chat/completions. Дефолт 0 — детерминированность
    // фоновых one-shot действий (теги, сводки, JSON-парсинг). Per-source override нужен для
    // провайдеров, не принимающих 0: kimi на всех моделях каталога требует ровно 1 и падает
    // 400 «invalid temperature: only 1 is allowed» (прод 2026-08-12). Берётся из
    // CheapHttpSources:{key}:Temperature.
    public record Source(
        string Key, string ProviderKey, string ApiBaseUrl, string ApiKey,
        IReadOnlyList<string> Models, double Temperature = 0)
    {
        public bool Configured => !string.IsNullOrWhiteSpace(ApiBaseUrl) && !string.IsNullOrWhiteSpace(ApiKey);
    }

    public CloudCheapClient(IHttpClientFactory http, IConfiguration config,
        LlmProviderRegistry providers, ILogger<CloudCheapClient> logger,
        Spend.ISpendCollector? spend = null)
    {
        _http = http;
        _logger = logger;
        _spend = spend;
        ProviderKey = config["OpenRouter:Provider"] is { Length: > 0 } legacyProvider ? legacyProvider : "openrouter";

        // Legacy openrouter-direct всегда на месте (совместимость с spend и пресетами);
        // дополнительные источники добавляются из CheapHttpSources.
        AddLegacyOpenRouterSource(providers, config);

        foreach (var child in config.GetSection("CheapHttpSources").GetChildren())
        {
            var sourceKey = child.Key;
            var providerKey = child["Provider"] is { Length: > 0 } configuredProvider ? configuredProvider : sourceKey;
            var cfg = providers.GetByKey(providerKey);
            var models = child.GetSection("Models").GetChildren()
                .Select(c => c.Get<Models.LlmModelConfig>()?.Id ?? c["Id"] ?? c.Value)
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .OfType<string>()
                .ToList();
            var temperature = double.TryParse(child["Temperature"],
                NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out var t)
                ? t : 0;
            _sources.Add(new Source(sourceKey, providerKey,
                cfg?.ApiBaseUrl?.TrimEnd('/') ?? "",
                cfg?.ApiKey ?? "",
                models, temperature));
        }
    }

    private void AddLegacyOpenRouterSource(LlmProviderRegistry providers, IConfiguration config)
    {
        var cfg = providers.GetByKey(ProviderKey);
        var models = config.GetSection("OpenRouter:DirectModels").GetChildren()
            .Select(c => c.Get<Models.LlmModelConfig>()?.Id ?? c["Id"] ?? c.Value)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .OfType<string>()
            .ToList();
        _sources.Add(new Source(ProviderKey, ProviderKey,
            cfg?.ApiBaseUrl?.TrimEnd('/') ?? "",
            cfg?.ApiKey ?? "",
            models, 0));
    }

    // Найти источник, к которому относится id модели. Первый подходящий источник
    // по порядку в конфиге выигрывает; коллизия логируется как предупреждение.
    // Если id неизвестен ни одному источнику — фолбэк на первый настроенный источник
    // (сохраняет поведение одиночного openrouter и прямых вызовов вне каталога).
    public Source? ResolveSource(string model)
    {
        var id = StripPrefix(model);
        if (string.IsNullOrWhiteSpace(id)) return null;

        Source? winner = null;
        foreach (var s in _sources)
        {
            if (!s.Models.Contains(id, StringComparer.OrdinalIgnoreCase)) continue;
            if (winner is null)
            {
                winner = s;
            }
            else
            {
                _logger.LogWarning(
                    "Коллизия id модели {Model} между источниками {First} и {Second}; выигрывает {Winner}",
                    id, winner.Key, s.Key, winner.Key);
                break;
            }
        }
        return winner ?? _sources.FirstOrDefault(s => s.Configured);
    }

    // Свободнотекстовая генерация выбранной моделью (контракт совпадает с
    // OllamaClient.GenerateTextAsync). maxTokens — лимит вывода профиля. Возвращает null при
    // любой ошибке/таймауте/пустом ответе — вызывающий откатывается на следующий маршрут.
    // ВАЖНО: вернувшийся FinishReason=length сам по себе null не возвращает — он попадает в
    // результат отдельным флагом (Truncated) на генераторе: парсер потребителя должен
    // увидеть, что ответ оборван, иначе симптом неотличим от таймаута (прод 2026-08-05:
    // планировщик командной реализации дважды не собрал план из-за 1024 токенов вывода).
    public async Task<CloudTextResult> GenerateDetailedAsync(
        string model, string prompt, TimeSpan timeout, int maxTokens,
        string? ownerId = null, string? label = null, CancellationToken ct = default)
    {
        var source = ResolveSource(model);
        if (source is null || !source.Configured) return new CloudTextResult(null, false);

        try
        {
            var client = _http.CreateClient("llm-provider");
            client.Timeout = timeout;

            using var req = new HttpRequestMessage(HttpMethod.Post, $"{source.ApiBaseUrl}/chat/completions")
            {
                Content = JsonContent.Create(new
                {
                    model,
                    stream = false,
                    temperature = source.Temperature,
                    max_tokens = maxTokens,
                    messages = new[] { new { role = "user", content = prompt } },
                }),
            };
            req.Headers.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", source.ApiKey);

            using var resp = await client.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode)
            {
                // 429 — исчерпан суточный/минутный лимит бесплатных моделей: штатный сценарий,
                // не шумим ошибкой, вызывающий уходит на следующий маршрут
                _logger.LogDebug("{Route} /chat/completions вернул {Status} для {Model}",
                    source.Key, resp.StatusCode, model);
                return new CloudTextResult(null, false);
            }

            var json = await resp.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ct);
            // Часть рассуждающих моделей кладёт ход мысли в message.reasoning отдельно от
            // content — тогда достаточно взять content. Но не все: MiniMax-M3 отдельного поля
            // не заводит вовсе (в message приходят content/role/name/audio_content) и пишет
            // рассуждение ПРЯМО В content тегом <think>…</think> — снимаем его ниже
            var content = json.TryGetProperty("choices", out var choices)
                && choices.ValueKind == JsonValueKind.Array && choices.GetArrayLength() > 0
                && choices[0].TryGetProperty("message", out var msg)
                && msg.TryGetProperty("content", out var c)
                    ? c.GetString()
                    : null;
            // FinishReason="length" — ответ оборван по лимиту max_tokens. OpenAI-совместимые
            // источники в этом случае кладут причину в choice.finish_reason, у некоторых
            // (ollama-совместимые) — в choice.stop_reason. Проверяем обе в логе.
            var truncated = false;
            if (choices.ValueKind == JsonValueKind.Array && choices.GetArrayLength() > 0
                && choices[0].ValueKind == JsonValueKind.Object)
            {
                truncated = IsTruncatedFinish(choices[0]);
                if (truncated)
                    _logger.LogWarning(
                        "{Route} ответ обрезан по лимиту {MaxTokens} токенов (модель {Model}, label={Label})",
                        source.Key, maxTokens, model, label ?? "-");
            }
            if (string.IsNullOrWhiteSpace(content)) return new CloudTextResult(null, truncated);
            // Расход пишем по ФАКТУ ответа, до срезки рассуждения: токены потрачены в любом
            // случае, а вызов, у которого ответ оказался одним лишь ходом мысли, из статистики
            // исчезать не должен — иначе самый дорогой класс вызовов невидим
            RecordSpend(model, source.Key, json, ownerId, label);
            var answer = StripReasoning(content!);
            if (string.IsNullOrWhiteSpace(answer))
            {
                _logger.LogWarning(
                    "{Route} ответ содержит только ход мысли, без самого ответа (модель {Model}, label={Label})",
                    source.Key, model, label ?? "-");
                return new CloudTextResult(null, truncated);
            }
            return new CloudTextResult(answer, truncated);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "{Route} недоступен, фолбэк на следующий маршрут", source.Key);
            return new CloudTextResult(null, false);
        }
    }

    // Свободнотекстовая генерация: обратная совместимость со старыми потребителями,
    // которым неотличимость обрыва от таймаута не важна (короткие ответы, у которых
    // лимита вывода хватает с запасом). Прямой обрыв с логом, но без bail-а.
    public async Task<string?> GenerateTextAsync(
        string model, string prompt, TimeSpan timeout, int maxTokens,
        string? ownerId = null, string? label = null, CancellationToken ct = default)
    {
        var result = await GenerateDetailedAsync(model, prompt, timeout, maxTokens, ownerId, label, ct);
        return result.Text;
    }

    // Снять ход мысли рассуждающей модели, приехавший внутри content тегом <think>…</think>
    // (MiniMax-M3; отдельного поля reasoning у неё нет). Берём то, что ПОСЛЕ последнего
    // закрывающего тега: рассуждение всегда идёт первым, а закрытий может быть несколько.
    //
    // Открытый <think> без закрытия — не «ответ с мусором», а его отсутствие: модель
    // израсходовала бюджет вывода на рассуждение и до ответа не дошла. Возвращаем пусто,
    // и вызывающий уходит дальше по цепочке — это честнее, чем отдать потребителю
    // рассуждение вместо результата (прод 28.08.2026: сводка «Что нового» разбирала
    // ход мысли как JSON — ExtractJsonArray хватал первую же скобку ВНУТРИ рассуждения,
    // и день уходил в fallback с сырыми коммитами).
    internal static string? StripReasoning(string content)
    {
        const string close = "</think>";
        var end = content.LastIndexOf(close, StringComparison.OrdinalIgnoreCase);
        if (end >= 0) return content[(end + close.Length)..].Trim();
        return content.Contains("<think", StringComparison.OrdinalIgnoreCase) ? null : content;
    }

    private static bool IsTruncatedFinish(JsonElement choice) =>
        (choice.TryGetProperty("finish_reason", out var fr) && fr.ValueKind == JsonValueKind.String
            && string.Equals(fr.GetString(), "length", StringComparison.OrdinalIgnoreCase))
        || (choice.TryGetProperty("stop_reason", out var sr) && sr.ValueKind == JsonValueKind.String
            && string.Equals(sr.GetString(), "length", StringComparison.OrdinalIgnoreCase));

    // Расход прямого вызова в аналитику: токены из usage OpenAI-совместимого ответа,
    // стоимость 0 (модели бесплатные). Источник spend провайдера — {sourceKey}-direct.
    // Ошибка записи вызов не роняет.
    private void RecordSpend(string model, string sourceKey, JsonElement json, string? ownerId, string? label)
    {
        if (_spend is null) return;
        try
        {
            long inTok = 0, outTok = 0;
            if (json.TryGetProperty("usage", out var u) && u.ValueKind == JsonValueKind.Object)
            {
                if (u.TryGetProperty("prompt_tokens", out var p) && p.ValueKind == JsonValueKind.Number)
                    inTok = p.GetInt64();
                if (u.TryGetProperty("completion_tokens", out var c) && c.ValueKind == JsonValueKind.Number)
                    outTok = c.GetInt64();
            }
            _spend.Record(new Models.SpendRecord
            {
                OwnerId = ownerId ?? "",
                Provider = $"{sourceKey}-direct",
                Model = model,
                Source = Models.SpendSources.Free,
                Label = label,
                InputTokens = inTok,
                OutputTokens = outTok,
                CostUsd = 0,
            });
        }
        catch { /* аналитика не должна ронять вызов */ }
    }
}
