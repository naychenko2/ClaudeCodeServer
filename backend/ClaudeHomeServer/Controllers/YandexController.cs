using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using ClaudeHomeServer.Services.Spend;
using ClaudeHomeServer.Services.Yandex;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ClaudeHomeServer.Controllers;

// Деньги Yandex Cloud: остаток на биллинг-аккаунте (Billing API) и расход на озвучку
// (наш собственный счётчик — Billing API разбивку по услугам не отдаёт вовсе).
//
// Баланс — кошелёк ИНСТАНСА, поэтому его видит только админ: то же правило, что у балансов
// LLM-провайдеров (ProviderBalance.WithoutMoney) — по умолчанию закрыто. Расход каждый видит
// свой, админ — по всем: ровно как в аналитике трат.
[ApiController]
[Authorize]
[Route("api/yandex")]
public class YandexController(YandexAccountService yandex, SpendAnalyticsService analytics)
    : ControllerBase
{
    private string CurrentUserId => User.FindFirstValue(JwtRegisteredClaimNames.Sub) ?? "";
    private bool IsAdmin => User.IsInRole("admin");

    [HttpGet("account")]
    public async Task<IActionResult> Account(int days = 30, CancellationToken ct = default)
    {
        var period = days is >= 1 and <= 365 ? days : 30;
        // Не-админу баланс не покажем — значит и в биллинг за ним ходить незачем:
        // остаётся только «настроено ли» и его собственный расход
        var res = IsAdmin
            ? await yandex.GetAsync(ct)
            : new YandexAccountResponse(yandex.Enabled, null, null, null);

        var to = DateOnly.FromDateTime(DateTime.UtcNow);
        var from = to.AddDays(-(period - 1));
        var spend = analytics.Rub(from, to, new SpendFilter(Owner: IsAdmin ? null : CurrentUserId));

        return Ok(new
        {
            enabled = res.Enabled,
            // Ошибку показываем всем: «баланс не настроен/не отвечает» секретом не является,
            // а без неё человек не поймёт, почему плашки нет
            error = res.Error,
            account = IsAdmin ? res.Account : null,
            asOf = res.AsOf,
            balanceHidden = !IsAdmin,
            days = period,
            spend,
        });
    }
}
