using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Llm;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ClaudeHomeServer.Controllers;

[ApiController]
[Authorize]
[Route("api/usage")]
public class UsageController(UsageService usage, ClaudeSubscriptionPool? subscriptionPool,
    LlmProviderRegistry providers, LocalActionRouter localRouter, OllamaClient ollama,
    SubscriptionOAuthUsageService oauthUsage, IConfiguration config) : ControllerBase
{
    // История снимков использования лимитов подписки + тариф + per-subscription (для экрана usage)
    [HttpGet]
    public IActionResult Get()
    {
        var all = usage.GetAll();
        var plan = usage.GetPlan();
        var bySub = usage.GetAllBySubscription();
        var ollamaInfo = BuildOllamaInfo();

        // Снимки окон лимитов сторонних CLI-провайдеров: их Anthropic-совместимые
        // эндпоинты тоже шлют rate_limit_event, и снимок пишется под ключ провайдера.
        // Отдаём отдельным блоком — вкладки провайдеров на экране usage показывают
        // те же окна (5ч/недельное, сброс), что и у Claude.
        var providerKeys = new HashSet<string>(providers.All.Select(p => p.Key), StringComparer.OrdinalIgnoreCase);
        Dictionary<string, IReadOnlyList<UsageSnapshot>>? providerSnaps = null;
        foreach (var (key, snaps) in bySub)
        {
            if (!providerKeys.Contains(key)) continue;
            providerSnaps ??= new Dictionary<string, IReadOnlyList<UsageSnapshot>>(StringComparer.OrdinalIgnoreCase);
            providerSnaps[key] = snaps;
        }

        // Статусы опроса api/oauth/usage per-аккаунт: по "unauthorized" вкладка честно
        // показывает «нужен claude login», а не гадает по свежести снимков
        var pollStatuses = oauthUsage.Statuses.Count > 0 ? oauthUsage.Statuses : null;

        // Для подписок из пула — проставляем DisplayName + статус роутинга (в ротации / выведен)
        if (subscriptionPool?.HasExtra == true)
        {
            // Показываем ВСЕ настроенные подписки пула (включая "claude", если она — подписка
            // с токеном), даже если снимков у аккаунта ещё нет: вкладке нужен статус опроса.
            // Чужие снапшоты per-subscription стора не попадают: ключи сторонних провайдеров
            // (уходят в блок Providers) и сироты после переименования аккаунта отсекаются.
            var named = new Dictionary<string, SubscriptionUsage>();
            foreach (var sub in subscriptionPool.All)
            {
                var key = sub.Key;
                var snaps = bySub.TryGetValue(key, out var s) ? s : new List<UsageSnapshot>();
                named[key] = new SubscriptionUsage(snaps, sub.DisplayName,
                    InRotation: subscriptionPool.IsInRotation(key),
                    Utilization: subscriptionPool.EffectiveUtilization(key),
                    Exhausted: subscriptionPool.IsExhausted(key),
                    Tier: subscriptionPool.TierLabel(key));
            }
            return Ok(new UsageResponse(all, plan, named, subscriptionPool.SoftThreshold, providerSnaps, ollamaInfo, pollStatuses));
        }

        return Ok(new UsageResponse(all, plan, null, null, providerSnaps, ollamaInfo, pollStatuses));
    }

    // Блок локальной модели: настройки Ollama + маршрут каждого фонового действия (локаль/claude)
    private OllamaUsageInfo BuildOllamaInfo()
    {
        var actions = LocalActionCatalog.All
            .Select(a =>
            {
                var route = localRouter.Resolve(a.Key);
                return new OllamaActionInfo(a.Key, a.Title, a.Group,
                    RoutedToOllama: route.Kind == RouteKind.Local && ollama.Enabled,
                    Source: route.Source.ToString().ToLowerInvariant(),
                    Route: route.Kind switch
                    {
                        RouteKind.Local => LocalActionOverridesStore.LocalRoute,
                        RouteKind.Claude => LocalActionOverridesStore.ClaudeRoute,
                        _ => route.Model ?? LocalActionOverridesStore.ClaudeRoute,
                    },
                    RequiresStrong: !a.DefaultLocal);
            })
            .ToList();
        return new OllamaUsageInfo(ollama.Enabled, ollama.Enabled ? ollama.TextModel : null,
            ollama.Enabled ? ollama.BaseUrl : null, actions);
    }
}
