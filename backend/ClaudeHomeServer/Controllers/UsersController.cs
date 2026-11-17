using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text.RegularExpressions;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Auth;
using ClaudeHomeServer.Services.ChildMode;
using ClaudeHomeServer.Services.Composition;
using ClaudeHomeServer.Services.Knowledge;
using ClaudeHomeServer.Services.Llm;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ClaudeHomeServer.Controllers;

[ApiController]
[Route("api/users")]
[Authorize(Roles = "admin")]
public class UsersController(UserStore users, SessionManager sessions,
    UserKnowledgeCascade knowledgeCascade, DefaultAssistantProvisioner provisioner,
    ProjectMembershipService membership, IPersonalProjects personalProjects,
    ChildTutorProvisioner childTutorProvisioner, ClaudeSubscriptionPool subscriptionPool,
    ILogger<UsersController> logger) : ControllerBase
{
    private static readonly Regex UsernameRegex = new(@"^[a-zA-Z0-9_-]+$", RegexOptions.Compiled);

    [HttpGet]
    public IActionResult GetAll()
    {
        var dtos = users.GetAll().Select(ToDto);
        return Ok(dtos);
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateUserRequest req)
    {
        var validationError = ValidateUsername(req.Username)
                           ?? ValidatePassword(req.Password)
                           ?? ValidateRole(req.Role)
                           ?? ValidateExecutionEnvironment(req.ExecutionEnvironment);
        if (validationError is not null) return BadRequest(new { error = validationError });

        try
        {
            var user = users.Add(req.Username, req.Password, req.Role,
                req.ExecutionEnvironment ?? ExecutionEnvironments.Local);
            // Провижн ассистента новому пользователю (план 2.3): новичок сразу получает
            // ассистента при заведении, без отдельного шага. Идемпотентно — повторный
            // вызов на первом входе вернёт уже созданную заготовку.
            await provisioner.EnsureAsync(user.Id);
            return CreatedAtAction(nameof(GetAll), ToDto(user));
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(new { error = ex.Message });
        }
    }

    [HttpPut("{id}")]
    public IActionResult Update(string id, [FromBody] UpdateUserRequest req)
    {
        if (req.Username is not null)
        {
            var err = ValidateUsername(req.Username);
            if (err is not null) return BadRequest(new { error = err });
        }

        if (req.Role is not null)
        {
            var err = ValidateRole(req.Role);
            if (err is not null) return BadRequest(new { error = err });
        }

        var envChange = req.ExecutionEnvironment;
        if (envChange is not null)
        {
            var err = ValidateExecutionEnvironment(envChange);
            if (err is not null) return BadRequest(new { error = err });

            // Среда фиксируется после появления чатов: корни проектов и профили сред различаются,
            // resume-транскрипты привязаны к путям старой среды (аналог guard'а смены провайдера)
            var existing = users.GetById(id);
            if (existing is not null && existing.ExecutionEnvironment != envChange
                && sessions.HasSessionsOwnedBy(id))
                return Conflict(new { error = "Нельзя сменить среду исполнения: у пользователя уже есть чаты. Удалите их и повторите." });
        }

        try
        {
            if (!users.Update(id, req.Username, req.Role, envChange))
                return NotFound();

            var user = users.GetById(id)!;
            return Ok(ToDto(user));
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpDelete("{id}")]
    public async Task<IActionResult> Delete(string id)
    {
        // Нельзя удалить самого себя
        var currentUserId = User.FindFirstValue(JwtRegisteredClaimNames.Sub);
        if (id == currentUserId)
            return BadRequest(new { error = "Нельзя удалить собственную учётную запись" });

        // Владелец общих проектов с живыми участниками: пока они есть, учётку не удаляем
        if (membership.OwnerHasMembers(id))
            return Conflict(new
            {
                error = "У пользователя есть общие проекты с участниками: сначала уберите участников.",
                code = "owner_has_members",
            });

        var user = users.GetById(id);
        try
        {
            if (!users.Delete(id))
                return NotFound();
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }

        // Каскад знаний: датасеты «{username}:…», локальные сторы, персоны. Без него всё это
        // сиротеет, а новый пользователь с тем же именем увидел бы чужие базы как свои.
        if (user is not null)
            try { await knowledgeCascade.CleanupAsync(user.Id, user.Username); }
            catch (Exception ex) { logger.LogWarning(ex, "Каскад знаний при удалении пользователя {User}", id); }

        // «Личное»: запись стора удаляется, папка на диске остаётся (ADR-040 §3)
        try { personalProjects.DeleteOf(id); }
        catch (Exception ex) { logger.LogWarning(ex, "Удаление записи «Личного» пользователя {User}", id); }

        return NoContent();
    }

    [HttpPut("{id}/password")]
    public IActionResult ResetPassword(string id, [FromBody] ResetPasswordRequest req)
    {
        var err = ValidatePassword(req.NewPassword);
        if (err is not null) return BadRequest(new { error = err });

        if (!users.ResetPassword(id, req.NewPassword))
            return NotFound();

        return NoContent();
    }

    /// <summary>
    /// Детский профиль пользователя (ADR-041 «Контракт», ручка №10; KM-B1e): включает и правит
    /// режим только админ. Роль — и по claim ([Authorize] класса), и по стору (AdminByStore):
    /// сервисный токен хода — всегда роль "user", без политики он к админ-ручке не допускается,
    /// а снятое админство не ждало бы истечения токена. [ChildAllowed] здесь нет намеренно:
    /// ребёнок свой профиль не правит (тот же приём, что у AdminUserModelTiersController).
    /// Тело — ChildProfileDto или { enabled: false } для выключения. Ответ 200 { profile,
    /// tutorPersonaId }; отказы — { code, message } с кодами ADR (фронт переводит code сам).
    /// Правила (RulesAgreedAt/RulesSummary) из тела не принимаются: их пишет только детский
    /// rules/agree (KM-B6b); resetRules=true — сбросить оба.
    /// </summary>
    [HttpPut("{id}/child-profile")]
    [Authorize(Policy = AdminByStoreRequirement.PolicyName)]
    public async Task<IActionResult> SetChildProfile(string id, [FromBody] ChildProfileDto req)
    {
        var user = users.GetById(id);
        if (user is null)
            return NotFound(new { code = "user_not_found", message = "Пользователь не найден." });

        // Выключение — без проверок тела: отказы таблицы ADR — проверки «при включении».
        // Профиль при этом сохраняется (Enabled=false): тело {enabled:false} просит выключить
        // режим, а не стереть выставленные админом лимиты; политика «профиль выключен» =
        // обычный пользователь (ChildModePolicy).
        if (!req.Enabled)
        {
            users.DisableChildMode(id);
            var off = users.GetById(id)!;
            return Ok(new ChildProfileResponse(ToChildProfileDto(off.ChildMode), off.ChildMode?.TutorPersonaId ?? ""));
        }

        if (ChildProfileValidator.Validate(req, user, users, subscriptionPool) is { } reject)
            return BadRequest(new { code = reject.Code, message = reject.Message });

        // Снимок прежнего профиля — ради отката при отказе провижнера («профиль не сохранён»).
        var previous = user.ChildMode;
        // Целый новый профиль, мутация — под локом стора: RulesAgreedAt/RulesSummary стор
        // возьмёт из живого профиля, а не из нашего снимка (иначе затёрся бы параллельный
        // agree ребёнка), и два одновременных PUT дают один целый профиль, не смесь полей.
        users.SetChildProfile(id, ToChildProfile(req), resetRules: req.ResetRules == true);

        // Тьютор провится после записи: EnsureAsync закрепляет id в существующем профиле
        // (SetChildTutorPersona) и идемпотентен — повторное включение возвращает живого тьютора.
        string? tutorId;
        try
        {
            tutorId = await childTutorProvisioner.EnsureAsync(id);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Детский режим: провижн тьютора не удался для {User}", id);
            users.SetChildProfile(id, previous, resetRules: false);
            return StatusCode(500, new { code = "tutor_provision_failed", message = "Не удалось создать персону-тьютора; профиль не изменён." });
        }
        // null — провижн невозможен (пользователь или профиль исчезли на параллельной правке,
        // запрос отменён): ребёнок без тьютора не остаётся — тот же откат и 500.
        if (tutorId is null)
        {
            users.SetChildProfile(id, previous, resetRules: false);
            return StatusCode(500, new { code = "tutor_provision_failed", message = "Не удалось закрепить персону-тьютора; профиль не изменён." });
        }

        var saved = users.GetById(id);
        if (saved?.ChildMode is not { } finalProfile)
            return StatusCode(500, new { code = "tutor_provision_failed", message = "Профиль ребёнка не сохранился; повторите." });
        return Ok(new ChildProfileResponse(ToChildProfileDto(finalProfile), tutorId));
    }

    // --- вспомогательные методы ---

    private static UserDto ToDto(User u) =>
        new(u.Id, u.Username, u.Role, u.CreatedAt, u.ExecutionEnvironment);

    // Wire-контракт ADR-041 (ChildProfileDto) → модель стора. Правил (RulesAgreedAt/
    // RulesSummary) в wire-типе нет: они живут только в модели и из тела не принимаются
    // никогда. Порог предупреждения: нет поля — дефолт 5 (ADR-041).
    private static ChildProfile ToChildProfile(ChildProfileDto req) => new()
    {
        Enabled = true,
        GuardianUserIds = [.. req.GuardianUserIds ?? []],
        ProviderKey = req.ProviderKey ?? "",
        Model = req.Model ?? "",
        Spaces = (req.Spaces ?? []).ToDictionary(
            kv => kv.Key,
            kv => new SpaceLimits
            {
                Windows = [.. (kv.Value?.Windows ?? []).Select(w => new TimeWindow
                {
                    From = w?.From ?? "",
                    To = w?.To ?? "",
                    Weekdays = w?.Weekdays is { } days ? [.. days] : null,
                })],
                DailyMinutes = kv.Value?.DailyMinutes,
            }),
        DailyBudgetUsd = req.DailyBudgetUsd,
        MonthlyBudgetUsd = req.MonthlyBudgetUsd,
        WarnMinutes = req.WarnMinutes ?? 5,
    };

    // Обратный маппинг — для поля profile ответа (null — профиля нет: выключение пользователя,
    // который им и не был). Правила наружу не отдаются: их поле — дата согласия ребёнка,
    // её показывает status-ручка (№1), не этот ответ.
    private static ChildProfileDto? ToChildProfileDto(ChildProfile? p) => p is null ? null :
        new ChildProfileDto(p.Enabled, [.. p.GuardianUserIds], p.ProviderKey, p.Model,
            p.Spaces.ToDictionary(
                kv => kv.Key,
                kv => new ChildSpaceLimitsDto([.. kv.Value.Windows.Select(
                    w => new ChildTimeWindowDto(w.From, w.To, w.Weekdays is { } days ? [.. days] : null))],
                    kv.Value.DailyMinutes)),
            p.DailyBudgetUsd, p.MonthlyBudgetUsd, p.WarnMinutes, null);

    private static string? ValidateUsername(string? username)
    {
        if (string.IsNullOrWhiteSpace(username))
            return "Имя пользователя не может быть пустым";
        if (username.Length < 3 || username.Length > 32)
            return "Имя пользователя должно содержать от 3 до 32 символов";
        if (!UsernameRegex.IsMatch(username))
            return "Имя пользователя может содержать только буквы, цифры, _ и -";
        return null;
    }

    private static string? ValidatePassword(string? password)
    {
        if (string.IsNullOrEmpty(password) || password.Length < 8)
            return "Пароль должен содержать не менее 8 символов";
        return null;
    }

    private static string? ValidateRole(string? role)
    {
        if (role is not "admin" and not "user")
            return "Роль должна быть 'admin' или 'user'";
        return null;
    }

    private static string? ValidateExecutionEnvironment(string? env)
    {
        if (env is not null && !ExecutionEnvironments.IsValid(env))
            return "Среда исполнения должна быть 'local' или 'container'";
        return null;
    }
}

public record UserDto(string Id, string Username, string Role, DateTime CreatedAt, string ExecutionEnvironment);
public record CreateUserRequest(string Username, string Password, string Role, string? ExecutionEnvironment = null);
public record UpdateUserRequest(string? Username, string? Role, string? ExecutionEnvironment = null);
public record ResetPasswordRequest(string NewPassword);
