using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.CodeGraph;
using ClaudeHomeServer.Services.Dossiers;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ClaudeHomeServer.Controllers;

// Просмотр паспортов изменений (ADR-004, этап 1).
[ApiController]
[Authorize]
[Route("api/projects")]
public class DossiersController(ProjectManager projects, DossierStore store,
    CodeGraphService graphs) : ControllerBase
{
    private string UserId => User.FindFirstValue(JwtRegisteredClaimNames.Sub)!;

    [HttpGet("{id}/dossiers")]
    public IActionResult List(string id, [FromQuery] string? file, [FromQuery] string? symbol, [FromQuery] string? commit)
    {
        var p = projects.GetById(id);
        if (p is null || p.OwnerId != UserId) return NotFound();

        var result = string.IsNullOrWhiteSpace(file) && string.IsNullOrWhiteSpace(symbol) && string.IsNullOrWhiteSpace(commit)
            ? store.List(UserId, id).OrderByDescending(d => d.CommittedAt).ToList()
            : store.Find(UserId, id, file, symbol, commit);

        // Символы в досье якорятся по снимку кодографа (DossierCaptureService.AnchorAsync):
        // пока граф корня не построен, свежие досье остаются на файловом уровне. Сигналим
        // фронту тем же заголовком, что и CodeGraphController на «граф строится» — дешёвым
        // mtime-чеком (GetCacheSignature, без загрузки graph.json). Заголовок аддитивен:
        // фронт, не использующий его, ничего не замечает.
        if (graphs.GetCacheSignature(p.RootPath) is null)
            Response.Headers["X-CodeGraph-Building"] = "true";

        return Ok(result);
    }
}
