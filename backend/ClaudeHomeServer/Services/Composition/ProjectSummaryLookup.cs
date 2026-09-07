namespace ClaudeHomeServer.Services.Composition;

// Реализация IProjectSummaryLookup: тонкая обёртка над ProjectManager,
// достаёт только имя и системный промпт. Полный `Project` со всем графом
// зависимостей модели (PermissionRule, BoardColumn, ProjectIcon, RootPath, …)
// через шов не утекает.
public sealed class ProjectSummaryLookup(ProjectManager projects) : IProjectSummaryLookup
{
    public ProjectSummary? GetById(string projectId)
    {
        var project = projects.GetById(projectId);
        return project is null ? null : new ProjectSummary(project.Name, project.SystemPrompt);
    }
}
