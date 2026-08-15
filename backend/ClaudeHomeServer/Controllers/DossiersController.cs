using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.CodeGraph;
using ClaudeHomeServer.Services.Dossiers;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ClaudeHomeServer.Controllers;

// Просмотр паспортов изменений (ADR-004, этап 1) + активный канал recall (этап 2 §5):
// поиск для MCP dossier_lookup и полная запись для dossier_get. Изоляция — владелец проекта
// (сервисный JWT памяти резолвит того же владельца, чужие записи не видны).
[ApiController]
[Authorize]
[Route("api/projects")]
public class DossiersController(ProjectManager projects, DossierStore store,
    CodeGraphService graphs, DossierRecallService recall) : ControllerBase
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

    // Поиск паспортов для MCP dossier_lookup (этап 2, ADR-004 §5): по пути, символу или
    // свободному тексту — в том числе archived (в пассивный канал они не идут, но найти
    // их можно). Один из фильтров обязателен; без фильтров — свежие сверху (лимит).
    [HttpGet("{id}/dossiers/lookup")]
    public IActionResult Lookup(string id, [FromQuery] string? path, [FromQuery] string? symbol,
        [FromQuery] string? query, [FromQuery] int limit = 20)
    {
        var p = projects.GetById(id);
        if (p is null || p.OwnerId != UserId) return NotFound();

        var capped = Math.Clamp(limit, 1, 50);
        return Ok(recall.Lookup(UserId, id, path, symbol, query, capped));
    }

    // Полная запись паспорта для MCP dossier_get (этап 2): по id, только свой проект.
    [HttpGet("{id}/dossiers/{dossierId}")]
    public IActionResult GetById(string id, string dossierId)
    {
        var p = projects.GetById(id);
        if (p is null || p.OwnerId != UserId) return NotFound();

        return store.GetById(UserId, id, dossierId) is { } dossier ? Ok(dossier) : NotFound();
    }
}
