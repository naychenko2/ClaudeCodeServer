using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Access;

namespace ClaudeHomeServer.Tests.Services.Architecture;

/// <summary>
/// Точка доступа для прямого вызова контроллера (вне хоста): доступ — по владению, без
/// рубильника общих проектов и без ролей участников. Эти тесты проверяют «свой/чужой» и
/// порядок отказа (доступ раньше работы с диском и раньше отказа шва), а не матрицу ролей:
/// она проверена HTTP-тестами на настоящем ProjectAccessService (SharedProjectReadTests).
/// </summary>
internal sealed class OwnerOnlyAccess(IProjectManager projects) : IProjectAccess
{
    public RequestScope? Resolve(string? callerUserId, string? projectId, ProjectRight right = ProjectRight.Read) =>
        projects.GetById(projectId!) is { } project && project.OwnerId == callerUserId
            ? new RequestScope(callerUserId!, project.OwnerId!, ProjectRole.Owner)
            : null;

    public RequestScope? ResolveSession(string? callerUserId, string? sessionId, ProjectRight right = ProjectRight.Read) => null;
}
