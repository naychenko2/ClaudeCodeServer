using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ClaudeHomeServer.Controllers;

[ApiController]
[Authorize]
[Route("api/feature-flags")]
public class FeatureFlagsController(FeatureFlagService flags, UserStore users,
    DefaultAssistantProvisioner provisioner,
    ClaudeHomeServer.Services.Backgrounds.ProjectBackgroundBackfill backgroundBackfill) : ControllerBase
{
    private string? UserId => User.FindFirstValue(JwtRegisteredClaimNames.Sub);

    // Реестр определений + эффективные значения текущего юзера
    [HttpGet]
    public IActionResult Get()
    {
        if (UserId is null) return Unauthorized();
        return Ok(new
        {
            definitions = flags.GetDefinitions(),
            values = flags.GetEffective(UserId),
        });
    }

    // Включить/выключить флаг для текущего юзера
    [HttpPut("{key}")]
    public async Task<IActionResult> Set(string key, [FromBody] SetFlagRequest req)
    {
        if (UserId is null) return Unauthorized();

        // Через сервис, не каталог напрямую: динамические флаги модулей (module-{id}) тоже валидны
        if (!flags.Exists(key))
            return BadRequest(new { error = $"Неизвестный фич-флаг: {key}" });

        if (!users.SetFeatureFlag(UserId, key, req.Enabled))
            return Unauthorized();

        // Включение флага default-personas-onboarding в рантайме (dark-launch) — основная точка
        // провижна сегодня: сразу заводим ассистента, чтобы метки и аватар-лицо появились без
        // перезагрузки (EnsureAsync шлёт broadcast created+default, а фронт после успешного PUT
        // зовёт refreshMe). План 2.2. Выключение флага персоны не трогает (план 3г).
        if (req.Enabled && key == FeatureFlagKeys.DefaultPersonasOnboarding)
            await provisioner.EnsureAsync(UserId);

        // Включение флага фонов — штатный триггер разовой генерации существующим проектам
        // (ADR-008 §10). В фоне: 39 вызовов сильной модели в HTTP-запрос не укладываются,
        // а прогон идемпотентен — повторное включение флага ничего не перетирает
        if (req.Enabled && key == FeatureFlagKeys.ProjectBackgrounds)
            backgroundBackfill.KickOff(UserId);

        return Ok(new { values = flags.GetEffective(UserId) });
    }
}

public record SetFlagRequest(bool Enabled);
