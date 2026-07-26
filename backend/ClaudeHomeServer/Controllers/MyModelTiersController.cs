using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ClaudeHomeServer.Controllers;

[ApiController]
[Authorize]
[Route("api/me")]
public class MyModelTiersController(UserStore users) : ControllerBase
{
    private string? UserId => User.FindFirstValue(JwtRegisteredClaimNames.Sub);

    // Собственные per-user слоты тиров: null — наследовать глобальные слоты инстанса.
    [HttpGet("model-tiers")]
    public IActionResult Get()
    {
        if (UserId is null) return Unauthorized();
        var (strong, medium, weak) = users.GetModelTiers(UserId);
        return Ok(new ModelTiersDto(strong, medium, weak));
    }

    // Патч per-user слотов: null = не трогать, "" = очистить к наследованию.
    [HttpPut("model-tiers")]
    public IActionResult Put([FromBody] PatchModelTiersRequest req)
    {
        if (UserId is null) return Unauthorized();

        if (!users.SetModelTiers(UserId, req.Strong, req.Medium, req.Weak))
            return Unauthorized();

        var (strong, medium, weak) = users.GetModelTiers(UserId);
        return Ok(new ModelTiersDto(strong, medium, weak));
    }
}

public record ModelTiersDto(string? Strong, string? Medium, string? Weak);
public record PatchModelTiersRequest(string? Strong, string? Medium, string? Weak);
