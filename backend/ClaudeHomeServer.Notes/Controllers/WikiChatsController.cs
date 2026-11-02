using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Composition;
using ClaudeHomeServer.Services.Notes;
using ClaudeHomeServer.Services.Notes.Wiki;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ClaudeHomeServer.Notes.Controllers;

// «Отбор чатов» вики (S6f, ADR-035, решение 12а): список «разбирается / серая зона /
// пропущено / нечего добавить» по каждому чату с причиной (балл и главные сигналы либо
// правило ступени 0) и пересчёт по кнопке. Список и пересчёт ничего не запускают:
// пересчёт пишет только свои поля. Разбор запускает только POST analyze (S7b0a) —
// единственный запрос этого контроллера, который доходит до модели: кнопка человека
// «разобрать этот чат»; analyze-status — его статус после 202. Поля списка running,
// cursor, chroniclePath (S7b0a) — текущее состояние чата для фронта: running и last —
// из сервиса, cursor — из стора, дом и путь хроники — из формул сервиса (HomeOf,
// ChroniclePath), не копированием построения. С N1 (дефект 58ad40a5) chroniclePath —
// null, если файла хроники нет: после отката фронт должен вести на «Не найдено»,
// а не на мёртвый путь.
//
// Образец — WikiPackagesController: [Authorize], владелец только из токена, коды отказов —
// контракт для фронта. Состояние читается из стора keeper/chat-triage.json (S6e): список
// показывает то, что уже посчитано; несчитанные чаты в список не встают — их посчитает
// пересчёт. Пересчёт обновляет только свои поля (UpdateScoring): балл и зону ступени 1;
// курсор, зона и причина исхода, nothingNewStreak принадлежат разбору (разведение владения
// по ревью S6f). Тело пересчёта — в службе WikiChatRecalc (NA-3), контроллер только
// отображает её итог: тот же пересчёт зовёт и ночной разбор. Поэтому список СОБИРАЕТ
// показ: исход разбора nothing_new приоритетен, пока его поставил разбор, иначе
// показывается зона ступени 1 (ShownZone).
[ApiController]
[Authorize]
[Route("api/wiki/chats")]
public class WikiChatsController : ControllerBase
{
    private readonly ISessionDirectory _sessions;
    private readonly WikiChatIngestService _chat;
    private readonly WikiChatRecalc _recalc;
    private readonly string _dataDir;
    private readonly IFeatureFlagGate? _flags;

    public WikiChatsController(ISessionDirectory sessions, WikiChatIngestService chat, WikiChatRecalc recalc,
        IConfiguration config, IFeatureFlagGate? flags = null)
    {
        _sessions = sessions;
        _chat = chat;
        _recalc = recalc;
        _flags = flags;
        // Каталог данных — как у всех сервисов (DataPath указывает на projects.json в dataDir).
        var dataPath = config["DataPath"] ?? Path.Combine(AppContext.BaseDirectory, "data", "projects.json");
        _dataDir = Path.GetDirectoryName(Path.GetFullPath(dataPath))!;
    }

    // Владелец — только из токена (claim sub), как в соседних контроллерах вертикали.
    private string? OwnerOrNull() =>
        User.Identity?.IsAuthenticated == true ? User.FindFirstValue(JwtRegisteredClaimNames.Sub) : null;

    // Порядок зон в списке — тот, что в интерфейсе отбора (ADR-035:217): сначала то, что
    // разбирается; неизвестная зона (стор будущего шага) — в конец, она не должна терять
    // запись из списка.
    private static readonly string[] ZoneOrder =
        [WikiChatScorer.ZoneTriage, WikiChatScorer.ZoneGray, WikiChatScorer.ZoneSkipped,
         WikiChatIngestService.ZoneNothingNew, WikiChatScorer.ZoneExcluded];

    private static int ZoneRank(string zone)
    {
        var i = Array.IndexOf(ZoneOrder, zone);
        return i < 0 ? ZoneOrder.Length : i;
    }

    // Показанная зона записи и её причина: поле zone принадлежит разбору (его исход
    // nothing_new), зона ступени 1 — пересчёту (stage1Zone). Показываем исход разбора, пока
    // он поставлен; иначе — зону ступени 1. Для записей до разведения владения (пересчёт
    // писал общий zone, stage1Zone нет) — прежний zone: старый файл показывается без пересчёта.
    private static string ShownZone(WikiChatTriageEntry e) =>
        e.Zone == WikiChatIngestService.ZoneNothingNew
            ? e.Zone
            : e.Stage1Zone ?? e.Zone ?? "";

    private static string? ShownReason(WikiChatTriageEntry e) =>
        e.Zone == WikiChatIngestService.ZoneNothingNew ? e.Reason : e.Stage1Reason ?? e.Reason;

    // Путь хроники отдаётся только когда файл хроники есть на диске (N1, дефект 58ad40a5):
    // откат по источнику снимает хронику целиком (WikiRetractService, мера 5), и со старым
    // путём фронт открывал мёртвую страницу. Проверка и отдача не атомарны: GET только
    // читает, файл может исчезнуть между ними — безвредно, 404 страницы — тот же
    // контракт для фронта, что был.
    private string? ChroniclePathIfExists(string home, string sessionId, string userId)
    {
        var path = WikiChatIngestService.ChroniclePath(home, sessionId);
        if (path is null) return null;
        var full = SafePath.JoinBelowRoot(WikiPaths.Root(_dataDir, userId), "pages/" + path);
        return System.IO.File.Exists(full) ? path : null;
    }

    /// <summary>GET /api/wiki/chats — список отбора по своей вики: запись стора на каждый
    /// существующий свой чат, с причиной и сигналами; counts — число чатов по каждой
    /// ПОКАЗАННОЙ зоне (ShownZone). Записи несуществующего или уже чужого чата из списка
    /// выпадают (список — про чаты, а не про файл), но на counts несуществующего чата нет.
    /// Нечитаемый стор — 409 с кодом: список не притворяется пустым там, где файл сломан.</summary>
    [HttpGet]
    public IActionResult List()
    {
        var userId = OwnerOrNull();
        if (userId is null) return Unauthorized();

        var read = WikiChatTriageStore.Get(WikiPaths.Root(_dataDir, userId));
        if (read.State == WikiTriageFileState.Invalid)
            return StatusCode(409, new { code = WikiChatTriageStore.CodeInvalid, message = "файл разбора чатов не читается" });

        var items = new List<WikiChatTriageDto>();
        var counts = new Dictionary<string, int>();
        foreach (var e in read.Entries)
        {
            var session = _sessions.GetById(e.SessionId);
            if (session is null || _sessions.ResolveOwnerId(session) != userId)
                continue;
            var zone = ShownZone(e);
            counts[zone] = counts.TryGetValue(zone, out var c) ? c + 1 : 1;
            // Дом чата — та же формула, что у разбора (AnalyzeAsync): в записи стора дома нет,
            // он выводится из чата. Дом и путь — из формул сервиса, не отсюда.
            items.Add(new WikiChatTriageDto(
                e.SessionId, session.Name, zone, e.Score, e.Signals, ShownReason(e),
                e.NothingNewStreak, e.TriagedAt,
                _chat.IsRunning(userId, e.SessionId), e.Cursor,
                ChroniclePathIfExists(WikiChatIngestService.HomeOf(session), e.SessionId, userId)));
        }

        items = items
            .OrderBy(i => ZoneRank(i.Zone))
            .ThenByDescending(i => i.Score ?? int.MinValue)
            .ThenBy(i => i.SessionId, StringComparer.Ordinal)
            .ToList();
        return Ok(new WikiChatsListDto(items, counts));
    }

    /// <summary>POST /api/wiki/chats/recalculate — «пересчитать» (кнопка блока «Отбор чатов»):
    /// ступень 0 и балл ступени 1 по всем своим чатам, результат — в стор. Ничего не запускает:
    /// ни модели, ни разбора (теневой режим). Курсор, зона и причина исхода разбора,
    /// nothingNewStreak существующих записей сохраняются: пересчёт пишет только свои поля
    /// (score/signals/stage1Zone/stage1Reason). 409 — нечитаемый стор (пересчёт его не
    /// перезапишет молча). Успех — число пересчитанных чатов и counts по зонам ступени 1
    /// (их посчитал скорер; список показывает их с приоритетом исхода разбора — ShownZone).
    /// Тело пересчёта — в службе WikiChatRecalc (NA-3: его зовёт и ночной разбор); контроллер
    /// резолвит владельца из токена и отображает итог службы в ответ (409 {code,message} /
    /// WikiChatsRecalcDto) — прежние строки отказа проходят как есть.</summary>
    [HttpPost("recalculate")]
    public async Task<IActionResult> Recalculate()
    {
        var userId = OwnerOrNull();
        if (userId is null) return Unauthorized();

        var res = await _recalc.RecalculateAsync(userId);
        if (!res.Ok)
            return StatusCode(409, new { code = res.Code, message = res.Reason });
        return Ok(new WikiChatsRecalcDto(res.Counted, res.Counts));
    }

    /// <summary>POST /api/wiki/chats/{sessionId}/analyze — «разобрать этот чат сейчас» (S7b0a,
    /// кнопка в чате; единственный запрос контроллера, доходящий до модели). Ждёт исход до
    /// AnalyzeWaitSeconds; не дождался — 202 {running:true}: разбор продолжается на стороне
    /// сервиса, его получают analyze-status. ct заявителя в прогон не идёт: отвалившийся
    /// клиент не гасит разбор (тот же порядок, что у заявки очереди, S6b-3). Отказ с машинным
    /// кодом — 404/409 {code,message}; без кода — 200 rejected: исключения наружу нет.</summary>
    private const int AnalyzeWaitSeconds = 10;

    [HttpPost("{sessionId}/analyze")]
    public Task<IActionResult> Analyze(string sessionId) => AnalyzeCore(sessionId, TimeSpan.FromSeconds(AnalyzeWaitSeconds));

    /// <summary>Тестовый шов: ожидание — параметр, тест не ждёт реальные 10 с.</summary>
    internal async Task<IActionResult> AnalyzeCore(string sessionId, TimeSpan wait)
    {
        var userId = OwnerOrNull();
        if (userId is null) return Unauthorized();
        // Разбор тратит модель: при выключенном llm-wiki не стартует. Флаг может смениться
        // между этой проверкой и ходом модели — допустимо; уже идущий разбор не прерывается.
        if (_flags?.IsEnabled(userId, FeatureFlagKeys.LlmWiki) != true)
            return StatusCode(409, new { code = "wiki_llm_wiki_off", message = "разбор не запущен: функция выключена" });

        var task = _chat.AnalyzeAsync(userId, sessionId); // ct заявителя в прогон не идёт
        try
        {
            return MapAnalysis(await task.WaitAsync(wait));
        }
        catch (TimeoutException)
        {
            // Разбор идёт (этой или чужой заявки): исход — через analyze-status.
            return StatusCode(202, new { running = true });
        }
    }

    private IActionResult MapAnalysis(WikiChatIngestService.ChatResult res)
    {
        if (res.Outcome == WikiChatIngestService.OutcomeRejected && res.Code is { Length: > 0 } code)
            // fallback — та же формулировка, что у service'а (F1): и в 404 чужого/несуществующего
            // одна строка на весь контракт — service её и так отдаёт; запасной текст не должен
            // отличаться от неё.
            return code == WikiChatIngestService.CodeNotFound
                ? NotFound(new { code, message = res.Reason ?? "чат не существует" })
                : StatusCode(409, new { code, message = res.Reason ?? "разбор отклонён" });
        // applied / awaiting_human / nothing_new / up_to_date и rejected без кода —
        // исход проходит как есть: сам исход и есть контракт.
        return Ok(new { outcome = res.Outcome, reason = res.Reason, packageIds = res.PackageIds });
    }

    /// <summary>GET /api/wiki/chats/{sessionId}/analyze-status — идёт ли разбор и каков его
    /// последний исход (опрос после 202; поле running списка — тот же источник). Чужой и
    /// несуществующий чат — один и тот же 404: per-owner, без утечки существования чужого.</summary>
    [HttpGet("{sessionId}/analyze-status")]
    public IActionResult AnalyzeStatus(string sessionId)
    {
        var userId = OwnerOrNull();
        if (userId is null) return Unauthorized();
        var session = _sessions.GetById(sessionId);
        if (session is null || _sessions.ResolveOwnerId(session) != userId)
            return NotFound(new { code = WikiChatIngestService.CodeNotFound, message = "чат не существует" });

        var running = _chat.IsRunning(userId, sessionId);
        var last = _chat.GetLast(userId, sessionId);
        return Ok(new
        {
            running,
            last = last is null
                ? null
                : (object)new
                {
                    outcome = last.Result.Outcome,
                    reason = last.Result.Reason,
                    packageIds = last.Result.PackageIds,
                    finishedAt = last.FinishedAt,
                },
        });
    }
}

/// <summary>Список отбора: записи по чатам (порядок — по зонам, внутри зоны — по баллу) и
/// числа по зонам.</summary>
public sealed record WikiChatsListDto(IReadOnlyList<WikiChatTriageDto> Items, IReadOnlyDictionary<string, int> Counts);

/// <summary>Строка списка: чат, его ПОКАЗАННАЯ зона (исход разбора либо зона ступени 1 —
/// ShownZone), балл и сигналы (null — балла ещё нет), причина для человека этой зоны,
/// счётчик «нечего добавить»; running — разбор идёт сейчас (S7b0a, тот же источник, что у
/// analyze-status), cursor — курсор разбора из стора (0 — ещё не разбирали), chroniclePath —
/// путь хроники под pages/ по формуле сервиса, только если файл хроники есть на диске
/// (null: не-проектный дом, id, не представимый в пути, или файла нет — хронику создаёт
/// применённый разбор и снимает откат по источнику).</summary>
public sealed record WikiChatTriageDto(
    string SessionId, string? Name, string Zone, int? Score,
    IReadOnlyList<WikiTriageSignal>? Signals, string? Reason,
    int NothingNewStreak, DateTimeOffset TriagedAt,
    bool Running, int Cursor, string? ChroniclePath);

/// <summary>Итог пересчёта: сколько чатов посчитано и counts по зонам.</summary>
public sealed record WikiChatsRecalcDto(int Counted, IReadOnlyDictionary<string, int> Counts);
