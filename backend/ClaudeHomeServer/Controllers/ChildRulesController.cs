using System.Collections.Concurrent;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.ChildMode;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ClaudeHomeServer.Controllers;

/// <summary>
/// Онбординг правил детского режима: чат-интервью с закреплённым тьютором, в котором ребёнку
/// объясняют правила, уже выставленные админом (KM-B6b, ADR-041 «Контракт», ручки №6–7).
///
/// start идемпотентен: незавершённый чат онбординга (тот же поиск, что исключает его из списка
/// чатов KM-B6a: OnboardingKind = ChildRules) возвращается как есть, нового — новый чат с
/// закреплёнными полями как у KM-B6a. agree пишет ТОЛЬКО RulesAgreedAt/RulesSummary под тем же
/// локом UserStore, что и админский PUT: согласие не затирает лимиты, правка лимитов — согласие.
/// Сброс согласия — только админ (resetRules, KM-B1e).
///
/// Первое сообщение — та же процедура, что у OnboardingController.user/start: лок kickoff с
/// try-захватом, проверка немоты, системная директива тьютору — она пересказывает правила из
/// профиля, поэтому живёт здесь, а не в OnboardingPrompts (те статичны, правила — из стора).
/// apply-transcript сюда не перенесён: он переписывает персону, детская ручка пишет только
/// факт согласия (ADR-041 «Отвергнуто»).
///
/// Порядок отказов: start — 403 not_child → 503 policy_unavailable → 409 rules_already_agreed;
/// agree — 403 not_child → 400 summary_empty → 400 summary_too_long → 404 not_rules_chat →
/// 409 rules_already_agreed; если профиль пропал между проверкой согласия и записью —
/// 503 policy_unavailable. Необработанного исключения наружу нет: сбой чтения или создания — 503.
/// </summary>
[ApiController]
[Authorize]
[Route("api/child")]
public class ChildRulesController(
    IChildModePolicy policy,
    UserStore users,
    SessionManager sessions,
    ILogger<ChildRulesController> logger) : ControllerBase
{
    private string UserId => User.FindFirstValue(JwtRegisteredClaimNames.Sub)!;

    // Идемпотентность double-start (две вкладки): поиск и создание чата атомарны под
    // per-ключевым семафором по ownerId — иначе параллельные start породили бы два чата
    // онбординга. Статический реестр: контроллер per-request, а гонка межзапросная
    // (тот же приём, что у OnboardingController). Ключей: rules:{userId} — по числу детей,
    // kickoff:{chatId} — по числу чатов онбординга; не чистятся.
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Locks = new();

    private static SemaphoreSlim LockFor(string key) =>
        Locks.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));

    [ChildAllowed]
    [HttpPost("rules/start")]
    public async Task<IActionResult> Start()
    {
        if (policy.For(UserId) is not { } snapshot)
            return ChildError(403, "not_child", "Эта ручка только для детей.");
        if (snapshot.DenyAll || snapshot.Profile is null)
            return Unavailable();

        var gate = LockFor("rules:" + UserId);
        // Ожидание лока — вне try: отмена запроса в очереди не должна уходить необработанным
        // исключением в пайплайн.
        try { await gate.WaitAsync(HttpContext.RequestAborted); }
        catch (OperationCanceledException) { return BadRequest(new { code = "cancelled", message = "Операция отменена" }); }

        Session? chat = null;
        ChildProfile? profile = null;
        IActionResult? earlyReturn = null;
        try
        {
            // Перечитываем профиль под локом: параллельный start или resetRules админа могли
            // сменить состояние, пока этот запрос стоял в очереди.
            profile = policy.For(UserId)?.Profile;
            if (profile is null)
                earlyReturn = Unavailable();
            else if (profile.RulesAgreedAt is not null)
                earlyReturn = ChildError(409, "rules_already_agreed", "Правила уже согласованы — уроки открыты.");
            else
            {
                // Резюм: живой незавершённый чат онбординга возвращается как есть
                // (GetProjectlessChats уже отдаёт по UpdatedAt убыв. — берём последний).
                chat = sessions.GetProjectlessChats(UserId)
                    .FirstOrDefault(s => s.OnboardingKind == OnboardingKinds.ChildRules && s.ChildSpace != null);
                if (chat is null)
                {
                    // Закреплённые поля — как у чата «Уроки» (KM-B6a): сверка И1-бис и лимиты И3
                    // идут внутри старта хода; чат правил — единственное исключение И3 (ChildModeGate).
                    chat = await sessions.CreateChatAsync(UserId, ChildModeLaunch.RequiredMode,
                        model: profile.Model, personaId: profile.TutorPersonaId,
                        onboardingKind: OnboardingKinds.ChildRules, childSpace: ChildSpaces.Lessons,
                        providerKey: profile.ProviderKey);
                    // CanonicalizeModel мог переписать модель — закрепляем ровно профильную
                    if (chat.Model != profile.Model)
                    {
                        chat.Model = profile.Model;
                        sessions.SaveSessions();
                    }
                }
            }
        }
        catch (Exception e)
        {
            logger.LogWarning(e, "Детский режим: онбординг правил не открыт (owner {Owner})", UserId);
            earlyReturn = Unavailable();
        }
        finally { gate.Release(); }

        if (earlyReturn is not null) return earlyReturn;

        await EnsureKickoffAsync(chat!, profile!);
        return Ok(new { sessionId = chat!.Id });
    }

    [ChildAllowed]
    [HttpPost("rules/agree")]
    public IActionResult Agree([FromBody] ChildRulesAgreeRequest req)
    {
        if (policy.For(UserId) is null)
            return ChildError(403, "not_child", "Эта ручка только для детей.");

        // Бюджет ADR-041: 1..2000 символов; leading/trailing пробелы — не содержание.
        var summary = (req.Summary ?? "").Trim();
        if (summary.Length == 0)
            return ChildError(400, "summary_empty", "Скажи в двух словах, о чём договорились.");
        if (summary.Length > 2000)
            return ChildError(400, "summary_too_long", "Слишком длинно — хватит пары фраз.");

        // Чужой, не детский и не онбординг правил — одно тело 404 (существование не раскрываем)
        var chat = req.SessionId is { } sid ? sessions.GetOwnedVisible(sid, UserId) : null;
        if (chat is null || chat.OnboardingKind != OnboardingKinds.ChildRules || chat.ChildSpace is null)
            return ChildError(404, "not_rules_chat", "Это не чат правил.");

        if (policy.For(UserId)?.Profile?.RulesAgreedAt is not null)
            return ChildError(409, "rules_already_agreed", "Правила уже согласованы.");

        // Чтение-изменение-запись под локом UserStore (AgreeChildRules): пишутся ТОЛЬКО
        // RulesAgreedAt/RulesSummary живого профиля — лимиты и тьютор целы при параллельном
        // PUT админа. false — согласие уже поставил кто-то раньше (одна гонка — один 200)
        // или профиль пропал между проверкой выше и записью (отключение режима взрослым
        // посреди запроса). Ответ по живому состоянию: 409 только если согласовано на самом
        // деле, иначе 503 — ложного «уже согласовано» при пропавшем профиле нет.
        var at = DateTime.UtcNow;
        if (!users.AgreeChildRules(UserId, at, summary))
            return policy.For(UserId)?.Profile?.RulesAgreedAt is not null
                ? ChildError(409, "rules_already_agreed", "Правила уже согласованы.")
                : Unavailable();

        return Ok(new { rulesAgreedAt = at });
    }

    // Первый ход тьютора — та же процедура, что EnsureKickoffAsync у OnboardingController:
    // и создание, и резюм проходят через неё, без неё чат открывается «немым». Лок
    // kickoff:{id} с try-захватом: параллельный start, пока kickoff в полёте, уходит сразу.
    // Сбой kickoff не рушит start: чат уже создан, следующий start повторит попытку.
    private async Task EnsureKickoffAsync(Session session, ChildProfile profile)
    {
        var gate = LockFor("kickoff:" + session.Id);
        if (!await gate.WaitAsync(0))
            return; // kickoff уже в полёте (параллельный start) — второго не нужно
        try
        {
            // Перечитываем под локом: статус и история могли смениться с момента резолва в start
            var fresh = sessions.GetOwned(session.Id, UserId);
            if (fresh is null) return;
            var history = await sessions.GetHistoryAsync(fresh.Id);
            if (!IsMuteRulesChat(history, fresh.Status)) return;
            await sessions.SendMessageAsync(fresh.Id, RulesKickoffDirective(profile),
                [], systemDirective: true, senderUserId: UserId);
        }
        catch (Exception ex)
        {
            // Молчание недопустимо: в проде без лога причину «немого чата» не найти
            logger.LogError(ex, "Детский режим: сбой kickoff онбординга правил {SessionId}", session.Id);
        }
        finally { gate.Release(); }
    }

    // Критерий «чат немой» — тот же, что IsMuteOnboarding у OnboardingController: нет реплик
    // собеседника; нет неслужебных сообщений пользователя (затравка в истории есть, в ленте
    // невидима); статус не Working и не Waiting — без проверки статуса повторный start в окно
    // между доставкой и первым ответом задублировал бы первую реплику.
    private static bool IsMuteRulesChat(IReadOnlyList<StoredMessage> history, SessionStatus status) =>
        status is not (SessionStatus.Working or SessionStatus.Waiting)
        && !history.Any(m => m is StoredTextMessage)
        && !history.Any(m => m is StoredUserMessage { SystemDirective: not true });

    // Затравка тьютора: правила пересказываются из живого профиля — их выставил админ, тьютор
    // их объясняет, а не выставляет (ADR-041 §5). Текст фактов — из ChildProfile.Spaces и
    // бюджетов; формулировку для ребёнка тьютор строит сам.
    private static string RulesKickoffDirective(ChildProfile profile)
    {
        var sb = new StringBuilder();
        sb.AppendLine("[Системный старт онбординга правил] Ребёнок только что открыл знакомство с правилами «Уроков». " +
            "Правила уже выставил взрослый — тьютор их объясняет, а не выставляет. Факты такие:");
        var lessons = profile.Spaces.TryGetValue(ChildSpaces.Lessons, out var s) ? s : null;
        if (lessons?.Windows.Count > 0)
            foreach (var w in lessons.Windows)
            {
                var days = w.Weekdays is { Count: > 0 } wd
                    ? string.Join(", ", wd.Order().Select(d => WeekdayNames[d - 1]))
                    : "в любой день";
                sb.AppendLine($"— уроки разрешены в окно {w.From}–{w.To}, {days}.");
            }
        else
            sb.AppendLine("— окна уроков ещё не выставлены — честно скажи об этом и не выдумывай.");
        if (lessons?.DailyMinutes is { } minutes)
            sb.AppendLine($"— дневной предел присутствия: {minutes} мин.");
        if (profile.DailyBudgetUsd is { } daily)
            sb.AppendLine($"— дневной предел расхода: ${daily}.");
        if (profile.MonthlyBudgetUsd is { } monthly)
            sb.AppendLine($"— месячный предел расхода: ${monthly}.");
        sb.AppendLine("Начни первым: коротко поздоровайся и объясни эти правила простым языком — что можно, " +
            "когда, сколько времени; что случится, когда время или бюджет кончатся; что при «мне неприятно» " +
            "можно остановить всё кнопкой. Спроси, согласен ли ребёнок жить по этим правилам. Когда согласится — " +
            "сформулируйте вместе короткий итог (пара фраз о том, что запомнили): он сохранится как согласие. " +
            "Правила не меняй и не обещай изменить: их правит только взрослый. Ход за тобой, не жди реплики от ребёнка.");
        return sb.ToString().TrimEnd();
    }

    private static readonly string[] WeekdayNames = ["Пн", "Вт", "Ср", "Чт", "Пт", "Сб", "Вс"];

    private static JsonResult Unavailable() =>
        new(new { code = "policy_unavailable", message = "Настройки режима не читаются — попросите взрослого проверить." })
        { StatusCode = 503 };

    private static JsonResult ChildError(int status, string code, string message) =>
        new(new { code, message }) { StatusCode = status };
}

// Тело POST /api/child/rules/agree (ADR-041, ручка №7): summary 1..2000 символов.
public record ChildRulesAgreeRequest(string? SessionId, string? Summary);
