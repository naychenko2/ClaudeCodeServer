using ClaudeHomeServer.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ClaudeHomeServer.Controllers;

[ApiController]
[Authorize]
[Route("api/fal")]
public class FalController(FalAccountService fal) : ControllerBase
{
    // Статистика аккаунта fal.ai: баланс + расход по моделям/дням за N дней (1..90)
    [HttpGet("account")]
    public async Task<IActionResult> Account([FromQuery] int days = 7)
        => Ok(await fal.GetAsync(days is >= 1 and <= 90 ? days : 7));
}
