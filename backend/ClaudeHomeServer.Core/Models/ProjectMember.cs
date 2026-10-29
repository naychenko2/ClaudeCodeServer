using ClaudeHomeServer.Services.Access;

namespace ClaudeHomeServer.Models;

/// <summary>
/// Участник общего проекта (shared-projects, разрез §2.1, S1-1): строка ACL, лежащая на
/// записи проекта. Владелец сюда никогда не пишется: его роль выводится из
/// <c>Project.OwnerId</c> (<see cref="Services.Access.ProjectAccess.Check"/>). Смысл в
/// списке имеют только <see cref="ProjectRole.Editor"/> и <see cref="ProjectRole.Reader"/>:
/// несостоявшееся значение не даёт доступа (точка доступа fail-closed).
/// </summary>
public sealed class ProjectMember
{
    /// <summary>Id участника (<c>User.Id</c>). Приглашение — по точному username (S1-2), в записи — id.</summary>
    public string UserId { get; set; } = "";

    /// <summary>Роль в проекте: Editor или Reader (матрица — разрез §2.1).</summary>
    public ProjectRole Role { get; set; }

    /// <summary>Когда добавлен.</summary>
    public DateTime AddedAt { get; set; } = DateTime.UtcNow;

    /// <summary>Кто добавил: приглашает и отзывает только владелец (разрез §2.1) — его id.</summary>
    public string AddedBy { get; set; } = "";
}
