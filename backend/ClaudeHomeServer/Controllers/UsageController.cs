using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ClaudeHomeServer.Controllers;

[ApiController]
[Authorize]
[Route("api/usage")]
public class UsageController(UsageService usage, ClaudeSubscriptionPool? subscriptionPool) : ControllerBase
{
    // История снимков использования лимитов подписки + тариф + per-subscription (для экрана usage)
    [HttpGet]
    public IActionResult Get()
    {
        var all = usage.GetAll();
        var plan = usage.GetPlan();
        var bySub = usage.GetAllBySubscription();

        // Для подписок из пула — проставляем DisplayName
        if (bySub.Count > 1 && subscriptionPool?.HasExtra == true)
        {
            var named = new Dictionary<string, SubscriptionUsage>();
            foreach (var (key, snaps) in bySub)
            {
                var displayName = key == "claude" ? null
                    : subscriptionPool.All.FirstOrDefault(s => s.Key == key)?.DisplayName;
                named[key] = new SubscriptionUsage(snaps, displayName);
            }
            return Ok(new UsageResponse(all, plan, named));
        }

        return Ok(new UsageResponse(all, plan, null));
    }
}
