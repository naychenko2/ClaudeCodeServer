using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.Access;

namespace ClaudeHomeServer.Services;

/// <summary>
/// Реализация <see cref="IProjectAccess"/> поверх <see cref="ProjectManager"/>, каталога
/// сессий и шова персон <see cref="IPersonaLookup"/>: решение о доступе принимает
/// Core-примитив <see cref="ProjectAccess"/> / <see cref="SessionAccess"/>, здесь только
/// достаём модели и собираем <see cref="RequestScope"/>. Персон здесь читается ровно
/// <c>GetByIdInternal</c> — полный <see cref="PersonaManager"/> не нужен (тот же вывод,
/// что у шова, W3-B1a).
/// </summary>
public sealed class ProjectAccessService(
    ProjectManager projects, ISessionDirectory sessions, SharedProjectsGate? gate = null,
    IPersonaLookup? personas = null) : IProjectAccess
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

    public RequestScope? ResolvePersona(string? callerUserId, string? personaId, ProjectRight right = ProjectRight.Read)
    {
        if (string.IsNullOrEmpty(callerUserId) || string.IsNullOrEmpty(personaId) || personas is null) return null;
        // Единственное место в пути запроса, где персона читается без проверки владельца
        // (GetByIdInternal): право решает сама персона — её Scope/ProjectId/OwnerId, а не sub
        // вызывающего (И-1, разрез §2). Get(id, caller) здесь означал бы прежний отказ редактору.
        var persona = personas.GetByIdInternal(personaId);
        if (persona is null) return null;

        // Проектная персона живого проекта — через доступ к её проекту (§2.2).
        // Зону персоны читает только PersonaZone.OwnProjectId (сторож PersonaZoneGuard)
        var ownProject = PersonaZone.OwnProjectId(persona);
        if (ownProject is not null && projects.GetById(ownProject) is not null)
        {
            var scope = Resolve(callerUserId, ownProject, right);
            // Персона «проекта» с чужим владельцем — битая запись, а не доступ (§2.2)
            return scope is not null && persona.OwnerId == scope.TenantOwnerId ? scope : null;
        }

        // Глобальная, сферная, проектная с исчезнувшим проектом — только по владению (§2.3).
        // Глобальные и сферные персоны владельца участнику не видны никогда (И-3).
        return persona.OwnerId == callerUserId
            ? new RequestScope(callerUserId, persona.OwnerId, ProjectRole.Owner)
            : null;
    }
}
