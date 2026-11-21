using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.ChildMode;
using ClaudeHomeServer.Services.Composition;
using ClaudeHomeServer.Services.Notes;
using ClaudeHomeServer.Services.Notes.Wiki;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ClaudeHomeServer.Notes.Controllers;

// Obsidian-совместимая база заметок, per-owner (изоляция как у задач — по claim sub).
//
// Контроллер живёт в вынесенной вертикали `ClaudeHomeServer.Notes` (Этап 5,
// волна 2). Это динамический модуль: Main не ссылается на сборку на компиляции
// (`ReferenceOutputAssembly="false"` в Main.csproj), и её часть в MVC не добавляется
// вовсе — ModuleLoader подключает ApplicationPart только загруженному модулю
// (Program.cs, реестр DynamicModules). Гейт `Subsystems:Notes:Enabled` закрыт —
// ModuleLoader не загружает dll и не регистрирует сервисы: MVC не находит action,
// маршруты `/api/notes/*` отдают общий 404 (из неполучастия части), никаких
// обращений к сервисам вертикали не происходит.
//
// Шов `INotesHubNotifier` (Core) заменил `IHubContext<SessionHub>` (Main):
// реализация в Main (`NotesHubNotifier` поверх `ISessionBroadcaster`)
// сама конструирует `NotesChangedMessage` и шлёт в группу владельца.
// Core-контракт намеренно не видит тип сообщения — обратной зависимости нет.
//
// Детский режим (Б20b, ADR-041 ред. 8, разрез §14.2): часть ручек размечена [ChildAllowed]
// и для ребёнка ведёт себя иначе — см. блок «Детский режим» ниже. Обе точки детской проверки
// — Core (`IChildModePolicy`, каталог проектов через `IProjectManager`/`ProjectCapabilityGuard`):
// ссылки вертикали Notes на Main нет и быть не должно.
[ApiController]
[Authorize]
[Route("api/notes")]
public class NotesController : ControllerBase
{
    private readonly NotesService _notes;
    private readonly NotesKnowledgeService _kb;
    private readonly NotesAiService _ai;
    private readonly NoteTaskSyncService _noteTasks;
    private readonly INotesHubNotifier _hubNotifier;
    private readonly string _dataDir;
    private readonly IChildModePolicy? _childPolicy;
    private readonly IProjectManager? _projects;

    public NotesController(NotesService notes, NotesKnowledgeService kb, NotesAiService ai,
        NoteTaskSyncService noteTasks, INotesHubNotifier hubNotifier, IConfiguration config,
        // Б20b: «ребёнок ли вызывающий» — только через единую точку политики; его проекты-
        // предметы — через Core-шов каталога проектов. Опциональны по той же причине, что
        // соответствующие параметры NotesService: бесхостовые тесты конструируют контроллер
        // напрямую. В боевой DI обе зарегистрированы (ядро берёт IChildModePolicy обязательным,
        // IProjectManager — ProjectManagerAdapter), поэтому «точки нет» — только тестовая
        // сборка: в ней ребёнок не опознаётся, и ручка отвечает как раньше.
        IChildModePolicy? childPolicy = null, IProjectManager? projects = null)
    {
        _notes = notes;
        _kb = kb;
        _ai = ai;
        _noteTasks = noteTasks;
        _hubNotifier = hubNotifier;
        _childPolicy = childPolicy;
        _projects = projects;
        // Каталог данных — как у всех сервисов и у WikiController (DataPath указывает на
        // projects.json в dataDir): нужен только корню вики для учёта чтений (R4).
        var dataPath = config["DataPath"] ?? Path.Combine(AppContext.BaseDirectory, "data", "projects.json");
        _dataDir = Path.GetDirectoryName(Path.GetFullPath(dataPath))!;
    }

    private string UserId => User.FindFirstValue(JwtRegisteredClaimNames.Sub)!;

    // Сервисный JWT хода (claim typ=svc, распознаётся как в WikiController): он не человек —
    // GET заметки отвечает под svc как раньше, но чтения страницы вики не считает (ADR-039, §7
    // «Не учитываются»). Локальные константы — тот же приём, что в WikiController/PluginsController.
    private const string TokenKindClaim = "typ";
    private const string ServiceTokenKind = "svc";
    private bool IsServiceToken => User.FindFirstValue(TokenKindClaim) == ServiceTokenKind;

    private Task Broadcast(string action, string? noteId = null, string? source = null)
    {
        // Любая мутация — отложенная синхронизация семантического индекса (дифф по хешам)
        _kb.QueueSync(UserId);
        // Шов `INotesHubNotifier` исторически принимает `string noteId` (не nullable);
        // события уровня папки (`folder_created`, `folder_deleted`, `updated` без
        // конкретной заметки) шлём с пустым id — клиент различает действия по полю
        // `Action`, а не по наличию `NoteId`.
        // S2c-4: источник передаём шву — проектная заметка уходит группе проекта
        // (участники получают «заметка изменилась») и моим вкладкам; личный vault
        // ("personal") — null, только владельцу, как раньше.
        var projectId = source is not null && source != "personal" ? source : null;
        return _hubNotifier.BroadcastNotesChangedAsync(UserId, action, noteId ?? string.Empty, projectId);
    }

    // Разбор id заметки (base64url(sourceKey|relPath), тот же EncodeId, что у NotesService):
    // источник нужен Delete (событие), путь — детской проверке пути (Get/Update/Delete), которая
    // судит путь ДО обращения к сервису. null, null — id не разобрался: для события это «только
    // владельцу, как раньше», для ребёнка — отказ (ChildGate не знает источника → 403).
    private static (string? Source, string? RelPath) IdParts(string id)
    {
        try
        {
            var b64 = id.Replace('-', '+').Replace('_', '/');
            switch (b64.Length % 4) { case 2: b64 += "=="; break; case 3: b64 += "="; break; }
            var raw = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(b64));
            var sep = raw.IndexOf('|');
            return sep < 0 ? (null, null) : (raw[..sep], raw[(sep + 1)..]);
        }
        catch (FormatException) { return (null, null); }
    }

    private static string? SourceOfId(string id) => IdParts(id).Source;

    // Единый ответ отказа «в вики пишет только Хранитель» (ADR-035, решение 2): 409 с кодом
    // wiki_readonly и текстом-подсказкой из исключения (он без абсолютных путей и ownerId).
    // Одно место на все мутирующие действия; в каждом из них catch (WikiReadOnlyException)
    // обязан стоять ПЕРЕД catch (InvalidOperationException) — WikiReadOnlyException наследует
    // её, и общий catch съедал бы код (ревью S1a).
    // ObjectResult (не IActionResult): действия объявлены ActionResult<…>, и к интерфейсу
    // неявного приведения нет — к производному от ActionResult типу есть.
    private ObjectResult WikiReadOnly(WikiReadOnlyException ex) =>
        new(new { code = WikiReadOnlyException.ErrorCode, message = ex.Message }) { StatusCode = 409 };

    // ─── Детский режим (Б20b, ADR-041 ред. 8, разрез §14.2) ───────────────────────
    //
    // Ребёнку из этих ручек открыт только notes/ его проектов-предметов: маркер
    // Project.ChildSubjectId (признак читается единой точкой ProjectCapabilities) плюс
    // per-owner — тот же вывод актёра, что в рельсе «Файлы» (ChildSubjectActor там же).
    // Личный vault ребёнка, чужой проект, источник вики и пустой source — 403 child_source,
    // а не 404: папка есть, она просто не этого ребёнка; «несуществующего» здесь нет.
    // Путь внутри разрешённого источника судит та же ChildPathPolicy, что у решения
    // разрешения инструмента хода и «Файлов»: Hidden → 404, Deny → 403 child_path.
    // Взрослый — обычной рельсой: свои проекты и права участника решает ProjectScope внутри
    // NotesService, здесь она не повторяется; куратор-опекун проекта-предмета — обычный
    // редактор его notes/ (снимков в корзину ему не делается: корзина — механизм ребёнка).
    private const string ChildSourceErrorCode = "child_source";
    private const string ChildPathErrorCode = "child_path";

    // Папка заметок проекта-предмета относительно ЕГО корня — тот же путь, что собирает
    // ResolveRoot (NotesService: Path.Combine(ServerRoot(project), "notes")). Имя нужно здесь
    // потому, что ChildPathPolicy судит путь от корня ПРЕДМЕТА, а заметки источника лежат на
    // уровень ниже — в notes/ проекта.
    private const string NotesDir = "notes";

    private sealed record ChildNotesScope(string Root);

    private static ObjectResult ChildRefuse(string code, string message) =>
        new(new { code, message }) { StatusCode = 403 };

    // Контекст ребёнка в ручке заметок: Scope — источник разрешён; Deny — отказ; ни того, ни
    // другого — вызывающий не ребёнок, обычная рельса (как до шага). Ребёнок опознаётся по
    // стору через единую точку политики (тот же приём, что у ChildChatsController): выключенный
    // профиль = обычный пользователь. DenyAll до этой ручки не доходит — сам гейт отказывает
    // его 403 на любом эндпоинте (ChildModeEndpointGuardTests.DenyAllProfile_Gets403).
    private (ChildNotesScope? Scope, ObjectResult? Deny) ChildGate(string? sourceKey)
    {
        if (_childPolicy?.For(UserId) is null) return (null, null);

        var project = string.IsNullOrEmpty(sourceKey) ? null : _projects?.GetById(sourceKey);
        // per-owner + маркер предмета: чужой проект-предмет, личный vault, вики и несуществующий
        // id источника — один отказ. Локальный проект (файлы на устройстве) здесь же: к его
        // notes/ сервер не обращается ни для кого (матрица, а не своя проверка локальности).
        if (project is null || project.OwnerId != UserId || !ProjectCapabilities.IsChildSubject(project)
            || ProjectCapabilityGuard.Refusal(project, ProjectCapabilityArea.FileBound) is not null
            || string.IsNullOrWhiteSpace(project.RootPath))
            return (null, ChildRefuse(ChildSourceErrorCode,
                "Заметки можно вести только в папке своего предмета."));

        return (new ChildNotesScope(project.RootPath), null);
    }

    // Путь заметки от корня предмета: relPath — путь внутри источника (его несёт id заметки и
    // поле Path ответа, forward slashes).
    private static string SubjectPath(string? relPath) =>
        string.IsNullOrEmpty(relPath) ? NotesDir : NotesDir + "/" + relPath.TrimStart('/');

    // Видимость записи списка для ребёнка: Hidden/Deny — в ответе нет ни строки, ни её
    // количества (тот же приём, что фильтр видимости в «Файлах»).
    private static bool ChildPathAllowed(ChildNotesScope scope, string? relPath, ChildPathOp op) =>
        ChildPathPolicy.Decide(ChildActor.Child, op, SubjectPath(relPath), scope.Root).Allowed;

    // Отказ по пути (И8) или null — можно продолжать. Тот же разбор, что у «Файлов»:
    // Hidden → 404 («этой заметки нет»: невидимость, а не запрет), Deny → 403 с причиной
    // политики.
    private static ObjectResult? ChildPathDenied(ChildNotesScope scope, string? relPath, ChildPathOp op)
    {
        var d = ChildPathPolicy.Decide(ChildActor.Child, op, SubjectPath(relPath), scope.Root);
        return d.Verdict switch
        {
            ChildPathVerdict.Allow => null,
            ChildPathVerdict.Hidden => new ObjectResult(new { error = "Этой заметки нет." }) { StatusCode = 404 },
            _ => ChildRefuse(ChildPathErrorCode, d.Reason ?? "Эта папка тебе не открыта."),
        };
    }

    // Имя файла заметки — её заголовок (Obsidian: файл = имя), санитайзер живёт в NotesService
    // и сохраняет ведущую «.» (срезает только хвостовые). ChildPathPolicy защищает скрытые имена
    // на любой глубине, поэтому заголовок с «.» в начале дал бы файл «.имя.md», которого ребёнок
    // не видит: отказывать надо до записи, а не после. Проверка — по ведущей «.»: это единственный
    // случай, который санитайзер доносит до защищённого имени (прочие защищённые имена при
    // добавлении «.md» перестают быть ими).
    private static ObjectResult? ChildHiddenTitle(string? title) =>
        title?.Trim() is { Length: > 0 } t && t.TrimEnd('.').Length > 0 && t[0] == '.'
            ? ChildRefuse(ChildPathErrorCode, "Скрытые файлы (с «.» в начале) тебе не открыты.")
            : null;

    // Снимок прежней версии перед записью ребёнка (конкурентность правки, И8/И11): то, что
    // ребёнок перезаписывает, не теряется — прежняя версия идёт в .ccs-trash/ предмета ДО
    // записи (тот же ChildTrash, что снимает версию перед правкой репетитора и удалением
    // ребёнка). Если клиент идёт по If-Match и его копия уже не актуальна, записи не будет
    // (409 ниже) — снимка не нужно: на диске ничего не перезаписалось. «*» (слабая форма,
    // «если существует») — запись будет при любом содержимом: снимок нужен.
    private bool ChildVersionIsOverwritten(ChildNotesScope scope, string? relPath, string ifMatch)
    {
        if (string.IsNullOrWhiteSpace(ifMatch)) return true;
        var token = ifMatch.Trim();
        if (token.StartsWith("W/", StringComparison.OrdinalIgnoreCase)) token = token[2..];
        token = token.Trim('"').Trim();
        if (token == "*") return true;
        var full = SafePath.Join(scope.Root, SubjectPath(relPath));
        // System.IO.File, не File: у ControllerBase есть своё File(...) — см. Attachment выше.
        if (!System.IO.File.Exists(full)) return false; // несуществующее не перезаписывается: 404 ниже
        return string.Equals(
            NotesService.ContentSha256(System.IO.File.ReadAllText(full, System.Text.Encoding.UTF8)),
            token, StringComparison.OrdinalIgnoreCase);
    }

    // Список заметок владельца (все источники). source — фильтр по источнику, q — поиск.
    // Ребёнку: source обязателен и это его проект-предмет (ChildGate), а из него убрано то,
    // что закрыто границей И8 (скрытые и защищённые папки — ни строки, ни количества).
    [HttpGet]
    [ChildAllowed]
    public ActionResult<IReadOnlyList<NoteSummary>> List(
        [FromQuery] string? source, [FromQuery] string? q)
    {
        var (child, denied) = ChildGate(source);
        if (denied is not null) return denied;
        var summaries = _notes.GetSummaries(UserId, source, q);
        return Ok(child is null ? summaries
            : summaries.Where(n => ChildPathAllowed(child, n.Path, ChildPathOp.List)).ToList());
    }

    // Источники для выбора «куда создать» (личный vault + проекты владельца).
    [HttpGet("sources")]
    public ActionResult<IReadOnlyList<NoteSourceDto>> Sources() =>
        Ok(_notes.GetSources(UserId));

    // Единый per-owner граф связей (узлы + рёбра, включая «призрачные» заметки).
    // annotations=true — включить комментарии к документам (узлы + связи привязки).
    [HttpGet("graph")]
    public ActionResult<NoteGraph> Graph([FromQuery] bool annotations = false) =>
        Ok(_notes.GetGraph(UserId, annotations));

    // Резолв заметки по имени вики-ссылки (+ фрагмент по якорю #Заголовок / #^блок) —
    // для hover-preview и embed-вставок ![[…]].
    [HttpGet("resolve")]
    public ActionResult Resolve([FromQuery] string name, [FromQuery] string? anchor)
    {
        if (string.IsNullOrWhiteSpace(name)) return BadRequest("Не задано имя");
        var r = _notes.ResolveByName(UserId, name, anchor);
        return r is null ? NotFound() : Ok(new { note = r.Value.Note, fragment = r.Value.Fragment });
    }

    // Вложение из vault (картинка для ![[img.png]]): JWT принимается и в query
    // access_token (браузерный <img> не шлёт Authorization-заголовок).
    [HttpGet("attachment")]
    public IActionResult Attachment([FromQuery] string source, [FromQuery] string path)
    {
        if (string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(path))
            return BadRequest();
        try
        {
            var full = _notes.ResolveAttachmentPath(UserId, source, path);
            if (!System.IO.File.Exists(full)) return NotFound();
            var provider = new Microsoft.AspNetCore.StaticFiles.FileExtensionContentTypeProvider();
            if (!provider.TryGetContentType(full, out var contentType))
                contentType = "application/octet-stream";
            return PhysicalFile(full, contentType);
        }
        catch (UnauthorizedAccessException) { return Forbid(); }
        catch (KeyNotFoundException) { return NotFound(); }
    }

    // Возможности раздела (semantic — настроен ли Dify для поиска «по смыслу»)
    [HttpGet("caps")]
    public ActionResult Caps() => Ok(new { semantic = _kb.Available });

    // --- Комментарии к документам (флаг doc-annotations) ---

    // Создать комментарий к выделению в MD-документе. Verify-before-write:
    // текст сверяется посимвольно, расхождение → 409 «выделите заново».
    [HttpPost("annotate")]
    public async Task<ActionResult<NoteDetail>> Annotate([FromBody] AnnotateRequest req)
    {
        try
        {
            var note = _notes.Annotate(UserId, req, byHuman: true);
            await Broadcast("created", note.Id, note.Source);
            return Ok(note);
        }
        catch (WikiReadOnlyException ex) { return WikiReadOnly(ex); }
        catch (AnnotationConflictException ex) { return Conflict(new { error = ex.Message }); }
        catch (ArgumentException ex) { return BadRequest(new { error = ex.Message }); }
        catch (KeyNotFoundException) { return NotFound(); }
        catch (UnauthorizedAccessException) { return Forbid(); }
    }

    // Комментарии документа с резолвом привязки (для подсветки блоков при чтении).
    // scope: personal | projectId; path — путь документа внутри области.
    [HttpGet("annotations")]
    public ActionResult<IReadOnlyList<DocAnnotationDto>> Annotations(
        [FromQuery] string scope, [FromQuery] string path)
    {
        if (string.IsNullOrWhiteSpace(scope) || string.IsNullOrWhiteSpace(path))
            return BadRequest();
        try { return Ok(_notes.GetDocAnnotations(UserId, scope, path)); }
        catch (KeyNotFoundException) { return NotFound(); }
        catch (UnauthorizedAccessException) { return Forbid(); }
    }

    // Перепривязка комментария к новому выделению (место изменилось/сирота при живом
    // документе); тот же verify-guard, что при создании — 409 при гонке с правкой
    [HttpPost("{id}/repin")]
    public async Task<ActionResult<NoteDetail>> Repin(string id, [FromBody] AnnotateSelection selection)
    {
        try
        {
            var note = _notes.RepinAnnotation(UserId, id, selection, byHuman: true);
            await Broadcast("updated", id, note.Source);
            return Ok(note);
        }
        catch (WikiReadOnlyException ex) { return WikiReadOnly(ex); }
        catch (AnnotationConflictException ex) { return Conflict(new { error = ex.Message }); }
        catch (ArgumentException ex) { return BadRequest(new { error = ex.Message }); }
        catch (InvalidOperationException ex) { return BadRequest(new { error = ex.Message }); }
        catch (KeyNotFoundException) { return NotFound(); }
        catch (UnauthorizedAccessException) { return Forbid(); }
    }

    // Ответ в треде комментария (реплика = заметка с annotates на корневую; тред плоский)
    [HttpPost("{id}/reply")]
    public async Task<ActionResult<NoteDetail>> Reply(string id, [FromBody] ReplyRequest req)
    {
        try
        {
            var note = _notes.Reply(UserId, id, req, byHuman: true);
            await Broadcast("created", note.Id, note.Source);
            return Ok(note);
        }
        catch (WikiReadOnlyException ex) { return WikiReadOnly(ex); }
        catch (ArgumentException ex) { return BadRequest(new { error = ex.Message }); }
        catch (InvalidOperationException ex) { return BadRequest(new { error = ex.Message }); }
        catch (KeyNotFoundException) { return NotFound(); }
        catch (UnauthorizedAccessException) { return Forbid(); }
    }

    // Ответы треда комментария (по времени создания)
    [HttpGet("{id}/replies")]
    public ActionResult<IReadOnlyList<NoteReplyDto>> Replies(string id)
    {
        try { return Ok(_notes.GetReplies(UserId, id)); }
        catch (KeyNotFoundException) { return NotFound(); }
        catch (UnauthorizedAccessException) { return Forbid(); }
    }

    // Смена статуса комментария (open | resolved)
    [HttpPost("{id}/status")]
    public async Task<ActionResult<NoteDetail>> SetStatus(string id, [FromBody] SetNoteStatusRequest req)
    {
        try
        {
            var note = _notes.SetAnnotationStatus(UserId, id, req.Status, byHuman: true);
            if (note is null) return NotFound();
            await Broadcast("updated", id, note.Source);
            return Ok(note);
        }
        catch (WikiReadOnlyException ex) { return WikiReadOnly(ex); }
        catch (ArgumentException ex) { return BadRequest(new { error = ex.Message }); }
        catch (InvalidOperationException ex) { return BadRequest(new { error = ex.Message }); }
        // «Источник не мой/не существует» = «несуществующее»: 404, как в Create
        catch (KeyNotFoundException) { return NotFound(); }
        catch (UnauthorizedAccessException) { return Forbid(); }
    }

    // Семантический поиск по заметкам (Dify retrieve). Пустой список — индекс пуст/выключен.
    [HttpGet("semantic")]
    public async Task<ActionResult> Semantic([FromQuery] string q, [FromQuery] int topK = 8)
    {
        if (string.IsNullOrWhiteSpace(q)) return BadRequest("Пустой запрос");
        if (!_kb.Available) return Ok(new { available = false, results = Array.Empty<NoteSemanticHit>() });
        try
        {
            var results = await _kb.SearchAsync(UserId, q, Math.Clamp(topK, 1, 20));
            return Ok(new { available = true, results });
        }
        catch (HttpRequestException ex)
        {
            return StatusCode(502, new { error = $"Dify недоступен: {ex.Message}" });
        }
    }

    // Полная переиндексация заметок в семантический индекс
    [HttpPost("reindex")]
    public async Task<ActionResult> Reindex()
    {
        if (!_kb.Available) return BadRequest("Dify не настроен");
        var changed = await _kb.SyncAllAsync(UserId);
        return Ok(new { changed });
    }

    // Шаблоны заметок (файлы templates/ личного vault).
    [HttpGet("templates")]
    public ActionResult<IReadOnlyList<NoteTemplateDto>> Templates() =>
        Ok(_notes.GetTemplates(UserId));

    // Дневниковая заметка (get-or-create Journal/{date}.md в личном vault).
    [HttpPost("daily")]
    public async Task<ActionResult<NoteDetail>> Daily([FromBody] DailyNoteRequest req)
    {
        var note = _notes.GetOrCreateDaily(UserId, req.Date);
        await Broadcast("updated", note.Id, note.Source);
        return Ok(note);
    }

    // ✨ Предложить связи с другими заметками (one-shot AI)
    [HttpPost("{id}/suggest-links")]
    public async Task<ActionResult> SuggestLinks(string id, CancellationToken ct)
    {
        try { return Ok(await _ai.SuggestLinksAsync(UserId, id, ct)); }
        catch (KeyNotFoundException) { return NotFound(); }
        catch (InvalidOperationException ex) { return StatusCode(502, new { error = ex.Message }); }
    }

    // ✨ Предложить теги (one-shot AI)
    [HttpPost("{id}/suggest-tags")]
    public async Task<ActionResult> SuggestTags(string id, CancellationToken ct)
    {
        try { return Ok(await _ai.SuggestTagsAsync(UserId, id, ct)); }
        catch (KeyNotFoundException) { return NotFound(); }
        catch (InvalidOperationException ex) { return StatusCode(502, new { error = ex.Message }); }
    }

    // ✨ Предложить заголовок по содержимому (one-shot AI, локальная модель если настроена)
    [HttpPost("{id}/suggest-title")]
    public async Task<ActionResult> SuggestTitle(string id, CancellationToken ct)
    {
        try { return Ok(new { title = await _ai.SuggestTitleAsync(UserId, id, ct) }); }
        catch (KeyNotFoundException) { return NotFound(); }
        catch (InvalidOperationException ex) { return StatusCode(502, new { error = ex.Message }); }
    }

    // ✨ Оглавление — секция «## Оглавление» в начале заметки (one-shot AI)
    [HttpPost("{id}/toc")]
    public async Task<ActionResult<NoteDetail>> Toc(string id, CancellationToken ct)
    {
        try
        {
            var note = await _ai.SuggestTocAsync(UserId, id, ct);
            await Broadcast("updated", note.Id, note.Source);
            return Ok(note);
        }
        catch (WikiReadOnlyException ex) { return WikiReadOnly(ex); }
        catch (KeyNotFoundException) { return NotFound(); }
        catch (UnauthorizedAccessException) { return Forbid(); }
        catch (InvalidOperationException ex) { return StatusCode(502, new { error = ex.Message }); }
    }

    // ✨ Перевод — секция «## Перевод» в конце заметки (one-shot AI)
    [HttpPost("{id}/translate")]
    public async Task<ActionResult<NoteDetail>> Translate(string id, CancellationToken ct)
    {
        try
        {
            var note = await _ai.TranslateAsync(UserId, id, ct);
            await Broadcast("updated", note.Id, note.Source);
            return Ok(note);
        }
        catch (WikiReadOnlyException ex) { return WikiReadOnly(ex); }
        catch (KeyNotFoundException) { return NotFound(); }
        catch (UnauthorizedAccessException) { return Forbid(); }
        catch (InvalidOperationException ex) { return StatusCode(502, new { error = ex.Message }); }
    }

    // ✨ Конспект дня — секция «Итоги дня» в daily note (one-shot AI)
    [HttpPost("daily/summary")]
    public async Task<ActionResult<NoteDetail>> DailySummary([FromBody] DailyNoteRequest req, CancellationToken ct)
    {
        try
        {
            var note = await _ai.DailySummaryAsync(UserId, req.Date, ct);
            await Broadcast("updated", note.Id, note.Source);
            return Ok(note);
        }
        catch (InvalidOperationException ex) { return StatusCode(502, new { error = ex.Message }); }
    }

    // --- Задачи из заметок (флаг notes-task-sync) ---

    // Чекбоксы заметки + связанные задачи (панель «Задачи из заметки»).
    [HttpGet("{id}/tasks")]
    public ActionResult<IReadOnlyList<NoteTaskDto>> NoteTasks(string id)
    {
        try { return Ok(_noteTasks.ListForNote(UserId, id)); }
        catch (KeyNotFoundException) { return NotFound(); }
    }

    // Промоут чекбокса в настоящую задачу (появляется в календаре, работают напоминания).
    [HttpPost("{id}/tasks/promote")]
    public async Task<ActionResult> PromoteTask(string id, [FromBody] PromoteTaskRequest req)
    {
        try { return Ok(await _noteTasks.PromoteAsync(UserId, id, req.Line, createdByHuman: true)); }
        catch (KeyNotFoundException) { return NotFound(); }
        catch (UnauthorizedAccessException) { return Forbid(); }
        catch (InvalidOperationException ex) { return BadRequest(new { error = ex.Message }); }
    }

    // Тоггл чекбокса из заметки: правит .md + синхронизирует связанную задачу.
    [HttpPost("{id}/tasks/toggle")]
    public async Task<ActionResult<NoteDetail>> ToggleTask(string id, [FromBody] ToggleTaskRequest req)
    {
        try { return Ok(await _noteTasks.ToggleAsync(UserId, id, req.Line, req.Done, byHuman: true)); }
        catch (WikiReadOnlyException ex) { return WikiReadOnly(ex); }
        catch (KeyNotFoundException) { return NotFound(); }
        catch (UnauthorizedAccessException) { return Forbid(); }
        catch (InvalidOperationException ex) { return BadRequest(new { error = ex.Message }); }
    }

    // Установить/убрать срок 📅 на строке-чекбоксе (дейт-пикер в секции — без ввода эмодзи).
    [HttpPost("{id}/tasks/set-due")]
    public async Task<ActionResult<NoteDetail>> SetTaskDue(string id, [FromBody] SetDueRequest req)
    {
        var due = string.IsNullOrWhiteSpace(req.Due) ? null : req.Due.Trim();
        if (due is not null && !DateOnly.TryParseExact(due, "yyyy-MM-dd", out _))
            return BadRequest(new { error = "Некорректная дата (нужен формат YYYY-MM-DD)" });
        try { return Ok(await _noteTasks.SetDueAsync(UserId, id, req.Line, due, byHuman: true)); }
        catch (WikiReadOnlyException ex) { return WikiReadOnly(ex); }
        catch (KeyNotFoundException) { return NotFound(); }
        catch (UnauthorizedAccessException) { return Forbid(); }
        catch (InvalidOperationException ex) { return BadRequest(new { error = ex.Message }); }
    }

    // Переименование/перенос папки целиком: newPath — полный новый путь папки.
    [HttpPost("folder/move")]
    public async Task<ActionResult> MoveFolder([FromBody] MoveFolderRequest req)
    {
        try
        {
            var moved = _notes.MoveFolder(UserId, req.Source, req.Path, req.NewPath, byHuman: true);
            await Broadcast("updated", null, req.Source);
            return Ok(new { notes = moved });
        }
        catch (WikiReadOnlyException ex) { return WikiReadOnly(ex); }
        catch (UnauthorizedAccessException) { return Forbid(); }
        catch (KeyNotFoundException) { return NotFound(); }
        catch (ArgumentException ex) { return BadRequest(ex.Message); }
        catch (InvalidOperationException ex) { return Conflict(new { error = ex.Message }); }
    }

    // Все физические папки владельца (в т.ч. пустые) — для дерева и «куда создать».
    [HttpGet("folders")]
    public ActionResult<IReadOnlyList<NoteFolderDto>> Folders() =>
        Ok(_notes.GetFolders(UserId));

    // Создать физическую папку (в т.ч. пустую).
    [HttpPost("folder")]
    public async Task<ActionResult<NoteFolderDto>> CreateFolder([FromBody] CreateNoteFolderRequest req)
    {
        try
        {
            var folder = _notes.CreateFolder(UserId, req.Source, req.Path);
            await Broadcast("folder_created", null, req.Source);
            return Ok(folder);
        }
        catch (WikiReadOnlyException ex) { return WikiReadOnly(ex); }
        catch (UnauthorizedAccessException) { return Forbid(); }
        catch (KeyNotFoundException) { return NotFound(); }
        catch (ArgumentException ex) { return BadRequest(ex.Message); }
        catch (InvalidOperationException ex) { return Conflict(new { error = ex.Message }); }
    }

    // Удалить физическую папку рекурсивно (пустую или с заметками).
    [HttpDelete("folder")]
    public async Task<IActionResult> DeleteFolder([FromQuery] string source, [FromQuery] string path)
    {
        try
        {
            var removed = _notes.DeleteFolder(UserId, source, path, byHuman: true);
            await Broadcast("folder_deleted", null, source);
            return Ok(new { removed });
        }
        catch (WikiReadOnlyException ex) { return WikiReadOnly(ex); }
        catch (UnauthorizedAccessException) { return Forbid(); }
        catch (KeyNotFoundException) { return NotFound(); }
        catch (ArgumentException ex) { return BadRequest(ex.Message); }
    }

    // Перенос заметки в другую папку того же источника (id меняется — путь в id).
    [HttpPost("{id}/move")]
    public async Task<ActionResult<NoteDetail>> Move(string id, [FromBody] MoveNoteRequest req)
    {
        try
        {
            var note = _notes.Move(UserId, id, req.Folder, req.TargetSource, byHuman: true);
            if (note is null) return NotFound();
            await Broadcast("updated", note.Id, note.Source);
            return Ok(note);
        }
        catch (WikiReadOnlyException ex) { return WikiReadOnly(ex); }
        catch (UnauthorizedAccessException) { return Forbid(); }
        // «Источник не мой/не существует» = «несуществующее»: 404, как в Create
        catch (KeyNotFoundException) { return NotFound(); }
        catch (ArgumentException) { return BadRequest("Некорректный id заметки"); }
        catch (InvalidOperationException ex) { return Conflict(new { error = ex.Message }); }
    }

    // «Связать» несвязанное упоминание: первое вхождение заголовка → [[…]].
    [HttpPost("{id}/link-mention")]
    public async Task<ActionResult<NoteDetail>> LinkMention(string id, [FromBody] LinkMentionRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.TargetTitle))
            return BadRequest("Не задан заголовок цели");
        try
        {
            var note = _notes.LinkMention(UserId, id, req.TargetTitle, byHuman: true);
            if (note is null) return NotFound();
            await Broadcast("updated", note.Id, note.Source);
            return Ok(note);
        }
        catch (WikiReadOnlyException ex) { return WikiReadOnly(ex); }
        catch (UnauthorizedAccessException) { return Forbid(); }
        // «Источник не мой/не существует» = «несуществующее»: 404, как в Create
        catch (KeyNotFoundException) { return NotFound(); }
        catch (ArgumentException) { return BadRequest("Некорректный id заметки"); }
    }

    // GET отдаёт заметку и заголовок ETag — хеш её содержимого (К8): по нему следующий PUT
    // проверяет, что копия клиента ещё актуальна.
    [HttpGet("{id}")]
    [ChildAllowed]
    public ActionResult<NoteDetail> Get(string id)
    {
        var (sourceKey, relPath) = IdParts(id);
        var (child, denied) = ChildGate(sourceKey);
        if (denied is not null) return denied;
        if (child is not null && ChildPathDenied(child, relPath, ChildPathOp.Read) is { } pathDenied)
            return pathDenied;
        var note = _notes.GetDetail(UserId, id);
        if (note is null) return NotFound();
        // R4 (ADR-039, §7): открытие страницы вики в «Заметках» — чтение Human; обычная заметка
        // — не страница вики, svc — не человек: ни то, ни другое не считается. Сбой учёта
        // (false стора) не меняет ответ. Ключ — note.Path: для источника wiki это путь под
        // pages/ (тот же PageKey, что у GET /api/wiki/page).
        if (note.Source == NotesService.WikiKey && !IsServiceToken)
            WikiReadsStore.RecordMany(WikiPaths.Root(_dataDir, UserId),
                [(note.Path, WikiReadKind.Human)], DateTimeOffset.UtcNow);
        Response.Headers.ETag = $"\"{NotesService.ContentSha256(note.Content)}\"";
        return Ok(note);
    }

    [HttpGet("{id}/backlinks")]
    public ActionResult<IReadOnlyList<NoteBacklinkDto>> Backlinks(string id) =>
        Ok(_notes.GetBacklinks(UserId, id));

    [HttpPost]
    [ChildAllowed]
    public async Task<ActionResult<NoteDetail>> Create([FromBody] CreateNoteRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Title))
            return BadRequest("Не задан заголовок заметки");
        var (child, denied) = ChildGate(req.Source);
        if (denied is not null) return denied;
        if (child is not null)
        {
            // Папка внутри notes/ предмета — по той же границе И8. Имя файла — заголовок,
            // его санитайзер (NotesService) к этой проверке добавляет только суффикс коллизии
            // «-2» (он не делает имя защищённым) и не сносит ведущую «.» — она ниже.
            if (ChildPathDenied(child, NotesService.SanitizeFolder(req.Folder), ChildPathOp.Write) is { } pathDenied)
                return pathDenied;
            if (ChildHiddenTitle(req.Title) is { } hiddenTitle) return hiddenTitle;
        }
        try
        {
            // S3-3c1: это правка человека (REST-ручка) — в журнал правок общего проекта (S3-3)
            var note = _notes.Create(UserId, req, byHuman: true);
            await Broadcast("created", note.Id, note.Source);
            return Ok(note);
        }
        catch (WikiReadOnlyException ex) { return WikiReadOnly(ex); }
        catch (UnauthorizedAccessException) { return Forbid(); }
        catch (KeyNotFoundException) { return NotFound(); }
    }

    // PUT с If-Match (К8): сохранение только поверх актуальной копии. Совпадает — запись;
    // не совпадает — 409 note_changed с актуальным состоянием, заметка не тронута.
    // Без If-Match — как раньше.
    [HttpPut("{id}")]
    [ChildAllowed]
    public async Task<ActionResult<NoteDetail>> Update(string id, [FromBody] UpdateNoteRequest req)
    {
        var ifMatch = Request.Headers.IfMatch.ToString();
        var (sourceKey, relPath) = IdParts(id);
        var (child, denied) = ChildGate(sourceKey);
        if (denied is not null) return denied;
        if (child is not null)
        {
            if (ChildPathDenied(child, relPath, ChildPathOp.Write) is { } pathDenied) return pathDenied;
            // Переименование файла при смене заголовка (Obsidian) — та же грань имени, что при
            // создании: скрытого имени ребёнок не заводит.
            if (ChildHiddenTitle(req.Title) is { } hiddenTitle) return hiddenTitle;
            if (ChildVersionIsOverwritten(child, relPath, ifMatch))
                ChildTrash.Snapshot(child.Root, SubjectPath(relPath), UserId);
        }
        try
        {
            // S3-3c1: правка человека — в журнал правок (S3-3); при 409/403/404 ниже — отказа
            // NoteConflictException/Unauth до записи, и журнал не вызывается вовсе.
            var note = _notes.Update(UserId, id, req, string.IsNullOrWhiteSpace(ifMatch) ? null : ifMatch, byHuman: true);
            if (note is null) return NotFound();
            // Новое состояние — новый ETag: следующая правка этого клиента идёт по нему
            Response.Headers.ETag = $"\"{NotesService.ContentSha256(note.Content)}\"";
            await Broadcast("updated", note.Id, note.Source);
            return Ok(note);
        }
        catch (NoteConflictException ex)
        {
            // Тело по К8: актуальный хеш и содержимое, время правки (состояние сервера).
            // author — из серверного состояния: его источник (буфер коммиттера) появляется в
            // S3-3; здесь сервер автора победившей правки не знает — null (авторство из тела
            // запроса не принимается: вызывающий мог проиграть гонку, и показать его автором
            // чужой правки — ложь). За REST-сохранением нет чата — source null.
            return Conflict(new
            {
                code = "note_changed",
                content = ex.Content,
                sha256 = ex.Sha256,
                author = (object?)null,
                changedAt = ex.ChangedAt,
                source = (string?)null,
            });
        }
        catch (WikiReadOnlyException ex) { return WikiReadOnly(ex); }
        catch (UnauthorizedAccessException) { return Forbid(); }
        // «Источник не мой/не существует» = «несуществующее»: 404, как в Create
        catch (KeyNotFoundException) { return NotFound(); }
        catch (ArgumentException) { return BadRequest("Некорректный id заметки"); }
    }

    [HttpDelete("{id}")]
    [ChildAllowed]
    public async Task<IActionResult> Delete(string id)
    {
        var (sourceKey, relPath) = IdParts(id);
        var (child, denied) = ChildGate(sourceKey);
        if (denied is not null) return denied;
        if (child is not null)
        {
            if (ChildPathDenied(child, relPath, ChildPathOp.Delete) is { } pathDenied) return pathDenied;
            // Удаление ребёнка обратимо (решение 54): прежняя версия — в .ccs-trash/ ДО удаления,
            // тот же порядок, что в рельсе «Файлы». Снимок не отменяет удаления и не откладывается
            // после него: иначе между ними в файл мог бы вернуться другой. Несуществующего файла
            // нет в корзине (контракт Snapshot — null), и удаление остаётся 404, как раньше.
            ChildTrash.Snapshot(child.Root, SubjectPath(relPath), UserId);
        }
        try
        {
            // S3-3c1: удаление человеком — правка пути в журнал (S3-3); 404 (файла нет) — отказа нет
            if (!_notes.Delete(UserId, id, byHuman: true)) return NotFound();
            await Broadcast("deleted", id, SourceOfId(id));
            return NoContent();
        }
        catch (WikiReadOnlyException ex) { return WikiReadOnly(ex); }
        catch (UnauthorizedAccessException) { return Forbid(); }
        // «Источник не мой/не существует» = «несуществующее»: 404, как в Create
        catch (KeyNotFoundException) { return NotFound(); }
        catch (ArgumentException) { return BadRequest("Некорректный id заметки"); }
    }
}
