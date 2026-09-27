using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Execution;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ClaudeHomeServer.Controllers;

/// <summary>
/// Первая отрисовка бейджа рук в чате (ADR-016 §7): дальше состояние едет событием
/// <c>hands_status</c>. <c>state = null</c> — у чата рук нет вовсе (флаг, не локальный проект,
/// тумблер проекта, пустой список провайдеров) и бейдж не рисуется.
/// </summary>
[ApiController]
[Authorize]
[Route("api/sessions")]
public class SessionHandsController(
    SessionManager sessions, ProjectManager projects, UserStore users, FeatureFlagService flags,
    IDeviceExecChannel? devices = null) : ControllerBase
{
    private string UserId => User.FindFirstValue(JwtRegisteredClaimNames.Sub)!;

    [HttpGet("{sessionId}/hands-status")]
    public IActionResult Get(string sessionId)
    {
        var session = sessions.GetById(sessionId);
        // Чужая/неизвестная сессия — 404, не раскрывая существование
        if (session is null || sessions.ResolveOwnerId(session) != UserId) return NotFound();

        var none = Ok(new HandsStatusView(null, null, null));
        if (session.ProjectId is null || projects.GetById(session.ProjectId) is not { } project
            || !ProjectCapabilities.IsDeviceBound(project))
            return none;
        if (!flags.IsEnabled(UserId, FeatureFlagKeys.LocalHands) || !project.HandsEnabled
            || users.GetById(UserId)?.HandsProviders is not { Count: > 0 })
            return none;

        var device = devices?.GetStatus(UserId, project.DeviceId!);
        if (device is not { Online: true } || !device.HasCapability(DeviceCapabilities.Hands))
            return Ok(new HandsStatusView(HandsChatStates.Unavailable, null, device?.DeviceName));

        var (state, reason) = DeviceHandsTurns.ChatStateOf(UserId, device.DeviceId, sessionId);
        return Ok(new HandsStatusView(state, reason, device.DeviceName));
    }
}

/// <param name="State"><see cref="HandsChatStates"/>; null — у чата рук нет.</param>
/// <param name="Reason"><see cref="HandsEndReason"/> у «остановлено».</param>
public sealed record HandsStatusView(string? State, string? Reason, string? DeviceName);
