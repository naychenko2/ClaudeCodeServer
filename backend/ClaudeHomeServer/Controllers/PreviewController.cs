using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using ClaudeHomeServer.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ClaudeHomeServer.Controllers;

[ApiController]
[Authorize]
public class PreviewController : ControllerBase
{
    private readonly ProjectManager _projects;
    private readonly DevServerService _devServer;
    private readonly ILogger<PreviewController> _log;

    public PreviewController(ProjectManager projects, DevServerService devServer, ILogger<PreviewController> log)
    {
        _projects = projects;
        _devServer = devServer;
        _log = log;
    }

    private string UserId => User.FindFirstValue(JwtRegisteredClaimNames.Sub) ?? "";

    private bool OwnsProject(string projectId)
    {
        var ownerId = _projects.GetById(projectId)?.OwnerId;
        return ownerId is not null && ownerId == UserId;
    }

    [HttpPost("/api/projects/{projectId}/preview/start")]
    public async Task<IActionResult> Start(string projectId, [FromBody] PreviewStartRequest req)
    {
        if (!OwnsProject(projectId)) return Forbid();
        if (string.IsNullOrWhiteSpace(req.Command))
            return BadRequest(new { error = "Команда не указана" });

        var result = await _devServer.StartAsync(projectId, UserId, req.Command, req.Args ?? [], req.Port);
        return Ok(new { status = result.Status, port = result.Port, error = result.Error });
    }

    [HttpPost("/api/projects/{projectId}/preview/stop")]
    public async Task<IActionResult> Stop(string projectId)
    {
        if (!OwnsProject(projectId)) return Forbid();
        await _devServer.StopAsync(projectId, UserId);
        return Ok(new { status = "stopped" });
    }

    [HttpGet("/api/projects/{projectId}/preview/status")]
    public IActionResult Status(string projectId)
    {
        if (!OwnsProject(projectId)) return Forbid();
        var (status, port, error) = _devServer.GetStatus(projectId, UserId);
        return Ok(new { status, port, error });
    }
}

public record PreviewStartRequest(string Command, string[]? Args = null, int? Port = null);
