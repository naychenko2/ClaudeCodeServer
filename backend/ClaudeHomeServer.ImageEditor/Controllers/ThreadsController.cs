using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.Composition;
using ClaudeHomeServer.Services.ImageEditor.Threads;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ClaudeHomeServer.Services.ImageEditor.Controllers;

// Нити картинок и фокус чата проекта (ADR-019 §1). Сессию не трогает: смена фокуса не пишет
// sessions.json и не двигает UpdatedAt — всё состояние в ImageThreadStore модуля.
//
// Гейты те же, что у остальных ручек редактора: флаг и чужой проект — одинаково 404. Чужой,
// несуществующий и чат другого проекта неотличимы — 404 с одним и тем же телом.
[ProjectCapability(ProjectCapabilityArea.FileBound)]
[ApiController]
[Authorize]
[Route("api/projects/{projectId}/image-editor/sessions/{sessionId}/threads")]
public class ThreadsController(
    IFeatureFlagGate flags,
    IProjectManager projects,
    ISessionDirectory directory,
    ImageThreadStore threads) : ControllerBase
{
    private string UserId => User.FindFirstValue(JwtRegisteredClaimNames.Sub)!;

    [HttpGet]
    public IActionResult Get(string projectId, string sessionId)
    {
        if (Gate(projectId, sessionId) is { } denied) return denied;
        return Ok(threads.Get(UserId, sessionId));
    }

    // Взять картинку в работу или снять выбор (threadId = null). revision — та, от которой
    // считал фронт; устарела — 409 с актуальным состоянием
    [HttpPut("focus")]
    public IActionResult PutFocus(string projectId, string sessionId, [FromBody] ImageThreadFocusRequest req)
    {
        if (Gate(projectId, sessionId) is { } denied) return denied;

        var written = threads.SetFocus(UserId, sessionId, req.ThreadId, req.Revision);
        return written.Status switch
        {
            ImageThreadWriteStatus.Ok => Ok(written.State),
            ImageThreadWriteStatus.Conflict => StatusCode(StatusCodes.Status409Conflict, new
            {
                error = "Выбор уже поменялся — перечитайте его",
                code = ImageEditErrorCodes.RevisionConflict,
                state = written.State,
            }),
            _ => Error(StatusCodes.Status404NotFound, ImageEditErrorCodes.ThreadNotFound, "Картинка не найдена в этом чате"),
        };
    }

    private IActionResult? Gate(string projectId, string sessionId)
    {
        if (!flags.IsEnabled(UserId, FeatureFlagKeys.ImageEditor))
            return NotFound(new { error = "Редактор картинок выключен" });
        var project = projects.GetById(projectId);
        if (project is null || project.OwnerId != UserId)
            return NotFound(new { error = "Проект не найден" });
        // Чат этого проекта (а проект уже свой): владение следует из проекта
        if (directory.GetById(sessionId) is not { } session || session.ProjectId != project.Id)
            return Error(StatusCodes.Status404NotFound, ImageEditErrorCodes.ChatNotFound, "Чат не найден");
        return null;
    }

    private ObjectResult Error(int status, string code, string error) => StatusCode(status, new { error, code });
}

public sealed record ImageThreadFocusRequest(string? ThreadId, long Revision);
