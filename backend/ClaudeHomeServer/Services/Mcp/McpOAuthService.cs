using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using ClaudeHomeServer.Models;

namespace ClaudeHomeServer.Services.Mcp;

/// <summary>Что отдаём фронту после старта входа: адрес окна провайдера и ключ pending-записи.</summary>
public sealed record McpOAuthStart(string AuthorizeUrl, string State, string RedirectUri);

/// <summary>Итог обмена кода: ключ сервера нужен странице callback, чтобы адресовать postMessage.</summary>
public sealed record McpOAuthCompleted(string ServerId, string ServerKey, string RedirectUri);

/// <summary>Беда, о которой человеку надо сказать словами (400 или страница callback).</summary>
public class McpOAuthException(string message) : Exception(message);

/// <summary>
/// OAuth 2.1 для внешних MCP-серверов по спеке MCP (2025-03-26 / 2025-06-18):
/// discovery (401 → WWW-Authenticate → protected-resource → authorization-server),
/// Dynamic Client Registration (RFC 7591), Authorization Code + PKCE S256 + resource
/// (RFC 8707), обновление токена перед ходом.
///
/// Топология продукта — бэкенд на хосте, UI открывается откуда угодно, — упирается
/// в redirect_uri: он обязан совпасть у трёх сторон (DCR, /authorize, обмен кода).
/// Канонический адрес берём из <c>Mcp:PublicBaseUrl</c>, а без настройки — origin
/// того запроса, которым человек нажал «Войти»: между стартом и callback он меняться
/// не может, и сервер сверяет точное совпадение.
///
/// Токены наружу не выходят никогда: в реестре лежит ссылка на запись McpSecretStore,
/// DTO отдаёт только срок и признак «токены есть» (McpServerMapper).
/// </summary>
public class McpOAuthService(
    McpRegistry registry,
    McpSecretStore secrets,
    McpStatusStore statuses,
    IHttpClientFactory httpFactory,
    IConfiguration config,
    ILogger<McpOAuthService> log)
{
    /// <summary>Имя тихого HTTP-клиента: чужой authorization server лежит штатно.</summary>
    public const string HttpClientName = "mcp-oauth";

    /// <summary>Путь страницы, на которую провайдер возвращает код.</summary>
    public const string CallbackPath = "/api/mcp/oauth/callback";

    // Окно, за которое человек успевает пройти вход у провайдера
    private static readonly TimeSpan PendingTtl = TimeSpan.FromMinutes(10);

    // Рефрешим не в момент истечения, а чуть раньше: ход только стартует, и токен,
    // живущий последние секунды, протухнет прямо посреди работы CLI
    private static readonly TimeSpan RefreshMargin = TimeSpan.FromMinutes(1);

    private readonly TimeSpan _timeout = TimeSpan.FromSeconds(
        Math.Clamp(config.GetValue("Mcp:OAuthTimeoutSeconds", 20), 1, 120));

    // Discovery обходит несколько адресов подряд (проба 401 + well-known кандидаты ресурса
    // и authorization server) — без своего таймаута на попытку общий таймаут одного запроса
    // (_timeout выше) складывается по недоступному хосту в минуты. Общий потолок на всю
    // цепочку — на случай, если кандидатов набежит больше, чем закладывались, по образцу
    // Mcp:ProbeTimeoutSeconds у McpProbeService
    private readonly TimeSpan _discoveryTimeout = TimeSpan.FromSeconds(
        Math.Clamp(config.GetValue("Mcp:OAuthDiscoveryTimeoutSeconds", 5), 1, 30));

    private readonly TimeSpan _discoveryOverallTimeout = TimeSpan.FromSeconds(
        Math.Clamp(config.GetValue("Mcp:OAuthDiscoveryOverallTimeoutSeconds", 20), 5, 60));

    private readonly string? _publicBaseUrl = config["Mcp:PublicBaseUrl"]?.TrimEnd('/');

    // state → незавершённый вход. Только в памяти: рестарт сервера обрывает вход, и это
    // честно — verifier переживать рестарт не должен
    private readonly ConcurrentDictionary<string, Pending> _pending = new(StringComparer.Ordinal);

    // Один рефреш на (владелец, сервер): параллельные ходы иначе сожгут refresh-токен
    // друг друга — часть серверов выдаёт его одноразовым
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _refreshGates = new(StringComparer.Ordinal);

    private sealed record Pending(
        string OwnerId, string ServerId, string Verifier, string RedirectUri,
        string TokenEndpoint, string ClientId, string? ClientSecretRef, string Resource,
        DateTime ExpiresAt);

    // ── redirect_uri ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Адрес возврата: настроенный публичный URL либо origin запроса. Настройка сильнее —
    /// на удалённом доступе UI и бэкенд смотрят наружу под одним именем, а origin вкладки
    /// может оказаться туннелем, о котором провайдер не знает.
    /// </summary>
    public string ResolveRedirectUri(string requestOrigin) =>
        (string.IsNullOrWhiteSpace(_publicBaseUrl) ? requestOrigin.TrimEnd('/') : _publicBaseUrl) + CallbackPath;

    // ── старт входа ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// Готовит вход: discovery, при необходимости DCR, сохранение настроек клиента
    /// в записи и pending-запись под state. Возвращает адрес окна провайдера.
    /// </summary>
    public async Task<McpOAuthStart> StartAsync(string ownerId, McpServerRecord record,
        string redirectUri, McpOAuthClientInput? input, CancellationToken ct = default)
    {
        // Свой адрес возврата — для серверов, которые принимают только http://127.0.0.1:PORT/…:
        // до нашего callback код не доедет, человек копирует его из адресной строки
        // и заканчивает вход через POST …/oauth/complete
        if (Trim(input?.RedirectUri) is { } custom) redirectUri = custom;
        if (record.Transport == McpTransport.Stdio)
            throw new McpOAuthException("OAuth есть только у http/sse-серверов");
        if (string.IsNullOrWhiteSpace(record.Url)
            || !Uri.TryCreate(record.Url, UriKind.Absolute, out var serverUrl))
            throw new McpOAuthException("У записи не задан адрес сервера");

        CleanupPending();

        var oauth = record.Auth.OAuth ?? new McpOAuthConfig();
        var (issuer, endpoints) = await DiscoverWithinBudgetAsync(serverUrl, oauth, ct);

        var clientId = Trim(input?.ClientId) ?? Trim(oauth.ClientId);
        var clientSecretRef = oauth.ClientSecretRef;
        if (Trim(input?.ClientSecret) is { } freshSecret)
            clientSecretRef = secrets.Set(ownerId, freshSecret);

        // Ручной client_id — для серверов, которые DCR не умеют; иначе регистрируемся сами
        if (clientId is null)
        {
            var registered = await RegisterClientAsync(endpoints, redirectUri, input?.Scopes ?? oauth.Scopes, ct);
            clientId = registered.ClientId;
            if (registered.ClientSecret is { Length: > 0 } secret)
                clientSecretRef = secrets.SetEntry(ownerId, McpSecretEntry.Plain(secret), clientSecretRef);
        }

        var scopes = input?.Scopes ?? oauth.Scopes
            ?? (endpoints.ScopesSupported.Count > 0 ? endpoints.ScopesSupported.ToList() : null);

        // Настройки клиента переживают неудачный вход: повторный «Войти» не станет
        // регистрировать в провайдере ещё одного клиента
        var updated = SaveAuth(ownerId, record, auth =>
        {
            auth.Kind = McpAuthKind.OAuth2;
            auth.OAuth = new McpOAuthConfig
            {
                AuthorizationServer = issuer.ToString(),
                TokenEndpoint = endpoints.TokenEndpoint,
                ClientId = clientId,
                ClientSecretRef = clientSecretRef,
                Scopes = scopes,
                AccessTokenRef = oauth.AccessTokenRef,
                ExpiresAt = oauth.ExpiresAt,
                RedirectUri = redirectUri,
            };
        });

        var state = McpPkce.CreateState();
        var verifier = McpPkce.CreateVerifier();
        var resource = CanonicalResource(serverUrl);
        _pending[state] = new Pending(ownerId, updated.Id, verifier, redirectUri,
            endpoints.TokenEndpoint, clientId!, clientSecretRef, resource,
            DateTime.UtcNow.Add(PendingTtl));

        var query = new Dictionary<string, string?>
        {
            ["response_type"] = "code",
            ["client_id"] = clientId,
            ["redirect_uri"] = redirectUri,
            ["state"] = state,
            ["code_challenge"] = McpPkce.Challenge(verifier),
            ["code_challenge_method"] = "S256",
            ["resource"] = resource,
            ["scope"] = scopes is { Count: > 0 } ? string.Join(' ', scopes) : null,
        };
        return new McpOAuthStart(AppendQuery(endpoints.AuthorizationEndpoint, query), state, redirectUri);
    }

    // ── завершение входа ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Меняет код на токены. <paramref name="expectedOwnerId"/> задан — путь ручной вставки
    /// кода, и state обязан принадлежать этому владельцу; callback провайдера приходит без
    /// JWT, там авторизация — сам state (непредсказуемый и одноразовый).
    /// </summary>
    public async Task<McpOAuthCompleted> CompleteAsync(string? state, string? code,
        string? expectedOwnerId = null, string? expectedServerId = null, string? arrivedAt = null,
        CancellationToken ct = default)
    {
        CleanupPending();
        if (string.IsNullOrWhiteSpace(state) || string.IsNullOrWhiteSpace(code))
            throw new McpOAuthException("Провайдер вернул неполный ответ — повтори вход");
        // Одноразовость: код повторно не примут, а зависшая запись — лишний verifier в памяти
        if (!_pending.TryRemove(state, out var pending))
            throw new McpOAuthException("Вход не найден или истёк — начни заново");
        if (pending.ExpiresAt <= DateTime.UtcNow)
            throw new McpOAuthException("Вход истёк — начни заново");
        // Чужой владелец или чужая запись — проверяем ДО обмена: код одноразовый, и после
        // обмена «не тот сервер» означало бы уже записанные не туда токены
        if (expectedOwnerId is not null && !string.Equals(expectedOwnerId, pending.OwnerId, StringComparison.Ordinal))
            throw new McpOAuthException("Вход не найден или истёк — начни заново");
        // Адрес возврата между стартом и callback меняться не может. Сверяем только когда
        // он выведен из origin запроса: при настроенном Mcp:PublicBaseUrl бэкенд может
        // стоять за прокси, и фактический Host законно отличается от публичного имени
        if (arrivedAt is not null && string.IsNullOrWhiteSpace(_publicBaseUrl)
            && !string.Equals(arrivedAt, pending.RedirectUri, StringComparison.OrdinalIgnoreCase))
            throw new McpOAuthException("Ответ пришёл на другой адрес — начни вход заново");
        if (expectedServerId is not null && !string.Equals(expectedServerId, pending.ServerId, StringComparison.Ordinal))
            throw new McpOAuthException("Код относится к другому серверу — начни вход заново");

        var record = registry.Get(pending.OwnerId, pending.ServerId)
            ?? throw new McpOAuthException("Сервер удалён, пока шёл вход");

        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = pending.RedirectUri,
            ["client_id"] = pending.ClientId,
            ["code_verifier"] = pending.Verifier,
            ["resource"] = pending.Resource,
        };
        var token = await RequestTokenAsync(pending.TokenEndpoint, form,
            pending.OwnerId, pending.ClientSecretRef, ct);

        var saved = StoreTokens(pending.OwnerId, record, token);
        statuses.Remove(pending.OwnerId, saved.Key); // прежнее «нужен вход» больше не правда
        return new McpOAuthCompleted(saved.Id, saved.Key, pending.RedirectUri);
    }

    // ── обновление токена ────────────────────────────────────────────────────────────

    /// <summary>
    /// Актуальная запись с живым токеном; null — нужен вход (токенов нет либо рефреш
    /// провалился). Вызывается перед сборкой конфига хода и перед пробой: молчаливая
    /// работа с протухшим токеном давала бы 401 у каждого инструмента.
    /// </summary>
    public async Task<McpServerRecord?> EnsureFreshAsync(string ownerId, McpServerRecord record,
        CancellationToken ct = default)
    {
        if (record.Auth.Kind != McpAuthKind.OAuth2) return record;
        if (!NeedsRefresh(ownerId, record, out var entry))
            return entry is null ? MarkNeedsAuth(ownerId, record, "Сервер не авторизован — нужен вход") : record;

        var gate = _refreshGates.GetOrAdd(ownerId + "\n" + record.Id, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            // Пока ждали замок, соседний ход мог обновить токен — перечитываем запись
            var current = registry.Get(ownerId, record.Id) ?? record;
            if (!NeedsRefresh(ownerId, current, out var fresh))
                return fresh is null ? MarkNeedsAuth(ownerId, current, "Сервер не авторизован — нужен вход") : current;

            var oauth = current.Auth.OAuth!;
            if (fresh!.RefreshToken is not { Length: > 0 } refreshToken)
                return MarkNeedsAuth(ownerId, current, "Срок токена истёк, обновить нечем — нужен вход");
            if (oauth.TokenEndpoint is not { Length: > 0 } tokenEndpoint
                || oauth.ClientId is not { Length: > 0 } clientId)
                return MarkNeedsAuth(ownerId, current, "Настройки OAuth неполны — нужен вход");

            var form = new Dictionary<string, string>
            {
                ["grant_type"] = "refresh_token",
                ["refresh_token"] = refreshToken,
                ["client_id"] = clientId,
            };
            if (current.Url is { Length: > 0 } url && Uri.TryCreate(url, UriKind.Absolute, out var serverUrl))
                form["resource"] = CanonicalResource(serverUrl);
            if (oauth.Scopes is { Count: > 0 } scopes) form["scope"] = string.Join(' ', scopes);

            try
            {
                var token = await RequestTokenAsync(tokenEndpoint, form, ownerId, oauth.ClientSecretRef, ct);
                // Провайдер вправе не прислать новый refresh — старый остаётся в силе
                return StoreTokens(ownerId, current, token with { RefreshToken = token.RefreshToken ?? refreshToken });
            }
            catch (McpOAuthException ex)
            {
                log.LogWarning("Токен MCP-сервера «{Key}» не обновился: {Error}", current.Key, ex.Message);
                return MarkNeedsAuth(ownerId, current, "Не удалось обновить токен — нужен вход");
            }
        }
        finally { gate.Release(); }
    }

    /// <summary>
    /// Синхронная обёртка для сборки конфига хода (провайдер там синхронный). Ход и так
    /// стартует секундами, а без свежего токена сервер всё равно пришлось бы снять.
    /// </summary>
    public McpServerRecord? EnsureFresh(string ownerId, McpServerRecord record)
    {
        if (record.Auth.Kind != McpAuthKind.OAuth2) return record;
        try
        {
            using var cts = new CancellationTokenSource(_timeout + TimeSpan.FromSeconds(5));
            return EnsureFreshAsync(ownerId, record, cts.Token).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Обновление токена MCP-сервера «{Key}» сорвалось", record.Key);
            return MarkNeedsAuth(ownerId, record, "Не удалось обновить токен — нужен вход");
        }
    }

    // Токен есть и живёт — рефреш не нужен. entry = null означает «токенов нет вовсе»
    private bool NeedsRefresh(string ownerId, McpServerRecord record, out McpSecretEntry? entry)
    {
        entry = record.Auth.OAuth?.AccessTokenRef is { Length: > 0 } tokenRef
            ? secrets.ResolveEntry(ownerId, tokenRef) : null;
        if (entry?.Value is not { Length: > 0 }) { entry = null; return false; }
        var expires = entry.ExpiresAt ?? record.Auth.OAuth?.ExpiresAt;
        return expires is not null && expires <= DateTime.UtcNow.Add(RefreshMargin);
    }

    // «Нужен вход» — видимое состояние, а не тихий пропуск: плитка сервера должна гореть
    private McpServerRecord? MarkNeedsAuth(string ownerId, McpServerRecord record, string error)
    {
        statuses.RecordAuthFailure(ownerId, record.Key, error);
        return null;
    }

    // ── discovery ────────────────────────────────────────────────────────────────────

    // Общий потолок на всю цепочку discovery. Каждый шаг внутри (GetJsonAsync,
    // ProbeResourceMetadataUrlAsync) сам глотает сетевые ошибки и таймауты попытки и просто
    // переходит к следующему кандидату — при недоступном хосте DiscoverAsync поэтому не
    // бросает исключение, а тихо доезжает до дефолтных путей спеки. Раз оборвались по этому
    // потолку — значит хост не отвечал вовсе, и вместо того, чтобы тащить угаданные (и заведомо
    // недостижимые) пути дальше в DCR, говорим человеку правду сразу
    private async Task<(Uri Issuer, McpOAuthEndpoints Endpoints)> DiscoverWithinBudgetAsync(
        Uri serverUrl, McpOAuthConfig oauth, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(_discoveryOverallTimeout);
        var result = await DiscoverAsync(serverUrl, oauth, cts.Token);
        if (cts.IsCancellationRequested && !ct.IsCancellationRequested)
            throw new McpOAuthException(
                $"Сервер авторизации не отвечает (проверка адресов заняла больше {_discoveryOverallTimeout.TotalSeconds:0} с)");
        return result;
    }

    // Адрес authorization server и его эндпоинты. Уже найденный issuer не ищем заново:
    // повторный вход после отзыва доступа не должен зависеть от того, отвечает ли сервер 401
    private async Task<(Uri Issuer, McpOAuthEndpoints Endpoints)> DiscoverAsync(
        Uri serverUrl, McpOAuthConfig oauth, CancellationToken ct)
    {
        var issuer = await FindIssuerAsync(serverUrl, oauth, ct);
        foreach (var candidate in McpOAuthDiscovery.AuthorizationServerCandidates(issuer))
        {
            var metadata = await GetJsonAsync(candidate, ct);
            if (metadata is { } json) return (issuer, McpOAuthDiscovery.EndpointsFrom(json, issuer));
        }
        // Метаданных нет — по спеке остаются дефолтные пути
        return (issuer, McpOAuthDiscovery.DefaultEndpoints(issuer));
    }

    private async Task<Uri> FindIssuerAsync(Uri serverUrl, McpOAuthConfig oauth, CancellationToken ct)
    {
        if (oauth.AuthorizationServer is { Length: > 0 } known
            && Uri.TryCreate(known, UriKind.Absolute, out var knownUri)) return knownUri;

        var metadataUrl = await ProbeResourceMetadataUrlAsync(serverUrl, ct);
        var candidates = metadataUrl is null
            ? McpOAuthDiscovery.ProtectedResourceCandidates(serverUrl)
            : [metadataUrl, .. McpOAuthDiscovery.ProtectedResourceCandidates(serverUrl)];

        foreach (var candidate in candidates)
        {
            if (await GetJsonAsync(candidate, ct) is not { } json) continue;
            if (McpOAuthDiscovery.AuthorizationServerFrom(json) is not { } server) continue;
            if (Uri.TryCreate(server, UriKind.Absolute, out var uri)) return uri;
        }
        // Метаданных ресурса нет — считаем сам сервер и его authorization server одним хостом
        return new Uri(serverUrl.GetLeftPart(UriPartial.Authority));
    }

    // 401 от самого MCP-сервера: в WWW-Authenticate лежит адрес метаданных ресурса
    private async Task<string?> ProbeResourceMetadataUrlAsync(Uri serverUrl, CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, serverUrl)
            {
                Content = new StringContent(McpProbeProtocol.InitializeRequest(), Encoding.UTF8, "application/json"),
            };
            request.Headers.TryAddWithoutValidation("Accept", "application/json, text/event-stream");
            using var response = await DiscoveryClient().SendAsync(request, ct);
            if (response.StatusCode is not (HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)) return null;
            return McpOAuthDiscovery.ResourceMetadataFrom(
                response.Headers.WwwAuthenticate.Select(h => h.ToString()));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return null; // молчащий сервер не отменяет вход — идём по дефолтным путям
        }
    }

    private async Task<JsonElement?> GetJsonAsync(string url, CancellationToken ct)
    {
        try
        {
            using var response = await DiscoveryClient().GetAsync(url, ct);
            if (!response.IsSuccessStatusCode) return null;
            var body = await response.Content.ReadAsStringAsync(ct);
            using var document = JsonDocument.Parse(body);
            return document.RootElement.Clone();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return null;
        }
    }

    // ── регистрация клиента (RFC 7591) ───────────────────────────────────────────────

    private sealed record RegisteredClient(string ClientId, string? ClientSecret);

    private async Task<RegisteredClient> RegisterClientAsync(McpOAuthEndpoints endpoints,
        string redirectUri, IReadOnlyList<string>? scopes, CancellationToken ct)
    {
        if (endpoints.RegistrationEndpoint is not { Length: > 0 } endpoint)
            throw new McpOAuthException(
                "Сервер не поддерживает автоматическую регистрацию — впиши client_id вручную");

        var payload = new Dictionary<string, object?>
        {
            ["client_name"] = "AI Home",
            ["redirect_uris"] = new[] { redirectUri },
            ["grant_types"] = new[] { "authorization_code", "refresh_token" },
            ["response_types"] = new[] { "code" },
            // Публичный клиент с PKCE: секрет бэкенду не нужен, а серверы, которые всё же
            // его выдают, присылают его в ответе — тогда используем client_secret_post
            ["token_endpoint_auth_method"] = "none",
            ["scope"] = scopes is { Count: > 0 } ? string.Join(' ', scopes) : null,
        };

        HttpResponseMessage response;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
            {
                Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"),
            };
            response = await Client().SendAsync(request, ct);
        }
        // TaskCanceledException от таймаута несёт бесполезное для человека «A task was
        // canceled» — называем настоящую причину, а не пересказываем исключение
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new McpOAuthException("Сервер авторизации не отвечает — регистрация клиента не выполнена");
        }
        catch (HttpRequestException ex)
        {
            throw new McpOAuthException("Регистрация клиента не удалась: " + ex.Message);
        }

        using (response)
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode)
                throw new McpOAuthException(
                    $"Сервер отказал в регистрации клиента ({(int)response.StatusCode}) — впиши client_id вручную");
            try
            {
                using var document = JsonDocument.Parse(body);
                var root = document.RootElement;
                if (!root.TryGetProperty("client_id", out var id) || id.ValueKind != JsonValueKind.String)
                    throw new McpOAuthException("Сервер не вернул client_id — впиши его вручную");
                var secret = root.TryGetProperty("client_secret", out var s) && s.ValueKind == JsonValueKind.String
                    ? s.GetString() : null;
                return new RegisteredClient(id.GetString()!, secret);
            }
            catch (JsonException)
            {
                throw new McpOAuthException("Ответ регистрации не разобран — впиши client_id вручную");
            }
        }
    }

    // ── токены ───────────────────────────────────────────────────────────────────────

    /// <summary>Ответ token endpoint. Наружу не выходит — живёт до записи в McpSecretStore.</summary>
    private sealed record TokenResponse(string AccessToken, string? RefreshToken,
        DateTime? ExpiresAt, string? Scope, string? TokenType);

    private async Task<TokenResponse> RequestTokenAsync(string tokenEndpoint,
        Dictionary<string, string> form, string ownerId, string? clientSecretRef, CancellationToken ct)
    {
        if (clientSecretRef is { Length: > 0 } && secrets.Resolve(ownerId, clientSecretRef) is { Length: > 0 } secret)
            form["client_secret"] = secret;

        HttpResponseMessage response;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, tokenEndpoint)
            {
                Content = new FormUrlEncodedContent(form),
            };
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            response = await Client().SendAsync(request, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            throw new McpOAuthException("Сервер токенов недоступен: " + ex.Message);
        }

        using (response)
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode)
                throw new McpOAuthException(
                    $"Сервер отказал в выдаче токена ({(int)response.StatusCode}): {ErrorFrom(body)}");
            try
            {
                using var document = JsonDocument.Parse(body);
                var root = document.RootElement;
                if (!root.TryGetProperty("access_token", out var access) || access.ValueKind != JsonValueKind.String)
                    throw new McpOAuthException("Сервер не вернул access_token");
                DateTime? expiresAt = root.TryGetProperty("expires_in", out var expires)
                                      && expires.TryGetInt32(out var seconds) && seconds > 0
                    ? DateTime.UtcNow.AddSeconds(seconds) : null;
                return new TokenResponse(
                    access.GetString()!,
                    Str(root, "refresh_token"), expiresAt, Str(root, "scope"), Str(root, "token_type"));
            }
            catch (JsonException)
            {
                throw new McpOAuthException("Ответ сервера токенов не разобран");
            }
        }

        static string? Str(JsonElement node, string name) =>
            node.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

        static string ErrorFrom(string body)
        {
            try
            {
                using var document = JsonDocument.Parse(body);
                var root = document.RootElement;
                var error = root.TryGetProperty("error_description", out var d) && d.ValueKind == JsonValueKind.String
                    ? d.GetString()
                    : root.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.String
                        ? e.GetString() : null;
                return error ?? "ответ без описания";
            }
            catch (JsonException) { return "ответ без описания"; }
        }
    }

    // Токены — в стор (ссылка сохраняется, чтобы рефреш не правил реестр), срок и scope —
    // в запись. AuthVersion растёт внутри registry.Update: заголовок запекается в конфиг
    // на старте CLI, и без смены сигнатуры живой процесс остался бы со старым токеном.
    private McpServerRecord StoreTokens(string ownerId, McpServerRecord record, TokenResponse token)
    {
        var oauth = record.Auth.OAuth ?? new McpOAuthConfig();
        var tokenRef = secrets.SetEntry(ownerId, new McpSecretEntry
        {
            Value = token.AccessToken,
            RefreshToken = token.RefreshToken,
            ExpiresAt = token.ExpiresAt,
            Scope = token.Scope,
            TokenType = token.TokenType,
        }, oauth.AccessTokenRef);

        return SaveAuth(ownerId, record, auth =>
        {
            auth.Kind = McpAuthKind.OAuth2;
            auth.OAuth = new McpOAuthConfig
            {
                AuthorizationServer = oauth.AuthorizationServer,
                TokenEndpoint = oauth.TokenEndpoint,
                ClientId = oauth.ClientId,
                ClientSecretRef = oauth.ClientSecretRef,
                Scopes = token.Scope is { Length: > 0 }
                    ? token.Scope.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList()
                    : oauth.Scopes,
                AccessTokenRef = tokenRef,
                ExpiresAt = token.ExpiresAt,
                RedirectUri = oauth.RedirectUri,
            };
        });
    }

    // Правка авторизации записи через штатный Update — он же поднимает AuthVersion.
    // Черновик собираем копией: Update заменяет запись целиком, и урезанный черновик
    // стёр бы env, headers и флаги доступности.
    private McpServerRecord SaveAuth(string ownerId, McpServerRecord record, Action<McpAuthConfig> apply)
    {
        var draft = JsonSerializer.Deserialize<McpServerRecord>(JsonSerializer.Serialize(record))!;
        draft.Auth ??= new McpAuthConfig();
        apply(draft.Auth);
        try
        {
            return registry.Update(ownerId, record.Id, draft)
                   ?? throw new McpOAuthException("Сервер удалён, пока шёл вход");
        }
        catch (InvalidOperationException ex)
        {
            throw new McpOAuthException(ex.Message);
        }
    }

    // ── мелочи ───────────────────────────────────────────────────────────────────────

    private HttpClient Client()
    {
        var client = httpFactory.CreateClient(HttpClientName);
        client.Timeout = _timeout;
        return client;
    }

    // Короткий таймаут одной попытки discovery — _discoveryTimeout, а не общий _timeout
    private HttpClient DiscoveryClient()
    {
        var client = httpFactory.CreateClient(HttpClientName);
        client.Timeout = _discoveryTimeout;
        return client;
    }

    // Идентификатор ресурса (RFC 8707) — адрес MCP-сервера без фрагмента и без query
    private static string CanonicalResource(Uri serverUrl) =>
        serverUrl.GetLeftPart(UriPartial.Path).TrimEnd('/');

    private static string AppendQuery(string url, Dictionary<string, string?> query)
    {
        var parts = query
            .Where(kv => !string.IsNullOrEmpty(kv.Value))
            .Select(kv => Uri.EscapeDataString(kv.Key) + "=" + Uri.EscapeDataString(kv.Value!));
        return url + (url.Contains('?') ? "&" : "?") + string.Join("&", parts);
    }

    private static string? Trim(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private void CleanupPending()
    {
        var now = DateTime.UtcNow;
        foreach (var (state, pending) in _pending)
            if (pending.ExpiresAt <= now) _pending.TryRemove(state, out _);
    }
}

/// <summary>
/// Ручные настройки из формы: client_id/secret — для серверов, которые DCR не умеют,
/// RedirectUri — для тех, кто принимает только loopback-адрес.
/// </summary>
public sealed record McpOAuthClientInput(string? ClientId, string? ClientSecret,
    List<string>? Scopes, string? RedirectUri = null);
