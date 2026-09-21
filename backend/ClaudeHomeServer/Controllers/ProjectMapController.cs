using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Docs;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ClaudeHomeServer.Controllers;

/// <summary>
/// Гигиена карты проекта (CLAUDE.md): факты сканера — размер, длинные секции, мёртвые
/// ссылки. Модель здесь не участвует, трат нет, ответ мгновенный.
///
/// Отдельный контроллер, а не ручка в DocsController: гигиена карты — не часть корпуса
/// документации (корневой CLAUDE.md в область панели по умолчанию даже не входит).
/// </summary>
[ApiController]
[Authorize]
[Route("api/projects/{id}/map-hygiene")]
public class ProjectMapController(ProjectManager projects, ProjectMapScanner scanner) : ControllerBase
{
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
}
