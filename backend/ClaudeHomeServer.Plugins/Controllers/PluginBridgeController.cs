using System.Security.Claims;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Composition;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using PluginBridge = ClaudeHomeServer.Services.Plugins.PluginBridge;

namespace ClaudeHomeServer.Services.Plugins.Controllers;

// Мост панели v1 (ADR-031 §3, шаг 3.1): раздача статики панели + эндпоинт call.
//
//   GET  /api/plugins/{id}/{ver}/assets/{**path} — статика панели: path относительно корня
//        пакета (entry из манифеста), отдаёт только файлы под panel/**. Сырой файл (ADR-031
//        §3.6: сервер HTML не разбирает; документ панели собирает фронт).
//   POST /api/plugins/{id}/call                   — вызов capability {capability, args}
//
// Гейты (как в PluginsController): подсистема выключена → 503, флаг plugins-mvp off → 403.
// Дополнительно на call: сервисный JWT → 403, чужой пакет → 404, выключенный → 409.
//
// CSP сырого ответа (ADR-031 §3.6): sandbox; default-src 'none' + X-Content-Type-Options:
// nosniff. Документ инертен даже при открытии верхним окном через ?access_token=;
// fetch().text() с фронта на заголовки не реагирует.
//
// Навигация iframe не несёт Authorization (ADR-031 §3.4): панель авторизуется САМОСТОЯТЕЛЬНО
// через call с user-JWT из cookie хоста.
[ApiController]
[Authorize]
[Route("api/plugins")]
public class PluginBridgeController(
    IConfiguration config,
    IFeatureFlagGate flags,
    // `= null` — обязательность для DI: подсистема выключена → PluginStore/PluginBridge не в
    // контейнере, и без явного значения по умолчанию активация контроллера падала (500, а не 503).
    PluginStore? store = null,
    PluginBridge? bridge = null,
    IProjectManager? projects = null) : ControllerBase
{
    // Claim'ы — литералы (как в PluginsController): MapInboundClaims=false оставляет raw-имена.
    private const string SubClaim = "sub";
    private const string TokenKindClaim = "typ";
    private const string ServiceTokenKind = "svc";

    private string UserId => User.FindFirstValue(SubClaim)!;
    private bool IsServiceToken => User.FindFirstValue(TokenKindClaim) == ServiceTokenKind;

    // CSP сырого ответа панели (ADR-031 §3.6, QA-фикс H): документ инертен даже при
    // открытии верхним окном через ?access_token= (Program.cs ≈ 1316); fetch().text()
    // с фронта на заголовки не реагирует.
    private const string RawPanelCsp = "sandbox; default-src 'none'";

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
    //
    // Контракт (ADR-031 §3.1, дефект a537ea41): {path} — относительно корня версии пакета
    // (совпадает с contributes.panels[0].entry из манифеста, например "panel/index.html").
    // Отдаём ТОЛЬКО файлы под panel/**: всё остальное внутри пакета (ccs-plugin.json,
    // skills/, commands/, .claude-plugin/) не торчит наружу. Путь — только через
    // SafePath.Join; «../», абсолютные пути, обратные слеши, symlink/ReparsePoint → 404.

    [HttpGet("{id}/{ver}/assets/{**path}")]
    public IActionResult Assets(string id, string ver, string path)
    {
        var gate = Gate(); if (gate is not null) return gate;

        // Сервисный JWT → 403: статика панели — только пользовательский токен (rework 12,
        // minor 1: единообразие с call и ручками черновиков).
        if (IsServiceToken)
            return StatusCode(403, new { error = "Сервисный JWT: статика/панель — только пользовательский токен" });

        var pluginId = NormalizeId(id);
        if (!PluginVersion.IsValid(ver))
            return NotFound();

        var record = store!.LoadInstalled(UserId).FirstOrDefault(r => r.Id == pluginId);
        if (record is null) return NotFound();
        if (!record.Enabled)
            return StatusCode(409, new { error = "Плагин выключен" });

        var packageRoot = store.PackageDir(UserId, pluginId, ver);

        // SafePath.Join: «../» вывел за корень пакета → UnauthorizedAccessException → 404.
        string filePath;
        try
        {
            filePath = SafePath.Join(packageRoot, path);
        }
        catch (UnauthorizedAccessException)
        {
            return NotFound();
        }

        // Разрешённый путь обязан остаться под panel/ (иначе «panel/../../x» → root/x).
        var panelRoot = Path.GetFullPath(Path.Combine(packageRoot, "panel"));
        if (!filePath.StartsWith(panelRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            && !filePath.Equals(panelRoot, StringComparison.Ordinal))
            return NotFound();

        // symlink/ReparsePoint не следовать: каждый сегмент пути от корня версии (rework 12,
        // minor 2: раньше только финальный файл — symlink-каталог в середине выводил за корень
        // пакета). cursor идёт от packageRoot по сегментам path (тот же путь, что filePath).
        // GetAttributes бросает FileNotFoundException на несуществующий путь — проверяем
        // существование сегмента перед чтением атрибутов (несуществующий = не reparse).
        string cursor = packageRoot;
        foreach (var seg in path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
        {
            if (seg.Length == 0) continue;
            cursor = Path.Combine(cursor, seg);
            if ((System.IO.File.Exists(cursor) || System.IO.Directory.Exists(cursor))
                && (System.IO.File.GetAttributes(cursor) & System.IO.FileAttributes.ReparsePoint) != 0)
                return NotFound();
        }
        // Финальный файл: существует (reparse-сегменты отбиты циклом выше).
        if (!System.IO.File.Exists(filePath))
            return NotFound();

        // Потолок размера файла панели (ADR-031 §3.6, QA-фикс H): больше 256 КиБ → 413
        // (на чтении, на случай подмены на диске).
        if (new System.IO.FileInfo(filePath).Length > PluginPackageValidator.MaxPanelFileBytes)
            return StatusCode(413, new { error = $"Файл панели превышает лимит размера ({PluginPackageValidator.MaxPanelFileBytes / 1024} КиБ)" });

        var content = System.IO.File.ReadAllBytes(filePath);
        Response.Headers["Content-Security-Policy"] = RawPanelCsp;
        Response.Headers["X-Content-Type-Options"] = "nosniff";
        return File(content, GetContentType(path));
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

        // Серверный гейт (rework 12, minor 7, defense in depth): если у вызова есть контекст
        // проекта (слот), проект должен допускать плагины (локальный в MVP — нет).
        // В 3.1 ProjectId всегда null (контекст слота приходит с фронта в 3.2) — гейт no-op.
        if (context.ProjectId is not null)
        {
            var project = projects?.GetById(context.ProjectId);
            if (project is not null)
            {
                var caps = ProjectCapabilities.PluginsFor(project);
                if (!caps.Available)
                    return StatusCode(409, new { error = caps.Reason });
            }
        }

        var result = await bridge!.DispatchAsync(context, req.Capability, req.Args, record.Granted, ct);

        if (result.Ok)
            return Ok(new PluginCallResponse { Ok = true, Result = result.Data });

        // Маппинг кода ошибки → HTTP-статус (ADR-031 §3.4; rework 12, major 4:
        // timeout/limit/upstream раньше падали в общий 500). Код в теле JSON не меняется —
        // фронт различает ошибки по нему, меняется только HTTP-статус.
        var status = result.Error!.Code switch
        {
            "denied" => 403,
            "not_found" => 404,
            "invalid" => 400,
            "unavailable" => 503,
            "timeout" => 504,
            "limit" => 429,
            "upstream" => 502,
            _ => 500,
        };
        return StatusCode(status, new PluginCallResponse
        {
            Ok = false,
            Error = new PluginCallErrorDto { Code = result.Error.Code, Message = result.Error.Message },
        });
    }

    // --- Хелперы ---

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
