using ClaudeHomeServer.Hubs;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;
using Microsoft.AspNetCore.SignalR;

namespace ClaudeHomeServer.Services.Git;

// Режим документов: авто-commit (и опционально push) после каждого завершённого хода Claude
// в проекте с GitAutoCommit. Подписка на SessionManager.OnSessionMessage — тот же хук, что
// у PersonaMemoryAutolearnService; работа уводится в фон, пайплайн хода не тормозим.
// ВАЖНО: git add -A захватит и незакоммиченные правки параллельной сессии в том же дереве —
// осознанное решение владельца («для документов, не для кода с параллельной ручной работой»),
// подсказка у тумблера предупреждает.
public sealed class GitAutoCommitService(
    SessionManager sessions,
    ProjectManager projects,
    UserStore users,
    GitService git,
    IHubContext<SessionHub> hub,
    ILogger<GitAutoCommitService> logger) : IHostedService
{
    public Task StartAsync(CancellationToken ct)
    {
        sessions.OnSessionMessage += OnSessionMessageAsync;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken ct)
    {
        sessions.OnSessionMessage -= OnSessionMessageAsync;
        return Task.CompletedTask;
    }

    private Task OnSessionMessageAsync(Session session, ServerMessage msg)
    {
        if (msg is not ResultMessage || session.ProjectId is null) return Task.CompletedTask;
        var project = projects.GetById(session.ProjectId);
        if (project is null || !project.GitAutoCommit || !GitService.IsGitRepo(project.RootPath))
            return Task.CompletedTask;

        _ = Task.Run(() => AutoCommitSafeAsync(project, session));
        return Task.CompletedTask;
    }

    private async Task AutoCommitSafeAsync(Project project, Session session)
    {
        try
        {
            var ownerId = project.OwnerId;
            var status = await git.StatusAsync(ownerId, project.RootPath);
            if (status.Staged.Count == 0 && status.Unstaged.Count == 0 && status.Untracked.Count == 0)
                return; // ход ничего не поменял

            await git.StageAllAsync(ownerId, project.RootPath);
            var message = $"Авто-сохранение: ход Claude в чате «{session.Name}»\n\n" +
                          $"{DateTime.Now:dd.MM.yyyy HH:mm}";
            await git.CommitAsync(ownerId, project.RootPath, message);

            if (project.GitAutoPush && project.GitRemoteUrl is not null)
            {
                var owner = ownerId is null ? null : users.GetById(ownerId);
                var creds = owner is { ForgejoUsername: { Length: > 0 } u, ForgejoToken: { Length: > 0 } t }
                    ? new GitCredentials(u, t) : null;
                var fresh = await git.StatusAsync(ownerId, project.RootPath);
                if (fresh.Upstream is null && fresh.Branch is not null)
                    await git.PushSetUpstreamAsync(ownerId, project.RootPath, fresh.Branch, creds);
                else
                    await git.PushAsync(ownerId, project.RootPath, creds);
            }

            if (project.OwnerId is not null)
                await hub.Clients.Group("user_" + project.OwnerId)
                    .SendAsync("message", new GitStatusChangedMessage(project.Id));
        }
        catch (Exception ex)
        {
            // Авто-коммит — best-effort: сломанный push не должен ломать ход
            logger.LogWarning(ex, "Авто-коммит в проекте {Project} не удался", project.Name);
        }
    }
}
