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
// `auto` через REST не решается (409). Пакеты здесь только читаются и решаются: их никто
// не создаёт (их строит Хранитель) и не применяет (применение — S4b).
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
    private readonly string _dataDir;

    public WikiPackagesController(IProjectManager projects, IConfiguration config)
    {
        _projects = projects;
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
    /// в proposed, index вне списка, отказ без причины, файл пакета нечитаем.</summary>
    [HttpPost("{id}/decide")]
    public IActionResult Decide(string id, [FromBody] WikiDecideRequest? req)
    {
        var userId = OwnerOrNull();
        if (userId is null) return Unauthorized();
        if (req?.Index is null || req.Accept is null)
            return BadRequest(new { code = CodeRequest, message = "нужны index операции и accept" });
        if (Owned(userId, id, out var root) is { } refusal) return refusal;
        return Store(WikiPackageStore.DecideOperation(root, id, req.Index.Value, req.Accept.Value, req.Reason));
    }

    /// <summary>POST /api/wiki/packages/{id}/accept-all — «принять всё спорное пакета».
    /// 409, если спорных операций нет: неспорные решает политика, а не человек.</summary>
    [HttpPost("{id}/accept-all")]
    public IActionResult AcceptAll(string id)
    {
        var userId = OwnerOrNull();
        if (userId is null) return Unauthorized();
        if (Owned(userId, id, out var root) is { } refusal) return refusal;
        return Store(WikiPackageStore.AcceptAllContested(root, id));
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
    private IActionResult Store(WikiPackageOpResult res) => res.Ok
        ? Ok(Dto(res.Package!))
        : res.Code == WikiPackageStore.CodeNotFound
            ? NotFound()
            : StatusCode(409, new { code = res.Code, message = res.Reason });

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
