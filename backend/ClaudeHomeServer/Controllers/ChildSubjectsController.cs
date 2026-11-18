using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Access;
using ClaudeHomeServer.Services.ChildMode;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ClaudeHomeServer.Controllers;

/// <summary>
/// Проекция репетиторов ребёнка (KM-T1e, И5, разрез §4 №4): GET /api/child/subjects отдаёт
/// ВКЛЮЧЁННЫЕ предметы профиля в порядке профиля — id, label, глиф, цвет плитки и репетитора
/// (id, имя, есть ли аватар). Промпта, маршрута (providerKey/model) и голоса здесь нет:
/// ребёнок видит плитки, а не настройку (И5). Порядок списка = порядок Subjects профиля.
///
/// Порядок отказов — как у ChildChatsController: 403 not_child → 503 policy_unavailable
/// (нечитаемый профиль, DenyAll). Висящий ключ репетитора (персона удалена) — tutor: null:
/// имени и аватара у несуществующей персоны нет, выдумывать их проекция не вправе.
///
/// Резолв ключа — через единую точку «персона → область» IProjectAccess.ResolvePersona
/// (W3-B1b, разрез §5 п.2), а не инлайновым personas.Get с ключом вызывающего: тьютор ребёнка —
/// его собственная глобальная персона (ChildTutorProvisioner.cs:74-83), ResolvePersona резолвит
/// её по владению (ProjectAccessService.cs:70-72); чужая или удалённая — null, как раньше.
/// </summary>
[ApiController]
[Authorize]
[Route("api/child")]
public class ChildSubjectsController(
    IChildModePolicy policy,
    PersonaManager personas,
    IProjectAccess access,
    ILogger<ChildSubjectsController> logger) : ControllerBase
{
    private string UserId => User.FindFirstValue(JwtRegisteredClaimNames.Sub)!;

    [ChildAllowed]
    [HttpGet("subjects")]
    public IActionResult Subjects()
    {
        var snapshot = policy.For(UserId);
        if (snapshot is null)
            return ChildError(403, "not_child", "Эта ручка только для детей.");
        if (snapshot.DenyAll || snapshot.Profile is null)
            return Unavailable();

        try
        {
            // Только включённые предметы, порядок — порядок профиля (плитки главной).
            var subjects = (snapshot.Profile.Subjects ?? [])
                .Where(s => s is { Enabled: true })
                .Select(s => new
                {
                    id = s.Id,
                    label = s.Label,
                    glyph = s.Glyph,
                    color = s.Color,
                    // Ключ — через единую точку резолва (ResolvePersona с правом Read, W3-B1b §5 п.2):
                    // своя глобальная персона ребёнка резолвится по владению (тот же образец, что
                    // TasksController.PersonaNameOf); чужая или удалённая — null, поведение то же.
                    tutor = access.ResolvePersona(UserId, s.TutorPersonaId, ProjectRight.Read) is { } scope
                            && personas.Get(s.TutorPersonaId, scope.TenantOwnerId) is { } tutor
                        ? new { personaId = tutor.Id, name = tutor.Name, hasAvatar = HasAvatar(tutor) }
                        : null,
                })
                .ToList();
            return Ok(new { subjects });
        }
        catch (Exception e)
        {
            logger.LogWarning(e, "Детский режим: проекция репетиторов не прочитана (owner {Owner})", UserId);
            return Unavailable();
        }
    }

    // Тот же признак «аватар есть», что у 404 в GET /api/personas/{id}/avatar
    // (PersonasController.Avatar): hasAvatar=true ⟺ ручка отдаёт картинку
    // (кроме случая «файл пропал с диска» — там 404, и фронт вернётся к инициалам).
    internal static bool HasAvatar(Persona persona) =>
        persona.Avatar.Kind == PersonaAvatarKind.Image && !string.IsNullOrEmpty(persona.Avatar.ImageFile);

    private static JsonResult Unavailable() =>
        ChildError(503, "policy_unavailable", "Настройки режима не читаются — попросите взрослого проверить.");

    private static JsonResult ChildError(int status, string code, string message) =>
        new(new { code, message }) { StatusCode = status };
}
