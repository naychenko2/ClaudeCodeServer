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
/// Гейт делает только allow-list эндпоинтов: ребёнок получает 403 на любом запросе без метки
/// <see cref="ChildAllowedAttribute"/>. SPA-фолбэк совпадает с любым путём без своего эндпоинта, поэтому
/// открыт ему лишь на GET/HEAD к корню <c>/</c> (<see cref="ChildSpaFallbackAttribute"/>), всё прочее на нём —
/// 403: иначе ребёнок прошёл бы в ветки <c>app.Use</c> после гейта (/telemetry-proxy, OnlyOffice, статика
/// модулей). У веток свои проверки профиля на входе для случаев, когда токен приходит не в <c>ctx.User</c>.
/// Аутентифицированный запрос без владельца (нет sub) отказывается так же. Анонимный запрос ребёнком не
/// считается: вход (login) закрывает свой [AllowAnonymous].
/// </para>
/// <para>
/// Гейт НЕ решает, что можно ребёнку с неполным профилем (<c>DenyAll</c>): такой ребёнок остаётся
/// ребёнком и доходит до размеченных <see cref="ChildAllowedAttribute"/> ручек, а каждая из них отвечает
/// ему по-детски — <c>503 policy_unavailable</c> с текстом «попросите взрослого» (ChildStatusController,
/// ChildSubjectsController, ChildChatsController) или 403 not_child_subject / 404. Так неполный профиль не
/// уводит ребёнка во взрослую поверхность: он видит свой экран с empty-state (Г14, дефект 65aa2189).
/// Раньше blanket-отказ гейта по <c>DenyAll</c> глушил именно эту детскую обработку контроллеров.
/// </para>
/// <para>
/// Операционный <c>DenyAll</c> (запуск хода ребёнка, MCP-тулзы) остаётся fail-closed в <see cref="ChildModeGate"/>:
/// дойти до своего экрана — не то же, что вести урок по нечитаемому маршруту.
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
                // Allow-list эндпоинтов: ребёнку открыто только помеченное [ChildAllowed]. Неполный
                // профиль (DenyAll) здесь НЕ отсекается — до контроллера доходит он, и тот отвечает по-детски
                // (503 policy_unavailable / 403 not_child_subject / 404), а не уводит во взрослую поверхность (Г14).
                var meta = ctx.GetEndpoint()?.Metadata;
                var allowed = !unknownOwner
                    && meta?.GetMetadata<ChildAllowedAttribute>() is not null
                    && (meta.GetMetadata<ChildSpaFallbackAttribute>() is null || IsSpaRootRead(ctx.Request));
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

    private static bool IsSpaRootRead(HttpRequest request) =>
        (HttpMethods.IsGet(request.Method) || HttpMethods.IsHead(request.Method))
        && (request.Path.Value is null or "" or "/");
}

public static class ChildModeGateExtensions
{
    public static IApplicationBuilder UseChildModeGate(this IApplicationBuilder app) =>
        app.UseMiddleware<ChildModeGateMiddleware>();
}
