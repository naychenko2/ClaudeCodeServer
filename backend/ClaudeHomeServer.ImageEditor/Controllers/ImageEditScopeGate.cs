using System.Diagnostics.CodeAnalysis;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.Access;
using ClaudeHomeServer.Services.Composition;
using Microsoft.AspNetCore.Mvc;

namespace ClaudeHomeServer.Services.ImageEditor.Controllers;

// Гейт области редактора — общий для проектных и личных ручек (разрез
// docs/research/image-editor-personal-chats-cut-2026-09.md, 1.1–1.2). Отдаёт область, а не проект:
// у личной области Project = null, и каждая ветка «диск проекта» обязана отказать до RootPath.
//
// Чужой проект и чужой чат одинаково 404: ручки редактора не выдают ни фичу, ни чужие id.
// Чужой, несуществующий, чат другого проекта и (для личной ручки) чат проекта
// неотличимы — одно и то же тело chat_not_found.
public sealed class ImageEditScopeGate(
    IProjectManager projects, ISessionDirectory directory, IProjectAccess access, PersonalFolderResolver? personal = null)
{
    // Ручки проекта без чата: нужно право записи (ручки редактора запускают платные задачи)
    public bool TryProject(string userId, string projectId,
        [NotNullWhen(true)] out Project? project, [NotNullWhen(false)] out IActionResult? denied)
    {
        project = null;
        // Решение (роль, рубильник общих проектов, запрет записи участнику в S1) — только через точку доступа
        if (projects.GetById(projectId) is not { } found || access.Resolve(userId, projectId, ProjectRight.Write) is null)
        {
            denied = new NotFoundObjectResult(new { error = "Проект не найден" });
            return false;
        }
        project = found;
        denied = null;
        return true;
    }

    // Ручки нитей чата проекта: проект свой, чат — этого проекта (владение следует из проекта)
    public bool TryProjectChat(string userId, string projectId, string sessionId,
        [NotNullWhen(true)] out ImageEditScope? scope, [NotNullWhen(false)] out IActionResult? denied)
    {
        scope = null;
        if (!TryProject(userId, projectId, out var project, out denied)) return false;
        // Приватный чат (PrivateToUserId) виден только автору: отказ неотличим от «нет такого чата».
        // Рубильник общих проектов решает Resolve в TryProject, CanSee — только видимость по модели
        if (directory.GetById(sessionId) is not { } session || session.ProjectId != project.Id
            || !SessionAccess.CanSee(session, project, userId))
        {
            denied = ChatNotFound();
            return false;
        }
        scope = ImageEditScope.Of(project);
        return true;
    }

    // sessionId из тела/query/формы ручки проекта: пустой — «чата нет», непустой проходит ту же
    // проверку приватности, что и sessionId в маршруте (находка 4.1 ревью R-S2d-1d)
    public bool TryOptionalChat(string userId, string projectId, string? sessionId,
        [NotNullWhen(false)] out IActionResult? denied)
    {
        denied = null;
        return string.IsNullOrWhiteSpace(sessionId)
               || TryProjectChat(userId, projectId, sessionId.Trim(), out _, out denied);
    }

    // Артефакт по id (задача, вариант, шаг): привязан к чату — зритель обязан его видеть (находка 4.3).
    // Без привязки — не гейтим; чат, которого уже нет, неотличим от чужого приватного (fail-closed)
    public bool TryArtifactChat(string userId, string projectId, string? chatSessionId) =>
        string.IsNullOrWhiteSpace(chatSessionId)
        || TryProjectChat(userId, projectId, chatSessionId.Trim(), out _, out _);

    // Ручки личного чата вне проекта: владение проверяется напрямую. Без условия ProjectId is null
    // личная ручка открыла бы нити чата проекта мимо гейта проекта (ADR-016, FileBound)
    public bool TryPersonalChat(string userId, string sessionId,
        [NotNullWhen(true)] out ImageEditScope? scope, [NotNullWhen(false)] out IActionResult? denied)
    {
        scope = null;
        if (directory.GetById(sessionId) is not { } session
            || session.ProjectId is not null
            || !SessionAccess.CanSee(session, null, userId))
        {
            denied = ChatNotFound();
            return false;
        }
        // «Личное» кладём, только если папка уже есть: GET-путь папку не создаёт (ADR-040 §1.2).
        // Ключ области остаётся personal, Session.ProjectId остаётся null
        scope = ImageEditScope.Of(session) with { TenantOwnerId = session.OwnerId, Project = personal?.Find(session.OwnerId) };
        denied = null;
        return true;
    }

    private static ObjectResult ChatNotFound() =>
        new(new { error = "Чат не найден", code = ImageEditErrorCodes.ChatNotFound }) { StatusCode = StatusCodes.Status404NotFound };
}
