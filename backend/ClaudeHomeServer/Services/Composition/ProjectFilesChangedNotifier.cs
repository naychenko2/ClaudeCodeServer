using ClaudeHomeServer.Hubs;
using Microsoft.AspNetCore.SignalR;

namespace ClaudeHomeServer.Services.Composition;

// Та же форма, что у FileWatcherService.Flush: метод «filesChanged», project-группа из
// SessionHubBroadcaster. Фронт не различает, кто наблюдал дерево — сервер или агент.
public sealed class ProjectFilesChangedNotifier(IHubContext<SessionHub> hub) : IProjectFilesChangedNotifier
{
    public Task FilesChangedAsync(string projectId, IReadOnlyList<string> paths, bool full) =>
        hub.Clients.Group(SessionHubBroadcaster.ProjectGroup(projectId))
            .SendAsync("filesChanged", new { projectId, paths = full ? [] : paths, full });
}
