using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;

namespace ClaudeHomeServer.Services.ChildMode;

/// <summary>
/// Гейт И1 детского режима (ADR-041): ребёнок может только то, что явно помечено
/// <see cref="ChildAllowedAttribute"/>. Стоит после UseAuthorization (метаданные эндпоинта и
/// <c>ctx.User</c> уже известны) и до UseModuleGateway. Владельца берём из <c>sub</c>, профиль —
/// по стору через <see cref="IChildModePolicy"/>, а не из claim: выключение профиля действует
/// со следующего запроса.
/// <para>
/// Отказ по умолчанию: аутентифицированный ребёнок на запросе без эндпоинта (ветки конвейера
/// вроде WebDAV или /preview) тоже получает 403 — у этих веток свои проверки профиля на входе
/// для случаев, когда токен приходит не в <c>ctx.User</c>. Профиль с <c>DenyAll</c> не пускает
/// никуда, даже на размеченные эндпоинты: «выход» — клиентский, серверного logout у JWT нет.
/// Аутентифицированный запрос без владельца (нет sub) отказывается так же. Анонимный запрос ребёнком не считается: вход (login) закрывает свой [AllowAnonymous].
/// </para>
/// </summary>
public sealed class ChildModeGateMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext ctx, IChildModePolicy policy)
    {
        if (ctx.User.Identity?.IsAuthenticated == true)
        {
            // Пустой владелец (нет sub) — fail-closed: For("") вернул бы null («не ребёнок»)
            var ownerId = ctx.User.FindFirstValue(JwtRegisteredClaimNames.Sub);
            var snapshot = string.IsNullOrWhiteSpace(ownerId) ? null : policy.For(ownerId);
            var unknownOwner = string.IsNullOrWhiteSpace(ownerId);
            if (unknownOwner || snapshot is not null)
            {
                // Хабы кроме /hubs/session (terminal, devices) ребёнку закрыты целиком:
                // у них нет [ChildAllowed], поэтому negotiate получает 403 здесь.
                var allowed = !unknownOwner && !snapshot!.DenyAll
                    && ctx.GetEndpoint()?.Metadata.GetMetadata<ChildAllowedAttribute>() is not null;
                if (!allowed)
                {
                    ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
                    await ctx.Response.WriteAsJsonAsync(new { error = "child_mode" });
                    return;
                }
            }
        }
        await next(ctx);
    }
}

public static class ChildModeGateExtensions
{
    public static IApplicationBuilder UseChildModeGate(this IApplicationBuilder app) =>
        app.UseMiddleware<ChildModeGateMiddleware>();
}
