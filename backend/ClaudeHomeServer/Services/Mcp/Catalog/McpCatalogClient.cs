using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;

namespace ClaudeHomeServer.Services.Mcp.Catalog;

/// <summary>
/// Доменная ошибка каталога: реестр недоступен или ответил мусором. Любая беда клиента —
/// одна эта ошибка с текстом для человека (принцип «каталог не роняет раздел»), а не
/// сырые исключения HttpClient наружу.
/// </summary>
public class McpCatalogUnavailableException(string message) : Exception(message);

/// <summary>Секция Mcp:Catalog в конфиге.</summary>
public class McpCatalogOptions
{
    /// <summary>
    /// Базовый адрес официального реестра (https://registry.modelcontextprotocol.io).
    /// Пустой — каталог выключен: единственный рубильник, второго не заводим.
    /// </summary>
    public string BaseUrl { get; set; } = "";
    /// <summary>Страница выдачи; клампится 1..100 (реестр больше не отдаёт).</summary>
    public int PageSize { get; set; } = 20;
    /// <summary>Время жизни кэша страницы, минуты.</summary>
    public int CacheMinutes { get; set; } = 30;
    /// <summary>Потолок записей кэша (размер одной записи — 1): кэш не растёт без предела.</summary>
    public int CacheMaxEntries { get; set; } = 256;
    /// <summary>Потолок длины поискового запроса: дольше — обрезаем, а не отказываем.</summary>
    public int MaxQueryLength { get; set; } = 100;
    /// <summary>Потолок имён в одном батче ревизии импортированных записей.</summary>
    public int RevisionBatchLimit { get; set; } = 50;

    public static McpCatalogOptions FromConfig(IConfiguration config) =>
        config.GetSection("Mcp:Catalog").Get<McpCatalogOptions>() ?? new();
}

/// <summary>
/// Клиент официального реестра MCP: поиск GET /v0.1/servers?search=&amp;limit=&amp;cursor=
/// и ревизия импортированных записей GET /v0.1/servers/{name}/versions/latest.
/// Поиск кэшируется в памяти на 30 минут по (q, cursor) — без ownerId: данные реестра
/// публичны и одинаковы для всех, а кэш на кнопочный поиск живёт быстро. TTL и лимит
/// записей — на инжектируемом TimeProvider/своём MemoryCache, чтобы тесты не ждали.
/// </summary>
public class McpCatalogClient
{
    /// <summary>Имя тихого HTTP-клиента: реестр зарубежный, ходим ЧЕРЕЗ egress-прокси.</summary>
    public const string HttpClientName = "mcp-catalog";

    // TTL кэша ревизии: статус в реестре меняется раз в месяцы, а UI-триггер человек
    // повторяет кликом — сутки держат реестр в покое (план «Каталог», волна 2)
    private static readonly TimeSpan RevisionCacheTtl = TimeSpan.FromHours(24);

    private readonly HttpClient _http;
    private readonly McpCatalogOptions _options;
    private readonly IMemoryCache _cache;
    private readonly TimeProvider _time;

    public bool IsEnabled => !string.IsNullOrWhiteSpace(_options.BaseUrl);

    public McpCatalogClient(IHttpClientFactory httpFactory, McpCatalogOptions options,
        TimeProvider? timeProvider = null)
    {
        _http = httpFactory.CreateClient(HttpClientName);
        _options = options;
        _time = timeProvider ?? TimeProvider.System;
        // Своя копия кэша (не общий AddMemoryCache): часы у неё свои — тесты двигают время
        _cache = new MemoryCache(new MemoryCacheOptions
        {
            Clock = new TimeProviderClock(_time),
            SizeLimit = _options.CacheMaxEntries,
        });
    }

    /// <summary>
    /// Страница поиска. Пробелы по краям срезаются, длинный запрос обрезается по капу.
    /// Бьётся в сеть только мимо кэша; любая беда (таймаут, 5xx, битый JSON, тело сверх
    /// буфера) — McpCatalogUnavailableException.
    /// </summary>
    public virtual async Task<McpCatalogSearchResult> SearchAsync(string q, string? cursor,
        CancellationToken ct = default)
    {
        var query = (q ?? "").Trim();
        if (query.Length > _options.MaxQueryLength)
            query = query[.._options.MaxQueryLength];
        cursor = string.IsNullOrEmpty(cursor) ? null : cursor.Trim();
        var cacheKey = query + "\n" + cursor;
        if (_cache.TryGetValue(cacheKey, out McpCatalogSearchResult? cached))
            return cached!;

        var page = await FetchAndMapAsync(query, cursor, ct);
        _cache.Set(cacheKey, page, new MemoryCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(_options.CacheMinutes),
            Size = 1,
        });
        return page;
    }

    // Ход в реестр и разбор ответа. Отдельный virtual-метод (а не часть SearchAsync):
    // контроллерные тесты подменяют его фейком, не затрагивая кэш
    protected virtual async Task<McpCatalogSearchResult> FetchAndMapAsync(string q, string? cursor,
        CancellationToken ct)
    {
        var limit = Math.Clamp(_options.PageSize, 1, 100);
        var url = $"{_options.BaseUrl.TrimEnd('/')}/v0.1/servers?limit={limit}";
        if (q.Length > 0) url += "&search=" + Uri.EscapeDataString(q);
        if (cursor is not null) url += "&cursor=" + Uri.EscapeDataString(cursor);

        HttpResponseMessage response;
        try
        {
            response = await _http.GetAsync(url, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException
            or OperationCanceledException or InvalidOperationException or UriFormatException)
        {
            throw new McpCatalogUnavailableException(
                "Реестр MCP не отвечает: " + ex.Message.Split('\n')[0]);
        }
        using (response)
        {
            if (!response.IsSuccessStatusCode)
                throw new McpCatalogUnavailableException(
                    $"Реестр MCP ответил {(int)response.StatusCode}");
            string body;
            try
            {
                body = await response.Content.ReadAsStringAsync(ct);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException
                or OperationCanceledException or ObjectDisposedException)
            {
                // Слишком большое тело падает именно здесь: MaxResponseContentBufferSize
                throw new McpCatalogUnavailableException("Ответ реестра слишком большой");
            }
            try
            {
                return McpCatalogMapper.MapSearchResponse(JsonSerializer.Deserialize<JsonElement>(body));
            }
            catch (JsonException)
            {
                throw new McpCatalogUnavailableException("Ответ реестра не разобран");
            }
        }
    }

    // ── Ревизия импортированных записей (волна 2) ─────────────────────────────────────

    /// <summary>
    /// Батч-сверка с реестром: по каждому имени — статус и свежесть версии. Не бросает
    /// исключений: беда конкретной проверки — CheckFailed в элементе ответа, соседние
    /// имена проверяются как ни в чём не бывало. Успешный результат кэшируется на сутки;
    /// неудачный НЕ кэшируется — лежащий реестр не знание о сервере, повторный клик
    /// обязан снова пойти в сеть.
    /// </summary>
    public async Task<IReadOnlyList<McpCatalogRevisionItem>> ReviseAsync(
        IReadOnlyList<McpCatalogRevisionQuery> queries, CancellationToken ct = default)
    {
        var results = new List<McpCatalogRevisionItem>(queries.Count);
        foreach (var query in queries)
        {
            // Дубли имени в одном батче бьются в сеть один раз: первый результат лёг в кэш
            var cacheKey = "rev:" + query.Name;
            if (_cache.TryGetValue(cacheKey, out McpCatalogRevisionItem? cached))
            {
                results.Add(cached!);
                continue;
            }
            var item = await CheckOneAsync(query, ct);
            if (!item.CheckFailed)
                _cache.Set(cacheKey, item, new MemoryCacheEntryOptions
                {
                    AbsoluteExpirationRelativeToNow = RevisionCacheTtl,
                    Size = 1,
                });
            results.Add(item);
        }
        return results;
    }

    // Одна проверка: ход в реестр + разбор. «Отозван» — только явный status
    // deprecated/deleted в разобранном ответе; всё остальное (404, таймаут, 5xx,
    // битый JSON) — CheckFailed, НЕ «отзыв»: иначе лежащий preview-сервис молча
    // превращался бы в вердикт «выключи рабочие серверы»
    private async Task<McpCatalogRevisionItem> CheckOneAsync(
        McpCatalogRevisionQuery query, CancellationToken ct)
    {
        string body;
        try
        {
            body = await FetchLatestAsync(query.Name, ct);
        }
        catch (McpCatalogUnavailableException ex)
        {
            return Failed(query.Name, ex.Message);
        }
        try
        {
            var (status, version) = McpCatalogMapper.MapLatestVersion(
                JsonSerializer.Deserialize<JsonElement>(body));
            var deprecated = status is "deprecated" or "deleted";
            return new McpCatalogRevisionItem(
                Name: query.Name, Status: status, Deprecated: deprecated,
                HasNewerVersion: McpCatalogSemVer.IsNewer(version, query.ImportedVersion),
                LatestVersion: version, CheckFailed: false, Error: null);
        }
        catch (JsonException)
        {
            return Failed(query.Name, "Ответ реестра не разобран");
        }
    }

    private static McpCatalogRevisionItem Failed(string name, string error) => new(
        Name: name, Status: null, Deprecated: false, HasNewerVersion: false,
        LatestVersion: null, CheckFailed: true, Error: error);

    // Ход за последней версией записи. Отдельный virtual-метод: контроллерные тесты
    // подменяют его фейком, не затрагивая кэш (как FetchAndMapAsync у поиска)
    protected virtual async Task<string> FetchLatestAsync(string name, CancellationToken ct)
    {
        var url = $"{_options.BaseUrl.TrimEnd('/')}/v0.1/servers/" +
            Uri.EscapeDataString(name) + "/versions/latest";

        HttpResponseMessage response;
        try
        {
            response = await _http.GetAsync(url, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException
            or OperationCanceledException or InvalidOperationException or UriFormatException)
        {
            throw new McpCatalogUnavailableException(
                "Реестр MCP не отвечает: " + ex.Message.Split('\n')[0]);
        }
        using (response)
        {
            if (!response.IsSuccessStatusCode)
                throw new McpCatalogUnavailableException(
                    $"Реестр MCP ответил {(int)response.StatusCode}");
            try
            {
                return await response.Content.ReadAsStringAsync(ct);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException
                or OperationCanceledException or ObjectDisposedException)
            {
                throw new McpCatalogUnavailableException("Ответ реестра слишком большой");
            }
        }
    }

    // MemoryCacheOptions.Clock ждёт ISystemClock — адаптируем TimeProvider
    private sealed class TimeProviderClock(TimeProvider time)
        : Microsoft.Extensions.Internal.ISystemClock
    {
        public DateTimeOffset UtcNow => time.GetUtcNow();
    }
}
