using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using ClaudeHomeServer.Filters;
using ClaudeHomeServer.Services.Llm;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ClaudeHomeServer.Controllers;

// Серверная половина «Визуального разворота плана» (docs/plans/visual-plan.md, часть B):
// карта плана для разворота схемой. Сам план приезжает событием plan_review в чат — здесь
// только структурный слепок по кнопке «Собрать схему». Замечания к разделам уходят прежним
// onRespond(requestId, approve, feedback) карточки плана — их эндпоинта тут нет намеренно.
[ApiController]
[Authorize]
[Route("api/plans")]
public class PlansController(PlanMapService maps) : ControllerBase
{
    // Планы согласования — до ~35 КБ (класс задачи профиля Large у места plan-map);
    // потолок ловит случайную отправку мегабайтного файла вместо плана
    private const int MaxPlanLength = 256 * 1024;

    // DefaultMapInboundClaims = false → sub не ремапится в NameIdentifier, читаем напрямую
    private string UserId => User.FindFirstValue(JwtRegisteredClaimNames.Sub)!;

    // Текст плана → карта или 204 при неудаче (любой сбой молчит: фронт остаётся на тексте
    // плана, замечания на разделах работают и без карты). Сборка платная — one-shot модель,
    // поэтому закрыта от делегированных ходов, как прочие кнопки-действия.
    [HttpPost("map")]
    [DenyOnDelegatedTurn("Сборка карты плана")]
    public async Task<IActionResult> BuildMap([FromBody] PlanMapRequest req, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.Plan))
            return BadRequest(new { error = "Текст плана пуст" });
        if (req.Plan.Length > MaxPlanLength)
            return BadRequest(new { error = $"План слишком большой: {req.Plan.Length / 1024} КБ при потолке {MaxPlanLength / 1024} КБ" });
        try
        {
            var map = await maps.BuildMapAsync(UserId, req.Plan, ct);
            return map is null ? NoContent() : Ok(map);
        }
        catch (PlanMapInProgressException ex) { return Conflict(new { error = ex.Message }); }
    }
}

// Текст плана целиком (markdown, как пришёл событием plan_review)
public record PlanMapRequest(string Plan);
