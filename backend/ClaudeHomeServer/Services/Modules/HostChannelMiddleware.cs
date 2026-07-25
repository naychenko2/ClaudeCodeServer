using System.IdentityModel.Tokens.Jwt;
using ClaudeHomeServer.Models;

namespace ClaudeHomeServer.Services.Modules;

/// <summary>
/// Аутентификация host-канала модулей на /api/host/** (контракт §10.2, ТЗ R11).
/// Отдельная поверхность ВНЕ HMAC-схемы ядра: контроллеры под этим префиксом не несут
/// [Authorize], единственный вход — модульный токен RS256 в Authorization.
///  1) модуль резолвится по aud токена (без подписи — подпись проверяется следом);
///  2) POST /api/host/token (обмен) принимает chan=gateway|mcp; все остальные пути —
///     только chan=host (обратно chan=host на /api/modules/** отвергает gateway: его
///     passthrough принимает лишь chan=mcp);
///  3) гейт видимости module-{id} — уже здесь, включая сам обмен токена (пакет §10.2 v1.5):
///     скрытый модуль → 403 module_hidden (на gateway тот же гейт даёт 404 — там модуль
///     «не существует» для клиента, здесь модуль-серверу честно сообщается причина);
///  4) провалидированные модуль/sub/имя кладутся в HttpContext.Items для контроллеров.
/// </summary>
public static class HostChannelMiddleware
{
    /// <summary>Ключи HttpContext.Items с результатом валидации host-канала.</summary>
    public const string ModuleItem = "HostChannel.Module";
    public const string UserIdItem = "HostChannel.UserId";
    public const string UserNameItem = "HostChannel.UserName";

    public static IApplicationBuilder UseHostChannel(this IApplicationBuilder app) =>
        app.UseWhen(
            ctx => ctx.Request.Path.StartsWithSegments("/api/host"),
            branch => branch.Use(Invoke));

    private static async Task Invoke(HttpContext ctx, RequestDelegate next)
    {
        var token = ExtractBearerToken(ctx.Request);

        var registry = ctx.RequestServices.GetRequiredService<ModuleRegistry>();
        var module = ResolveModuleByAudience(token, registry);
        if (module is null)
        {
            await WriteUnauthorized(ctx);
            return;
        }

        // Обмен токена (§10.2) предъявляет рабочий канал модуля; всё остальное — только host.
        var isExchange = ctx.Request.Path.Equals("/api/host/token", StringComparison.OrdinalIgnoreCase);
        var allowedChans = isExchange ? new[] { "gateway", "mcp" } : ["host"];

        var tokens = ctx.RequestServices.GetRequiredService<ModuleTokenService>();
        var validated = tokens.Validate(token, module, allowedChans);
        if (validated is null)
        {
            await WriteUnauthorized(ctx);
            return;
        }

        // Гейт видимости module-{id} (§10.2 v1.5) — на host-канале всегда, включая обмен:
        // скрытому модулю токен не выдаётся и вызовы не обслуживаются.
        var flags = ctx.RequestServices.GetRequiredService<FeatureFlagService>();
        if (!flags.IsEnabled(validated.UserId, module.FeatureFlagKey))
        {
            ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
            await ctx.Response.WriteAsJsonAsync(new { error = "module_hidden" });
            return;
        }

        ctx.Items[ModuleItem] = module;
        ctx.Items[UserIdItem] = validated.UserId;
        ctx.Items[UserNameItem] = validated.UserName;
        await next(ctx);
    }

    /// <summary>Bearer-токен из заголовка Authorization, либо null.</summary>
    public static string? ExtractBearerToken(HttpRequest request)
    {
        var auth = request.Headers.Authorization.ToString();
        return auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
            ? auth["Bearer ".Length..].Trim()
            : null;
    }

    // Модуль и sub по aud/sub токена БЕЗ проверки подписи: aud нужен раньше валидации, чтобы
    // знать, против какого Audience валидировать. Подпись/lifetime/chan проверяются следом
    // в Validate — подделка aud/sub здесь ничем не грозит.
    private static (LoadedModule? Module, string? Sub) ParseUnverified(string? token, ModuleRegistry registry)
    {
        if (string.IsNullOrWhiteSpace(token)) return (null, null);
        try
        {
            var parsed = new JwtSecurityTokenHandler { MapInboundClaims = false }.ReadJwtToken(token);
            var module = registry.All.FirstOrDefault(m =>
                parsed.Audiences.Contains(m.Audience, StringComparer.Ordinal));
            return (module, parsed.Subject);
        }
        catch
        {
            return (null, null);
        }
    }

    private static LoadedModule? ResolveModuleByAudience(string? token, ModuleRegistry registry) =>
        ParseUnverified(token, registry).Module;

    /// <summary>
    /// Ключ партиции rate-limit host-токена (§10.2 v1.5): "{moduleId}:{sub}" — читается из
    /// aud/sub токена БЕЗ проверки подписи (партиционер выполняется до UseHostChannel, где
    /// провалидированных данных ещё нет). Null — токен не распознан, вызывающий (партиционер
    /// в Program.cs) откатывается на IP, чтобы мусорные запросы не делили общую партицию.
    /// </summary>
    public static string? TryReadPartitionKey(string? token, ModuleRegistry registry)
    {
        var (module, sub) = ParseUnverified(token, registry);
        return module is not null && !string.IsNullOrEmpty(sub) ? $"{module.Id}:{sub}" : null;
    }

    private static async Task WriteUnauthorized(HttpContext ctx)
    {
        ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
        await ctx.Response.WriteAsJsonAsync(new { error = "unauthorized" });
    }
}
