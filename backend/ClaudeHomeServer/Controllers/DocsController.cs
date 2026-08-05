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
            return Ok(docs.GetIndex(p.RootPath, docs.ResolveScope(p)));
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
            var detail = docs.GetDoc(p.RootPath, path, docs.ResolveScope(p));
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
            return Ok(docs.Search(p.RootPath, q, docs.ResolveScope(p)));
        }
        catch (KeyNotFoundException) { return NotFound(); }
    }

    // Настройка области: что выбрано, что можно выбрать (папки, файлы корня, типы файлов)
    // и откуда она взята — файл репозитория или настройка продукта
    [HttpGet("scope")]
    public IActionResult Scope(string projectId)
    {
        try
        {
            return Ok(docs.Describe(GetProject(projectId)));
        }
        catch (KeyNotFoundException) { return NotFound(); }
    }

    // Сохранить область. У каждой оси null — вернуть к дефолту, [] — «ничего отсюда».
    // Нормализация и отсев мусора — в сервисе, поэтому ответ отдаёт уже сохранённое значение,
    // а не присланное: фронт должен показать галки ровно такими, какими они легли в стор.
    //
    // Куда писать, решает наличие .docs: есть файл — правим его, нет — настройку проекта.
    // Разводить это по двум эндпоинтам нельзя: единственный существующий потребитель
    // (кнопка «вернуть README в область» в панели) молча перестал бы работать на проектах
    // с файлом — писал бы в хранилище, которое больше не читается.
    [HttpPut("scope")]
    public IActionResult SetScope(string projectId, [FromBody] SetDocsScopeRequest req)
    {
        try
        {
            var project = GetProject(projectId);   // владение проверяем до записи
            var file = docs.ReadScopeFile(project.RootPath);
            if (file.Scope is null)
            {
                var saved = projects.SetDocsScope(projectId, req.Folders, req.RootFiles, req.Types, req.Home);
                return Ok(docs.Describe(saved));
            }

            // null оси — «вернуть к дефолту», поэтому в файл уезжает дефолтное значение.
            // Home устроен иначе (как color и groupId у проекта): null — «не менять»,
            // пустая строка — сброс к авто. Иначе запрос без home (та самая кнопка про
            // README) стирал бы выбранную домашнюю страницу
            docs.WriteScopeFile(project.RootPath, new DocsScope(
                req.Folders ?? DocsIndexService.DefaultScope.Folders,
                req.RootFiles ?? DocsIndexService.DefaultScope.RootFiles,
                req.Types ?? DocsIndexService.DefaultScope.Types,
                req.Home is null ? file.Scope.Home : DocsIndexService.NormalizeHome(req.Home)));
            return Ok(docs.Describe(project));
        }
        catch (KeyNotFoundException) { return NotFound(); }
        catch (IOException e) { return StatusCode(500, new { error = $"Не удалось записать {DocsIndexService.ScopeFileName}: {e.Message}" }); }
        catch (UnauthorizedAccessException e) { return StatusCode(500, new { error = $"Нет доступа к {DocsIndexService.ScopeFileName}: {e.Message}" }); }
    }

    // Вынести текущую область в файл репозитория: с этого момента она версионируется и
    // одинакова у всех, кто открыл репозиторий. Отдельным действием, а не автоматически —
    // продукт не создаёт файлы в чужом рабочем дереве без спроса.
    [HttpPost("scope-file")]
    public IActionResult SaveScopeFile(string projectId)
    {
        try
        {
            var project = GetProject(projectId);
            docs.WriteScopeFile(project.RootPath, docs.ResolveScope(project));
            return Ok(docs.Describe(project));
        }
        catch (KeyNotFoundException) { return NotFound(); }
        catch (IOException e) { return StatusCode(500, new { error = $"Не удалось записать {DocsIndexService.ScopeFileName}: {e.Message}" }); }
        catch (UnauthorizedAccessException e) { return StatusCode(500, new { error = $"Нет доступа к {DocsIndexService.ScopeFileName}: {e.Message}" }); }
    }
}

public record SetDocsScopeRequest(List<string>? Folders, List<string>? RootFiles, List<string>? Types, string? Home = null);
