using ClaudeHomeServer.Services.Composition;

namespace ClaudeHomeServer.Services;

// Реализация шва IProjectRootLookup: тонкая обёртка над ProjectManager,
// достаёт только RootPath и заворачивает в ProjectRootLocation. Полный
// `Project` через шов не утекает — вертикали получают ровно то, что им
// разрешено знать о проекте.
//
// Зарегистрирована в `Program.cs` рядом с `ProjectManager` как singleton.
public sealed class ProjectRootLookup : IProjectRootLookup
{
    private readonly ProjectManager _projects;

    public ProjectRootLookup(ProjectManager projects)
    {
        _projects = projects;
    }

    public IReadOnlyList<ProjectRootLocation> GetByRootPath(string path) =>
        _projects.GetByRootPath(path)
            .Select(p => new ProjectRootLocation(p.RootPath))
            .ToList();
}