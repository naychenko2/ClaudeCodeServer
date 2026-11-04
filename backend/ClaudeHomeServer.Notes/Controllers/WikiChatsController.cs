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
// а не на мёртвый путь. С N1b (Ф2) список несёт и поле mark — пометку человека с чата
// (WikiMarks.Important/Skip, null — нет): её ставит PUT {sessionId}/mark этого же
// контроллера или откат по источнику; живёт она на чате (Session.WikiMark), не в сторе,
// и зон сама не меняет — ступени отбора и разбор читают её с чата.
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

    // Признак сервисного токена (B2b, Ф2): тот же claim-литерал, что в PluginsController/
    // TasksController (MapInboundClaims=false оставляет raw-имена — см. PluginsController:39-44).
    // Пересчёт, пометка и разбор — действия человека: сервисный токен (MCP-обвязка хода)
    // на всех трёх мутирующих ручках отказывается 403 — до флага, до чата и до любой записи;
    // GET списка и analyze-status остаются читающими.
    private const string TokenKindClaim = "typ";
    private const string ServiceTokenKind = "svc";
    private bool IsServiceToken => User.FindFirstValue(TokenKindClaim) == ServiceTokenKind;

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
    // nothing_new), зона ступени 1 — пересчёту (stage1Zone), вердикт ступени 2 — модели
    // (stage2Zone). Приоритет: nothing_new > ступень 2 > ступень 1. Вердикт ступени 2 живёт
    // только пока ступень 1 держит чат серым: пересчёт увёл его в skipped/excluded (человек
    // поставил skip) — skip побеждает, устаревший вердикт не показывается. Для записей до
    // разведения владения (пересчёт писал общий zone, stage1Zone нет) — прежний zone.
    private static bool Stage2Applies(WikiChatTriageEntry e) =>
        e.Stage2Zone is not null && e.Stage1Zone == WikiChatScorer.ZoneGray;

    internal static string ShownZone(WikiChatTriageEntry e) =>
        e.Zone == WikiChatIngestService.ZoneNothingNew
            ? e.Zone
            : Stage2Applies(e) ? e.Stage2Zone! : e.Stage1Zone ?? e.Zone ?? "";

    internal static string? ShownReason(WikiChatTriageEntry e) =>
        e.Zone == WikiChatIngestService.ZoneNothingNew ? e.Reason
            : Stage2Applies(e) ? e.Stage2Reason ?? e.Stage1Reason
            : e.Stage1Reason ?? e.Reason;

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
    /// ПОКАЗАННОЙ зоне (ShownZone). Записи несуществующего, уже чужого или приватного чата
    /// (PrivateToUserId, FIN3-3) из списка выпадают (список — про чаты, а не про файл), на
    /// counts их тоже нет: имя, зона и причина приватного чата — машинная производная от его
    /// содержания, а машинные потребители приватный чат не читают. Нечитаемый стор — 409 с
    /// кодом: список не притворяется пустым там, где файл сломан.</summary>
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
            // Приватный чат (FIN3-3): запись о нём выпадает из списка и из counts так же, как
            // запись несуществующего, — при любом запросившем (тот же фильтр, что у BuildQueue
            // и AnalyzeAsync: машинные потребители приватный чат не читают).
            if (session is null || _sessions.ResolveOwnerId(session) != userId || session.PrivateToUserId is not null)
                continue;
            var zone = ShownZone(e);
            counts[zone] = counts.TryGetValue(zone, out var c) ? c + 1 : 1;
            // Дом чата — та же формула, что у разбора (AnalyzeAsync): в записи стора дома нет,
            // он выводится из чата. Дом и путь — из формул сервиса, не отсюда.
            items.Add(new WikiChatTriageDto(
                e.SessionId, session.Name, zone, e.Score, e.Signals, ShownReason(e),
                e.NothingNewStreak, e.TriagedAt,
                _chat.IsRunning(userId, e.SessionId), e.Cursor,
                ChroniclePathIfExists(WikiChatIngestService.HomeOf(session), e.SessionId, userId),
                // Пометка — свойство чата (его ставит PUT mark или откат по источнику),
                // список показывает её для кнопок в строке: null — пометки нет.
                session.WikiMark));
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
    /// WikiChatsRecalcDto) — прежние строки отказа проходят как есть. С N1b (Ф2) тот же
    /// контроллер обслуживает пометку чата (PUT {sessionId}/mark): «важный» / «не разбирать» /
    /// снять — настройка на чате (Session.WikiMark), не в сторе; зон она сама не меняет,
    /// она кормит ими ступени отбора и разбор.</summary>
    [HttpPost("recalculate")]
    public async Task<IActionResult> Recalculate()
    {
        var userId = OwnerOrNull();
        if (userId is null) return Unauthorized();
        // B2b: пересчёт по кнопке — действие человека; отказ по типу токена раньше
        // обращения к службе (стор не будет ни прочитан, ни записан).
        if (IsServiceToken)
            return StatusCode(403, new { error = "Сервисный JWT: пересчёт запускает только пользовательский токен" });

        var res = await _recalc.RecalculateAsync(userId);
        if (!res.Ok)
            return StatusCode(409, new { code = res.Code, message = res.Reason });
        return Ok(new WikiChatsRecalcDto(res.Counted, res.Counts));
    }

    private const string CodeLlmWikiOff = "wiki_llm_wiki_off";
    private const string CodeMarkInvalid = "wiki_mark_invalid";

    /// <summary>PUT /api/wiki/chats/{sessionId}/mark — пометка чата человеком (N1b, ADR-035 §12а):
    /// «важный» (WikiMarks.Important — разбирается всегда, минуя ступени 1–2 отбора), «не
    /// разбирать» (WikiMarks.Skip — никогда; ту же пометку ставит откат по источнику, §12г мера 5)
    /// или null — снять. Тело — {mark: "important"|"skip"|null}; пустое тело — 400 (намерения нет).
    /// Порядок проверок — как у analyze: токен → сервисный токен (403, B2b: пометку ставит
    /// только человек) → флаг llm-wiki (409, при отказе пометка не
    /// меняется) → свой чат (чужой и несуществующий — один 404 с одним текстом, как у
    /// analyze-status) → значение из WikiMarks (иначе 400). Пометка живёт на чате
    /// (ISessionDirectory.SetWikiMark → Session.WikiMark), не в сторе: список показывает её
    /// полем mark, ступени отбора и разбор читают её оттуда же.
    /// Конкурентность: две смены пометки — последняя побеждает (поле одно, SetWikiMark атомарен
    /// в SessionManager); пометка, поставленная во время идущего разбора, его не прерывает —
    /// ничто в прогоне её не отменяет, а следующая заявка (и ночь) прочитает новую пометку
    /// на своей проверке начала: skip будет отвергнут с marked_skip до обращения к модели. UpdatedAt
    /// не двигается: пометка — настройка чата, а не активность (SessionManager.SetWikiMark).</summary>
    [HttpPut("{sessionId}/mark")]
    public IActionResult Mark(string sessionId, [FromBody] WikiChatMarkRequest? req)
    {
        var userId = OwnerOrNull();
        if (userId is null) return Unauthorized();
        // B2b: пометку ставит только человек — отказ по типу токена раньше флага и чата.
        if (IsServiceToken)
            return StatusCode(403, new { error = "Сервисный JWT: пометку чата ставит только пользовательский токен" });
        if (_flags?.IsEnabled(userId, FeatureFlagKeys.LlmWiki) != true)
            return StatusCode(409, new { code = CodeLlmWikiOff, message = "пометка не сохранена: функция выключена" });

        var session = _sessions.GetById(sessionId);
        if (session is null || _sessions.ResolveOwnerId(session) != userId)
            return NotFound(new { code = WikiChatIngestService.CodeNotFound, message = "чат не существует" });

        if (req is null)
            return BadRequest(new { code = CodeMarkInvalid, message = "нужно тело {mark: important, skip или null}" });
        var mark = req.Mark;
        if (mark is not null && mark != WikiMarks.Important && mark != WikiMarks.Skip)
            return BadRequest(new { code = CodeMarkInvalid, message = "пометка: important, skip или null (снять)" });

        if (!_sessions.SetWikiMark(sessionId, mark))
            // Чат исчез между проверкой владельца и записью (гонка с удалением чата):
            // тот же 404, что и выше; неизвестная метка сюда не доходит — она отвергнута выше.
            return NotFound(new { code = WikiChatIngestService.CodeNotFound, message = "чат не существует" });
        return Ok(new { mark });
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
        // B2b: «разобрать этот чат» — кнопка человека; отказ по типу токена раньше
        // флага и обращения к модели.
        if (IsServiceToken)
            return StatusCode(403, new { error = "Сервисный JWT: разбор чата запускает только пользовательский токен" });
        // Разбор тратит модель: при выключенном llm-wiki не стартует. Флаг может смениться
        // между этой проверкой и ходом модели — допустимо; уже идущий разбор не прерывается.
        if (_flags?.IsEnabled(userId, FeatureFlagKeys.LlmWiki) != true)
            return StatusCode(409, new { code = CodeLlmWikiOff, message = "разбор не запущен: функция выключена" });

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
/// применённый разбор и снимает откат по источнику); mark — пометка человека с чата
/// (WikiMarks.Important/Skip, null — пометки нет; её ставит PUT mark или откат по
/// источнику, поле нужно фронту для кнопок в строке списка).</summary>
public sealed record WikiChatTriageDto(
    string SessionId, string? Name, string Zone, int? Score,
    IReadOnlyList<WikiTriageSignal>? Signals, string? Reason,
    int NothingNewStreak, DateTimeOffset TriagedAt,
    bool Running, int Cursor, string? ChroniclePath, string? Mark);

/// <summary>Итог пересчёта: сколько чатов посчитано и counts по зонам.</summary>
public sealed record WikiChatsRecalcDto(int Counted, IReadOnlyDictionary<string, int> Counts);

/// <summary>Тело PUT mark: mark — «important» / «skip» / null (снять пометку). Других
/// полей нет; неизвестное значение — 400 (валидация до записи, как в SetWikiMark).</summary>
public sealed class WikiChatMarkRequest
{
    public string? Mark { get; set; }
}
