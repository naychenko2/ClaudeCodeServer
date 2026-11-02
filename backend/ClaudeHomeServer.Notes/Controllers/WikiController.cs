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

// REST вики владельца (S2f, ADR-035/ADR-036): дерево разделов, каталог страниц раздела,
// страница, «Завести», adopt, удаление раздела-сироты; здоровье раздела и запуск линта
// по кнопке (S3-3); журнал своей вики (S7c-0, ADR-035 §12г меры 4–6); «Разобрать заметку
// в базу» (S7b0b). Образец — NotesController.
//
// Владелец — только из токена (claim sub), как во всей вертикали. Сервисный токен хода
// (typ=svc) — все мутирующие ручки этого контроллера отказаны 403 (B2a): вику решает и
// заводит только человек; ручки чтения отвечают под svc как раньше. Раздел — ключ,
// составленный из проекта/сферы пользователя (WikiSections), а не путь из тела запроса:
// чужой ключ, называющий существующий чужой проект, — 403. Человек видит ВСЮ свою вики:
// WikiVisibility — правило сборки контекста для моделей, а не право человека
// (ADR-036, решение 2, «Человек в UI видит всю вики») — здесь она не применяется.
//
// Страницы этот контроллер не пишет: «Завести», adopt и удаление раздела идут через
// WikiKeeper — единственный писатель pages/ (ADR-035, решение 2). Журнал — тоже только
// чтение: он читает log.jsonl и пакеты, ничего не меняя (S7c-0).
[ApiController]
[Authorize]
[Route("api/wiki")]
public class WikiController : ControllerBase
{
    private readonly WikiKeeper _keeper;
    private readonly IProjectManager _projects;
    private readonly ISphereDirectory _spheres;
    private readonly WikiLintService _lint;
    private readonly WikiJournalReader _journal;
    // S7b0b: «Разобрать заметку в базу» — тот же захват и та же очередь разбора, что у
    // инструмента wiki_ingest (NotesToolset); регистрация — NotesSubsystem (Singleton).
    private readonly WikiSourceCapture _capture;
    private readonly WikiIngestService _ingest;
    private readonly IFeatureFlagGate? _flags;
    private readonly string _dataDir;

    public WikiController(WikiKeeper keeper, IProjectManager projects, ISphereDirectory spheres,
        WikiLintService lint, WikiJournalReader journal, IFeatureFlagGate? flags, IConfiguration config,
        WikiSourceCapture capture, WikiIngestService ingest)
    {
        _keeper = keeper;
        _projects = projects;
        _spheres = spheres;
        _lint = lint;
        _journal = journal;
        _flags = flags;
        _capture = capture;
        _ingest = ingest;
        // Каталог данных — как у всех сервисов (DataPath указывает на projects.json в dataDir).
        var dataPath = config["DataPath"] ?? Path.Combine(AppContext.BaseDirectory, "data", "projects.json");
        _dataDir = Path.GetDirectoryName(Path.GetFullPath(dataPath))!;
    }

    // Владелец — только из токена. [Authorize] — главный гейт; явная проверка — его копия
    // внутри действий: без токена действие отвечает 401 (контракт), а не падает с NRE на claim.
    private string? OwnerOrNull() =>
        User.Identity?.IsAuthenticated == true ? User.FindFirstValue(JwtRegisteredClaimNames.Sub) : null;

    // Сервисный JWT хода (claim typ=svc, тот же смысл, что JwtService): к вики он не адресат —
    // «Завести», adopt, удаление раздела, разбор заметки и линт отказаны им 403 (B2a; разведка
    // B0: легитимных svc-вызовов этих ручек нет). Локальные константы — как в PluginsController
    // и TasksController: вертикаль не тянет ядро ради двух строк. Отказ — до разбора тела и до
    // любых состояний: svc не доходит ни до одной записи.
    private const string TokenKindClaim = "typ";
    private const string ServiceTokenKind = "svc";
    private bool IsServiceToken => User.FindFirstValue(TokenKindClaim) == ServiceTokenKind;
    private IActionResult ServiceTokenRefusal() =>
        StatusCode(403, new { error = "Сервисный токен: вики решает только пользовательский токен" });

    // Коды отказов — контракт для фронта (S5a), устойчивые строки, как wiki_readonly.
    private const string CodeSectionKey = "wiki_section_key";
    private const string CodeForeign = "wiki_foreign";
    private const string CodePageKey = "wiki_page_key";
    private const string CodeSectionOff = "wiki_section_off";
    private const string CodeSphereReserved = "wiki_sphere_reserved";
    private const string CodeProjectMissing = "wiki_project_missing";
    private const string CodeNotOrphan = "wiki_not_orphan";
    private const string CodeLintRunning = "wiki_lint_running";
    private const string CodeLlmWikiOff = "wiki_llm_wiki_off";
    private const string CodeOriginKey = "wiki_origin_key";
    private const string CodeClassKey = "wiki_class_key";
    // S7b0b «Разобрать заметку в базу»: состояния вики — 409 с кодом (тот же смысл, что у
    // отказа инструмента wiki_ingest), заметка, не ставшая источником, — 404 not_found.
    private const string CodeNotInitialized = "wiki_not_initialized";
    private const string CodePackagesLimit = "packages_limit";
    private const string CodeNotFound = "not_found";
    private const string CodeNoteId = "wiki_note_id";

    // Состояния манифеста раздела в контракте (WikiCatalogDto.ManifestState / ответ
    // GET page): закрытый перечень — три состояния WikiManifestState без внутреннего имени.
    // Значения совпадают с union WikiManifestState фронта (frontend/src/lib/wiki.ts):
    // «ok» — норма, «missing»/«invalid» — «манифест не прочитан», и того и другого хватает,
    // чтобы не рисовать плашку дрейфа на разделе без манифеста.
    private const string ManifestStateOk = "ok";
    private const string ManifestStateMissing = "missing";
    private const string ManifestStateInvalid = "invalid";

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

        var root = WikiPaths.Root(_dataDir, userId);
        var pagesRoot = WikiPaths.Pages(_dataDir, userId);
        var dir = Path.Combine(pagesRoot, WikiSections.Folder(key!)!);
        // Признак дрейфа каждой страницы: файл сверяется с манифестом раздела той же мерой,
        // что шаг 2 применения (WikiApplyService.IsPageDrifted) — второй копии меры нет. UI
        // получает плашку «Расходится с журналом Хранителя» и «Принять как есть» ДО adopt,
        // а не после 409 от применения. Только чтение: блокировки раздела здесь нет — GET
        // ничего не пишет и не держит применение (ревью S5b).
        var manifest = ReadManifestQuietly(root, key!);
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
                    var pageKey = Path.GetRelativePath(pagesRoot, full).Replace('\\', '/');
                    pages.Add(new WikiPageInfoDto(
                        pageKey,
                        TitleOf(text, name), fm?.Summary, fm?.Type, fm?.Status,
                        fm?.Sources.Count ?? 0, fm?.Updated,
                        IsDrifted(root, pageKey, manifest)));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Каталог раздела недоступен — пустой каталог, не исключение из запроса.
            }
        }
        pages.Sort((a, b) => string.CompareOrdinal(a.PageKey, b.PageKey));
        return Ok(new WikiCatalogDto(key!, LiveNameOrNull(userId, key!),
            WikiSections.IsOrphan(key!, userId, _spheres, _projects), pages, ManifestStateOf(manifest.State)));
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

        var root = WikiPaths.Root(_dataDir, userId);
        var full = SafePath.JoinBelowRoot(root, "pages/" + page);
        if (!System.IO.File.Exists(full)) return NotFound();
        // Дрейф этой страницы (файл против её записи в манифесте) и состояние манифеста —
        // то, чем UI рисует плашку и «Принять как есть»: до adopt человек видит их, а не 409.
        var manifest = ReadManifestQuietly(root, key!);
        return Ok(new
        {
            pageKey = page,
            content = System.IO.File.ReadAllText(full),
            // sha256 байтов страницы — baseHash редактора (B3b): сохранение сверяет его под замком раздела.
            hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.IO.File.ReadAllBytes(full))).ToLowerInvariant(),
            drift = IsDrifted(root, page!, manifest),
            manifestState = ManifestStateOf(manifest.State),
        });
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
        if (IsServiceToken) return ServiceTokenRefusal();
        if (_flags?.IsEnabled(userId, FeatureFlagKeys.LlmWiki) != true)
            return StatusCode(409, new { code = CodeLlmWikiOff, message = "вики не заведена: функция выключена" });

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
        if (IsServiceToken) return ServiceTokenRefusal();
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
        if (IsServiceToken) return ServiceTokenRefusal();
        var (key, refusal) = ResolveSection(userId, section);
        if (refusal is not null) return refusal;
        if (!WikiSections.IsOrphan(key!, userId, _spheres, _projects))
            return StatusCode(409, new { code = CodeNotOrphan, message = "удаляется только раздел-сирота" });

        var res = await _keeper.DeleteSectionAsync(userId, key!);
        if (res.Status != WikiOpStatus.Applied) return OpResult(res);
        return Ok(new { deleted = true });
    }

    // ─── «Разобрать заметку в базу» (S7b0b) ─────────────────────────────────

    /// <summary>POST /api/wiki/ingest/note {noteId, target?}: та же склейка «захват →
    /// карточка → заявка», что у инструмента wiki_ingest (WikiCardIngest, S7b0b): два
    /// входа — одна логика. Заметка попадает в разбор только по заявке владельца и только
    /// своя: владельца проверяет CaptureNote (тот же путь, что у MCP). Порядок отказов —
    /// как у инструмента: не заведённая вика и лимит открытых пакетов проверяются ДО
    /// захвата (21-я заявка не ставится, ADR-035, решение 4). Отказ захвата — 404
    /// {code:"not_found"}: для вызывающего заметка не стала источником (чужой, нет такой,
    /// не источник), причина — в message. Исход заявки — 200 с outcome: её решает очередь
    /// разбора (WikiIngestService), не эта ручка; исключений наружу нет.</summary>
    [HttpPost("ingest/note")]
    public async Task<IActionResult> IngestNote([FromBody] WikiIngestNoteRequest? req)
    {
        var userId = OwnerOrNull();
        if (userId is null) return Unauthorized();
        if (IsServiceToken) return ServiceTokenRefusal();
        if (_flags?.IsEnabled(userId, FeatureFlagKeys.LlmWiki) != true)
            return StatusCode(409, new { code = CodeLlmWikiOff, message = "заявка не принята: функция выключена" });
        if (req is null || string.IsNullOrWhiteSpace(req.NoteId))
            return BadRequest(new { code = CodeNoteId, message = "нужен id заметки" });

        var root = WikiPaths.Root(_dataDir, userId);
        if (!WikiPaths.IsInitialized(_dataDir, userId))
            return StatusCode(409, new { code = CodeNotInitialized,
                message = "вики этого владельца не заведена: её разбор работает только с заведённой" });
        if (WikiPackageStore.CountOpen(root) >= WikiPackage.MaxOpenPackages)
            return StatusCode(409, new { code = CodePackagesLimit,
                message = $"открытых пакетов уже {WikiPackage.MaxOpenPackages}: разберите Входящие, потом повторяй заявку." });

        var cap = await _capture.CaptureNote(userId, req.NoteId, req.Target);
        if (!cap.Ok)
            return NotFound(new { code = CodeNotFound, message = cap.Reason ?? "заметка не найдена" });

        var res = await WikiCardIngest.RunAsync(_ingest, cap, root, userId, HttpContext.RequestAborted);
        return Ok(new { outcome = res.Outcome, packageId = res.PackageId, reason = res.Reason });
    }

    // ─── Здоровье раздела (S3-3) ─────────────────────────────────────────────

    /// <summary>GET /api/wiki/health/{section} — _health.md раздела как есть: это отчёт
    /// последнего прогона линта (текст Хранителя), а не производный DTO. Файла нет — 404:
    /// раздел ещё не линтовался (или его вовсе нет на диске), и при выключенном llm-wiki
    /// свежих отчётов нет вовсе: прогон такой вики не пишется (WikiLintService), поэтому её
    /// health — 404 «нет данных», а не «здорово». Чужой владелец — 403 той же
    /// ResolveSection, что у каталога. Section — ключ с «/» (projects/{id}), поэтому
    /// параметр catch-all.</summary>
    [HttpGet("health/{**section}")]
    public IActionResult Health(string? section)
    {
        var userId = OwnerOrNull();
        if (userId is null) return Unauthorized();
        var (key, refusal) = ResolveSection(userId, section);
        if (refusal is not null) return refusal;

        var full = SafePath.JoinBelowRoot(WikiPaths.Root(_dataDir, userId!),
            "pages/" + WikiSections.Folder(key!) + WikiKeeper.HealthFileName);
        if (!System.IO.File.Exists(full)) return NotFound();
        try
        {
            return Ok(new { section = key, content = System.IO.File.ReadAllText(full) });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Файл есть, но не читается — тот же ответ, что «файла нет»: health нет.
            return NotFound();
        }
    }

    // ─── Прогон линта по кнопке (S3-3) ───────────────────────────────────────

    /// <summary>POST /api/wiki/lint — прогон линта по всем разделам своей вики. Функция
    /// выключена — 409 с причиной: прогон такой вики не выполняется вовсе, и её «Находки (0)»
    /// были бы ложью (решение по ревью S3-3). Уже идёт — 409 с кодом: второй прогон того же
    /// владельца дал бы двойную запись _health.md и metrics.jsonl (конкурентность шага
    /// S3-3).</summary>
    [HttpPost("lint")]
    public async Task<IActionResult> Lint()
    {
        var userId = OwnerOrNull();
        if (userId is null) return Unauthorized();
        if (IsServiceToken) return ServiceTokenRefusal();
        var res = await _lint.TryRunOwnerAsync(userId);
        if (res.Outcome == WikiLintRunOutcome.FeatureOff)
            return StatusCode(409, new { code = CodeLlmWikiOff, message = "проверка не выполнена: функция выключена" });
        if (!res.Started)
            return StatusCode(409, new { code = CodeLintRunning, message = "линт владельца уже идёт" });
        return Ok(new { ran = true, sectionsOk = res.SectionsOk, sectionsFailed = res.SectionsFailed });
    }

    // ─── Журнал ─────────────────────────────────────────────────────────────

    /// <summary>GET /api/wiki/journal?section=&amp;origin=&amp;class=&amp;take= — журнал своей вики:
    /// записи log.jsonl, склеенные с пакетами (статус, политика «само / через Входящие», класс
    /// origin × op, страницы), с именем источника (чат — название его владельца, заметка —
    /// заголовок). Новые сверху. Только чтение: ручка ничего не пишет (S7c-0, ADR-035 §12г
    /// меры 4–6; пункт 5 «Нужно от бэка»).
    /// Окно — последние `take` строк (DefaultTake, потолка MaxTake; выше — урезается, фактическое
    /// окно видно в ответе): filters сужают прочитанный хвост, а не ищут за его краем.
    /// Отказы: 403 — раздел называет чужой проект (тот же ResolveSection, что у раздела и
    /// страницы), 400 — не ключ / значение фильтра вне закрытого перечня, 409 — функция
    /// выключена (тот же код, что у линта и настроек). Несуществующий раздел — не отказ:
    /// журнал пустой (раздел мог не иметь ни одной операции).</summary>
    [HttpGet("journal")]
    public IActionResult Journal(
        [FromQuery] string? section, [FromQuery] string? origin, [FromQuery] string? @class, [FromQuery] int? take)
    {
        var userId = OwnerOrNull();
        if (userId is null) return Unauthorized();
        if (_flags?.IsEnabled(userId, FeatureFlagKeys.LlmWiki) != true)
            return StatusCode(409, new { code = CodeLlmWikiOff, message = "журнал не прочитан: функция выключена" });

        string? key = null;
        if (!string.IsNullOrWhiteSpace(section))
        {
            var (resolved, refusal) = ResolveSection(userId, section);
            if (refusal is not null) return refusal;
            key = resolved;
        }
        if (origin is not null && !WikiGate.OriginValues.Contains(origin))
            return BadRequest(new { code = CodeOriginKey, message = $"источник «{origin}» не из закрытого перечня" });
        if (@class is not null && !WikiSettingsStore.IsValidClassKey(@class))
            return BadRequest(new { code = CodeClassKey, message = $"класс «{@class}» не пары «источник × операция»" });

        var res = _journal.Read(userId, new WikiJournalReader.Query(key, origin, @class, take));
        return Ok(new WikiJournalDto(
            [.. res.Entries.Select(e => new WikiJournalEntryDto(
                e.Ts, e.Op, e.Section, e.Actor, e.Source, e.Origin, e.SourceName, e.Pages, e.Packages,
                e.Status, e.Policy, e.Classes))],
            res.Take, res.LinesRead, res.InvalidLines));
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

    // Манифест раздела для показа — то же чтение, что у Хранителя и применения, но без
    // исключения наружу: нечитаемый файл манифеста не роняет GET (урок S4b). Его состояние
    // при этом не «Valid» ни при каком чтении: ни применения, ни плашки дрейфа (IsDrifted).
    private static WikiManifestReadResult ReadManifestQuietly(string root, string sectionKey)
    {
        try
        {
            return WikiManifest.TryRead(root, sectionKey);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new WikiManifestReadResult(WikiManifestState.Invalid, new Dictionary<string, string>());
        }
    }

    // Дрейф страницы для показа — та же мера, что шаг 2 применения (WikiApplyService.IsPageDrifted):
    // файл ≠ его запись в манифесте, файла нет, а запись есть, либо файл есть, а записи нет.
    // Манифест Missing/Invalid — НЕ дрейф: на разделе без манифеста плашка была бы ложной,
    // UI решает по manifestState (решение Софьи). Не подтверждённый дрейф (файл не читается,
    // в манифесте чужой путь) — тоже не дрейф: страница остаётся обычной, её чинит Хранитель.
    private static bool IsDrifted(string root, string pageKey, WikiManifestReadResult manifest)
    {
        if (manifest.State != WikiManifestState.Valid) return false;
        try
        {
            return WikiApplyService.IsPageDrifted(root, pageKey, manifest.Manifest);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static string ManifestStateOf(WikiManifestState state) => state switch
    {
        WikiManifestState.Valid => ManifestStateOk,
        WikiManifestState.Missing => ManifestStateMissing,
        _ => ManifestStateInvalid,
    };

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
/// не разобрана — страница остаётся видимой); sourcesCount — число источников из шапки;
/// drift — файл страницы разошёлся с её записью в манифесте раздела (правка мимо Хранителя):
/// UI рисует плашку и «Принять как есть». Ложного дрейфа нет: раздел без манифеста или с
/// невалидным манифестом — drift=false, состояние видно в catalog.manifestState.</summary>
public sealed record WikiPageInfoDto(
    string PageKey, string Title, string? Summary, string? Type, string? Status, int SourcesCount, string? Updated,
    bool Drift);

/// <summary>Каталог страниц раздела. manifestState — состояние манифеста раздела:
/// «ok» | «missing» | «invalid» (закрытый перечень; «missing» — норма до первого
/// применения). Дрейф страниц считается только при «ok».</summary>
public sealed record WikiCatalogDto(string Key, string? Name, bool Orphan, IReadOnlyList<WikiPageInfoDto> Pages, string ManifestState);

/// <summary>«Завести вики». Section — необязательный адресат кнопки (ключ раздела проекта);
/// по нему проверяется способность. Сам раздел не заводится — его заводит первая
/// применённая операция.</summary>
public sealed record WikiInitRequest(string? Section);

/// <summary>«Принять как есть» для страницы раздела.</summary>
public sealed record WikiAdoptRequest(string Section, string Page);

/// <summary>«Разобрать заметку в базу» (S7b0b). NoteId — id заметки: только своей, это
/// проверяет захват. Target — необязательный явный выбор «common» для источника вне
/// проекта (ADR-036, решение 3): его проверяет захват (WikiSourceHome), второй таблицы
/// допустимых target здесь нет.</summary>
public sealed record WikiIngestNoteRequest(string? NoteId, string? Target = null);

/// <summary>Запись журнала (S7c-0): событие строки log.jsonl и то, что о нём сказал пакет.
/// ts/op/section/actor/source/pages — из строки журнала как они записаны; origin — префикс
/// источника (первый, если их несколько: строка несёт их списком, отдельного поля под множество
/// нет); sourceName — имя чата этого владельца или заголовок заметка, иначе null (чужой и
/// несуществующий чат — без имени, не отказ; имя — данные, без разметки, до 120 символов);
/// status/policy/classes — из склейки с пакетом: status null, когда пакетов несколько (их id в
/// packages), classes пуст, когда пакета нет (relink, adopt). policy «auto» | «inbox» — из
/// строки применения, иначе по пакету; packages — id, названные строкой retract. Формат добавляет
/// поля к тому, что фронт строит на GET /api/wiki/packages, и ничего не меняет (S7c-2).</summary>
public sealed record WikiJournalEntryDto(
    DateTimeOffset Ts, string Op, string Section, string Actor,
    string? Source, string? Origin, string? SourceName,
    IReadOnlyList<string> Pages, IReadOnlyList<string> Packages,
    string? Status, string? Policy, IReadOnlyList<string> Classes);

/// <summary>Окно журнала. take — фактическое окно после обрезки (запрошенное выше потолка
/// урезается); linesRead — сколько строк журнала реально прочитано (мера того, что хвост
/// ограничен, а не весь файл); invalidLines — нечитаемые строки хвоста: их видно числом, они не
/// притворяются записями.</summary>
public sealed record WikiJournalDto(
    IReadOnlyList<WikiJournalEntryDto> Entries, int Take, int LinesRead, int InvalidLines);
