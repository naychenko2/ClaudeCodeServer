using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text.Json;
using ClaudeHomeServer.Services.Mcp;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ClaudeHomeServer.Controllers;

/// <summary>
/// Инстансное подключение Higgsfield: один OAuth-вход админа шарится всеми
/// пользователями. <see cref="Status"/> доступен любому залогиненному,
/// <see cref="Connect"/>/<see cref="Disconnect"/> — только admin,
/// <see cref="Callback"/> — анонимный (провайдер редиректит браузер без JWT).
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

    /// <summary>
    /// Возврат провайдера. Анонимен намеренно: в редиректе нет ни JWT, ни кук нашего
    /// домена. Авторизация — сам state: непредсказуемый, одноразовый, живёт 10 минут.
    /// Отвечает маленькой страницей, которая говорит открывшему окну результат и закрывается.
    /// </summary>
    [AllowAnonymous]
    [HttpGet("callback")]
    public async Task<IActionResult> Callback(
        [FromQuery] string? code, [FromQuery] string? state,
        [FromQuery] string? error, [FromQuery(Name = "error_description")] string? errorDescription,
        CancellationToken ct)
    {
        if (!string.IsNullOrEmpty(error))
            return Page(false, errorDescription ?? error, null);
        try
        {
            await service.CompleteAsync(state, code, ct: ct);
            return Page(true, null, HiggsfieldOAuthService.Key);
        }
        catch (McpOAuthException ex) { return Page(false, ex.Message, null); }
    }

    // Страница ответа: результат уезжает в открывшее окно через postMessage и вкладка
    // закрывается сама. Значения подставляются только сериализацией в JSON-литералы —
    // текст ошибки приходит от чужого сервера, и в разметку он попасть не должен.
    // targetOrigin = "*" осознанно: UI живёт где угодно (удалённый доступ, туннель),
    // а в сообщении нет ничего секретного — только «получилось» и ключ сервера.
    private ContentResult Page(bool ok, string? error, string? serverKey)
    {
        var payload = JsonSerializer.Serialize(new { type = "mcp-oauth", ok, key = serverKey, error });
        const string target = "\"*\"";
        var title = ok ? "Вход выполнен" : "Вход не удался";
        var text = ok ? "Можно закрыть это окно." : "Не удалось: " + (error ?? "неизвестная ошибка");
        var html = $$"""
            <!doctype html>
            <html lang="ru"><head><meta charset="utf-8"><title>{{title}}</title></head>
            <body style="font-family:system-ui,sans-serif;padding:24px">
            <p>{{System.Net.WebUtility.HtmlEncode(text)}}</p>
            <script>
            (function () {
              var payload = {{payload}};
              try { if (window.opener) window.opener.postMessage(payload, {{target}}); } catch (e) {}
              setTimeout(function () { window.close(); }, 400);
            })();
            </script>
            </body></html>
            """;
        return Content(html, "text/html; charset=utf-8");
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

}
