using ClaudeHomeServer.Services.Llm;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ClaudeHomeServer.Controllers;

[ApiController]
[Authorize]
[Route("api/providers")]
public class ProvidersController(IProviderBalanceService balance) : ControllerBase
{
    private bool IsAdmin => User.IsInRole("admin");

    // Баланс аккаунта CLI-провайдера (кэш 5 мин); 404 — провайдер не настроен
    // или не имеет источника баланса. Не-админу режем кошелёк (см. WithoutMoney): баланс, валюту,
    // подарочный остаток, лимит ключа и расход провайдера — раздел показывает кошелёк владельца.
    [HttpGet("{key}/balance")]
    public async Task<IActionResult> GetBalance(string key, CancellationToken ct)
    {
        if (balance.GetSupported(key) is null) return NotFound(new { error = "Провайдер не настроен" });
        var result = await balance.GetAsync(key, ct);
        if (result is null) return StatusCode(502, new { error = "Баланс недоступен" });
        return Ok(IsAdmin ? (object)result : result.WithoutMoney());
    }

    // История баланса (снапшоты последних дней) — для экрана «Использование».
    // Обновляем текущий баланс перед отдачей, чтобы график включал свежую точку.
    // Не-админу история видна только для бесспорной квоты (IsQuota): деньги, пустая/неразобранная
    // валюта и сбой (current null) режутся — «по умолчанию закрыто», иначе можно раскрыть кошелёк.
    [HttpGet("{key}/usage")]
    public async Task<IActionResult> GetUsage(string key, CancellationToken ct)
    {
        if (balance.GetSupported(key) is null) return NotFound(new { error = "Провайдер не настроен" });
        var current = await balance.GetAsync(key, ct);
        var snapshots = balance.GetSnapshots(key);
        if (!IsAdmin && current is not { IsQuota: true })
            snapshots = [];
        object? balanceView = current is null ? null : (IsAdmin ? current : current.WithoutMoney());
        return Ok(new { balance = balanceView, snapshots });
    }
}
