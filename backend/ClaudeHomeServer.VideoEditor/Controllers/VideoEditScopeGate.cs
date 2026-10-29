using System.Diagnostics.CodeAnalysis;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.Access;
using ClaudeHomeServer.Services.Composition;
using ClaudeHomeServer.Services.VideoEditor.Contracts;
using Microsoft.AspNetCore.Mvc;

namespace ClaudeHomeServer.Services.VideoEditor.Controllers;

// Гейт области модуля «Видео» — общий для проектных и личных ручек (как AudioEditScopeGate). Отдаёт
// область, а не проект: у личной области Project = null, и каждая ветка «диск проекта» обязана отказать
// до RootPath.
//
// Чужой проект и чужой чат одинаково 404: ручки не выдают чужие id.
// Чужой, несуществующий, чат другого проекта и (для личной ручки) чат проекта неотличимы.
public sealed class VideoEditScopeGate(IProjectManager projects, ISessionDirectory directory, IProjectAccess access)
{
    // Ручки проекта без чата: нужно право записи (ручки редактора запускают платные задачи)
    public bool TryProject(string userId, string projectId,
        [NotNullWhen(true)] out VideoEditScope? scope, [NotNullWhen(false)] out IActionResult? denied)
    {
        scope = null;
        // Решение (роль, рубильник общих проектов, запрет записи участнику в S1) — только через точку доступа
        if (projects.GetById(projectId) is not { } found || access.Resolve(userId, projectId, ProjectRight.Write) is null)
        {
            denied = new NotFoundObjectResult(new { error = "Проект не найден" });
            return false;
        }
        scope = VideoEditScope.Of(found);
        denied = null;
        return true;
    }

    // Ручки чата проекта: проект свой, чат — этого проекта (владение следует из проекта)
    public bool TryProjectChat(string userId, string projectId, string sessionId,
        [NotNullWhen(true)] out VideoEditScope? scope, [NotNullWhen(false)] out IActionResult? denied)
    {
        if (!TryProject(userId, projectId, out scope, out denied)) return false;
        if (directory.GetById(sessionId) is not { } session || session.ProjectId != scope.Project!.Id)
        {
            scope = null;
            denied = ChatNotFound();
            return false;
        }
        return true;
    }

    // Ручки личного чата вне проекта: владение проверяется напрямую. Без условия ProjectId is null личная
    // ручка открыла бы нити чата проекта мимо гейта проекта
    public bool TryPersonalChat(string userId, string sessionId,
        [NotNullWhen(true)] out VideoEditScope? scope, [NotNullWhen(false)] out IActionResult? denied)
    {
        scope = null;
        if (directory.GetById(sessionId) is not { } session
            || session.ProjectId is not null
            || !SessionAccess.CanSee(session, null, userId))
        {
            denied = ChatNotFound();
            return false;
        }
        scope = VideoEditScope.Of(session) with { TenantOwnerId = session.OwnerId };
        denied = null;
        return true;
    }

    private static ObjectResult ChatNotFound() =>
        new(new { error = "Чат не найден", code = VideoEditorErrors.ChatNotFound }) { StatusCode = StatusCodes.Status404NotFound };
}
