namespace ClaudeHomeServer.Services.Llm.Gateway;

// Вход в шлюз по токену хода: маршрут /gw/t/{turnId}/…, секрет — в X-Turn-Token.
// Отозванный, просроченный по потолку, чужой или выдуманный токен — 401 без объяснений.
// Прошедшая привязка кладётся в HttpContext.Items — дальше шлюз берёт владельца и чат
// только оттуда, а не из заголовков клиента.
public sealed class TurnTokenEndpointFilter(TurnTokenService tokens) : IEndpointFilter
{
    public const string HeaderName = "X-Turn-Token";
    public const string RouteKey = "turnId";

    public ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;
        var grant = tokens.Validate(
            http.Request.RouteValues[RouteKey] as string,
            http.Request.Headers[HeaderName].ToString());
        if (grant is null)
            return ValueTask.FromResult<object?>(Results.Unauthorized());
        http.Items[typeof(TurnTokenGrant)] = grant;
        return next(context);
    }
}
