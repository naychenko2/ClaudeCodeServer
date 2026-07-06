using ClaudeHomeServer.Services.Llm.DeepSeek;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ClaudeHomeServer.Controllers;

[ApiController]
[Authorize]
[Route("api/providers")]
public class ProvidersController(DeepSeekBalanceService deepSeekBalance) : ControllerBase
{
    // Баланс аккаунта DeepSeek (кэш 5 мин); 404 — провайдер не настроен
    [HttpGet("deepseek/balance")]
    public async Task<IActionResult> GetDeepSeekBalance(CancellationToken ct)
    {
        if (!deepSeekBalance.Enabled) return NotFound(new { error = "DeepSeek не настроен" });
        var balance = await deepSeekBalance.GetAsync(ct);
        return balance is null
            ? StatusCode(502, new { error = "Баланс недоступен" })
            : Ok(balance);
    }
}
