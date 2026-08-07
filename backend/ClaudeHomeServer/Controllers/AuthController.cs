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
public class AuthController(UserStore users, JwtService jwt, FeatureFlagService flags,
    PersonaManager personas) : ControllerBase
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
        return Ok(new { token, expiresAt, username = user.Username, displayName = user.DisplayName });
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
        var me = userId is null ? null : users.GetById(userId);
        var contextThresholds = me?.ContextThresholds;
        var executionEnvironment = me?.ExecutionEnvironment ?? ExecutionEnvironments.Local;
        // Дефолт-персона (фича default-personas-onboarding). Нормализация сироты на чтении:
        // DefaultPersonaId с несуществующей персоной отдаём как null — онбординг-гейт сам
        // чинит осиротевший дефолт вместо молчаливого нарушения «остаться без дефолта нельзя»
        var defaultPersonaId = me?.DefaultPersonaId is { } dpid && userId is not null
            && personas.Get(dpid, userId) is not null ? dpid : null;
        // needsOnboarding — ГЛОБАЛЬНЫЙ гейт первого входа: «у пользователя нет личной
        // дефолт-персоны». Намеренно не учитывает проектный дефолт (руководителя проекта):
        // проектов может быть много, и сводить «есть ли хоть один без руководителя» в один
        // флаг на auth/me — смешивать два независимых гейта. Проектный онбординг — локальное
        // решение экрана: WorkspacePage смотрит project.defaultPersonaId и открывает свой гейт
        // точечно по открытому проекту, не опираясь на этот флаг.
        var needsOnboarding = userId is not null && defaultPersonaId is null
            && flags.IsEnabled(userId, FeatureFlagKeys.DefaultPersonasOnboarding);
        // displayName отдаём и здесь: имя могли поправить в users.json уже после логина,
        // а перевыпускать токен ради этого незачем
        return Ok(new
        {
            userId, username, displayName = me?.DisplayName, role, featureFlags,
            contextThresholds, executionEnvironment,
            defaultPersonaId, needsOnboarding, onboardingSessionId = me?.OnboardingSessionId,
        });
    }

    // Таймзона пользователя (IANA, например "Europe/Moscow") — фронт шлёт при старте.
    // Нужна планировщику напоминаний для перевода локальных сроков задач в UTC.
    [Authorize]
    [HttpPut("timezone")]
    public IActionResult SetTimeZone([FromBody] TimeZoneRequest req)
    {
        var userId = User.FindFirstValue(JwtRegisteredClaimNames.Sub);
        if (userId is null) return Unauthorized();

        if (string.IsNullOrWhiteSpace(req.TimeZone) ||
            !TimeZoneInfo.TryFindSystemTimeZoneById(req.TimeZone.Trim(), out _))
            return BadRequest(new { error = "Неизвестная таймзона" });

        if (!users.SetTimeZone(userId, req.TimeZone.Trim())) return Unauthorized();
        return NoContent();
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

        // Смена пароля отозвала ВСЕ токены пользователя, включая текущий — иначе автор смены
        // разлогинил бы сам себя. Отдаём свежий: перечитываем юзера, чтобы взять новую версию
        var user = users.GetById(userId);
        if (user is null) return Unauthorized();
        var (token, expiresAt) = jwt.Issue(user);
        return Ok(new { token, expiresAt });
    }
}

public record LoginRequest(string? Username, string? Password);
public record ChangePasswordRequest(string? CurrentPassword, string NewPassword);
public record ContextThresholdsRequest(int? WarnPct, int? DangerPct);
public record TimeZoneRequest(string? TimeZone);
