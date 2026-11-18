using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Auth;
using ClaudeHomeServer.Services.ChildMode;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ClaudeHomeServer.Controllers;

/// <summary>
/// Админские ручки репетитора профиля ребёнка (Т2c, ADR-041 редакция 2 §12 и редакция 3 §22,
/// разрез «Репетиторы» §5 / «Знания» §5): повторный провижн репетитора предмета и правка
/// характера репетитора со списками «Обязательно / Никогда». Обе ручки только для админа:
/// [Authorize(Roles="admin")] по claim + AdminByStore по стору (тот же образец, что у
/// PUT child-profile в UsersController.cs:173: сервисный токен хода и снятое админство сюда
/// не проходят), без [ChildAllowed]: ребёнок своего репетитора не правит (И5).
///
/// Модель и маршрут здесь нет: они живут только в записи ChildSubject (редакция 2 §12),
/// Persona.Model репетитора пуст — поле model в теле игнорируется. Прочие слоты контракта
/// (tone, outputFormat, speechExamples, instructions) тоже игнорируются: они стоят в слое
/// выше child-safety-оговорки, которую всё равно перебивает PersonaOverride (разрез §5).
/// Правка идёт через PersonasCrudService — тот же код, что у PUT персоны и её событие
/// «updated»; частичная семантика: null-поле тела не меняет значение.
/// </summary>
[ApiController]
[Authorize(Roles = "admin")]
[Route("api/users/{id}")]
public class ChildTutorsController(
    UserStore users,
    PersonaManager personas,
    ChildTutorProvisioner childTutorProvisioner,
    PersonasCrudService crud,
    ILogger<ChildTutorsController> logger) : ControllerBase
{
    // Пределы списков «Обязательно / Никогда» (ADR-041 редакция 3 §22): до 12 пунктов по 300 символов.
    private const int MaxRuleCount = 12;
    private const int MaxRuleLength = 300;

    /// <summary>
    /// Провижн репетитора предмета (разрез §5, строка POST child-subjects/{subjectId}/tutor):
    /// идемпотентен — живой ключ возвращает тот же id без создания; это доводка редактора для
    /// предмета, провитого не полностью. null от провижнера — ровно «пользователя/профиля/
    /// предмета нет или запрос отменён» → 404 subject_not_found; исключение стора → 500
    /// {code,message}, профиль не тронут (откатом здесь управлять нечем — ничего не записывалось).
    /// </summary>
    [HttpPost("child-subjects/{subjectId}/tutor")]
    [Authorize(Policy = AdminByStoreRequirement.PolicyName)]
    public async Task<IActionResult> EnsureSubjectTutor(string id, string subjectId)
    {
        if (users.GetById(id) is null)
            return NotFound(new { code = "user_not_found", message = "Пользователь не найден." });

        string? tutorId;
        try
        {
            tutorId = await childTutorProvisioner.EnsureSubjectTutorAsync(id, subjectId);
        }
        catch (Exception e)
        {
            logger.LogWarning(e, "Детский режим: провижн репетитора предмета {Subject} не удался для {User}", subjectId, id);
            return StatusCode(500, new { code = "tutor_provision_failed", message = "Не удалось создать персону-репетитора предмета." });
        }

        // Ребёнок без профиля — тоже «предмета нет»: ни одного предмета в нём нет.
        if (tutorId is null)
            return NotFound(new { code = "subject_not_found", message = "Предмет не найден в профиле ребёнка." });

        return Ok(new { personaId = tutorId });
    }

    /// <summary>
    /// Правка характера репетитора (ADR-041 редакция 2 §12, редакция 3 §22): name, character,
    /// greeting, voice, gender и списки mustDo/mustNot — через PersonasCrudService, как PUT
    /// персоны. Персона обязана быть репетитором ЭТОГО ребёнка (ключ профиля или любого его
    /// предмета) — иначе 404 tutor_not_found; висящий ключ (персона удалена) — тот же 404,
    /// чужая персона — тоже: существование чужой персоны не раскрываем. Тело проверяется до
    /// первой записи: отказанный вызов оставляет репетитора нетронутым.
    /// </summary>
    [HttpPut("child-tutors/{personaId}")]
    [Authorize(Policy = AdminByStoreRequirement.PolicyName)]
    public async Task<IActionResult> PutTutor(string id, string personaId, [FromBody] ChildTutorPatch req)
    {
        if (users.GetById(id) is not { } child)
            return NotFound(new { code = "user_not_found", message = "Пользователь не найден." });
        if (child.ChildMode is not { } profile
            || !IsChildTutor(profile, personaId)
            || personas.Get(personaId, id) is null)
            return NotFound(new { code = "tutor_not_found", message = "Эта персона — не репетитор этого ребёнка." });

        // Пустые строки отбрасываются до счёта: пустой пункт — не пункт (разрез §5).
        var mustDo = CleanRules(req.MustDo);
        var mustNot = CleanRules(req.MustNot);
        if (RulesTooLong(mustDo) || RulesTooLong(mustNot))
            return BadRequest(new { code = "rules_too_long",
                message = $"Правил «Обязательно / Никогда» — не больше {MaxRuleCount}, каждое — не длиннее {MaxRuleLength} символов." });
        // Голос проверяется до первой записи той же VoiceError, что у PUT /{id}/voice
        // (PersonasCrudService.cs:1403): отказанный голос не должен оставить половину тела
        // применённой — дальше SetVoiceAsync отказаться уже нечем.
        if (PersonasCrudService.VoiceError(req.Voice) is { } voiceError)
            return BadRequest(new { code = "voice_invalid", message = voiceError });

        // Контракт — частичный патч только по трём слотам: MergeContract (PersonaManager.cs:449)
        // не трогает остальные; null-слоты — «не менять», пустой список — явная очистка.
        var contract = req.Character is null && mustDo is null && mustNot is null
            ? null
            : new PersonaContract { Character = req.Character, MustDo = mustDo, MustNot = mustNot };

        ActionResult<Persona> updated;
        try
        {
            updated = await crud.UpdateAsync(id, personaId,
                new UpdatePersonaRequest(
                    Name: req.Name, Role: null, Description: null, SystemPrompt: null,
                    // Model: null — и поле model тела игнорируется: маршрут репетитора живёт
                    // в записи предмета, Persona.Model остаётся пустым (редакция 2 §12).
                    Model: null, Effort: null, Scope: null, ProjectId: null, Color: null,
                    Greeting: req.Greeting, MemoryEnabled: null,
                    Contract: contract,
                    Gender: req.Gender),
                callerSessionId: null);
        }
        catch (Exception e)
        {
            logger.LogWarning(e, "Детский режим: правка репетитора {Persona} не удалась (ребёнок {Owner})", personaId, id);
            return StatusCode(500, new { code = "tutor_update_failed", message = "Не удалось изменить персону-репетитора." });
        }
        // Отказ сервиса пробрасывается как есть (та же форма, что у PUT персоны): достижим здесь
        // только потолок размера контракта — он про текст характера, не про списки.
        // Результат без значения — конвертированный ActionResult из UpdateAsync (Value null,
        // Result — исходный отказ).
        if (updated.Value is not { } persona)
            return updated.Result ?? new StatusCodeResult(500);

        // Голос — отдельная мутация того же сервиса (как PUT /{id}/voice): после валидации
        // отказа уже не будет; событие «updated» приходит по её итогу — с финальным состоянием.
        if (req.Voice is not null)
        {
            try
            {
                await crud.SetVoiceAsync(id, personaId, req.Voice);
            }
            catch (Exception e)
            {
                logger.LogWarning(e, "Детский режим: голос репетитора {Persona} не изменён (ребёнок {Owner})", personaId, id);
                return StatusCode(500, new { code = "tutor_update_failed", message = "Не удалось изменить голос репетитора." });
            }
        }

        return Ok(persona);
    }

    // Тот же признак «репетитор ребёнка», что у аватар-ручки (PersonasController.cs:709):
    // закреплённая персона профиля или репетитор ЛЮБОГО его предмета, включая выключенный:
    // выключенный предмет скрыт с главной, но его репетитор остаётся репетитором профиля.
    // Сравнение id — Ordinal: это GUID.
    private static bool IsChildTutor(ChildProfile profile, string personaId) =>
        string.Equals(profile.TutorPersonaId, personaId, StringComparison.Ordinal)
        || (profile.Subjects ?? []).Any(s => s is not null
            && string.Equals(s.TutorPersonaId, personaId, StringComparison.Ordinal));

    // Пустые строки — не пункты: они отбрасываются до счёта предела (разрез §5).
    private static List<string>? CleanRules(List<string>? rules) =>
        rules is null ? null : rules.Where(s => !string.IsNullOrWhiteSpace(s)).ToList();

    // Предел считается по отчищенному списку: >12 пунктов или пункт длиннее 300 символов.
    private static bool RulesTooLong(List<string>? rules) =>
        rules is { } list && (list.Count > MaxRuleCount || list.Any(r => r.Length > MaxRuleLength));
}

/// <summary>
/// Тело PUT child-tutors (разрез «Знания» §5, ChildTutorPatch): null — поле не трогать;
/// mustDo/mustNot: [] — очистить, пустые строки отбрасываются. Прочие поля тела
/// (tone, model, outputFormat…) игнорируются: их нет в контракте ручки (разрез §5).
/// </summary>
public record ChildTutorPatch(
    string? Name,
    string? Character,
    string? Greeting,
    PersonaVoice? Voice,
    PersonaGender? Gender,
    List<string>? MustDo = null,
    List<string>? MustNot = null);
