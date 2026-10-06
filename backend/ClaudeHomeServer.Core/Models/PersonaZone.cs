using ClaudeHomeServer.Services.Spheres;

namespace ClaudeHomeServer.Models;

/// <summary>
/// Зона персоны — какие проекты ей видны. Единая точка правды для трёх зон (Global/Project/Sphere).
/// Зона сферы считается на КАЖДЫЙ вызов, без кэша. Fail-closed: флаг выключен, сфера удалена
/// или <see cref="Persona.SphereId"/> пуст — зона пуста, а не «как у Global».
/// </summary>
public static class PersonaZone
{
    /// <summary>Видна ли персоне работа в проекте <paramref name="projectId"/>.</summary>
    public static bool VisibleIn(Persona persona, string? projectId, ISphereDirectory dir)
    {
        var ids = ProjectIds(persona, dir);
        if (ids is null) return true;
        return projectId is not null && ids.Contains(projectId, StringComparer.Ordinal);
    }

    /// <summary>Проекты зоны; <c>null</c> — без сужения (только Global).</summary>
    public static IReadOnlyList<string>? ProjectIds(Persona persona, ISphereDirectory dir) =>
        persona.Scope switch
        {
            PersonaScope.Global => null,
            PersonaScope.Project => string.IsNullOrEmpty(persona.ProjectId) ? [] : [persona.ProjectId],
            PersonaScope.Sphere => string.IsNullOrEmpty(persona.SphereId) || !dir.Enabled(persona.OwnerId)
                ? []
                : dir.ProjectsOf(persona.OwnerId, persona.SphereId),
            _ => [],
        };

    /// <summary>Проект «своей» команды: ProjectId проектной персоны; null — не проектная или проект не задан.</summary>
    public static string? OwnProjectId(Persona persona) =>
        persona.Scope == PersonaScope.Project && !string.IsNullOrEmpty(persona.ProjectId) ? persona.ProjectId : null;

    /// <summary>Проектная персона именно этого проекта (без справочника сфер — членство в команде проекта).</summary>
    public static bool IsProjectTeam(Persona persona, string? projectId) =>
        persona.Scope == PersonaScope.Project && persona.ProjectId == projectId;

    /// <summary>Персона команды именно этой сферы (членство, без проверки флага).</summary>
    public static bool IsSphereTeam(Persona persona, string? sphereId) =>
        persona.Scope == PersonaScope.Sphere && persona.SphereId == sphereId;

    /// <summary>Проектная ли зона у запрошенного скоупа (для запросов создания/правки).</summary>
    public static bool IsProjectScope(PersonaScope? scope) => scope == PersonaScope.Project;

    /// <summary>Персона сферы (зона — проекты сферы).</summary>
    public static bool IsSpherePersona(Persona persona) => persona.Scope == PersonaScope.Sphere;

    /// <summary>Проектная ли персона (зона — один проект).</summary>
    public static bool IsProjectPersona(Persona persona) => persona.Scope == PersonaScope.Project;

    /// <summary>Писать в память сферы может человек (<c>persona == null</c>) и персона ЭТОЙ сферы.</summary>
    public static bool CanWriteSphereMemory(Persona? persona, string sphereId) =>
        persona is null
        || (persona.Scope == PersonaScope.Sphere && !string.IsNullOrEmpty(persona.SphereId)
            && string.Equals(persona.SphereId, sphereId, StringComparison.Ordinal));

    /// <summary>
    /// Отказ, если проект чата вышел из сферы персоны; null — всё в порядке (в т.ч. для не-сферных
    /// персон и чата вне проекта: выходить не из чего).
    /// </summary>
    public static string? OutOfZoneRefusal(Persona persona, string? sessionProjectId, ISphereDirectory dir)
    {
        if (persona.Scope != PersonaScope.Sphere || sessionProjectId is null) return null;
        if (VisibleIn(persona, sessionProjectId, dir)) return null;
        var name = string.IsNullOrEmpty(persona.SphereId) ? null : dir.SphereName(persona.OwnerId, persona.SphereId);
        return $"Проект больше не в сфере «{name ?? "—"}» — смените собеседника";
    }
}
