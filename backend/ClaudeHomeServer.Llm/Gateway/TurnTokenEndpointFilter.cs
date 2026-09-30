using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.DependencyInjection;
using ClaudeHomeServer.Protocol;

namespace ClaudeHomeServer.Services.Llm.Gateway;

// Вход в шлюз по токену хода: маршрут /gw/t/{turnId}/…, секрет — в X-Turn-Token.
// Токен устройства принимается ТОЛЬКО вместе с учёткой устройства, к которому он привязан
// (ADR-016 §2): Authorization: Device … + отпечаток проверяет та же схема, что у
// /api/devices/* (ADR-008), фильтр лишь сверяет опознанное устройство с привязкой токена.
//
// Серверный ход провайдера (ADR-016 §2, пункт про серверный ход) — токен без устройства:
// его выдаёт ClaudeSession серверному процессу CLI провайдера с NormalizeToolInputArrays, чтобы
// ответ модели прошёл через нормализатор шлюза. Такой токен принимается только там, где вход
// явно разрешил (allowServerTurn — только LLM-маршрут), только без учётки устройства и только
// с маршрутом провайдера: MCP-шлюз и туннель выхода остаются строго за устройствами, а подписки
// Claude серверному ходу через шлюз не положены. На IP-адрес не опираемся: из песочницы
// запрос приходит с docker-моста, а не с loopback.
//
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

    // Вход только для устройств (MCP-шлюз)
    public ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next) =>
        InvokeAsync(context, next, tokens, allowServerTurn: false);

    // Вход LLM-шлюза: устройства плюс серверный ход провайдера
    public static ValueTask<object?> InvokeWithServerTurnAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next) =>
        InvokeAsync(context, next, context.HttpContext.RequestServices.GetRequiredService<TurnTokenService>(), allowServerTurn: true);

    private static async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next,
        TurnTokenService tokens, bool allowServerTurn)
    {
        var http = context.HttpContext;
        var grant = await AuthorizeAsync(http, tokens, allowServerTurn);
        if (grant is null) return Results.Unauthorized();
        http.Items[typeof(TurnTokenGrant)] = grant;
        return await next(context);
    }

    // Та же проверка для входов, которые живут не на фильтре эндпоинта (туннель выхода — ветка
    // с WebSocket-мидлварой). null — отказ, 401 отвечает вызывающий.
    internal static async Task<TurnTokenGrant?> AuthorizeAsync(HttpContext http, TurnTokenService tokens,
        bool allowServerTurn = false)
    {
        var turnId = http.Request.RouteValues[RouteKey] as string;
        var token = http.Request.Headers[HeaderName].ToString();
        var deviceId = await AuthenticatedDeviceAsync(http);
        if (deviceId is not null)
        {
            var grant = tokens.Validate(turnId, token, deviceId);
            // Validate пропускает токен без привязки к устройству — устройству такой не годится
            return grant is not null && string.Equals(grant.DeviceId, deviceId, StringComparison.Ordinal) ? grant : null;
        }
        if (!allowServerTurn) return null;
        var server = tokens.Validate(turnId, token);
        return server is { DeviceId: null, Route.Kind: GatewayUpstreamKind.Provider } ? server : null;
    }

    private static async Task<string?> AuthenticatedDeviceAsync(HttpContext http)
    {
        var result = await http.AuthenticateAsync(DesktopProtocol.DeviceTokenScheme);
        return result.Succeeded ? result.Principal.FindFirst(DesktopProtocol.DeviceIdClaim)?.Value : null;
    }
}
