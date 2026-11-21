using ClaudeHomeServer.Hubs;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.Access;
using ClaudeHomeServer.Services.Composition;
using ClaudeHomeServer.Services.Git;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;

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
    SessionManager sessions, UserConnectionRegistry userConnections, IHubContext<SessionHub> hub,
    ILogger<ProjectMembershipService> logger)
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

    /// <summary>
    /// Код отказа ручных операций членства над проектом-предметом (Б14b, ADR-041 ред. 6,
    /// решение 62, разрез §12 «Чего куратору нельзя»): его состав кураторов меняется только
    /// из профиля ребёнка (синхронизация Б14a) — Add/ChangeRole/Remove/Leave отвечают этим
    /// 409 для любого вызывающего, включая владельца-ребёнка и куратора.
    /// </summary>
    public const string ManagedByChildProfile = "managed_by_child_profile";

    /// <summary>Добавить участника по точному username (регистр не важен, как при входе).</summary>
    public async Task<MembershipResult> AddAsync(
        string callerId, string projectId, string? username, ProjectRole role, CancellationToken ct = default)
    {
        if (!gate.InstanceEnabled)
            return new(503, SwitchDisabled, "Общие проекты выключены на этом сервере.");

        var denied = RequireOwner(callerId, projectId, out var project);
        if (denied is not null) return denied;

        // Б14b (решение 62): членство проекта-предмета пишет только синхронизация Б14a —
        // ручное приглашение закрыто для всех, включая владельца-ребёнка: его кураторский
        // список — в ChildProfile.GuardianUserIds, не в этих ручках. Порядок — после
        // RequireOwner: куратор (не владелец) отсекается здесь же 403'ом и до границы не
        // доходит; для него та же граница стоит в ChangeRole/Remove/Drop, где проверка
        // идёт до ownership.
        if (ProjectCapabilities.IsChildSubject(project))
            return SubjectProjectRefusal();

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
        try
        {
            if (!git.IsGitRepo(project.RootPath))
                await git.InitAsync(project.OwnerId, project.RootPath, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new(500, "git_init_failed", "Не удалось подготовить git-репозиторий проекта.");
        }

        // QA-D1 (решение (б) разреза 2cc7578d): при каждом приглашении — снятие игнора
        // заметок в общем проекте. Идемпотентно и безопасно ставить здесь: покрывает и
        // свежий init, и репозиторий, заведённый до QA-D1. Свой .gitignore владельца
        // метод не трогает (только точное совпадение нашего блока). Автор коммита —
        // владелец: серверный git-конфиг может не иметь user.name.
        // Сбой здесь НЕ отменяет приглашение (возврат ревью bc33193f, п.2): журнал заметок
        // вторичен членству — 500 из-за него отнимал у владельца право пригласить. Отказ
        // назван в Warning; участник входит, журнал заметок догонит следующий вызов.
        try
        {
            var owner = project.OwnerId is null ? null : users.GetById(project.OwnerId);
            await git.EnsureNotesVersionedAsync(
                project.OwnerId, project.RootPath, ct,
                owner is null ? null : AuthorOf(owner));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Снятие игнора заметок в проекте {ProjectId} не удалось: приглашение продолжается", projectId);
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
            project.FormerMembers = project.FormerMembers?.Where(f => f.UserId != target.Id).ToList();
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
        // Б14b (решение 62): роль куратора предмета — Editor из профиля (решение 60),
        // ручная смена роли в проекте-предмете — 409 для любого вызывающего.
        var denied = RequireSubjectProject(projectId);
        if (denied is not null) return denied;

        denied = RequireOwner(callerId, projectId, out _);
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
        // Б14b (решение 62): из проекта-предмета не «выходят» — его список правит
        // синхронизация профиля (Б14a). Отказ — до owner_cannot_leave: для владельца-
        // ребёнка ответ — граница фичи, а не обычное «владелец не покидает свой проект».
        if (ProjectCapabilities.IsChildSubject(project))
            return SubjectProjectRefusal();
        if (role == ProjectRole.Owner)
            return new(400, "owner_cannot_leave", "Владелец не может покинуть свой проект.");
        return await DropAsync(projectId, callerId);
    }

    /// <summary>
    /// Есть ли у пользователя проекты с живыми участниками: пока есть, удалять его учётную
    /// запись нельзя (жизненный цикл владельца целиком — S5).
    /// </summary>
    public bool OwnerHasMembers(string ownerId) =>
        projects.GetAll().Any(p => p.OwnerId == ownerId && ProjectAccess.IsShared(p));

    private async Task<MembershipResult> DropAsync(string projectId, string memberUserId)
    {
        // Б14b (решение 62): Remove и Leave пишут через этот метод — отказ стоит здесь
        // один раз, до любой записи; вызывается и после RequireOwner (Remove), и после
        // собственной проверки видимости (Leave). Синхронизация Б14a этот метод НЕ зовёт:
        // её путь — SyncSubjectProjectMembersAsync, без проверки (решение 62: пишет только она).
        var denied = RequireSubjectProject(projectId);
        if (denied is not null) return denied;

        Project updated;
        lock (_lock)
        {
            var project = projects.GetById(projectId)!;
            var members = project.Members?.ToList() ?? [];
            if (members.RemoveAll(m => m.UserId == memberUserId) == 0)
                return new(404, "not_member", "Этот человек не участвует в проекте.");
            project.FormerMembers = [
                .. (project.FormerMembers ?? []).Where(f => f.UserId != memberUserId),
                new FormerProjectMember
                {
                    UserId = memberUserId, DisplayName = users.GetById(memberUserId)?.DisplayName,
                    RemovedAt = DateTime.UtcNow,
                },
            ];
            updated = projects.SetMembers(projectId, members);
        }
        // Хаб-следствие отзыва (разрез §2.2): доступ теряется немедленно — без перезагрузки
        // клиента его живые соединения выкидываются из групп проекта и его чатов
        await RevokeHubAccessAsync(projectId, memberUserId, updated);
        // F2-O3 (финальное ревью «Общих проектов»): и запущенное гасится — в приватных чатах
        // отозванного останавливаются ходы и цикл «до готово», очередь очищается: автор
        // приватного чата после отзыва его не видит, а ход и стоящие в очереди сообщения
        // продолжали бы работу ради того, кому она уже не достаётся.
        sessions.StopChatsOfRevokedMember(projectId, memberUserId);
        return new(200, Project: updated);
    }

    /// <summary>
    /// Запись членства для синхронизации кураторов проекта-предмета (Б14a, ADR-041 ред. 6,
    /// решения 61–62): desired — опекуны профиля ребёнка с ролью Editor, остальных в списке
    /// нет. Проверок приглашения здесь нет по построению (флага сторон, git'а, «приглашает
    /// только владелец»): ACL проекта-предмета правит только синхронизация, ручные ручки на
    /// неё отвечают 409 (решение 62). Инвариант сохранён: владелец в Members не пишется — его
    /// роль выводится из OwnerId (ProjectMember.cs:7). Флаг shared-projects и рубильник
    /// сервер за человека НЕ включает (решение 61: это его экспериментальная настройка).
    /// Живые строки сохраняются со своей датой (повторный обход без правки профиля — no-op);
    /// снятый опекун — не FormerMember: он не покидал проект сам и в чатах предмета не писал
    /// (решение 63), следа «больше не в проекте» над его сообщениями здесь быть не может.
    /// Чтение-проверка-запись — под тем же _lock, что у Add/Drop. Хаб-следствия те же, что
    /// у обычных операций («то же широковещание», решение 61): отозванных выкидываем из
    /// групп (RevokeHubAccessAsync), список обновляем project_members_changed ровно один раз
    /// на обход — при пустом desired без прежних участников события нет (ничего не изменилось).
    /// Проекта нет (удалён под параллельной правкой) — ничего не синхронизируем.
    /// </summary>
    internal async Task SyncSubjectProjectMembersAsync(string childId, string projectId,
        IReadOnlyCollection<string> guardianUserIds, CancellationToken ct = default)
    {
        Project updated;
        List<string> revoked;
        lock (_lock)
        {
            var project = projects.GetById(projectId);
            if (project is null) return;
            var desired = guardianUserIds
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Distinct(StringComparer.Ordinal)
                .Where(id => id != project.OwnerId) // владелец в Members не пишется — инвариант модели
                .ToList();
            var before = project.Members ?? [];
            var members = before.ToList();
            members.RemoveAll(m => !desired.Contains(m.UserId));
            foreach (var userId in desired)
            {
                var existing = members.FirstOrDefault(m => m.UserId == userId);
                if (existing is not null)
                {
                    // Роль куратора — Editor (решение 60): живая строка доводится до неё
                    // без смены даты (смены роли человек в проекте-предмете не делал).
                    if (existing.Role != ProjectRole.Editor)
                        members[members.IndexOf(existing)] = new ProjectMember
                        {
                            UserId = existing.UserId, Role = ProjectRole.Editor,
                            AddedAt = existing.AddedAt, AddedBy = existing.AddedBy,
                        };
                    continue;
                }
                // AddedBy — ребёнок-владелец: сервер пишет ACL по его профилю, владельца
                // приглашает не другой человек (состав строки — тот же, что у ручного Add).
                members.Add(new ProjectMember
                {
                    UserId = userId, Role = ProjectRole.Editor, AddedAt = DateTime.UtcNow, AddedBy = childId,
                });
            }
            var finalIds = new HashSet<string>(members.Select(m => m.UserId), StringComparer.Ordinal);
            var beforeIds = new HashSet<string>(before.Select(m => m.UserId), StringComparer.Ordinal);
            var roleChanged = members.Any(m =>
                before.FirstOrDefault(o => o.UserId == m.UserId)?.Role != m.Role);
            if (beforeIds.SetEquals(finalIds) && !roleChanged)
                return; // состав и роли те же — запись и широковещание не нужны (идемпотентный обход)
            revoked = before.Select(m => m.UserId).Where(id => !finalIds.Contains(id)).ToList();
            updated = projects.SetMembers(projectId, members);
        }
        // Отзыв снятых — тот же путь, что у Drop (Revoke сам широковещает список на каждого);
        // без отзывов — одно project_members_changed на обход (тот же порядок, что у Add/ChangeRole).
        foreach (var userId in revoked)
            await RevokeHubAccessAsync(projectId, userId, updated);
        if (revoked.Count == 0)
            await hub.Clients.Group("project_" + projectId)
                .SendAsync("project_members_changed", new { projectId });
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

    // Б14b (решение 62): проект-предмет — членство пишет только синхронизация профиля
    // (Б14a), ручные операции отвечают 409 для любого вызывающего. Отсутствие — не отказ:
    // отсутствия проекта здесь нет (GetById → null → не проект-предмет), его 404 остаётся
    // у RequireOwner/Check — существование не раскрываем.
    private MembershipResult? RequireSubjectProject(string projectId) =>
        ProjectCapabilities.IsChildSubject(projects.GetById(projectId)) ? SubjectProjectRefusal() : null;

    private static MembershipResult SubjectProjectRefusal() =>
        new(409, ManagedByChildProfile,
            "Участники проекта-предмета определяются профилем ребёнка: состав правит синхронизация профиля, вручную его менять нельзя.");

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

    // Автор технического коммита репозитория проекта — владелец (тот же образец, что
    // GitAutoCommitService и HumanEditCommitter: DisplayName ?? Username).
    private static GitCommitAuthor AuthorOf(User user) =>
        new(user.Id, user.DisplayName ?? user.Username, user.Username);
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
    public static (object? Members, string? MyRole, string? OwnerUsername, string? OwnerDisplayName, object? FormerMembers) Summary(
        Project p, string callerId, UserStore users)
    {
        var role = ProjectAccess.Check(p, callerId);
        var former = p.FormerMembers is { Count: > 0 } f
            ? f.Select(m => new { userId = m.UserId, displayName = m.DisplayName }).ToList() : null;
        var shared = ProjectAccess.IsShared(p) || role is not (null or ProjectRole.Owner);
        if (!shared) return (null, null, null, null, former);
        var owner = users.GetById(p.OwnerId!);
        var members = p.Members ?? [];
        return (new { count = members.Count, first = members.Take(3).Select(m => Person(users, m.UserId)) },
            role is { } r ? RoleName(r) : null, owner?.Username, owner?.DisplayName, former);
    }
}
