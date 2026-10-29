using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.Access;
using ClaudeHomeServer.Services.Git;

namespace ClaudeHomeServer.Services;

/// <summary>
/// Итог операции над членством: статус HTTP, машинный код отказа и текст для человека.
/// Отказы возвращаются ДО записи — <c>projects.json</c> при них не меняется.
/// </summary>
public sealed record MembershipResult(int Status, string? Code = null, string? Error = null, Project? Project = null)
{
    public bool Ok => Status is >= 200 and < 300;
}

/// <summary>
/// Членство в общем проекте (shared-projects, план §5 S1-2): добавить по ТОЧНОМУ username,
/// сменить роль, убрать, покинуть. Перечисления учёток здесь нет по построению — единственный
/// вход в поиск это точное имя. Приглашает и отзывает только владелец, участник может лишь
/// покинуть проект. Права проверяются точкой доступа <see cref="ProjectAccess"/>.
/// </summary>
public sealed class ProjectMembershipService(
    ProjectManager projects, UserStore users, SharedProjectsGate gate, GitService git)
{
    // Чтение-проверка-запись списка участников — одна секция, иначе два приглашения подряд
    // перезапишут друг друга
    private readonly object _lock = new();

    public const string UserNotFound = "user_not_found";
    public const string AlreadyMember = "already_member";
    public const string Self = "self";
    public const string FlagDisabled = "flag_disabled";
    public const string LocalProject = "local_project";
    public const string SwitchDisabled = "shared_projects_disabled";

    /// <summary>Добавить участника по точному username (регистр не важен, как при входе).</summary>
    public async Task<MembershipResult> AddAsync(
        string callerId, string projectId, string? username, ProjectRole role, CancellationToken ct = default)
    {
        if (!gate.InstanceEnabled)
            return new(503, SwitchDisabled, "Общие проекты выключены на этом сервере.");

        var denied = RequireOwner(callerId, projectId, out var project);
        if (denied is not null) return denied;

        if (!gate.FlagOn(callerId))
            return new(409, FlagDisabled,
                "Чтобы приглашать участников, включите «Общие проекты» в экспериментальных функциях.");
        if (!ProjectCapabilities.Shareable(project))
            return new(409, LocalProject, "Проект на устройстве нельзя открыть другим людям.");
        if (role is not (ProjectRole.Editor or ProjectRole.Reader))
            return new(400, "invalid_role", "Роль участника — редактор или читатель.");

        var target = string.IsNullOrWhiteSpace(username) ? null : users.FindByUsername(username.Trim());
        if (target is null)
            return new(404, UserNotFound, "Пользователь с таким именем не найден.");
        if (target.Id == project.OwnerId)
            return new(400, Self, "Вы владелец проекта — приглашать себя не нужно.");
        if (project.Members?.Any(m => m.UserId == target.Id) == true)
            return new(409, AlreadyMember, "Этот человек уже участвует в проекте.");
        if (!gate.FlagOn(target.Id))
            return new(409, FlagDisabled,
                "У этого человека выключены «Общие проекты»: пусть включит их в экспериментальных функциях, и приглашение пройдёт.");

        // Репозиторий нужен до записи: сбой git не должен оставить участника в проекте без истории
        if (!git.IsGitRepo(project.RootPath))
        {
            try { await git.InitAsync(project.OwnerId, project.RootPath, ct); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return new(500, "git_init_failed", "Не удалось подготовить git-репозиторий проекта.");
            }
        }

        lock (_lock)
        {
            // Состояние могло уйти, пока шёл git init
            project = projects.GetById(projectId)!;
            if (project.Members?.Any(m => m.UserId == target.Id) == true)
                return new(409, AlreadyMember, "Этот человек уже участвует в проекте.");
            var members = project.Members?.ToList() ?? [];
            members.Add(new ProjectMember
            {
                UserId = target.Id, Role = role, AddedAt = DateTime.UtcNow, AddedBy = callerId,
            });
            return new(200, Project: projects.SetMembers(projectId, members));
        }
    }

    /// <summary>Сменить роль участника (только владелец).</summary>
    public MembershipResult ChangeRole(string callerId, string projectId, string memberUserId, ProjectRole role)
    {
        var denied = RequireOwner(callerId, projectId, out _);
        if (denied is not null) return denied;
        if (role is not (ProjectRole.Editor or ProjectRole.Reader))
            return new(400, "invalid_role", "Роль участника — редактор или читатель.");

        lock (_lock)
        {
            var project = projects.GetById(projectId)!;
            var members = project.Members?.ToList() ?? [];
            var member = members.FirstOrDefault(m => m.UserId == memberUserId);
            if (member is null) return new(404, "not_member", "Этот человек не участвует в проекте.");
            members[members.IndexOf(member)] = new ProjectMember
            {
                UserId = member.UserId, Role = role, AddedAt = member.AddedAt, AddedBy = member.AddedBy,
            };
            return new(200, Project: projects.SetMembers(projectId, members));
        }
    }

    /// <summary>Убрать участника (только владелец).</summary>
    public MembershipResult Remove(string callerId, string projectId, string memberUserId)
    {
        var denied = RequireOwner(callerId, projectId, out _);
        if (denied is not null) return denied;
        return Drop(projectId, memberUserId);
    }

    /// <summary>Покинуть проект: единственная операция, доступная не владельцу.</summary>
    public MembershipResult Leave(string callerId, string projectId)
    {
        var project = projects.GetById(projectId);
        var role = ProjectAccess.Check(project, callerId);
        if (role is null) return new(404, "not_found", "Не найдено.");
        if (role == ProjectRole.Owner)
            return new(400, "owner_cannot_leave", "Владелец не может покинуть свой проект.");
        return Drop(projectId, callerId);
    }

    /// <summary>
    /// Есть ли у пользователя проекты с живыми участниками: пока есть, удалять его учётную
    /// запись нельзя (жизненный цикл владельца целиком — S5).
    /// </summary>
    public bool OwnerHasMembers(string ownerId) =>
        projects.GetAll().Any(p => p.OwnerId == ownerId && p.Members is { Count: > 0 });

    private MembershipResult Drop(string projectId, string memberUserId)
    {
        lock (_lock)
        {
            var project = projects.GetById(projectId)!;
            var members = project.Members?.ToList() ?? [];
            if (members.RemoveAll(m => m.UserId == memberUserId) == 0)
                return new(404, "not_member", "Этот человек не участвует в проекте.");
            return new(200, Project: projects.SetMembers(projectId, members));
        }
    }

    // Не видит проекта — 404 (существование не раскрываем), видит, но не владелец — 403
    private MembershipResult? RequireOwner(string callerId, string projectId, out Project project)
    {
        project = projects.GetById(projectId)!;
        var role = ProjectAccess.Check(project, callerId);
        if (role is null) return new(404, "not_found", "Не найдено.");
        if (role != ProjectRole.Owner)
            return new(403, "not_owner", "Участниками проекта управляет только его владелец.");
        return null;
    }
}
