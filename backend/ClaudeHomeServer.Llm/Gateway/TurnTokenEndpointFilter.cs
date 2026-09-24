using Microsoft.AspNetCore.Authentication;
using ClaudeHomeServer.Protocol;

namespace ClaudeHomeServer.Services.Llm.Gateway;

// Вход в шлюз по токену хода: маршрут /gw/t/{turnId}/…, секрет — в X-Turn-Token.
// Токен принимается ТОЛЬКО вместе с учёткой устройства, к которому он привязан (ADR-016 §2):
// Authorization: Device … + отпечаток проверяет та же схема, что у /api/devices/* (ADR-008),
// фильтр лишь сверяет опознанное устройство с привязкой токена. Токен без привязки к
// устройству шлюз не принимает вовсе.
// Отозванный, просроченный по потолку, чужой или выдуманный токен, чужое, отозванное или
// непредъявленное устройство — 401 без объяснений.
// Прошедшая привязка кладётся в HttpContext.Items — дальше шлюз берёт владельца и чат
// только оттуда, а не из заголовков клиента.
public sealed class TurnTokenEndpointFilter(TurnTokenService tokens) : IEndpointFilter
{
    public const string HeaderName = "X-Turn-Token";
    public const string RouteKey = "turnId";
    // Имя заголовка отпечатка схемы устройства: дальше шлюза он не едет
    public const string DeviceFingerprintHeader = "X-Device-Fingerprint";

    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;
        var grant = await AuthorizeAsync(http, tokens);
        if (grant is null) return Results.Unauthorized();
        http.Items[typeof(TurnTokenGrant)] = grant;
        return await next(context);
    }

    // Та же проверка для входов, которые живут не на фильтре эндпоинта (туннель выхода — ветка
    // с WebSocket-мидлварой). null — отказ, 401 отвечает вызывающий.
    internal static async Task<TurnTokenGrant?> AuthorizeAsync(HttpContext http, TurnTokenService tokens)
    {
        var deviceId = await AuthenticatedDeviceAsync(http);
        var grant = deviceId is null
            ? null
            : tokens.Validate(
                http.Request.RouteValues[RouteKey] as string,
                http.Request.Headers[HeaderName].ToString(),
                deviceId);
        // Validate пропускает токен без привязки к устройству — шлюзу такой не годится
        return grant is not null && string.Equals(grant.DeviceId, deviceId, StringComparison.Ordinal) ? grant : null;
    }

    private static async Task<string?> AuthenticatedDeviceAsync(HttpContext http)
    {
        var result = await http.AuthenticateAsync(DesktopProtocol.DeviceTokenScheme);
        return result.Succeeded ? result.Principal.FindFirst(DesktopProtocol.DeviceIdClaim)?.Value : null;
    }
}
