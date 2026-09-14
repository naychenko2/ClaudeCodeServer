using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Tasks;
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
        try
        {
            var note = await briefing.GenerateAsync(UserId, req?.Date, ct);
            return Ok(note);
        }
        // Подсистема заметок выключена — бриф писать некуда. 503, а не 500: это состояние
        // инстанса, а не сбой; форма ответа — как у прочих "не настроено на этом сервере"
        // (McpCatalogController, TtsController). Гейт нужен на сервере независимо от того,
        // что фронт прячет кнопку брифа (`notesOn` в lib/ai/actions.tsx): REST дёргают и мимо UI.
        catch (BriefingUnavailableException ex)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable,
                new { error = ex.Message, reason = "notes_disabled" });
        }
    }
}
