using ClaudeHomeServer.Models;

namespace ClaudeHomeServer.Services.Access;

/// <summary>
/// Точка видимости чата — чистая функция над моделью, пара к <see cref="ProjectAccess"/>.
/// Сегодня — «только владелец»: проектный чат виден тем, кому доступен его проект
/// (та же точка правды), чат вне проекта — тому, кому принадлежит сам чат
/// (<c>Session.OwnerId</c>). Приватность чатов (правило И.2) привнесёт S2d-1.
/// </summary>
public static class SessionAccess
{
    /// <summary>
    /// Видит ли человек чат. null/пустой <paramref name="userId"/> — нет. Проектный чат без
    /// проекта (проект не найден) — нет: резолвить владельца нечем (fail-closed).
    /// </summary>
    public static bool CanSee(Session? session, Project? project, string? userId)
    {
        if (session is null || string.IsNullOrEmpty(userId)) return false;
        return session.ProjectId is not null
            ? ProjectAccess.Check(project, userId) is not null
            : session.OwnerId == userId;
    }
}
