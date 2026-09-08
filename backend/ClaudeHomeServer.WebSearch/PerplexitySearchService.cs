using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;

namespace ClaudeHomeServer.Services.WebSearch;

/// <summary>
/// Настройки веб-поиска (секция <c>Perplexity</c> appsettings). Пустой <see cref="ApiKey"/> —
/// поиск выключен: тулсет <c>websearch</c> ходу не объявляется вовсе (тот же рубильник, что
/// у Dify — единственный, второго не заводим). Ключ машинно-специфичен и живёт
/// в appsettings.Local.json, в отслеживаемом файле остаётся пустая строка.
/// </summary>
public sealed class PerplexityOptions
{
    /// <summary>Базовый адрес API (без хвоста /chat/completions).</summary>
    public string ApiUrl { get; set; } = "https://api.perplexity.ai";

    /// <summary>Ключ API. Пусто = поиск выключен.</summary>
    public string ApiKey { get; set; } = "";

    /// <summary>Модель поиска. Дефолт — самая дешёвая линейки Sonar.</summary>
    public string Model { get; set; } = "sonar";

    /// <summary>
    /// Потолок одного запроса: DNS + прокси + коннект + тело. 45 с не с потолка: живой замер
    /// 2026-09-06 через egress-прокси дал 23 с на обычный запрос — при 30 с запаса почти нет,
    /// а таймаут поиска бесполезен модели ровно так же, как ошибка.
    /// </summary>
    public int TimeoutSeconds { get; set; } = 45;

    /// <summary>Потолок длины ответа модели в символах: длинный ответ съедает окно локальной.</summary>
    public int MaxAnswerChars { get; set; } = 6000;

    /// <summary>Сколько цитат отдавать модели (Sonar возвращает до нескольких десятков).</summary>
    public int MaxCitations { get; set; } = 10;

    /// <summary>
    /// Цена за 1M токенов, $. Ноль (дефолт) = «цена неизвестна»: трата пишется в токенах,
    /// а денег в записи нет. Выдуманная цена в отчёте о деньгах хуже отсутствующей —
    /// тариф зависит от плана и меняется, поэтому значения проставляет хозяин инстанса.
    /// Учтите: у Sonar сверх токенов есть ещё плата ЗА ЗАПРОС поиска, из ответа API она
    /// не выводится и здесь не считается.
    /// </summary>
    public double PriceInPer1M { get; set; }

    public double PriceOutPer1M { get; set; }

    public bool IsConfigured => !string.IsNullOrWhiteSpace(ApiKey);

    public static PerplexityOptions FromConfig(IConfiguration config) =>
        config.GetSection("Perplexity").Get<PerplexityOptions>() ?? new();
}

/// <summary>Одна ссылка-источник ответа: URL обязателен, заголовок и дата — как отдал Sonar.</summary>
public sealed record WebSearchCitation(string Url, string? Title, string? Date);

/// <summary>
/// Исход поиска: либо ответ с цитатами и расходом токенов, либо текст ошибки для модели.
/// Текст ошибки уже очищен от ключа (<see cref="PerplexitySearchService"/>).
/// </summary>
public sealed record WebSearchOutcome
{
    public bool Success { get; private init; }
    public string Answer { get; private init; } = "";
    public IReadOnlyList<WebSearchCitation> Citations { get; private init; } = [];
    public long InputTokens { get; private init; }
    public long OutputTokens { get; private init; }
    public string? Error { get; private init; }

    public static WebSearchOutcome Ok(string answer, IReadOnlyList<WebSearchCitation> citations,
        long inputTokens, long outputTokens) => new()
    {
        Success = true, Answer = answer, Citations = citations,
        InputTokens = inputTokens, OutputTokens = outputTokens,
    };

    public static WebSearchOutcome Fail(string error) => new() { Success = false, Error = error };
}

/// <summary>
/// Веб-поиск через Perplexity Sonar (<c>POST {ApiUrl}/chat/completions</c>). Единственный
/// доступный нам поисковый API: замер 2026-09-06 через egress-прокси владельца показал, что
/// google/bing/duckduckgo/brave и публичные SearXNG отдают 000 или капчу, а api.perplexity.ai
/// отвечает 401 (то есть доезжает) — поэтому готовые MCP веб-поиска у нас молча не работают.
///
/// Клиент ходит ЧЕРЕЗ egress-прокси (сервис зарубежный) — <c>WithoutEgressProxy</c> тут звать
/// нельзя, в отличие от локальных Dify/Forgejo.
///
/// Ключ не покидает бэкенд: он живёт только в заголовке Authorization этого запроса, наружу
/// (в конфиг хода, env процесса CLI, ответ инструмента) не уезжает никогда. Тексты ошибок
/// для модели собираются из статуса и тела ответа и дополнительно прогоняются через
/// <see cref="Redact"/> — тело чужого сервиса нам не подконтрольно, и эхо ключа в нём
/// не должно доехать ни до модели, ни до лога.
/// </summary>
public sealed class PerplexitySearchService(
    IHttpClientFactory httpClientFactory,
    PerplexityOptions options,
    ILogger<PerplexitySearchService> logger)
{
    /// <summary>Имя тихого HTTP-клиента: сервис опциональный, мёртвый не должен сыпать стектрейсами.</summary>
    public const string HttpClientName = "perplexity";

    /// <summary>Значения фильтра свежести, которые принимает Sonar.</summary>
    private static readonly string[] RecencyValues = ["day", "week", "month", "year"];

    public bool IsConfigured => options.IsConfigured;

    public string Model => options.Model;

    public double PriceInPer1M => options.PriceInPer1M;

    public double PriceOutPer1M => options.PriceOutPer1M;

    /// <summary>Известное значение фильтра свежести либо null: мусор от модели тихо игнорируем.</summary>
    public static string? NormalizeRecency(string? value) =>
        value is not null && RecencyValues.Contains(value) ? value : null;

    public async Task<WebSearchOutcome> SearchAsync(string query, string? recency, CancellationToken ct)
    {
        if (!options.IsConfigured)
            return WebSearchOutcome.Fail("Веб-поиск не настроен: задайте Perplexity:ApiKey в конфигурации сервера.");

        var sw = Stopwatch.StartNew();
        try
        {
            var outcome = await SearchCoreAsync(query, recency, ct);
            LogOutcome(outcome, sw.Elapsed);
            return outcome;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            logger.LogInformation("Веб-поиск: таймаут за {ElapsedMs} мс", (int)sw.Elapsed.TotalMilliseconds);
            return WebSearchOutcome.Fail($"Веб-поиск не ответил за {options.TimeoutSeconds} с — попробуйте позже.");
        }
        catch (HttpRequestException ex)
        {
            // Сеть/прокси/TLS: сообщение исключения ключа не содержит, но Redact стоит на всех
            // путях наружу — цена одна, а гарантия становится структурной
            var text = Redact(ex.Message);
            logger.LogInformation("Веб-поиск: сеть недоступна ({Error})", text);
            return WebSearchOutcome.Fail($"Веб-поиск недоступен: {text}");
        }
    }

    private async Task<WebSearchOutcome> SearchCoreAsync(string query, string? recency, CancellationToken ct)
    {
        var body = new JsonObject
        {
            ["model"] = options.Model,
            ["messages"] = new JsonArray
            {
                new JsonObject { ["role"] = "user", ["content"] = query },
            },
        };
        if (NormalizeRecency(recency) is { } filter) body["search_recency_filter"] = filter;

        var client = httpClientFactory.CreateClient(HttpClientName);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(options.TimeoutSeconds));

        using var request = new HttpRequestMessage(HttpMethod.Post,
            options.ApiUrl.TrimEnd('/') + "/chat/completions")
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + options.ApiKey);

        using var response = await client.SendAsync(request, cts.Token);
        var raw = await response.Content.ReadAsStringAsync(cts.Token);

        if (!response.IsSuccessStatusCode)
            return WebSearchOutcome.Fail(
                $"Веб-поиск отклонил запрос (HTTP {(int)response.StatusCode}): {Redact(Snippet(raw))}");

        return Parse(raw, options.MaxAnswerChars, options.MaxCitations);
    }

    /// <summary>
    /// Разбор ответа Sonar. Разбор защитный: у Perplexity сосуществуют два поля источников —
    /// старое <c>citations</c> (просто URL) и новое <c>search_results</c> (URL + заголовок +
    /// дата). Берём то, что богаче, и склеиваем по URL: цитаты — половина ценности инструмента
    /// (локальная модель склонна выдумывать, проверяемая ссылка это лечит), терять их из-за
    /// смены формы ответа нельзя.
    /// </summary>
    internal static WebSearchOutcome Parse(string raw, int maxAnswerChars, int maxCitations)
    {
        JsonNode? root;
        try { root = JsonNode.Parse(raw); }
        catch (System.Text.Json.JsonException) { return WebSearchOutcome.Fail("Веб-поиск вернул нечитаемый ответ."); }

        if (root is not JsonObject obj) return WebSearchOutcome.Fail("Веб-поиск вернул неожиданный ответ.");

        var answer = obj["choices"] is JsonArray { Count: > 0 } choices
            && choices[0] is JsonObject choice
            && choice["message"] is JsonObject message
            && message["content"] is JsonValue content
            && content.TryGetValue<string>(out var text)
            ? text
            : "";

        var citations = new List<WebSearchCitation>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (obj["search_results"] is JsonArray results)
        {
            foreach (var node in results)
            {
                if (node is not JsonObject item) continue;
                var url = Str(item, "url");
                if (url.Length == 0 || !seen.Add(url)) continue;
                citations.Add(new WebSearchCitation(url, NullIfEmpty(Str(item, "title")),
                    NullIfEmpty(Str(item, "date"))));
            }
        }

        if (obj["citations"] is JsonArray urls)
        {
            foreach (var node in urls)
            {
                if (node is not JsonValue value || !value.TryGetValue<string>(out var url)) continue;
                if (url.Length == 0 || !seen.Add(url)) continue;
                citations.Add(new WebSearchCitation(url, null, null));
            }
        }

        var usage = obj["usage"] as JsonObject;
        var inputTokens = Long(usage, "prompt_tokens");
        var outputTokens = Long(usage, "completion_tokens");

        if (answer.Length == 0 && citations.Count == 0)
            return WebSearchOutcome.Fail("Веб-поиск ничего не нашёл.");

        return WebSearchOutcome.Ok(Truncate(answer, maxAnswerChars),
            citations.Count > maxCitations ? citations[..maxCitations] : citations,
            inputTokens, outputTokens);
    }

    /// <summary>
    /// Вырезает ключ из строки, идущей наружу (ответ модели, лог). Тело ответа чужого сервиса
    /// нам не подконтрольно — эхо ключа в нём структурно невозможно пропустить только так.
    /// </summary>
    private string Redact(string text) =>
        options.ApiKey.Length == 0 ? text : text.Replace(options.ApiKey, "***", StringComparison.Ordinal);

    private void LogOutcome(WebSearchOutcome outcome, TimeSpan elapsed)
    {
        // Текст запроса в лог не идёт никогда (та же формула, что у ридера: исход + время):
        // поисковый запрос — содержимое разговора человека, логу оно не принадлежит
        if (outcome.Success)
            logger.LogInformation("Веб-поиск: ok, {Citations} цитат, {In}+{Out} токенов за {ElapsedMs} мс",
                outcome.Citations.Count, outcome.InputTokens, outcome.OutputTokens,
                (int)elapsed.TotalMilliseconds);
        else
            logger.LogInformation("Веб-поиск: отказ ({Error}) за {ElapsedMs} мс",
                outcome.Error, (int)elapsed.TotalMilliseconds);
    }

    // Кусок тела ответа для текста ошибки: чужой сервис может ответить страницей на мегабайт
    private static string Snippet(string raw)
    {
        var oneLine = raw.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return oneLine.Length <= 300 ? oneLine : oneLine[..300] + "…";
    }

    private static string Truncate(string text, int max) =>
        text.Length <= max ? text : text[..max] + "\n\n…(ответ обрезан)";

    private static string Str(JsonObject obj, string name) =>
        obj[name] is JsonValue v && v.TryGetValue<string>(out var s) ? s : "";

    private static string? NullIfEmpty(string value) => value.Length == 0 ? null : value;

    private static long Long(JsonObject? obj, string name) =>
        obj?[name] is JsonValue v && v.TryGetValue<long>(out var i) ? i : 0;
}
