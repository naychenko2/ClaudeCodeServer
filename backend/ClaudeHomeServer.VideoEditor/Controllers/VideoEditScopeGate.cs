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
        // Приватный чат (PrivateToUserId) виден только автору: отказ неотличим от «нет такого чата».
        // Рубильник общих проектов решает Resolve в TryProject, CanSee — только видимость по модели
        if (directory.GetById(sessionId) is not { } session || session.ProjectId != scope.Project!.Id
            || !SessionAccess.CanSee(session, scope.Project, userId))
        {
            scope = null;
            denied = ChatNotFound();
            return false;
        }
        return true;
    }

    // sessionId из тела/query ручки проекта: пустой — «чата нет», непустой проходит ту же проверку
    // приватности, что и sessionId в маршруте (находка 4.1 ревью R-S2d-1d)
    public bool TryOptionalChat(string userId, string projectId, string? sessionId,
        [NotNullWhen(false)] out IActionResult? denied)
    {
        denied = null;
        return string.IsNullOrWhiteSpace(sessionId)
               || TryProjectChat(userId, projectId, sessionId.Trim(), out _, out denied);
    }

    // Задача по id привязана к чату — зритель обязан его видеть; чата уже нет — как чужой (fail-closed)
    public bool TryArtifactChat(string userId, string projectId, string? chatSessionId) =>
        string.IsNullOrWhiteSpace(chatSessionId)
        || TryProjectChat(userId, projectId, chatSessionId.Trim(), out _, out _);

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
