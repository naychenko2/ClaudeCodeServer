using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Docs;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ClaudeHomeServer.Controllers;

// Документация проекта для панели «Доки»: README.md + docs/**. Отдельно от FilesController,
// потому что отдаёт не файлы, а корпус — заголовки, якоря и связи между документами.
[ApiController]
[Authorize]
[Route("api/projects/{projectId}/docs")]
public class DocsController(DocsIndexService docs, ProjectManager projects) : ControllerBase
{
    // DefaultMapInboundClaims = false → sub читаем напрямую (как в FilesController)
    private string? UserId => User.FindFirstValue(JwtRegisteredClaimNames.Sub);

    // Проект текущего пользователя; чужой/несуществующий → 404, как у соседних контроллеров
    private ClaudeHomeServer.Models.Project GetProject(string projectId)
    {
        var p = projects.GetById(projectId);
        if (p is null || p.OwnerId != UserId)
            throw new KeyNotFoundException($"Проект не найден: {projectId}");
        return p;
    }

    // Индекс документов: путь, заголовок, подзаголовки со слагами, дата правки, размер
    [HttpGet]
    public IActionResult Index(string projectId)
    {
        try { return Ok(docs.GetIndex(GetProject(projectId).RootPath)); }
        catch (KeyNotFoundException) { return NotFound(); }
    }

    // Документ с содержимым и связями в обе стороны.
    // 404 на путь вне области документации — это гейт: без него эндпоинт стал бы вторым
    // универсальным файл-ридером поверх files/content, в обход его правил.
    [HttpGet("doc")]
    public IActionResult Doc(string projectId, [FromQuery] string path)
    {
        try
        {
            var detail = docs.GetDoc(GetProject(projectId).RootPath, path);
            return detail is null ? NotFound() : Ok(detail);
        }
        catch (KeyNotFoundException) { return NotFound(); }
    }

    [HttpGet("search")]
    public IActionResult Search(string projectId, [FromQuery] string q = "")
    {
        try { return Ok(docs.Search(GetProject(projectId).RootPath, q)); }
        catch (KeyNotFoundException) { return NotFound(); }
    }
}
