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

    // Потолок размера файла панели (тот же, что в PluginBridgeController): больше 4 MiB → 413,
    // чтобы предпросмотр черновика не выкачивал неограниченный файл в память сервера.
    private const long MaxPanelBytes = 4 * 1024 * 1024; // 4 MiB

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
    // Тот же srcdoc-ready HTML (CSP + шим моста), что у PluginBridgeController.PanelSrcdoc,
    // но из _drafts/{slug}/panel/index.html. Гейты те же, что у остальных ручек черновиков:
    // сервисный JWT → 403, чужой/несуществующий → 404, нет панели → 404, файл > 4 MiB → 413.
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

        // Путь панели: _drafts/{slug}/panel/index.html, только через SafePath.Join.
        string indexPath;
        try { indexPath = SafePath.Join(Path.Combine(draftDir, "panel"), "index.html"); }
        catch (Exception) { return NotFound(new { error = $"Черновик {slug} не найден" }); }

        // Нет панели → 404; symlink → 404 (не следовать); файл > 4 MiB → 413.
        if (!System.IO.File.Exists(indexPath)
            || (System.IO.File.GetAttributes(indexPath) & System.IO.FileAttributes.ReparsePoint) != 0)
            return NotFound(new { error = $"Черновик {slug} без панели" });
        if (new System.IO.FileInfo(indexPath).Length > MaxPanelBytes)
            return StatusCode(413, new { error = "Файл панели превышает лимит размера (4 MiB)" });

        // Тот же код сборки HTML (CSP + шим моста), что у PanelSrcdoc — не копия.
        var srcdoc = PluginBridgeController.BuildSrcdoc(System.IO.File.ReadAllText(indexPath));
        return Content(srcdoc, "text/html; charset=utf-8");
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
        var records = store.LoadInstalled(UserId);
        // Старая запись той же id+version (для 409 «поднимите версию»); любая по id (для
        // granted/secrets при обновлении версии).
        var oldSameVersion = manifest?.Id is { } mid
            ? records.FirstOrDefault(r => r.Id == mid && r.Version == manifest.Version)
            : null;
        var oldById = manifest?.Id is { } mid2
            ? records.FirstOrDefault(r => r.Id == mid2)
            : null;

        // Установка: валидация, no-op/409/400 — единственный арбитр PluginInstaller.
        var result = installer!.Install(UserId, "draft:" + slug);

        // 409: Install ругается на «sha изменился» по id. Конфликт ТОЛЬКО если та же id+version
        // (содержимое изменилось, версию не подняли). Если существующая запись — ДРУГАЯ версия
        // (bump) → это обновление, не конфликт: запись собираем сами (Install при Conflict её не
        // возвращает) — sha256 из валидатора (тот же, что в Install), requested/id/version из манифеста.
        // Права: первая установка → granted = []; обновление → granted = старые ∩ новые запрошенные
        // (minor 14, D2: считаем сразу, без обходной записи Granted = [], которую потом перезаписывали).
        PluginInstall record;
        if (result.Status == InstallStatus.Conflict)
        {
            if (oldSameVersion is not null)
                return Conflict(new { error = result.Error });
            var validation = drafts.Validate(UserId, slug);
            var requested = manifest.Permissions?.Requested ?? [];
            record = new PluginInstall
            {
                Id = manifest!.Id,
                Version = manifest.Version,
                Sha256 = validation.Sha256 ?? "",
                Requested = requested,
                Granted = oldById is null ? [] : oldById.Granted
                    .Where(p => requested.Contains(p, StringComparer.Ordinal))
                    .ToList(),
                Enabled = true,
                Source = "draft:" + slug,
            };
        }
        else
        {
            if (result.Status is not (InstallStatus.Ok or InstallStatus.NoOp))
                return BadRequest(new { error = result.Error, problems = result.Problems });
            // Ok: новая запись (granted = [] по C2, старой записи того же id нет),
            // NoOp: существующая (granted/secrets уже на месте, новые права не появляются).
            record = result.Record!;
        }

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
