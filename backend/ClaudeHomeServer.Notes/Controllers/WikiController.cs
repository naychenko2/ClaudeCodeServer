using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Notes;
using ClaudeHomeServer.Services.Notes.Wiki;
using ClaudeHomeServer.Services.Spheres;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ClaudeHomeServer.Notes.Controllers;

// REST вики владельца (S2f, ADR-035/ADR-036): дерево разделов, каталог страниц раздела,
// страница, «Завести», adopt, удаление раздела-сироты. Образец — NotesController.
//
// Владелец — только из токена (claim sub), как во всей вертикали. Раздел — ключ,
// составленный из проекта/сферы пользователя (WikiSections), а не путь из тела запроса:
// чужой ключ, называющий существующий чужой проект, — 403. Человек видит ВСЮ свою вики:
// WikiVisibility — правило сборки контекста для моделей, а не право человека
// (ADR-036, решение 2, «Человек в UI видит всю вики») — здесь она не применяется.
//
// Страницы этот контроллер не пишет: «Завести», adopt и удаление раздела идут через
// WikiKeeper — единственный писатель pages/ (ADR-035, решение 2).
[ApiController]
[Authorize]
[Route("api/wiki")]
public class WikiController : ControllerBase
{
    private readonly WikiKeeper _keeper;
    private readonly IProjectManager _projects;
    private readonly ISphereDirectory _spheres;
    private readonly string _dataDir;

    public WikiController(WikiKeeper keeper, IProjectManager projects, ISphereDirectory spheres, IConfiguration config)
    {
        _keeper = keeper;
        _projects = projects;
        _spheres = spheres;
        // Каталог данных — как у всех сервисов (DataPath указывает на projects.json в dataDir).
        var dataPath = config["DataPath"] ?? Path.Combine(AppContext.BaseDirectory, "data", "projects.json");
        _dataDir = Path.GetDirectoryName(Path.GetFullPath(dataPath))!;
    }

    // Владелец — только из токена. [Authorize] — главный гейт; явная проверка — его копия
    // внутри действий: без токена действие отвечает 401 (контракт), а не падает с NRE на claim.
    private string? OwnerOrNull() =>
        User.Identity?.IsAuthenticated == true ? User.FindFirstValue(JwtRegisteredClaimNames.Sub) : null;

    // Коды отказов — контракт для фронта (S5a), устойчивые строки, как wiki_readonly.
    private const string CodeSectionKey = "wiki_section_key";
    private const string CodeForeign = "wiki_foreign";
    private const string CodePageKey = "wiki_page_key";
    private const string CodeSectionOff = "wiki_section_off";
    private const string CodeSphereReserved = "wiki_sphere_reserved";
    private const string CodeProjectMissing = "wiki_project_missing";
    private const string CodeNotOrphan = "wiki_not_orphan";

    // ─── Дерево разделов ────────────────────────────────────────────────────

    // GET /api/wiki/tree — дерево разделов своей вики: живые имена проектов и сфер,
    // пометка сироты, признаки local/sphereReserved, число страниц, признак заведённости.
    [HttpGet("tree")]
    public ActionResult<WikiTreeDto> Tree()
    {
        var userId = OwnerOrNull();
        if (userId is null) return Unauthorized();

        var sections = new Dictionary<string, WikiSectionDto>(StringComparer.Ordinal);
        // Постоянные разделы — в дереве всегда: они часть любой вики (ADR-036, решение 1).
        sections[WikiSections.Personal] = Describe(userId, WikiSections.Personal, "Личное", local: false);
        sections[WikiSections.Common] = Describe(userId, WikiSections.Common, "Общее", local: false);
        // Разделы проектов владельца — с ЖИВЫМ именем проекта (не из пути): и те, чьей папки
        // на диске ещё нет (раздел заводится первой применённой операцией — ADR-035, решение 1).
        foreach (var p in _projects.GetByOwner(userId))
            sections[WikiSections.ProjectsPrefix + p.Id] =
                Describe(userId, WikiSections.ProjectsPrefix + p.Id, p.Name,
                    local: ProjectCapabilities.IsDeviceBound(p));
        // Разделы на диске: сироты и сферы — живого списка их нет, только то, что есть.
        var pagesRoot = WikiPaths.Pages(_dataDir, userId);
        if (Directory.Exists(pagesRoot))
            foreach (var key in DiskSectionKeys(pagesRoot))
                if (!sections.ContainsKey(key))
                    sections[key] = Describe(userId, key, LiveNameOrNull(userId, key), local: false);

        return Ok(new WikiTreeDto(WikiPaths.IsInitialized(_dataDir, userId),
            [.. sections.Values.OrderBy(s => s.Key, StringComparer.Ordinal)]));
    }

    // ─── Каталог страниц раздела ────────────────────────────────────────────

    // GET /api/wiki/section?section={key} — каталог страниц: шапка (type/summary/status),
    // число источников, updated. Служебные файлы раздела (_*) в каталог не входят.
    [HttpGet("section")]
    public IActionResult Section([FromQuery] string? section)
    {
        var userId = OwnerOrNull();
        if (userId is null) return Unauthorized();
        var (key, refusal) = ResolveSection(userId, section);
        if (refusal is not null) return refusal;

        var pagesRoot = WikiPaths.Pages(_dataDir, userId);
        var dir = Path.Combine(pagesRoot, WikiSections.Folder(key!)!);
        var pages = new List<WikiPageInfoDto>();
        if (Directory.Exists(dir))
        {
            try
            {
                foreach (var full in Directory.EnumerateFiles(dir, "*.md", SearchOption.AllDirectories))
                {
                    var name = Path.GetFileName(full);
                    if (name.StartsWith('_')) continue; // проекции — не страницы
                    string text;
                    try { text = System.IO.File.ReadAllText(full); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
                    var fm = WikiFrontmatter.Parse(text).Model;
                    pages.Add(new WikiPageInfoDto(
                        Path.GetRelativePath(pagesRoot, full).Replace('\\', '/'),
                        TitleOf(text, name), fm?.Summary, fm?.Type, fm?.Status,
                        fm?.Sources.Count ?? 0, fm?.Updated));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Каталог раздела недоступен — пустой каталог, не исключение из запроса.
            }
        }
        pages.Sort((a, b) => string.CompareOrdinal(a.PageKey, b.PageKey));
        return Ok(new WikiCatalogDto(key!, LiveNameOrNull(userId, key!),
            WikiSections.IsOrphan(key!, userId, _spheres, _projects), pages));
    }

    // ─── Страница ───────────────────────────────────────────────────────────

    // GET /api/wiki/page?section={key}&page={pageKey} — текст страницы (только чтение).
    [HttpGet("page")]
    public IActionResult Page([FromQuery] string? section, [FromQuery] string? page)
    {
        var userId = OwnerOrNull();
        if (userId is null) return Unauthorized();
        var (key, refusal) = ResolveSection(userId, section);
        if (refusal is not null) return refusal;
        // Тот же контракт пары (раздел, страница), что у CheckPage Хранителя: страница обязана
        // принадлежать разделу, служебные файлы — не страницы.
        if (string.IsNullOrWhiteSpace(page) || WikiPaths.IsProjection(page) || WikiSections.Parse(page) != key)
            return BadRequest(new { code = CodePageKey, message = "страница должна принадлежать разделу" });

        var full = SafePath.JoinBelowRoot(WikiPaths.Root(_dataDir, userId), "pages/" + page);
        if (!System.IO.File.Exists(full)) return NotFound();
        return Ok(new { pageKey = page, content = System.IO.File.ReadAllText(full) });
    }

    // ─── «Завести вики» ─────────────────────────────────────────────────────

    /// <summary>«Завести вики»: pages/_schema.md и каталоги keeper/ (одна на владельца,
    /// повторный вызов ничего не меняет). Раздел в теле — адресат кнопки: по нему
    /// проверяется способность — локальный проект отказан кодом способности
    /// (ProjectCapabilities.WikiSectionFor, ADR-035 §9а); раздел сферы резервирован
    /// (ADR-036, решение 7). Сам раздел «Завести» не заводит: раздел заводится первой
    /// применённой операцией в нём.</summary>
    [HttpPost("initialize")]
    public async Task<IActionResult> Initialize([FromBody] WikiInitRequest? req)
    {
        var userId = OwnerOrNull();
        if (userId is null) return Unauthorized();

        if (!string.IsNullOrWhiteSpace(req?.Section))
        {
            var (key, refusal) = ResolveSection(userId, req!.Section);
            if (refusal is not null) return refusal;
            if (WikiSections.TryProjectId(key!, out var projectId))
            {
                var project = _projects.GetById(projectId);
                if (project is null)
                    return NotFound(new { code = CodeProjectMissing, message = "проекта нет — раздел-сирота, заводить не для чего" });
                var capability = ProjectCapabilities.WikiSectionFor(project);
                if (!capability.Available)
                    return StatusCode(409, new { code = CodeSectionOff, message = capability.Reason });
            }
            if (WikiSections.TrySphereId(key!, out _))
                return StatusCode(409, new { code = CodeSphereReserved, message = WikiSections.SphereWriteRefusal });
        }

        await _keeper.EnsureInitializedAsync(userId);
        return Ok(new { initialized = true });
    }

    // ─── Adopt: «Принять как есть» ──────────────────────────────────────────

    /// <summary>Системная операция adopt (ADR-035, решение 6): текущий файл на диске
    /// принимается в манифест раздела, дрейф снимается, файл не трогается.</summary>
    [HttpPost("adopt")]
    public async Task<IActionResult> Adopt([FromBody] WikiAdoptRequest? req)
    {
        var userId = OwnerOrNull();
        if (userId is null) return Unauthorized();
        if (req is null) return BadRequest(new { code = CodeSectionKey, message = "тело запроса не задано" });

        var (key, refusal) = ResolveSection(userId, req.Section);
        if (refusal is not null) return refusal;
        if (string.IsNullOrWhiteSpace(req.Page) || WikiPaths.IsProjection(req.Page) || WikiSections.Parse(req.Page) != key)
            return BadRequest(new { code = CodePageKey, message = "страница должна принадлежать разделу" });

        return OpResult(await _keeper.AdoptAsync(userId, key!, req.Page));
    }

    // ─── Удаление раздела-сироты ────────────────────────────────────────────

    /// <summary>Удаление раздела-сироты по кнопке человека (ADR-036, решение 1): только
    /// сирота; живой раздел удалить нельзя (409). Автоочистки сирот нет.</summary>
    [HttpDelete("section")]
    public async Task<IActionResult> DeleteSection([FromQuery] string? section)
    {
        var userId = OwnerOrNull();
        if (userId is null) return Unauthorized();
        var (key, refusal) = ResolveSection(userId, section);
        if (refusal is not null) return refusal;
        if (!WikiSections.IsOrphan(key!, userId, _spheres, _projects))
            return StatusCode(409, new { code = CodeNotOrphan, message = "удаляется только раздел-сирота" });

        var res = await _keeper.DeleteSectionAsync(userId, key!);
        if (res.Status != WikiOpStatus.Applied) return OpResult(res);
        return Ok(new { deleted = true });
    }

    // ─── Помощники ──────────────────────────────────────────────────────────

    // Раздел — ключ из проекта/сферы владельца (ADR-036, решение 1), не путь из тела:
    // ключ проверяется как ключ (400). Если он называет существующий проект — проект обязан
    // принадлежать владельцу (403: только своя вики). Раздел удалённого проекта принадлежит
    // владельцу по месту: он лежит в data/wiki/{ownerId}/.
    private (string? Key, IActionResult? Refusal) ResolveSection(string userId, string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw) || !IsSectionKey(raw))
            return (null, BadRequest(new { code = CodeSectionKey, message = "не ключ раздела" }));
        if (WikiSections.TryProjectId(raw, out var projectId)
            && _projects.GetById(projectId) is { } project && project.OwnerId != userId)
            return (null, StatusCode(403, new { code = CodeForeign, message = "только своя вики" }));
        return (raw, null);
    }

    private static bool IsSectionKey(string key) =>
        key is WikiSections.Common or WikiSections.Personal
        || WikiSections.TryProjectId(key, out _) || WikiSections.TrySphereId(key, out _);

    // Живое имя раздела: проект — имя проекта (только своего; чужое имя в своё дерево
    // не протекает), сфера — имя из справочника; сирота — null (живого имени нет).
    private string? LiveNameOrNull(string userId, string key) =>
        WikiSections.TryProjectId(key, out var projectId)
            ? _projects.GetById(projectId) is { } p && p.OwnerId == userId ? p.Name : null
            : WikiSections.TrySphereId(key, out var sphereId) ? _spheres.SphereName(userId, sphereId)
            : key switch { WikiSections.Common => "Общее", WikiSections.Personal => "Личное", _ => null };

    private WikiSectionDto Describe(string userId, string key, string? name, bool local) =>
        new(key, name,
            WikiSections.IsOrphan(key, userId, _spheres, _projects),
            local,
            WikiSections.TrySphereId(key, out _),
            CountPages(userId, key));

    // Ключи разделов, которые есть на диске: верхние папки pages/ и их подпапки-id.
    // Имена папок при этом не хардкодятся — разбор один, WikiSections.Parse (fail-closed:
    // неразобранный каталог в дерево не попадает).
    private static IEnumerable<string> DiskSectionKeys(string pagesRoot)
    {
        foreach (var top in Directory.EnumerateDirectories(pagesRoot))
        {
            var relTop = Path.GetRelativePath(pagesRoot, top).Replace('\\', '/');
            if (WikiSections.Parse(relTop + "/") is { } key) { yield return key; continue; }
            // «Проекты»/«Сферы» сами — не ключ раздела: разделы — их подпапки по id.
            foreach (var sub in Directory.EnumerateDirectories(top))
            {
                var relSub = Path.GetRelativePath(pagesRoot, sub).Replace('\\', '/');
                if (WikiSections.Parse(relSub + "/") is { } subKey) yield return subKey;
            }
        }
    }

    // Число страниц раздела: .md без служебных проекций (_*). Сироты и пустые разделы — 0.
    private int CountPages(string userId, string sectionKey)
    {
        var folder = WikiSections.Folder(sectionKey);
        if (folder is null) return 0;
        var dir = Path.Combine(WikiPaths.Pages(_dataDir, userId), folder);
        if (!Directory.Exists(dir)) return 0;
        try
        {
            return Directory.EnumerateFiles(dir, "*.md", SearchOption.AllDirectories)
                .Count(f => !Path.GetFileName(f).StartsWith('_'));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    // Заголовок страницы: первый «# …» тела, иначе — имя файла. Строгая шапка опциональна
    // для показа каталога: страница с битой шапкой остаётся видимой (её чинит Хранитель/lint).
    private static string TitleOf(string text, string fileName)
    {
        foreach (var line in WikiFrontmatter.SplitLines(text))
            if (line.StartsWith("# ", StringComparison.Ordinal)) return line[2..].Trim();
        return Path.GetFileNameWithoutExtension(fileName);
    }

    private IActionResult OpResult(WikiOpResult res) => res.Status switch
    {
        WikiOpStatus.Applied => Ok(new { status = "applied" }),
        WikiOpStatus.Drift => StatusCode(409, new { code = "wiki_drift", message = res.Reason }),
        _ => StatusCode(409, new { code = "wiki_refused", message = res.Reason }),
    };
}

// ─── DTO контракта (С5a — по ним работает Кира) ─────────────────────────────

/// <summary>Один раздел дерева: key — ключ раздела (ADR-036, решение 1); name — живое имя
/// проекта/сферы, null у сироты; orphan — раздел удалённого проекта/сферы; local — проект
/// локальный (способности WikiSection нет, ADR-035 §9а); sphereReserved — запись в раздел
/// резервирована за следующей фичей (ADR-036, решение 7); pageCount — число страниц.</summary>
public sealed record WikiSectionDto(
    string Key, string? Name, bool Orphan, bool Local, bool SphereReserved, int PageCount);

/// <summary>Дерево разделов своей вики. initialized — есть pages/_schema.md («Вики не заведена»).</summary>
public sealed record WikiTreeDto(bool Initialized, IReadOnlyList<WikiSectionDto> Sections);

/// <summary>Страница в каталоге раздела: summary/type/status — из шапки (null, если шапка
/// не разобрана — страница остаётся видимой); sourcesCount — число источников из шапки.</summary>
public sealed record WikiPageInfoDto(
    string PageKey, string Title, string? Summary, string? Type, string? Status, int SourcesCount, string? Updated);

/// <summary>Каталог страниц раздела.</summary>
public sealed record WikiCatalogDto(string Key, string? Name, bool Orphan, IReadOnlyList<WikiPageInfoDto> Pages);

/// <summary>«Завести вики». Section — необязательный адресат кнопки (ключ раздела проекта);
/// по нему проверяется способность. Сам раздел не заводится — его заводит первая
/// применённая операция.</summary>
public sealed record WikiInitRequest(string? Section);

/// <summary>«Принять как есть» для страницы раздела.</summary>
public sealed record WikiAdoptRequest(string Section, string Page);
