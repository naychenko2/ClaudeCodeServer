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
/// Урок — чат проекта-предмета этого ребёнка (Б9b, решение Андрея 19.11, ADR-041 ред. 8):
/// POST …/chat — ЕДИНСТВЕННАЯ точка его создания. Проект и его руководитель обеспечиваются
/// провижером предмета (Б17a, идемпотентен); персона чата — руководитель проекта
/// (Project.DefaultPersonaId), cwd — RootPath проекта, PrivateToUserId — null («куратор видит
/// все уроки», Б9a отказывает старт скрытому уроку). Ребёнку закрыт и тумблер приватности
/// общей ручки: PUT /api/chats/{id}/visibility не помечен [ChildAllowed] → 403 гейтом И1.
///
/// Поставщик и модель закрепляются по живому маршруту (тот же вызов, что у догона на старте
/// хода) — не по полям предмета: с Б17a репетитор — руководитель проекта, его модель и есть
/// модель урока (И10). Голос — единственная настройка чата, доступная ребёнку: ручка пишет
/// только VoiceMode/VoiceStyle и не может тронуть закреплённые поля.
///
/// Список «Уроков» отдаёт живые уроки (чаты проектов-предметов ребёнка) и «старые уроки» —
/// детские чаты вне проекта, созданные до Б9b: их старт отказан Б9a, в ответе они с readOnly.
///
/// Порядок отказов: 403 not_child → 400 unknown_space → 503 policy_unavailable →
/// 403 space_disabled → 400 unknown_subject → 403 subject_disabled → 409 по состоянию
/// калькулятора → 409 subject_not_ready (провижн предмета невозможен). Необработанного
/// исключения наружу нет: сбой расчёта, провижна или создания — 503.
/// </summary>
[ApiController]
[Authorize]
[Route("api/child")]
// projects + tutors (Б9b): проект-предмет и его руководитель — провижер предмета (Б17a);
// gate (И10, И2′): живой маршрут урока — единственная точка, которая знает модель и
// поставщика репетитора (LiveRouteFor — тот же вызов, что у догона и сверки на старте хода).
public class ChildChatsController(
    IChildModePolicy policy,
    IChildModeGate gate,
    ChildLimitsCalculator limits,
    SessionManager sessions,
    ProjectManager projects,
    ChildTutorProvisioner tutors,
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

        // Б9b: урок — чат проекта-предмета от лица его руководителя. Проект и руководитель
        // обеспечивает провижер предмета: это точка записи урока (его создания), а не GET —
        // тот же порядок, что у точек записи самого предмета. null — провижна невозможна
        // (дом ребёнка не настроен, предмет исчез под параллельной правкой профиля):
        // 409 subject_not_ready, необработанного исключения наружу нет.
        Project subjectProject;
        try
        {
            if (await tutors.EnsureSubjectTutorAsync(UserId, subject.Id) is null)
                return SubjectNotReady();
            var found = SubjectProjectOf(UserId, subject.Id);
            // Руководитель обеспечен выше; своего проекта под ним нет только если его снесли
            // между двумя чтениями (удаление ребёнка или предмета) — тот же отказ.
            if (found is null || string.IsNullOrWhiteSpace(found.DefaultPersonaId))
                return SubjectNotReady();
            subjectProject = found;
        }
        catch (Exception e)
        {
            logger.LogWarning(e, "Детский режим: проект-предмет не обеспечен при создании урока (owner {Owner}, предмет {Subject})", UserId, subject.Id);
            return Unavailable();
        }

        // Живой маршрут урока (И10, И2′) — до создания чата: он даёт и модель, и поставщика,
        // а сверка И1-бис читает их уже в момент старта сессии; после старта их уже не поправить
        // без лишнего хода. Развёртку несёт гейт (та же точка, что у догона и у сверки запуска):
        // своей копии развёртки модели в контроллере нет. Заготовка — чат-мерка: LiveRouteOf
        // смотрит на детскую метку и OnboardingKind, только их она и несёт. null — репетитора
        // или его развёрнутой модели нет: тот же 409 subject_not_ready, без исключения.
        var probe = new Session { OwnerId = UserId, ChildSpace = space, ChildSubject = subject.Id };
        if (gate.LiveRouteFor(UserId, probe) is not { } route)
            return SubjectNotReady();

        try
        {
            // Метка, модель и закреплённый поставщик идут в саму фабрику: И1-бис читает их
            // в момент старта сессии, после — уже поздно.
            var chat = await sessions.CreateAsync(subjectProject.Id, ChildModeLaunch.RequiredMode,
                model: route.Model, personaId: route.PersonaId, providerKey: route.ProviderKey,
                createdByUserId: UserId, childSpace: space, childSubject: subject.Id);
            return StatusCode(201, new { sessionId = chat.Id });
        }
        catch (Exception e)
        {
            logger.LogWarning(e, "Детский режим: урок не создан (owner {Owner}, space {Space})", UserId, space);
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
            // Живые уроки — чаты проектов-предметов ребёнка (Б9b): все его предметы-проекты,
            // включая удалённые из профиля (их проекты по ADR-041 живут — уроки остаются
            // видимыми). GetVisibleByProject — человеческая выборка проекта (S2d-4a): чужой
            // приватный чат в список ребёнка не приходит. Без онбординга правил, новые сверху.
            var subjects = policy.For(UserId)!.Profile!.Subjects;
            var live = projects.GetByOwner(UserId)
                .Where(ProjectCapabilities.IsChildSubject)
                .SelectMany(p => sessions.GetVisibleByProject(p.Id, UserId))
                .Where(s => s.ChildSpace == space && s.OnboardingKind != OnboardingKinds.ChildRules)
                .Select(s => (chat: s, readOnly: false));

            // «Старые уроки» — детские чаты вне проекта, созданные до Б9b: их старт отказан
            // Б9a («старый урок только для чтения»), поэтому они только для чтения и в ответе
            // идут с readOnly.
            var legacy = sessions.GetProjectlessChats(UserId)
                .Where(s => s.ChildSpace == space && s.OnboardingKind != OnboardingKinds.ChildRules)
                .Select(s => (chat: s, readOnly: true));

            var chats = live.Concat(legacy)
                .OrderByDescending(x => x.chat.UpdatedAt)
                .Select(row =>
                {
                    var s = row.chat;
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
                        // Живой урок (чаты проекта-предмета) — false; «старый урок» вне проекта —
                        // true: его старт отказан Б9a, оболочка открывает его только для чтения
                        // и не показывает кнопку «продолжить».
                        readOnly = row.readOnly,
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

        // Чужой, не детский и несуществующий — одно тело 404 (существование не раскрываем).
        // Урок Б9b — чат проекта-предмета, поэтому ProjectId здесь не признак «не детский»:
        // граница одна — ChildSpace (тот же, что у списка и сверки И1-бис).
        var chat = sessions.GetOwnedVisible(id, UserId);
        if (chat is null || chat.ChildSpace is null)
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

    // Проект-предмета этого предмета — только свои (per-owner): тот же поиск, что у
    // ChildModeGate.LiveRouteOf и ChildTutorProvisioner.SubjectProject (чужой проект-предмет
    // с тем же slug предмета — не этот случай).
    private Project? SubjectProjectOf(string childId, string subjectId) =>
        projects.GetByOwner(childId).FirstOrDefault(p => ProjectCapabilities.IsChildSubject(p)
            && string.Equals(p.ChildSubjectId, subjectId, StringComparison.OrdinalIgnoreCase));

    private static JsonResult SubjectNotReady() =>
        ChildError(409, "subject_not_ready", "Этот предмет ещё не готов — попросите взрослого проверить его настройки.");

    private static JsonResult Unavailable() =>
        ChildError(503, "policy_unavailable", "Настройки режима не читаются — попросите взрослого проверить.");

    private static JsonResult ChildError(int status, string code, string message) =>
        new(new { code, message }) { StatusCode = status };
}

// Тело POST /api/child/spaces/{space}/chat (ADR-041 ред. 2): предмет — ключ ChildSubject.Id.
public record ChildCreateChatRequest(string? Subject);

// Тело PUT /api/child/chats/{id}/voice (ADR-041, ручка №5): style необязателен.
public record ChildVoiceRequest(bool Enabled, string? Style = null);
