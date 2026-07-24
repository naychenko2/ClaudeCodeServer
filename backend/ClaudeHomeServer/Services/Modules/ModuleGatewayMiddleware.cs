using Microsoft.AspNetCore.Http.Features;

namespace ClaudeHomeServer.Services.Modules;

/// <summary>
/// Identity-injection на границе gateway для /api/modules/{id}/** (контракт §5.2, ТЗ R4).
/// Работает ДО YARP-прокси:
///  1) всегда срезает входящие X-AIHome-* (клиент не может подделать заголовки ядра);
///  2) {routePrefix}/ui/** — публичная статика: срезается и Authorization (модулю
///     не утекает cc_token), токен не инжектится, на ответ ставится immutable-кэш (§7);
///  3) остальные пути: валидация cc_token (Authorization: Bearer | ?access_token=);
///     невалиден/отсутствует → 401 на границе, запрос модуля не достигает;
///     валиден → входящий Authorization заменяется свежим модульным токеном chan=gateway;
///  4) per-request лимит тела 100 МБ (§3.1; Kestrel вернёт 413 при превышении).
/// Неизвестный модуль → 404 (без модуля путь всё равно ушёл бы в SPA-fallback).
/// </summary>
public static class ModuleGatewayMiddleware
{
    public const long MaxRequestBodyBytes = 100 * 1024 * 1024;

    public static IApplicationBuilder UseModuleGateway(this IApplicationBuilder app) =>
        app.UseWhen(
            ctx => ctx.Request.Path.StartsWithSegments("/api/modules"),
            branch => branch.Use(Invoke));

    private static async Task Invoke(HttpContext ctx, RequestDelegate next)
    {
        // Срезка клиентских X-AIHome-* — безусловно, до любых веток (§5.2)
        foreach (var header in ctx.Request.Headers.Keys
                     .Where(k => k.StartsWith("X-AIHome-", StringComparison.OrdinalIgnoreCase)).ToList())
            ctx.Request.Headers.Remove(header);

        var moduleId = ExtractModuleId(ctx.Request.Path);
        if (moduleId is null)
        {
            // /api/modules без id — список модулей для оболочки (ModulesController), не gateway
            await next(ctx);
            return;
        }

        var registry = ctx.RequestServices.GetRequiredService<ModuleRegistry>();
        var module = registry.Get(moduleId);
        if (module is null)
        {
            ctx.Response.StatusCode = StatusCodes.Status404NotFound;
            await ctx.Response.WriteAsJsonAsync(new { error = "module_not_found", moduleId });
            return;
        }

        // §3.1: лимит тела запроса 100 МБ per-route (фото чеков, выписки)
        if (ctx.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } bodySize)
            bodySize.MaxRequestBodySize = MaxRequestBodyBytes;

        var uiPrefix = $"{module.Manifest.Backend!.RoutePrefix}/ui";
        if (ctx.Request.Path.StartsWithSegments(uiPrefix))
        {
            // Публичная статика (§7): анонимно, cc_token модулю не форвардим
            ctx.Request.Headers.Remove("Authorization");
            ctx.Response.OnStarting(() =>
            {
                if (ctx.Response.StatusCode == StatusCodes.Status200OK
                    && string.IsNullOrEmpty(ctx.Response.Headers.CacheControl))
                    ctx.Response.Headers.CacheControl = "public, max-age=31536000, immutable";
                return Task.CompletedTask;
            });
            await next(ctx);
            return;
        }

        // Data-plane (§5.2): два валидных предъявителя на Authorization —
        //  (а) cc_token ядра (HMAC) — браузерный путь: срезаем и наминтим chan=gateway;
        //  (б) модульный токен этого модуля (RS256, chan=mcp) — обратный вызов модуля через
        //      gateway (MODULE_API_TOKEN): passthrough как есть, без re-mint (токен уже валиден
        //      и у модуля, и у ядра). Иначе → 401 на границе ядра.
        var rawToken = ExtractCcToken(ctx.Request);
        var jwt = ctx.RequestServices.GetRequiredService<JwtService>();
        var tokens = ctx.RequestServices.GetRequiredService<ModuleTokenService>();
        var flags = ctx.RequestServices.GetRequiredService<FeatureFlagService>();

        // (а) cc_token ядра (HMAC): браузерный путь → свежий модульный токен chan=gateway
        var userId = jwt.ValidateUserToken(rawToken);
        var user = userId is null
            ? null
            : ctx.RequestServices.GetRequiredService<UserStore>().GetById(userId);
        if (user is null)
        {
            // (б) модульный токен chan=mcp этого модуля (RS256) — passthrough (§5.2б).
            // Authorization не трогаем: модуль увидит свой исходный chan=mcp/sub/aud.
            // Гейт видимости (R8) тот же — выключенный модуль недоступен и через mcp-токен.
            if (tokens.TryValidate(rawToken, module, out var tokenSub)
                && flags.IsEnabled(tokenSub!, module.FeatureFlagKey))
            {
                await next(ctx);
                return;
            }

            ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await ctx.Response.WriteAsJsonAsync(new { error = "unauthorized" });
            return;
        }

        // Гейт видимости (R8): выключенный модуль недоступен и через gateway
        if (!flags.IsEnabled(user.Id, module.FeatureFlagKey))
        {
            ctx.Response.StatusCode = StatusCodes.Status404NotFound;
            await ctx.Response.WriteAsJsonAsync(new { error = "module_not_found", moduleId });
            return;
        }

        // §5.2(а): свежий модульный токен вместо клиентского Authorization.
        // Браузер модульный токен никогда не видит — он рождается и живёт только здесь.
        var moduleToken = tokens.Issue(module, user.Id, user.DisplayName ?? user.Username, "gateway");
        ctx.Request.Headers.Authorization = $"Bearer {moduleToken}";

        await next(ctx);
    }

    // /api/modules/{id}/... → {id}; сам /api/modules (список для фронта) — контроллер ядра, не gateway
    private static string? ExtractModuleId(PathString path)
    {
        var segments = path.Value?.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return segments is { Length: >= 3 } ? segments[2] : null;
    }

    private static string? ExtractCcToken(HttpRequest request)
    {
        var auth = request.Headers.Authorization.ToString();
        if (auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            return auth["Bearer ".Length..].Trim();
        var query = request.Query["access_token"].ToString();
        return string.IsNullOrWhiteSpace(query) ? null : query;
    }
}
