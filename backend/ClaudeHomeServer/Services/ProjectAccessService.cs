using ClaudeHomeServer.Services.Access;

namespace ClaudeHomeServer.Services;

/// <summary>
/// Реализация <see cref="IProjectAccess"/> поверх <see cref="ProjectManager"/> и каталога
/// сессий: решение о доступе принимает Core-примитив <see cref="ProjectAccess"/> /
/// <see cref="SessionAccess"/>, здесь только достаём модели и собираем <see cref="RequestScope"/>.
/// </summary>
public sealed class ProjectAccessService(
    ProjectManager projects, ISessionDirectory sessions, SharedProjectsGate? gate = null) : IProjectAccess
{
    public RequestScope? Resolve(string? callerUserId, string? projectId, ProjectRight right = ProjectRight.Read)
    {
        if (string.IsNullOrEmpty(callerUserId) || string.IsNullOrEmpty(projectId)) return null;
        var project = projects.GetById(projectId);
        if (!ProjectAccess.Can(project, callerUserId, right)) return null;
        var role = ProjectAccess.Check(project, callerUserId)!.Value;
        if (role != ProjectRole.Owner)
        {
            // Участник видит общий проект только при рубильнике и флаге; без гейта — fail-closed
            if (gate?.IsOn(callerUserId) != true) return null;
            // Участник Write не получает: S2a-10 открыл ему только RunTurn (отправка в чат); запись откроют S2b/S2c
            if (right == ProjectRight.Write) return null;
        }
        return new RequestScope(callerUserId, project!.OwnerId!, role);
    }

    public RequestScope? ResolveSession(string? callerUserId, string? sessionId, ProjectRight right = ProjectRight.Read)
    {
        if (string.IsNullOrEmpty(callerUserId) || string.IsNullOrEmpty(sessionId)) return null;
        var session = sessions.GetById(sessionId);
        if (session is null) return null;
        // Видимость чата — одна точка SessionAccess (S2d-1): приватный проектный чат не проходит
        // и для владельца проекта. Дальше для проектного чата — право и рубильник через Resolve;
        // для чата вне проекта — владение самим чатом (поведение прежнее).
        var project = session.ProjectId is not null ? projects.GetById(session.ProjectId) : null;
        if (!SessionAccess.CanSee(session, project, callerUserId)) return null;
        return session.ProjectId is not null
            ? Resolve(callerUserId, session.ProjectId, right)
            : new RequestScope(callerUserId, session.OwnerId!, ProjectRole.Owner);
    }
}
