using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ClaudeHomeServer.Controllers;

[ApiController]
[Authorize]
[Route("api/projects/{projectId}/knowledge")]
public class KnowledgeController(
    ProjectManager projects,
    KnowledgeService knowledge,
    FileService files,
    WorkspaceKnowledgeStore workspaceStore,
    ProjectKnowledgeSyncService sync) : ControllerBase
{
    private string UserId => User.FindFirstValue(JwtRegisteredClaimNames.Sub)!;
    private string Username => User.FindFirstValue(System.Security.Claims.ClaimTypes.Name) ?? UserId;

    private Project? GetOwnedProject(string projectId)
    {
        var p = projects.GetById(projectId);
        return p?.OwnerId == UserId ? p : null;
    }

    // GET /api/projects/{id}/knowledge — статус БЗ + список документов
    [HttpGet]
    public async Task<IActionResult> GetStatus(string projectId)
    {
        var p = GetOwnedProject(projectId);
        if (p is null) return NotFound();

        var wk = workspaceStore.GetByPath(p.RootPath);
        if (string.IsNullOrEmpty(wk?.DifyDatasetId))
            return Ok(new { datasetId = (string?)null, documents = Array.Empty<object>(), total = 0 });

        // Сверка при открытии панели БЗ: подхватить правки, сделанные пока сервер/ватчеры не смотрели
        sync.QueueSync(p.RootPath);

        try
        {
            var docs = await knowledge.ListAllDocumentsAsync(wk.DifyDatasetId);
            var tags = wk.DocumentTags ?? new Dictionary<string, List<string>>();
            var docsDto = docs.Data.Select(d => new
            {
                id = d.Id,
                name = d.Name,
                indexingStatus = d.IndexingStatus,
                tags = tags.TryGetValue(d.Name, out var t) ? t : [],
            });
            return Ok(new { datasetId = wk.DifyDatasetId, documents = docsDto, total = docs.Total });
        }
        catch (HttpRequestException ex)
        {
            return StatusCode(502, new { error = ex.Message });
        }
    }

    // GET /api/projects/{id}/knowledge/search — семантический поиск по базе знаний проекта
    [HttpGet("search")]
    public async Task<IActionResult> Search(string projectId, [FromQuery] string q = "", [FromQuery] int topK = 8)
    {
        var p = GetOwnedProject(projectId);
        if (p is null) return NotFound();

        if (string.IsNullOrWhiteSpace(q))
            return Ok(new { items = Array.Empty<object>() });

        var wk = workspaceStore.GetByPath(p.RootPath);
        if (string.IsNullOrEmpty(wk?.DifyDatasetId))
            return Ok(new { items = Array.Empty<object>(), hint = "знания не проиндексированы" });

        try
        {
            var chunks = await knowledge.RetrieveAsync(wk.DifyDatasetId, q.Trim(), Math.Clamp(topK, 1, 20));
            return Ok(new
            {
                items = chunks.Select(c => new { content = c.Content, score = c.Score, documentName = c.DocumentName }),
            });
        }
        catch (HttpRequestException ex) { return StatusCode(502, new { error = ex.Message }); }
    }

    // POST /api/projects/{id}/knowledge/index — индексировать файл (lazy-создаёт датасет)
    [HttpPost("index")]
    public async Task<IActionResult> IndexFile(string projectId, [FromBody] IndexFileRequest req)
    {
        var p = GetOwnedProject(projectId);
        if (p is null) return NotFound();

        if (!KnowledgeService.IsKnowledgeIndexable(req.RelativePath))
            return BadRequest(new { error = $"Формат файла не поддерживается для индексирования: {Path.GetExtension(req.RelativePath)}" });

        try
        {
            // Идемпотентная индексация через синк-сервис: имя документа = относительный путь,
            // повторный вызов обновляет документ (не плодит дубль), файл попадает под отслеживание
            var (datasetId, doc) = await sync.IndexPathAsync(p, Username, req.RelativePath);
            var wk = workspaceStore.GetByPath(p.RootPath);
            var docName = req.RelativePath.Replace('\\', '/');
            var docTags = wk?.DocumentTags?.TryGetValue(docName, out var dt) == true ? dt : new List<string>();
            return Ok(new { datasetId, document = new { id = doc.Id, name = doc.Name, indexingStatus = doc.IndexingStatus, tags = docTags } });
        }
        catch (FileNotFoundException) { return NotFound(new { error = "Файл не найден" }); }
        catch (InvalidOperationException ex) { return BadRequest(new { error = ex.Message }); }
        catch (HttpRequestException ex) { return StatusCode(502, new { error = ex.Message }); }
    }

    // GET /api/projects/{id}/knowledge/tags — теги всех документов
    [HttpGet("tags")]
    public IActionResult GetTags(string projectId)
    {
        var p = GetOwnedProject(projectId);
        if (p is null) return NotFound();
        var wk = workspaceStore.GetByPath(p.RootPath);
        return Ok(wk?.DocumentTags ?? new Dictionary<string, List<string>>());
    }

    // PUT /api/projects/{id}/knowledge/tags — задать теги для документа
    [HttpPut("tags")]
    public async Task<IActionResult> SetTags(string projectId, [FromBody] SetTagsRequest req)
    {
        var p = GetOwnedProject(projectId);
        if (p is null) return NotFound();

        var tags = req.Tags ?? [];

        // Сохраняем в Dify если датасет настроен и передан documentId
        var wk = workspaceStore.GetByPath(p.RootPath);
        if (!string.IsNullOrEmpty(wk?.DifyDatasetId) && !string.IsNullOrEmpty(req.DocumentId))
        {
            try
            {
                await knowledge.UpdateDocumentTagsAsync(wk.DifyDatasetId, req.DocumentId, tags);
            }
            catch (HttpRequestException ex)
            {
                return StatusCode(502, new { error = ex.Message });
            }
        }

        // Локальный кэш в WorkspaceKnowledge — для быстрого отображения без запросов к Dify
        var workspace = workspaceStore.GetOrCreate(p.RootPath);
        workspace.DocumentTags ??= new Dictionary<string, List<string>>();
        if (tags.Count == 0)
            workspace.DocumentTags.Remove(req.DocumentName);
        else
            workspace.DocumentTags[req.DocumentName] = tags;
        workspaceStore.Save(workspace);

        return NoContent();
    }

    // POST /api/projects/{id}/knowledge/index-folder — рекурсивно индексировать папку
    [HttpPost("index-folder")]
    public async Task<IActionResult> IndexFolder(string projectId, [FromBody] IndexFileRequest req)
    {
        var p = GetOwnedProject(projectId);
        if (p is null) return NotFound();

        // Папка заметок индексируется отдельной базой знаний (NotesKnowledgeService,
        // per-owner dataset) — файловая индексация notes/ дала бы двойной индекс.
        var rel = (req.RelativePath ?? "").Replace('\\', '/').Trim('/');
        if (rel.Equals("notes", StringComparison.OrdinalIgnoreCase) ||
            rel.StartsWith("notes/", StringComparison.OrdinalIgnoreCase))
            return BadRequest(new { error = "Заметки индексируются отдельной базой знаний" });

        IEnumerable<FileEntry> allFiles;
        try
        {
            allFiles = files.Tree(p.RootPath, req.RelativePath ?? "", p.ShowHiddenFiles);
        }
        catch (DirectoryNotFoundException)
        {
            return NotFound(new { error = "Папка не найдена" });
        }

        // Исключаем notes/ при индексации папки-родителя (напр. корня проекта)
        var indexable = allFiles
            .Where(f => !f.IsDirectory && KnowledgeService.IsKnowledgeIndexable(f.Path)
                        && !IsInNotesVault(f.Path))
            .ToList();

        if (indexable.Count == 0)
            return Ok(new { indexed = 0, skipped = 0, documents = Array.Empty<object>() });

        var indexed = new List<object>();
        int skipped = 0;

        foreach (var file in indexable)
        {
            try
            {
                // Идемпотентная индексация: повторный прогон папки обновляет документы, не плодит дубли
                var (_, doc) = await sync.IndexPathAsync(p, Username, file.Path);
                var wk = workspaceStore.GetByPath(p.RootPath);
                var docTags = wk?.DocumentTags?.TryGetValue(file.Path, out var dt) == true ? dt : new List<string>();
                indexed.Add(new { id = doc.Id, name = doc.Name, indexingStatus = doc.IndexingStatus, tags = docTags });
            }
            catch
            {
                skipped++;
            }
        }

        return Ok(new { indexed = indexed.Count, skipped, documents = indexed });
    }

    // DELETE /api/projects/{id}/knowledge/documents/{documentId}
    [HttpDelete("documents/{documentId}")]
    public async Task<IActionResult> DeleteDocument(string projectId, string documentId)
    {
        var p = GetOwnedProject(projectId);
        if (p is null) return NotFound();

        var wk = workspaceStore.GetByPath(p.RootPath);
        if (string.IsNullOrEmpty(wk?.DifyDatasetId)) return NotFound();

        try
        {
            await knowledge.DeleteDocumentAsync(wk.DifyDatasetId, documentId);
            // Снять файл с отслеживания — иначе синк пересоздаст документ по живому файлу
            sync.ForgetDocument(p.RootPath, documentId);
            return NoContent();
        }
        catch (HttpRequestException ex) { return StatusCode(502, new { error = ex.Message }); }
    }

    // DELETE /api/projects/{id}/knowledge — удалить датасет
    [HttpDelete]
    public async Task<IActionResult> DeleteDataset(string projectId)
    {
        var p = GetOwnedProject(projectId);
        if (p is null) return NotFound();

        var wk = workspaceStore.GetByPath(p.RootPath);
        if (string.IsNullOrEmpty(wk?.DifyDatasetId)) return NoContent();

        try { await knowledge.DeleteDatasetAsync(wk.DifyDatasetId); }
        catch (HttpRequestException) { /* датасет мог быть удалён в Dify — сбрасываем ссылку */ }

        workspaceStore.Delete(p.RootPath);
        return NoContent();
    }

    // Путь внутри vault заметок (notes/…) — такие файлы индексируются отдельно
    private static bool IsInNotesVault(string path)
    {
        var n = path.Replace('\\', '/').TrimStart('/');
        return n.StartsWith("notes/", StringComparison.OrdinalIgnoreCase);
    }
}

public record IndexFileRequest(string RelativePath);
public record SetTagsRequest(string DocumentName, string? DocumentId, List<string>? Tags);
