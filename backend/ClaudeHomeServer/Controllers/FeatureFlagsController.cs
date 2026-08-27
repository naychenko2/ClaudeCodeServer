using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using ClaudeHomeServer.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ClaudeHomeServer.Controllers;

[ApiController]
[Authorize]
[Route("api/feature-flags")]
public class FeatureFlagsController(FeatureFlagService flags, UserStore users) : ControllerBase
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
    public IActionResult Set(string key, [FromBody] SetFlagRequest req)
    {
        if (UserId is null) return Unauthorized();

        // Через сервис, не каталог напрямую: динамические флаги модулей (module-{id}) тоже валидны
        if (!flags.Exists(key))
            return BadRequest(new { error = $"Неизвестный фич-флаг: {key}" });

        if (!users.SetFeatureFlag(UserId, key, req.Enabled))
            return Unauthorized();

        return Ok(new { values = flags.GetEffective(UserId) });
    }
}

public record SetFlagRequest(bool Enabled);
