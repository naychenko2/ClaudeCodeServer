using ClaudeHomeServer.Services.Access;
using ClaudeHomeServer.Services.Composition;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services.Git;

namespace ClaudeHomeServer.Services;

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
    ISessionBroadcaster broadcaster,
    ITurnActorRegistry turnActors,
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

    // Актор идущего хода по чату. К ResultMessage ядро уже сняло статус хода и обнулило
    // актора (SessionStore.cs:799-800; свидетель — SessionManagerHierarchyTests.TurnActor.cs:33-34),
    // поэтому запоминаем его на сообщениях ВНУТРИ хода — тот же образец, что
    // ChatTurnLoggerService.cs:41-53. Ключ — session.Id; запись снимается на ResultMessage.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _rememberedActors = new();

    private Task OnSessionMessageAsync(Session session, ServerMessage msg)
    {
        if (session.ProjectId is null) return Task.CompletedTask;
        var project = projects.GetById(session.ProjectId);
        if (msg is not ResultMessage)
        {
            // Внутри хода актор ещё жив: обновляем запомненного на каждом сообщении.
            // Ранний выход для проекта, где автора не бывает по построению: личный
            // (участников нет — тот же признак, что в AuthorOfTurn) и без владельца.
            if (project is null || project.OwnerId is null || !ProjectAccess.IsShared(project))
                return Task.CompletedTask;
            if (turnActors.Actor(session.Id, project.OwnerId) is { } live)
                _rememberedActors[session.Id] = live;
            return Task.CompletedTask;
        }

        // Ход закончен: запомненного снимаем словарь в любом случае — дальше он нужен
        // только как автор коммита, а коммита может и не быть (гейты ниже).
        _rememberedActors.TryRemove(session.Id, out var rememberedActor);

        // Локальный проект коммитит у себя на устройстве — серверный git туда не ходит (ADR-016 §4)
        if (project is not null && !ProjectCapabilities.FilesOnServer(project)) return Task.CompletedTask;
        // Чат в отдельном worktree меняет файлы ТАМ — коммитим его дерево, не корень проекта
        var root = session.WorktreePath ?? project?.RootPath;
        if (project is null || root is null || !project.GitAutoCommit || !git.IsGitRepo(root))
            return Task.CompletedTask;

        // Автор автокоммита (S3-1b) — актор завершённого хода: берётся запомненным внутри
        // хода, СИНХРОННО, до Task.Run: к моменту фоновой задачи реестр хода пуст (ядро
        // снимает актора при выходе статуса из Working — SessionStore.cs:799-800).
        var author = AuthorOfTurn(project, session, rememberedActor);
        _ = Task.Run(() => AutoCommitSafeAsync(project, session, root, author));
        return Task.CompletedTask;
    }

    // Автор коммита общего проекта: актор хода — тот, кого запомнили внутри хода (тот же
    // шов реестра, что у тулсетов). Живой запрос к реестру здесь даёт актора только пока
    // у прогона живы фоновые агенты («липкий» прогон) — он и есть fallback. Актор неизвестен
    // (фон, исполнитель без инициатора) или его нет в сторе — автора нет (fail-closed);
    // личный проект — автора нет: git-конфиг репозитория и есть владелец, поведение прежнее.
    private GitCommitAuthor? AuthorOfTurn(Project project, Session session, string? rememberedActor)
    {
        if (!ProjectAccess.IsShared(project)) return null;
        var ownerId = project.OwnerId;
        if (ownerId is null) return null;
        var actorId = string.IsNullOrEmpty(rememberedActor) ? turnActors.Actor(session.Id, ownerId) : rememberedActor;
        if (string.IsNullOrEmpty(actorId)) return null;
        var user = users.GetById(actorId);
        if (user is null) return null;
        return new GitCommitAuthor(actorId, user.DisplayName ?? user.Username, user.Username);
    }

    private async Task AutoCommitSafeAsync(Project project, Session session, string root, GitCommitAuthor? author)
    {
        try
        {
            var ownerId = project.OwnerId;
            var status = await git.StatusAsync(ownerId, root);
            if (status.Staged.Count == 0 && status.Unstaged.Count == 0 && status.Untracked.Count == 0)
                return; // ход ничего не поменял

            await git.StageAllAsync(ownerId, root);
            var subject = $"Авто-сохранение: ход Claude в чате «{session.Name}»";
            var message = $"{subject}\n\n{DateTime.Now:dd.MM.yyyy HH:mm}";
            var sha = await git.CommitAsync(ownerId, root, message, author: author);

            // Плашка «Изменения сохранены» в ленту чата — со ссылкой на просмотр коммита
            await broadcaster.ToSession(session.Id,
                new GitTurnCommitMessage(project.Id, sha, subject) { SessionId = session.Id });

            if (project.GitAutoPush && project.GitRemoteUrl is not null)
            {
                var owner = ownerId is null ? null : users.GetById(ownerId);
                var creds = owner is { ForgejoUsername: { Length: > 0 } u, ForgejoToken: { Length: > 0 } t }
                    ? new GitCredentials(u, t) : null;
                var fresh = await git.StatusAsync(ownerId, root);
                if (fresh.Upstream is null && fresh.Branch is not null)
                    await git.PushSetUpstreamAsync(ownerId, root, fresh.Branch, creds);
                else
                    await git.PushAsync(ownerId, root, creds);
            }

            if (project.OwnerId is not null)
                await broadcaster.ToProject(project.Id, new GitStatusChangedMessage(project.Id));
        }
        catch (Exception ex)
        {
            // Авто-коммит — best-effort: сломанный push не должен ломать ход
            logger.LogWarning(ex, "Авто-коммит в проекте {Project} не удался", project.Name);
        }
    }
}
