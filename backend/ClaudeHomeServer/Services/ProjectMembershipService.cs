using ClaudeHomeServer.Hubs;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.Access;
using ClaudeHomeServer.Services.Composition;
using ClaudeHomeServer.Services.Git;
using Microsoft.AspNetCore.SignalR;

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
/// вход в поиск — это точное имя. Приглашает и отзывает только владелец, участник может лишь
/// покинуть проект. Права проверяются точкой доступа <see cref="ProjectAccess"/>.
/// Любая операция над членством имеет хаб-следствие: Add/Remove/Leave/смена роли шлют в группу
/// проекта событие project_members_changed (К3 плана v2), чтобы список участников обновлялся
/// без перезагрузки. Отзыв и смена роли — S1-4, разрез §2.2: доступ теряется немедленно —
/// живые соединения отозванного выкидываются из групп проекта и из всех его групп логов
/// дев-сервера этого проекта (S1-4b: после отзыва живое соединение не получает ни одного
/// проектного потока); при смене роли группа остаётся.
/// </summary>
public sealed class ProjectMembershipService(
    ProjectManager projects, UserStore users, SharedProjectsGate gate, GitService git,
    SessionManager sessions, UserConnectionRegistry userConnections, IHubContext<SessionHub> hub)
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

        Project updated;
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
            updated = projects.SetMembers(projectId, members);
        }
        // Список участников обновляется у владельца и существующих участников без перезагрузки
        // страницы (К3 плана v2, тот же событие, что при смене роли). Сам добавленный ещё не
        // в группе проекта — событие дойдёт до него, когда он придёт и подпишется
        await hub.Clients.Group("project_" + projectId)
            .SendAsync("project_members_changed", new { projectId });
        return new(200, Project: updated);
    }

    /// <summary>Сменить роль участника (только владелец).</summary>
    public async Task<MembershipResult> ChangeRoleAsync(
        string callerId, string projectId, string memberUserId, ProjectRole role)
    {
        var denied = RequireOwner(callerId, projectId, out _);
        if (denied is not null) return denied;
        if (role is not (ProjectRole.Editor or ProjectRole.Reader))
            return new(400, "invalid_role", "Роль участника — редактор или читатель.");

        Project updated;
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
            updated = projects.SetMembers(projectId, members);
        }
        // К3 (план v2): смена роли — группа хаба остаётся (доступ не отзывается); фронт по
        // событию перечитывает поля проекта, в том числе свою роль (в обе стороны)
        await hub.Clients.Group("project_" + projectId)
            .SendAsync("project_members_changed", new { projectId });
        return new(200, Project: updated);
    }

    /// <summary>Убрать участника (только владелец).</summary>
    public async Task<MembershipResult> RemoveAsync(string callerId, string projectId, string memberUserId)
    {
        var denied = RequireOwner(callerId, projectId, out _);
        if (denied is not null) return denied;
        return await DropAsync(projectId, memberUserId);
    }

    /// <summary>Покинуть проект: единственная операция, доступная не владельцу.</summary>
    public async Task<MembershipResult> LeaveAsync(string callerId, string projectId)
    {
        var project = projects.GetById(projectId);
        var role = ProjectAccess.Check(project, callerId);
        if (role is null) return new(404, "not_found", "Не найдено.");
        if (role == ProjectRole.Owner)
            return new(400, "owner_cannot_leave", "Владелец не может покинуть свой проект.");
        return await DropAsync(projectId, callerId);
    }

    /// <summary>
    /// Есть ли у пользователя проекты с живыми участниками: пока есть, удалять его учётную
    /// запись нельзя (жизненный цикл владельца целиком — S5).
    /// </summary>
    public bool OwnerHasMembers(string ownerId) =>
        projects.GetAll().Any(p => p.OwnerId == ownerId && p.Members is { Count: > 0 });

    private async Task<MembershipResult> DropAsync(string projectId, string memberUserId)
    {
        Project updated;
        lock (_lock)
        {
            var project = projects.GetById(projectId)!;
            var members = project.Members?.ToList() ?? [];
            if (members.RemoveAll(m => m.UserId == memberUserId) == 0)
                return new(404, "not_member", "Этот человек не участвует в проекте.");
            updated = projects.SetMembers(projectId, members);
        }
        // Хаб-следствие отзыва (разрез §2.2): доступ теряется немедленно — без перезагрузки
        // клиента его живые соединения выкидываются из групп проекта и его чатов
        await RevokeHubAccessAsync(projectId, memberUserId, updated);
        return new(200, Project: updated);
    }

    /// <summary>
    /// Выкидывание отозванного из групп хаба и события К3 (план v2).
    /// Ключ — userId отозванного, не арендатор: SignalR адресует группу только соединением,
    /// поэтому берём все его живые вкладки из реестра (S1-4) и снимаем с каждой и группу
    /// проекта, и группы чатов этого проекта, и все его группы логов дев-сервера этого
    /// проекта (S1-4b, хвост №1 ревью R-S1-4) — по записям подписки из реестра: перечисление
    /// живых сервисов пропустило бы группу уже остановленного сервиса, в которой вкладка
    /// осталась бы до следующего его подъёма. Инвариант: после отзыва живое соединение
    /// отозванного не получает ни одного проектного потока. Затем обновление списка
    /// project_members_changed в группу проекта (отозванный уже выкинут из неё — для него
    /// личное событие ниже) и личное событие К3 project_access_revoked в его собственную
    /// группу user_{id} (её выкидывание не касается).
    /// Порядок внутри одного соединения — FIFO исходящего канала: событие приходит после
    /// выкидывания, и его получение для клиента — доказательство завершённого выкидывания.
    /// </summary>
    private async Task RevokeHubAccessAsync(string projectId, string memberUserId, Project project)
    {
        var sessionIds = sessions.GetByProject(projectId).Select(s => s.Id).ToList();
        foreach (var connectionId in userConnections.ConnectionIdsOf(memberUserId))
        {
            await hub.Groups.RemoveFromGroupAsync(connectionId, "project_" + projectId);
            foreach (var sessionId in sessionIds)
                await hub.Groups.RemoveFromGroupAsync(connectionId, sessionId);
            foreach (var group in userConnections.GroupsOf(connectionId))
                if (SessionHubBroadcaster.IsPreviewLogGroupOf(group, projectId))
                    await hub.Groups.RemoveFromGroupAsync(connectionId, group);
        }
        await hub.Clients.Group("project_" + projectId)
            .SendAsync("project_members_changed", new { projectId });
        // модель допускает проект без владельца — имя отдаём null, а не падающим вызовом
        var ownerDisplayName = project.OwnerId is null ? null : users.GetById(project.OwnerId)?.DisplayName;
        await hub.Clients.Group("user_" + memberUserId).SendAsync("project_access_revoked",
            new { projectId, ownerDisplayName });
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

/// <summary>
/// Представление участников проекта для REST: полный список (GET members и ответы мутаций) и
/// короткая сводка для DTO проекта. Имена берутся из каталога пользователей; чужие учётки
/// не перечисляются — только владелец и участники самого проекта.
/// </summary>
public static class ProjectMembersView
{
    private static string RoleName(ProjectRole r) => r.ToString().ToLowerInvariant();

    private static object Person(UserStore users, string userId)
    {
        var u = users.GetById(userId);
        return new { userId, username = u?.Username ?? "", displayName = u?.DisplayName };
    }

    /// <summary>Полный ответ: участники, владелец и роль вызывающего.</summary>
    public static object Full(Project p, string callerId, UserStore users) => new
    {
        members = (p.Members ?? []).Select(m =>
        {
            var u = users.GetById(m.UserId);
            return new { userId = m.UserId, username = u?.Username ?? "", displayName = u?.DisplayName, role = RoleName(m.Role), addedAt = m.AddedAt };
        }),
        owner = Person(users, p.OwnerId!),
        myRole = ProjectAccess.Check(p, callerId) is { } r ? RoleName(r) : null,
    };

    /// <summary>
    /// Сводка для DTO проекта: null у обычного проекта без участников, где вызывающий — владелец.
    /// </summary>
    public static (object? Members, string? MyRole, string? OwnerUsername, string? OwnerDisplayName) Summary(
        Project p, string callerId, UserStore users)
    {
        var role = ProjectAccess.Check(p, callerId);
        var shared = p.Members is { Count: > 0 } || role is not (null or ProjectRole.Owner);
        if (!shared) return (null, null, null, null);
        var owner = users.GetById(p.OwnerId!);
        var members = p.Members ?? [];
        return (new { count = members.Count, first = members.Take(3).Select(m => Person(users, m.UserId)) },
            role is { } r ? RoleName(r) : null, owner?.Username, owner?.DisplayName);
    }
}
