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
//
// Зависимость от `ISpendAnalytics` опциональная: при выключенной подсистеме Spend (её сборка
// не загружена через `DynamicModules:N:Enabled=false`) DI-резолв вернёт null, и запросы,
// которым нужна аналитика расхода, отдают честный 503 — пользователь понимает, что подсистема
// выключена, а не получает «пустые данные» как при рабочей аналитике или 500.
[ApiController]
[Authorize]
[Route("api/yandex")]
public class YandexController(YandexAccountService yandex, ISpendAnalytics? analytics = null)
    : ControllerBase
{
    private string CurrentUserId => User.FindFirstValue(JwtRegisteredClaimNames.Sub) ?? "";
    private bool IsAdmin => User.IsInRole("admin");

    [HttpGet("account")]
    public async Task<IActionResult> Account(int days = 30, CancellationToken ct = default)
    {
        var period = days is >= 1 and <= 365 ? days : 30;

        // Без подсистемы Spend расход за окно собрать нечем — гейт ДО похода в биллинг:
        // админу без аналитики остаток бесполезен (503 + причина), и дёргать `yandex.GetAsync`
        // только ради поля `Enabled` смысла нет (это синглтон `iam.IsConfigured`).
        // Раньше 503 выдавался ПОСЛЕ `await yandex.GetAsync(ct)` — сетевой поход шёл
        // впустую и его результат отбрасывался (Александр, ревью c66e4127).
        if (analytics is null)
            return StatusCode(503, new { error = "Подсистема аналитики расхода выключена" });

        // Биллинг ходим только если он нужен: админу для баланса, остальным — фича выключена.
        // Не-админу без аналитики мы уже отрезали выше; здесь `IsAdmin => биллинг`,
        // иначе — лёгкий ответ без сетевого похода.
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
