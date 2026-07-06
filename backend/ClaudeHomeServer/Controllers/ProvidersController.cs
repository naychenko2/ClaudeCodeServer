using ClaudeHomeServer.Services.Llm;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ClaudeHomeServer.Controllers;

[ApiController]
[Authorize]
[Route("api/providers")]
public class ProvidersController(ProviderBalanceService balance) : ControllerBase
{
    // Баланс аккаунта CLI-провайдера (кэш 5 мин); 404 — провайдер не настроен
    // или не имеет источника баланса
    [HttpGet("{key}/balance")]
    public async Task<IActionResult> GetBalance(string key, CancellationToken ct)
    {
        if (balance.GetSupported(key) is null) return NotFound(new { error = "Провайдер не настроен" });
        var result = await balance.GetAsync(key, ct);
        return result is null
            ? StatusCode(502, new { error = "Баланс недоступен" })
            : Ok(result);
    }

    // История баланса (снапшоты последних дней) — для экрана «Использование».
    // Обновляем текущий баланс перед отдачей, чтобы график включал свежую точку.
    [HttpGet("{key}/usage")]
    public async Task<IActionResult> GetUsage(string key, CancellationToken ct)
    {
        if (balance.GetSupported(key) is null) return NotFound(new { error = "Провайдер не настроен" });
        var current = await balance.GetAsync(key, ct);
        return Ok(new { balance = current, snapshots = balance.GetSnapshots(key) });
    }
}
