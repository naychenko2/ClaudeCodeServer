using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using ClaudeHomeServer.Services.Mcp;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ClaudeHomeServer.Controllers;

/// <summary>
/// Инстансное подключение Higgsfield: один OAuth-вход админа шарится всеми
/// пользователями. <see cref="Status"/> доступен любому залогиненному,
/// <see cref="Connect"/>/<see cref="Callback"/>/<see cref="Disconnect"/> — только admin.
/// </summary>
[ApiController]
[Authorize]
[Route("api/higgsfield")]
public class HiggsfieldInstanceController(
    HiggsfieldOAuthService service) : ControllerBase
{
    private string UserId => User.FindFirstValue(JwtRegisteredClaimNames.Sub)!;

    /// <summary>Старт OAuth-входа: возвращает authorize-URL для открытия окна провайдера.</summary>
    [Authorize(Roles = "admin")]
    [HttpPost("connect")]
    public async Task<IActionResult> Connect(CancellationToken ct)
    {
        var origin = $"{Request.Scheme}://{Request.Host}";
        var redirectUri = $@"{origin}/api/higgsfield/callback";

        try
        {
            var (authorizeUrl, state, _) = await service.ConnectAsync(UserId, redirectUri, ct);
            return Ok(new { authorizeUrl, state, redirectUri });
        }
        catch (McpOAuthException ex) { return BadRequest(new { error = ex.Message }); }
        catch (InvalidOperationException ex) { return BadRequest(new { error = ex.Message }); }
    }

    /// <summary>Приём кода/redirect от провайдера. Body: { state, code }.</summary>
    [Authorize(Roles = "admin")]
    [HttpPost("callback")]
    public async Task<IActionResult> Callback([FromBody] CallbackRequest req, CancellationToken ct)
    {
        try
        {
            await service.CompleteAsync(req.State, req.Code, UserId, ct);
            return Ok(new { ok = true });
        }
        catch (McpOAuthException ex) { return BadRequest(new { error = ex.Message }); }
    }

    /// <summary>Отключение: чистит токены и состояние.</summary>
    [Authorize(Roles = "admin")]
    [HttpPost("disconnect")]
    public IActionResult Disconnect()
    {
        service.Disconnect();
        return Ok(new { ok = true });
    }

    /// <summary>Состояние подключения: { connected, expiresAt? }.</summary>
    [HttpGet("status")]
    public IActionResult Status()
    {
        var (connected, expiresAt, _) = service.Status();
        return Ok(new { connected, expiresAt });
    }

    public sealed record CallbackRequest(string? State, string? Code);
}
