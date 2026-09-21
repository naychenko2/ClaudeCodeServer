using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Docs;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ClaudeHomeServer.Controllers;

/// <summary>
/// Гигиена карты проекта (CLAUDE.md). Две ручки по числу фаз: <c>scan</c> — факты
/// сканера (мгновенно, без модели и без трат), <c>review</c> — формулировки модели
/// поверх тех же фактов (ход до минут).
///
/// Отдельный контроллер, а не ручка в DocsController: гигиена карты — не часть корпуса
/// документации (корневой CLAUDE.md в область панели по умолчанию даже не входит).
/// </summary>
[ApiController]
[Authorize]
[Route("api/projects/{id}/map-hygiene")]
public class ProjectMapController(
    ProjectManager projects, ProjectMapScanner scanner, ProjectMapReviewService review) : ControllerBase
{
    // BaseSha — отпечаток карты с момента скана: по нему видно, что человек смотрит на
    // тот же файл, который сейчас разбирает сервер
    public record ReviewRequest(string? BaseSha);

    private string UserId => User.FindFirstValue(JwtRegisteredClaimNames.Sub)!;

    // Чужой проект — 404, а не 403: подтверждать его существование незачем (как у соседей).
    // ct — RequestAborted: скан обходит дерево проекта целиком, и ушедший клиент не должен
    // оставлять обход работать
    [HttpGet("scan")]
    public ActionResult Scan(string id, CancellationToken ct)
    {
        var project = projects.GetById(id);
        if (project is null || project.OwnerId != UserId) return NotFound();

        return Ok(scanner.Scan(project.RootPath, ct));
    }

    /// <summary>
    /// Формулировки модели поверх фактов свежего скана.
    ///
    /// BaseSha сверяется ДО обращения к модели и отвечает 409 так же, как применение
    /// правок: ход идёт до минут, и за это время карту в общем дереве вполне могут
    /// дописать — тогда суждения приехали бы по новому составу поверх старых фактов на
    /// экране, то есть с другими id и с чекбоксами, тихо переставшими совпадать с
    /// находками. Отказ здесь дёшев: человек видит «файл изменился · Проверить заново».
    /// </summary>
    [HttpPost("review")]
    public async Task<ActionResult> Review(string id, [FromBody] ReviewRequest? body, CancellationToken ct)
    {
        var project = projects.GetById(id);
        if (project is null || project.OwnerId != UserId) return NotFound();

        var report = scanner.Scan(project.RootPath, ct);
        // Карты нет вовсе (удалили или переименовали, пока отчёт был открыт) — тот же 409
        // и та же плашка: для человека случай неотличим от «карту подменили»
        if (report.BaseSha is null || !string.Equals(report.BaseSha, body?.BaseSha, StringComparison.Ordinal))
            return Conflict(new { error = "staleBaseSha", baseSha = report.BaseSha });

        return Ok(await review.ReviewAsync(report, UserId, ct));
    }
}
