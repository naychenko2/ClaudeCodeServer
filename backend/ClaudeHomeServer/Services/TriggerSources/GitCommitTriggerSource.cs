using ClaudeHomeServer.Models;
using Microsoft.Extensions.Logging;

namespace ClaudeHomeServer.Services.TriggerSources;

// Git-коммит-триггер: новый коммит в репозитории проекта. Poll HEAD через FileService.GetCommitsRaw
// (TTL-кеш — см. ChangelogService): если HEAD сдвинулся — собираем коммиты новее прошлого HEAD.
// Первое наблюдение (LastGitHeadSha пусто) НЕ эмитит — иначе React на всю историю.
//
// Args: projectId, paths?:["src/**"] (фильтр по путям — Phase 2, пока игнорируем; фильтр оставляем гейту)
public sealed class GitCommitTriggerSource(ProjectManager projects, FileService files,
    ILogger<GitCommitTriggerSource> log) : ITriggerSource
{
    public AutomationTriggerType Type => AutomationTriggerType.GitCommit;

    public Task<IReadOnlyList<TriggerEvent>> EvaluateAsync(TriggerContext ctx, CancellationToken ct)
    {
        var args = TriggerArgs.Of(ctx.Rule.Trigger);
        var project = args.GetString("projectId") is { } pid ? projects.GetById(pid) : null;
        if (project is null || string.IsNullOrWhiteSpace(project.RootPath))
            return Task.FromResult<IReadOnlyList<TriggerEvent>>(Array.Empty<TriggerEvent>());

        List<GitCommitRaw> commits;
        try { commits = files.GetCommitsRaw(project.RootPath, "", limit: 50); }
        catch (Exception ex)
        {
            log.LogDebug(ex, "git log не удался для проекта {Project}", project.Id);
            return Task.FromResult<IReadOnlyList<TriggerEvent>>(Array.Empty<TriggerEvent>());
        }
        if (commits.Count == 0) return Task.FromResult<IReadOnlyList<TriggerEvent>>(Array.Empty<TriggerEvent>());

        var head = commits[0].Sha;
        var prevHead = ctx.State.LastGitHeadSha;
        ctx.State.LastGitHeadSha = head;   // обновляем всегда (в т.ч. при первом наблюдении)

        // Первое наблюдение или HEAD не сдвинулся — не эмитим
        if (prevHead is null || prevHead == head)
            return Task.FromResult<IReadOnlyList<TriggerEvent>>(Array.Empty<TriggerEvent>());

        // Коммиты новее prevHead (commits — новые сверху)
        var fresh = new List<GitCommitRaw>();
        foreach (var c in commits)
        {
            if (c.Sha == prevHead) break;
            fresh.Add(c);
        }
        if (fresh.Count == 0) return Task.FromResult<IReadOnlyList<TriggerEvent>>(Array.Empty<TriggerEvent>());

        var sample = fresh.Take(8).Select(c => $"• {c.Subject} ({c.Author})").ToList();
        var summary = fresh.Count == 1
            ? $"Новый коммит в «{project.Name}»: {fresh[0].Subject}"
            : $"Новых коммитов в «{project.Name}»: {fresh.Count}";
        var details = new Dictionary<string, string>
        {
            ["commits"] = string.Join("\n", sample) + (fresh.Count > 8 ? $"\n…и ещё {fresh.Count - 8}" : ""),
        };
        return Task.FromResult<IReadOnlyList<TriggerEvent>>(new[]
        {
            new TriggerEvent(ctx.Rule.Id, AutomationTriggerType.GitCommit, summary, details),
        });
    }
}
