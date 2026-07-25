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
        var auth = ctx.Request.Headers.Authorization.ToString();
        var token = auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
            ? auth["Bearer ".Length..].Trim()
            : null;

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

    // Модуль по aud токена БЕЗ проверки подписи: aud нужен раньше валидации, чтобы знать,
    // против какого Audience валидировать. Подпись/lifetime/chan проверяются следом в Validate.
    private static LoadedModule? ResolveModuleByAudience(string? token, ModuleRegistry registry)
    {
        if (string.IsNullOrWhiteSpace(token)) return null;
        try
        {
            var parsed = new JwtSecurityTokenHandler { MapInboundClaims = false }.ReadJwtToken(token);
            return registry.All.FirstOrDefault(m =>
                parsed.Audiences.Contains(m.Audience, StringComparer.Ordinal));
        }
        catch
        {
            return null;
        }
    }

    private static async Task WriteUnauthorized(HttpContext ctx)
    {
        ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
        await ctx.Response.WriteAsJsonAsync(new { error = "unauthorized" });
    }
}
