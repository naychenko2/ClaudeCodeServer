using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Dossiers;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ClaudeHomeServer.Controllers;

// Просмотр паспортов изменений (ADR-004, этап 1). За флагом change-dossiers — гейтит
// эндпоинт, а не только панель (по образцу ReaderController/link-reader).
[ApiController]
[Authorize]
[Route("api/projects")]
public class DossiersController(ProjectManager projects, DossierStore store, FeatureFlagService flags) : ControllerBase
{
    private string UserId => User.FindFirstValue(JwtRegisteredClaimNames.Sub)!;

    [HttpGet("{id}/dossiers")]
    public IActionResult List(string id, [FromQuery] string? file, [FromQuery] string? symbol, [FromQuery] string? commit)
    {
        var p = projects.GetById(id);
        if (p is null || p.OwnerId != UserId) return NotFound();
        if (!flags.IsEnabled(UserId, FeatureFlagKeys.ChangeDossiers)) return Forbid();

        var result = string.IsNullOrWhiteSpace(file) && string.IsNullOrWhiteSpace(symbol) && string.IsNullOrWhiteSpace(commit)
            ? store.List(UserId, id).OrderByDescending(d => d.CommittedAt).ToList()
            : store.Find(UserId, id, file, symbol, commit);

        return Ok(result);
    }
}
