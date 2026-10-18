using System.Net;
using System.Text;
using System.Text.Json;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;

namespace ClaudeHomeServer.Services.Plugins;

/// <summary>
/// Серверный прокси <c>net:{host}</c> (ADR-031 §3.4, шаг 3.3): обработчик операции
/// <c>net.fetch</c> моста панели. Панель (sandbox-iframe, <c>connect-src 'none'</c>) не
/// может сама ходить в сеть — весь выход наружу делает сервер по выданному праву.
///
/// Инварианты (ADR-031 §3.4, §5):
///   1. Точный хост: url обязан совпадать с выданным правом <c>net:{host}</c>
///      (грамматика хоста — <see cref="PluginPermission.IsHost"/>); чужой хост → <c>denied</c>.
///   2. <see cref="SsrfGuard"/> на резолв адреса: loopback/private/link-local/metadata → <c>upstream</c>.
///   3. Редиректы только на тот же хост, иначе <c>upstream</c>.
///   4. Лимиты: таймаут → <c>timeout</c>, размер ответа / число запросов в минуту → <c>limit</c>.
///   5. Методы GET/POST; заголовки по белому списку; тело ограничено по размеру.
///   6. В ответ уходят статус, белый список заголовков и тело; секрет (подставленный в
///      исходящий заголовок) в ответ не возвращается никогда — вычищается из тела и
///      заголовков ответа, если эхо.
///
/// <c>Permission</c> — префикс <c>"net:"</c>: мост находит обработчик по домену, а точное
/// сверение хоста (с granted) делает сам прокси (у него granted). Stateless — singleton.
/// </summary>
public class PluginNetProxy : IPluginCapability
{
    /// <summary>Префикс: мост находит этот обработчик через <c>Permission.StartsWith("net:")</c>.</summary>
    public string Permission => "net:";

    private readonly PluginSecretsStore _secrets;
    private readonly HttpClient _http;

    // --- Лимиты (ADR-031 §3.4): значения — дефолты, тесты переопределяют ---

    /// <summary>Таймаут одного исходящего запроса (включая редиректы).</summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>Потолок размера тела ответа в байтах (превышение → <c>limit</c>).</summary>
    public long MaxResponseBytes { get; set; } = 1024 * 1024; // 1 MiB

    /// <summary>Потолок числа редиректов (превышение → <c>limit</c>).</summary>
    public int MaxRedirects { get; set; } = 5;

    /// <summary>Потолок числа запросов на плагин в минуту (превышение → <c>limit</c>).</summary>
    public int MaxRequestsPerMinute { get; set; } = 30;

    // --- Белые списки ---

    /// <summary>Допустимые HTTP-методы панели.</summary>
    private static readonly HashSet<string> AllowedMethods = new(StringComparer.OrdinalIgnoreCase)
    {
        "GET", "POST",
    };

    /// <summary>Исходящие заголовки, которые панель вправе ставить (остальные отбрасываются).</summary>
    private static readonly HashSet<string> AllowedRequestHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Authorization", "Content-Type", "Accept", "X-Api-Key", "X-Request-Id",
    };

    /// <summary>Входящие (ответные) заголовки, которые уходят во фрейм (остальные не несутся).</summary>
    private static readonly HashSet<string> AllowedResponseHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Content-Type", "Content-Length", "X-Request-Id", "X-Rate-Limit-Remaining",
    };

    /// <summary>Маркер вычищенного секрета в ответе (эхо: значение заменено, не возвращается).</summary>
    internal const string Redacted = "[REDACTED]";

    // Rate limit: скользящее окно по (ownerId:pluginId).
    private readonly object _rateLock = new();
    private readonly Dictionary<string, List<DateTime>> _rateWindow = new();

    public PluginNetProxy(PluginSecretsStore secrets, HttpClient http)
    {
        _secrets = secrets;
        _http = http;
    }

    /// <inheritdoc/>
    public async Task<PluginCapabilityResult> HandleAsync(
        PluginCallContext context, string op, JsonElement args, IReadOnlyList<string> granted, CancellationToken ct)
    {
        // 1. url: обязательный, http/https.
        var url = GetString(args, "url");
        if (string.IsNullOrWhiteSpace(url))
            return Err("invalid", "Не задан url");
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            return Err("invalid", $"Некорректный url: {url}");

        // 2. method: GET (дефолт) или POST.
        var method = (GetString(args, "method") ?? "GET").ToUpperInvariant();
        if (!AllowedMethods.Contains(method))
            return Err("invalid", $"Метод не поддерживается: {method}");

        // 3. Точный хост из granted: url-хост обязан совпадать с хостом выданного права
        //    net:{host} (грамматика — PluginPermission.IsHost). Порт из права не несём:
        //    net:api.linear.app и net:api.linear.app:443 — один и тот же хост.
        var urlHost = uri.IdnHost.ToLowerInvariant();
        if (!granted.Any(g => g.StartsWith("net:", StringComparison.Ordinal)
            && HostOf(g["net:".Length..]) == urlHost))
            return Err("denied", $"Нет права: net:{urlHost}");

        // 4. SsrfGuard: резолв адреса, запрет loopback/private/link-local/metadata.
        var check = await SsrfGuard.CheckAsync(uri, ct);
        if (check == SsrfGuard.AddressCheck.Private)
            return Err("upstream", $"Приватный адрес: {uri.Host}");
        if (check == SsrfGuard.AddressCheck.DnsFailed)
            return Err("upstream", $"DNS не ответил: {uri.Host}");

        // 5. Rate limit: число запросов на плагин в минуту.
        if (!TryAcquireRate(context.OwnerId, context.PluginId))
            return Err("limit", "Превышено число запросов на плагин в минуту");

        // 6. Секреты: выданные secret:{name} (имена) — для подстановки в заголовки.
        var allowedSecrets = granted
            .Where(g => g.StartsWith("secret:", StringComparison.Ordinal))
            .Select(g => g["secret:".Length..])
            .ToHashSet(StringComparer.Ordinal);

        // 7. Исходящие заголовки: белый список + подстановка {{secret:NAME}} (только выданные).
        var headers = new List<(string Name, string Value)>();
        if (args.TryGetProperty("headers", out var headersEl) && headersEl.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in headersEl.EnumerateObject())
            {
                if (!AllowedRequestHeaders.Contains(prop.Name)) continue;
                var value = prop.Value.ValueKind == JsonValueKind.String
                    ? prop.Value.GetString()!
                    : prop.Value.GetRawText();
                // Подстановка: {{secret:NAME}} → значение (если право выдано и секрет задан).
                value = _secrets.Resolve(context.OwnerId, context.PluginId, value, allowedSecrets);
                headers.Add((prop.Name, value));
            }
        }

        // auth: {secret, header, prefix} — альтернативный способ подставить секрет в заголовок.
        if (args.TryGetProperty("auth", out var authEl) && authEl.ValueKind == JsonValueKind.Object)
        {
            var secretName = GetString(authEl, "secret") ?? "";
            var headerName = GetString(authEl, "header") ?? "Authorization";
            var prefix = GetString(authEl, "prefix") ?? "";
            if (allowedSecrets.Contains(secretName, StringComparer.Ordinal)
                && _secrets.Get(context.OwnerId, context.PluginId, secretName) is { } authValue)
            {
                headers.Add((headerName, prefix + authValue));
            }
        }

        // 8. Тело (POST): ограничение по размеру.
        string? body = null;
        if (method == "POST" && args.TryGetProperty("body", out var bodyEl))
        {
            body = bodyEl.ValueKind == JsonValueKind.String ? bodyEl.GetString()! : bodyEl.GetRawText();
            if (body.Length > MaxResponseBytes)
                return Err("limit", "Тело запроса превышает лимит");
        }

        // 9. Запрос: GET/POST, редиректы только на тот же хост, таймаут, размер ответа.
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(Timeout);
        try
        {
            var currentUri = uri;
            var redirects = 0;
            HttpResponseMessage? response = null;
            try
            {
                while (true)
                {
                    var request = new HttpRequestMessage(new HttpMethod(method), currentUri);
                    foreach (var (name, value) in headers)
                        request.Headers.TryAddWithoutValidation(name, value);
                    if (method == "POST" && body is not null)
                        request.Content = new StringContent(body, Encoding.UTF8, "application/json");

                    using (request)
                    {
                        var resp = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token);
                        // Редирект: только на тот же хост, иначе upstream.
                        if (resp.StatusCode is HttpStatusCode.Moved or HttpStatusCode.Found or
                            HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect)
                        {
                            var location = resp.Headers.Location;
                            if (location is null)
                                return Err("upstream", "Редирект без Location");
                            if (location.IdnHost.ToLowerInvariant() != urlHost)
                                return Err("upstream", $"Редирект на другой хост: {location.Host}");
                            if (++redirects > MaxRedirects)
                                return Err("limit", "Превышено число редиректов");
                            currentUri = location;
                            resp.Dispose();
                            continue;
                        }
                        response = resp;
                        break;
                    }
                }
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                return Err("timeout", "Таймаут запроса");
            }
            catch (HttpRequestException)
            {
                return Err("upstream", "Сбой внешнего хоста");
            }

            // 10. Ответ: статус, белый список заголовков, тело. Секрет вычищается (эхо).
            using (response)
            {
                var bodyBytes = await response.Content.ReadAsByteArrayAsync(cts.Token);
                if (bodyBytes.Length > MaxResponseBytes)
                    return Err("limit", "Ответ превышает лимит размера");

                var responseBody = Encoding.UTF8.GetString(bodyBytes);
                var responseHeaders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var header in response.Headers)
                {
                    if (AllowedResponseHeaders.Contains(header.Key))
                        responseHeaders[header.Key] = string.Join(", ", header.Value);
                }
                foreach (var header in response.Content.Headers)
                {
                    if (AllowedResponseHeaders.Contains(header.Key))
                        responseHeaders[header.Key] = string.Join(", ", header.Value);
                }

                // Эхо секрета: значение, подставленное в исходящий заголовок, не должно
                // вернуться во фрейм — вычищаем из тела и заголовков ответа.
                responseBody = RedactSecrets(context, responseBody, allowedSecrets);
                foreach (var key in responseHeaders.Keys.ToList())
                    responseHeaders[key] = RedactSecrets(context, responseHeaders[key], allowedSecrets);

                return Ok(new
                {
                    status = (int)response.StatusCode,
                    headers = responseHeaders,
                    body = responseBody,
                });
            }
        }
        finally
        {
            // cts закрывается using'ом; linked-токен не гасим явно (ct — от моста).
        }
    }

    // --- Хелперы ---

    // Хост из строки «host» или «host:port» (право net:{host}): порт отбрасываем,
    // приводим к нижнему регистру. net:api.linear.app и net:api.linear.app:443 → api.linear.app.
    internal static string HostOf(string hostOrHostPort)
    {
        var idx = hostOrHostPort.LastIndexOf(':');
        var host = idx >= 0 ? hostOrHostPort[..idx] : hostOrHostPort;
        return host.ToLowerInvariant();
    }

    // Вычистить значения секретов из текста (эхо в ответе): значение → Redacted.
    private string RedactSecrets(PluginCallContext context, string text, HashSet<string> allowedSecrets)
    {
        if (string.IsNullOrEmpty(text)) return text;
        foreach (var name in allowedSecrets)
        {
            if (_secrets.Get(context.OwnerId, context.PluginId, name) is { } value
                && value.Length > 0
                && text.Contains(value, StringComparison.Ordinal))
            {
                text = text.Replace(value, Redacted, StringComparison.Ordinal);
            }
        }
        return text;
    }

    // Скользящее окно: не более MaxRequestsPerMinute за 60 с на (ownerId:pluginId).
    private bool TryAcquireRate(string ownerId, string pluginId)
    {
        var key = ownerId + ":" + pluginId;
        var now = DateTime.UtcNow;
        lock (_rateLock)
        {
            if (!_rateWindow.TryGetValue(key, out var window))
                _rateWindow[key] = window = [];
            window.RemoveAll(t => now - t > TimeSpan.FromMinutes(1));
            if (window.Count >= MaxRequestsPerMinute)
                return false;
            window.Add(now);
            return true;
        }
    }

    // Строковое поле JSON-объекта (null, если нет или не string).
    private static string? GetString(JsonElement obj, string name) =>
        obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    private static PluginCapabilityResult Ok(object data) => new(true, Data: data);

    private static PluginCapabilityResult Err(string code, string message) =>
        new(false, Error: new PluginCallError(code, message));
}
