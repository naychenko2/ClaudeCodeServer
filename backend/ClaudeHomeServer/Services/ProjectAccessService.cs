using ClaudeHomeServer.Services.Access;

namespace ClaudeHomeServer.Services;

/// <summary>
/// Реализация <see cref="IProjectAccess"/> поверх <see cref="ProjectManager"/> и каталога
/// сессий: решение о доступе принимает Core-примитив <see cref="ProjectAccess"/> /
/// <see cref="SessionAccess"/>, здесь только достаём модели и собираем <see cref="RequestScope"/>.
/// </summary>
public sealed class ProjectAccessService(ProjectManager projects, ISessionDirectory sessions) : IProjectAccess
{
    public RequestScope? Resolve(string? callerUserId, string? projectId, ProjectRight right = ProjectRight.Read)
    {
        if (string.IsNullOrEmpty(callerUserId) || string.IsNullOrEmpty(projectId)) return null;
        var project = projects.GetById(projectId);
        if (!ProjectAccess.Can(project, callerUserId, right)) return null;
        return new RequestScope(callerUserId, project!.OwnerId!, ProjectAccess.Check(project, callerUserId)!.Value);
    }

    public RequestScope? ResolveSession(string? callerUserId, string? sessionId, ProjectRight right = ProjectRight.Read)
    {
        if (string.IsNullOrEmpty(callerUserId) || string.IsNullOrEmpty(sessionId)) return null;
        var session = sessions.GetById(sessionId);
        if (session is null) return null;
        // Проектный чат — доступ через проект (с правом), чат вне проекта — владение самим чатом
        if (session.ProjectId is not null) return Resolve(callerUserId, session.ProjectId, right);
        return SessionAccess.CanSee(session, null, callerUserId)
            ? new RequestScope(callerUserId, session.OwnerId!, ProjectRole.Owner)
            : null;
    }
}
