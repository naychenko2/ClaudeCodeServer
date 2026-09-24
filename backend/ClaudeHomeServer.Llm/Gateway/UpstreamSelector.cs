using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;

namespace ClaudeHomeServer.Services.Llm.Gateway;

public enum GatewayUpstreamKind { Subscription, Provider }

// Маршрут хода через шлюз: куда и с какой моделью. Решается при выдаче токена хода (до запуска
// CLI) и едет в его привязке; посреди хода меняется только аккаунт подписки — ротацией внутри
// setup-token набора (UpstreamSelector.ResolveUpstream).
public sealed record GatewayRoute(
    GatewayUpstreamKind Kind,
    string Model,
    string? SubscriptionKey = null,
    string? ProviderKey = null);

// Итог выбора маршрута при старте хода: маршрут либо текст отказа для карточки ошибки хода.
public sealed record GatewayRouteDecision(GatewayRoute? Route, string? FailureText);

// Итог старта хода через шлюз: токен хода либо отказ. Ход без токена не стартует — вызывающий
// завершает его error с FailureText.
public sealed record GatewayTurnStart(IssuedTurnToken? Token, string? FailureText);

// Upstream конкретного запроса: адрес и учётные данные сервера. Credential наружу не уходит
// никогда — его ставит шлюз в заголовок upstream-запроса.
public sealed record GatewayUpstream(string BaseUrl, string Credential, GatewayRoute Route)
{
    public bool IsSubscription => Route.Kind == GatewayUpstreamKind.Subscription;
}

// Upstream запроса либо отказ (HTTP-статус + текст). Route — маршрут после возможной ротации.
public sealed record GatewayUpstreamDecision(GatewayUpstream? Upstream, int FailureStatus = 0, string? FailureText = null);

// Выбор upstream шлюза LLM (ADR-016, план §2 пп. 1 и 3). Провайдера и модель решает сервер,
// а не CLI: `--model` клиента — только подсказка, а фоновые haiku-вызовы CLI у стороннего
// upstream переписываются на модель провайдера (грабля 2 спайка).
//
// Подписки — только аккаунты на `claude setup-token`, и только через PickSetupToken: общий Pick
// тут не зовётся нигде (на пустом наборе он отдаёт интерактивный логин, а на аккаунте с ключом —
// API-ключ). Тумблер AllowSubscriptions читается живьём и проверяется ДО любого обращения к
// пулу — и при старте хода, и на каждом запросе, включая ротацию.
public class UpstreamSelector(
    ClaudeSubscriptionPool pool,
    LlmProviderRegistry providers,
    IOptionsMonitor<LlmGatewayOptions> options)
{
    public GatewayRouteDecision SelectRoute(string? model)
    {
        if (providers.ResolveByModel(model) is { } p)
        {
            if (!p.Enabled) return new(null, TurnFailureText.GatewayProviderNotConfigured);
            var main = string.IsNullOrWhiteSpace(model) ? p.Models.FirstOrDefault()?.Id ?? "" : model!;
            return new(new GatewayRoute(GatewayUpstreamKind.Provider, main, ProviderKey: p.Key), null);
        }
        if (!LlmProviderRegistry.IsNativeClaudeModel(model))
            return new(null, $"Модель «{model}» не найдена ни у одного провайдера сервера.");

        if (!options.CurrentValue.AllowSubscriptions)
            return new(null, TurnFailureText.GatewaySubscriptionsDisabled);
        var key = pool.PickSetupToken(model);
        return key is null
            ? new(null, TurnFailureText.LocalProjectsNeedSetupToken)
            : new(new GatewayRoute(GatewayUpstreamKind.Subscription, model ?? "", SubscriptionKey: key), null);
    }

    // Точка входа старта хода через шлюз: маршрут решается и привязывается к токену ДО запуска
    // CLI. Подходящего маршрута нет — токена нет, ход не стартует.
    public GatewayTurnStart StartTurn(TurnTokenService tokens, string ownerId, string sessionId,
        string? deviceId, string? model, TurnTokenLifetime lifetime = TurnTokenLifetime.Turn)
    {
        var decision = SelectRoute(model);
        return decision.Route is null
            ? new(null, decision.FailureText)
            : new(tokens.Issue(ownerId, sessionId, deviceId, decision.Route, lifetime), null);
    }

    public GatewayUpstreamDecision ResolveUpstream(GatewayRoute route)
    {
        if (route.Kind == GatewayUpstreamKind.Provider)
        {
            var p = providers.GetByKey(route.ProviderKey);
            if (p is null || !p.Enabled)
                return new(null, StatusCodes.Status503ServiceUnavailable, TurnFailureText.GatewayProviderNotConfigured);
            var credential = string.IsNullOrWhiteSpace(p.ApiKey) && p.IsLocal
                ? LlmProviderRegistry.LocalNoAuthToken
                : p.ApiKey;
            return new(new GatewayUpstream(p.AnthropicBaseUrl, credential, route));
        }

        var opts = options.CurrentValue;
        if (!opts.AllowSubscriptions)
            return new(null, StatusCodes.Status403Forbidden, TurnFailureText.GatewaySubscriptionsDisabled);

        var key = route.SubscriptionKey;
        if (key is null || pool.IsExhausted(key) || pool.IsAuthDead(key) || pool.SetupTokenOf(key) is null)
        {
            // Ротация посреди хода — только внутри setup-token набора.
            key = pool.PickSetupToken(route.Model);
            if (key is null)
                return new(null, StatusCodes.Status403Forbidden, TurnFailureText.LocalProjectsNeedSetupToken);
            route = route with { SubscriptionKey = key };
        }
        // Токен строго из конфига пула, профиль CLI (.credentials.json) не читается.
        return new(new GatewayUpstream(opts.AnthropicBaseUrl, pool.SetupTokenOf(key)!, route));
    }

    // Upstream отверг учётные данные подписки: следующий запрос хода уйдёт на соседний
    // setup-token аккаунт. Пометку снимет первый же ответ с заголовками лимитов (рекордер).
    public void ReportAuthRejected(GatewayRoute route)
    {
        if (route.Kind == GatewayUpstreamKind.Subscription && route.SubscriptionKey is { } key)
            pool.MarkAuthDead(key);
    }

    // Модель, с которой запрос уйдёт upstream.
    public string RewriteModel(GatewayRoute route, string? requested)
    {
        if (route.Kind == GatewayUpstreamKind.Subscription)
        {
            // Родные id Claude (в т.ч. фоновый haiku) у Anthropic валидны — оставляем.
            if (!string.IsNullOrWhiteSpace(requested) && providers.ResolveByModel(requested) is null
                && LlmProviderRegistry.IsNativeClaudeModel(requested))
                return requested!;
            return string.IsNullOrWhiteSpace(route.Model) ? requested ?? "" : route.Model;
        }

        var p = providers.GetByKey(route.ProviderKey);
        if (!string.IsNullOrWhiteSpace(requested) && p is not null
            && string.Equals(providers.ResolveByModel(requested)?.Key, p.Key, StringComparison.OrdinalIgnoreCase))
            return requested!;
        var main = string.IsNullOrWhiteSpace(route.Model) ? p?.Models.FirstOrDefault()?.Id ?? requested ?? "" : route.Model;
        if (p is null || string.IsNullOrWhiteSpace(requested)) return main;
        // Слоты — как в LlmProviderRegistry.BuildCliEnv для серверного хода.
        if (requested.Contains("haiku", StringComparison.OrdinalIgnoreCase))
            return string.IsNullOrWhiteSpace(p.SmallModel) ? main : p.SmallModel;
        if (requested.Contains("sonnet", StringComparison.OrdinalIgnoreCase))
            return string.IsNullOrWhiteSpace(p.MediumModel) ? main : p.MediumModel;
        return main;
    }
}
