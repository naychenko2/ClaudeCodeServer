using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.Mcp;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ClaudeHomeServer.Controllers;

/// <summary>
/// Вход и выход из встроенной интеграции Higgsfield. Отдельно от McpOAuthController:
/// Higgsfield — запись реестра с фиксированным URL, создаётся автоматически при первом
/// входе, и управление живёт в одном месте, а не привязано к id записи в реестре.
/// </summary>
[ApiController]
[Authorize]
[Route("api/mcp/integrations/higgsfield")]
public class HiggsfieldAuthController(
    HiggsfieldIntegration higgsfield,
    Services.FeatureFlagService flags) : ControllerBase
{
    private string UserId => User.FindFirstValue(JwtRegisteredClaimNames.Sub)!;

    /// <summary>Состояние интеграции: флаг, вход выполнен, срок токена.</summary>
    [HttpGet]
    public IActionResult Get()
    {
        if (!flags.IsEnabled(UserId, FeatureFlagKeys.Higgsfield))
            return Ok(new { enabled = false, flagOn = false, connected = false, expiresAt = (DateTime?)null });

        var record = higgsfield.TryGetRecord(UserId);
        var connected = higgsfield.TryGetAccessToken(UserId) is not null;
        return Ok(new
        {
            enabled = true,
            flagOn = true,
            connected,
            expiresAt = record?.Auth.OAuth?.ExpiresAt,
        });
    }

    /// <summary>Старт OAuth-входа: создаёт запись и возвращает адрес окна провайдера.</summary>
    [HttpPost("login")]
    public async Task<IActionResult> Login(CancellationToken ct)
    {
        if (!flags.IsEnabled(UserId, FeatureFlagKeys.Higgsfield))
            return BadRequest(new { error = "Интеграция Higgsfield отключена. Включите её в настройках экспериментальных функций." });

        try
        {
            var redirectUri = await higgsfield.LoginAsync(UserId,
                $"{Request.Scheme}://{Request.Host}{McpOAuthService.CallbackPath}", ct);
            return Ok(new
            {
                authorizeUrl = redirectUri.AuthorizeUrl,
                state = redirectUri.State,
                redirectUri = redirectUri.RedirectUri,
            });
        }
        catch (McpOAuthException ex) { return BadRequest(new { error = ex.Message }); }
        catch (InvalidOperationException ex) { return BadRequest(new { error = ex.Message }); }
    }

    /// <summary>Завершение OAuth-входа: обмен кода на токены.</summary>
    [HttpPost("complete")]
    public async Task<IActionResult> Complete([FromBody] McpOAuthCompleteRequest req, CancellationToken ct)
    {
        try
        {
            var done = await higgsfield.CompleteAsync(req.State, req.Code, UserId, ct);
            return Ok(new { ok = true, key = done.ServerKey });
        }
        catch (McpOAuthException ex) { return BadRequest(new { error = ex.Message }); }
    }

    /// <summary>Выход: удаляет токены и сбрасывает статус авторизации.</summary>
    [HttpPost("logout")]
    public IActionResult Logout()
    {
        higgsfield.Logout(UserId);
        return Ok(new { ok = true });
    }
}
