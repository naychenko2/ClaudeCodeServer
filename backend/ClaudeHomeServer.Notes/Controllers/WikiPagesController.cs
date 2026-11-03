using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Composition;
using ClaudeHomeServer.Services.Notes;
using ClaudeHomeServer.Services.Notes.Wiki;
using ClaudeHomeServer.Services.Spheres;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ClaudeHomeServer.Notes.Controllers;

// REST правки страницы человеком (ADR-038, решения 1–3 и 9; шаг B3b): сохранение страницы и
// «Вернуть мою». Каждое сохранение — пакет автора human, он идёт тем же конвейером применения,
// что пакеты ИИ (WikiApplyService: блокировка раздела → сверка → гейт → прообразы → запись →
// манифест, журнал, проекции), и применяется сразу: человек решил сам, «Входящих» нет.
//
// Главный инвариант: пакет human создаёт только человек. Ручки под [Authorize] с владельцем
// из claim sub; сервисный токен хода (typ=svc) отказан 403 ДО разбора тела и до любых
// состояний — иначе агент `curl`-ом подделал бы правку человека, написал утверждения без
// цитат и обошёл цитатный контракт. Инструмента MCP для правки от имени человека нет.
// Серверный гейт `wiki_unified_off` — 409, когда выключен любой из двух флагов: llm-wiki
// (без вики нечего править) или notes-wiki-unified (вложенный флаг, ADR-038 §9).
//
// Конкурентность оптимистическая, по baseHash (sha256 байтов страницы, как в манифесте):
// страница изменилась после открытия редактора (чужое сохранение, применённый пакет ИИ) —
// 409 wiki_page_changed со свежей версией, UI рисует сравнение «Моё / ИИ / Было». Сверка и
// запись — одна критическая секция блокировки раздела (WikiApplyService), исключений наружу нет.
[ApiController]
[Authorize]
[Route("api/wiki/pages")]
public class WikiPagesController : ControllerBase
{
    private readonly WikiApplyService _apply;
    private readonly IProjectManager _projects;
    private readonly IFeatureFlagGate? _flags;
    private readonly ISphereDirectory? _spheres;

    public WikiPagesController(WikiApplyService apply, IProjectManager projects, IFeatureFlagGate? flags,
        ISphereDirectory? spheres = null)
    {
        _apply = apply;
        _projects = projects;
        _flags = flags;
        _spheres = spheres;
    }

    // Коды отказов — контракт для фронта, устойчивые строки (как в WikiController).
    private const string CodeSectionKey = "wiki_section_key";
    private const string CodeForeign = "wiki_foreign";
    private const string CodePageKey = "wiki_page_key";
    private const string CodeSectionOff = "wiki_section_off";
    private const string CodeSphereReserved = "wiki_sphere_reserved";
    private const string CodeProjectMissing = "wiki_project_missing";
    private const string CodeUnifiedOff = "wiki_unified_off";
    private const string CodePageChanged = "wiki_page_changed";
    private const string CodeInvalid = "wiki_edit_invalid";

    // Локальные константы сервисного токена — как в WikiController, PluginsController и TasksController.
    private const string TokenKindClaim = "typ";
    private const string ServiceTokenKind = "svc";

    private string? OwnerOrNull() =>
        User.Identity?.IsAuthenticated == true ? User.FindFirstValue(JwtRegisteredClaimNames.Sub) : null;

    private bool IsServiceToken => User.FindFirstValue(TokenKindClaim) == ServiceTokenKind;

    /// <summary>POST /api/wiki/pages/edit {section, page, baseHash?, content} — сохранить
    /// страницу. content — полный новый текст; baseHash — sha256 страницы, какой её видел
    /// редактор (нет — создание новой страницы). 200 {status: applied|unchanged, hash,
    /// packageId?}; 409 wiki_page_changed со свежей страницей; 409 wiki_drift — файл разошёлся с
    /// манифестом (решает adopt); 400 wiki_edit_invalid — правка не прошла разбор или гейт
    /// (message и violations по-русски, для редактора).</summary>
    [HttpPost("edit")]
    public async Task<IActionResult> Edit([FromBody] WikiPageEditRequest? req)
    {
        var userId = OwnerOrNull();
        if (userId is null) return Unauthorized();
        if (IsServiceToken) return ServiceTokenRefusal();
        if (!UnifiedOn(userId)) return UnifiedOffRefusal();
        if (req is null) return BadRequest(new { code = CodeSectionKey, message = "тело запроса не задано" });

        var (key, refusal) = ResolveWritableSection(userId, req.Section, req.Page);
        if (refusal is not null) return refusal;
        return ToResult(userId, await _apply.EditPageAsync(userId, key!, req.Page!, req.BaseHash, req.Content), req.Page!);
    }

    /// <summary>POST /api/wiki/pages/restore {section, page, baseHash, current, restore} —
    /// «Вернуть мою»: current — действующее утверждение, restore — формулировка человека в
    /// «Истории». Новой карточки не создаётся: возвращённая строка ссылается на исходную.
    /// Ответы — как у edit.</summary>
    [HttpPost("restore")]
    public async Task<IActionResult> Restore([FromBody] WikiPageRestoreRequest? req)
    {
        var userId = OwnerOrNull();
        if (userId is null) return Unauthorized();
        if (IsServiceToken) return ServiceTokenRefusal();
        if (!UnifiedOn(userId)) return UnifiedOffRefusal();
        if (req is null) return BadRequest(new { code = CodeSectionKey, message = "тело запроса не задано" });

        var (key, refusal) = ResolveWritableSection(userId, req.Section, req.Page);
        if (refusal is not null) return refusal;
        return ToResult(userId,
            await _apply.RestorePageAsync(userId, key!, req.Page!, req.BaseHash, req.Current, req.Restore), req.Page!);
    }

    private IActionResult ServiceTokenRefusal() =>
        StatusCode(403, new { error = "Сервисный токен: вики решает только пользовательский токен" });

    // Флаг действует только при включённом llm-wiki: без вики нечего править (ADR-038, §9).
    private bool UnifiedOn(string userId) =>
        _flags?.IsEnabled(userId, FeatureFlagKeys.LlmWiki) == true
        && _flags.IsEnabled(userId, FeatureFlagKeys.NotesWikiUnified);

    private IActionResult UnifiedOffRefusal() =>
        StatusCode(409, new { code = CodeUnifiedOff, message = "правка страниц выключена: функция недоступна" });

    // Раздел — ключ из проекта/сферы владельца, страница — путь раздела; запись в раздел сферы и
    // в раздел локального проекта отказана теми же кодами, что «Завести вики».
    private (string? Key, IActionResult? Refusal) ResolveWritableSection(string userId, string? section, string? page)
    {
        if (string.IsNullOrWhiteSpace(section) || !IsSectionKey(section))
            return (null, BadRequest(new { code = CodeSectionKey, message = "не ключ раздела" }));
        if (WikiSections.TryProjectId(section, out var projectId))
        {
            var project = _projects.GetById(projectId);
            if (project is not null && project.OwnerId != userId)
                return (null, StatusCode(403, new { code = CodeForeign, message = "только своя вики" }));
            if (project is null)
                return (null, NotFound(new { code = CodeProjectMissing, message = "проекта нет — раздел-сирота, править нельзя" }));
            var capability = ProjectCapabilities.WikiSectionFor(project);
            if (!capability.Available)
                return (null, StatusCode(409, new { code = CodeSectionOff, message = capability.Reason }));
        }
        if (WikiSections.TrySphereId(section, out _))
            return (null, StatusCode(409, new { code = CodeSphereReserved, message = WikiSections.SphereWriteRefusal }));
        if (page is null || string.IsNullOrWhiteSpace(page) || WikiPaths.IsProjection(page) || WikiSections.Parse(page) != section)
            return (null, BadRequest(new { code = CodePageKey, message = "страница должна принадлежать разделу" }));
        return (section, null);
    }

    // Живое имя раздела для текста отказа (как в WikiController): чужой проект и сирота — null.
    private string? LiveNameOrNull(string userId, string key) =>
        WikiSections.TryProjectId(key, out var projectId)
            ? _projects.GetById(projectId) is { } p && p.OwnerId == userId ? p.Name : null
            : WikiSections.TrySphereId(key, out var sphereId) ? _spheres?.SphereName(userId, sphereId)
            : key switch { WikiSections.Common => "Общее", WikiSections.Personal => "Личное", _ => null };

    private static bool IsSectionKey(string key) =>
        key is WikiSections.Common or WikiSections.Personal
        || WikiSections.TryProjectId(key, out _) || WikiSections.TrySphereId(key, out _);

    private IActionResult ToResult(string userId, WikiApplyService.HumanEditResult res, string page) => res.Outcome switch
    {
        WikiApplyService.HumanEditOutcome.Applied =>
            Ok(new { status = "applied", packageId = res.PackageId, hash = res.Hash, content = res.Content }),
        WikiApplyService.HumanEditOutcome.Unchanged => Ok(new { status = "unchanged", hash = res.Hash }),
        WikiApplyService.HumanEditOutcome.Changed => StatusCode(409, new
        {
            code = CodePageChanged,
            message = res.Reason,
            page = res.Content is null && res.Hash is null ? null : new { pageKey = page, content = res.Content, hash = res.Hash },
        }),
        WikiApplyService.HumanEditOutcome.Drift => StatusCode(409, new { code = "wiki_drift", message = res.Reason }),
        WikiApplyService.HumanEditOutcome.Invalid =>
            BadRequest(new { code = CodeInvalid, message = res.Reason, violations = res.Violations,
                issues = WikiGateIssues.Classify(res.Violations, page, k => LiveNameOrNull(userId, k)) }),
        _ => StatusCode(409, new { code = "wiki_refused", message = res.Reason }),
    };
}

/// <summary>Сохранение страницы: Content — полный новый текст; BaseHash — sha256 байтов страницы
/// при открытии редактора (null — страницы не было).</summary>
public sealed record WikiPageEditRequest(string? Section, string? Page, string? BaseHash, string? Content);

/// <summary>«Вернуть мою»: Current — действующее утверждение, Restore — формулировка человека в «Истории».</summary>
public sealed record WikiPageRestoreRequest(string? Section, string? Page, string? BaseHash, string? Current, string? Restore);
