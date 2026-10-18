using System.Security.Claims;
using System.Text;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Composition;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using PluginBridge = ClaudeHomeServer.Services.Plugins.PluginBridge;

namespace ClaudeHomeServer.Services.Plugins.Controllers;

// Мост панели v1 (ADR-031 §3, шаг 3.1): раздача статики панели + эндпоинт call.
//
//   GET  /api/plugins/{id}/{ver}/assets/{**path} — статика панели из packages/{id}/{ver}/panel/
//   GET  /api/plugins/{id}/{ver}/panel            — srcdoc-ready HTML (index.html + шим + CSP)
//   POST /api/plugins/{id}/call                   — вызов capability {capability, args}
//
// Гейты (как в PluginsController): подсистема выключена → 503, флаг plugins-mvp off → 403.
// Дополнительно на call: сервисный JWT → 403, чужой пакет → 404, выключенный → 409.
//
// CSP: default-src 'self'; script-src 'self' 'unsafe-inline'; style-src 'self' 'unsafe-inline';
// frame-ancestors 'self'; connect-src 'self'. X-Frame-Options: DENY НЕ ставим — мы сами
// показываем фрейм (ADR-031 §3.1). srcdoc — строже: default-src 'none', connect-src 'none'.
//
// Навигация iframe не несёт Authorization (ADR-031 §3.4): панель авторизуется САМОСТОЯТЕЛЬНО
// через call с user-JWT из cookie хоста.
[ApiController]
[Authorize]
[Route("api/plugins")]
public class PluginBridgeController(
    IConfiguration config,
    IFeatureFlagGate flags,
    PluginStore? store,
    PluginBridge? bridge) : ControllerBase
{
    // Claim'ы — литералы (как в PluginsController): MapInboundClaims=false оставляет raw-имена.
    private const string SubClaim = "sub";
    private const string TokenKindClaim = "typ";
    private const string ServiceTokenKind = "svc";

    private string UserId => User.FindFirstValue(SubClaim)!;
    private bool IsServiceToken => User.FindFirstValue(TokenKindClaim) == ServiceTokenKind;

    // CSP для раздачи файлов панели (ADR-031 §3.1).
    private const string PanelCsp =
        "default-src 'self'; script-src 'self' 'unsafe-inline'; style-src 'self' 'unsafe-inline'; " +
        "frame-ancestors 'self'; connect-src 'self'";

    // CSP для srcdoc (строже: внешних ресурсов нет, ADR-031 §3.1).
    private const string SrcdocCsp =
        "default-src 'none'; script-src 'unsafe-inline'; style-src 'unsafe-inline'; " +
        "img-src data: blob:; connect-src 'none'; form-action 'none'";

    // Гейт: null — прошёл; иначе отказ.
    private IActionResult? Gate()
    {
        if (!SubsystemGate.IsEnabled(config, "plugins"))
            return StatusCode(503, new { error = "Подсистема плагинов выключена (Subsystems:plugins:Enabled=false)" });
        if (!flags.IsEnabled(UserId, FeatureFlagKeys.PluginsMvp))
            return StatusCode(403, new { error = "Флаг plugins-mvp выключен" });
        return null;
    }

    // Id пакета в URL — один сегмент publisher~slug (M1): ~ → / + проверка формата.
    private static string NormalizeId(string id) =>
        PluginId.TryNormalize(id, out var norm) ? norm.Value : id;

    // --- GET /api/plugins/{id}/{ver}/assets/{**path} — статика панели ---

    [HttpGet("{id}/{ver}/assets/{**path}")]
    public IActionResult Assets(string id, string ver, string path)
    {
        var gate = Gate(); if (gate is not null) return gate;

        var pluginId = NormalizeId(id);
        if (!PluginVersion.IsValid(ver))
            return NotFound();

        var record = store!.LoadInstalled(UserId).FirstOrDefault(r => r.Id == pluginId);
        if (record is null) return NotFound();
        if (!record.Enabled)
            return StatusCode(409, new { error = "Плагин выключен" });

        // Файл: packages/{id}/{ver}/panel/{path} (SafePath.Join — защита от path traversal).
        var panelDir = Path.Combine(store.PackageDir(UserId, pluginId, ver), "panel");
        var filePath = SafePath.Join(panelDir, path);
        if (!System.IO.File.Exists(filePath)) return NotFound();

        var content = System.IO.File.ReadAllBytes(filePath);
        Response.Headers["Content-Security-Policy"] = PanelCsp;
        return File(content, GetContentType(path));
    }

    // --- GET /api/plugins/{id}/{ver}/panel — srcdoc-ready HTML (ADR-031 §3.1) ---
    //
    // Хост собирает HTML из panel/index.html + шим plugin-kit + CSP (meta), вставляет
    // через <iframe srcdoc=...>. Это снимает проблему навигации iframe (нет Authorization).

    [HttpGet("{id}/{ver}/panel")]
    public IActionResult PanelSrcdoc(string id, string ver)
    {
        var gate = Gate(); if (gate is not null) return gate;

        var pluginId = NormalizeId(id);
        if (!PluginVersion.IsValid(ver))
            return NotFound();

        var record = store!.LoadInstalled(UserId).FirstOrDefault(r => r.Id == pluginId);
        if (record is null) return NotFound();
        if (!record.Enabled)
            return StatusCode(409, new { error = "Плагин выключен" });

        var panelDir = Path.Combine(store.PackageDir(UserId, pluginId, ver), "panel");
        var indexPath = SafePath.Join(panelDir, "index.html");
        if (!System.IO.File.Exists(indexPath)) return NotFound();

        var html = System.IO.File.ReadAllText(indexPath);
        var srcdoc = BuildSrcdoc(html);
        return Content(srcdoc, "text/html; charset=utf-8");
    }

    // --- POST /api/plugins/{id}/call — вызов capability ---

    [HttpPost("{id}/call")]
    public async Task<IActionResult> Call(string id, [FromBody] PluginCallRequest req, CancellationToken ct)
    {
        var gate = Gate(); if (gate is not null) return gate;

        // Сервисный JWT → 403: call делает только пользовательский токен (ADR-031 §3.4).
        if (IsServiceToken)
            return StatusCode(403, new { error = "Сервисный JWT: call — только пользовательский токен" });

        var pluginId = NormalizeId(id);
        var record = store!.LoadInstalled(UserId).FirstOrDefault(r => r.Id == pluginId);
        if (record is null) return NotFound();
        if (!record.Enabled)
            return StatusCode(409, new { error = "Плагин выключен" });

        if (string.IsNullOrWhiteSpace(req.Capability))
            return BadRequest(new { error = "Не задана capability (операция моста)" });

        // Контекст вызова (ADR-031 §3.3, M8): владелец + id плагина + контекст слота.
        var context = new PluginCallContext(
            OwnerId: UserId,
            PluginId: pluginId,
            ProjectId: null,   // контекст слота приходит с фронтенда (3.2), в 3.1 — null
            ObjectId: null);

        var result = await bridge!.DispatchAsync(context, req.Capability, req.Args, record.Granted, ct);

        if (result.Ok)
            return Ok(new PluginCallResponse { Ok = true, Result = result.Data });

        // Маппинг кода ошибки → HTTP-статус (ADR-031 §3.4).
        var (status, _) = result.Error!.Code switch
        {
            "denied" => (403, "denied"),
            "not_found" => (404, "not_found"),
            "invalid" => (400, "invalid"),
            "unavailable" => (503, "unavailable"),
            _ => (500, "upstream"),
        };
        return StatusCode(status, new PluginCallResponse
        {
            Ok = false,
            Error = new PluginCallErrorDto { Code = result.Error.Code, Message = result.Error.Message },
        });
    }

    // --- Хелперы ---

    // srcdoc: HTML + CSP (meta) + шим plugin-kit (мост + CSS-токены).
    // CSP в meta (не в HTTP-заголовке): srcdoc не имеет собственного HTTP-контекста.
    private static string BuildSrcdoc(string html)
    {
        var sb = new StringBuilder();
        sb.Append("<!DOCTYPE html>\n");
        sb.Append("<html>\n<head>\n");
        sb.Append("  <meta http-equiv=\"Content-Security-Policy\" content=\"" + SrcdocCsp + "\">\n");
        sb.Append("  <meta charset=\"utf-8\">\n");
        sb.Append("  <style>/* CSS-токены тем (plugin-kit) */\n");
        sb.Append("    :root { --cc-bg: #fff; --cc-fg: #222; --cc-accent: #6366f1; }\n");
        sb.Append("    @media (prefers-color-scheme: dark) { :root { --cc-bg: #1a1a1a; --cc-fg: #eee; } }\n");
        sb.Append("  </style>\n");
        sb.Append("  <script>/* Шим plugin-kit: мост v1 (ready/call/resize) */\n");
        sb.Append("    window.ccsPlugin = {\n");
        sb.Append("      ready() { parent.postMessage({ source: 'ccs-plugin', v: 1, type: 'ready' }, '*'); },\n");
        sb.Append("      call(id, op, args) { parent.postMessage({ source: 'ccs-plugin', v: 1, type: 'call', id, op, args }, '*'); },\n");
        sb.Append("      resize(h) { parent.postMessage({ source: 'ccs-plugin', v: 1, type: 'resize', height: h }, '*'); }\n");
        sb.Append("    };\n");
        sb.Append("  </script>\n");
        sb.Append("</head>\n<body>\n");
        sb.Append(html);
        sb.Append("\n</body>\n</html>");
        return sb.ToString();
    }

    // MIME-тип по расширению файла.
    private static string GetContentType(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        return ext switch
        {
            ".html" or ".htm" => "text/html; charset=utf-8",
            ".css" => "text/css; charset=utf-8",
            ".js" or ".mjs" => "application/javascript; charset=utf-8",
            ".json" => "application/json; charset=utf-8",
            ".svg" => "image/svg+xml",
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".gif" => "image/gif",
            ".woff2" => "font/woff2",
            _ => "application/octet-stream",
        };
    }
}
