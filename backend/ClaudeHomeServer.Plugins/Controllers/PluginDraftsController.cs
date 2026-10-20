using System.Security.Claims;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.Composition;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ClaudeHomeServer.Services.Plugins.Controllers;

// REST черновиков build-it-yourself (ADR-031, шаг 4.3a): человек просматривает черновик
// агента и «Принимает» его. Тонкий, как PluginsController: гейты + маппинг на HTTP; вся
// логика — в PluginDraftService (список/предпросмотр) и PluginInstaller (установка).
//
// Гейты (те же, что у PluginsController, плюс plugins-build-yourself):
//   1) подсистема выключена (Subsystems:plugins:Enabled=false) → 503 (M2: сервис не активен);
//   2) флаг plugins-mvp выключен для владельца → 403 (пользовательский гейт);
//   3) флаг plugins-build-yourself выключен для владельца → 403 (гейт «Собрать плагин»).
//
// На ВСЕХ ручках: сервисный JWT (typ=svc) → 403 (черновик собирает агент через MCP, а
// принимает/просматривает человек — пользовательский токен); чужой владелец → 404
// (черновик другого владельца не в _drafts текущего → не найден).
//
// Контроллер тонкий: гейты + маппинг на HTTP. Ядро — в PluginDraftService (List/Preview)
// и PluginInstaller (Install).
[ApiController]
[Authorize]
[Route("api/plugins/drafts")]
public class PluginDraftsController(
    IConfiguration config,
    IFeatureFlagGate flags,
    // `= null` — обязательность для DI: подсистема выключена → PluginStore/… не в контейнере,
    // и без явного значения по умолчанию активация контроллера падала (500, а не честный 503 гейта).
    PluginStore? store = null,
    PluginDraftService? drafts = null,
    PluginInstaller? installer = null,
    PluginDraftDiff? diff = null) : ControllerBase
{
    // Claim'ы — литералы: вертикаль не тянет JwtBearer-пакет, а MapInboundClaims=false
    // (Program.cs) оставляет raw-имена. Смысл 1:1 с JwtService (sub = id владельца,
    // typ=svc — сервисный токен MCP).
    private const string SubClaim = "sub";
    private const string TokenKindClaim = "typ";
    private const string ServiceTokenKind = "svc";

    // CSP сырого ответа панели черновика (ADR-031 §3.6, QA-фикс H): тот же, что у
    // PluginBridgeController.Assets.
    private const string RawPanelCsp = "sandbox; default-src 'none'";

    private string UserId => User.FindFirstValue(SubClaim)!;
    private bool IsServiceToken => User.FindFirstValue(TokenKindClaim) == ServiceTokenKind;

    // Гейты: null — прошёл; иначе отказ. Подсистема → 503 (M2), plugins-mvp → 403,
    // plugins-build-yourself → 403 (гейт «Собрать плагин»).
    private IActionResult? Gate()
    {
        if (!SubsystemGate.IsEnabled(config, "plugins"))
            return StatusCode(503, new { error = "Подсистема плагинов выключена (Subsystems:plugins:Enabled=false)" });
        if (!flags.IsEnabled(UserId, FeatureFlagKeys.PluginsMvp))
            return StatusCode(403, new { error = "Флаг plugins-mvp выключен" });
        if (!flags.IsEnabled(UserId, FeatureFlagKeys.PluginsBuildYourself))
            return StatusCode(403, new { error = "Флаг plugins-build-yourself выключен" });
        return null;
    }

    // --- GET /api/plugins/drafts — список черновиков владельца (шаг 4.3a) ---
    // slug, id, версия, время изменения, число проблем последней проверки, признак
    // «установлена такая же версия».

    [HttpGet]
    public IActionResult List()
    {
        var gate = Gate(); if (gate is not null) return gate;
        if (IsServiceToken)
            return StatusCode(403, new { error = "Сервисный JWT: черновики ведёт только пользовательский токен" });
        return Ok(drafts!.List(UserId));
    }

    // --- GET /api/plugins/drafts/{slug}/preview — предпросмотр (шаг 4.3a) ---
    // Ответ тот же, что у draft_preview для агента: вызываем тот же PluginDraftService.Preview.

    [HttpGet("{slug}/preview")]
    public IActionResult Preview(string slug)
    {
        var gate = Gate(); if (gate is not null) return gate;
        if (IsServiceToken)
            return StatusCode(403, new { error = "Сервисный JWT: черновики ведёт только пользовательский токен" });

        // Чужой/несуществующий черновик → 404 (Preview бросает FileNotFoundException/ArgumentException).
        try
        {
            return Ok(drafts!.Preview(UserId, slug));
        }
        catch (Exception e) when (e is FileNotFoundException or ArgumentException)
        {
            return NotFound(new { error = e.Message });
        }
    }

    // --- GET /api/plugins/drafts/{slug}/panel — предпросмотр панели черновика (шаг 4.3d) ---
    // Сырой файл панели (ADR-031 §3.6: сервер HTML не разбирает; документ собирает фронт).
    // Файл берётся по entry из манифеста черновика (тот же контракт, что у установленной
    // панели: entry — относительный путь panel/…); без манифеста/панели → panel/index.html.
    // Гейты те же, что у остальных ручек черновиков:
    // сервисный JWT → 403, чужой/несуществующий → 404, нет панели → 404, файл > 256 КиБ → 413.
    // Путь только через SafePath.Join (без выхода из каталога черновика); symlink не следовать.

    [HttpGet("{slug}/panel")]
    public IActionResult Panel(string slug)
    {
        var gate = Gate(); if (gate is not null) return gate;
        if (IsServiceToken)
            return StatusCode(403, new { error = "Сервисный JWT: черновики ведёт только пользовательский токен" });

        // Чужой/несуществующий черновик → 404 (DraftDir null — slug не по грамматике, каталога нет).
        var draftDir = drafts!.DraftDir(UserId, slug);
        if (draftDir is null || !System.IO.Directory.Exists(draftDir))
            return NotFound(new { error = $"Черновик {slug} не найден" });

        // Entry панели: из манифеста черновика (тот же контракт, что у установленной панели).
        // Без манифеста или без панели — fallback на panel/index.html.
        var entry = "panel/index.html";
        var (manifest, _) = PluginInstaller.ReadManifest(draftDir);
        var panel = manifest?.Contributes?.Panels?.FirstOrDefault();
        if (panel is not null && !string.IsNullOrEmpty(panel.Entry)
            && PluginManifestValidation.IsValidPanelEntry(panel.Entry))
            entry = panel.Entry;

        // Путь панели: _drafts/{slug}/{entry}, только через SafePath.Join.
        string filePath;
        try { filePath = SafePath.Join(draftDir, entry); }
        catch (Exception) { return NotFound(new { error = $"Черновик {slug} не найден" }); }

        // Нет панели → 404; symlink → 404 (не следовать); файл > 256 КиБ → 413.
        if (!System.IO.File.Exists(filePath)
            || (System.IO.File.GetAttributes(filePath) & System.IO.FileAttributes.ReparsePoint) != 0)
            return NotFound(new { error = $"Черновик {slug} без панели" });
        if (new System.IO.FileInfo(filePath).Length > PluginPackageValidator.MaxPanelFileBytes)
            return StatusCode(413, new { error = $"Файл панели превышает лимит размера ({PluginPackageValidator.MaxPanelFileBytes / 1024} КиБ)" });

        var content = System.IO.File.ReadAllBytes(filePath);
        Response.Headers["Content-Security-Policy"] = RawPanelCsp;
        Response.Headers["X-Content-Type-Options"] = "nosniff";
        return File(content, "text/html; charset=utf-8");
    }

    // --- GET /api/plugins/drafts/{slug}/diff — сравнение с установленной версией (шаг 4.3b) ---
    // Черновик vs ПОСЛЕДНЯЯ установленная версия того же id: файлы (added/removed/modified),
    // построчный diff текстовых (потолки 200/1000 → truncated), бинарные/крупные «N байт»,
    // манифест (права/секреты). Нет установленной версии → «новый плагин» со списком файлов.
    // Гейты те же: сервисный JWT → 403, чужой/несуществующий → 404 (Diff бросает
    // FileNotFoundException/ArgumentException, как Preview).

    [HttpGet("{slug}/diff")]
    public IActionResult Diff(string slug)
    {
        var gate = Gate(); if (gate is not null) return gate;
        if (IsServiceToken)
            return StatusCode(403, new { error = "Сервисный JWT: черновики ведёт только пользовательский токен" });

        try
        {
            return Ok(diff!.Diff(UserId, slug));
        }
        catch (Exception e) when (e is FileNotFoundException or ArgumentException)
        {
            return NotFound(new { error = e.Message });
        }
    }

    // --- POST /api/plugins/drafts/{slug}/accept — принять черновик (шаг 4.3a) ---
    // Устанавливает пакет по метке draft:<slug> (PluginInstaller — единственный арбитр:
    // валидация, no-op по совпавшему sha, 409 при смене пакета). Права НЕ выдаёт:
    //   - первая установка  → granted = [];
    //   - обновление        → granted = старые ∩ новые запрошенные;
    //   - в ответе consentRequired: true + список НОВЫХ прав (появились при обновлении).
    // Заданные секреты прошлой версии сохраняются; черновик после успеха НЕ удаляется.

    [HttpPost("{slug}/accept")]
    public IActionResult Accept(string slug)
    {
        var gate = Gate(); if (gate is not null) return gate;
        if (IsServiceToken)
            return StatusCode(403, new { error = "Сервисный JWT: черновики ведёт только пользовательский токен" });

        // Чужой/несуществующий черновик → 404 ДО установки (иначе Install вернёт 400 «каталог
        // не найден», а не 404).
        if (!drafts!.Exists(UserId, slug))
            return NotFound(new { error = $"Черновик {slug} не найден" });

        // Манифест черновика: id/version/permissions (потолок). id из него — ключ старой записи.
        var (manifest, _) = PluginInstaller.ReadManifest(store!.DraftDir(UserId, slug));
        // Старая запись по id (любой версии) — для granted/secrets при обновлении версии.
        // (Bump vs 409 решает сам Install: Conflict — только та же id+version, другой sha.)
        var oldById = manifest?.Id is { } mid
            ? store.LoadInstalled(UserId).FirstOrDefault(r => r.Id == mid)
            : null;

        // Установка: валидация, no-op/409/400 — единственный арбитр PluginInstaller:
        //   - Ok: первая установка (granted = []) ИЛИ штатное обновление/bump (granted = старые
        //     ∩ новые; Install сам кладёт пакет новой версии на диск и возвращает запись —
        //     defect 8565ccff: раньше bump уходил в Conflict ДО Directory.Move, и пакет
        //     новой версии на диск не попадал, а контроллер писал «битую» запись);
        //   - NoOp: совпал sha (granted/secrets не сбрасываются);
        //   - Conflict: ТОЛЬКО та же id+version, другой sha (неизменяемость версии) → 409.
        var result = installer!.Install(UserId, "draft:" + slug);

        PluginInstall record;
        if (result.Status == InstallStatus.Conflict)
        {
            // 409: содержимое той же версии изменилось, версию не подняли.
            return Conflict(new { error = result.Error });
        }

        if (result.Status is not (InstallStatus.Ok or InstallStatus.NoOp))
            return BadRequest(new { error = result.Error, problems = result.Problems });
        // Ok: новая запись (первая установка granted = [] / обновление granted = старые ∩ новые);
        // NoOp: существующая (granted/secrets уже на месте, новые права не появляются).
        record = result.Record!;

        // consentRequired + newPermissions:
        //   - первая установка (oldById == null): ВСЕ запрошенные права «новые» для человека
        //     (ADR-031 §4, макет: «Принять…» → диалог согласия → выдача). newPermissions = Requested,
        //     consentRequired = Requested непусто. granted = [] (accept ничего не выдаёт сам).
        //   - обновление (oldById != null): newPermissions = Requested \ старые Requested;
        //     consentRequired = newPermissions непусто. granted = старые ∩ новые (выше).
        bool consentRequired;
        List<string> newPermissions;
        if (oldById is null)
        {
            consentRequired = record.Requested.Count > 0;
            newPermissions = record.Requested.ToList();
        }
        else
        {
            var newRequested = record.Requested.Except(oldById.Requested, StringComparer.Ordinal).ToList();
            consentRequired = newRequested.Count > 0;
            newPermissions = newRequested;
        }

        // Секреты: заданные в прошлой версии сохраняются (значения — в plugin-secrets.json,
        // здесь — только имена + set + updatedAt). Новая версия без секрета — имя не расширяет.
        if (oldById is not null)
            record.Secrets = oldById.Secrets
                .Where(s => record.Requested.Contains($"secret:{s.Name}", StringComparer.Ordinal))
                .ToList();

        // load → mutate → save под одной блокировкой (PluginStore.Update): два параллельных
        // accept одного slug не портят installed.json (запись атомарно перезаписывается).
        store.Update(UserId, recs =>
        {
            recs.RemoveAll(r => r.Id == record.Id);
            recs.Add(record);
            return recs;
        });

        // isUpdate: есть ли уже запись этого id (обновление/повторный accept), а не первая
        // установка. Фронт открывает диалог согласия в режиме modify (новые права НЕ отмечены),
        // когда isUpdate, и install (все запрошенные отмечены), когда первая установка
        // (Глеб MAJOR 4: раньше всегда install → новое право при обновлении приходило отмеченным).
        // previousVersion: версия, на которую плагин был установлен до этого accept (null — первая
        // установка). Права по-прежнему не выдаёт (granted не расширяется accept'ом).
        return Ok(new
        {
            id = record.Id,
            version = record.Version,
            sha256 = record.Sha256,
            source = record.Source,
            enabled = record.Enabled,
            requested = record.Requested,
            granted = record.Granted,
            consentRequired,
            newPermissions,
            isUpdate = oldById is not null,
            previousVersion = oldById?.Version,
            secrets = record.Secrets
                .Select(s => new { s.Name, s.Set, s.UpdatedAt })
                .ToList(),
        });
    }

    // --- POST /api/plugins/drafts/{slug}/reject — отклонить черновик (шаг 4.3c) ---
    // Переносит каталог черновика в служебный _rejected/{slug} (вне _drafts и вне наблюдения)
    // и записывает время отклонения. Гейты те же: сервисный JWT → 403, чужой/несуществующий → 404.
    // Ответ: slug, время отклонения (rejectedAt) и дата, когда исчезнет (expiresAt = +30 дней).

    [HttpPost("{slug}/reject")]
    public IActionResult Reject(string slug)
    {
        var gate = Gate(); if (gate is not null) return gate;
        if (IsServiceToken)
            return StatusCode(403, new { error = "Сервисный JWT: черновики ведёт только пользовательский токен" });

        // Чужой/несуществующий черновик → 404 ДО переноса (иначе Reject вернёт 400, а не 404).
        if (!drafts!.Exists(UserId, slug))
            return NotFound(new { error = $"Черновик {slug} не найден" });

        var op = drafts.Reject(UserId, slug);
        if (!op.Ok)
            return op.Code == "already-rejected"
                ? Conflict(new { error = op.Message })
                : BadRequest(new { error = op.Message });

        var meta = drafts.RejectedMeta(UserId, slug);
        DateTime? rejectedAt = meta?.RejectedAt;
        DateTime? expiresAt = meta is null ? null : meta.RejectedAt.AddDays(PluginDraftService.RejectedRetentionDays);
        return Ok(new { slug, rejectedAt, expiresAt });
    }

    // --- POST /api/plugins/drafts/{slug}/restore — вернуть отклонённый черновик (шаг 4.3c) ---
    // Возвращает _rejected/{slug} обратно в _drafts/{slug}; если slug уже занят новым черновиком → 409.
    // Событие наблюдателя plugin_draft_changed приходит как обычно (черновик снова в _drafts).
    // Гейты те же: сервисный JWT → 403, чужой/не отклонён → 404.

    [HttpPost("{slug}/restore")]
    public IActionResult Restore(string slug)
    {
        var gate = Gate(); if (gate is not null) return gate;
        if (IsServiceToken)
            return StatusCode(403, new { error = "Сервисный JWT: черновики ведёт только пользовательский токен" });

        var op = drafts!.Restore(UserId, slug);
        if (!op.Ok)
            return op.Code == "conflict" ? Conflict(new { error = op.Message })
                 : op.Code == "not-found" ? NotFound(new { error = op.Message })
                 : BadRequest(new { error = op.Message });
        return Ok(new { slug, restored = true });
    }
}
