using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ClaudeHomeServer.Controllers;

// «Итог сессии» — конспект сессии заметкой. Единый маршрут по id сессии:
// покрывает и проектные сессии, и чаты вне проекта (владение проверяет сервис).
[ApiController]
[Authorize]
[Route("api/sessions")]
public class SessionSummaryController(
    SessionSummaryService summary, ChatTaskExtractionService taskExtraction) : ControllerBase
{
    private string UserId => User.FindFirstValue(JwtRegisteredClaimNames.Sub)!;

    [HttpPost("{sessionId}/summary")]
    public async Task<ActionResult<NoteDetail>> Summarize(string sessionId, CancellationToken ct)
    {
        try
        {
            return Ok(await summary.SummarizeAsync(UserId, sessionId, ct));
        }
        catch (KeyNotFoundException) { return NotFound(); }
        catch (UnauthorizedAccessException) { return Forbid(); }
        catch (SummaryInProgressException ex) { return Conflict(new { error = ex.Message }); }
        // Подсистема заметок выключена — итог сохранять некуда. 503 + reason, а не 502:
        // 502 означает «апстрим сбойнул, повтори», а здесь повторять бессмысленно, пока
        // подсистему не включат. Форма ответа общая с BriefingController — одна причина
        // отказа обязана давать один наблюдаемый контракт.
        catch (SummaryUnavailableException ex)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable,
                new { error = ex.Message, reason = "notes_disabled" });
        }
        catch (SummaryGenerationException ex) { return StatusCode(502, new { error = ex.Message }); }
        catch (InvalidOperationException ex) { return BadRequest(new { error = ex.Message }); }
    }

    // «Задачи из чата» (флаг chat-extract-tasks): извлечь кандидатов в задачи из
    // транскрипта. Ничего не создаёт — фронт показывает диалог подтверждения.
    [HttpPost("{sessionId}/extract-tasks")]
    public async Task<ActionResult<ExtractTasksResult>> ExtractTasks(string sessionId, CancellationToken ct)
    {
        try
        {
            return Ok(await taskExtraction.ExtractAsync(UserId, sessionId, ct));
        }
        catch (KeyNotFoundException) { return NotFound(); }
        catch (UnauthorizedAccessException) { return Forbid(); }
        catch (InvalidOperationException ex) { return BadRequest(new { error = ex.Message }); }
    }
}
