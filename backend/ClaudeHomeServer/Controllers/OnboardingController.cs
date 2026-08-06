using System.Collections.Concurrent;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ClaudeHomeServer.Controllers;

// Онбординги (фича default-personas-onboarding): обязательные чат-интервью первого входа
// (личная дефолт-персона) и создания проекта (персона-руководитель). Онбординг — обычная
// сессия существующего рантайма со спец-промптом (Session.OnboardingKind → врезка в
// SessionManager.BuildPersonaLayer); финализация — make-default из этой сессии
// (PersonasController.MakeDefault → FinalizeOnboardingAsync).
[ApiController]
[Authorize]
[Route("api/onboarding")]
public class OnboardingController(SessionManager sessions, UserStore users,
    ProjectManager projects, PersonaManager personas) : ControllerBase
{
    private string UserId => User.FindFirstValue(JwtRegisteredClaimNames.Sub)!;

    // Идемпотентность double-start (две вкладки, повторный логин): создание сессии и запись
    // OnboardingSessionId — атомарно под per-ключевым семафором, иначе двойной start породил
    // бы две сессии и перезапись id. Статический реестр: контроллер per-request, а гонка
    // межзапросная. Семафоры копеечные и не чистятся — ключей столько же, сколько
    // пользователей и проектов.
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Locks = new();

    private static SemaphoreSlim LockFor(string key) =>
        Locks.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));

    // Старт (или резюм) онбординга пользователя: чат вне проекта с OnboardingKind="user",
    // ведёт системный «Мастер настройки» (персоны у сессии нет). Живая сессия из
    // User.OnboardingSessionId возвращается как есть; удалённая — заменяется новой.
    [HttpPost("user/start")]
    public async Task<IActionResult> StartUser()
    {
        var gate = LockFor("user:" + UserId);
        await gate.WaitAsync();
        try
        {
            var me = users.GetById(UserId);
            if (me is null) return Unauthorized();

            if (me.OnboardingSessionId is { } sid && sessions.GetOwned(sid, UserId) is { } existing)
                return Ok(existing);

            var chat = await sessions.CreateChatAsync(UserId, ClaudeMode.Auto,
                name: "Настройка системы", onboardingKind: OnboardingKinds.User);
            users.SetOnboardingSession(UserId, chat.Id);
            return Ok(chat);
        }
        catch (InvalidOperationException ex) { return BadRequest(new { error = ex.Message }); }
        finally { gate.Release(); }
    }

    // Старт (или резюм) онбординга проекта: проектная сессия с личной дефолт-персоной
    // владельца и OnboardingKind="project". Без личного дефолта — 400 (сначала онбординг
    // пользователя). Живая сессия из Project.OnboardingSessionId возвращается как есть.
    [HttpPost("project/{projectId}/start")]
    public async Task<IActionResult> StartProject(string projectId)
    {
        var project = projects.GetById(projectId);
        if (project is null || project.OwnerId != UserId) return NotFound();

        var gate = LockFor("project:" + projectId);
        await gate.WaitAsync();
        try
        {
            // Перечитываем проект под локом: параллельный start мог уже записать сессию
            project = projects.GetById(projectId);
            if (project is null) return NotFound();
            if (project.OnboardingSessionId is { } sid && sessions.GetOwned(sid, UserId) is { } existing)
                return Ok(existing);

            // Ведёт личная дефолт-персона; сирота (персона удалена) — как отсутствие дефолта
            var defaultId = users.GetById(UserId)?.DefaultPersonaId;
            var defaultPersona = defaultId is null ? null : personas.Get(defaultId, UserId);
            if (defaultPersona is null)
                return BadRequest(new { error = "Сначала пройдите личный онбординг: у вас ещё нет дефолт-персоны" });

            var session = await sessions.CreateAsync(projectId, ClaudeMode.Auto,
                name: "Знакомство с проектом", personaId: defaultPersona.Id,
                onboardingKind: OnboardingKinds.Project);
            projects.SetOnboardingSession(projectId, session.Id);
            return Ok(session);
        }
        catch (InvalidOperationException ex) { return BadRequest(new { error = ex.Message }); }
        finally { gate.Release(); }
    }
}
