using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Docs;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ClaudeHomeServer.Controllers;

/// <summary>
//// Гигиена карты проекта (CLAUDE.md). Три ручки по числу фаз:
///  • <c>scan</c> — факты сканера, мгновенно, без модели и без трат (волна 1).
///  • <c>review</c> — формулировки модели поверх тех же фактов, ход до минут (волна 2).
///  • <c>apply</c> — применить отмеченные механические правки к файлу (волна 4).
///
/// Отдельный контроллер, а не ручка в DocsController: гигиена карты — не часть
/// корпуса документации (корневой CLAUDE.md в область панели по умолчанию даже не
/// входит). <c>scan</c> открыт без гейта: ничего не меняет и работает у теста
/// волны 1 (план §11: «не обязательно, решает исполнитель»). <c>review</c> и
/// <c>apply</c> закрыты флагом — пока он выключен, для пользователя этой фичи
/// не существует (NotFound, не Forbid). <c>apply</c> пишет в CLAUDE.md, и открытая
/// ручка записи при выключенной фиче была бы лишним риском.
/// </summary>
[ApiController]
[Authorize]
[Route("api/projects/{id}/map-hygiene")]
public class ProjectMapController(
    ProjectManager projects, ProjectMapScanner scanner, FeatureFlagService flags) : ControllerBase
{
    private string UserId => User.FindFirstValue(JwtRegisteredClaimNames.Sub)!;

    // Гейт фичи — на ручках записи (review, apply). scan не гейтится (план §11: «не
    // обязательно»), и тест волны 1 не предполагает флага — единообразие тут
    // спорит с поведением существующего теста
    private bool FeatureEnabled() =>
        flags.IsEnabled(UserId, FeatureFlagKeys.ProjectMapHygiene);

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

    // Заглушка: полная реализация — задача волны 2 (формулировки модели). Здесь
    // только гейт и 501 «сервер знает метод, но ещё не реализовал», чтобы UI при
    // выключенной фиче получал 404, а при включённой без воли 2 — 501 (а не молчание
    // или падение). Денис заменит тело метода, гейт останется.
    [HttpPost("review")]
    public ActionResult Review(string id, [FromBody] object? body, CancellationToken ct)
    {
        if (!FeatureEnabled()) return NotFound();
        // TODO(волна 2): baseSha, сшивка фактов с суждениями модели, NotFound на
        // чужой проект и 409 на устаревший baseSha — по плану Р9
        return StatusCode(StatusCodes.Status501NotImplemented);
    }

    // Заглушка: полная реализация — задача волны 4 (контракт записи Р10а: read-verify-write
    // под локом, temp + File.Move, уникальность якоря вне кодовых заборов, BOM и
    // окончания строк). Здесь только гейт + 501 — см. Review.
    [HttpPost("apply")]
    public ActionResult Apply(string id, [FromBody] object? body, CancellationToken ct)
    {
        if (!FeatureEnabled()) return NotFound();
        // TODO(волна 4)
        return StatusCode(StatusCodes.Status501NotImplemented);
    }
}
