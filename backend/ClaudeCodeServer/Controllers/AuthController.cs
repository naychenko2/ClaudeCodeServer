using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using ClaudeCodeServer.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace ClaudeCodeServer.Controllers;

[ApiController]
[Route("api/auth")]
public class AuthController(UserStore users, JwtService jwt) : ControllerBase
{
    [AllowAnonymous]
    [EnableRateLimiting("auth-login")]
    [HttpPost("login")]
    public IActionResult Login([FromBody] LoginRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Username) || string.IsNullOrWhiteSpace(req.Password))
            return BadRequest(new { error = "Укажите имя пользователя и пароль" });

        var user = users.FindByUsername(req.Username);
        if (user is null || !users.VerifyPassword(user, req.Password))
            return Unauthorized(new { error = "Неверное имя пользователя или пароль" });

        var (token, expiresAt) = jwt.Issue(user);
        return Ok(new { token, expiresAt, username = user.Username });
    }

    [Authorize]
    [HttpGet("me")]
    public IActionResult Me()
    {
        // DefaultMapInboundClaims = false → sub не ремапится в NameIdentifier
        var userId = User.FindFirstValue(JwtRegisteredClaimNames.Sub);
        var username = User.FindFirstValue(ClaimTypes.Name);
        var role = User.FindFirstValue(ClaimTypes.Role);
        return Ok(new { userId, username, role });
    }
}

public record LoginRequest(string? Username, string? Password);
