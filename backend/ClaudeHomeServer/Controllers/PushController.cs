using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using ClaudeHomeServer.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ClaudeHomeServer.Controllers;

// Управление web-push подписками устройств пользователя
[ApiController]
[Authorize]
[Route("api/push")]
public class PushController(PushService push, PushSubscriptionStore store) : ControllerBase
{
    private string UserId => User.FindFirstValue(JwtRegisteredClaimNames.Sub)!;

    // Публичный VAPID-ключ — нужен браузеру для pushManager.subscribe()
    [HttpGet("vapid-public-key")]
    public IActionResult VapidPublicKey() => Ok(new { publicKey = push.PublicKey });

    [HttpPost("subscribe")]
    public IActionResult Subscribe([FromBody] PushSubscribeRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Endpoint) ||
            string.IsNullOrWhiteSpace(req.P256dh) ||
            string.IsNullOrWhiteSpace(req.Auth))
            return BadRequest(new { error = "Неполная push-подписка" });

        store.Upsert(UserId, new PushSubscriptionRecord
        {
            Endpoint = req.Endpoint,
            P256dh = req.P256dh,
            Auth = req.Auth,
            UserAgent = Request.Headers.UserAgent.ToString(),
        });
        return NoContent();
    }

    [HttpPost("unsubscribe")]
    public IActionResult Unsubscribe([FromBody] PushUnsubscribeRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Endpoint))
            return BadRequest(new { error = "Не указан endpoint" });
        store.Remove(UserId, req.Endpoint);
        return NoContent();
    }
}

public record PushSubscribeRequest(string? Endpoint, string? P256dh, string? Auth);
public record PushUnsubscribeRequest(string? Endpoint);
