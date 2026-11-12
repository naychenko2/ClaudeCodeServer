using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Notes;
using ClaudeHomeServer.Services.Notes.Wiki;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ClaudeHomeServer.Notes.Controllers;

// «Входящие» вики: список пакетов изменений и решение человека по их спорным операциям
// (S4a, ADR-035, решения 4 и 12г). Образец — WikiController (S2f): [Authorize], владелец
// только из токена, чужой раздел — 403, коды отказов — контракт для фронта (S7a).
// Сервисный токен хода (typ=svc) — все мутирующие ручки отказаны 403 (B2a): решение по
// пакету и откат — только действие человека (решение 5); ручки чтения отвечают под svc как раньше.
//
// Здесь человек решает только то, что ушло во «Входящие»: операция с решением политики
// `auto` через REST не решается (409). Пакеты здесь читаются, решаются и откатываются (S4b-3):
// их никто не создаёт (их строит Хранитель). Решение человека замкнуто применением (S4g,
// ADR-035 §5): тот же запрос, который оставил в пакете ноль нерешённых спорных операций и
// хотя бы одну принятую, применяет его через WikiApplyService и отвечает итоговое состояние
// пакета; вердикт облачного судьи — решение той же операции (И1, J-J2b): оно применяется тем
// же путём и с теми же гейтами кода, а человек его вторым решением не переворачивает (409 с
// его путём — откатом пакета); решение, оставившее нерешённые спорные (proposed) или
// отклонившее все (rejected), ничего не применяет. Отдельной ручки «применить» нет (повтор применения человеком — не здесь);
// откат применённого пакета — только кнопкой человека (мера 4), MCP-инструмента нет (решение 5).
//
// Пакеты лежат в data/wiki/{владельца}/keeper/packages/, поэтому чужой пакет в свой список
// не попадает по построению; остаётся вторая граница — раздел пакета, называющий чужой
// проект (тот же ResolveSection, что у WikiController): он не показывается и не решается.
[ApiController]
[Authorize]
[Route("api/wiki/packages")]
public class WikiPackagesController : ControllerBase
{
    private readonly IProjectManager _projects;
    private readonly WikiApplyService _apply;
    private readonly WikiPackageRevert _revert;
    private readonly WikiRetractService _retract;
    private readonly ISessionDirectory _sessions;
    private readonly WikiChatIngestService _chat;
    private readonly string _dataDir;

    public WikiPackagesController(IProjectManager projects, IConfiguration config, WikiApplyService apply,
        WikiPackageRevert revert, WikiRetractService retract, ISessionDirectory sessions, WikiChatIngestService chat)
    {
        _projects = projects;
        _apply = apply;
        _revert = revert;
        _retract = retract;
        _sessions = sessions;
        _chat = chat;
        // Каталог данных — как у всех сервисов (DataPath указывает на projects.json в dataDir).
        var dataPath = config["DataPath"] ?? Path.Combine(AppContext.BaseDirectory, "data", "projects.json");
        _dataDir = Path.GetDirectoryName(Path.GetFullPath(dataPath))!;
    }

    // Владелец — только из токена (claim sub). Явная проверка — копия гейта внутри действия:
    // без токена действие отвечает 401 (контракт), а не падает с NRE на claim.
    private string? OwnerOrNull() =>
        User.Identity?.IsAuthenticated == true ? User.FindFirstValue(JwtRegisteredClaimNames.Sub) : null;

    // Сервисный JWT хода (claim typ=svc, тот же смысл, что JwtService): «Входящие» — дверь
    // человека, svc не решает пакеты и не откатывает их (403, B2a; решение 5: у отката нет
    // агентского инструмента). Локальные константы — как в PluginsController/TasksController.
    private const string TokenKindClaim = "typ";
    private const string ServiceTokenKind = "svc";
    private bool IsServiceToken => User.FindFirstValue(TokenKindClaim) == ServiceTokenKind;
    private IActionResult ServiceTokenRefusal() =>
        StatusCode(403, new { error = "Сервисный токен: «Входящие» решает только пользовательский токен" });

    private const string CodeForeign = "wiki_foreign";
    private const string CodeRequest = "wiki_pkg_request";
    // Чат не найден или чужой — один ответ: по нему нельзя отличить «нет» от «не твой»
    // (отказ без утечки существования, мера 5).
    private const string CodeSource = "wiki_retract_source";

    /// <summary>GET /api/wiki/packages?inbox=true — список пакетов своей вики.
    /// inbox=true — только «Входящие»: ждущие решения человека (status proposed);
    /// без него — все (и журнал состояний: applied, rejected, stale). Нечитаемые файлы
    /// отдаются числом: их видно, но они не притворяются пакетами.</summary>
    [HttpGet]
    public IActionResult List([FromQuery] bool? inbox)
    {
        var userId = OwnerOrNull();
        if (userId is null) return Unauthorized();

        var list = WikiPackageStore.List(WikiPaths.Root(_dataDir, userId));
        var mine = list.Packages.Where(p => !IsForeignSection(userId, p.Section));
        var shown = inbox == true ? mine.Where(p => p.Status == WikiPackage.StatusProposed) : mine;
        return Ok(new WikiPackagesDto(
            [.. shown.OrderByDescending(p => p.CreatedAt).ThenBy(p => p.Id, StringComparer.Ordinal).Select(Dto)],
            list.InvalidFiles.Count));
    }

    /// <summary>GET /api/wiki/packages/{id} — карточка пакета с операциями и их решениями.</summary>
    [HttpGet("{id}")]
    public IActionResult Get(string id)
    {
        var userId = OwnerOrNull();
        if (userId is null) return Unauthorized();
        var res = WikiPackageStore.TryRead(WikiPaths.Root(_dataDir, userId), id);
        // «нет» и «нечитаем» — разные исходы для фронта: 404 и 409, как в Owned.
        if (res.State == WikiPackageFileState.Missing) return NotFound();
        if (res.State != WikiPackageFileState.Valid || res.Package is null)
            return StatusCode(409, new { code = WikiPackageStore.CodeInvalid, message = "файл пакета не читается" });
        if (IsForeignSection(userId, res.Package.Section))
            return StatusCode(403, new { code = CodeForeign, message = "только своя вики" });
        return Ok(Dto(res.Package));
    }

    /// <summary>POST /api/wiki/packages/{id}/decide — {index, accept, reason?}: принять или
    /// отклонить с причиной одну спорную операцию. 409: операция не спорная (auto), пакет не
    /// в proposed, index вне списка, отказ без причины, файл пакета нечитаем, операция уже
    /// решена — человеком или судьёй (решение не меняется; по вердикту судья в теле — путь
    /// человека: откат пакета). Успех — {package, apply}: карточка пакета после решения
    /// и, если решение оставило ноль нерешённых спорных и хотя бы одну принятую операцию,
    /// исход применения этого же пакета; для proposed/rejected apply — null.</summary>
    [HttpPost("{id}/decide")]
    public async Task<IActionResult> Decide(string id, [FromBody] WikiDecideRequest? req)
    {
        var userId = OwnerOrNull();
        if (userId is null) return Unauthorized();
        if (IsServiceToken) return ServiceTokenRefusal();
        if (req?.Index is null || req.Accept is null)
            return BadRequest(new { code = CodeRequest, message = "нужны index операции и accept" });
        if (Owned(userId, id, out var root, out var before) is { } refusal) return refusal;
        var res = WikiPackageStore.DecideOperation(root, id, req.Index.Value, req.Accept.Value, req.Reason);
        if (!res.Ok) return Refusal(res, before, req.Index);
        return await AfterDecision(userId, root, res.Package!);
    }

    /// <summary>POST /api/wiki/packages/{id}/accept-all — «принять всё спорное пакета».
    /// 409, если спорных операций нет: неспорные решает политика, а не человек. Спорные,
    /// решённые судьёй, пропускаются: их вердикт остаётся как есть (И1 — у операции одно
    /// решение), меняется только то, что ещё не решено. Успех и применение — те же, что у
    /// decide: после «принять всё» применяются и принятые судьёй операции тоже.</summary>
    [HttpPost("{id}/accept-all")]
    public async Task<IActionResult> AcceptAll(string id)
    {
        var userId = OwnerOrNull();
        if (userId is null) return Unauthorized();
        if (IsServiceToken) return ServiceTokenRefusal();
        if (Owned(userId, id, out var root, out _) is { } refusal) return refusal;
        var res = WikiPackageStore.AcceptAllContested(root, id);
        if (!res.Ok) return Refusal(res);
        return await AfterDecision(userId, root, res.Package!);
    }

    /// <summary>POST /api/wiki/packages/{id}/revert — «Откатить пакет» (кнопка журнала,
    /// решение 12г, мера 4). Только действие человека: ни у агента, ни у персоны такого инструмента
    /// нет (решение 5) — сервисный токен хода отказан 403 до прочих проверок (B2a).
    /// 404 — пакета нет; 403 — чужой раздел; 409 — пакет не применён, уже откатан,
    /// или страницы изменены после применения (перечень таких страниц — в теле: человек видит, на что
    /// смотреть перед повторной попыткой). Успех — тело со списком возвращённых страниц и, отдельно,
    /// списком тех, что уже стояли в состоянии «до пакета» к началу этого вызова (W3-3: их вернул
    /// прежний виток или их снесли мимо Хранителя — этот вызов их файлом не трогал, и в одном списке
    /// с возвращёнными это читалось бы как «я их вернул»); статус пакета после успеха — «reverted»,
    /// повторный откат того же пакета — 409.</summary>
    [HttpPost("{id}/revert")]
    public async Task<IActionResult> Revert(string id)
    {
        var userId = OwnerOrNull();
        if (userId is null) return Unauthorized();
        if (IsServiceToken) return ServiceTokenRefusal();
        if (Owned(userId, id, out _, out _) is { } refusal) return refusal;

        var r = await _revert.RevertAsync(userId, id).ConfigureAwait(false);
        if (r.Reverted)
            return Ok(new WikiRevertDto(r.RestoredPages, r.AlreadyPages));
        return StatusCode(409, new { code = r.Code, message = r.Reason, pages = r.ChangedPages });
    }

    /// <summary>POST /api/wiki/packages/retract — {sourceRef}: «Откатить всё из этого источника»
    /// (кнопка у хроники, у карточки источника и в «Журнале», мера 5; S4f-2). Только действие
    /// человека: ни у агента, ни у персоны такого инструмента нет (решение 5, как у отката пакета) —
    /// сервисный токен хода отказан 403 раньше этих проверок (B2a).
    /// 400 — форма sourceRef (тот же разбор, что у сервиса; проверяется ПЕРВОЙ среди проверок
    /// пользовательского вызова, до владения);
    /// 404 — источника нет или он чужой: один ответ на оба случая, он не выдаёт наружу,
    /// существует ли источник. Владение проверяется для КАЖДОГО origin: чат — по каталогу чатов
    /// (свой/чужой), прочие origin — наличием в вики владельца пакета с этим источником (чужого
    /// в его корне нет по построению). 409 — вики не заведена или внутренний сбой операции (с
    /// причиной). 200 — исход: списки пакетов откатанных / снятых (сноски сняты) / оставшихся,
    /// снятые хроники, названные проблемы частичного исхода и признак пометки «не разбирать».
    /// Снятие сносок нормализует файл страницы через WikiPage.Render() (весь файл, не только сноска).</summary>
    [HttpPost("retract")]
    public async Task<IActionResult> Retract([FromBody] WikiRetractRequest? req)
    {
        var userId = OwnerOrNull();
        if (userId is null) return Unauthorized();
        if (IsServiceToken) return ServiceTokenRefusal();
        if (string.IsNullOrWhiteSpace(req?.SourceRef))
            return BadRequest(new { code = WikiRetractService.CodeShape, message = "нужен sourceRef" });
        // Форма — раньше владения: «session:» без id это 400, а не 404.
        var shape = WikiRetractService.SourceShapeError(req.SourceRef);
        if (shape is not null)
            return BadRequest(new { code = WikiRetractService.CodeShape, message = shape });

        // Чужой и несуществующий источник — один 404. Вики не заведена — не «нет источника», а
        // 409 сервиса: сначала его отказ, потому что проверять наличие пакетов негде.
        var owned = WikiRetractService.OriginOf(req.SourceRef) == "session"
            ? SessionOwnedBy(userId, req.SourceRef)
            : !WikiPaths.IsInitialized(_dataDir, userId) || _retract.SourceKnown(userId, req.SourceRef);
        if (!owned)
            return NotFound(new { code = CodeSource, message = "такого источника в твоей вики нет" });

        var r = await _retract.RetractAsync(userId, req.SourceRef).ConfigureAwait(false);
        if (!r.Ok)
            return r.Code == WikiRetractService.CodeShape
                ? BadRequest(new { code = r.Code, message = r.Reason })
                : StatusCode(409, new { code = r.Code, message = r.Reason });

        return Ok(new WikiRetractDto(
            [.. r.Outcomes.Where(o => o.Reverted).Select(o => o.PackageId)],
            [.. r.Unlinked.Select(u => u.PackageId)],
            [.. r.NeedsUnlink.Select(o => o.PackageId)],
            [.. r.RemovedChronicles],
            [.. r.Failed],
            r.MarkedSkip,
            [.. r.Outcomes.Where(o => o.Reverted).SelectMany(o => o.AlreadyPages ?? [])
                .Distinct(StringComparer.Ordinal)]));
    }

    // ─── Помощники ──────────────────────────────────────────────────────────

    private bool SessionOwnedBy(string userId, string sourceRef)
    {
        var session = _sessions.GetById(WikiRetractService.IdPartOf(sourceRef));
        return session is not null && _sessions.ResolveOwnerId(session) == userId;
    }

    // Пакет читается из своего корня — чужого по построению нет; остаётся раздел: он не
    // должен называть чужой проект (тот же контракт, что ResolveSection у WikiController).
    // 404 — пакета нет, 409 — его файл нечитаем: это разные исходы для фронта.
    private IActionResult? Owned(string userId, string id, out string root, out WikiPackage? package)
    {
        root = WikiPaths.Root(_dataDir, userId);
        package = null;
        var res = WikiPackageStore.TryRead(root, id);
        if (res.State == WikiPackageFileState.Missing) return NotFound();
        if (res.State != WikiPackageFileState.Valid)
            return StatusCode(409, new { code = WikiPackageStore.CodeInvalid, message = "файл пакета не читается" });
        if (IsForeignSection(userId, res.Package!.Section))
            return StatusCode(403, new { code = CodeForeign, message = "только своя вики" });
        package = res.Package;
        return null;
    }

    // Коды стора → HTTP: «нет пакета» — 404 (здесь он возможен только в гонке с удалением),
    // остальное — 409 с причиной для человека.
    //
    // «Операция уже решена» по вердикту судьи (J-J2b): человеку нужен его путь. Вердикт —
    // тоже решение этой операции (И1), человек его не переворачивает вторым решением:
    // пересмотр — новым пакетом (разбором того же окна), откат уже применённого — кнопкой
    // отката. Без этой строки 409 читался бы как сбой, а не как «тебя уже спросили».
    private IActionResult Refusal(WikiPackageOpResult res, WikiPackage? before = null, int? index = null)
    {
        if (res.Code == WikiPackageStore.CodeNotFound) return NotFound();
        var message = res.Reason;
        if (res.Code == WikiPackageStore.CodeAlreadyDecided
            && index is >= 0 && before is not null && index < before.Operations.Count
            && before.Operations[index.Value].JudgeDecision is not null)
            message = $"{res.Reason} (решил судья; путь человека — откат пакета)";
        return StatusCode(409, new { code = res.Code, message });
    }

    /// <summary>Здесь только отображение результата применения в HTTP (J-J2b): правило
    /// «решение замкнуто применением» (S4g, ADR-035 §5) и обхват исключения живут в
    /// WikiApplyService.ApplyDecidedAsync — их зовёт и этот запрос, и проход судьи (J-J3a),
    /// и коды ответов не имеют права разойтись. Коды — как на master: 200 с карточкой после
    /// применения и её исходом; 200 с последней прочитанной карточкой, если файла пакета уже
    /// нет (ревью S4g, M2, W3-1); 409 — файл не читается.
    ///
    /// Пакет, который решили, но который не применился («apply_failed»/«stale»), возвращает
    /// здесь своё окно в разбор (ReturnDecidedWindowsAsync, T-A2) — в ФОНЕ, не дожидаясь
    /// (T-A3): форма HTTP-ответа от этого не меняется и не ждёт прогона чата. Окно — дело
    /// разбора чата, а не применения: сам возврат зовёт _chat (в сервисе применения он был бы
    /// циклом DI: Apply → Chat → Ingest → Apply), поэтому здесь остаётся его вызов.
    ///
    /// internal — граница после применения: тест ставит диск в состояние «файла пакета уже
    /// нет» и в состояние «исключение из применения» и видит форму ответа, а не гонку
    /// (ревью S4g, M2/M3).</summary>
    internal async Task<IActionResult> AfterDecision(string userId, string root, WikiPackage decided)
    {
        var r = await _apply.ApplyDecidedAsync(userId, root, decided).ConfigureAwait(false);
        var apply = MapApply(r.Apply);
        if (r.Outcome == WikiApplyService.DecidedOutcome.PackageMissing)
            return Ok(new WikiDecisionDto(Dto(r.Package!), apply));
        if (r.Outcome == WikiApplyService.DecidedOutcome.PackageUnreadable)
            return StatusCode(409, new { code = WikiPackageStore.CodeInvalid, message = r.Reason });
        // T-A3 (ревью T-A2, замечание 3): возврат окна — в фоне, не дожидаясь. При идущем
        // прогоне чата ReturnWindow ждёт его конца, а человеку ответ решения нужен сразу:
        // кнопка «Входящих» не имеет права висеть на чужом разборе. Фоне нужны только
        // значения (владелец, карточка с диска, исход применения) и singleton _chat
        // (NotesSubsystem.cs:109): ни HttpContext, ни scoped-зависимостей запроса в него не
        // попадает. Задача не может быть faulted, т.е. «unobserved task exception» в логе от
        // неё нет: ReturnWindowAsync fail-closed на входе и весь его корпус под обхватом
        // (WikiChatIngestService.cs:359-386), остальное — разбор прочитанной карточки без
        // обращений к вводу.
        _ = ReturnDecidedWindowsAsync(userId, r.Package!, r.Apply);
        return Ok(new WikiDecisionDto(Dto(r.Package!), apply));
    }

    /// <summary>Исход применения сервиса → тело ответа (S7a): форма контракта фронта не
    /// меняется — те же поля applied/code/reason/violations.</summary>
    private static WikiApplyOutcomeDto? MapApply(WikiApplyService.ApplyResult? apply) =>
        apply is null
            ? null
            : new WikiApplyOutcomeDto(apply.Value.Applied, apply.Value.Code, apply.Value.Reason, apply.Value.Violations);

    /// <summary>T-A2: пакет, который человек решил, но который не применился, не теряет своё
    /// окно: курсор разбора каждого чата-источника возвращается на его начало (ReturnWindowAsync,
    /// T-A1) — дельта разбиралась заново, её попыткой станет новый пакет. Условия — все три:
    /// статус на диске — «apply_failed» или «stale» (успешное применение окно не возвращает:
    /// его результат на месте); в пакете есть операция с решением человека (авто-путь без
    /// решения человека окном не владеет — его закрыл код, не человек); код применения не
    /// «wiki_apply_claim» и не «wiki_apply_status»: такой код значит, что этот вызов гонку
    /// проиграл — пакетом владеет тот, кто его заявляет или применил, и проигравший окно не
    /// возвращает. Ключ — origin источника, а не форма ref: окно принадлежит чату,
    /// несессионные источники не трогаются. Несколько окон одного чата — возврат к самому
    /// раннему началу (min a). Отказ ReturnWindow (false, исключения наружу нет) невиден
    /// ответу: контракт решения не меняется.
    ///
    /// T-A3 (ревью T-A2, замечание 3): вызывается ПОСЛЕ формы ответа, в фоне — при идущем
    /// прогоне чата откат ждёт его конца, и ждать ответа этому вызову не за чем. Наружу из
    /// фонового задачи выйти нечему: ReturnWindowAsync fail-closed на входе, его корпус — под
    /// обхватом ReturnWindowGuardedAsync (WikiChatIngestService.cs:373-386), остальной код —
    /// разбор прочитанной карточки (словарь, TryParseSessionRef, Math.Min) без обращений к
    /// вводу; зависимости — только singleton _chat и значения вызова.</summary>
    private async Task ReturnDecidedWindowsAsync(string userId, WikiPackage pkg, WikiApplyService.ApplyResult? apply)
    {
        if (pkg.Status is not (WikiPackage.StatusApplyFailed or WikiPackage.StatusStale))
            return;
        if (!pkg.Operations.Any(o => o.HumanDecision is not null))
            return;
        if (apply?.Code is WikiApplyService.CodeClaim or WikiApplyService.CodeStatus)
            return;

        var windows = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var s in pkg.Sources)
        {
            if (s.Origin != WikiGate.OriginSession)
                continue;
            if (!WikiPage.TryParseSessionRef(s.Ref, out var sessionId, out var from, out _))
                continue;
            windows[sessionId] = windows.TryGetValue(sessionId, out var cur) ? Math.Min(cur, from) : from;
        }
        // log не передаём: у контроллеров вертикали логгера нет.
        foreach (var (sessionId, from) in windows)
            await _chat.ReturnWindowAsync(userId, sessionId, from).ConfigureAwait(false);
    }

    private static WikiPackageDto Dto(WikiPackage p) => new(
        p.Id, p.Section, p.Status, p.StatusReason, p.CreatedAt, p.UpdatedAt,
        [.. p.Sources.Select(s => new WikiPackageSourceDto(s.Origin, s.Ref))],
        [.. p.Operations.Select((o, i) => new WikiPackageOperationDto(
            i, o.Op, o.Page, o.Target, o.Text, o.Class, o.Decision, o.Reason, o.BaseHashes,
            o.InputTokens, o.OutputTokens, o.HumanDecision, o.HumanReason,
            o.Claim, o.Quotes, o.Kind, o.AsOf,
            o.JudgeDecision, o.JudgeReason, o.JudgeModel, o.JudgeAt, o.JudgeCostUsd,
            o.ReasonCode))]);

    // Раздел пакета — чужой проект? Имя проекта не показывается и не решается: только своя
    // вики (ADR-036, решение 1).
    private bool IsForeignSection(string userId, string section) =>
        WikiSections.TryProjectId(section, out var projectId)
        && _projects.GetById(projectId) is { } project && project.OwnerId != userId;
}

// ─── DTO контракта (S7a — по ним работает Кира) ─────────────────────────────

/// <summary>Список пакетов. invalidCount — число нечитаемых файлов в каталоге пакетов:
/// их надо показать как сломанные, а не как пустоту.</summary>
public sealed record WikiPackagesDto(IReadOnlyList<WikiPackageDto> Packages, int InvalidCount);

/// <summary>Пакет: id, раздел, статус (checking/proposed/…/stale), причина отказа или
/// устаревания, времена, источники и операции. Операции — в порядке индексов: они
/// неизменяемы после создания, поэтому index и есть адрес операции.</summary>
public sealed record WikiPackageDto(
    string Id,
    string Section,
    string Status,
    string? StatusReason,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    IReadOnlyList<WikiPackageSourceDto> Sources,
    IReadOnlyList<WikiPackageOperationDto> Operations);

public sealed record WikiPackageSourceDto(string Origin, string Ref);

/// <summary>Намерение операции: class — «origin × op», decision — auto|inbox, reason — почему
/// спорная; humanDecision — решение человека (accepted|rejected|null = не решено). Доказательства
/// операции (дефект a9d8d6eb): claim — утверждение, quotes — цитаты-доказательства (1–3, порядок
/// сохраняется), kind — вид создаваемой страницы, asOf — дата «на когда». Всё читается из операции
/// пакета как есть (WikiPackage.Operation); для старой операции без полей — null, quotes — null,
/// а не []. Вердикт облачного судьи (дефект 67cd64d0): judgeDecision/judgeReason/judgeModel/
/// judgeAt/judgeCostUsd — тоже как есть из операции: вкладка «Решения» фронта (lib/wiki.ts)
/// читает ровно эти имена; у операции без вердикта — null, а не отсутствие поля.
/// Код причины (дефект из ревью a92a9550): reasonCode — машинный код вердикта политики
/// (перечень WikiApplyPolicy.ReasonCodes), как есть из операции: по нему решается допуск
/// к облачному судье (WikiApplyPolicy.JudgeAdmitted). У операции без кода (старый пакет)
/// — null, а не отсутствие поля.</summary>
public sealed record WikiPackageOperationDto(
    int Index,
    string Op,
    string Page,
    string? Target,
    string? Text,
    string Class,
    string Decision,
    string? Reason,
    IReadOnlyDictionary<string, string> BaseHashes,
    int? InputTokens,
    int? OutputTokens,
    string? HumanDecision,
    string? HumanReason,
    string? Claim = null,
    IReadOnlyList<string>? Quotes = null,
    string? Kind = null,
    string? AsOf = null,
    string? JudgeDecision = null,
    string? JudgeReason = null,
    string? JudgeModel = null,
    DateTimeOffset? JudgeAt = null,
    decimal? JudgeCostUsd = null,
    string? ReasonCode = null);

/// <summary>Решение по одной спорной операции. accept=false требует reason.</summary>
public sealed record WikiDecideRequest(int? Index, bool? Accept, string? Reason);

/// <summary>Итог решения человека (S4g — по нему работает Кира): карточка пакета после решения
/// и исход применения этого же пакета. package.status — состояние ПОСЛЕ применения: «applied»,
/// «apply_failed», «stale» — или прежний («proposed»: остались нерешённые спорные; «rejected»:
/// все отклонены), когда применять было нечего. apply = null ровно в этих двух случаях.
/// Ничего чужого в ответе нет: только этот пакет и исход его применения.</summary>
public sealed record WikiDecisionDto(WikiPackageDto Package, WikiApplyOutcomeDto? Apply);

/// <summary>Исход применения: applied=true — страницы записаны, пакет «applied», code/reason
/// пустые. Отказ — код (wiki_apply_*) и причина человеческим текстом; violations — полный
/// перечень для провала гейта. При отказе решения человека сохранены в package.operations.</summary>
public sealed record WikiApplyOutcomeDto(
    bool Applied,
    string? Code,
    string? Reason,
    IReadOnlyList<string> Violations);

/// <summary>Результат отката: RestoredPages — страницы, возвращённые к состоянию до применения
/// («Журнал» обновляет их и дифф строки op=revert). AlreadyPages — подмножество RestoredPages:
/// страницы, которые уже стояли в состоянии «до пакета» к началу этого вызова (их вернул прежний
/// виток этого же отката или их снесли/вернули мимо Хранителя): этот вызов их файлом не трогал, и
/// молчать об этом значило бы «успех, страницы возвращены» о файле, которого он не касался (W3-3).
/// Поле опционально для текущих потребителей. Отказ — в теле 409: code + message + pages (pages —
/// изменённые страницы, на которые человеку смотреть перед повторной попыткой).</summary>
public sealed record WikiRevertDto(IReadOnlyList<string> RestoredPages, IReadOnlyList<string>? AlreadyPages = null);

/// <summary>Запрос «Откатить всё из этого источника»: карточка источника
/// («{origin}:…» — та же форма, что в пакетах и журнале).</summary>
public sealed record WikiRetractRequest(string? SourceRef);

/// <summary>Исход отката по источнику (мера 5): revertedPackages — побайтно откатанные;
/// unlinkedPackages — обработанные снятием сносок (страницы этих пакетов больше не несут
/// ссылок на источник); remainingPackages — «остались»: пакеты, чьи страницы не удалось обработать
/// (причины — в failed); removedChronicles — страницы хроники, снятые целиком; failed —
/// названные проблемы частичного исхода; markedSkip — чату поставлена пометка «не разбирать»;
/// alreadyPages — страницы откатанных пакетов, которые уже стояли в состоянии «до пакета» к
/// началу отката (этот вызов их файлом не трогал; подмножество восстановленных, W3-3).</summary>
public sealed record WikiRetractDto(
    IReadOnlyList<string> RevertedPackages,
    IReadOnlyList<string> UnlinkedPackages,
    IReadOnlyList<string> RemainingPackages,
    IReadOnlyList<string> RemovedChronicles,
    IReadOnlyList<string> Failed,
    bool MarkedSkip,
    IReadOnlyList<string>? AlreadyPages = null);
