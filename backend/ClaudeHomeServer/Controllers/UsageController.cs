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
    LlmProviderRegistry providers, IConfiguration config) : ControllerBase
{
    // История снимков использования лимитов подписки + тариф + per-subscription (для экрана usage)
    [HttpGet]
    public IActionResult Get()
    {
        var all = usage.GetAll();
        var plan = usage.GetPlan();
        var bySub = usage.GetAllBySubscription();

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

        // Для подписок из пула — проставляем DisplayName + статус роутинга (в ротации / выведен)
        if (bySub.Count > 1 && subscriptionPool?.HasExtra == true)
        {
            // Показываем только ключи настроенных подписок пула (включая "claude", если она —
            // подписка с токеном). Отсекаем чужие снапшоты в per-subscription сторе: ключи
            // сторонних провайдеров (уходят в блок Providers) и сирот после переименования аккаунта.
            var poolKeys = new HashSet<string>(subscriptionPool.All.Select(s => s.Key), StringComparer.Ordinal);

            var named = new Dictionary<string, SubscriptionUsage>();
            foreach (var (key, snaps) in bySub)
            {
                if (!poolKeys.Contains(key)) continue;
                var displayName = subscriptionPool.All.FirstOrDefault(s => s.Key == key)?.DisplayName;
                var tier = subscriptionPool.TierLabel(key);
                named[key] = new SubscriptionUsage(snaps, displayName,
                    InRotation: subscriptionPool.IsInRotation(key),
                    Utilization: subscriptionPool.EffectiveUtilization(key),
                    Exhausted: subscriptionPool.IsExhausted(key),
                    Tier: tier);
            }
            return Ok(new UsageResponse(all, plan, named, subscriptionPool.SoftThreshold, providerSnaps));
        }

        return Ok(new UsageResponse(all, plan, null, null, providerSnaps));
    }
}
