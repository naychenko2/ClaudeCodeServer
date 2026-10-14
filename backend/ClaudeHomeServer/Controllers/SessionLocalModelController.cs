using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Llm;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ClaudeHomeServer.Controllers;

// Итог последнего хода на локальной модели для владельца чата (фича local-model-stats):
// расширенный чип Ctx показывает его после хода. [Authorize] без admin (в отличие от
// панели /api/local-model — та глобальная, а итог хода принадлежит чату).
//
// lastTurn: null — ходов на локальной модели не было (или рестарт бэкенда: счётчик
// ходов живёт в памяти). Чужая/неизвестная сессия — 404, не раскрывая существование
// (тот же приём, что SessionHandsController).
[ApiController]
[Authorize]
[Route("api/sessions")]
public class SessionLocalModelController(SessionManager sessions, IServiceProvider services) : ControllerBase
{
    // Счётчик хода необязателен: подсистема Llm может быть выключена (GetService → null).
    private readonly ILocalModelTurnMeter? _meter = services.GetService<ILocalModelTurnMeter>();

    private string? UserId => User.FindFirstValue(JwtRegisteredClaimNames.Sub);

    [HttpGet("{sessionId}/local-model")]
    public IActionResult Get(string sessionId)
    {
        var session = sessions.GetById(sessionId);
        if (session is null || sessions.ResolveOwnerId(session) != UserId) return NotFound();

        var last = _meter?.GetLastTurn(sessionId);
        return Ok(new LocalModelTurnResponse
        {
            LastTurn = last is null ? null : new LastTurnDto
            {
                GenTps = last.GenTps,
                // PrefillTps — движок пока не отчитывается (суммарного prefill-времени хода
                // нет) → null; поле в wire есть, фронт рисует «—».
                PrefillTps = null,
                TtftMs = last.TtftMs,
                QueueMsApprox = last.QueueMsApprox,
                CacheHitPerc = last.CacheHitPerc,
                OutTokens = last.OutTokens,
            },
        });
    }

    // Снимок ответа: lastTurn — null, если ходов не было.
    public sealed record LocalModelTurnResponse
    {
        public LastTurnDto? LastTurn { get; init; }
    }

    // Итог завершённого хода: суммы по запросам хода. Числа nullable — движок не
    // отдал поле (на экране «—»).
    public sealed record LastTurnDto
    {
        public double? GenTps { get; init; }
        public double? PrefillTps { get; init; }
        public int? TtftMs { get; init; }
        public int? QueueMsApprox { get; init; }
        public double? CacheHitPerc { get; init; }
        public int? OutTokens { get; init; }
    }
}
