using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text.Json;
using ClaudeHomeServer.Services.Mcp;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ClaudeHomeServer.Controllers;

/// <summary>
/// Вход в внешний MCP-сервер по OAuth (волна 7). Отдельно от McpServersController:
/// callback провайдера обязан быть анонимным — JWT в редиректе не будет, авторизация там
/// по одноразовому state.
/// </summary>
[ApiController]
[Authorize]
[Route("api/mcp")]
public class McpOAuthController(McpRegistry registry, McpOAuthService oauth) : ControllerBase
{
    private string UserId => User.FindFirstValue(JwtRegisteredClaimNames.Sub)!;

    /// <summary>
    /// Готовит вход и отдаёт адрес окна провайдера — фронт открывает его window.open.
    /// Ручные client_id/client_secret нужны серверам без динамической регистрации.
    /// </summary>
    [HttpPost("servers/{id}/oauth/start")]
    public async Task<IActionResult> Start(string id, [FromBody] McpOAuthStartRequest? req, CancellationToken ct)
    {
        var record = registry.Get(UserId, id);
        if (record is null) return NotFound(new { error = "Сервер не найден" });
        try
        {
            var redirectUri = oauth.ResolveRedirectUri($"{Request.Scheme}://{Request.Host}");
            var start = await oauth.StartAsync(UserId, record, redirectUri,
                new McpOAuthClientInput(req?.ClientId, req?.ClientSecret, req?.Scopes, req?.RedirectUri), ct);
            return Ok(new { authorizeUrl = start.AuthorizeUrl, state = start.State, redirectUri = start.RedirectUri });
        }
        catch (McpOAuthException ex) { return BadRequest(new { error = ex.Message }); }
    }

    /// <summary>
    /// Запасной путь «вставьте код вручную»: часть серверов принимает только
    /// <c>http://127.0.0.1:PORT/…</c>, и до нашего callback код не доезжает.
    /// </summary>
    [HttpPost("servers/{id}/oauth/complete")]
    public async Task<IActionResult> Complete(string id, [FromBody] McpOAuthCompleteRequest req, CancellationToken ct)
    {
        if (registry.Get(UserId, id) is null) return NotFound(new { error = "Сервер не найден" });
        try
        {
            var done = await oauth.CompleteAsync(req.State, req.Code, UserId, id, ct: ct);
            return Ok(new { ok = true, key = done.ServerKey });
        }
        catch (McpOAuthException ex) { return BadRequest(new { error = ex.Message }); }
    }

    /// <summary>
    /// Возврат провайдера. Анонимен намеренно: в редиректе нет ни JWT, ни кук нашего
    /// домена. Авторизация — сам state: непредсказуемый, одноразовый, живёт 10 минут.
    /// Отвечает маленькой страницей, которая говорит открывшему окну результат и закрывается.
    /// </summary>
    [AllowAnonymous]
    [HttpGet("oauth/callback")]
    public async Task<IActionResult> Callback([FromQuery] string? code, [FromQuery] string? state,
        [FromQuery] string? error, [FromQuery(Name = "error_description")] string? errorDescription,
        CancellationToken ct)
    {
        if (!string.IsNullOrEmpty(error))
            return Page(false, errorDescription ?? error, null);
        try
        {
            var done = await oauth.CompleteAsync(state, code,
                arrivedAt: $"{Request.Scheme}://{Request.Host}{McpOAuthService.CallbackPath}", ct: ct);
            return Page(true, null, done.ServerKey);
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
}

/// <summary>
/// Ручные настройки; всё пустое — регистрируемся у сервера сами (DCR) и возвращаемся
/// на свой callback. RedirectUri задаётся для серверов, требующих loopback-адрес.
/// </summary>
public record McpOAuthStartRequest(string? ClientId, string? ClientSecret,
    List<string>? Scopes, string? RedirectUri);

public record McpOAuthCompleteRequest(string? State, string? Code);
