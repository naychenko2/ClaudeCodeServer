namespace ClaudeHomeServer.Services.Composition;

// Реализация IProjectSummaryLookup: тонкая обёртка над ProjectManager,
// достаёт только имя и системный промпт. Полный `Project` со всем графом
// зависимостей модели (PermissionRule, BoardColumn, ProjectIcon, RootPath, …)
// через шов не утекает.
public sealed class ProjectSummaryLookup(ProjectManager projects) : IProjectSummaryLookup
{
    public ProjectSummary? GetById(string ownerId, string projectId)
    {
        var project = projects.GetById(projectId);
        // Чужой проект — тот же null, что и несуществующий: сверка живёт в шве, чтобы
        // вызывающая вертикаль не могла её пропустить.
        return project is null || project.OwnerId != ownerId
            ? null
            : new ProjectSummary(project.Name, project.SystemPrompt);
    }
}
