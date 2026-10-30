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
//
// Здесь человек решает только то, что ушло во «Входящие»: операция с решением политики
// `auto` через REST не решается (409). Пакеты здесь читаются, решаются и откатываются (S4b-3):
// их никто не создаёт (их строит Хранитель). Решение человека замкнуто применением (S4g,
// ADR-035 §5): тот же запрос, который оставил в пакете ноль нерешённых спорных операций и
// хотя бы одну принятую, применяет его через WikiApplyService и отвечает итоговое состояние
// пакета; решение, оставившее нерешённые спорные (proposed) или отклонившее все (rejected),
// ничего не применяет. Отдельной ручки «применить» нет (повтор применения человеком — не здесь);
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
    private readonly string _dataDir;

    public WikiPackagesController(IProjectManager projects, IConfiguration config, WikiApplyService apply,
        WikiPackageRevert revert, WikiRetractService retract, ISessionDirectory sessions)
    {
        _projects = projects;
        _apply = apply;
        _revert = revert;
        _retract = retract;
        _sessions = sessions;
        // Каталог данных — как у всех сервисов (DataPath указывает на projects.json в dataDir).
        var dataPath = config["DataPath"] ?? Path.Combine(AppContext.BaseDirectory, "data", "projects.json");
        _dataDir = Path.GetDirectoryName(Path.GetFullPath(dataPath))!;
    }

    // Владелец — только из токена (claim sub). Явная проверка — копия гейта внутри действия:
    // без токена действие отвечает 401 (контракт), а не падает с NRE на claim.
    private string? OwnerOrNull() =>
        User.Identity?.IsAuthenticated == true ? User.FindFirstValue(JwtRegisteredClaimNames.Sub) : null;

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
    /// решена (решение не меняется). Успех — {package, apply}: карточка пакета после решения
    /// и, если решение оставило ноль нерешённых спорных и хотя бы одну принятую операцию,
    /// исход применения этого же пакета; для proposed/rejected apply — null.</summary>
    [HttpPost("{id}/decide")]
    public async Task<IActionResult> Decide(string id, [FromBody] WikiDecideRequest? req)
    {
        var userId = OwnerOrNull();
        if (userId is null) return Unauthorized();
        if (req?.Index is null || req.Accept is null)
            return BadRequest(new { code = CodeRequest, message = "нужны index операции и accept" });
        if (Owned(userId, id, out var root) is { } refusal) return refusal;
        var res = WikiPackageStore.DecideOperation(root, id, req.Index.Value, req.Accept.Value, req.Reason);
        if (!res.Ok) return Refusal(res);
        return await AfterDecision(userId, root, res.Package!);
    }

    /// <summary>POST /api/wiki/packages/{id}/accept-all — «принять всё спорное пакета».
    /// 409, если спорных операций нет: неспорные решает политика, а не человек. Успех и
    /// применение — те же, что у decide.</summary>
    [HttpPost("{id}/accept-all")]
    public async Task<IActionResult> AcceptAll(string id)
    {
        var userId = OwnerOrNull();
        if (userId is null) return Unauthorized();
        if (Owned(userId, id, out var root) is { } refusal) return refusal;
        var res = WikiPackageStore.AcceptAllContested(root, id);
        if (!res.Ok) return Refusal(res);
        return await AfterDecision(userId, root, res.Package!);
    }

    /// <summary>POST /api/wiki/packages/{id}/revert — «Откатить пакет» (кнопка журнала,
    /// решение 12г, мера 4). Только действие человека: ни у агента, ни у персоны такого инструмента
    /// нет (решение 5). 404 — пакета нет; 403 — чужой раздел; 409 — пакет не применён, уже откатан,
    /// или страницы изменены после применения (перечень таких страниц — в теле: человек видит, на что
    /// смотреть перед повторной попыткой). Успех — тело со списком возвращённых страниц; статус
    /// пакета после успеха — «reverted», повторный откат того же пакета — 409.</summary>
    [HttpPost("{id}/revert")]
    public async Task<IActionResult> Revert(string id)
    {
        var userId = OwnerOrNull();
        if (userId is null) return Unauthorized();
        if (Owned(userId, id, out _) is { } refusal) return refusal;

        var r = await _revert.RevertAsync(userId, id).ConfigureAwait(false);
        if (r.Reverted)
            return Ok(new WikiRevertDto(r.RestoredPages));
        return StatusCode(409, new { code = r.Code, message = r.Reason, pages = r.ChangedPages });
    }

    /// <summary>POST /api/wiki/packages/retract — {sourceRef}: «Откатить всё из этого источника»
    /// (кнопка у хроники, у карточки источника и в «Журнале», мера 5; S4f-2). Только действие
    /// человека: ни у агента, ни у персоны такого инструмента нет (решение 5, как у отката пакета).
    /// 400 — форма sourceRef (тот же разбор, что у сервиса); 404 — чата нет или он чужой: один
    /// ответ на оба случая, он не выдаёт наружу, существует ли источник (свой/чужой проверяется
    /// по каталогу чатов — единственному имени здесь; для прочих origin граница — per-owner:
    /// чужого в своём корне нет по построению, и их карточки этот путь не зовёт). 409 — вики не
    /// заведена или внутренний сбой операции (с причиной). 200 — исход: списки пакетов
    /// откатанных / снятых (сноски сняты) / оставшихся, снятые хроники, названные проблемы
    /// частичного исхода и признак пометки «не разбирать».</summary>
    [HttpPost("retract")]
    public async Task<IActionResult> Retract([FromBody] WikiRetractRequest? req)
    {
        var userId = OwnerOrNull();
        if (userId is null) return Unauthorized();
        if (string.IsNullOrWhiteSpace(req?.SourceRef))
            return BadRequest(new { code = WikiRetractService.CodeShape, message = "нужен sourceRef" });

        if (WikiRetractService.OriginOf(req.SourceRef) == "session")
        {
            var session = _sessions.GetById(WikiRetractService.IdPartOf(req.SourceRef));
            if (session is null || _sessions.ResolveOwnerId(session) != userId)
                return NotFound(new { code = CodeSource, message = "такого источника в твоей вики нет" });
        }

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
            r.MarkedSkip));
    }

    // ─── Помощники ──────────────────────────────────────────────────────────

    // Пакет читается из своего корня — чужого по построению нет; остаётся раздел: он не
    // должен называть чужой проект (тот же контракт, что ResolveSection у WikiController).
    // 404 — пакета нет, 409 — его файл нечитаем: это разные исходы для фронта.
    private IActionResult? Owned(string userId, string id, out string root)
    {
        root = WikiPaths.Root(_dataDir, userId);
        var res = WikiPackageStore.TryRead(root, id);
        if (res.State == WikiPackageFileState.Missing) return NotFound();
        if (res.State != WikiPackageFileState.Valid)
            return StatusCode(409, new { code = WikiPackageStore.CodeInvalid, message = "файл пакета не читается" });
        if (IsForeignSection(userId, res.Package!.Section))
            return StatusCode(403, new { code = CodeForeign, message = "только своя вики" });
        return null;
    }

    // Коды стора → HTTP: «нет пакета» — 404 (здесь он возможен только в гонке с удалением),
    // остальное — 409 с причиной для человека.
    private IActionResult Refusal(WikiPackageOpResult res) =>
        res.Code == WikiPackageStore.CodeNotFound
            ? NotFound()
            : StatusCode(409, new { code = res.Code, message = res.Reason });

    /// <summary>Решение человека замкнуто применением (S4g, ADR-035 §5): если оно оставило в
    /// пакете ноль нерешённых спорных операций и хотя бы одну принятую (accepted |
    /// partially_accepted), тот же запрос применяет пакет; решение, оставившее нерешённые
    /// спорные (proposed) или отклонившее все (rejected), ничего не применяет — применять
    /// нечего, и это видно по статусу в ответе. Применение читает пакет с диска под своей
    /// блокировкой (ApplyAsync, решение человека берётся только из дисковых HumanDecision),
    /// его сбой — результат-отказ с причиной, а не исключение и не «зависший applying»:
    /// решения записаны до применения и остаются на диске. Итоговый статус — прочитанный с
    /// диска после применения, а не предположение: «applied»/«apply_failed»/«stale» или
    /// прежний, если применять было нечего.</summary>
    private async Task<IActionResult> AfterDecision(string userId, string root, WikiPackage decided)
    {
        WikiApplyOutcomeDto? apply = null;
        if (decided.Status is WikiPackage.StatusAccepted or WikiPackage.StatusPartiallyAccepted)
        {
            var r = await _apply.ApplyAsync(userId, decided).ConfigureAwait(false);
            apply = new WikiApplyOutcomeDto(r.Applied, r.Code, r.Reason, r.Violations);
        }
        var after = WikiPackageStore.TryRead(root, decided.Id);
        if (after.State == WikiPackageFileState.Missing) return NotFound();
        if (after.State != WikiPackageFileState.Valid || after.Package is null)
            return StatusCode(409, new { code = WikiPackageStore.CodeInvalid, message = "файл пакета не читается" });
        return Ok(new WikiDecisionDto(Dto(after.Package), apply));
    }

    private static WikiPackageDto Dto(WikiPackage p) => new(
        p.Id, p.Section, p.Status, p.StatusReason, p.CreatedAt, p.UpdatedAt,
        [.. p.Sources.Select(s => new WikiPackageSourceDto(s.Origin, s.Ref))],
        [.. p.Operations.Select((o, i) => new WikiPackageOperationDto(
            i, o.Op, o.Page, o.Target, o.Text, o.Class, o.Decision, o.Reason, o.BaseHashes,
            o.InputTokens, o.OutputTokens, o.HumanDecision, o.HumanReason))]);

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
/// спорная; humanDecision — решение человека (accepted|rejected|null = не решено).</summary>
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
    string? HumanReason);

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

/// <summary>Результат отката: страницы, возвращённые к состоянию до применения («Журнал» обновляет
/// их и дифф строки op=revert). Отказ — в теле 409: code + message + pages (pages — изменённые
/// страницы, на которые человеку смотреть перед повторной попыткой).</summary>
public sealed record WikiRevertDto(IReadOnlyList<string> RestoredPages);

/// <summary>Запрос «Откатить всё из этого источника»: карточка источника
/// («{origin}:…» — та же форма, что в пакетах и журнале).</summary>
public sealed record WikiRetractRequest(string? SourceRef);

/// <summary>Исход отката по источнику (мера 5): revertedPackages — побайтно откатанные;
/// unlinkedPackages — обработанные снятием сносок (страницы этих пакетов больше не несут
/// ссылок на источник); remainingPackages — «остались»: пакеты, чьи страницы не удалось обработать
/// (причины — в failed); removedChronicles — страницы хроники, снятые целиком; failed —
/// названные проблемы частичного исхода; markedSkip — чату поставлена пометка «не разбирать».</summary>
public sealed record WikiRetractDto(
    IReadOnlyList<string> RevertedPackages,
    IReadOnlyList<string> UnlinkedPackages,
    IReadOnlyList<string> RemainingPackages,
    IReadOnlyList<string> RemovedChronicles,
    IReadOnlyList<string> Failed,
    bool MarkedSkip);
