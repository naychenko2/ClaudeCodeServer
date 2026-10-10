using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using ClaudeHomeServer.Services.Composition;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ClaudeHomeServer.Services.ImageEditor.Controllers;

// Занятость видеокарты обучением на уровне инстанса (ADR-026-contracts §7.4): имя владельца
// не отдаём никому; отменить чужое обучение может только админ, роль проверяется здесь.
[ProjectCapability(ProjectCapabilityArea.Platform)]
[ApiController]
[Authorize]
[Route("api/image-editor/lora-train/active")]
public class LoraTrainAdminController(ILocalLoraTrainer? trainer = null) : ControllerBase
{
    private string UserId => User.FindFirstValue(JwtRegisteredClaimNames.Sub)!;

    [HttpGet]
    public IActionResult Active()
    {
        if (trainer is null) return NoSeam();
        var busy = trainer.Busy(UserId);
        var admin = User.IsInRole("admin");
        return Ok(new
        {
            busy = busy is not null,
            mine = busy?.Mine ?? false,
            busyUntil = busy?.BusyUntil,
            jobId = admin ? busy?.JobId : null,
        });
    }

    [HttpDelete]
    public async Task<IActionResult> CancelActive(CancellationToken ct)
    {
        if (!User.IsInRole("admin"))
            return StatusCode(StatusCodes.Status403Forbidden, new { error = "Отменять чужое обучение может только админ", code = "forbidden" });
        if (trainer is null) return NoSeam();
        if (trainer.Busy(UserId) is not { } busy)
            return StatusCode(StatusCodes.Status404NotFound, new { error = "Обучение не идёт", code = "not_training" });
        var result = await trainer.CancelAsync(UserId, busy.JobId, asAdmin: true, ct);
        return result == LoraCancelResult.NotFound
            ? StatusCode(StatusCodes.Status404NotFound, new { error = "Обучение не идёт", code = "not_training" })
            : NoContent();
    }

    private IActionResult NoSeam() =>
        StatusCode(StatusCodes.Status503ServiceUnavailable,
            new { error = "Обучение LoRA недоступно: подсистема картинок выключена", code = "no_seam" });
}
