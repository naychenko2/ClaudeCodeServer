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
