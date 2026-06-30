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
    WorkspaceKnowledgeStore workspaceStore) : ControllerBase
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
            var datasetId = await knowledge.EnsureDatasetAsync(p, Username);
            // Сохраняем относительный путь как имя документа, чтобы можно было переиндексировать из панели БЗ
            var docName = req.RelativePath.Replace('\\', '/');
            // Передаём существующие теги из workspace store в Dify как doc_metadata
            var wk = workspaceStore.GetByPath(p.RootPath);
            var existingTags = wk?.DocumentTags?.TryGetValue(docName, out var t) == true ? t : null;

            DifyDocumentInfo doc;
            if (KnowledgeService.IsTextIndexable(req.RelativePath))
            {
                var content = files.ReadFile(p.RootPath, req.RelativePath);
                doc = await knowledge.IndexFileByTextAsync(datasetId, docName, content, existingTags);
            }
            else
            {
                var bytes = files.ReadFileBytes(p.RootPath, req.RelativePath);
                doc = await knowledge.IndexFileByBytesAsync(datasetId, docName, bytes, existingTags);
            }

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

        IEnumerable<FileEntry> allFiles;
        try
        {
            allFiles = files.Tree(p.RootPath, req.RelativePath, p.ShowHiddenFiles);
        }
        catch (DirectoryNotFoundException)
        {
            return NotFound(new { error = "Папка не найдена" });
        }

        var indexable = allFiles
            .Where(f => !f.IsDirectory && KnowledgeService.IsKnowledgeIndexable(f.Path))
            .ToList();

        if (indexable.Count == 0)
            return Ok(new { indexed = 0, skipped = 0, documents = Array.Empty<object>() });

        var datasetId = await knowledge.EnsureDatasetAsync(p, Username);
        var wk = workspaceStore.GetByPath(p.RootPath);

        var indexed = new List<object>();
        int skipped = 0;

        foreach (var file in indexable)
        {
            var docName = file.Path;
            var existingTags = wk?.DocumentTags?.TryGetValue(docName, out var t) == true ? t : null;
            try
            {
                DifyDocumentInfo doc;
                if (KnowledgeService.IsTextIndexable(file.Path))
                {
                    var content = files.ReadFile(p.RootPath, file.Path);
                    doc = await knowledge.IndexFileByTextAsync(datasetId, docName, content, existingTags);
                }
                else
                {
                    var bytes = files.ReadFileBytes(p.RootPath, file.Path);
                    doc = await knowledge.IndexFileByBytesAsync(datasetId, docName, bytes, existingTags);
                }
                var docTags = wk?.DocumentTags?.TryGetValue(docName, out var dt) == true ? dt : new List<string>();
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
}

public record IndexFileRequest(string RelativePath);
public record SetTagsRequest(string DocumentName, string? DocumentId, List<string>? Tags);
