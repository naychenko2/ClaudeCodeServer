using System.Net;
using System.Net.Sockets;
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
///      Проверка — на КАЖДОМ шаге редиректа (редирект на приватный адрес — тот же SSRF),
///      а боевой транспорт дополнительно закрепляет адрес в <c>ConnectCallback</c>
///      (TOCTOU/DNS-rebinding: между проверкой и TCP-connect адрес не пере-резолвится).
///   3. Редиректы только на тот же хост, иначе <c>upstream</c>.
///   4. Лимиты: таймаут → <c>timeout</c>, размер ответа / число запросов в минуту → <c>limit</c>.
///   5. Методы GET/POST; заголовки по белому списку; тело ограничено по размеру.
///   6. В ответ уходят статус, белый список заголовков и тело; секрет (подставленный в
///      исходящий заголовок) в ответ не возвращается никогда — вычищается из тела и
///      заголовков ответа, если эхо. Сверка эхо — на уровне байт (тело не декодируется
///      как UTF-8: не-текстовый контент не превращается в mojibake, а битый суррогат
///      не роняет запрос).
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
    // Резолв хоста для предварительной SSRF-проверки (по умолчанию системный Dns;
    // тесты подставляют фейк, чтобы детерминировать DNS rebinding).
    private readonly Func<string, CancellationToken, Task<IPAddress[]>> _dnsResolver;

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

    public PluginNetProxy(PluginSecretsStore secrets, HttpMessageHandler handler,
        Func<string, CancellationToken, Task<IPAddress[]>>? dnsResolver = null)
    {
        _secrets = secrets;
        // disposeHandler: false — хендлер живёт дольше запроса (боевой — singleton'ный
        // SocketsHttpHandler, тестовый — фейк, который тест сам не гасит).
        _http = new HttpClient(handler, disposeHandler: false);
        _dnsResolver = dnsResolver ?? ((host, ct) => Dns.GetHostAddressesAsync(host, ct));
    }

    /// <summary>
    /// Боевой транспорт (ADR-031 §3.4, rework 12): <c>SocketsHttpHandler</c> без прокси
    /// (egress-прокси релеит на приватные адреса, см. ADR-005) и без авто-редиректов
    /// (редиректы разбирает сам прокси, с повторной SSRF-проверкой на каждом шаге).
    /// <c>ConnectCallback</c> закрепляет адрес: резолвит хост прямо перед TCP-connect и
    /// подключается к первому публичному адресу — TOCTOU-окно между предварительной
    /// проверкой и реальным соединением (DNS rebinding) закрыто, а имя хоста в URI
    /// остаётся исходным (заголовок Host и TLS SNI не ломаются, в отличие от подмены
    /// URI на голый IP).
    /// </summary>
    public static SocketsHttpHandler CreateHandler() => new()
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        UseProxy = false,
        ConnectTimeout = TimeSpan.FromSeconds(10),
        ConnectCallback = ConnectAsync,
    };

    private static async ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext context, CancellationToken ct)
    {
        var address = await ResolvePublicAsync(context.DnsEndPoint.Host, ct)
            ?? throw new HttpRequestException($"Адрес {context.DnsEndPoint.Host} не публичный — соединение запрещено");
        var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(new IPEndPoint(address, context.DnsEndPoint.Port), ct);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    /// <summary>Адрес для соединения, если ВСЕ адреса хоста публичные; иначе null (fail-closed, как у SsrfGuard).</summary>
    private static async Task<IPAddress?> ResolvePublicAsync(string host, CancellationToken ct)
    {
        IPAddress[] addresses;
        if (IPAddress.TryParse(host, out var literal)) addresses = [literal];
        else
        {
            try { addresses = await Dns.GetHostAddressesAsync(host, ct); }
            catch (SocketException) { return null; }
        }
        return addresses.Length > 0 && addresses.All(SsrfGuard.IsPublic) ? addresses[0] : null;
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

        // 4. SsrfGuard: проверка адреса — на КАЖДОМ шаге редиректа (в цикле ниже, шаг 9),
        //    а не один раз на исходном url: редирект на приватный адрес — тот же SSRF,
        //    а DNS rebinding закрывает боевой ConnectCallback (CreateHandler).

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
        // Нормализация префикса (rework 12, minor 10): между префиксом и значением ровно один
        // пробел, либо ноль, если префикс пустой (в т.ч. только пробелы). Без нормализации
        // prefix "Bearer " (с пробелом) давал двойной пробел, а "" — склеенное значение.
        if (args.TryGetProperty("auth", out var authEl) && authEl.ValueKind == JsonValueKind.Object)
        {
            var secretName = GetString(authEl, "secret") ?? "";
            var headerName = GetString(authEl, "header") ?? "Authorization";
            var prefix = (GetString(authEl, "prefix") ?? "").TrimEnd();
            if (allowedSecrets.Contains(secretName, StringComparer.Ordinal)
                && _secrets.Get(context.OwnerId, context.PluginId, secretName) is { } authValue)
            {
                var headerValue = prefix.Length == 0 ? authValue : prefix + " " + authValue;
                headers.Add((headerName, headerValue));
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
                    // SSRF-проверка на каждом шаге: резолвим хост currentUri (исходный url
                    // на первой итерации, Location — на следующих) и запрещаем приватные
                    // адреса / сбой DNS. Редирект на приватный адрес без этой проверки —
                    // SSRF (исходный url был публичным, а Location — нет).
                    var (_, check) = await ResolveAndCheckAsync(currentUri, cts.Token);
                    if (check == SsrfGuard.AddressCheck.Private)
                        return Err("upstream", $"Приватный адрес: {currentUri.Host}");
                    if (check == SsrfGuard.AddressCheck.DnsFailed)
                        return Err("upstream", $"DNS не ответил: {currentUri.Host}");

                    var request = new HttpRequestMessage(new HttpMethod(method), currentUri);
                    foreach (var (name, value) in headers)
                        request.Headers.TryAddWithoutValidation(name, value);
                    if (method == "POST" && body is not null)
                        request.Content = new StringContent(body, Encoding.UTF8, "application/json");

                    using (request)
                    {
                        // Боевой транспорт дополнительно закрепляет адрес в ConnectCallback
                        // (резолвит хост прямо перед connect: TOCTOU/DNS rebinding закрыт).
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
                // вернуться во фрейм — вычищаем из тела и заголовков ответа. Тело — на уровне
                // байт (rework 12, minor 9): не-текстовый контент (картинка, бинарь) не
                // декодируем как UTF-8 (mojibake), а значение секрета ищем как UTF-8-байты.
                var responseBody = Encoding.UTF8.GetString(RedactSecretsBytes(context, bodyBytes, allowedSecrets));
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

    // Предварительная SSRF-проверка хоста (rework 12, critical 1): резолвим и классифицируем
    // ВСЕ адреса хоста. Возвращает (адрес для закрепления, вердикт):
    //   - IP-литерал → (сам литерал, Public/Private по SsrfGuard.IsPublic);
    //   - имя → резолв (_dnsResolver): сбой/пусто → DnsFailed, любой приватный → Private,
    //     иначе → (первый публичный адрес, Public).
    // Адрес для закрепления — тот, к которому боевой ConnectCallback подключится: проверка и
    // соединение видят один и тот же адрес (TOCTOU/DNS rebinding закрыт).
    private async Task<(IPAddress? Pinned, SsrfGuard.AddressCheck Check)> ResolveAndCheckAsync(Uri uri, CancellationToken ct)
    {
        if (IPAddress.TryParse(uri.Host, out var literal))
            return (literal, SsrfGuard.IsPublic(literal) ? SsrfGuard.AddressCheck.Public : SsrfGuard.AddressCheck.Private);

        IPAddress[] addresses;
        try { addresses = await _dnsResolver(uri.Host, ct); }
        catch { return (null, SsrfGuard.AddressCheck.DnsFailed); }
        if (addresses.Length == 0) return (null, SsrfGuard.AddressCheck.DnsFailed);
        if (addresses.Any(a => !SsrfGuard.IsPublic(a))) return (null, SsrfGuard.AddressCheck.Private);
        return (addresses[0], SsrfGuard.AddressCheck.Public);
    }

    // Вычистить значения секретов из текста (эхо в заголовках ответа): значение → Redacted.
    // Заголовки — всегда текст, поэтому строковая сверка здесь корректна.
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

    // Вычистить значения секретов из ТЕЛА ответа на уровне байт (rework 12, minor 9):
    // тело не декодируем как UTF-8 — не-текстовый контент (картинка, бинарь) не превращается
    // в mojibake, а битый суррогат не роняет запрос. Значение секрета — текст, ищем его
    // UTF-8-байты и заменяем на UTF-8-байты маркера Redacted.
    private byte[] RedactSecretsBytes(PluginCallContext context, byte[] body, HashSet<string> allowedSecrets)
    {
        foreach (var name in allowedSecrets)
        {
            if (_secrets.Get(context.OwnerId, context.PluginId, name) is not { } value || value.Length == 0)
                continue;
            body = ReplaceBytes(body, Encoding.UTF8.GetBytes(value), Encoding.UTF8.GetBytes(Redacted));
        }
        return body;
    }

    // Заменить все вхождения последовательности needle в body на replacement (байты).
    private static byte[] ReplaceBytes(byte[] body, byte[] needle, byte[] replacement)
    {
        if (needle.Length == 0) return body;
        var result = new List<byte>(body.Length);
        var i = 0;
        while (i <= body.Length - needle.Length)
        {
            if (MatchesAt(body, i, needle))
            {
                result.AddRange(replacement);
                i += needle.Length;
            }
            else
            {
                result.Add(body[i]);
                i++;
            }
        }
        // Хвост короче needle вхождения не содержит — копируем как есть.
        for (; i < body.Length; i++)
            result.Add(body[i]);
        return result.ToArray();
    }

    // Совпадает ли needle в body, начиная с позиции start.
    private static bool MatchesAt(byte[] body, int start, byte[] needle)
    {
        for (var j = 0; j < needle.Length; j++)
            if (body[start + j] != needle[j]) return false;
        return true;
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

    // Очистить окно лимита при удалении плагина (rework 12, minor 8): словарь _rateWindow
    // singleton'а, иначе он рос бы бесконечно (ключ на каждый когда-либо установленный
    // плагин). Вызывает PluginUninstall после каскада.
    public void RemoveRateWindow(string ownerId, string pluginId)
    {
        lock (_rateLock)
            _rateWindow.Remove(ownerId + ":" + pluginId);
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
