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
/// Право действия над проектом: Read — просмотр проекта и его чатов, Write — правка
/// файлов и данных, RunTurn — отправка сообщений в чаты проекта и ведение своего хода (с S2a-10
/// открыто редактору: его ход идёт в узкой рамке S2a-2…S2a-5). Управление чужим ходом, права
/// владельца (режим, одобрение разрешений, штаб, смена провайдера) и shell на хосте под RunTurn
/// НЕ лежат — они на <see cref="Configure"/> или проверяются автором хода отдельно. Матрица «роль → права» —
/// <see cref="ProjectAccess.RoleHasRight"/>. Новые значения — только в конец (порядок значим).
/// </summary>
public enum ProjectRight
{
    Read,
    Write,
    RunTurn,
    /// <summary>
    /// Настройки проекта и необратимые действия над ним: удаление, имя/путь/значок, устройство,
    /// руки, архивация, колонки доски, git-удалённый репозиторий, а также управление ходом
    /// владельца: режим прав, цикл «до готово», штаб, смена провайдера, терминал (shell на хосте),
    /// старт онбординга. Только владелец — у редактора и читателя этого права нет и не будет
    /// (ревью S1-2, S2a-10).
    /// </summary>
    Configure,
    /// <summary>
    /// Запись контента проекта участником (S2c-3): заменяет собой закрытый для участника
    /// <see cref="Write"/> там, где двери переведут поимённо (S2c-7/S2c-8). Владелец и редактор —
    /// да, читатель — нет. Его используют переведённые двери: создание задачи в проект и правка
    /// задачи (ProjectTasksController.Create, TasksController.Update), удаление задачи
    /// (TaskDeleteDenied, авторство поверх права), резолв чужой доски из тела при создании личной
    /// задачи (TasksController.Create) и агентские строки таблицы прав (ActorToolRights:
    /// files_*, notes_*, tasks_* — TasksOpenQ1). Всё, что не переведено поимённо, закрыто
    /// замком <see cref="Write"/>: фичевые поля и жизненный цикл фичи остаются на Write
    /// (FeatureOwnerGate) — редактору они не открываются этим правом.
    /// </summary>
    WriteContent,
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
        return member.Role is ProjectRole.Editor or ProjectRole.Reader ? member.Role : null;
    }

    /// <summary>Можно ли человеку право <paramref name="right"/> в проекте.</summary>
    public static bool Can(Project? project, string? userId, ProjectRight right) =>
        RoleHasRight(Check(project, userId), right);

    /// <summary>
    /// Матрица «роль → права» (разрез §2.1, минимальная): владелец имеет все права,
    /// редактор — Read, Write и RunTurn (отправка и ведение своего хода, S2a-10; Configure не
    /// получит никогда), читатель — только Read («нет права на запись»). Приглашение участников
    /// — отдельное решение владельца в ProjectMembersController. null-роль не имеет ни одного права.
    /// </summary>
    private static bool RoleHasRight(ProjectRole? role, ProjectRight right) =>
        role switch
        {
            ProjectRole.Owner => true,
            ProjectRole.Editor => right is ProjectRight.Read or ProjectRight.Write or ProjectRight.RunTurn or ProjectRight.WriteContent,
            ProjectRole.Reader => right == ProjectRight.Read,
            _ => false,
        };
}
