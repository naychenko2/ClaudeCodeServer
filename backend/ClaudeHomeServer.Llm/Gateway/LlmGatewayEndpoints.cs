using System.Buffers;
using System.Text.Json;
using System.Text.Json.Nodes;
using ClaudeHomeServer.Services.Llm.Claude;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
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
        "Host", "Authorization", "x-api-key", TurnTokenEndpointFilter.HeaderName, "anthropic-beta",
        "Connection", "Keep-Alive", "Proxy-Authorization", "Proxy-Connection", "TE", "Trailer",
        "Transfer-Encoding", "Upgrade", "Content-Length", "Cookie",
    };

    private static readonly HashSet<string> DropResponseHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Connection", "Keep-Alive", "Proxy-Connection", "TE", "Trailer", "Transfer-Encoding", "Upgrade",
    };

    public static IEndpointConventionBuilder MapLlmGateway(this IEndpointRouteBuilder app) =>
        app.Map(Route, HandleAsync)
            // Выключенный шлюз не отличим от отсутствующего: 404 раньше проверки токена.
            .AddEndpointFilter((ctx, next) =>
                ctx.HttpContext.RequestServices.GetRequiredService<IOptionsMonitor<LlmGatewayOptions>>().CurrentValue.Enabled
                    ? next(ctx)
                    : ValueTask.FromResult<object?>(Results.NotFound()))
            .AddEndpointFilter<TurnTokenEndpointFilter>()
            // Авторизация — токен хода (фильтр выше), а не JWT пользователя.
            .AllowAnonymous();

    private static async Task HandleAsync(HttpContext ctx, string? path, UpstreamSelector selector,
        TurnTokenService tokens, SubscriptionLimitRecorder limits, IHttpClientFactory httpFactory)
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

        using var request = await BuildRequestAsync(ctx, path, upstream, selector);
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

            ctx.Response.StatusCode = (int)response.StatusCode;
            foreach (var (name, values) in response.Headers.Concat(response.Content.Headers))
                if (!DropResponseHeaders.Contains(name))
                    ctx.Response.Headers[name] = values.ToArray();

            ctx.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();
            // Заголовки — клиенту сразу, не дожидаясь первого куска тела: CLI ждёт их, чтобы
            // начать разбор потока.
            await ctx.Response.StartAsync(ctx.RequestAborted);
            await ctx.Response.Body.FlushAsync(ctx.RequestAborted);
            await using var body = await response.Content.ReadAsStreamAsync(ctx.RequestAborted);
            var buffer = ArrayPool<byte>.Shared.Rent(16 * 1024);
            try
            {
                int read;
                while ((read = await body.ReadAsync(buffer, ctx.RequestAborted)) > 0)
                {
                    await ctx.Response.Body.WriteAsync(buffer.AsMemory(0, read), ctx.RequestAborted);
                    await ctx.Response.Body.FlushAsync(ctx.RequestAborted);
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }
    }

    private static async Task<HttpRequestMessage> BuildRequestAsync(HttpContext ctx, string path,
        GatewayUpstream upstream, UpstreamSelector selector)
    {
        var target = upstream.BaseUrl.TrimEnd('/') + "/" + path + ctx.Request.QueryString.Value;
        var request = new HttpRequestMessage(new HttpMethod(ctx.Request.Method), target);

        if (ctx.Request.ContentLength > 0 || ctx.Request.Headers.TransferEncoding.Count > 0)
        {
            using var ms = new MemoryStream();
            await ctx.Request.Body.CopyToAsync(ms, ctx.RequestAborted);
            var bytes = ms.ToArray();
            if (ctx.Request.ContentType?.Contains("json", StringComparison.OrdinalIgnoreCase) == true)
                bytes = RewriteModel(bytes, upstream.Route, selector);
            request.Content = new ByteArrayContent(bytes);
        }

        foreach (var (name, values) in ctx.Request.Headers)
        {
            if (DropRequestHeaders.Contains(name)) continue;
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
        return request;
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
