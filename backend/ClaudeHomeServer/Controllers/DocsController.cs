using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using ClaudeHomeServer.Models;
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
    private Project GetProject(string projectId)
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
        try
        {
            var p = GetProject(projectId);
            return Ok(docs.GetIndex(p.RootPath, DocsIndexService.ScopeOf(p)));
        }
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
            var p = GetProject(projectId);
            var detail = docs.GetDoc(p.RootPath, path, DocsIndexService.ScopeOf(p));
            return detail is null ? NotFound() : Ok(detail);
        }
        catch (KeyNotFoundException) { return NotFound(); }
    }

    [HttpGet("search")]
    public IActionResult Search(string projectId, [FromQuery] string q = "")
    {
        try
        {
            var p = GetProject(projectId);
            return Ok(docs.Search(p.RootPath, q, DocsIndexService.ScopeOf(p)));
        }
        catch (KeyNotFoundException) { return NotFound(); }
    }

    // Настройка области: что выбрано, что можно выбрать (папки, файлы корня, типы файлов)
    [HttpGet("scope")]
    public IActionResult Scope(string projectId)
    {
        try
        {
            var p = GetProject(projectId);
            return Ok(docs.Describe(p.RootPath, DocsIndexService.ScopeOf(p)));
        }
        catch (KeyNotFoundException) { return NotFound(); }
    }

    // Сохранить область. У каждой оси null — вернуть к дефолту, [] — «ничего отсюда».
    // Нормализация и отсев мусора — в сервисе, поэтому ответ отдаёт уже сохранённое значение,
    // а не присланное: фронт должен показать галки ровно такими, какими они легли в стор.
    [HttpPut("scope")]
    public IActionResult SetScope(string projectId, [FromBody] SetDocsScopeRequest req)
    {
        try
        {
            GetProject(projectId);   // владение проверяем до записи
            var saved = projects.SetDocsScope(projectId, req.Folders, req.RootFiles, req.Types, req.Home);
            return Ok(docs.Describe(saved.RootPath, DocsIndexService.ScopeOf(saved)));
        }
        catch (KeyNotFoundException) { return NotFound(); }
    }
}

public record SetDocsScopeRequest(List<string>? Folders, List<string>? RootFiles, List<string>? Types, string? Home = null);
