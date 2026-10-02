using System.Diagnostics.CodeAnalysis;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.Composition;
using Microsoft.AspNetCore.Mvc;

namespace ClaudeHomeServer.Services.ImageEditor.Controllers;

// Гейт области редактора — общий для проектных и личных ручек (разрез
// docs/research/image-editor-personal-chats-cut-2026-09.md, 1.1–1.2). Отдаёт область, а не проект:
// у личной области Project = null, и каждая ветка «диск проекта» обязана отказать до RootPath.
//
// Выключенный флаг, чужой проект и чужой чат одинаково 404: ручки редактора не выдают ни фичу,
// ни чужие id. Чужой, несуществующий, чат другого проекта и (для личной ручки) чат проекта
// неотличимы — одно и то же тело chat_not_found.
public sealed class ImageEditScopeGate(IFeatureFlagGate flags, IProjectManager projects, ISessionDirectory directory)
{
    // Ручки проекта без чата: проект свой
    public bool TryProject(string userId, string projectId,
        [NotNullWhen(true)] out Project? project, [NotNullWhen(false)] out IActionResult? denied)
    {
        project = null;
        if (!flags.IsEnabled(userId, FeatureFlagKeys.ImageEditor))
        {
            denied = Disabled();
            return false;
        }
        if (projects.GetById(projectId) is not { } found || found.OwnerId != userId)
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
        if (directory.GetById(sessionId) is not { } session || session.ProjectId != project.Id)
        {
            denied = ChatNotFound();
            return false;
        }
        scope = ImageEditScope.Of(project);
        return true;
    }

    // Ручки личного чата вне проекта: владение проверяется напрямую. Без условия ProjectId is null
    // личная ручка открыла бы нити чата проекта мимо гейта проекта (ADR-016, FileBound)
    public bool TryPersonalChat(string userId, string sessionId,
        [NotNullWhen(true)] out ImageEditScope? scope, [NotNullWhen(false)] out IActionResult? denied)
    {
        scope = null;
        if (!flags.IsEnabled(userId, FeatureFlagKeys.ImageEditor))
        {
            denied = Disabled();
            return false;
        }
        if (directory.GetById(sessionId) is not { } session
            || directory.ResolveOwnerId(session) != userId
            || session.ProjectId is not null)
        {
            denied = ChatNotFound();
            return false;
        }
        scope = ImageEditScope.Of(session);
        denied = null;
        return true;
    }

    private static NotFoundObjectResult Disabled() => new(new { error = "Редактор картинок выключен" });

    private static ObjectResult ChatNotFound() =>
        new(new { error = "Чат не найден", code = ImageEditErrorCodes.ChatNotFound }) { StatusCode = StatusCodes.Status404NotFound };
}
