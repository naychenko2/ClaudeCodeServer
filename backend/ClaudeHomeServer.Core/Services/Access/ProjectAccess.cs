using ClaudeHomeServer.Models;

namespace ClaudeHomeServer.Services.Access;

/// <summary>
/// Роль человека в проекте. Сегодня одна реальная роль — владелец (S0-1, поведение
/// продукта не меняется); Editor/Reader привнесёт модель членства (S1-1) новыми
/// значениями в конце перечисления.
/// </summary>
public enum ProjectRole
{
    /// <summary>Владелец проекта: <c>Project.OwnerId == userId</c>.</summary>
    Owner,
}

/// <summary>
/// Право действия над проектом: Read — просмотр проекта и его чатов, Write — правка и
/// запуск ходов (нужно с S2a-10). Матрица «роль → права» — <see cref="ProjectAccess.RoleHasRight"/>.
/// </summary>
public enum ProjectRight
{
    Read,
    Write,
}

/// <summary>
/// Точка доступа к проекту — чистая функция над моделью (по образцу
/// <see cref="ProjectCapabilities"/>), не вертикаль и не сервис: зовут напрямую, как
/// <c>SafePath.Join</c> (разрез §6.1). Сегодня — «только владелец»: это ровно та проверка
/// <c>OwnerId == userId</c>, собранная в одну точку вместо инлайновых сравнений; их
/// разметку и перевод делают S0-3…S0-7.
/// </summary>
public static class ProjectAccess
{
    /// <summary>
    /// Роль человека в проекте или null — доступа нет. null/пустой <paramref name="userId"/>
    /// и null-проект — тоже null: «неизвестно, кто» и «проекта нет» не могут означать доступ
    /// (fail-closed).
    /// </summary>
    public static ProjectRole? Check(Project? project, string? userId) =>
        project is not null && !string.IsNullOrEmpty(userId) && project.OwnerId == userId
            ? ProjectRole.Owner
            : null;

    /// <summary>Можно ли человеку право <paramref name="right"/> в проекте.</summary>
    public static bool Can(Project? project, string? userId, ProjectRight right) =>
        RoleHasRight(Check(project, userId), right);

    /// <summary>
    /// Матрица «роль → права». Сегодня у единственной роли (владелец) — все права;
    /// с S1-1 здесь появится различие Editor/Reader (у читателя Write — false).
    /// null-роль не имеет ни одного права.
    /// </summary>
    private static bool RoleHasRight(ProjectRole? role, ProjectRight right) =>
        role is not null;
}
