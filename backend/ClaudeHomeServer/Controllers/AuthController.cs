using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text.Json;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Composition;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace ClaudeHomeServer.Controllers;

[ApiController]
[Route("api/auth")]
public class AuthController(UserStore users, JwtService jwt, FeatureFlagService flags,
    PersonaManager personas, SubsystemStateStore subsystems) : ControllerBase
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
        var cacheWarnThresholds = me?.CacheWarnThresholds;
        var executionEnvironment = me?.ExecutionEnvironment ?? ExecutionEnvironments.Local;
        // Дефолт-персона. Нормализация сироты на чтении:
        // DefaultPersonaId с несуществующей персоной отдаём как null — тогда онбординг-гейт
        // сам чинит осиротевший дефолт вместо молчаливого нарушения «остаться без дефолта нельзя».
        // ВАЖНО: здесь ТОЛЬКО чтение — провижн ассистента на GET НЕ вызывается НИКОГДА (план 2.5,
        // принцип 3): мутации живут на точках записи, /me остаётся чистым синхронным чтением.
        var defaultPersona = me?.DefaultPersonaId is { } dpid && userId is not null
            ? personas.Get(dpid, userId) : null;
        var defaultPersonaId = defaultPersona?.Id;
        // needsOnboarding — «предложить знакомство»: знакомство не пройдено и дефолт —
        // это НЕТРОНУТАЯ заготовка (DefaultPersonaId == AssistantPersonaId), причём она
        // резолвится в ЖИВУЮ персону. Резолв, а не проверка поля на null, критичен: мёртвый
        // id (заготовку удалили) не должен ни зажигать метку, ни запирать знакомство навсегда.
        // Полноэкранного гейта нет — признак лишь зажигает карточку-приглашение.
        var needsOnboarding = userId is not null
            && me?.IntroCompletedAt is null
            && me?.DefaultPersonaId is not null
            && me?.DefaultPersonaId == me?.AssistantPersonaId
            && defaultPersona is not null;
        // displayName отдаём и здесь: имя могли поправить в users.json уже после логина,
        // а перевыпускать токен ради этого незачем
        // Активные подсистемы: список ключей, реально зарегистрированных в текущем
        // процессе. Снимок берётся из `SubsystemStateStore` — он наполняется на старте
        // через `AddSubsystems` и не меняется в рантайме (гейт одноразовый).
        return Ok(new
        {
            userId, username, displayName = me?.DisplayName, role, featureFlags,
            contextThresholds, cacheWarnThresholds, executionEnvironment,
            defaultPersonaId, needsOnboarding, onboardingSessionId = me?.OnboardingSessionId,
            subsystems = subsystems.ActiveKeys(),
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

    // Порог «кэш промпта скоро остынет» (per-user, минуты остатка). Менять может только
    // свой профиль: userId берётся из токена. Контракт тела: сброс — только пустое тело
    // `{}` (или оба поля null); любое имя, кроме warn1hMin/warn5mMin, — 400, а не тихий
    // сброс: опечатка или чужая единица (секунды) не должна молча выключать настройку.
    [Authorize]
    [HttpPut("cache-thresholds")]
    public IActionResult SetCacheWarnThresholds([FromBody] JsonElement body)
    {
        var userId = User.FindFirstValue(JwtRegisteredClaimNames.Sub);
        if (userId is null) return Unauthorized();

        // null-тело — тот же сброс, что `{}`: «оба поля null». Всё, что не объект, — 400
        if (body.ValueKind is not (JsonValueKind.Object or JsonValueKind.Null))
            return BadRequest(new { error = "Тело — объект {\"warn1hMin\": ..., \"warn5mMin\": ...} в минутах или {} для сброса" });

        CacheWarnThresholds? thresholds = null;
        if (body.ValueKind == JsonValueKind.Object)
        {
            int? warn1h = null, warn5m = null;
            foreach (var prop in body.EnumerateObject())
            {
                if (prop.Value.ValueKind is not (JsonValueKind.Null or JsonValueKind.Number)
                    || (prop.Value.ValueKind == JsonValueKind.Number && !prop.Value.TryGetInt32(out _)))
                    return BadRequest(new { error = "Пороги — целые числа в минутах или null" });

                if (string.Equals(prop.Name, "warn1hMin", StringComparison.OrdinalIgnoreCase))
                    warn1h = prop.Value.ValueKind == JsonValueKind.Null ? null : prop.Value.GetInt32();
                else if (string.Equals(prop.Name, "warn5mMin", StringComparison.OrdinalIgnoreCase))
                    warn5m = prop.Value.ValueKind == JsonValueKind.Null ? null : prop.Value.GetInt32();
                else
                    return BadRequest(new { error = $"Неизвестное поле \"{prop.Name}\" — допустимы только warn1hMin и warn5mMin (минуты)" });
            }

            if (warn1h is not null || warn5m is not null)
            {
                if (warn1h is not { } a || warn5m is not { } b || !CacheWarnThresholds.InRange(a, b))
                    return BadRequest(new
                    {
                        error = $"Порог часового кэша — {CacheWarnThresholds.MinMin}–{CacheWarnThresholds.Max1hMin} мин, " +
                              $"5-минутного — {CacheWarnThresholds.MinMin}–{CacheWarnThresholds.Max5mMin} мин",
                    });
                thresholds = new CacheWarnThresholds(a, b);
            }
        }

        if (!users.SetCacheWarnThresholds(userId, thresholds)) return Unauthorized();
        return Ok(new { cacheWarnThresholds = thresholds });
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
