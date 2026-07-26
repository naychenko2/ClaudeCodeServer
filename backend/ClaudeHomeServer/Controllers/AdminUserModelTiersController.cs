using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ClaudeHomeServer.Controllers;

[ApiController]
[Authorize(Roles = "admin")]
[Route("api/admin/users")]
public class AdminUserModelTiersController(UserStore users) : ControllerBase
{
    // Per-user слоты тиров любого пользователя (admin): null — наследовать глобальные слоты инстанса.
    [HttpGet("{userId}/model-tiers")]
    public IActionResult Get(string userId)
    {
        if (users.GetById(userId) is null) return NotFound();
        var (strong, medium, weak) = users.GetModelTiers(userId);
        return Ok(new ModelTiersDto(strong, medium, weak));
    }

    // Патч per-user слотов: null = не трогать, "" = очистить к наследованию.
    [HttpPut("{userId}/model-tiers")]
    public IActionResult Put(string userId, [FromBody] PatchModelTiersRequest req)
    {
        if (users.GetById(userId) is null) return NotFound();

        if (!users.SetModelTiers(userId, req.Strong, req.Medium, req.Weak))
            return NotFound();

        var (strong, medium, weak) = users.GetModelTiers(userId);
        return Ok(new ModelTiersDto(strong, medium, weak));
    }
}
