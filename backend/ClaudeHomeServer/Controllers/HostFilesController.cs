using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Execution;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ClaudeHomeServer.Controllers;

// Read-only просмотр произвольного абсолютного хостового пути вне корня проекта (клик по
// файлу из diff/чата, указывающему за пределы Project.RootPath — FileService.SafeJoin его
// закономерно отбрасывает). Гейт доступа: «просмотр = досягаемость агента пользователя» —
// local-юзер и так читает весь хост своими процессами, container-юзер ограничен монтированиями
// песочницы. Только чтение, SafeJoin/FilesController не трогаем.
[ApiController]
[Authorize]
[Route("api/host-files")]
public class HostFilesController(FileService files, UserStore users, ILauncherFactory launchers) : ControllerBase
{
    // DefaultMapInboundClaims = false → sub не ремапится в NameIdentifier, читаем напрямую
    private string? UserId => User.FindFirstValue(JwtRegisteredClaimNames.Sub);

    [HttpGet("content")]
    public IActionResult GetContent([FromQuery] string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathRooted(path))
            return BadRequest("Путь должен быть абсолютным");

        var userId = UserId;
        if (userId is null || users.GetById(userId) is not { } user) return Unauthorized();

        // container-юзер: путь обязан лежать внутри монтирований песочницы — ToRuntime
        // бросает за их пределами (тот же принцип, что SafeJoin для путей внутри проекта)
        if (user.ExecutionEnvironment == ExecutionEnvironments.Container)
        {
            try { launchers.ForOwner(userId).Paths.ToRuntime(path); }
            catch (InvalidOperationException) { return StatusCode(403); }
        }

        try
        {
            if (Directory.Exists(path)) return NotFound();
            if (!System.IO.File.Exists(path)) return NotFound();

            // FileService.SafeJoin(dir, name) собирает обратно тот же абсолютный путь —
            // так переиспользуем детект бинарности/изображения без дублирования логики
            var dir = Path.GetDirectoryName(path)!;
            var name = Path.GetFileName(path);

            if (files.IsBinaryFile(dir, name))
            {
                if (files.IsImageFile(dir, name))
                {
                    var ext = Path.GetExtension(name).TrimStart('.').ToLower();
                    var mime = ext == "svg" ? "image/svg+xml" : $"image/{ext}";
                    return Ok(new
                    {
                        content = (string?)null,
                        isBinary = true,
                        isImage = true,
                        mimeType = mime,
                        base64 = files.GetFileBase64(dir, name)
                    });
                }
                return Ok(new
                {
                    content = (string?)null,
                    isBinary = true,
                    isImage = false,
                    mimeType = "application/octet-stream",
                    fileSize = files.GetFileSize(dir, name)
                });
            }

            return Ok(new { content = files.ReadFile(dir, name), isBinary = false, isImage = false });
        }
        catch (DirectoryNotFoundException) { return NotFound(); }
        catch (UnauthorizedAccessException) { return StatusCode(403); }
    }
}
