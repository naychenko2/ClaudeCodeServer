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
//
// С LI-2 «разобрать заметку в базу» и «перенести в вики» заводят вику заявителя тем же
// EnsureInitializedAsync, что и POST initialize, — вместо прежнего 409 wiki_not_initialized:
// человек не обязан заводить вику руками. Ручка initialize остаётся как есть (совместимость
// и ручной повтор); GET-ручки вики не заводят.
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
    // B5: «Принять как мою правку» (пакет human из диффа) и «Перенести в вики» (заметка → страница).
    private readonly WikiApplyService _apply;
    private readonly WikiNoteTransfer _transfer;
    private readonly IFeatureFlagGate? _flags;
    private readonly TimeProvider _time;
    private readonly string _dataDir;

    // Окно метки «ИИ заменил моё» на странице (ADR-038, контракт чтения для фронта).
    private static readonly TimeSpan OverridesWindow = TimeSpan.FromDays(14);

    public WikiController(WikiKeeper keeper, IProjectManager projects, ISphereDirectory spheres,
        WikiLintService lint, WikiJournalReader journal, IFeatureFlagGate? flags, IConfiguration config,
        WikiSourceCapture capture, WikiIngestService ingest, WikiApplyService apply, WikiNoteTransfer transfer,
        TimeProvider? time = null)
    {
        _time = time ?? TimeProvider.System;
        _apply = apply;
        _transfer = transfer;
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
    // S7b0b «Разобрать заметку в базу»: заметка, не ставшая источником, — 404 not_found.
    // Состояния «вики не заведена» здесь больше нет: с LI-2 вика заявителя заводится до заявки
    // (тот же код остаётся за MCP-инструментом и сервисами — вторая линия защиты).
    private const string CodePackagesLimit = "packages_limit";
    private const string CodeNotFound = "not_found";
    private const string CodeNoteId = "wiki_note_id";
    // B5: режим adopt, серверный гейт совместной правки, исходы переноса заметки.
    private const string CodeAdoptMode = "wiki_adopt_mode";
    private const string CodeUnifiedOff = "wiki_unified_off";
    private const string CodePageChanged = "wiki_page_changed";
    private const string CodeEditInvalid = "wiki_edit_invalid";
    private const string CodeNoteTransferred = "wiki_note_transferred";
    private const string CodeTransferTarget = "wiki_transfer_target";

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
                var texts = new List<(string PageKey, string Name, string Text)>();
                foreach (var full in Directory.EnumerateFiles(dir, "*.md", SearchOption.AllDirectories))
                {
                    var name = Path.GetFileName(full);
                    if (name.StartsWith('_')) continue; // проекции — не страницы
                    string text;
                    try { text = System.IO.File.ReadAllText(full); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
                    texts.Add((Path.GetRelativePath(pagesRoot, full).Replace('\\', '/'), name, text));
                }
                // Авторство считает одна точка — WikiProjections.Authorship; у карточек источников его нет.
                var authorship = WikiProjections.Authorship(
                    texts.Select(t => new WikiGate.Draft(t.PageKey, t.Text)).ToList());
                foreach (var (pageKey, name, text) in texts)
                {
                    var fm = WikiFrontmatter.Parse(text).Model;
                    pages.Add(new WikiPageInfoDto(
                        pageKey,
                        TitleOf(text, name), fm?.Summary, fm?.Type, fm?.Status,
                        fm?.Sources.Count ?? 0, fm?.Updated,
                        IsDrifted(root, pageKey, manifest),
                        authorship.GetValueOrDefault(pageKey)));
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
        var content = System.IO.File.ReadAllText(full);
        var (authorship, footnotes) = ReadAuthorship(root, key!, content);
        var overrides = _journal.PageOverrides(userId, key!, page!, _time.GetUtcNow() - OverridesWindow, content)
            .Select(o => new WikiOverrideDto(o.Op, o.Statement, ResolveHistoryLine(content, o.Previous), o.At, o.PackageId,
                o.Op == WikiPackage.OpConflict ? ResolveLiveLine(content, o.Previous, o.Statement) : null))
            .ToList();
        return Ok(new
        {
            pageKey = page,
            content,
            authorship,
            footnotes,
            overrides,
            // sha256 байтов страницы — baseHash редактора (B3b): сохранение сверяет его под замком раздела.
            hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.IO.File.ReadAllBytes(full))).ToLowerInvariant(),
            drift = IsDrifted(root, page!, manifest),
            manifestState = ManifestStateOf(manifest.State),
        });
    }

    // «Вернуть мою» принимает формулировку из «Истории» страницы, а не цитату цели модели: цель
    // бывает короче строки (буллет, разметка, сноски). Пару previous отдаём как фактическую строку
    // «Истории», найденную терпимым поиском (как в retract); не нашли — null, метка остаётся без пары.
    private static string? ResolveHistoryLine(string content, string? target)
    {
        if (string.IsNullOrWhiteSpace(target)
            || WikiPage.Parse(content).Model?.Sections.FirstOrDefault(s => s.Name == "История") is not { } history)
            return null;
        foreach (var line in history.Lines)
            if (QuoteMatcher.ContainsVerbatim(line, target))
                return line.Trim();
        return null;
    }

    // Спор: метка «ИИ оспаривает» стоит на живой строке человека, на которую ссылается пакет (op.Target).
    // Ищем терпимым поиском только среди секций утверждений, кроме «Истории» (как FindStatement:
    // «Заметки» и «Открытые вопросы» — не цель спора); строку, дословно цитирующую довод ИИ
    // (statement), пропускаем — довод не может быть целью. Не нашли — null.
    private static string? ResolveLiveLine(string content, string? target, string? statement)
    {
        if (string.IsNullOrWhiteSpace(target) || WikiPage.Parse(content).Model is not { } model)
            return null;
        foreach (var section in model.Sections.Where(s =>
                     s.Name != "История" && WikiPage.AssertionSectionNames.Contains(s.Name)))
            foreach (var line in section.Lines)
            {
                if (!string.IsNullOrWhiteSpace(statement) && QuoteMatcher.ContainsVerbatim(line, statement))
                    continue;
                if (QuoteMatcher.ContainsVerbatim(line, target))
                    return line.Trim();
            }
        return null;
    }

    // Авторство и сноски страницы: карточки источников берутся из папки «Источники/» раздела
    // (там их держит захват), счёт — WikiProjections. Неразобранная страница — без сносок и
    // без признака: показать нечего, выдумывать нечем.
    private static (string? Authorship, IReadOnlyList<WikiFootnoteDto> Footnotes) ReadAuthorship(
        string root, string sectionKey, string content)
    {
        if (WikiPage.Parse(content).Model is not { } model)
            return (null, []);
        var cards = new List<WikiGate.Draft>();
        var dir = SafePath.JoinBelowRoot(root, "pages/" + WikiSections.Folder(sectionKey)!);
        if (Directory.Exists(dir))
        {
            try
            {
                foreach (var f in Directory.EnumerateFiles(dir, "*.md", SearchOption.AllDirectories))
                {
                    var rel = Path.GetRelativePath(dir, f).Replace('\\', '/');
                    if (!rel.Split('/').SkipLast(1).Contains(WikiSourceCapture.SourceDirName)) continue;
                    try { cards.Add(new WikiGate.Draft(WikiSections.Folder(sectionKey) + rel, System.IO.File.ReadAllText(f))); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        var index = WikiProjections.Cards(cards);
        var authorship = model.Frontmatter.Type == WikiFrontmatter.TypeSource
            ? null
            : WikiProjections.PageAuthorship(model, t => index.TryGetValue(WikiGate.Norm(t), out var c) ? c.Origin : null);
        var footnotes = WikiProjections.Footnotes(model, index)
            .Select(f => new WikiFootnoteDto(f.Label, f.Card, f.Origin, f.Captured, f.CardPage))
            .ToList();
        return (authorship, footnotes);
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
    /// принимается в манифест раздела, дрейф снимается, файл не трогается. Режимы (ADR-038, §6):
    /// `as-is` (по умолчанию, без флага) — как выше; `mine` («Принять как мою правку») — дифф
    /// «манифест → диск» собирается в пакет human, под флагами llm-wiki и notes-wiki-unified,
    /// baseHash обязателен (sha256 файла, каким его видел человек).</summary>
    [HttpPost("adopt")]
    public async Task<IActionResult> Adopt([FromBody] WikiAdoptRequest? req)
    {
        var userId = OwnerOrNull();
        if (userId is null) return Unauthorized();
        if (IsServiceToken) return ServiceTokenRefusal();
        if (req is null) return BadRequest(new { code = CodeSectionKey, message = "тело запроса не задано" });
        var mine = req.Mode == "mine";
        if (req.Mode is not (null or "as-is" or "mine"))
            return BadRequest(new { code = CodeAdoptMode, message = "режим adopt: as-is или mine" });
        if (mine && !UnifiedOn(userId)) return UnifiedOffRefusal();

        var (key, refusal) = ResolveSection(userId, req.Section);
        if (refusal is not null) return refusal;
        if (string.IsNullOrWhiteSpace(req.Page) || WikiPaths.IsProjection(req.Page) || WikiSections.Parse(req.Page) != key)
            return BadRequest(new { code = CodePageKey, message = "страница должна принадлежать разделу" });

        if (!mine)
            return OpResult(await _keeper.AdoptAsync(userId, key!, req.Page));
        return MineResult(userId, await _apply.AdoptMineAsync(userId, key!, req.Page, req.BaseHash), req.Page);
    }

    // Гейт совместной правки: выключен любой из двух флагов — 409 (как в WikiPagesController).
    private bool UnifiedOn(string userId) =>
        _flags?.IsEnabled(userId, FeatureFlagKeys.LlmWiki) == true
        && _flags.IsEnabled(userId, FeatureFlagKeys.NotesWikiUnified);

    private IActionResult UnifiedOffRefusal() =>
        StatusCode(409, new { code = CodeUnifiedOff, message = "правка страниц выключена: функция недоступна" });

    private IActionResult MineResult(string userId, WikiApplyService.HumanEditResult res, string page) => res.Outcome switch
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
            BadRequest(new { code = CodeEditInvalid, message = res.Reason, violations = res.Violations,
                issues = WikiGateIssues.Classify(res.Violations, page, k => LiveNameOrNull(userId, k)) }),
        _ => StatusCode(409, new { code = "wiki_refused", message = res.Reason }),
    };

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
    /// своя: владельца проверяет CaptureNote (тот же путь, что у MCP). С LI-2 вика заявителя
    /// заводится до заявки (прежний 409 wiki_not_initialized этой ручки нет); лимит открытых
    /// пакетов по-прежнему проверяется ДО захвата (21-я заявка не ставится, ADR-035,
    /// решение 4). Отказ захвата — 404
    /// {code:"not_found"}: для вызывающего заметка не стала источником (чужой, нет такой,
    /// не источник), причина — в message. Исход заявки — 200 с outcome: её решает очередь
    /// разбора (WikiIngestService), не эта ручка; исключений наружу нет.</summary>
    [HttpPost("ingest/note")]
    public async Task<IActionResult> IngestNote([FromBody] WikiIngestNoteRequest? req)
    {
        var userId = OwnerOrNull();
        if (userId is null) return Unauthorized();
        if (IsServiceToken) return ServiceTokenRefusal();
        // «Перенести в вики» (B5) — не заявка на разбор, а правка человека: свой гейт флагов.
        if (req?.Transfer == true)
            return await TransferNote(userId, req);
        if (_flags?.IsEnabled(userId, FeatureFlagKeys.LlmWiki) != true)
            return StatusCode(409, new { code = CodeLlmWikiOff, message = "заявка не принята: функция выключена" });
        if (req is null || string.IsNullOrWhiteSpace(req.NoteId))
            return BadRequest(new { code = CodeNoteId, message = "нужен id заметки" });

        var root = WikiPaths.Root(_dataDir, userId);
        // LI-2: заявка владельца — действие человека, которому нужна вики: она заводится сама
        // вместо прежнего 409 wiki_not_initialized. Идемпотентно и под замком владельца;
        // проверка лимита открытых пакетов — после, как и прежде (21-я заявка не ставится).
        await _keeper.EnsureInitializedAsync(userId);
        if (WikiPackageStore.CountOpen(root) >= WikiPackage.MaxOpenPackages)
            return StatusCode(409, new { code = CodePackagesLimit,
                message = $"открытых пакетов уже {WikiPackage.MaxOpenPackages}: разберите Входящие, потом повторяй заявку." });

        var cap = await _capture.CaptureNote(userId, req.NoteId, req.Target);
        if (!cap.Ok)
            return NotFound(new { code = CodeNotFound, message = cap.Reason ?? "заметка не найдена" });

        var res = await WikiCardIngest.RunAsync(_ingest, cap, root, userId, HttpContext.RequestAborted);
        return Ok(new { outcome = res.Outcome, packageId = res.PackageId, reason = res.Reason });
    }

    // «Перенести в вики»: страница human в доме заметки + замена заметки ссылкой (WikiNoteTransfer).
    private async Task<IActionResult> TransferNote(string userId, WikiIngestNoteRequest req)
    {
        if (!UnifiedOn(userId)) return UnifiedOffRefusal();
        if (string.IsNullOrWhiteSpace(req.NoteId))
            return BadRequest(new { code = CodeNoteId, message = "нужен id заметки" });
        if (req.Target is not null)
            return BadRequest(new { code = CodeTransferTarget, message = "перенос идёт в раздел заметки: target не задаётся" });
        // LI-2: «перенести в вики» — действие человека: вика заводится сама вместо прежнего
        // 409 wiki_not_initialized. Оба флага (UnifiedOn) и валидация тела проверяются выше;
        // svc — в контроллере. Гейт IsInitialized в _transfer остаётся второй линией защиты.
        await _keeper.EnsureInitializedAsync(userId);

        var res = await _transfer.TransferAsync(userId, req.NoteId);
        return res.Outcome switch
        {
            WikiNoteTransfer.TransferOutcome.Transferred =>
                Ok(new { outcome = "transferred", section = res.Section, page = res.Page, packageId = res.PackageId }),
            WikiNoteTransfer.TransferOutcome.NotFound => NotFound(new { code = CodeNotFound, message = res.Reason }),
            WikiNoteTransfer.TransferOutcome.AlreadyTransferred =>
                StatusCode(409, new { code = CodeNoteTransferred, message = res.Reason }),
            WikiNoteTransfer.TransferOutcome.Exists =>
                StatusCode(409, new { code = CodePageChanged, message = res.Reason }),
            WikiNoteTransfer.TransferOutcome.Invalid =>
                BadRequest(new { code = CodeEditInvalid, message = res.Reason, violations = res.Violations,
                    issues = WikiGateIssues.Classify(res.Violations ?? [], res.Page ?? "", k => LiveNameOrNull(userId, k)) }),
            WikiNoteTransfer.TransferOutcome.NotTransferable =>
                BadRequest(new { code = CodeNoteId, message = res.Reason }),
            _ => StatusCode(409, new { code = "wiki_refused", message = res.Reason }),
        };
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

    /// <summary>GET /api/wiki/journal?section=&amp;origin=&amp;class=&amp;take=&amp;overrodeHuman= — журнал своей вики:
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
        [FromQuery] string? section, [FromQuery] string? origin, [FromQuery] string? @class, [FromQuery] int? take,
        [FromQuery] bool overrodeHuman = false)
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

        var res = _journal.Read(userId, new WikiJournalReader.Query(key, origin, @class, take, overrodeHuman));
        return Ok(new WikiJournalDto(
            [.. res.Entries.Select(e => new WikiJournalEntryDto(
                e.Ts, e.Op, e.Section, e.Actor, e.Source, e.Origin, e.SourceName, e.Pages, e.Packages,
                e.Status, e.Policy, e.Classes, e.OverrodeHuman, e.OverrodeKind, e.OverridePageKey, e.OverrideReverted))],
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
/// authorship — "ai" | "together" | "human" по сноскам действующих утверждений (у карточек
/// источников null); drift — файл страницы разошёлся с её записью в манифесте раздела (правка мимо Хранителя):
/// UI рисует плашку и «Принять как есть». Ложного дрейфа нет: раздел без манифеста или с
/// невалидным манифестом — drift=false, состояние видно в catalog.manifestState.</summary>
public sealed record WikiPageInfoDto(
    string PageKey, string Title, string? Summary, string? Type, string? Status, int SourcesCount, string? Updated,
    bool Drift, string? Authorship = null);

/// <summary>Сноска страницы в GET page: label — s1…sN, card — заголовок карточки, origin и
/// captured — из шапки карточки (null, когда карточки нет).</summary>
public sealed record WikiFootnoteDto(string Label, string Card, string? Origin, string? Captured, string? CardPage);

/// <summary>Что ИИ сам сделал с утверждением человека на странице за последние 14 дней
/// (GET page, overrides): op — supersede | conflict, statement — текст ИИ без сносок, previous —
/// что заменялось (строка «Истории»), target — для спора живая строка человека на странице (для замены null). Выводится из журнала и пакета, своего состояния не имеет.</summary>
public sealed record WikiOverrideDto(string Op, string? Statement, string? Previous, DateTimeOffset At, string PackageId, string? Target = null);

/// <summary>Каталог страниц раздела. manifestState — состояние манифеста раздела:
/// «ok» | «missing» | «invalid» (закрытый перечень; «missing» — норма до первого
/// применения). Дрейф страниц считается только при «ok».</summary>
public sealed record WikiCatalogDto(string Key, string? Name, bool Orphan, IReadOnlyList<WikiPageInfoDto> Pages, string ManifestState);

/// <summary>«Завести вики». Section — необязательный адресат кнопки (ключ раздела проекта);
/// по нему проверяется способность. Сам раздел не заводится — его заводит первая
/// применённая операция.</summary>
public sealed record WikiInitRequest(string? Section);

/// <summary>«Принять как есть» для страницы раздела.</summary>
public sealed record WikiAdoptRequest(string Section, string Page, string? Mode = null, string? BaseHash = null);

/// <summary>«Разобрать заметку в базу» (S7b0b). NoteId — id заметки: только своей, это
/// проверяет захват. Target — необязательный явный выбор «common» для источника вне
/// проекта (ADR-036, решение 3): его проверяет захват (WikiSourceHome), второй таблицы
/// допустимых target здесь нет.</summary>
public sealed record WikiIngestNoteRequest(string? NoteId, string? Target = null, bool Transfer = false);

/// <summary>Запись журнала (S7c-0): событие строки log.jsonl и то, что о нём сказал пакет.
/// ts/op/section/actor/source/pages — из строки журнала как они записаны; origin — префикс
/// источника (первый, если их несколько: строка несёт их списком, отдельного поля под множество
/// нет); sourceName — имя чата этого владельца или заголовок заметка, иначе null (чужой и
/// несуществующий чат — без имени, не отказ; имя — данные, без разметки, до 120 символов);
/// status/policy/classes — из склейки с пакетом: status null, когда пакетов несколько (их id в
/// packages), classes пуст, когда пакета нет (relink, adopt). policy «auto» | «inbox» — из
/// строки применения, иначе по пакету; packages — id, названные строкой retract. Формат добавляет
/// поля к тому, что фронт строит на GET /api/wiki/packages, и ничего не меняет (S7c-2).
/// overrodeHuman — ИИ сам заменил или оспорил утверждение человека (ADR-038, предохранитель 4);
/// overrodeKind — `replaced` (supersede@human) | `disputed` (conflict@human), null без признака
/// и у старых строк; при замене и оспаривании в одной строке — `replaced`;
/// overridePageKey — pageKey страницы первой автозамены/спора пакета (null, если не определить);
/// overrideReverted — все замены пакета уже возвращены более поздним `restore` (спор — всегда false).</summary>
public sealed record WikiJournalEntryDto(
    DateTimeOffset Ts, string Op, string Section, string Actor,
    string? Source, string? Origin, string? SourceName,
    IReadOnlyList<string> Pages, IReadOnlyList<string> Packages,
    string? Status, string? Policy, IReadOnlyList<string> Classes,
    bool OverrodeHuman = false, string? OverrodeKind = null,
    string? OverridePageKey = null, bool OverrideReverted = false);

/// <summary>Окно журнала. take — фактическое окно после обрезки (запрошенное выше потолка
/// урезается); linesRead — сколько строк журнала реально прочитано (мера того, что хвост
/// ограничен, а не весь файл); invalidLines — нечитаемые строки хвоста: их видно числом, они не
/// притворяются записями.</summary>
public sealed record WikiJournalDto(
    IReadOnlyList<WikiJournalEntryDto> Entries, int Take, int LinesRead, int InvalidLines);
