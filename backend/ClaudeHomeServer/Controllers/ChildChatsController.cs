using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.ChildMode;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ClaudeHomeServer.Controllers;

/// <summary>
/// Чат пространства «Уроки»: создание, список, голос (KM-B6a, ADR-041 «Контракт», ручки №3–5).
/// Владелец — только из JWT; ребёнок опознаётся по стору через IChildModePolicy (как у
/// ChildStatusController). Расчёт лимитов — только ChildLimitsCalculator.
///
/// Закреплённые поля нового чата (персона-тьютор, поставщик, модель, режим, ChildSpace,
/// ProjectId = null) берутся из профиля и обязаны пройти сверку И1-бис (ChildModeGate.LaunchMismatch)
/// на первом ходу. Голос — единственная настройка чата, доступная ребёнку: ручка пишет только
/// VoiceMode/VoiceStyle и не может тронуть закреплённые поля.
///
/// Порядок отказов: 403 not_child → 400 unknown_space → 503 policy_unavailable →
/// 403 space_disabled → (create) 409 по состоянию калькулятора. Необработанного исключения
/// наружу нет: сбой расчёта или создания — 503.
/// </summary>
[ApiController]
[Authorize]
[Route("api/child")]
public class ChildChatsController(
    IChildModePolicy policy,
    ChildLimitsCalculator limits,
    SessionManager sessions,
    ILogger<ChildChatsController> logger) : ControllerBase
{
    private string UserId => User.FindFirstValue(JwtRegisteredClaimNames.Sub)!;

    // Состояния калькулятора, при которых новый чат не создаётся (ADR-041, ручка №3)
    private static bool Blocks(ChildState s) => s is not (ChildState.Ok or ChildState.Warn);

    [ChildAllowed]
    [HttpPost("spaces/{space}/chat")]
    public async Task<IActionResult> Create(string space, [FromBody] ChildCreateChatRequest? req)
    {
        if (CheckSpace(space) is { } refused) return refused;
        // Один снимок профиля на запрос: предмет и поля чата берутся из одной и той же записи
        var profile = policy.For(UserId)!.Profile!;
        var subject = string.IsNullOrWhiteSpace(req?.Subject)
            ? null
            : profile.Subjects.FirstOrDefault(x => string.Equals(x.Id, req.Subject, StringComparison.OrdinalIgnoreCase));
        if (subject is null)
            return ChildError(400, "unknown_subject", "Такого предмета нет.");
        if (!subject.Enabled)
            return ChildError(403, "subject_disabled", "Этот предмет закрыт.");

        ChildStatus? status;
        try
        {
            status = limits.Evaluate(UserId, space, DateTimeOffset.UtcNow);
        }
        catch (Exception e)
        {
            logger.LogWarning(e, "Детский режим: лимиты не посчитались при создании чата (owner {Owner}, space {Space})", UserId, space);
            return Unavailable();
        }
        if (status is null) return ChildError(403, "not_child", "Эта ручка только для детей.");
        if (Blocks(status.State))
            return new JsonResult(new { code = ChildStateJsonConverter.ToWire(status.State), message = ChildLimitTexts.For(status.State) })
                { StatusCode = 409 };

        try
        {
            // Закреплённые поля уходят в создание: сверка И1-бис и лимиты И3 идут уже внутри старта сессии
            var chat = await sessions.CreateChatAsync(UserId, ChildModeLaunch.RequiredMode,
                model: subject.Model, personaId: subject.TutorPersonaId,
                childSpace: space, providerKey: subject.ProviderKey, childSubject: subject.Id);
            return StatusCode(201, new { sessionId = chat.Id });
        }
        catch (Exception e)
        {
            logger.LogWarning(e, "Детский режим: чат не создан (owner {Owner}, space {Space})", UserId, space);
            return Unavailable();
        }
    }

    [ChildAllowed]
    [HttpGet("spaces/{space}/chats")]
    public IActionResult List(string space)
    {
        if (CheckSpace(space) is { } refused) return refused;
        try
        {
            // Только свои детские чаты пространства вне проектов, без онбординга правил; новые сверху
            // (GetProjectlessChats уже отдаёт по UpdatedAt убыв., порядок фиксируем явно).
            var subjects = policy.For(UserId)!.Profile!.Subjects;
            var chats = sessions.GetProjectlessChats(UserId)
                .Where(s => s.ChildSpace == space && s.OnboardingKind != OnboardingKinds.ChildRules)
                .OrderByDescending(s => s.UpdatedAt)
                .Select(s =>
                {
                    // Один поиск записи предмета по id (без учёта регистра): из неё и признак
                    // закрытия, и подпись. Дубль id в профиле отвергает валидатор
                    // (ChildProfileValidator.ValidateSubjects: !ids.Add(id) — отказ при повторном id),
                    // поэтому совпадение не более одного.
                    var subject = subjects.FirstOrDefault(x =>
                        string.Equals(x.Id, s.ChildSubject, StringComparison.OrdinalIgnoreCase));
                    return new
                    {
                        sessionId = s.Id, name = s.Name, updatedAt = s.UpdatedAt,
                        subject = s.ChildSubject,
                        // Предмет выключен или удалён: чат остаётся в списке, но только для чтения
                        subjectClosed = subject is null || !subject.Enabled,
                        // Название предмета из профиля — включённого или выключенного: строка
                        // «Старых уроков» подписана даже без предмета в GET /api/child/subjects (ХВ2-Б1).
                        // null — предмета у чата нет или он удалён из профиля.
                        subjectLabel = subject?.Label,
                        // Озвучка чата: оболочка восстанавливает её при открытии (Т3c, субзадача
                        // из отчёта Т3a — без этого поля старая озвучка терялась при открытии)
                        voiceMode = s.VoiceMode,
                    };
                })
                .ToList();
            return Ok(new { chats });
        }
        catch (Exception e)
        {
            logger.LogWarning(e, "Детский режим: список чатов не прочитан (owner {Owner}, space {Space})", UserId, space);
            return Unavailable();
        }
    }

    [ChildAllowed]
    [HttpPut("chats/{id}/voice")]
    public IActionResult Voice(string id, [FromBody] ChildVoiceRequest req)
    {
        if (policy.For(UserId) is null)
            return ChildError(403, "not_child", "Эта ручка только для детей.");
        if (req.Style is not null && !VoiceStyles.IsKnown(req.Style))
            return ChildError(400, "invalid_style", "Неизвестный стиль озвучки.");

        // Чужой, не детский и несуществующий — одно тело 404 (существование не раскрываем)
        var chat = sessions.GetOwnedVisible(id, UserId);
        if (chat is null || chat.ProjectId is not null || chat.ChildSpace is null)
            return ChildError(404, "chat_not_found", "Чат не найден.");

        try
        {
            // Только голос: SetVoiceMode пишет VoiceMode/VoiceStyle под замком SessionManager и
            // не касается закреплённых полей.
            var updated = sessions.SetVoiceMode(id, req.Enabled, req.Style);
            if (updated is null) return ChildError(404, "chat_not_found", "Чат не найден.");
            return Ok(new { voiceMode = updated.VoiceMode, voiceStyle = VoiceStyles.Normalize(updated.VoiceStyle) });
        }
        catch (Exception e)
        {
            logger.LogWarning(e, "Детский режим: голос чата не записан (owner {Owner}, chat {Chat})", UserId, id);
            return Unavailable();
        }
    }

    // Общие отказы по пространству (ручки №3 и №4); null — можно продолжать, профиль читается
    private IActionResult? CheckSpace(string space)
    {
        var snapshot = policy.For(UserId);
        if (snapshot is null)
            return ChildError(403, "not_child", "Эта ручка только для детей.");
        if (space != ChildSpaces.Lessons)
            return ChildError(400, "unknown_space", "Неизвестное пространство.");
        if (snapshot.DenyAll || snapshot.Profile is null)
            return Unavailable();
        if (!snapshot.Profile.Spaces.ContainsKey(space))
            return ChildError(403, "space_disabled", "Это пространство тебе не открыто.");
        return null;
    }

    private static JsonResult Unavailable() =>
        ChildError(503, "policy_unavailable", "Настройки режима не читаются — попросите взрослого проверить.");

    private static JsonResult ChildError(int status, string code, string message) =>
        new(new { code, message }) { StatusCode = status };
}

// Тело POST /api/child/spaces/{space}/chat (ADR-041 ред. 2): предмет — ключ ChildSubject.Id.
public record ChildCreateChatRequest(string? Subject);

// Тело PUT /api/child/chats/{id}/voice (ADR-041, ручка №5): style необязателен.
public record ChildVoiceRequest(bool Enabled, string? Style = null);
