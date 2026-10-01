using System.Security.Claims;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.AudioEditor.Jobs;
using ClaudeHomeServer.Services.Composition;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ClaudeHomeServer.Services.AudioEditor.Controllers;

// Схема частных параметров модели для автоформы «Дополнительно» (ADR-021 §2): только модели из
// каталога поставщика. Схема не зависит от проекта и чата — ручка вне области; выключенный флаг — 404,
// как у остальных ручек модуля. Id модели fal содержит «/», поэтому всё — в query
[ApiController]
[Authorize]
[Route("api/audio-editor/schema")]
public sealed class AudioSchemaController(IFeatureFlagGate flags, AudioEditJobService jobs) : ControllerBase
{
    // Claim «sub» сервисного JWT; пакета System.IdentityModel у модуля нет (своих PackageReference не держим)
    private string UserId => User.FindFirstValue("sub")!;

    [HttpGet]
    public async Task<IActionResult> Get([FromQuery] string? provider, [FromQuery] string? model, [FromQuery] string? op,
        CancellationToken ct)
    {
        if (!flags.IsEnabled(UserId, FeatureFlagKeys.AudioEditor))
            return NotFound(new { error = "Модуль «Звук» выключен" });
        if (string.IsNullOrWhiteSpace(provider) || string.IsNullOrWhiteSpace(model) || string.IsNullOrWhiteSpace(op))
            return BadRequest(new { error = "Нужны provider, model и op", code = AudioEditErrorCodes.InvalidRequest });

        var result = await jobs.SchemaAsync(provider, model, op, ct);
        if (result.Value is { } schema) return Ok(schema);
        return result.ErrorCode == AudioEditErrorCodes.InvalidRequest
            ? NotFound(new { error = result.Error, code = result.ErrorCode })
            : StatusCode(StatusCodes.Status503ServiceUnavailable, new { error = result.Error, code = result.ErrorCode });
    }
}
