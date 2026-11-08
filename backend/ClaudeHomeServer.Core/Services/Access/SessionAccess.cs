using ClaudeHomeServer.Models;

namespace ClaudeHomeServer.Services.Access;

/// <summary>
/// Точка видимости чата — чистая функция над моделью, пара к <see cref="ProjectAccess"/>.
/// Проектный чат виден тем, кому доступен его проект (та же точка правды), и не приватен
/// другому автору (приватность чатов, правило И.2, S2d-1): чат с <c>PrivateToUserId</c>
/// виден ТОЛЬКО автору — ни другим участникам, ни владельцу проекта (решение 28.10).
/// Чат вне проекта — тому, кому принадлежит сам чат (<c>Session.OwnerId</c>).
/// </summary>
public static class SessionAccess
{
    /// <summary>
    /// Видит ли человек чат. null/пустой <paramref name="userId"/> — нет. Проектный чат без
    /// проекта (проект не найден) — нет: резолвить владельца нечем (fail-closed). Приватный
    /// проектный чат (<c>PrivateToUserId</c>) — только его автору; мусорное значение поля
    /// (пустая строка) не даёт доступа никому (fail-closed). Пустой <c>PrivateToUserId</c> —
    /// чат общий, поведение прежнее.
    /// </summary>
    public static bool CanSee(Session? session, Project? project, string? userId)
    {
        if (session is null || string.IsNullOrEmpty(userId)) return false;
        if (session.ProjectId is null) return session.OwnerId == userId;
        return ProjectAccess.Check(project, userId) is not null
            && (session.PrivateToUserId is null || session.PrivateToUserId == userId);
    }
}
