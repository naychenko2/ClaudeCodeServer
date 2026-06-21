using ClaudeCodeServer.Services;
using Microsoft.AspNetCore.Mvc;

namespace ClaudeCodeServer.Controllers;

[ApiController]
[Route("api/projects/{projectId}/files")]
public class FilesController(FileService files, ProjectManager projects) : ControllerBase
{
    private string GetRoot(string projectId)
    {
        var p = projects.GetById(projectId)
            ?? throw new KeyNotFoundException($"Проект не найден: {projectId}");
        return p.RootPath;
    }

    [HttpGet]
    public IActionResult List(string projectId, [FromQuery] string path = "")
    {
        try { return Ok(files.List(GetRoot(projectId), path)); }
        catch (KeyNotFoundException) { return NotFound(); }
        catch (DirectoryNotFoundException) { return NotFound(); }
    }

    [HttpGet("search")]
    public IActionResult Search(string projectId, [FromQuery] string q = "")
    {
        try { return Ok(files.Search(GetRoot(projectId), q)); }
        catch (KeyNotFoundException) { return NotFound(); }
    }

    [HttpGet("content")]
    public IActionResult GetContent(string projectId, [FromQuery] string path)
    {
        try
        {
            var root = GetRoot(projectId);
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
        catch (UnauthorizedAccessException) { return Forbid(); }
    }

    [HttpPut("content")]
    public IActionResult SaveContent(string projectId, [FromQuery] string path, [FromBody] SaveContentRequest req)
    {
        try { files.WriteFile(GetRoot(projectId), path, req.Content); return Ok(); }
        catch (KeyNotFoundException) { return NotFound(); }
        catch (UnauthorizedAccessException) { return Forbid(); }
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
        catch (UnauthorizedAccessException) { return Forbid(); }
    }

    [HttpPost("mkdir")]
    public IActionResult CreateDir(string projectId, [FromBody] PathRequest req)
    {
        try { files.CreateDirectory(GetRoot(projectId), req.Path); return Ok(); }
        catch (KeyNotFoundException) { return NotFound(); }
        catch (UnauthorizedAccessException) { return Forbid(); }
    }

    [HttpPost("rename")]
    public IActionResult Rename(string projectId, [FromBody] RenameRequest req)
    {
        try { files.Rename(GetRoot(projectId), req.OldPath, req.NewPath); return Ok(); }
        catch (KeyNotFoundException) { return NotFound(); }
        catch (UnauthorizedAccessException) { return Forbid(); }
    }

    [HttpDelete]
    public IActionResult Delete(string projectId, [FromQuery] string path)
    {
        try { files.Delete(GetRoot(projectId), path); return NoContent(); }
        catch (KeyNotFoundException) { return NotFound(); }
        catch (FileNotFoundException) { return NotFound(); }
        catch (UnauthorizedAccessException) { return Forbid(); }
    }
}

public record SaveContentRequest(string Content);
public record PathRequest(string Path);
public record RenameRequest(string OldPath, string NewPath);
