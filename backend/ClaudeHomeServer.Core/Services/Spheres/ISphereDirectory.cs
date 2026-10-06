namespace ClaudeHomeServer.Services.Spheres;

/// <summary>
/// Справочник членства проектов в сферах — единственный источник правды для зон персон.
/// Ничего не кэшируется: ответ отражает состояние на момент вызова.
/// </summary>
public interface ISphereDirectory
{
    /// <summary>Сфера проекта; null — проект вне сфер, сфера удалена или флаг <c>spheres</c> выключен.</summary>
    string? SphereOf(string ownerId, string projectId);

    /// <summary>Проекты владельца в сфере; пусто, если сферы нет, она чужая или флаг выключен.</summary>
    IReadOnlyList<string> ProjectsOf(string ownerId, string sphereId);

    /// <summary>Включён ли флаг <c>spheres</c> у владельца.</summary>
    bool Enabled(string ownerId);

    /// <summary>Название сферы для текстов отказов; null — сферы нет.</summary>
    string? SphereName(string ownerId, string sphereId);

    /// <summary>Устав сферы (markdown); null — сферы нет, она чужая, флаг выключен или устав пуст.</summary>
    string? CharterOf(string ownerId, string sphereId);
}

/// <summary>Проект сменил сферу (<c>null</c> — вне сфер).</summary>
public sealed record SphereMembershipChanged(string OwnerId, string ProjectId, string? OldSphereId, string? NewSphereId);
