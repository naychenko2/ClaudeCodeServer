using ClaudeCodeServer.Services;
using Microsoft.AspNetCore.Mvc;

namespace ClaudeCodeServer.Controllers;

[ApiController]
[Route("api/projects/{projectId}/files")]
public class FilesController(FileService files, ProjectManager projects, SyncService sync) : ControllerBase
{
    private string GetRoot(string projectId)
    {
        var p = projects.GetById(projectId)
            ?? throw new KeyNotFoundException($"Проект не найден: {projectId}");
        return p.RootPath;
    }

    // Проставляет состояние синхронизации (direct/inherited/null) каждой записи
    private IEnumerable<FileEntry> Annotate(string projectId, IEnumerable<FileEntry> entries) =>
        entries.Select(e => e with { Synced = sync.GetSyncState(projectId, e.Path) });

    [HttpGet]
    public IActionResult List(string projectId, [FromQuery] string path = "")
    {
        try { return Ok(Annotate(projectId, files.List(GetRoot(projectId), path))); }
        catch (KeyNotFoundException) { return NotFound(); }
        catch (DirectoryNotFoundException) { return NotFound(); }
    }

    [HttpGet("tree")]
    public IActionResult Tree(string projectId, [FromQuery] string path = "")
    {
        try { return Ok(Annotate(projectId, files.Tree(GetRoot(projectId), path))); }
        catch (KeyNotFoundException) { return NotFound(); }
        catch (DirectoryNotFoundException) { return NotFound(); }
    }

    [HttpGet("search")]
    public IActionResult Search(string projectId, [FromQuery] string q = "")
    {
        try { return Ok(Annotate(projectId, files.Search(GetRoot(projectId), q))); }
        catch (KeyNotFoundException) { return NotFound(); }
    }

    [HttpGet("content")]
    public IActionResult GetContent(string projectId, [FromQuery] string path)
    {
        try
        {
            var root = GetRoot(projectId);

            // Просматриваемые документы (pdf/docx/xlsx) — отдаём base64 для клиентского рендеринга
            var doc = files.GetDocumentInfo(path);
            if (doc is { } d)
            {
                var size = files.GetFileSize(root, path);
                // Слишком большой документ — только метаданные + скачивание, без base64
                var docBase64 = size > FileService.MaxDocumentBytes ? null : files.GetFileBase64(root, path);
                return Ok(new { content = (string?)null, isBinary = true, isImage = false,
                    isDocument = true, docKind = d.Kind, mimeType = d.Mime, base64 = docBase64, fileSize = size });
            }

            if (files.IsBinaryFile(root, path))
            {
                if (files.IsImageFile(root, path))
                {
                    var ext = System.IO.Path.GetExtension(path).TrimStart('.').ToLower();
                    var mime = ext == "svg" ? "image/svg+xml" : $"image/{ext}";
                    return Ok(new { content = (string?)null, isBinary = true, isImage = true,
                        mimeType = mime, base64 = files.GetFileBase64(root, path) });
                }
                var info = new System.IO.FileInfo(System.IO.Path.Combine(root, path));
                return Ok(new { content = (string?)null, isBinary = true, isImage = false,
                    mimeType = "application/octet-stream", fileSize = info.Length });
            }
            return Ok(new { content = files.ReadFile(root, path), isBinary = false, isImage = false });
        }
        catch (KeyNotFoundException) { return NotFound(); }
        catch (FileNotFoundException) { return NotFound(); }
        catch (UnauthorizedAccessException) { return StatusCode(403); }
    }

    [HttpPut("content")]
    public IActionResult SaveContent(string projectId, [FromQuery] string path, [FromBody] SaveContentRequest req)
    {
        try { files.WriteFile(GetRoot(projectId), path, req.Content); return Ok(); }
        catch (KeyNotFoundException) { return NotFound(); }
        catch (UnauthorizedAccessException) { return StatusCode(403); }
    }

    [HttpGet("diff")]
    public IActionResult GetDiff(string projectId, [FromQuery] string path)
    {
        try
        {
            var diff = files.GetDiff(GetRoot(projectId), path);
            return Ok(new { diff });
        }
        catch (KeyNotFoundException) { return NotFound(); }
    }

    [HttpPost("revert")]
    public IActionResult Revert(string projectId, [FromBody] PathRequest req)
    {
        try
        {
            var ok = files.RevertFile(GetRoot(projectId), req.Path);
            return ok ? Ok() : BadRequest(new { error = "Не удалось откатить (не git-репозиторий?)" });
        }
        catch (KeyNotFoundException) { return NotFound(); }
    }

    [HttpPost("create")]
    public IActionResult CreateFile(string projectId, [FromBody] PathRequest req)
    {
        try { files.CreateFile(GetRoot(projectId), req.Path); return Ok(); }
        catch (KeyNotFoundException) { return NotFound(); }
        catch (UnauthorizedAccessException) { return StatusCode(403); }
    }

    [HttpPost("mkdir")]
    public IActionResult CreateDir(string projectId, [FromBody] PathRequest req)
    {
        try { files.CreateDirectory(GetRoot(projectId), req.Path); return Ok(); }
        catch (KeyNotFoundException) { return NotFound(); }
        catch (UnauthorizedAccessException) { return StatusCode(403); }
    }

    [HttpPost("rename")]
    public IActionResult Rename(string projectId, [FromBody] RenameRequest req)
    {
        try { files.Rename(GetRoot(projectId), req.OldPath, req.NewPath); return Ok(); }
        catch (KeyNotFoundException) { return NotFound(); }
        catch (UnauthorizedAccessException) { return StatusCode(403); }
    }

    [HttpDelete]
    public IActionResult Delete(string projectId, [FromQuery] string path)
    {
        try { files.Delete(GetRoot(projectId), path); return NoContent(); }
        catch (KeyNotFoundException) { return NotFound(); }
        catch (FileNotFoundException) { return NotFound(); }
        catch (UnauthorizedAccessException) { return StatusCode(403); }
    }
}

public record SaveContentRequest(string Content);
public record PathRequest(string Path);
public record RenameRequest(string OldPath, string NewPath);
