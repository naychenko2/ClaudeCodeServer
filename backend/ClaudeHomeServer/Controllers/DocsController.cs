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
            return Ok(docs.GetIndex(p.RootPath, p.DocsFolders));
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
            var detail = docs.GetDoc(p.RootPath, path, p.DocsFolders);
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
            return Ok(docs.Search(p.RootPath, q, p.DocsFolders));
        }
        catch (KeyNotFoundException) { return NotFound(); }
    }

    // Настройка области: что выбрано и какие папки проекта вообще годятся в документацию
    [HttpGet("folders")]
    public IActionResult Folders(string projectId)
    {
        try
        {
            var p = GetProject(projectId);
            return Ok(FoldersInfo(p));
        }
        catch (KeyNotFoundException) { return NotFound(); }
    }

    // Сохранить область. folders: null — вернуть к дефолту (docs/); [] — только README.md.
    // Нормализация и отсев мусора — в сервисе, поэтому ответ отдаёт уже сохранённое значение,
    // а не присланное: фронт должен показать галки ровно такими, какими они легли в стор.
    [HttpPut("folders")]
    public IActionResult SetFolders(string projectId, [FromBody] SetDocsFoldersRequest req)
    {
        try
        {
            GetProject(projectId);   // владение проверяем до записи
            return Ok(FoldersInfo(projects.SetDocsFolders(projectId, req.Folders)));
        }
        catch (KeyNotFoundException) { return NotFound(); }
    }

    private DocsFoldersInfo FoldersInfo(Project p) => new(
        DocsIndexService.NormalizeFolders(p.DocsFolders),
        docs.SuggestFolders(p.RootPath, p.DocsFolders),
        DocsIndexService.DefaultFolders);
}

public record SetDocsFoldersRequest(List<string>? Folders);
