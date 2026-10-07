using ClaudeHomeServer.Services.Spheres;

namespace ClaudeHomeServer.Services;

/// <summary>Справочник «сфер нет»: подстановка, когда DI его не дал (тесты). Зона персоны сферы — пустая (fail-closed).</summary>
internal sealed class NoSphereDirectory : ISphereDirectory
{
    public static readonly NoSphereDirectory Instance = new();
    public string? SphereOf(string ownerId, string projectId) => null;
    public IReadOnlyList<string> ProjectsOf(string ownerId, string sphereId) => [];
    public bool Enabled(string ownerId) => false;
    public string? SphereName(string ownerId, string sphereId) => null;
    public string? CharterOf(string ownerId, string sphereId) => null;
}
