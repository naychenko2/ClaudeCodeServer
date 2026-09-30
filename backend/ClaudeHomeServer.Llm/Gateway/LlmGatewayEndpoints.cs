using System.Buffers;
using System.Collections.Frozen;
using System.Text.Json;
using System.Text.Json.Nodes;
using ClaudeHomeServer.Services.Llm.Claude;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ClaudeHomeServer.Services.Llm.Gateway;

// Шлюз LLM (ADR-016, план §3 задача 1.2): CLI на устройстве ходит сюда через сайдкар по адресу
// /gw/t/{turnId}/llm/…, шлюз подставляет учётные данные сервера и проксирует на upstream.
//
// Что шлюз делает сам, а не отдаёт на откуп клиенту:
// - upstream и модель — по маршруту токена хода (UpstreamSelector), заголовки авторизации
//   клиента выбрасываются;
// - GET api/hello отвечает сам: CLI стучится туда при старте, а сторонние upstream дают 404;
// - к подписке добавляет anthropic-beta: oauth-2025-04-20 (CLI в режиме AUTH_TOKEN его не шлёт);
// - заголовки anthropic-ratelimit-unified-* ответа пишет в пул через SubscriptionLimitRecorder —
//   ту же точку, что rate_limit_event хода;
// - SSE отдаёт без буферизации: каждый прочитанный кусок сразу уходит клиенту с Flush.
// Любой HTTP-метод проходит: CLI и streamable HTTP шлют не только POST.
public static class LlmGatewayEndpoints
{
    public const string HttpClientName = "llm-gateway";
    public const string Route = "/gw/t/{turnId}/llm/{**path}";
    public const string OAuthBeta = "oauth-2025-04-20";

    // Заголовки, которые не пересылаются upstream: авторизация клиента (её заменяет шлюз),
    // токен хода и hop-by-hop. anthropic-beta собирается отдельно.
    private static readonly HashSet<string> DropRequestHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Host", "Authorization", "x-api-key", TurnTokenEndpointFilter.HeaderName,
        TurnTokenEndpointFilter.DeviceFingerprintHeader, "anthropic-beta",
        "Connection", "Keep-Alive", "Proxy-Authorization", "Proxy-Connection", "TE", "Trailer",
        "Transfer-Encoding", "Upgrade", "Content-Length", "Cookie",
    };

    // Общий для обоих шлюзов (LLM и MCP): hop-by-hop и всё, чем upstream мог бы выставить
    // куки или затеять аутентификацию на стороне устройства — клиент шлюза знает только токен хода.
    internal static readonly FrozenSet<string> DroppedResponseHeaders = new[]
    {
        "Connection", "Keep-Alive", "Proxy-Connection", "TE", "Trailer", "Transfer-Encoding", "Upgrade",
        "Set-Cookie", "Set-Cookie2", "WWW-Authenticate", "Proxy-Authenticate", "Authentication-Info",
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    public static IEndpointConventionBuilder MapLlmGateway(this IEndpointRouteBuilder app) =>
        app.Map(Route, HandleAsync)
            // Выключенный шлюз не отличим от отсутствующего: 404 раньше проверки токена.
            .AddEndpointFilter((ctx, next) =>
                ctx.HttpContext.RequestServices.GetRequiredService<IOptionsMonitor<LlmGatewayOptions>>().CurrentValue.Enabled
                    ? next(ctx)
                    : ValueTask.FromResult<object?>(Results.NotFound()))
            // Токен хода устройства или серверного хода провайдера (NormalizeToolInputArrays)
            .AddEndpointFilter(TurnTokenEndpointFilter.InvokeWithServerTurnAsync)
            // Авторизация — токен хода (фильтр выше), а не JWT пользователя.
            .AllowAnonymous();

    private static async Task HandleAsync(HttpContext ctx, string? path, UpstreamSelector selector,
        TurnTokenService tokens, SubscriptionLimitRecorder limits, IHttpClientFactory httpFactory,
        IOptionsMonitor<LlmGatewayOptions> options, ILoggerFactory loggers)
    {
        var grant = (TurnTokenGrant)ctx.Items[typeof(TurnTokenGrant)]!;
        path ??= "";

        if (HttpMethods.IsGet(ctx.Request.Method) && path.Equals("api/hello", StringComparison.OrdinalIgnoreCase))
        {
            await Results.Json(new { message = "hello" }).ExecuteAsync(ctx);
            return;
        }

        if (grant.Route is null)
        {
            await WriteErrorAsync(ctx, StatusCodes.Status403Forbidden, "Ход не проходит через шлюз LLM.");
            return;
        }

        var decision = selector.ResolveUpstream(grant.Route);
        if (decision.Upstream is not { } upstream)
        {
            await WriteErrorAsync(ctx, decision.FailureStatus, decision.FailureText ?? "Шлюз отказал.");
            return;
        }
        if (upstream.Route != grant.Route)
            tokens.Reroute(grant.TurnId, upstream.Route);

        var normalize = selector.NormalizesToolInput(upstream.Route);
        var (request, requestBody) = await BuildRequestAsync(ctx, path, upstream, selector, normalize);
        using var _ = request;
        HttpResponseMessage response;
        try
        {
            response = await httpFactory.CreateClient(HttpClientName)
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ctx.RequestAborted);
        }
        catch (HttpRequestException ex)
        {
            await WriteErrorAsync(ctx, StatusCodes.Status502BadGateway, $"Upstream недоступен: {ex.Message}");
            return;
        }

        using (response)
        {
            if (upstream.IsSubscription)
            {
                if ((int)response.StatusCode == StatusCodes.Status401Unauthorized)
                    selector.ReportAuthRejected(upstream.Route);
                foreach (var m in ClaudeRateLimitParser.FromUnifiedHeaders(name => Header(response, name)))
                    limits.Record(upstream.Route.SubscriptionKey, m, "gateway", context: $"шлюз, ход {grant.TurnId}");
            }

            // Нормализатор (флаг провайдера): решаем по типу ответа. Успешный JSON правится
            // целиком ДО заголовков — меняется Content-Length; SSE — фильтром в цикле ниже.
            // Без флага — прежний путь байт в байт, ни одной лишней аллокации.
            var mediaType = response.Content.Headers.ContentType?.MediaType;
            ToolInputResponseFilter? sse = null;
            byte[]? fixedJson = null;
            if (normalize)
            {
                var schemas = ToolInputResponseFilter.ToolSchemas(requestBody);
                var log = loggers.CreateLogger(LogCategory);
                var fixes = 0;
                void OnFix(ToolInputFix fix)
                {
                    fixes++;
                    log.LogWarning(
                        "Шлюз LLM развернул обёртку {{\"item\": …}} в tool_use.input: инструмент {Tool}, провайдер {Provider}, поля {Paths}, ход {TurnId}",
                        fix.ToolName, upstream.Route.ProviderKey, string.Join(", ", fix.Paths), grant.TurnId);
                    if (fixes == 1) DumpRequest(options.CurrentValue, requestBody, grant.TurnId, log);
                }
                if (string.Equals(mediaType, "text/event-stream", StringComparison.OrdinalIgnoreCase))
                    sse = new ToolInputResponseFilter(schemas, OnFix);
                else if (response.IsSuccessStatusCode && mediaType?.Contains("json", StringComparison.OrdinalIgnoreCase) == true)
                    fixedJson = ToolInputResponseFilter.NormalizeMessage(
                        await response.Content.ReadAsByteArrayAsync(ctx.RequestAborted), schemas, OnFix);
            }

            ctx.Response.StatusCode = (int)response.StatusCode;
            foreach (var (name, values) in response.Headers.Concat(response.Content.Headers))
                if (!DroppedResponseHeaders.Contains(name))
                    ctx.Response.Headers[name] = values.ToArray();

            if (fixedJson is not null)
            {
                ctx.Response.ContentLength = fixedJson.Length;
                await ctx.Response.Body.WriteAsync(fixedJson, ctx.RequestAborted);
                return;
            }

            ctx.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();
            // Заголовки — клиенту сразу, не дожидаясь первого куска тела: CLI ждёт их, чтобы
            // начать разбор потока.
            await ctx.Response.StartAsync(ctx.RequestAborted);
            await ctx.Response.Body.FlushAsync(ctx.RequestAborted);
            // Тело JSON уже прочитано нормализатором (ReadAsByteArrayAsync буферизует контент)
            // — повторное чтение отдаёт те же байты
            await using var body = await response.Content.ReadAsStreamAsync(ctx.RequestAborted);
            var buffer = ArrayPool<byte>.Shared.Rent(16 * 1024);
            var filtered = sse is null ? null : new ArrayBufferWriter<byte>();
            try
            {
                int read;
                while ((read = await body.ReadAsync(buffer, ctx.RequestAborted)) > 0)
                {
                    if (sse is null)
                        await ctx.Response.Body.WriteAsync(buffer.AsMemory(0, read), ctx.RequestAborted);
                    else
                    {
                        sse.Push(buffer.AsSpan(0, read), filtered!);
                        await WriteFilteredAsync(ctx, filtered!);
                    }
                    await ctx.Response.Body.FlushAsync(ctx.RequestAborted);
                }
                if (sse is not null)
                {
                    sse.Complete(filtered!);
                    await WriteFilteredAsync(ctx, filtered!);
                    await ctx.Response.Body.FlushAsync(ctx.RequestAborted);
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }
    }

    public const string LogCategory = "ClaudeHomeServer.LlmGateway";

    // Имя файла дампа: префикс, метка времени UTC и id хода шлюза (Guid "N")
    public const string DumpFilePrefix = "gw-normalized-";
    private static readonly System.Text.RegularExpressions.Regex DumpFileName =
        new(@"^gw-normalized-\d{8}-\d{6}-\d{3}-[0-9a-f]{32}\.json$", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    private static async Task WriteFilteredAsync(HttpContext ctx, ArrayBufferWriter<byte> filtered)
    {
        if (filtered.WrittenCount == 0) return;
        await ctx.Response.Body.WriteAsync(filtered.WrittenMemory, ctx.RequestAborted);
        filtered.ResetWrittenCount();
    }

    // Дамп тела запроса, ответ на который пришлось нормализовать (LlmGateway:DumpNormalizedRequestsDir):
    // по нему дефект провайдера воспроизводится голым запросом. Заголовки не пишутся — ключей
    // в файле нет. Сбой дампа ход не трогает.
    //
    // Чистка старых дампов удаляет ТОЛЬКО файлы с точным именем дампа (DumpFileName), никогда по
    // маске «*.json»: каталог задаёт админ, и «.» или data/ иначе унесли бы appsettings и сторы.
    private static void DumpRequest(LlmGatewayOptions opts, byte[]? body, string turnId, ILogger log)
    {
        if (string.IsNullOrWhiteSpace(opts.DumpNormalizedRequestsDir) || body is not { Length: > 0 }) return;
        try
        {
            Directory.CreateDirectory(opts.DumpNormalizedRequestsDir);
            var file = Path.Combine(opts.DumpNormalizedRequestsDir,
                $"{DumpFilePrefix}{DateTime.UtcNow:yyyyMMdd-HHmmss-fff}-{turnId}.json");
            File.WriteAllBytes(file, body);
            foreach (var old in new DirectoryInfo(opts.DumpNormalizedRequestsDir).GetFiles(DumpFilePrefix + "*.json")
                         .Where(f => DumpFileName.IsMatch(f.Name))
                         .OrderByDescending(f => f.Name).Skip(Math.Max(1, opts.DumpKeep)))
                old.Delete();
            log.LogWarning("Тело запроса с нормализованным ответом сохранено: {File}", file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log.LogWarning(ex, "Дамп запроса с нормализованным ответом не записан");
        }
    }

    private static async Task<(HttpRequestMessage Request, byte[]? Body)> BuildRequestAsync(HttpContext ctx, string path,
        GatewayUpstream upstream, UpstreamSelector selector, bool normalize)
    {
        var target = upstream.BaseUrl.TrimEnd('/') + "/" + path + ctx.Request.QueryString.Value;
        var request = new HttpRequestMessage(new HttpMethod(ctx.Request.Method), target);

        byte[]? bytes = null;
        if (ctx.Request.ContentLength > 0 || ctx.Request.Headers.TransferEncoding.Count > 0)
        {
            using var ms = new MemoryStream();
            await ctx.Request.Body.CopyToAsync(ms, ctx.RequestAborted);
            bytes = ms.ToArray();
            if (ctx.Request.ContentType?.Contains("json", StringComparison.OrdinalIgnoreCase) == true)
                bytes = RewriteModel(bytes, upstream.Route, selector);
            request.Content = new ByteArrayContent(bytes);
        }

        foreach (var (name, values) in ctx.Request.Headers)
        {
            if (DropRequestHeaders.Contains(name)) continue;
            // Ответ под нормализатор разбирается шлюзом — сжатый он был бы непрозрачен
            if (normalize && name.Equals("Accept-Encoding", StringComparison.OrdinalIgnoreCase)) continue;
            if (name.StartsWith("Content-", StringComparison.OrdinalIgnoreCase))
                request.Content?.Headers.TryAddWithoutValidation(name, values.ToArray());
            else
                request.Headers.TryAddWithoutValidation(name, values.ToArray());
        }

        var betas = ctx.Request.Headers["anthropic-beta"]
            .SelectMany(v => (v ?? "").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            .ToList();
        if (upstream.IsSubscription)
        {
            request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + upstream.Credential);
            if (!betas.Contains(OAuthBeta, StringComparer.OrdinalIgnoreCase)) betas.Add(OAuthBeta);
        }
        else
        {
            // Anthropic-совместимые провайдеры принимают ключ одним из двух способов — как и
            // серверный CLI (ANTHROPIC_AUTH_TOKEN + ANTHROPIC_API_KEY), шлём оба.
            request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + upstream.Credential);
            request.Headers.TryAddWithoutValidation("x-api-key", upstream.Credential);
        }
        if (betas.Count > 0)
            request.Headers.TryAddWithoutValidation("anthropic-beta", string.Join(",", betas));
        return (request, bytes);
    }

    // Поле model тела запроса переписывается по маршруту хода; не JSON или без model — как есть.
    private static byte[] RewriteModel(byte[] body, GatewayRoute route, UpstreamSelector selector)
    {
        try
        {
            if (JsonNode.Parse(body) is not JsonObject obj) return body;
            if (obj["model"] is not JsonValue v || !v.TryGetValue<string>(out var requested)) return body;
            var model = selector.RewriteModel(route, requested);
            if (model == requested) return body;
            obj["model"] = model;
            return JsonSerializer.SerializeToUtf8Bytes(obj);
        }
        catch (JsonException)
        {
            return body;
        }
    }

    private static string? Header(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues(name, out var values) ? values.FirstOrDefault() : null;

    // Ошибка в формате Anthropic API: CLI показывает message человеку.
    private static Task WriteErrorAsync(HttpContext ctx, int status, string message) =>
        Results.Json(new
        {
            type = "error",
            error = new
            {
                type = status == StatusCodes.Status403Forbidden ? "permission_error" : "api_error",
                message,
            },
        }, statusCode: status).ExecuteAsync(ctx);
}
