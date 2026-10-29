using ClaudeHomeServer.Models;

namespace ClaudeHomeServer.Services.Access;

/// <summary>
/// Роль человека в проекте. Владелец выводится из владения (<c>OwnerId</c>), редактор и
/// читатель — из списка участников <c>Project.Members</c> (модель членства S1-1, матрица
/// прав — разрез §2.1). Номера закреплены ЯВНО: ProjectMember.Role лежит на диске числом,
/// новые значения добавлены в конце — прежние не сдвинулись.
/// </summary>
public enum ProjectRole
{
    /// <summary>Владелец проекта: <c>Project.OwnerId == userId</c>. В Members не пишется.</summary>
    Owner = 0,
    /// <summary>Участник с правом чтения и записи (разрез §2.1).</summary>
    Editor = 1,
    /// <summary>Участник только с правом чтения — «нет права на запись» (разрез §2.1).</summary>
    Reader = 2,
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
/// <c>SafePath.Join</c> (разрез §6.1). Владелец — по владению, участники (Editor/Reader) —
/// по <c>Project.Members</c> (модель членства S1-1); инлайновых сравнений вне этой точки
/// не заводим, их разметку и перевод делают S0-3…S0-7.
/// </summary>
public static class ProjectAccess
{
    /// <summary>
    /// Роль человека в проекте или null — доступа нет. Владелец — по владению (<c>OwnerId</c>),
    /// редактор/читатель — по списку участников <c>Project.Members</c> (S1-1). null/пустой
    /// <paramref name="userId"/> и null-проект — тоже null: «неизвестно, кто» и «проекта нет»
    /// не могут означать доступ (fail-closed). Несостоявшаяся роль в записи участника
    /// (например Owner — владельца выводит <c>OwnerId</c>, а не список) доступа не даёт.
    /// </summary>
    public static ProjectRole? Check(Project? project, string? userId)
    {
        if (project is null || string.IsNullOrEmpty(userId)) return null;
        if (project.OwnerId == userId) return ProjectRole.Owner;
        var member = project.Members?.FirstOrDefault(m => m.UserId == userId);
        if (member is null) return null;
        return member.Role is ProjectRole.Editor or ProjectRole.Reader ? ProjectRole.Editor : null; // МУТАЦИЯ c4
    }

    /// <summary>Можно ли человеку право <paramref name="right"/> в проекте.</summary>
    public static bool Can(Project? project, string? userId, ProjectRight right) =>
        RoleHasRight(Check(project, userId), right);

    /// <summary>
    /// Матрица «роль → права» (разрез §2.1, минимальная): владелец и редактор имеют оба
    /// права, читатель — только Read («нет права на запись»). Тонкие права (настройки
    /// проекта, приглашение участников) в матрицу привнесёт их шаг, здесь их нет.
    /// null-роль не имеет ни одного права.
    /// </summary>
    private static bool RoleHasRight(ProjectRole? role, ProjectRight right) =>
        role switch
        {
            ProjectRole.Owner or ProjectRole.Editor => true,
            ProjectRole.Reader => right == ProjectRight.Read,
            _ => false,
        };
}
