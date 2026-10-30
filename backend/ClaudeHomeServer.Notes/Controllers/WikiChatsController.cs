using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Notes;
using ClaudeHomeServer.Services.Notes.Wiki;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ClaudeHomeServer.Notes.Controllers;

// «Отбор чатов» вики (S6f, ADR-035, решение 12а): список «разбирается / серая зона /
// пропущено / нечего добавить» по каждому чату с причиной (балл и главные сигналы либо
// правило ступени 0) и пересчёт по кнопке. ТЕНЕВОЙ РЕЖИМ шага: список и пересчёт ничего
// не запускают — ни ступени 2 (дешёвой модели), ни разбора: разбор остаётся кнопкой в Ф1,
// пометки в меню чата — Ф2. Поэтому у контроллера нет зависимости ни от ICheapTextRunner,
// ни от WikiChatIngestService: он не может позвать модель, даже если бы хотел.
//
// Образец — WikiPackagesController: [Authorize], владелец только из токена, коды отказов —
// контракт для фронта. Состояние читается из стора keeper/chat-triage.json (S6e): список
// показывает то, что уже посчитано; несчитанные чаты в список не встают — их посчитает
// пересчёт. Пересчёт обновляет только поля балла (UpdateScoring): курсор разбора ему не
// принадлежит (его двигает разбор), nothingNewStreak — его ведёт исход разбора.
[ApiController]
[Authorize]
[Route("api/wiki/chats")]
public class WikiChatsController : ControllerBase
{
    private readonly ISessionDirectory _sessions;
    private readonly string _dataDir;

    public WikiChatsController(ISessionDirectory sessions, IConfiguration config)
    {
        _sessions = sessions;
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

    /// <summary>GET /api/wiki/chats — список отбора по своей вики: запись стора на каждый
    /// существующий свой чат, с причиной и сигналами; counts — число чатов по каждой зоне.
    /// Записи несуществующего или уже чужого чата из списка выпадают (список — про чаты,
    /// а не про файл), но на counts несуществующего чата нет. Нечитаемый стор — 409 с кодом:
    /// список не притворяется пустым там, где файл сломан.</summary>
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
            var zone = e.Zone ?? "";
            counts[zone] = counts.TryGetValue(zone, out var c) ? c + 1 : 1;
            items.Add(new WikiChatTriageDto(
                e.SessionId, session.Name, zone, e.Score, e.Signals, e.Reason,
                e.NothingNewStreak, e.TriagedAt));
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
    /// ни модели, ни разбора (теневой режим). Курсор и nothingNewStreak существующих записей
    /// сохраняются. 409 — нечитаемый стор (пересчёт его не перезапишет молча). Успех — число
    /// пересчитанных чатов и counts по зонам.</summary>
    [HttpPost("recalculate")]
    public async Task<IActionResult> Recalculate()
    {
        var userId = OwnerOrNull();
        if (userId is null) return Unauthorized();

        var root = WikiPaths.Root(_dataDir, userId);
        var read = WikiChatTriageStore.Get(root);
        if (read.State == WikiTriageFileState.Invalid)
            return StatusCode(409, new { code = WikiChatTriageStore.CodeInvalid, message = "файл разбора чатов не читается" });
        var streaks = new Dictionary<string, int>();
        foreach (var e in read.Entries)
            streaks[e.SessionId] = e.NothingNewStreak;

        // Все свои чаты, включая архивные: ADR-035 §12в считает бэклог «по всем чатам
        // владельца»; архивность — способ хранения, не признак бессмысленности.
        var counts = new Dictionary<string, int>();
        int counted = 0;
        foreach (var session in _sessions.GetAll().Where(s => _sessions.ResolveOwnerId(s) == userId))
        {
            var history = await _sessions.GetHistoryAsync(session.Id);
            var res = WikiChatScorer.Score(new WikiChatScorer.WikiChatInput(
                session.TaskExecution, session.WikiMark, session.IsPinned,
                session.SummaryNoteId is not null,
                session.ContinuedFromSessionId, session.BranchedFromSessionId,
                session.CreatedAt, session.UpdatedAt,
                streaks.TryGetValue(session.Id, out var st) ? st : 0,
                history));
            var up = WikiChatTriageStore.UpdateScoring(
                root, session.Id, res.Score,
                [.. res.Signals.Select(s => new WikiTriageSignal(s.Name, s.Weight))],
                res.Zone, res.Reason);
            if (!up.Ok)
                return StatusCode(409, new { code = up.Code, message = up.Reason });
            counts[res.Zone] = counts.TryGetValue(res.Zone, out var c) ? c + 1 : 1;
            counted++;
        }
        return Ok(new WikiChatsRecalcDto(counted, counts));
    }
}

/// <summary>Список отбора: записи по чатам (порядок — по зонам, внутри зоны — по баллу) и
/// числа по зонам.</summary>
public sealed record WikiChatsListDto(IReadOnlyList<WikiChatTriageDto> Items, IReadOnlyDictionary<string, int> Counts);

/// <summary>Строка списка: чат, его зона, балл и сигналы (null — балла ещё нет), причина для
/// человека, счётчик «нечего добавить».</summary>
public sealed record WikiChatTriageDto(
    string SessionId, string? Name, string Zone, int? Score,
    IReadOnlyList<WikiTriageSignal>? Signals, string? Reason,
    int NothingNewStreak, DateTimeOffset TriagedAt);

/// <summary>Итог пересчёта: сколько чатов посчитано и counts по зонам.</summary>
public sealed record WikiChatsRecalcDto(int Counted, IReadOnlyDictionary<string, int> Counts);
