using ClaudeCodeServer.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace ClaudeCodeServer.Controllers;

[ApiController]
[Route("api/auth")]
public class AuthController(ApiKeyAuthService auth) : ControllerBase
{
    // Анонимный: проверяет ключ, введённый на странице входа.
    // Ключ принимается из тела (страница входа) или из заголовка Authorization.
    [AllowAnonymous]
    [EnableRateLimiting("auth-ping")]
    [HttpPost("ping")]
    public IActionResult Ping([FromBody] PingRequest req)
    {
        var key = req.ApiKey;
        if (string.IsNullOrWhiteSpace(key))
        {
            var authHeader = Request.Headers.Authorization.ToString();
            if (authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
                key = authHeader["Bearer ".Length..].Trim();
        }

        if (!auth.Validate(key))
            return Unauthorized(new { error = "Неверный API-ключ" });

        return Ok(new { ok = true });
    }
}

public record PingRequest(string? ServerUrl, string? ApiKey);
