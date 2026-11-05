namespace ClaudeHomeServer.Services.Access;

/// <summary>
/// Резолв области запроса по проекту или чату. Нет доступа (в том числе «проекта/чата нет»)
/// — null: отказ не различает «чужое» и «несуществующее». Намеренно НЕТ метода, строящего
/// ключ хранилищ из id вызывающего: ключ отдаёт только <see cref="RequestScope.TenantOwnerId"/>.
/// </summary>
public interface IProjectAccess
{
    /// <summary>Область для проекта или null — доступа к праву <paramref name="right"/> нет.</summary>
    RequestScope? Resolve(string? callerUserId, string? projectId, ProjectRight right = ProjectRight.Read);

    /// <summary>
    /// Область для чата: проектный чат — через доступ к его проекту, чат вне проекта — через
    /// владение самим чатом (<see cref="SessionAccess.CanSee"/>). null — чата нет или не виден.
    /// </summary>
    RequestScope? ResolveSession(string? callerUserId, string? sessionId, ProjectRight right = ProjectRight.Read);

    /// <summary>
    /// Область для персоны (разрез «Проектные персоны общего проекта», §2): проектная персона —
    /// через доступ к своему проекту (плюс сверка <c>persona.OwnerId</c> с владельцем проекта),
    /// глобальная и сферная — только по владению самой персоной. null — персоны нет, вызывающий
    /// пуст или доступа нет («нет» и «чужое» неотличимы).
    /// </summary>
    /// <remarks>
    /// Дефолт — null, а не ошибка: реализации без каталога персон (и тестовые фейки) остаются
    /// fail-closed. Одна точка правды «персона → область» (И-1): вне неё право персоны не решают.
    /// </remarks>
    RequestScope? ResolvePersona(string? callerUserId, string? personaId, ProjectRight right = ProjectRight.Read) => null;
}
