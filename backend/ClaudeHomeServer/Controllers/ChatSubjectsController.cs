using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Access;
using ClaudeHomeServer.Services.ChatSubjects;
using ClaudeHomeServer.Services.Composition;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ClaudeHomeServer.Controllers;

// Темы чатов проекта (docs/features/chat-subjects.md): реестр тем, ручной перенос чата, запуск
// разбора. Доступ к проекту — [ProjectScope] (чужой — 404). Платформа: разбор ходит в серверную ленту history.json,
// устройство локального проекта ему не нужно.
[ProjectCapability(ProjectCapabilityArea.Platform, ProjectKey = "id")]
[ProjectScope(Key = "id")]
[ApiController]
[Authorize]
[Route("api/projects/{id}")]
public class ChatSubjectsController(SessionManager sessions, ProjectManager projects,
    ChatSubjectSweeper sweeper) : ControllerBase
{
    private string UserId => User.FindFirstValue(JwtRegisteredClaimNames.Sub)!;

    [HttpGet("subjects")]
    public IActionResult GetSubjects(string id)
    {
        return Ok(projects.GetSubjects(id));
    }

    // Реестр целиком: переименование, порядок, цвет, новые темы человека, удаление. Чаты
    // удалённой темы читаются «без темы»: разложенные моделью снова кандидаты разбора, а
    // перенесённые туда человеком возвращаются автоматике (ReleaseOrphanedHumanSubjects).
    // Пустой реестр — только явным subjects: []; тело без поля ({} или subjects: null) — 400,
    // а не молчаливое «удалить всё».
    [HttpPut("subjects")]
    [ProjectScope(Key = "id", Right = ProjectRight.Write)]
    public IActionResult PutSubjects(string id, [FromBody] PutSubjectsRequest? req)
    {
        if (req?.Subjects is not { } subjects)
            return BadRequest(new { error = "Нужен список тем: subjects (пустой список — удалить все темы)" });
        IReadOnlyList<ProjectSubject> registry;
        try { registry = projects.ReplaceSubjects(id, subjects); }
        catch (ArgumentException ex) { return BadRequest(new { error = ex.Message }); }
        sessions.ReleaseOrphanedHumanSubjects(id);
        return Ok(registry);
    }

    // Ручной перенос чата: subjectId — в тему, null — «убрать из темы» (оба закрепляют);
    // release = true — «Вернуть автоматике» (тема и отметка попытки сбрасываются, чат снова
    // кандидат). UpdatedAt чата не двигается.
    [HttpPut("sessions/{sessionId}/subject")]
    [ProjectScope(Key = "id", Right = ProjectRight.Write)]
    public IActionResult MoveSession(string id, string sessionId, [FromBody] MoveSessionSubjectRequest req)
    {
        if (sessions.GetById(sessionId) is not { } chat || chat.ProjectId != id) return NotFound();
        // FIN3-2 (FINAL-3, п. 2 ревью b13f2105): чужой приватный чат неотличим от несуществующего
        // и для редактора проекта: ни ответ (Name, LastMessage, ClaudeSessionId), ни запись темы
        // не состоятся. Гейт — Core-точка видимости (SessionAccess.CanSee), та же, что у правки
        // чата (S2d-3f): решение о праве здесь уже дал атрибут [ProjectScope], решение о видимости
        // — здесь, до любой записи.
        if (!sessions.IsVisibleTo(sessionId, UserId)) return NotFound();
        var write = req.Release == true
            ? new SubjectWrite(sessionId, SubjectWriteKind.Release)
            : new SubjectWrite(sessionId, SubjectWriteKind.Human, req.SubjectId);
        try { sessions.ApplySubjects(id, [write], DateTime.UtcNow); }
        catch (KeyNotFoundException) { return BadRequest(new { error = "Тема не найдена" }); }
        return Ok(sessions.GetById(sessionId));
    }

    // «Разложить сейчас»: все кандидаты проекта, включая залежи. 202 + фон; повторный клик,
    // пока идёт проход, второго прохода не запускает и тоже отвечает 202 с состоянием
    [HttpPost("subjects/run")]
    [ProjectScope(Key = "id", Right = ProjectRight.Write)]
    public IActionResult Run(string id) => Start(id, resetAi: false);

    // «Разложить заново»: снять назначения модели и отметки попыток, затем тот же запуск
    [HttpPost("subjects/reset-ai")]
    [ProjectScope(Key = "id", Right = ProjectRight.Write)]
    public IActionResult ResetAi(string id) => Start(id, resetAi: true);

    [HttpGet("subjects/run")]
    public IActionResult RunState(string id)
    {
        return Ok(ToDto(sweeper.GetState(id), started: null));
    }

    private IActionResult Start(string id, bool resetAi)
    {
        var (started, state) = sweeper.StartManual(id, resetAi, DateTime.UtcNow);
        return Accepted(ToDto(state, started));
    }

    private static object ToDto(SubjectRunState s, bool? started) => new
    {
        state = s.State,
        done = s.Done,
        total = s.Total,
        lastError = s.LastError,
        finishedAt = s.FinishedAt,
        started,
    };
}

public sealed record PutSubjectsRequest(List<ProjectSubjectEdit>? Subjects);

public sealed record MoveSessionSubjectRequest(string? SubjectId, bool? Release);
