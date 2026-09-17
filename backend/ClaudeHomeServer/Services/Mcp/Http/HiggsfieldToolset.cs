using System.Text.Json;
using System.Text.Json.Nodes;

namespace ClaudeHomeServer.Services.Mcp.Http;

/// <summary>
/// Прокси-тулсет Higgsfield к mcp.higgsfield.ai/mcp с белым списком из 10 инструментов.
/// Состав конфига хода строится из кэша с валидностью 30 мин, fail-open на предыдущем снимке.
/// Доступ к прокси через хвост /mcp/higgsfield/{sessionId}, fail-closed на чужой сессии (обязательно свой).
/// При отсутствии инстансного подключения (пустой EnsureFresh) список пуст — сервер не объявляется ходу.
///
/// Проблема Mcp-Session-Id (ревью Глеба): Higgsfield сейчас отвечает без заголовка Mcp-Session-Id.
/// Если он когда-либо перейдёт на Streamable HTTP с сессиями,
/// этот прокси должен упасть первым, а не молча ломаться о проде. См. тест на pitfall в HiggsfieldToolsetTests.
/// </summary>
public sealed class HiggsfieldToolset : IMcpParameterizedToolset
{
    private readonly HiggsfieldOAuthService higgsfield;
    private readonly IHttpClientFactory clientFactory;
    private readonly SessionManager sessions;
    private readonly Mcp.McpStatusStore? mcpStatus;
    private readonly ILogger<HiggsfieldToolset> log;

    public HiggsfieldToolset(
        HiggsfieldOAuthService higgsfield,
        IHttpClientFactory clientFactory,
        SessionManager sessions,
        IConfiguration config,
        Mcp.McpStatusStore? mcpStatus,
        ILogger<HiggsfieldToolset> log)
    {
        this.higgsfield = higgsfield;
        this.clientFactory = clientFactory;
        this.sessions = sessions;
        this.mcpStatus = mcpStatus;
        this.log = log;
        _snapshotPath = ResolveSnapshotPath(config);
        // Холодный старт: поднимаем снимок с диска. lastSuccessAt берём из снимка
        // (новый формат с полем SavedAt); если поля нет — старый формат — считаем снимок
        // свежим. Иначе при первом же вызове GetCachedTools он истёк бы и пошёл в сеть.
        var loaded = LoadSnapshotFromDisk(_snapshotPath);
        _cachedTools = loaded?.Tools;
        // Для обратной совместимости: снимок без SavedAt (старый формат) — относимся как
        // к «только что сохранённому». Иначе после выкатки на новый код все существующие
        // инсталляции остались бы без сервера, пока warmer не обновит кэш (а warmer не
        // идёт, если снимок старше 24ч — лечим именно это условие).
        _lastSuccessAt = loaded?.SavedAt ?? DateTime.UtcNow;
    }

    public const string ServerName = McpEndpoints.HiggsfieldName;

    /// <summary>Имя HTTP-клиента для тихих запросов к Higgsfield.</summary>
    public const string HttpClientName = "higgsfield-mcp";

    /// <summary>Базовый URL эндпоинта Higgsfield MCP.</summary>
    public const string UpstreamUrl = HiggsfieldOAuthService.Url;

    /// <summary>TTL кэша списка tools/list: 30 минут.</summary>
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(30);

    /// <summary>
    /// Потолок возраста успешного снимка. После него лучше не объявлять сервер,
    /// чем кормить модель фантомными инструментами (апстрим переименовал
    /// <c>generate_image</c> → модель бесконечно видит 10 фантомов, каждый вызов
    /// горит ошибкой). Замер шире дневного окна дрейфа апстрима с запасом.
    /// </summary>
    private static readonly TimeSpan SnapshotMaxAge = TimeSpan.FromDays(1);

    /// <summary>
    /// Короткий отрицательный кэш после провала fetch: следующая попытка не раньше,
    /// чем через эту паузу. Без него пять чатов после рестарта при лежащем апстриме
    /// долбят сеть каждый ход.
    /// </summary>
    private static readonly TimeSpan NegativeCacheTtl = TimeSpan.FromMinutes(1.5);

    /// <summary>
    /// Имя файла снимка списка инструментов в data/ — кеш, едет в исключения облачного архива
    /// (см. <c>BackupPaths.ShouldInclude</c>): секретов в нём нет, а восстановленный с диска
    /// снимок всё равно устаревший — проще дождаться первого успешного tools/list.
    /// </summary>
    public const string SnapshotFileName = "higgsfield-tools.json";

    /// <summary>
    /// Белый список инструментов — единственный источник правды для состава хода.
    /// Higgsfield отдаёт 88+ инструментов; в конфиг едут только эти 10.
    /// Остальные (подписки, деплой, sandbox, TikTok) являются шумом и риском для модели.
    /// </summary>
    internal static readonly string[] Whitelist =
    [
        "generate_image",
        "generate_video",
        "generate_audio",
        "job_status",
        "jobs_wait",
        "show_generation_by_ids",
        "models_explore",
        "media_import_url",
        "media_upload",
        "media_upload_widget",
    ];

    private static readonly HashSet<string> WhitelistSet = new(Whitelist, StringComparer.Ordinal);

    public string Name => ServerName;
    public string Version => "1.0.0";

    // Кэш: последний успешный снимок + два таймстампа.
    //   _lastSuccessAt   — момент последнего УСПЕШНОГО tools/list (из сети).
    //                       Снимкам старше SnapshotMaxAge — отказ: фантомные инструменты.
    //   _lastFetchAttempt — момент последней ПОПЫТКИ (любой исход). Используется для
    //                       negative cache: после провала следующая попытка не раньше
    //                       NegativeCacheTtl, иначе пять чатов при лежащем апстриме
    //                       долбят сеть каждый ход (см. задачу 67b7c30a).
    // При пуске процесса _cachedTools и _lastSuccessAt поднимаются со снимка
    // data/higgsfield-tools.json — иначе первый ход после рестарта лотерея на рваном канале.
    private readonly object _cacheLock = new();
    private IReadOnlyList<McpToolSchema>? _cachedTools;
    private DateTime _lastSuccessAt;
    private DateTime _lastFetchAttempt;
    private readonly string _snapshotPath;

    // ---- Состав ----

    public IReadOnlyList<McpToolSchema> ToolsFor(McpToolCallContext context)
    {
        if (!TryResolveSession(context, out _)) return [];

        // Решаем «надо ли обновлять» под локом. Сетевого вызова здесь НЕТ — критический
        // фикс ревью Глеба: раньше ToolsFor ходил в сеть (40–80 с на лежащем апстриме) и
        // CLI с MCP_TIMEOUT=30 не дожидался handshake. Теперь фоном пинаем RefreshNowAsync.
        IReadOnlyList<McpToolSchema>? cached;
        DateTime lastSuccessAt;
        DateTime lastFetchAttempt;
        bool needRefresh;
        lock (_cacheLock)
        {
            cached = _cachedTools;
            lastSuccessAt = _lastSuccessAt;
            lastFetchAttempt = _lastFetchAttempt;
            needRefresh = cached is null || lastSuccessAt + CacheTtl < DateTime.UtcNow;
        }

        // Снимку больше суток — отдаём пустой состав: апстрим мог переименовать инструменты,
        // и кормить модель фантомами опаснее, чем оставить чат без интеграции. Сервер при
        // этом продолжает быть «объявленным» — это решает McpRegistry (запись живёт), а
        // отсутствие инструментов означает «не отдавать ни одного», не «не показывать».
        if (cached is not null && DateTime.UtcNow - lastSuccessAt > SnapshotMaxAge)
        {
            ScheduleRefresh(lastFetchAttempt);
            return [];
        }

        if (needRefresh) ScheduleRefresh(lastFetchAttempt);
        return cached ?? [];
    }

    // Запускает RefreshNowAsync в фоне, если только что не пробовали. Negative cache
    // обязателен: иначе пять одновременных ToolsFor после рестарта при лежащем апстриме
    // долбят сеть каждый ход (тик warmer раз в 20 мин, а ходы идут постоянно).
    private void ScheduleRefresh(DateTime lastFetchAttempt)
    {
        if (DateTime.UtcNow - lastFetchAttempt < NegativeCacheTtl) return;
        _ = Task.Run(() => RefreshNowAsync(CancellationToken.None));
    }

    /// <summary>
    /// Принудительный прогрев снимка: для <see cref="IHostedService"/> стартёра. Идёт
    /// той же дорогой, что и ToolsFor (та же <see cref="FetchToolsListAsync"/>,
    /// тот же WARN на отказе, та же запись на диск при успехе), но снаружи лока —
    /// ходы не должны ловить «не успели обновить кэш» как свою ошибку.
    /// Возвращает true, если снимок обновлён.
    /// </summary>
    public async Task<bool> RefreshNowAsync(CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        lock (_cacheLock) _lastFetchAttempt = now;
        IReadOnlyList<McpToolSchema>? fresh = null;
        try
        {
            fresh = await FetchToolsListAsync(ct);
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Higgsfield warmer: refresh failed");
        }
        if (fresh is null) return false;
        lock (_cacheLock)
        {
            _cachedTools = fresh;
            _lastSuccessAt = DateTime.UtcNow;
        }
        try { SaveSnapshot(fresh); }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Higgsfield warmer: snapshot save failed");
        }
        return true;
    }

    // ---- Вызов ----

    public async Task<McpToolCallResult> CallAsync(string tool, JsonObject arguments,
        McpToolCallContext context, CancellationToken ct)
    {
        if (!TryResolveSession(context, out _))
            return Deny("Chat invoker not found or belongs to another owner.");
        var token = higgsfield.EnsureFresh();
        if (token is null)
            return Deny("Higgsfield is not connected on this instance - contact the admin.");
        if (!WhitelistSet.Contains(tool))
            return Deny($"Tool {tool} is not part of the Higgsfield toolset on this instance.");
        try
        {
            var body = new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = 1,
                ["method"] = "tools/call",
                ["params"] = new JsonObject
                {
                    ["name"] = tool,
                    ["arguments"] = arguments.DeepClone(),
                },
            };
            var response = await PostToHiggsfieldAsync(token, body, ct);
            return response is null
                ? Deny("Higgsfield did not respond (connection failure or timeout).")
                : ExtractResult(response);
        }
        catch (HttpRequestException ex)
        {
            return Deny($"Higgsfield unavailable: {OneLine(ex.Message)}");
        }
        catch (TaskCanceledException) when (ct is not { IsCancellationRequested: true })
        {
            return Deny("Higgsfield timed out. You may retry.");
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Higgsfield tools/call {Tool} failed", tool);
            return Deny($"Internal proxy error: {OneLine(ex.Message)}");
        }
    }

    // ---- HTTP / SSE ----

    private async Task<JsonObject?> PostToHiggsfieldAsync(string token, JsonObject body,
        CancellationToken ct, int timeoutMs = 120_000)
    {
        var client = clientFactory.CreateClient(HttpClientName);
        client.Timeout = TimeSpan.FromMilliseconds(timeoutMs);
        var request = new HttpRequestMessage(HttpMethod.Post, UpstreamUrl)
        {
            Content = new StringContent(body.ToJsonString(JsonOpts),
                System.Text.Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        // MCP поверх Streamable HTTP требует Accept с обоими типами: без text/event-stream
        // апстрим вправе ответить 406 Not Acceptable (Higgsfield именно это и делал).
        request.Headers.Accept.ParseAdd("application/json");
        request.Headers.Accept.ParseAdd("text/event-stream");
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        using var stream = await response.Content.ReadAsStreamAsync(ct);
        var text = await new System.IO.StreamReader(stream).ReadToEndAsync(ct);
        if (!response.IsSuccessStatusCode)
        {
            log.LogWarning("Higgsfield responded HTTP {Status}: {Body}",
                (int)response.StatusCode, OneLine(text[..Math.Min(text.Length, 200)]));
            return null;
        }
        var data = ExtractSseData(text);
        if (data is null)
        {
            log.LogWarning("Higgsfield SSE: data line not found. Body: {Text}", OneLine(text));
            return null;
        }
        return data;
    }

    private async Task<IReadOnlyList<McpToolSchema>?> FetchToolsListAsync(CancellationToken ct)
    {
        var token = higgsfield.EnsureFresh();
        // EnsureFresh()==null — инстанс не подключён, никакого handshake не было;
        // статусы про это не пишем: McpStatusStore уже хранит Connected=false
        // (см. higgsfield.json/State), а Failed за «не зашли в сеть» — ложь.
        if (token is null) return null;
        var body = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = 0,
            ["method"] = "tools/list",
            ["params"] = new JsonObject(),
        };
        var bodyJson = body.ToJsonString(JsonOpts);

        var client = clientFactory.CreateClient(HttpClientName);
        // Канал до mcp.higgsfield.ai рвётся: замер 6 попыток подряд — 0.9 с / 17.8 с /
        // 19.9 с / >40 с. CLI спрашивает tools/list один раз при подъёме сервера; 15 с
        // не хватало — тулсет пропадал молча на весь ход. 40 с — запас под два плохих
        // ответа подряд, плюс один повтор ниже.
        client.Timeout = TimeSpan.FromSeconds(40);

        Exception? lastError = null;
        for (var attempt = 1; attempt <= 2; attempt++)
        {
            // HttpRequestMessage и его Content одноразовые — собираем заново на каждой попытке.
            var request = new HttpRequestMessage(HttpMethod.Post, UpstreamUrl)
            {
                Content = new StringContent(bodyJson, System.Text.Encoding.UTF8, "application/json"),
            };
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
            // MCP поверх Streamable HTTP требует Accept с обоими типами: без text/event-stream
            // апстрим вправе ответить 406 Not Acceptable (Higgsfield именно это и делал).
            request.Headers.Accept.ParseAdd("application/json");
            request.Headers.Accept.ParseAdd("text/event-stream");
            try
            {
                using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
                using var stream = await response.Content.ReadAsStreamAsync(ct);
                var text = await new System.IO.StreamReader(stream).ReadToEndAsync(ct);
                if (!response.IsSuccessStatusCode)
                {
                    var error = $"HTTP {(int)response.StatusCode}";
                    log.LogWarning("Higgsfield tools/list: {Error}", error);
                    RecordToolsListFailure(error);
                    return null;
                }
                var data = ExtractSseData(text);
                if (data?["result"] is not JsonObject result)
                {
                    // 200 ОК, но тело не JSON-RPC — апстрим отдал мусор. Это ОТКАЗ handshake,
                    // а не «пустой ответ». ADR-012 «тихо терять инструмент нельзя».
                    log.LogWarning("Higgsfield tools/list: 200 OK but no JSON-RPC result. Body: {Body}",
                        OneLine(text[..Math.Min(text.Length, 200)]));
                    RecordToolsListFailure("200 OK but no JSON-RPC result");
                    return null;
                }
                if (result["tools"] is not JsonArray tools)
                {
                    log.LogWarning("Higgsfield tools/list: result has no 'tools' array");
                    RecordToolsListFailure("result has no 'tools' array");
                    return null;
                }
                var filtered = new List<McpToolSchema>();
                foreach (var node in tools)
                {
                    if (node is not JsonObject t) continue;
                    var name = t["name"] is JsonValue nv ? nv.ToString() : null;
                    if (name is null || !WhitelistSet.Contains(name)) continue;
                    filtered.Add(new McpToolSchema(
                        name,
                        t["description"] is JsonValue dv ? dv.ToString() : "",
                        t["inputSchema"] as JsonObject ?? new JsonObject()));
                }
                if (filtered.Count == 0)
                {
                    // Апстрим ответил, но ни одного инструмента из белого списка. Аномалия:
                    // либо upstream резко сменил имена, либо версия схемы разъехалась.
                    // Без статуса наружу уйдёт пустой tools/list, и CLI покажет сервер
                    // как connected с нулём инструментов — то же «тихо терять инструмент нельзя».
                    log.LogWarning("Higgsfield tools/list: filtered list is empty (no whitelisted tools)");
                    RecordToolsListFailure("empty filtered list");
                    return null;
                }
                RecordToolsListSuccess();
                return filtered;
            }
            catch (TaskCanceledException) when (ct.IsCancellationRequested)
            {
                // Внешняя отмена (хост/вызов) — пробрасываем, это не наш таймаут.
                throw;
            }
            catch (Exception ex) when (ex is TaskCanceledException or HttpRequestException)
            {
                // Таймаут клиента (TaskCanceledException без внешней отмены) или сетевой сбой —
                // повторяем один раз. Без backoff: у апстрима случается длинная пауза в
                // середине ответа, и без задержки вторая попытка часто попадает в неё же.
                lastError = ex;
                log.LogWarning("Higgsfield tools/list: attempt {Attempt} failed: {Error}",
                    attempt, OneLine(ex.Message));
            }
        }
        var finalError = lastError is null ? "both attempts failed" : OneLine(lastError.Message);
        log.LogWarning(lastError, "Higgsfield tools/list: both attempts failed");
        RecordToolsListFailure(finalError);
        return null;
    }

    // Сюда сходятся все ветки отказа handshake: пишем статус в McpStatusStore,
    // чтобы CLI/UI не показывали сервер как connected с нулём инструментов.
    // OwnerId — фиксированный ServiceOwnerId: запись и секреты живут под ним,
    // а не под владельцем хода (см. HiggsfieldOAuthService — шаг 1 ADR-001).
    private void RecordToolsListFailure(string error)
    {
        if (mcpStatus is null) return; // unit-тесты без DI
        try
        {
            mcpStatus.RecordProbe(HiggsfieldOAuthService.ServiceOwnerId, HiggsfieldOAuthService.Key,
                McpServerStatuses.Failed, error);
        }
        catch (Exception ex)
        {
            // McpStatusStore сам логгирует; наш страх — не уронить caller
            log.LogWarning(ex, "Higgsfield tools/list: failed to record status");
        }
    }

    // Парный к RecordToolsListFailure: единственный путь «интеграция здорова». Без
    // него прошлая ошибка зависает в сторе вечно — до первой новой ошибки карточка
    // в UI остаётся красной. Симметричный контракт: тот же ServiceOwnerId/Key,
    // Source=Probe (природа наблюдения та же — наш сетевой запрос), error=null.
    // Зовётся из FetchToolsListAsync после фильтрации белого списка, чтобы
    // подъём с диска и возврат свежего кэша не плодили лишних записей.
    private void RecordToolsListSuccess()
    {
        if (mcpStatus is null) return;
        try
        {
            mcpStatus.RecordProbe(HiggsfieldOAuthService.ServiceOwnerId, HiggsfieldOAuthService.Key,
                McpServerStatuses.Connected, error: null);
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Higgsfield tools/list: failed to record success status");
        }
    }

    /// <summary>
    /// Извлекает JSON из SSE-ответа Higgsfield.
    /// PITFALL: если Higgsfield когда-либо введёт сессию, этот парсер сломается.
    /// </summary>
    internal static JsonObject? ExtractSseData(string text)
    {
        foreach (var line in text.Split('\n'))
        {
            var trimmed = line.TrimStart();
            if (!trimmed.StartsWith("data:", StringComparison.Ordinal)) continue;
            var json = trimmed["data:".Length..].Trim();
            if (json.Length == 0) continue;
            try
            {
                var node = JsonNode.Parse(json);
                if (node is JsonObject obj) return obj;
            }
            catch (JsonException) { }
        }
        return null;
    }

    private static McpToolCallResult ExtractResult(JsonObject rpc)
    {
        if (rpc["result"] is JsonObject result)
        {
            if (result["content"] is JsonArray content && content.Count > 0
                && content[0] is JsonObject first
                && first["text"] is JsonValue tv)
            {
                var isError = result["isError"] is JsonValue ev && ev.GetValue<bool>();
                return new McpToolCallResult(tv.ToString(), isError);
            }
            return new McpToolCallResult(result.ToJsonString());
        }
        if (rpc["error"] is JsonObject error)
        {
            var msg = error["message"] is JsonValue mv ? mv.ToString() : "Unknown Higgsfield error";
            return Deny($"Higgsfield: {msg}");
        }
        return Deny("Higgsfield returned an unexpected response.");
    }

    private bool TryResolveSession(McpToolCallContext context, out Models.Session? session)
    {
        session = null;
        // Fail-closed без SessionManager: unit-тесты передают null, и попытка
        // GetOwned ниже упала бы NRE. На проде sessions всегда заданы DI,
        // и null здесь означает «контейнер не собрали» — это и есть отказ.
        if (sessions is null) return false;
        if (!TryParseRoute(context.RouteTail, out var sessionId))
            return false;
        session = sessions.GetOwned(sessionId, context.OwnerId);
        return session is not null;
    }

    private static bool TryParseRoute(string? route, out string sessionId)
    {
        sessionId = "";
        if (route is null || route.Split('/').Length != 1) return false;
        if (route.Length is < 1 or > 128
            || !route.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_'))
            return false;
        sessionId = route;
        return true;
    }

    /// <summary>URL эндпоинта для конфига хода: /mcp/higgsfield/{sessionId}.</summary>
    public static string EndpointFor(string apiUrl, string sessionId) =>
        McpEndpoints.EndpointFor(apiUrl, ServerName) + "/" + sessionId;

    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web)
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    // ---- Снимок на диск ----

    // Резолв data/ по образцу PromptSnapshotStore/McpStatusStore: берём каталог из
    // DataPath (по дефолту — рядом с data/projects.json). Ошибка чтения/парсинга
    // снимка возвращает null — это нормальный fail-open (см. JsonFileStore.Load).
    // null config (например в дешёвом юните) → дефолт под AppContext.BaseDirectory,
    // конструктор всё равно не должен падать.
    private static string ResolveSnapshotPath(IConfiguration? config)
    {
        var dataPath = config?["DataPath"]
            ?? Path.Combine(AppContext.BaseDirectory, "data", "projects.json");
        var dataDir = Path.GetDirectoryName(dataPath)
            ?? Path.Combine(AppContext.BaseDirectory, "data");
        return Path.Combine(dataDir, SnapshotFileName);
    }

    /// <summary>
    /// Снимок с диска: новый формат — объект <c>{ savedAt, tools }</c>; старый формат —
    /// массив инструментов (обратная совместимость, <c>SavedAt</c> в этом случае null).
    /// Старый формат в конструкторе трактуется как «только что сохранённый» (см. ctor),
    /// иначе выкатка сломала бы существующие инсталляции до первого успешного tools/list.
    /// </summary>
    private sealed class SnapshotSnapshot
    {
        public DateTime? SavedAt { get; set; }
        public List<McpToolSchema>? Tools { get; set; }
    }

    private SnapshotSnapshot? LoadSnapshotFromDisk(string path)
    {
        var loaded = JsonFileStore.Load<SnapshotSnapshot>(path, JsonOpts, log);
        if (loaded is null) return null;
        if (loaded.Tools is { Count: > 0 }) return loaded;
        // Обратная совместимость: массив напрямую (старый формат).
        var legacy = JsonFileStore.Load<List<McpToolSchema>>(path, JsonOpts, log);
        if (legacy is null || legacy.Count == 0) return null;
        return new SnapshotSnapshot { SavedAt = null, Tools = legacy };
    }

    private void SaveSnapshot(IReadOnlyList<McpToolSchema> tools)
    {
        try
        {
            var payload = new SnapshotSnapshot
            {
                SavedAt = DateTime.UtcNow,
                Tools = tools.ToList(),
            };
            JsonFileStore.Save(_snapshotPath, payload, JsonOpts);
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Higgsfield tools/list: snapshot save failed");
        }
    }

    private static McpToolCallResult Deny(string text) => new(text, IsError: true);
    private static string OneLine(string text) => text.Replace("\r", " ").Replace("\n", " ");
}