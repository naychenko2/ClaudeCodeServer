using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Composition;
using ClaudeHomeServer.Services.Desktop;
using ClaudeHomeServer.Services.Execution;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ClaudeHomeServer.Controllers;

/// <summary>
/// Билет браузера к localhost-API агента устройства (ADR-016, задача 4.2). Файлы локального
/// проекта сервер не трогает (вход размечен группой «платформа»): он только удостоверяет,
/// что этот пользователь открывает этот проект на этом устройстве. Сервисный токен владельца
/// билет не получает — он лежит в env каждого хода.
/// </summary>
[ProjectCapability(ProjectCapabilityArea.Platform)]
[ApiController]
[Authorize]
[Route("api/projects/{projectId}/device-agent")]
public sealed class DeviceAgentTicketController(
    ProjectManager projects,
    AgentTicketService tickets,
    FeatureFlagService flags,
    IDeviceExecChannel? deviceExec = null) : ControllerBase
{
    private string UserId => User.FindFirstValue(JwtRegisteredClaimNames.Sub)!;

    [HttpPost("ticket")]
    public IActionResult Issue(string projectId)
    {
        if (User.FindFirstValue(JwtService.TokenKindClaim) == JwtService.ServiceTokenKind)
            return StatusCode(StatusCodes.Status403Forbidden,
                new { error = "Билет к агенту устройства выдаётся только веб-сессии владельца" });

        var project = projects.GetById(projectId);
        if (project is null || project.OwnerId != UserId) return NotFound();
        if (!ProjectCapabilities.IsDeviceBound(project))
            return BadRequest(new { error = "Проект не локальный: его файлы на сервере" });
        if (!flags.IsEnabled(UserId, FeatureFlagKeys.LocalProjects))
            return StatusCode(StatusCodes.Status403Forbidden,
                new { error = "Локальные проекты выключены (экспериментальная функция «Локальные проекты»)" });

        var files = ProjectCapabilities.For(project, deviceExec?.GetStatus(UserId, project.DeviceId!)).Files;
        if (!files.Available)
            return Conflict(new { error = files.Reason, code = ProjectCapabilityGuard.Code });

        var ticket = tickets.Issue(UserId, project);
        if (ticket is null) return NotFound();
        return Ok(new
        {
            ticket = ticket.Ticket,
            deviceId = ticket.DeviceId,
            expiresAt = ticket.ExpiresAt,
            port = DeviceAgentApi.DefaultPort,
            header = DeviceAgentApi.TicketHeader,
        });
    }
}
