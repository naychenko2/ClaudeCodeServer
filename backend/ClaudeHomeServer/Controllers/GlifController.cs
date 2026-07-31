using ClaudeHomeServer.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ClaudeHomeServer.Controllers;

[ApiController]
[Authorize]
[Route("api/glif")]
public class GlifController(GlifAccountService glif) : ControllerBase
{
    // Статистика аккаунта glif.app: план + баланс (кредиты) + расход. Параметр days оставлен
    // для совместимости, но фильтрации по нему НЕТ: whoami отдаёт фиксированные окна
    // (last24h/last7d/last30d) — значение влияет только на ключ кэша сервиса.
    [HttpGet("account")]
    public async Task<IActionResult> Account([FromQuery] int days = 7)
        => Ok(await glif.GetAsync(days is >= 1 and <= 90 ? days : 7));
}
