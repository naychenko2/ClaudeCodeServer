using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace ClaudeHomeServer.Controllers;

[ApiController]
[Route("api/auth")]
public class AuthController(UserStore users, JwtService jwt, FeatureFlagService flags) : ControllerBase
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

        // Лениво вычисляем NT-хэш, если его ещё нет — нужен для NTLM WebDAV (Microsoft Office)
        if (req.Password is not null) users.EnsureNtHash(user, req.Password);

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
        var featureFlags = userId is null ? null : flags.GetEffective(userId);
        // Пороги индикатора контекста: override юзера или null (фронт применит дефолты)
        var contextThresholds = userId is null ? null : users.GetById(userId)?.ContextThresholds;
        return Ok(new { userId, username, role, featureFlags, contextThresholds });
    }

    // Пороги индикатора заполнения контекста (per-user). body null/пустой → сброс к дефолтам
    [Authorize]
    [HttpPut("context-thresholds")]
    public IActionResult SetContextThresholds([FromBody] ContextThresholdsRequest req)
    {
        var userId = User.FindFirstValue(JwtRegisteredClaimNames.Sub);
        if (userId is null) return Unauthorized();

        ContextThresholds? thresholds = null;
        if (req.WarnPct is not null || req.DangerPct is not null)
        {
            if (req.WarnPct is not (>= 1 and <= 99) || req.DangerPct is not (>= 1 and <= 99))
                return BadRequest(new { error = "Пороги должны быть в диапазоне 1–99" });
            if (req.WarnPct >= req.DangerPct)
                return BadRequest(new { error = "Порог предупреждения должен быть меньше порога тревоги" });
            thresholds = new ContextThresholds(req.WarnPct.Value, req.DangerPct.Value);
        }

        if (!users.SetContextThresholds(userId, thresholds)) return Unauthorized();
        return Ok(new { contextThresholds = thresholds });
    }

    [Authorize]
    [HttpPut("password")]
    public IActionResult ChangePassword([FromBody] ChangePasswordRequest req)
    {
        if (string.IsNullOrEmpty(req.NewPassword) || req.NewPassword.Length < 8)
            return BadRequest(new { error = "Новый пароль должен содержать не менее 8 символов" });

        var userId = User.FindFirstValue(JwtRegisteredClaimNames.Sub);
        if (userId is null) return Unauthorized();

        if (!users.ChangePassword(userId, req.CurrentPassword ?? "", req.NewPassword))
            return BadRequest(new { error = "Неверный текущий пароль" });

        return NoContent();
    }
}

public record LoginRequest(string? Username, string? Password);
public record ChangePasswordRequest(string? CurrentPassword, string NewPassword);
public record ContextThresholdsRequest(int? WarnPct, int? DangerPct);
