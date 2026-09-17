using System.Text.Json;
using System.Text.Json.Nodes;
using ClaudeHomeServer.Models;

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
        // Холодный старт: поднимаем снимок с диска, чтобы первый ход после выкатки
        // жил на нём 30 мин, а не лез в сеть. _cachedAt = now — снимок считается
        // свежим; иначе при первом же вызове GetCachedTools он истёк бы и пошёл в сеть.
        var loaded = LoadSnapshotFromDisk(_snapshotPath);
        _cachedTools = loaded;
        _cachedAt = loaded is null ? DateTime.MinValue : DateTime.UtcNow;
    }

    public const string ServerName = McpEndpoints.HiggsfieldName;

    /// <summary>Имя HTTP-клиента для тихих запросов к Higgsfield.</summary>
    public const string HttpClientName = "higgsfield-mcp";

    /// <summary>Базовый URL эндпоинта Higgsfield MCP.</summary>
    public const string UpstreamUrl = HiggsfieldOAuthService.Url;

    /// <summary>TTL кэша списка tools/list: 30 минут.</summary>
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(30);

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

    // Кэш: последний успешный снимок + время. Fail-open при отказе обновления.
    // При пуске процесса _cachedTools поднимается со снимка data/higgsfield-tools.json,
    // если он есть — иначе первый ход после рестарта лотерея на рваном канале.
    private readonly object _cacheLock = new();
    private IReadOnlyList<McpToolSchema>? _cachedTools;
    private DateTime _cachedAt;
    private readonly string _snapshotPath;

    // ---- Состав ----

    public IReadOnlyList<McpToolSchema> ToolsFor(McpToolCallContext context)
    {
        if (!TryResolveSession(context, out _)) return [];
        return GetCachedTools();
    }

    /// <summary>
    /// Принудительный прогрев снимка: для <see cref="IHostedService"/> стартёра. Идёт
    /// той же дорогой, что и GetCachedTools (та же <see cref="FetchToolsListAsync"/>,
    /// тот же WARN на отказе, та же запись на диск при успехе), но снаружи лока —
    /// ходы не должны ловить «не успели обновить кэш» как свою ошибку.
    /// Возвращает true, если снимок обновлён.
    /// </summary>
    public async Task<bool> RefreshNowAsync(CancellationToken ct)
    {
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
            _cachedAt = DateTime.UtcNow;
        }
        try { SaveSnapshot(fresh); }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Higgsfield warmer: snapshot save failed");
        }
        return true;
    }

    private IReadOnlyList<McpToolSchema> GetCachedTools()
    {
        // Решаем «надо ли обновлять» под локом; сам сетевой вызов идёт ВНЕ лока.
        // ОСОЗНАННЫЙ отказ от single-flight: при холодном кэше N ходов пошлют N запросов.
        // Это выбор, а не побочка: tools/list — handshake, не горячий путь; на рваном канале
        // параллель полезна (быстрее кто-то да ответит); семафор ради handshake — overkill.
        var expired = false;
        lock (_cacheLock)
        {
            expired = _cachedAt + CacheTtl < DateTime.UtcNow;
            if (!expired) return _cachedTools ?? [];
        }

        // Сетевой вызов вне лока: следующий ход пройдёт под локом независимо от того,
        // сколько сейчас идёт обновлений, и при таймауте вернётся к свежему fail-open.
        IReadOnlyList<McpToolSchema>? fresh = null;
        try
        {
            fresh = FetchToolsListAsync(CancellationToken.None).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Higgsfield tools/list: refresh failed - fail-open on last snapshot");
        }
        if (fresh is not null)
        {
            lock (_cacheLock)
            {
                _cachedTools = fresh;
                _cachedAt = DateTime.UtcNow;
            }
            // Снимок на диск — в фоне, чтобы ход не ждал записи. Ошибка записи НЕ
            // пробрасывается: лучше работать без снимка, чем уронить ход. При следующем
            // успешном tools/list файл перезапишется.
            _ = Task.Run(() => SaveSnapshot(fresh));
        }
        lock (_cacheLock)
        {
            return _cachedTools ?? [];
        }
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
        var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
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
            try
            {
                var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
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

    private IReadOnlyList<McpToolSchema>? LoadSnapshotFromDisk(string path)
    {
        var loaded = JsonFileStore.Load<List<McpToolSchema>>(path, JsonOpts, log);
        if (loaded is null || loaded.Count == 0) return null;
        return loaded;
    }

    private void SaveSnapshot(IReadOnlyList<McpToolSchema> tools)
    {
        try
        {
            JsonFileStore.Save(_snapshotPath, tools.ToList(), JsonOpts);
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Higgsfield tools/list: snapshot save failed");
        }
    }

    private static McpToolCallResult Deny(string text) => new(text, IsError: true);
    private static string OneLine(string text) => text.Replace("\r", " ").Replace("\n", " ");
}
