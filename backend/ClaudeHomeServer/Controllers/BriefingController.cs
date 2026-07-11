using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ClaudeHomeServer.Controllers;

// Утренний бриф-агент (флаг daily-briefing). On-demand генерация плана дня в дневник.
[ApiController]
[Authorize]
[Route("api/briefing")]
public class BriefingController(DailyBriefingService briefing) : ControllerBase
{
    private string UserId => User.FindFirstValue(JwtRegisteredClaimNames.Sub)!;

    // Собрать бриф на дату (локальная дата клиента; пусто — сегодня в таймзоне юзера)
    // и записать в дневниковую заметку. Возвращает обновлённую заметку.
    [HttpPost("today")]
    public async Task<ActionResult<NoteDetail>> Today([FromBody] DailyNoteRequest? req, CancellationToken ct)
    {
        var note = await briefing.GenerateAsync(UserId, req?.Date, ct);
        return Ok(note);
    }
}
